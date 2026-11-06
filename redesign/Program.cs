using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmiteLoader
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LoaderForm());
        }
    }

    sealed class LoaderForm : Form
    {
        readonly string projectRoot;
        readonly string dist;
        readonly TabControl tabs = new TabControl();
        readonly TabPage loaderPage = new TabPage("Loader");
        readonly TabPage instructionsPage = new TabPage("Instructions");
        readonly Button inject = new Button();
        readonly Button launchUpdateMode = new Button();
        readonly Button update = new Button();
        readonly Button finish = new Button();
        readonly Button refresh = new Button();
        readonly Button openLog = new Button();
        readonly Label status = new Label();
        readonly Label version = new Label();
        readonly TextBox log = new TextBox();
        readonly ProgressBar progress = new ProgressBar();
        readonly Timer timer = new Timer();
        DateTime dumpStartedUtc;
        string pendingBuild;
        string detectedGameExe;
        bool busy;
        int? updateModePid;

        public LoaderForm()
        {
            dist = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            projectRoot = Directory.GetParent(dist).FullName;
            detectedGameExe = GameLocator.FindGameExe();
            Text = "SMITE 2 · CONTROL CENTER";
            ClientSize = new Size(1020, 790);
            MinimumSize = new Size(1036, 829);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(12, 17, 23);
            ForeColor = Color.FromArgb(232, 241, 242);
            Font = new Font("Segoe UI", 10f);

            tabs.Dock = DockStyle.Fill;
            tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabs.ItemSize = new Size(165, 42);
            tabs.SizeMode = TabSizeMode.Fixed;
            tabs.Padding = new Point(18, 8);
            tabs.DrawItem += (sender, args) => {
                bool selected = args.Index == tabs.SelectedIndex;
                Rectangle rect = tabs.GetTabRect(args.Index);
                using (var fill = new SolidBrush(Color.FromArgb(14, 21, 27)))
                    args.Graphics.FillRectangle(fill, rect);
                if (selected)
                {
                    using (var line = new SolidBrush(Color.FromArgb(102, 223, 201)))
                        args.Graphics.FillRectangle(line, rect.Left + 16, rect.Bottom - 4, rect.Width - 32, 3);
                }
                using (var tabFont = new Font("Segoe UI Semibold", 10f))
                    TextRenderer.DrawText(args.Graphics, tabs.TabPages[args.Index].Text,
                        tabFont, rect, selected ? Color.FromArgb(234, 250, 246) : Color.FromArgb(150, 166, 172),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            loaderPage.Text = "ПАНЕЛЬ";
            instructionsPage.Text = "ИНСТРУКЦИЯ";
            loaderPage.BackColor = Color.FromArgb(12, 17, 23);
            instructionsPage.BackColor = Color.FromArgb(12, 17, 23);
            tabs.TabPages.Add(loaderPage);
            tabs.TabPages.Add(instructionsPage);
            Controls.Add(tabs);

            var kicker = MakeLabel("ALWAYSLOSE  /  SMITE 2", 10f, FontStyle.Bold,
                Color.FromArgb(102, 223, 201), new Rectangle(28, 17, 620, 24));
            var title = MakeLabel("Центр управления", 24f, FontStyle.Bold,
                Color.FromArgb(240, 246, 246), new Rectangle(25, 42, 660, 46));
            var caption = MakeLabel("Лоадер, обновление SDK и журнал — в одном окне.", 10f, FontStyle.Regular,
                Color.FromArgb(153, 172, 178), new Rectangle(29, 89, 720, 25));
            version.Bounds = new Rectangle(30, 118, 940, 24);
            version.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            version.ForeColor = Color.FromArgb(190, 207, 210);
            version.Font = new Font("Segoe UI", 9.5f);
            version.AutoEllipsis = true;

            var gameCard = MakeCard(new Rectangle(28, 157, 950, 142));
            gameCard.Controls.Add(MakeLabel("01  /  ПОДКЛЮЧЕНИЕ", 10f, FontStyle.Bold,
                Color.FromArgb(102, 223, 201), new Rectangle(23, 15, 450, 24)));
            gameCard.Controls.Add(MakeLabel("Запустите игру через Steam и дождитесь лобби.", 10f, FontStyle.Regular,
                Color.FromArgb(182, 196, 201), new Rectangle(24, 46, 710, 23)));
            ConfigureButton(inject, "ЗАГРУЗИТЬ DLL", 24, 85, 190, true);
            ConfigureButton(refresh, "ОБНОВИТЬ СТАТУС", 226, 85, 190, false);
            gameCard.Controls.AddRange(new Control[] { inject, refresh });

            var sdkCard = MakeCard(new Rectangle(28, 312, 950, 152));
            sdkCard.Controls.Add(MakeLabel("02  /  ОБНОВЛЕНИЕ SDK", 10f, FontStyle.Bold,
                Color.FromArgb(102, 223, 201), new Rectangle(23, 15, 460, 24)));
            sdkCard.Controls.Add(MakeLabel("После обновления игры выполните три шага по порядку.", 10f, FontStyle.Regular,
                Color.FromArgb(182, 196, 201), new Rectangle(24, 46, 750, 23)));
            ConfigureButton(launchUpdateMode, "1  РЕЖИМ ОБНОВЛЕНИЯ", 24, 91, 238, false);
            ConfigureButton(update, "2  ЗАГРУЗИТЬ DUMPER", 273, 91, 238, false);
            ConfigureButton(finish, "3  СОБРАТЬ DLL", 522, 91, 210, false);
            finish.Enabled = false;
            sdkCard.Controls.AddRange(new Control[] { launchUpdateMode, update, finish });
            new ToolTip().SetToolTip(launchUpdateMode,
                "Игра запускается напрямую для обновления SDK. Офлайн-режим не гарантируется.");

            var statusCaption = MakeLabel("СОСТОЯНИЕ", 9f, FontStyle.Bold,
                Color.FromArgb(126, 147, 152), new Rectangle(30, 480, 200, 22));
            status.Bounds = new Rectangle(30, 504, 930, 40);
            status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            status.Text = "Проверка игры и DLL...";
            status.ForeColor = Color.FromArgb(102, 223, 201);
            status.Font = new Font("Segoe UI Semibold", 10f);
            status.AutoEllipsis = true;
            progress.Bounds = new Rectangle(30, 549, 950, 5);
            progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            progress.Style = ProgressBarStyle.Marquee;
            progress.Visible = false;

            var logCaption = MakeLabel("ЖУРНАЛ  /  СОБЫТИЯ", 9f, FontStyle.Bold,
                Color.FromArgb(126, 147, 152), new Rectangle(30, 570, 350, 26));
            ConfigureButton(openLog, "ОТКРЫТЬ ЖУРНАЛ", 800, 561, 178, false);
            openLog.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            log.Bounds = new Rectangle(28, 608, 950, 112);
            log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.BackColor = Color.FromArgb(16, 24, 31);
            log.ForeColor = Color.FromArgb(194, 211, 214);
            log.Font = new Font("Consolas", 9f);
            log.BorderStyle = BorderStyle.FixedSingle;

            loaderPage.Controls.AddRange(new Control[] { kicker, title, caption, version, gameCard, sdkCard,
                statusCaption, status, progress, logCaption, openLog, log });
            BuildInstructions();
            inject.Click += async (s, e) => await InjectTrainer();
            launchUpdateMode.Click += (s, e) => LaunchForUpdate();
            update.Click += async (s, e) => await StartUpdate();
            finish.Click += async (s, e) => await CompleteUpdate();
            refresh.Click += (s, e) => RefreshState();
            openLog.Click += (s, e) => Process.Start("notepad.exe", Quote(Path.Combine(dist, "SmiteLoader.log")));
            timer.Interval = 3000;
            timer.Tick += (s, e) => { if (!busy) RefreshState(); };
            timer.Start();
            RefreshState();
            WriteLog("Loader ready. Launch update mode starts the game executable directly for SDK updates; offline operation is not guaranteed.");
            if (detectedGameExe != null) WriteLog("Auto-detected game executable: " + detectedGameExe);
        }

        static Label MakeLabel(string text, float size, FontStyle style, Color color, Rectangle bounds)
        {
            return new Label { Text = text, Font = new Font("Segoe UI", size, style),
                ForeColor = color, BackColor = Color.Transparent, Bounds = bounds, AutoEllipsis = true };
        }

        static Panel MakeCard(Rectangle bounds)
        {
            var card = new Panel { Bounds = bounds, BackColor = Color.FromArgb(19, 28, 35),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            card.Paint += (sender, e) => {
                using (var border = new Pen(Color.FromArgb(44, 62, 68)))
                    e.Graphics.DrawRectangle(border, 0, 0, card.ClientSize.Width - 1, card.ClientSize.Height - 1);
            };
            return card;
        }

        void BuildInstructions()
        {
            var guide = new FlowLayoutPanel {
                Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, Padding = new Padding(30, 26, 24, 24),
                BackColor = Color.FromArgb(12, 17, 23), ForeColor = Color.White
            };
            instructionsPage.Controls.Add(guide);
            AddGuideText(guide, "КАК ПОЛЬЗОВАТЬСЯ ЛОАДЕРОМ", 20f, FontStyle.Bold,
                Color.FromArgb(240, 246, 246));
            AddGuideText(guide, "Режим обновления запускает EXE игры напрямую. Офлайн-режим не гарантируется.",
                10f, FontStyle.Regular, Color.FromArgb(229, 185, 144));
            AddGuideText(guide, "01  /  ПОДКЛЮЧЕНИЕ", 12f, FontStyle.Bold,
                Color.FromArgb(102, 223, 201));
            AddGuideText(guide,
                "1. Запустите игру через Steam и дождитесь лобби.\n" +
                "2. На вкладке «Панель» нажмите «Загрузить DLL».\n" +
                "3. Если DLL отсутствует, соберите её с помощью Build_Trainer_for_Loader.bat.",
                11f, FontStyle.Regular, Color.FromArgb(210, 225, 227));
            AddGuideText(guide, "02  /  ПОСЛЕ ОБНОВЛЕНИЯ ИГРЫ", 12f, FontStyle.Bold,
                Color.FromArgb(102, 223, 201));
            AddGuideText(guide,
                "1. Закройте игру и нажмите «Режим обновления».\n" +
                "2. Нажмите «Загрузить Dumper» и дождитесь завершения генерации SDK.\n" +
                "3. Нажмите «Собрать DLL» и подтвердите завершение дампа.\n" +
                "4. Закройте запущенную напрямую игру. Откройте игру через Steam и дождитесь лобби.\n" +
                "5. Нажмите «Загрузить DLL».",
                11f, FontStyle.Regular, Color.FromArgb(210, 225, 227));
            AddGuideText(guide, "ЕСЛИ ВОЗНИКЛА ОШИБКА", 12f, FontStyle.Bold,
                Color.FromArgb(102, 223, 201));
            AddGuideText(guide,
                "Откройте журнал на вкладке «Панель». Если игра не найдена, выберите Hemingway-Win64-Shipping.exe вручную.",
                11f, FontStyle.Regular, Color.FromArgb(182, 196, 201));
        }

        static void AddGuideText(FlowLayoutPanel guide, string text, float size,
            FontStyle style, Color color)
        {
            var font = new Font("Segoe UI", size, style);
            const int width = 820;
            int height = TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + 12;
            guide.Controls.Add(new Label { Text = text, Font = font, ForeColor = color,
                BackColor = guide.BackColor, Size = new Size(width, height),
                Margin = new Padding(0, 0, 0, 16), TextAlign = ContentAlignment.TopLeft });
        }

        void ConfigureButton(Button button, string text, int x, int y, int width, bool primary)
        {
            button.Text = text;
            button.Bounds = new Rectangle(x, y, width, 42);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(65, 86, 90);
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(139, 235, 215) : Color.FromArgb(38, 55, 63);
            button.BackColor = primary ? Color.FromArgb(102, 223, 201) : Color.FromArgb(29, 41, 48);
            button.ForeColor = primary ? Color.FromArgb(12, 31, 30) : Color.FromArgb(221, 234, 234);
            button.Font = new Font("Segoe UI Semibold", 9f);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
        }

        void WriteLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            var line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message.Trim() +
                Environment.NewLine;
            log.AppendText(line);
            try { File.AppendAllText(Path.Combine(dist, "SmiteLoader.log"), line); }
            catch { /* The in-window log still shows the result. */ }
        }

        void SetStatus(string message, bool error = false)
        {
            status.Text = message;
            status.ForeColor = error ? Color.FromArgb(255, 151, 137) : Color.FromArgb(123, 225, 208);
            if (error) WriteLog(message);
        }

        void SetBusy(bool value)
        {
            busy = value;
            inject.Enabled = !value;
            launchUpdateMode.Enabled = !value;
            update.Enabled = !value;
            refresh.Enabled = !value;
            finish.Enabled = !value && pendingBuild != null;
            progress.Visible = value;
        }

        Process GameProcess()
        {
            return Process.GetProcessesByName("Hemingway-Win64-Shipping")
                .FirstOrDefault();
        }

        void LaunchForUpdate()
        {
            if (busy) return;
            try
            {
                if (GameProcess() != null)
                {
                    SetStatus("SMITE 2 уже запущена. Закройте игру перед запуском режима обновления.", true);
                    return;
                }
                var exe = GameLocator.FindGameExe();
                if (exe == null)
                {
                    using (var picker = new OpenFileDialog {
                        Title = "Locate SMITE 2 for direct SDK update launch",
                        Filter = "SMITE 2 shipping executable|Hemingway-Win64-Shipping.exe",
                        FileName = "Hemingway-Win64-Shipping.exe"
                    })
                    {
                        if (picker.ShowDialog(this) != DialogResult.OK) return;
                        exe = picker.FileName;
                        GameLocator.SaveGameExe(exe);
                    }
                }
                detectedGameExe = exe;
                WriteLog("Starting direct update mode from " + exe);
                var start = new ProcessStartInfo(exe) {
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = false
                };
                var machinePath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
                var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
                start.EnvironmentVariables["PATH"] = machinePath + ";" + userPath;
                var started = Process.Start(start);
                if (started == null) throw new InvalidOperationException("The game process did not start.");
                updateModePid = started.Id;
                WriteLog("Direct update-mode process PID: " + updateModePid);
                SetStatus("Игра запущена для обновления SDK. Работа без сети не гарантируется.");
            }
            catch (Exception ex) { SetStatus("Не удалось запустить режим обновления: " + ex.Message, true); }
        }

        string GameBuild(Process game)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(game.MainModule.FileName);
                foreach (var value in new[] { info.FileVersion, info.ProductVersion })
                {
                    var match = Regex.Match(value ?? "", @"CL-(\d+)", RegexOptions.IgnoreCase);
                    if (match.Success) return match.Groups[1].Value;
                }
                return null;
            }
            catch { return null; }
        }

        string TrainerDll(string build)
        {
            return build == null ? null : Path.Combine(dist, "Smite_Internal_" + build + ".dll");
        }

        string DumperDll()
        {
            var bundled = Path.Combine(dist, "Dumper-7.dll");
            return File.Exists(bundled) ? bundled : null;
        }

        string MsBuild()
        {
            var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Microsoft Visual Studio\Installer\vswhere.exe");
            if (!File.Exists(vswhere)) return null;
            try
            {
                var start = new ProcessStartInfo(vswhere,
                    "-latest -products * -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
                };
                using (var proc = Process.Start(start))
                {
                    var root = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit();
                    var msbuild = Path.Combine(root, @"MSBuild\Current\Bin\MSBuild.exe");
                    return proc.ExitCode == 0 && File.Exists(msbuild) ? msbuild : null;
                }
            }
            catch { return null; }
        }

        void RefreshState()
        {
            try
            {
                var game = GameProcess();
                if (game == null)
                {
                    version.Text = "Игра: закрыта  ·  Режим обновления: " +
                        (GameLocator.IsGameExe(detectedGameExe) ? "готов" : "укажите EXE игры");
                    if (!busy && pendingBuild == null)
                        SetStatus("Режим обновления запускает игру напрямую; работа без сети не гарантируется.");
                    return;
                }
                var build = GameBuild(game);
                var dll = TrainerDll(build);
                version.Text = "Сборка игры: " + (build ?? "неизвестна") +
                    "  ·  Трейнер: " + (dll != null && File.Exists(dll) ? "готов" : "нужно обновление");
                if (!busy && pendingBuild == null)
                    SetStatus(dll != null && File.Exists(dll) ? "Готово к загрузке DLL." :
                        "Соберите DLL или обновите SDK для этой версии игры.");
            }
            catch (Exception ex) { SetStatus("Не удалось проверить игру: " + ex.Message, true); }
        }

        bool RequireProject()
        {
            if (File.Exists(Path.Combine(projectRoot, "Trainer.sln")) &&
                File.Exists(Path.Combine(projectRoot, @"tools\update_sdk_build.ps1"))) return true;
            SetStatus("Нужен исходный проект трейнера. Лоадер должен находиться в папке dist проекта.", true);
            return false;
        }

        bool TryGame(out Process game, out string build)
        {
            try { game = GameProcess(); }
            catch (Exception ex) { game = null; SetStatus("Не удалось проверить игру: " + ex.Message, true); }
            build = game == null ? null : GameBuild(game);
            if (game == null) { SetStatus("Сначала запустите SMITE 2.", true); return false; }
            if (build == null) { SetStatus("Не удалось определить сборку игры. Запустите лоадер от имени администратора.", true); return false; }
            return true;
        }

        bool HasTrainer(Process game)
        {
            // Fail closed when modules cannot be inspected; a second injection is unsafe.
            return game.Modules.Cast<ProcessModule>().Any(m =>
                m.ModuleName.StartsWith("Smite_Internal_", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.ModuleName, "Trainer.dll", StringComparison.OrdinalIgnoreCase));
        }

        bool HasDumper(Process game)
        {
            return game.Modules.Cast<ProcessModule>().Any(m =>
                m.ModuleName.StartsWith("Dumper-7", StringComparison.OrdinalIgnoreCase));
        }

        async Task InjectTrainer()
        {
            if (busy) return;
            Process game; string build;
            if (!TryGame(out game, out build)) return;
            var dll = TrainerDll(build);
            if (!File.Exists(dll)) { SetStatus("Нет DLL для сборки " + build + ". Соберите DLL или выполните обновление SDK.", true); return; }
            try
            {
                if (HasTrainer(game)) { SetStatus("Трейнер уже загружен. Перезапустите игру перед загрузкой другой DLL.", true); return; }
            }
            catch (Exception ex) { SetStatus("Не удалось проверить загруженные модули: " + ex.Message, true); return; }
            SetBusy(true);
            SetStatus("Загрузка трейнера...");
            var result = await RunScriptAsync("inject_trainer.ps1", "-ProcessId " + game.Id +
                " -DllPath " + Quote(dll));
            WriteLog(result.Output);
            SetStatus(result.Code == 0 ? "Трейнер загружен в сборку " + build + "." :
                "Ошибка загрузки. " + ShortError(result.Output), result.Code != 0);
            SetBusy(false);
        }

        async Task StartUpdate()
        {
            if (busy || !RequireProject()) return;
            Process game; string build;
            if (!TryGame(out game, out build)) return;
            if (updateModePid == null || game.Id != updateModePid.Value)
            {
                SetStatus("Закройте игру и запустите режим обновления из этого окна лоадера.", true);
                return;
            }
            var dumper = DumperDll();
            if (dumper == null) { SetStatus("Dumper-7.dll не найден. Переустановите лоадер.", true); return; }
            if (MsBuild() == null)
            {
                SetStatus("Требуются Visual Studio Build Tools с MSVC v143 и Windows SDK.", true);
                return;
            }
            try
            {
                string gameExe;
                try { gameExe = game.MainModule.FileName; }
                catch { gameExe = GameLocator.FindGameExe(); }
                var ini = DumperConfiguration.DisableAutomaticSdkTest(gameExe);
                WriteLog("Disabled Dumper-7's automatic CMake SDK test in " + ini);
            }
            catch (Exception ex)
            {
                SetStatus("Не удалось настроить Dumper-7: " + ex.Message, true);
                return;
            }
            try { dumpStartedUtc = game.StartTime.ToUniversalTime(); }
            catch { dumpStartedUtc = DateTime.UtcNow.AddHours(-1); }
            bool dumperLoaded;
            try { dumperLoaded = HasDumper(game); }
            catch (Exception ex) { SetStatus("Не удалось проверить загруженные модули: " + ex.Message, true); return; }
            if (dumperLoaded)
            {
                pendingBuild = build;
                finish.Enabled = true;
                SetStatus("Dumper-7 уже загружен. После завершения нажмите «3 Собрать DLL».");
                return;
            }
            SetBusy(true);
            SetStatus("Загрузка Dumper-7 в процесс игры...");
            try
            {
                var module = await Task.Run(() => NativeDllInjector.Inject(game.Id, dumper));
                WriteLog("Injected Dumper-7.dll at 0x" + module.ToInt64().ToString("X") +
                    " into game PID " + game.Id);
                await Task.Delay(2000);
                if (game.HasExited)
                {
                    SetStatus("Игра закрылась после загрузки Dumper-7. Проверьте журнал.", true);
                    return;
                }
            }
            catch (Exception ex)
            {
                SetStatus("Ошибка загрузки Dumper-7: " + ex.Message, true);
                return;
            }
            finally { SetBusy(false); }
            pendingBuild = build;
            finish.Enabled = true;
            SetStatus("Dumper-7 загружен. Когда SDK будет создан, нажмите «3 Собрать DLL».");
        }

        string CompletedSdkFolder(string build)
        {
            const string root = @"C:\Dumper-7";
            if (!Directory.Exists(root)) return null;
            foreach (var folder in Directory.GetDirectories(root, "*Hemingway*")
                .OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                if (!Regex.IsMatch(Path.GetFileName(folder), @"-" + Regex.Escape(build) + @"-Hemingway$",
                    RegexOptions.IgnoreCase)) continue;
                var sdk = Path.Combine(folder, "CppSDK");
                var header = Path.Combine(sdk, "SDK.hpp");
                var functions = Path.Combine(sdk, @"SDK\Hemingway_functions.cpp");
                var finishMarker = Path.Combine(folder, @"Dumpspace\StructsInfo.json");
                if (!File.Exists(header) || !File.Exists(functions) || !File.Exists(finishMarker) ||
                    !Directory.Exists(Path.Combine(sdk, "SDK"))) continue;
                if (File.GetLastWriteTimeUtc(header) < dumpStartedUtc.AddSeconds(-5)) continue;
                var text = File.ReadAllText(header);
                if (!Regex.IsMatch(text, @"(?m)^//\s*\d+\.\d+\.\d+-" + Regex.Escape(build) + @"\s*$"))
                    continue;
                if (Directory.GetFiles(Path.Combine(sdk, "SDK"), "*", SearchOption.TopDirectoryOnly).Length < 100)
                    continue;
                return sdk;
            }
            return null;
        }

        async Task CompleteUpdate()
        {
            if (busy || pendingBuild == null) return;
            if (MessageBox.Show(this, "Dumper-7 завершил создание SDK?", "Завершение обновления",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var sdk = CompletedSdkFolder(pendingBuild);
            if (sdk == null)
            {
                SetStatus("Полный SDK от Dumper-7 для сборки " + pendingBuild +
                    " ещё не готов. Дождитесь завершения дампа.", true);
                return;
            }
            SetBusy(true);
            SetStatus("Копирование SDK и сборка новой DLL...");
            var result = await RunScriptAsync("complete_sdk_update.ps1", "-SdkFolder " + Quote(sdk) +
                " -ExpectedBuild " + Quote(pendingBuild) + " -ProjectRoot " + Quote(projectRoot));
            WriteLog(result.Output);
            if (result.Code == 0)
            {
                SetStatus("Обновление завершено. Закройте игру, запустите её через Steam и загрузите DLL в лобби.");
                pendingBuild = null;
            }
            else SetStatus("Ошибка сборки. " + ShortError(result.Output), true);
            SetBusy(false);
        }

        static string ShortError(string output)
        {
            var line = (output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Reverse().FirstOrDefault(x => x.Contains("error") || x.Contains("Exception") || x.Contains("failed"));
            return (line ?? "See the log below.").Trim();
        }

        static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }

        async Task<RunResult> RunScriptAsync(string script, string args)
        {
            var path = Path.Combine(projectRoot, "tools", script);
            if (!File.Exists(path)) return new RunResult { Code = 1, Output = "Missing script: " + path };
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo {
                        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                            @"System32\WindowsPowerShell\v1.0\powershell.exe"),
                        Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(path) + " " + args,
                        WorkingDirectory = projectRoot,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    var windowsModules = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        @"System32\WindowsPowerShell\v1.0\Modules");
                    var programModules = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        @"WindowsPowerShell\Modules");
                    psi.EnvironmentVariables["PSModulePath"] =
                        string.Join(";", windowsModules, programModules);
                    using (var process = Process.Start(psi))
                    {
                        var stdout = process.StandardOutput.ReadToEndAsync();
                        var stderr = process.StandardError.ReadToEndAsync();
                        process.WaitForExit();
                        Task.WaitAll(stdout, stderr);
                        return new RunResult { Code = process.ExitCode,
                            Output = (stdout.Result + Environment.NewLine + stderr.Result).Trim() };
                    }
                }
                catch (Exception ex) { return new RunResult { Code = 1, Output = ex.Message }; }
            });
        }

        sealed class RunResult { public int Code; public string Output; }
    }
}
