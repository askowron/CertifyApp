using System.Diagnostics;
using System.IO;

namespace Certify.WPF;

public static class TaskSchedulerHelper
{
    public static string GetCreateCommand()
    {
        var exe = Process.GetCurrentProcess().MainModule?.FileName ?? "CertifyApp.exe";
        // Schtasks: daily renew at 03:00 as SYSTEM
        return $"schtasks /Create /SC DAILY /TN \"CertifyApp Renew\" /TR \"\\\"{exe}\\\" --renew\" /ST 03:00 /RU SYSTEM /F";
    }

    public static void TryCreateTask(IProgress<string>? log = null)
    {
        try
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrWhiteSpace(exe)) return;
            var psi = new ProcessStartInfo("schtasks", $"/Create /SC DAILY /TN \"CertifyApp Renew\" /TR \"\\\"{exe}\\\" --renew\" /ST 03:00 /RU SYSTEM /F")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) { log?.Report("TaskScheduler błąd: nie uruchomiono schtasks"); return; }
            // Oba strumienie rownolegle (stderr niewczytany mogl zablokowac proces),
            // a bledy (np. brak uprawnien Administratora) ida wlasnie na stderr.
            var errTask = p.StandardError.ReadToEndAsync();
            var outp = p.StandardOutput.ReadToEnd().Trim();
            var errp = errTask.Result.Trim();
            p.WaitForExit(5000);
            if (outp.Length > 0) log?.Report(outp);
            if (errp.Length > 0) log?.Report(errp);
            log?.Report($"schtasks exit {p.ExitCode}");
        }
        catch (Exception ex) { log?.Report($"TaskScheduler błąd: {ex.Message}"); }
    }
}
