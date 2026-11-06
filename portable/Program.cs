using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        readonly Panel loaderPage = new Panel();
        readonly Panel instructionsPage = new Panel();
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
        bool setupComplete;
        const string PackageUrl = "https://github.com/66lvl/smite-loader/releases/download/v2.0.0/SmiteLoader-source.zip";
        const string PackageHash = "__PACKAGE_HASH__";
        int? updateModePid;

        public LoaderForm()
        {
            dist = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            projectRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"AppData\LocalLow\SmiteLoader\project");
            detectedGameExe = GameLocator.FindGameExe();
            Text = "SMITE 2 · CONTROL CENTER";
            ClientSize = new Size(1020, 790);
            MinimumSize = new Size(1036, 829);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(10, 12, 22);
            ForeColor = Color.FromArgb(232, 241, 242);
            Font = new Font("Segoe UI", 10f);

            // Use a custom dark navigation bar instead of the native white TabControl header.
            var nav = new Panel { Dock = DockStyle.Top, Height = 42,
                BackColor = Color.FromArgb(16, 17, 31) };
            var mainTab = new Button();
            var guideTab = new Button();
            ConfigureNavButton(mainTab, "ПАНЕЛЬ", 16);
            ConfigureNavButton(guideTab, "ИНСТРУКЦИЯ", 181);
            var indicator = new Panel { Bounds = new Rectangle(32, 39, 133, 3),
                BackColor = Color.FromArgb(178, 154, 255) };
            nav.Controls.AddRange(new Control[] { mainTab, guideTab, indicator });
            indicator.BringToFront();
            var pages = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(10, 12, 22) };
            loaderPage.Dock = DockStyle.Fill;
            instructionsPage.Dock = DockStyle.Fill;
            loaderPage.BackColor = Color.FromArgb(10, 12, 22);
            instructionsPage.BackColor = Color.FromArgb(10, 12, 22);
            pages.Controls.Add(instructionsPage);
            pages.Controls.Add(loaderPage);
            instructionsPage.Visible = false;
            mainTab.Click += (sender, args) => {
                instructionsPage.Visible = false; loaderPage.Visible = true; loaderPage.BringToFront();
                indicator.Left = 32; mainTab.ForeColor = Color.White;
                guideTab.ForeColor = Color.FromArgb(150, 150, 173);
            };
            guideTab.Click += (sender, args) => {
                loaderPage.Visible = false; instructionsPage.Visible = true; instructionsPage.BringToFront();
                indicator.Left = 197; guideTab.ForeColor = Color.White;
                mainTab.ForeColor = Color.FromArgb(150, 150, 173);
            };
            Controls.Add(pages);
            Controls.Add(nav);

            var kicker = MakeLabel("ALWAYSLOSE   /   CONTROL SYSTEM", 9f, FontStyle.Bold,
                Color.FromArgb(178, 154, 255), new Rectangle(30, 22, 620, 23));
            var title = MakeLabel("SMITE 2", 31f, FontStyle.Bold,
                Color.FromArgb(245, 244, 255), new Rectangle(25, 48, 450, 60));
            var caption = MakeLabel("ВАШ ЦЕНТР УПРАВЛЕНИЯ", 9f, FontStyle.Bold,
                Color.FromArgb(146, 150, 173), new Rectangle(32, 108, 500, 23));
            version.Bounds = new Rectangle(420, 72, 552, 28);
            version.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            version.ForeColor = Color.FromArgb(192, 190, 214);
            version.TextAlign = ContentAlignment.MiddleRight;
            version.Font = new Font("Segoe UI", 9.5f);
            version.AutoEllipsis = true;

            var gameCard = MakeCard(new Rectangle(28, 151, 578, 268));
            gameCard.Controls.Add(MakeLabel("01  /  ПОДКЛЮЧЕНИЕ", 9f, FontStyle.Bold,
                Color.FromArgb(178, 154, 255), new Rectangle(27, 24, 450, 24)));
            gameCard.Controls.Add(MakeLabel("Всё готово к игре?", 22f, FontStyle.Bold,
                Color.FromArgb(246, 244, 255), new Rectangle(24, 60, 500, 45)));
            gameCard.Controls.Add(MakeLabel("Запустите SMITE 2 через Steam, дождитесь лобби и загрузите DLL.",
                10f, FontStyle.Regular, Color.FromArgb(167, 169, 190), new Rectangle(28, 115, 500, 48)));
            ConfigureButton(inject, "ЗАГРУЗИТЬ DLL  →", 28, 190, 235, true);
            ConfigureButton(refresh, "ОБНОВИТЬ СТАТУС", 276, 190, 220, false);
            gameCard.Controls.AddRange(new Control[] { inject, refresh });

            var sdkCard = MakeCard(new Rectangle(623, 151, 355, 268));
            sdkCard.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            sdkCard.Controls.Add(MakeLabel("02  /  ОБНОВЛЕНИЕ SDK", 9f, FontStyle.Bold,
                Color.FromArgb(178, 154, 255), new Rectangle(23, 24, 300, 24)));
            sdkCard.Controls.Add(MakeLabel("После обновления игры:", 10f, FontStyle.Regular,
                Color.FromArgb(167, 169, 190), new Rectangle(24, 53, 290, 25)));
            ConfigureButton(launchUpdateMode, "01   РЕЖИМ ОБНОВЛЕНИЯ", 24, 88, 305, false);
            ConfigureButton(update, "02   ЗАГРУЗИТЬ DUMPER", 24, 139, 305, false);
            ConfigureButton(finish, "03   СОБРАТЬ DLL", 24, 190, 305, false);
            finish.Enabled = false;
            sdkCard.Controls.AddRange(new Control[] { launchUpdateMode, update, finish });
            new ToolTip().SetToolTip(launchUpdateMode,
                "Игра запускается напрямую для обновления SDK. Офлайн-режим не гарантируется.");

            var statusCaption = MakeLabel("СОСТОЯНИЕ  /  LIVE", 9f, FontStyle.Bold,
                Color.FromArgb(142, 140, 166), new Rectangle(30, 442, 250, 22));
            status.Bounds = new Rectangle(30, 468, 930, 40);
            status.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            status.Text = "Проверка игры и DLL...";
            status.ForeColor = Color.FromArgb(178, 154, 255);
            status.Font = new Font("Segoe UI Semibold", 10f);
            status.AutoEllipsis = true;
            progress.Bounds = new Rectangle(30, 513, 950, 5);
            progress.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            progress.Style = ProgressBarStyle.Marquee;
            progress.Visible = false;

            var logCaption = MakeLabel("ЖУРНАЛ СОБЫТИЙ", 9f, FontStyle.Bold,
                Color.FromArgb(142, 140, 166), new Rectangle(30, 550, 350, 26));
            ConfigureButton(openLog, "ОТКРЫТЬ ЖУРНАЛ", 800, 538, 178, false);
            openLog.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            log.Bounds = new Rectangle(28, 591, 950, 129);
            log.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.BackColor = Color.FromArgb(17, 19, 32);
            log.ForeColor = Color.FromArgb(192, 190, 214);
            log.Font = new Font("Consolas", 9f);
            log.BorderStyle = BorderStyle.FixedSingle;

            loaderPage.Controls.AddRange(new Control[] { kicker, title, caption, version, gameCard, sdkCard,
                statusCaption, status, progress, logCaption, openLog, log });
            // Compute positions only after the page has its real size, and again on resize.
            Action layoutDashboard = () => {
                float factor = DeviceDpi / 96f;
                Func<int, int> px = value => (int)Math.Round(value * factor);
                int width = loaderPage.ClientSize.Width;
                int height = loaderPage.ClientSize.Height;
                if (width <= 0 || height <= 0) return;
                int margin = px(28), gap = px(17), sdkWidth = px(355);
                int rightX = width - margin - sdkWidth;
                int leftWidth = Math.Max(px(510), rightX - margin - gap);
                gameCard.SetBounds(margin, px(151), leftWidth, px(268));
                sdkCard.SetBounds(rightX, px(151), sdkWidth, px(268));
                int versionX = Math.Max(px(420), width - px(580));
                version.SetBounds(versionX, px(72), Math.Max(0, width - margin - versionX), px(28));
                status.SetBounds(px(30), px(468), Math.Max(0, width - px(60)), px(40));
                progress.SetBounds(px(30), px(513), Math.Max(0, width - px(60)), px(5));
                openLog.SetBounds(width - margin - px(178), px(538), px(178), px(42));
                log.SetBounds(margin, px(591), Math.Max(0, width - 2 * margin),
                    Math.Max(px(80), height - px(613)));
            };
            loaderPage.Resize += (sender, args) => layoutDashboard();
            Shown += async (sender, args) => { layoutDashboard(); await StartSetupIfNeeded(); };
            BuildInstructions();
            inject.Click += async (s, e) => await InjectTrainer();
            launchUpdateMode.Click += (s, e) => LaunchForUpdate();
            update.Click += async (s, e) => await StartUpdate();
            finish.Click += async (s, e) => await CompleteUpdate();
            refresh.Click += async (s, e) => { if (!setupComplete) await StartSetupIfNeeded(); else RefreshState(); };
            openLog.Click += (s, e) => Process.Start("notepad.exe", Quote(Path.Combine(dist, "SmiteLoader.log")));
            timer.Interval = 3000;
            timer.Tick += (s, e) => { if (!busy && setupComplete) RefreshState(); };
            timer.Start();
            SetStatus("Проверка файлов лоадера...");
            WriteLog("Loader ready. Launch update mode starts the game executable directly for SDK updates; offline operation is not guaranteed.");
            if (detectedGameExe != null) WriteLog("Auto-detected game executable: " + detectedGameExe);
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int enabled = 1;
                if (DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        static void ConfigureNavButton(Button button, string text, int left)
        {
            button.Text = text;
            button.Bounds = new Rectangle(left, 0, 165, 39);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(35, 35, 55);
            button.BackColor = Color.FromArgb(16, 17, 31);
            button.ForeColor = text == "ПАНЕЛЬ" ? Color.White : Color.FromArgb(150, 150, 173);
            button.Font = new Font("Segoe UI Semibold", 9f);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
        }

        static Label MakeLabel(string text, float size, FontStyle style, Color color, Rectangle bounds)
        {
            return new Label { Text = text, Font = new Font("Segoe UI", size, style),
                ForeColor = color, BackColor = Color.Transparent, Bounds = bounds, AutoEllipsis = true };
        }

        static Panel MakeCard(Rectangle bounds)
        {
            var card = new Panel { Bounds = bounds, BackColor = Color.FromArgb(21, 23, 40),
                Anchor = AnchorStyles.Top | AnchorStyles.Left };
            card.Paint += (sender, e) => {
                using (var border = new Pen(Color.FromArgb(59, 55, 82)))
                    e.Graphics.DrawRectangle(border, 0, 0, card.ClientSize.Width - 1, card.ClientSize.Height - 1);
            };
            return card;
        }

        void BuildInstructions()
        {
            var guide = new FlowLayoutPanel {
                Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, Padding = new Padding(30, 26, 24, 24),
                BackColor = Color.FromArgb(10, 12, 22), ForeColor = Color.White
            };
            instructionsPage.Controls.Add(guide);
            AddGuideText(guide, "КАК ПОЛЬЗОВАТЬСЯ ЛОАДЕРОМ", 20f, FontStyle.Bold,
                Color.FromArgb(240, 246, 246));
            AddGuideText(guide, "Режим обновления запускает EXE игры напрямую. Офлайн-режим не гарантируется.",
                10f, FontStyle.Regular, Color.FromArgb(229, 185, 144));
            AddGuideText(guide, "01  /  ПОДКЛЮЧЕНИЕ", 12f, FontStyle.Bold,
                Color.FromArgb(178, 154, 255));
            AddGuideText(guide,
                "1. Запустите игру через Steam и дождитесь лобби.\n" +
                "2. На вкладке «Панель» нажмите «Загрузить DLL».\n" +
                "3. Если DLL отсутствует, выполните обновление SDK и сборку в лоадере.",
                11f, FontStyle.Regular, Color.FromArgb(210, 225, 227));
            AddGuideText(guide, "02  /  ПОСЛЕ ОБНОВЛЕНИЯ ИГРЫ", 12f, FontStyle.Bold,
                Color.FromArgb(178, 154, 255));
            AddGuideText(guide,
                "1. Закройте игру и нажмите «Режим обновления».\n" +
                "2. Нажмите «Загрузить Dumper» и дождитесь завершения генерации SDK.\n" +
                "3. Нажмите «Собрать DLL» и подтвердите завершение дампа.\n" +
                "4. Закройте запущенную напрямую игру. Откройте игру через Steam и дождитесь лобби.\n" +
                "5. Нажмите «Загрузить DLL».",
                11f, FontStyle.Regular, Color.FromArgb(210, 225, 227));
            AddGuideText(guide, "ЕСЛИ ВОЗНИКЛА ОШИБКА", 12f, FontStyle.Bold,
                Color.FromArgb(178, 154, 255));
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
            button.FlatAppearance.BorderColor = Color.FromArgb(84, 77, 112);
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(206, 188, 255) : Color.FromArgb(48, 43, 70);
            button.BackColor = primary ? Color.FromArgb(178, 154, 255) : Color.FromArgb(35, 35, 55);
            button.ForeColor = primary ? Color.FromArgb(24, 17, 44) : Color.FromArgb(221, 234, 234);
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
            status.ForeColor = error ? Color.FromArgb(255, 151, 137) : Color.FromArgb(178, 154, 255);
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

        bool SetupFilesReady()
        {
            try
            {
                if (!File.Exists(Path.Combine(projectRoot, ".smite-portable-v2")) ||
                    !File.Exists(Path.Combine(projectRoot, @"tools\update_sdk_build.ps1")) ||
                    !File.Exists(Path.Combine(dist, "Dumper-7.dll"))) return false;
                var header = File.ReadAllText(Path.Combine(projectRoot, @"CppSDK\SDK.hpp"));
                var match = Regex.Match(header, @"(?m)^//\s*\d+\.\d+\.\d+-(\d+)\s*$");
                return match.Success && File.Exists(TrainerDll(match.Groups[1].Value));
            }
            catch { return false; }
        }

        async Task StartSetupIfNeeded()
        {
            if (busy) return;
            if (SetupFilesReady()) { setupComplete = true; SetBusy(false); RefreshState(); return; }
            setupComplete = false;
            inject.Enabled = launchUpdateMode.Enabled = update.Enabled = finish.Enabled = false;
            var reply = MessageBox.Show(this,
                "При первом запуске лоадер скачает исходный проект в LocalLow, " +
                "скопирует Dumper-7.dll и соберёт DLL рядом с этим EXE. " +
                "Если нужны компоненты сборки Microsoft, будет показан отдельный запрос.\n\nПродолжить?",
                "Первый запуск", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (reply != DialogResult.Yes) { SetStatus("Подготовка не выполнена. Нажмите «Обновить статус», чтобы повторить.", true); return; }
            SetBusy(true);
            SetStatus("Загрузка и подготовка проекта; сборка может занять несколько минут...");
            var temp = Path.Combine(Path.GetTempPath(), "SmiteBootstrap-" + Guid.NewGuid().ToString("N") + ".ps1");
            try
            {
                using (var source = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("PortableBootstrap.ps1"))
                using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    if (source == null) throw new InvalidOperationException("Не найден встроенный скрипт подготовки.");
                    source.CopyTo(target);
                }
                var result = await RunExternalScriptAsync(temp, "-ProjectRoot " + Quote(projectRoot) +
                    " -OutputDir " + Quote(dist) + " -PackageUrl " + Quote(PackageUrl) +
                    " -ExpectedHash " + Quote(PackageHash));
                WriteLog(result.Output);
                if (result.Code != 0) throw new InvalidOperationException("Ошибка подготовки. Подробности в журнале.");
                if (!SetupFilesReady()) throw new InvalidOperationException("После подготовки не найдены необходимые файлы.");
                setupComplete = true;
            }
            catch (Exception ex) { SetStatus(ex.Message, true); }
            finally { try { File.Delete(temp); } catch { } SetBusy(false); }
            if (setupComplete) RefreshState();
            else
            {
                inject.Enabled = launchUpdateMode.Enabled = update.Enabled = finish.Enabled = false;
                refresh.Enabled = true;
            }
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
            SetStatus("Исходный проект не найден в LocalLow. Перезапустите подготовку.", true);
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
                " -ExpectedBuild " + Quote(pendingBuild) + " -ProjectRoot " + Quote(projectRoot) + " -OutputDir " + Quote(dist));
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
            return await RunExternalScriptAsync(path, args);
        }

        async Task<RunResult> RunExternalScriptAsync(string path, string args)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo {
                        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                            @"System32\WindowsPowerShell\v1.0\powershell.exe"),
                        Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(path) + " " + args,
                        WorkingDirectory = Directory.Exists(projectRoot) ? projectRoot : dist,
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
