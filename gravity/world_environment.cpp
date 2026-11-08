#include "time_dilation.h"
#include "gravity_experiment.h"
#include "world_environment.h"
#include "sky_inspector.h"
#include "night_sky.h"
#include "world_environment_logic.h"
#include "../core/sdk_helpers.h"
#include "../core/hooks.h"
#include "../ui/theme.h"
#include <atomic>
#include <mutex>
#include <cstdint>

namespace WorldEnvironment
{
    namespace
    {
        std::mutex s_mutex,s_dispatch;
        Config s_config{};
        const char* s_status="Disabled - original camera grading";
        std::atomic_bool s_enabled{false},s_active{false},s_stopping{false},s_suspend{false};
        std::atomic<unsigned> s_calls{0},s_revision{0};
        std::atomic<UINT> s_setup{0};
        std::atomic<HWND> s_window{nullptr};
        std::atomic<uint64_t> s_lastTick{0};
        uint64_t s_lastPost=0; // Present only.
        int s_timerToken;
        const UINT_PTR TimerId=reinterpret_cast<UINT_PTR>(&s_timerToken);
        unsigned s_seenRevision=0; // Dispatcher only below.
        bool s_yielded=false,s_faulted=false;
        struct Lease
        {
            SDK::UCameraComponent* Camera=nullptr;
            SDK::UClass* Class=nullptr;
            SDK::UObject* Outer=nullptr;
            SDK::FName Name{};
            int Index=-1;
            bool OriginalOverride=false,Complete=false;
            unsigned OwnedMask=0;
            Logic::Gain Original{},Written{};
        } s_lease;
        struct Context { SDK::UWorld* World=nullptr;SDK::AHWPlayerController_Smite* PC=nullptr;SDK::AHWCharacter_PlayableCharacter* Pawn=nullptr;SDK::UCameraComponent* Camera=nullptr; };
        void Publish(const char* message){std::lock_guard<std::mutex> lock(s_mutex);s_status=message;}
        bool Live(SDK::UObject* object)
        {
            if(!Helpers::IsValidPtr(object)||!Helpers::IsValidPtr(object->Class))return false;
            const auto bad=static_cast<uint32_t>(SDK::EObjectFlags::ClassDefaultObject)|static_cast<uint32_t>(SDK::EObjectFlags::BeginDestroyed)|static_cast<uint32_t>(SDK::EObjectFlags::FinishDestroyed)|static_cast<uint32_t>(SDK::EObjectFlags::MirroredGarbage);
            return !(static_cast<uint32_t>(object->Flags)&bad);
        }
        Logic::Gain ReadGain(SDK::UCameraComponent* camera)
        {
            const auto& g=camera->PostProcessSettings.ColorGain;
            return {static_cast<double>(g.X),static_cast<double>(g.Y),static_cast<double>(g.Z),static_cast<double>(g.W)};
        }
        void WriteGain(SDK::UCameraComponent* camera,const Logic::Gain& gain)
        {
            auto& g=camera->PostProcessSettings.ColorGain;
            g.X=gain.X;g.Y=gain.Y;g.Z=gain.Z;g.W=gain.W;
        }
        bool ContextReady(Context* out)
        {
            auto* world=Helpers::GetWorld();auto* rawPC=Helpers::GetLocalPC();
            if(!Live(world)||!Live(world->PersistentLevel)||!Live(world->GameState)||!world->GameState->IsA(SDK::AHWGameState::StaticClass()))return false;
            if(!Live(rawPC)||!rawPC->IsA(SDK::AHWPlayerController_Smite::StaticClass()))return false;
            auto* pc=static_cast<SDK::AHWPlayerController_Smite*>(rawPC);
            if(pc->bIsDeathSpectating||!Live(pc->PlayerState)||pc->PlayerState->bIsInactive||pc->PlayerState->bFromPreviousLevel)return false;
            auto* rawPawn=pc->Pawn;
            if(!Live(rawPawn)||!rawPawn->IsA(SDK::AHWCharacter_PlayableCharacter::StaticClass())||rawPawn->Controller!=pc)return false;
            auto* pawn=static_cast<SDK::AHWCharacter_PlayableCharacter*>(rawPawn);
            auto* manager=pc->PlayerCameraManager;
            if(!Live(manager)||manager->PCOwner!=pc||manager->ViewTarget.Target!=pawn)return false;
            if(manager->PendingViewTarget.Target&&manager->PendingViewTarget.Target!=pawn)return false;
            auto* camera=pawn->Camera;
            if(!Live(camera)||!camera->IsA(SDK::UCameraComponent::StaticClass())||camera->Outer!=pawn)return false;
            // Do not force a zero blend weight to 1: that could activate unrelated camera effects.
            if(!std::isfinite(camera->PostProcessBlendWeight)||camera->PostProcessBlendWeight<=0.f||camera->PostProcessBlendWeight>1.f)return false;
            if(camera->Index<0||SDK::UObject::GObjects->GetByIndex(camera->Index)!=camera)return false;
            *out={world,pc,pawn,camera};return true;
        }
        void Forget(){s_lease=Lease{};s_active=false;}
        void RestoreMatchingChannels(SDK::UCameraComponent* camera)
        {
            auto& gain=camera->PostProcessSettings.ColorGain;
            if((s_lease.OwnedMask&1)&&gain.X==s_lease.Written.X)gain.X=s_lease.Original.X;
            if((s_lease.OwnedMask&2)&&gain.Y==s_lease.Written.Y)gain.Y=s_lease.Original.Y;
            if((s_lease.OwnedMask&4)&&gain.Z==s_lease.Written.Z)gain.Z=s_lease.Original.Z;
            if((s_lease.OwnedMask&8)&&gain.W==s_lease.Written.W)gain.W=s_lease.Original.W;
        }
        // Return true only when there is no outstanding change owned by this module.
        bool Restore()
        {
            if(!s_active.load())return true;
            // Resolve registry slot before dereferencing cached camera. The SDK does
            // not expose object serials here; also check name/class/outer. No guessed offsets.
            auto* object=SDK::UObject::GObjects->GetByIndex(s_lease.Index);
            if(object!=s_lease.Camera){Forget();return true;}
            if(!Live(object)||object->Class!=s_lease.Class||object->Outer!=s_lease.Outer||object->Name!=s_lease.Name)
            {Forget();return true;}
            auto* camera=static_cast<SDK::UCameraComponent*>(object);
            if(!s_lease.Complete)
            {
                RestoreMatchingChannels(camera);
                if((s_lease.OwnedMask&16)&&camera->PostProcessSettings.bOverride_ColorGain)
                    camera->PostProcessSettings.bOverride_ColorGain=s_lease.OriginalOverride;
                Forget();return true;
            }
            if(!camera->PostProcessSettings.bOverride_ColorGain||!Logic::Same(ReadGain(camera),s_lease.Written))
            {RestoreMatchingChannels(camera);Forget();s_yielded=true;Publish("External grading preserved; unchanged owned channels restored");return true;}
            s_lease.Complete=false; // A fault during restoration must remain recoverable too.
            WriteGain(camera,s_lease.Original);
            camera->PostProcessSettings.bOverride_ColorGain=s_lease.OriginalOverride;
            Forget();return true;
        }
        void Poll(HWND hwnd)
        {
            Config config;unsigned revision;
            {std::lock_guard<std::mutex> lock(s_mutex);config=s_config;revision=s_revision.load();}
            if(s_faulted){Restore();s_faulted=false;s_yielded=true;Publish("Recovered after camera access fault; toggle off/on to retry");return;}
            if(revision!=s_seenRevision){s_seenRevision=revision;s_yielded=false;}
            if(s_suspend.load()||!config.NightEnabled||config.Strength<=0.f||GetForegroundWindow()!=hwnd)
            {
                Restore();if(!s_yielded)Publish(config.NightEnabled&&!s_suspend.load()?"Paused: zero strength or game unfocused":"Disabled - original camera grading");return;
            }
            if(s_yielded){Publish("Paused: another camera effect owns grading. Toggle off/on to retry");return;}
            Context context{};
            if(!ContextReady(&context)){Restore();if(!s_yielded)Publish("Waiting: owned gameplay camera with active post-process blend required");return;}
            if(s_active.load()&&s_lease.Camera!=context.Camera){Restore();if(s_yielded)return;}
            auto* camera=context.Camera;
            if(s_active.load())
            {
                if(camera->Index!=s_lease.Index||camera->Class!=s_lease.Class||camera->Outer!=s_lease.Outer||camera->Name!=s_lease.Name)
                {Forget();s_yielded=true;Publish("Camera identity changed; effect paused");return;}
                if(!camera->PostProcessSettings.bOverride_ColorGain||!Logic::Same(ReadGain(camera),s_lease.Written))
                {Restore();s_yielded=true;Publish("External camera grading detected; night look paused");return;}
            }
            else
            {
                const auto original=ReadGain(camera);
                if(!Logic::Finite(original)||original.X<0.0||original.Y<0.0||original.Z<0.0||original.W<0.0){Publish("Unavailable: non-finite or negative camera grading");return;}
                s_lease.Camera=camera;s_lease.Index=camera->Index;s_lease.Class=camera->Class;s_lease.Outer=camera->Outer;s_lease.Name=camera->Name;
                s_lease.Original=original;s_lease.OriginalOverride=camera->PostProcessSettings.bOverride_ColorGain;
            }
            const Logic::Gain base=s_lease.OriginalOverride?s_lease.Original:Logic::Gain{};
            const auto desired=Logic::Night(base,config.Strength,config.Coolness);
            // Recheck context without executing reflected getters or network actions.
            if(Helpers::GetWorld()!=context.World||Helpers::GetLocalPC()!=context.PC||context.PC->Pawn!=context.Pawn||context.Pawn->Camera!=camera)
            {Restore();Publish("Camera context changed; waiting");return;}
            // Set ownership before writes so a partial write fault cannot silently discard restore work.
            s_active=true;s_lease.Complete=false;
            auto& gain=camera->PostProcessSettings.ColorGain;
            s_lease.Written.X=desired.X;s_lease.OwnedMask|=1;gain.X=desired.X;
            s_lease.Written.Y=desired.Y;s_lease.OwnedMask|=2;gain.Y=desired.Y;
            s_lease.Written.Z=desired.Z;s_lease.OwnedMask|=4;gain.Z=desired.Z;
            s_lease.Written.W=desired.W;s_lease.OwnedMask|=8;gain.W=desired.W;
            s_lease.OwnedMask|=16;camera->PostProcessSettings.bOverride_ColorGain=true;
            s_lease.Complete=true;
            Publish("Night look applied to own camera (local color grading, not time-of-day)");
        }
        void PollSafe(HWND hwnd)
        {
            __try {Poll(hwnd);}
            __except(EXCEPTION_EXECUTE_HANDLER){s_faulted=true;Publish("Camera access failed; disable and wait for restore before unloading");}
        }
    }
    Config GetConfig(){std::lock_guard<std::mutex> lock(s_mutex);return s_config;}
    void SetConfig(const Config& value)
    {
        Config next=value;next.Strength=Logic::Unit(next.Strength,0.65f);next.Coolness=Logic::Unit(next.Coolness,0.60f);
        std::lock_guard<std::mutex> lock(s_mutex);s_config=next;s_enabled=next.NightEnabled;++s_revision;
    }
    void Service()
    {
        if(s_stopping.load()||(!s_enabled.load()&&!s_active.load()&&!SkyInspector::Pending()&&!NightSky::NeedsService()&&!TimeDilationExperiment::NeedsService()&&!GravityExperiment::NeedsService()))return;
        HWND hwnd=Hooks::g_hWnd;if(!hwnd||!IsWindow(hwnd))return;
        const auto now=GetTickCount64();if(s_window.load()==hwnd&&now-s_lastTick.load()<2000)return;
        if(now-s_lastPost<500)return;s_lastPost=now;
        if(!s_setup.load())s_setup=RegisterWindowMessageW(L"ALWAYSLOSE.WorldEnvironment.NightLook.v1");
        if(s_setup.load())PostMessageW(hwnd,s_setup.load(),0,0);
    }
    bool HandleWindowMessage(HWND hwnd,UINT message,WPARAM wParam,LPARAM)
    {
        const UINT setup=s_setup.load();const bool isSetup=setup&&message==setup;
        if(!isSetup&&!(message==WM_TIMER&&wParam==TimerId))return false;
        static thread_local bool handling=false;if(handling)return true;handling=true;++s_calls;
        {
            std::lock_guard<std::mutex> lock(s_dispatch);
            if(!s_stopping.load()&&GetWindowThreadProcessId(hwnd,nullptr)==GetCurrentThreadId())
            {
                if(isSetup){auto* old=s_window.load();if(old&&old!=hwnd)KillTimer(old,TimerId);if(SetTimer(hwnd,TimerId,100,nullptr))s_window=hwnd;else Publish("Environment timer unavailable");}
                if(!s_suspend.load())SkyInspector::ProcessOnWindowThread();
                NightSky::TickOnWindowThread(hwnd,s_suspend.load());
                TimeDilationExperiment::TickOnWindowThread(hwnd,s_suspend.load());
                GravityExperiment::TickOnWindowThread(hwnd,s_suspend.load());
                PollSafe(hwnd);s_lastTick=GetTickCount64();
                if(!s_enabled.load()&&!s_active.load()&&!SkyInspector::Pending()&&!NightSky::NeedsService()&&!TimeDilationExperiment::NeedsService()&&!GravityExperiment::NeedsService()){KillTimer(hwnd,TimerId);s_window=nullptr;}
            }
        }
        --s_calls;handling=false;return true; // Never consumes mouse/keyboard or changes input capture.
    }
    bool PrepareUnload()
    {
        {
            std::lock_guard<std::mutex> dispatch(s_dispatch);s_suspend=true;SkyInspector::CancelPending();NightSky::Disable();TimeDilationExperiment::Disable();GravityExperiment::RequestDisable();
            auto config=GetConfig();config.NightEnabled=false;SetConfig(config);
        }
        if(!s_active.load()&&!NightSky::HasPendingRestore()&&!TimeDilationExperiment::HasPendingRestore()&&!GravityExperiment::HasPendingRestore())return true;
        const UINT setup=s_setup.load();HWND hwnd=s_window.load();if(!hwnd)hwnd=Hooks::g_hWnd;
        if(hwnd&&setup)PostMessageW(hwnd,setup,0,0);
        for(int i=0;i<100&&(s_active.load()||NightSky::HasPendingRestore()||TimeDilationExperiment::HasPendingRestore()||GravityExperiment::HasPendingRestore());i++)Sleep(10);
        if(s_active.load()||NightSky::HasPendingRestore()||TimeDilationExperiment::HasPendingRestore()||GravityExperiment::HasPendingRestore()){Publish("Unload refused: camera, sky, gravity or world time restore pending; disable and return to gameplay");s_suspend=false;return false;}
        return true;
    }
    void CancelUnload(){if(!s_stopping.load())s_suspend=false;}
    void Shutdown()
    {
        s_stopping=true;
        {std::lock_guard<std::mutex> lock(s_dispatch);SkyInspector::CancelPending();HWND hwnd=s_window.exchange(nullptr);if(hwnd)KillTimer(hwnd,TimerId);}
        while(s_calls.load())Sleep(1);
    }
    void DrawSettings()
    {
        Config config=GetConfig();bool changed=false;
        UITheme::BeginCard("world_night",UITheme::Icon::Visuals,"World / Environment - Night look");
        changed|=UITheme::CheckboxRow("Enable night look",&config.NightEnabled);
        UITheme::HelpLastItem("Night look","Darkens and cools your own camera through local post-processing. Not a time-of-day, sun or sky change. No visibility, fog-of-war or gameplay changes.");
        changed|=UITheme::SliderFloatRow("Night strength",&config.Strength,0.f,1.f,"%.2f");
        UITheme::HelpLastItem("Night strength","Zero restores original camera grading. Higher values only darken the scene; no shadow boosting.");
        changed|=UITheme::SliderFloatRow("Coolness",&config.Coolness,0.f,1.f,"%.2f");
        if(ImGui::Button("Restore original look",{UITheme::Px(210.f),UITheme::Px(36.f)})){config.NightEnabled=false;changed=true;}
        UITheme::HelpLastItem("Restore original look","Disables the effect and restores owned camera fields on the window thread. Does not overwrite the saved profile.");
        if(changed)SetConfig(config);
        const char* status;{std::lock_guard<std::mutex> lock(s_mutex);status=s_status;}
        ImGui::TextWrapped("Status: %s",status);
        UITheme::InfoText("Own gameplay camera only. Pauses for spectating, camera blends and focus loss. If the game replaces grading, the effect yields rather than fighting it.");
        UITheme::InfoText("This is camera color grading, not actual map lighting. Rain is not included: world particles need a verified effect asset and indoor occlusion.");
        UITheme::EndCard();
        GravityExperiment::DrawPanel();
        TimeDilationExperiment::DrawPanel();
        NightSky::DrawPanel();
        SkyInspector::DrawPanel();
    }
}
