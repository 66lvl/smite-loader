using System;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Windows.Forms;

internal static class Setup
{
    [STAThread]
    private static void Main()
    {
        var answer = MessageBox.Show(
            "Установщик скачает закрытый пакет проекта из GitHub. Потребуется вход в личный аккаунт, который приглашён в репозиторий.\n\n" +
            "При необходимости установщик предложит скачать GitHub CLI и Visual Studio Build Tools. Будут показаны запросы на подтверждение и права администратора.\n\nПродолжить?",
            "SMITE Loader — установка", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (answer != DialogResult.Yes) return;
        string path = Path.Combine(Path.GetTempPath(), "SmiteSetup-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("Install.ps1"))
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                if (input == null) throw new Exception("Не найден встроенный скрипт установщика.");
                input.CopyTo(output);
            }
            var psi = new ProcessStartInfo {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + path + "\"",
                UseShellExecute = false,
                CreateNoWindow = false
            };
            using (var p = Process.Start(psi)) { if (p == null) throw new Exception("Не удалось запустить PowerShell."); p.WaitForExit(); }
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Ошибка установки", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { try { File.Delete(path); } catch { } }
    }
}
