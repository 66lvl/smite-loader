#pragma once
// Experimental client-local gravity. Serviced by WorldEnvironment's window dispatcher.
#include "../core/sdk_helpers.h"
#include "../ui/theme.h"
#include <atomic>
#include <cmath>
#include <mutex>

namespace GravityExperiment
{
    namespace
    {
        std::mutex g_mutex;
        bool g_enabled = false;
        float g_multiplier = 1.0f;
        const char* g_status = "Disabled. Client-local experiment.";
        float g_originalSample = 0.f, g_requestedSample = 0.f, g_observedSample = 0.f;
        std::atomic_bool g_needsService{false}, g_pending{false};
        struct Identity {
            SDK::UObject* Pointer = nullptr; SDK::UClass* Class = nullptr;
            SDK::UObject* Outer = nullptr; SDK::FName Name{}; int Index = -1;
        };
        struct Lease {
            Identity World{}, Pawn{}, Movement{};
            float Original = 1.f, Written = 1.f, Multiplier = 1.f;
        } g_lease; // Accessed by dispatcher only; no registry serial exposed by this SDK.
        bool Live(SDK::UObject* obj)
        {
            if(!Helpers::IsValidPtr(obj) || !Helpers::IsValidPtr(obj->Class)) return false;
            const auto bad=static_cast<unsigned>(SDK::EObjectFlags::ClassDefaultObject)|
                static_cast<unsigned>(SDK::EObjectFlags::BeginDestroyed)|
                static_cast<unsigned>(SDK::EObjectFlags::FinishDestroyed)|
                static_cast<unsigned>(SDK::EObjectFlags::MirroredGarbage);
            return !(static_cast<unsigned>(obj->Flags)&bad);
        }
        bool Registered(SDK::UObject* obj)
        { return Live(obj) && obj->Index>=0 && SDK::UObject::GObjects->GetByIndex(obj->Index)==obj; }
        Identity Capture(SDK::UObject* obj)
        { return {obj,obj->Class,obj->Outer,obj->Name,obj->Index}; }
        bool Same(const Identity& id)
        {
            if(id.Index<0 || !SDK::UObject::GObjects) return false;
            auto* obj=SDK::UObject::GObjects->GetByIndex(id.Index);
            return obj==id.Pointer && Live(obj) && obj->Class==id.Class && obj->Outer==id.Outer && obj->Name==id.Name;
        }
        void Publish(const char* text) { std::lock_guard<std::mutex> lock(g_mutex); g_status=text; }
        void Sample(float original,float requested,float observed)
        { std::lock_guard<std::mutex> lock(g_mutex); g_originalSample=original;g_requestedSample=requested;g_observedSample=observed; }
        void Forget() { g_lease=Lease{};g_pending=false; }
        bool Restore()
        {
            if(!g_pending.load()) return true;
            if(!Same(g_lease.Movement)) { Forget();return true; }
            auto* movement=static_cast<SDK::UCharacterMovementComponent*>(g_lease.Movement.Pointer);
            float current=movement->GravityScale;
            Sample(g_lease.Original,g_lease.Written,current);
            if(!std::isfinite(current)||current!=g_lease.Written)
            { Forget();Publish("Game value preserved; gravity field no longer owned.");return true; }
            movement->GravityScale=g_lease.Original;
            if(movement->GravityScale!=g_lease.Original)
            { Publish("Restore not confirmed. Keep loaded and retry.");return false; }
            Sample(g_lease.Original,g_lease.Written,movement->GravityScale);
            Forget();return true;
        }
        void Disable()
        { std::lock_guard<std::mutex> lock(g_mutex);g_enabled=false;g_needsService=false; }
        bool Context(SDK::UWorld** outWorld,SDK::AHWCharacter_PlayableCharacter** outPawn,SDK::UCharacterMovementComponent** outMovement)
        {
            auto* world=Helpers::GetWorld();auto* rawPC=Helpers::GetLocalPC();
            if(!Registered(world)||!Live(world->PersistentLevel)||!Live(world->GameState)||
                !world->GameState->IsA(SDK::AHWGameState::StaticClass()))return false;
            if(!Live(rawPC)||!rawPC->IsA(SDK::AHWPlayerController_Smite::StaticClass()))return false;
            auto* pc=static_cast<SDK::AHWPlayerController_Smite*>(rawPC);
            if(pc->bIsDeathSpectating||!Live(pc->PlayerState)||pc->PlayerState->bIsInactive||pc->PlayerState->bFromPreviousLevel)return false;
            auto* rawPawn=pc->Pawn;
            if(!Registered(rawPawn)||!rawPawn->IsA(SDK::AHWCharacter_PlayableCharacter::StaticClass())||rawPawn->Controller!=pc)return false;
            auto* pawn=static_cast<SDK::AHWCharacter_PlayableCharacter*>(rawPawn);
            auto* movement=pawn->CharacterMovement;
            if(!Registered(movement)||!movement->IsA(SDK::UCharacterMovementComponent::StaticClass())||movement->Outer!=pawn)return false;
            *outWorld=world;*outPawn=pawn;*outMovement=movement;return true;
        }
        void Poll(HWND hwnd,bool suspending)
        {
            bool enabled;float multiplier;
            { std::lock_guard<std::mutex> lock(g_mutex);enabled=g_enabled;multiplier=g_multiplier; }
            if(suspending||!enabled)
            { if(Restore()) Publish("Disabled; owned value restored or game value preserved.");return; }
            if(GetForegroundWindow()!=hwnd)
            { Disable();Restore();Publish("Disabled after focus loss; enable again manually.");return; }
            SDK::UWorld* world=nullptr;SDK::AHWCharacter_PlayableCharacter* pawn=nullptr;SDK::UCharacterMovementComponent* move=nullptr;
            if(!Context(&world,&pawn,&move))
            { Disable();Restore();Publish("Disabled: local playable character not available.");return; }
            if(g_pending.load())
            {
                if(world!=g_lease.World.Pointer||pawn!=g_lease.Pawn.Pointer||move!=g_lease.Movement.Pointer||
                   !Same(g_lease.World)||!Same(g_lease.Pawn)||!Same(g_lease.Movement))
                { Disable();Restore();Publish("Character or world changed; enable again manually.");return; }
                const float current=move->GravityScale;Sample(g_lease.Original,g_lease.Written,current);
                if(!std::isfinite(current)||current!=g_lease.Written)
                { Disable();Forget();Publish("Game replaced local gravity; disabled without reapplying.");return; }
                if(multiplier==g_lease.Multiplier)return;
                if(!Restore())return;
            }
            if(multiplier==1.f){Publish("Multiplier 1.00: no change to gravity.");return;}
            const float original=move->GravityScale;
            if(!std::isfinite(original)||original<=0.f||original>10.f)
            { Disable();Publish("Disabled: unusual or invalid baseline gravity.");return; }
            const float desired=original*multiplier;
            if(Helpers::GetWorld()!=world||Helpers::GetLocalPC()->Pawn!=pawn||pawn->CharacterMovement!=move||move->GravityScale!=original)
            { Disable();Publish("Context changed before write; disabled.");return; }
            g_lease={Capture(world),Capture(pawn),Capture(move),original,desired,multiplier};
            g_pending=true; // Preserve restore obligation even if a write faults.
            move->GravityScale=desired;Sample(original,desired,move->GravityScale);
            if(move->GravityScale!=desired){Disable();Restore();Publish("Local write not retained; disabled.");return;}
            Publish("Local GravityScale changed; server acceptance NOT confirmed.");
        }
    }
    inline bool NeedsService(){return g_needsService.load()||g_pending.load();}
    inline bool HasPendingRestore(){return g_pending.load();}
    inline void RequestDisable(){Disable();}
    inline void TickOnWindowThread(HWND hwnd,bool suspending)
    {
        if(!NeedsService())return;
        __try { Poll(hwnd,suspending); }
        __except(EXCEPTION_EXECUTE_HANDLER)
        { Disable();Publish("SDK access fault. Disable and wait for restore before unload."); }
    }
    inline void DrawPanel()
    {
        bool enabled;float multiplier;const char* status;float original,requested,observed;
        {std::lock_guard<std::mutex> lock(g_mutex);enabled=g_enabled;multiplier=g_multiplier;}
        UITheme::BeginCard("gravity_experiment",UITheme::Icon::Visuals,"Experimental / Gravity");
        bool changed=UITheme::CheckboxRow("Enable local gravity",&enabled);
        changed|=UITheme::SliderFloatRow("Gravity multiplier",&multiplier,0.25f,2.f,"%.2fx");
        if(ImGui::Button("Disable gravity and restore")){enabled=false;changed=true;}
        if(changed){std::lock_guard<std::mutex> lock(g_mutex);g_enabled=enabled;g_multiplier=std::isfinite(multiplier)?(multiplier<.25f?.25f:(multiplier>2.f?2.f:multiplier)):1.f;g_needsService=enabled;}
        {std::lock_guard<std::mutex> lock(g_mutex);status=g_status;original=g_originalSample;requested=g_requestedSample;observed=g_observedSample;}
        ImGui::TextWrapped("Status: %s",status);
        ImGui::Text("Local values: original=%.2f requested=%.2f observed=%.2f",original,requested,observed);
        ImGui::Text("Restore pending: %s",HasPendingRestore()?"YES":"NO");
        UITheme::InfoText("Own character only. Client-side field, no server acknowledgement. Corrections can occur. Test in practice. Off by default; not saved.");
        UITheme::InfoText("Disables on focus loss, character/world change or an external field change. Restoration is best effort; no guaranteed game-thread affinity.");
        UITheme::EndCard();
    }
}
