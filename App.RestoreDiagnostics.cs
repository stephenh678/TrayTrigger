#if DEBUG
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using TrayTrigger.Services;

namespace TrayTrigger;

/// <summary>
/// --test-restore-restart &lt;folder&gt;: Restore's whole path without its two file dialogs. Backs up
/// what is on disk to &lt;folder&gt;\backup.zip, makes the safety backup, unpacks the backup for the
/// next start and restarts through <see cref="RestartApplication"/> - so the start after it puts the
/// files back and shows the Restore Complete dialog, exactly as a restore from Settings does. The
/// data restored is the data that was there, so nothing changes but the safety backup and the .bak
/// files. Needs no other TrayTrigger running: the restarted one has to take the single-instance mutex.
/// </summary>
public partial class App
{
    private bool TryHandleRestoreDevArgs(StartupEventArgs e, int i)
    {
        if (!e.Args[i].Equals("--test-restore-restart", StringComparison.OrdinalIgnoreCase) || i + 1 >= e.Args.Length) return false;
        string folder = e.Args[i + 1];
        _skipSettingsSaveOnExit = true;

        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                string zip = Path.Combine(folder, "backup.zip");
                var service = new BackupService(_storageService);
                var manifest = service.Create(zip);
                string safety = service.CreateSafetyBackup();
                service.StageRestore(zip, safety);
                File.WriteAllText(Path.Combine(folder, "staged.txt"),
                    $"Backed up {manifest.Games} games, {manifest.Tools} tools, {manifest.Scripts} scripts, {manifest.ArtFiles} art files.\nSafety backup: {safety}\n");
                Dispatcher.BeginInvoke(RestartApplication);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(folder, "staged.txt"), "ERROR " + ex);
                Dispatcher.BeginInvoke(ExitApplication);
            }
        });
        return true;
    }
}
#endif
