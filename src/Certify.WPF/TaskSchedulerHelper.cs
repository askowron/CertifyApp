using System.Diagnostics;
using System.IO;

namespace Certify.WPF;

/// <summary>Stan zadania Harmonogramu (null z GetInfo = zadanie nie istnieje).</summary>
public sealed record ScheduledTaskInfo(
    bool Enabled,
    bool Running,
    DateTime? LastRun,
    int LastResult,
    DateTime? NextRun,
    string? Command,
    TimeSpan? StartTime);

public static class TaskSchedulerHelper
{
    public const string TaskName = "CertifyApp Renew";
    public static readonly TimeSpan DefaultTime = new(3, 0, 0);
    // SCHED_S_TASK_HAS_NOT_RUN
    private const int NeverRun = 0x41303;

    public static string ExePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "CertifyApp.exe";

    private static List<string> CreateArgs(TimeSpan time) =>
    [
        "/Create", "/SC", "DAILY", "/TN", TaskName,
        "/TR", $"\"{ExePath}\" --renew",
        "/ST", $"{time.Hours:00}:{time.Minutes:00}",
        "/RU", "SYSTEM", "/F"
    ];

    /// <summary>Tworzy albo nadpisuje (/F) zadanie: codziennie o time jako SYSTEM, sciezka biezacego exe.</summary>
    public static bool Create(TimeSpan time, IProgress<string>? log) => RunSchtasks(CreateArgs(time), log);

    public static bool RunNow(IProgress<string>? log) => RunSchtasks(["/Run", "/TN", TaskName], log);

    public static bool SetEnabled(bool enabled, IProgress<string>? log) =>
        RunSchtasks(["/Change", "/TN", TaskName, enabled ? "/ENABLE" : "/DISABLE"], log);

    public static bool Delete(IProgress<string>? log) => RunSchtasks(["/Delete", "/TN", TaskName, "/F"], log);

    /// <summary>
    /// Stan przez COM Schedule.Service - niezalezny od jezyka systemu (wyjscie schtasks /Query
    /// ma zlokalizowane naglowki i formaty dat). Null = brak zadania albo blad odczytu.
    /// </summary>
    public static ScheduledTaskInfo? GetInfo()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) return null;
            dynamic svc = Activator.CreateInstance(type)!;
            svc.Connect();
            dynamic folder = svc.GetFolder("\\");
            dynamic task;
            try { task = folder.GetTask(TaskName); }
            catch { return null; } // nie istnieje

            DateTime? Date(object v) => v is DateTime d && d.Year > 2000 ? d : null;
            string? command = null;
            TimeSpan? start = null;
            dynamic def = task.Definition;
            foreach (dynamic action in def.Actions)
            {
                // TASK_ACTION_EXEC = 0
                if ((int)action.Type == 0) { command = (string)action.Path; break; }
            }
            foreach (dynamic trigger in def.Triggers)
            {
                if (DateTime.TryParse((string)trigger.StartBoundary, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var sb))
                { start = sb.TimeOfDay; break; }
            }
            // TASK_STATE_RUNNING = 4
            return new ScheduledTaskInfo(
                (bool)task.Enabled, (int)task.State == 4,
                Date(task.LastRunTime), (int)task.LastTaskResult, Date(task.NextRunTime),
                command?.Trim('"'), start);
        }
        catch { return null; }
    }

    public static bool HasNeverRun(ScheduledTaskInfo info) => info.LastRun == null || info.LastResult == NeverRun;

    /// <summary>Czy zadanie wskazuje na biezacy exe (ClickOnce zmienia katalog przy kazdej aktualizacji).</summary>
    public static bool PointsToCurrentExe(ScheduledTaskInfo info) =>
        info.Command != null && string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(info.Command)),
            Path.GetFullPath(ExePath), StringComparison.OrdinalIgnoreCase);

    private static bool RunSchtasks(List<string> args, IProgress<string>? log)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) { log?.Report("[Scheduler] Błąd: nie uruchomiono schtasks"); return false; }
            // Oba strumienie rownolegle (stderr niewczytany mogl zablokowac proces),
            // a bledy (np. brak uprawnien Administratora) ida wlasnie na stderr.
            var errTask = p.StandardError.ReadToEndAsync();
            var outp = p.StandardOutput.ReadToEnd().Trim();
            var errp = errTask.Result.Trim();
            p.WaitForExit(15000);
            if (outp.Length > 0) log?.Report($"[Scheduler] {outp}");
            if (errp.Length > 0) log?.Report($"[Scheduler] {errp}");
            log?.Report($"[Scheduler] schtasks {args[0]} exit {p.ExitCode}");
            return p.ExitCode == 0;
        }
        catch (Exception ex) { log?.Report($"[Scheduler] Błąd: {ex.Message}"); return false; }
    }
}
