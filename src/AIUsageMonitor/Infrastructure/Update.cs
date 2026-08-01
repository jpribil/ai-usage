using System.Diagnostics;

namespace AIUsageMonitor.Infrastructure;

internal static class Update
{
    internal static bool ApplyIfRequested(string[] args)
    {
        if (args.Length != 4 || !args[0].Equals("--apply-update", StringComparison.OrdinalIgnoreCase) || !int.TryParse(args[3], out var processId))
        {
            return false;
        }

        var target = Path.GetFullPath(args[1]);
        var source = Path.GetFullPath(args[2]);
        if (!File.Exists(source))
        {
            MessageBox.Show("Downloaded update was not found.", "AI Usage Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit(30_000);
        }
        catch (ArgumentException)
        {
            // The original process has already exited, which is the desired state.
        }

        var backup = target + ".old";
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                if (File.Exists(backup))
                {
                    File.Delete(backup);
                }
                if (File.Exists(target))
                {
                    File.Move(target, backup);
                }
                File.Copy(source, target, overwrite: true);
                File.Delete(backup);
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                File.Delete(source);
                return true;
            }
            catch (IOException)
            {
                RestoreBackup(target, backup);
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException)
            {
                RestoreBackup(target, backup);
                Thread.Sleep(500);
            }
        }

        MessageBox.Show("Unable to apply the update. The previous version was restored.", "AI Usage Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return true;
    }

    private static void RestoreBackup(string target, string backup)
    {
        try
        {
            if (File.Exists(target)) File.Delete(target);
            if (File.Exists(backup)) File.Move(backup, target);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
