using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Certify.Core.Localization;
using Certify.Core.Models;

namespace Certify.Core.Services;

/// <summary>
/// Wykonuje zadania certyfikatu (jak w CTW: Pre-Request przed requestem,
/// Deployment po standardowym wdrozeniu, w kolejnosci z listy).
/// Aktualizuje LastRunUtc/LastStatus/LastMessage na definicji zadania.
/// </summary>
public class CertificateTaskRunner
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly CertificateExporter _exporter = new();

    public async Task<bool> RunPreRequestTasksAsync(ManagedCertificate cert, IProgress<string>? log, CancellationToken ct)
    {
        var tasks = cert.Tasks.Where(t => t.Enabled && t.Stage == CertificateTaskStage.PreRequest).ToList();
        if (tasks.Count == 0) return true;
        log?.Report($"[Tasks] Pre-Request: {tasks.Count} zadań...");
        foreach (var t in tasks)
        {
            ct.ThrowIfCancellationRequested();
            if (!await RunSingleAsync(t, cert, certSuccess: true, log, ct))
            {
                log?.Report($"[Tasks] Pre-Request przerwany na zadaniu '{t.Name}'.");
                return false;
            }
        }
        return true;
    }

    public async Task RunDeploymentTasksAsync(ManagedCertificate cert, bool certSuccess, IProgress<string>? log, CancellationToken ct)
    {
        var tasks = cert.Tasks.Where(t => t.Enabled && t.Stage == CertificateTaskStage.Deployment).ToList();
        if (tasks.Count == 0) return;
        log?.Report($"[Tasks] Deployment: {tasks.Count} zadan (sukces certyfikatu: {certSuccess})...");
        foreach (var t in tasks)
        {
            ct.ThrowIfCancellationRequested();
            if (!certSuccess && t.Trigger == CertificateTaskTrigger.OnSuccess)
            {
                t.LastRunUtc = DateTime.UtcNow;
                t.LastStatus = CertificateTaskLastStatus.Skipped;
                t.LastMessage = "Pominieto (certyfikat nie wystawiony)";
                log?.Report($"[Tasks] '{t.Name}': pominięto (tylko po sukcesie).");
                continue;
            }
            await RunSingleAsync(t, cert, certSuccess, log, ct);
        }
    }

    private async Task<bool> RunSingleAsync(CertificateTaskDefinition task, ManagedCertificate cert, bool certSuccess, IProgress<string>? log, CancellationToken ct)
    {
        log?.Report($"[Tasks] '{task.Name}' ({CertificateTaskParameters.TaskTypeDisplay(task.TaskType)})...");
        try
        {
            switch (task.TaskType)
            {
                case CertificateTaskType.ExportCertificate:
                    await RunExportAsync(task, cert, ct);
                    break;
                case CertificateTaskType.RestartService:
                    await RunRestartServiceAsync(task, log, ct);
                    break;
                case CertificateTaskType.RunPowerShellScript:
                    await RunPowerShellAsync(task, cert, log, ct);
                    break;
                case CertificateTaskType.RunProgram:
                    await RunProgramAsync(task, cert, log, ct);
                    break;
                case CertificateTaskType.Wait:
                    await RunWaitAsync(task, ct);
                    break;
                case CertificateTaskType.Webhook:
                    await RunWebhookAsync(task, cert, certSuccess, log, ct);
                    break;
                default:
                    throw new NotSupportedException(UIStrings.T("Task_Err_Type", task.TaskType));
            }
            task.LastRunUtc = DateTime.UtcNow;
            task.LastStatus = CertificateTaskLastStatus.Success;
            task.LastMessage = UIStrings.T("Task_Msg_Done");
            log?.Report($"[Tasks] '{task.Name}': Success.");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Anulowanie przez uzytkownika to nie "Failed" zadania - przerwij cala operacje.
            throw;
        }
        catch (TaskSkippedException ex)
        {
            task.LastRunUtc = DateTime.UtcNow;
            task.LastStatus = CertificateTaskLastStatus.Skipped;
            task.LastMessage = ex.Message;
            log?.Report($"[Tasks] '{task.Name}': pominięto ({ex.Message}).");
            return true;
        }
        catch (Exception ex)
        {
            task.LastRunUtc = DateTime.UtcNow;
            task.LastStatus = CertificateTaskLastStatus.Failed;
            task.LastMessage = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
            log?.Report($"[Tasks] '{task.Name}': FAILED: {ex.Message}");
            return false;
        }
    }

    private async Task RunExportAsync(CertificateTaskDefinition task, ManagedCertificate cert, CancellationToken ct)
    {
        var dest = task.GetParam(CertificateTaskParameters.P_DestinationPath);
        if (string.IsNullOrWhiteSpace(dest)) throw new Exception(UIStrings.T("Task_Err_Dest"));
        var formatName = task.GetParam(CertificateTaskParameters.P_Format, nameof(CertificateExportFormat.PemFullChainExcludingKey));
        if (!Enum.TryParse<CertificateExportFormat>(formatName, out var format))
            throw new Exception(UIStrings.T("Task_Err_Format", formatName));
        var res = await _exporter.ExportAsync(cert, format, dest, ct);
        if (!res.Success) throw new Exception(res.Message);
    }

    private async Task RunRestartServiceAsync(CertificateTaskDefinition task, IProgress<string>? log, CancellationToken ct)
    {
        var svc = task.GetParam(CertificateTaskParameters.P_ServiceName);
        if (string.IsNullOrWhiteSpace(svc)) throw new Exception(UIStrings.T("Task_Err_Svc"));
        var action = task.GetParam(CertificateTaskParameters.P_ServiceAction, "Restart");

        if (action is "Restart" or "Stop")
        {
            await RunProcessAsync("sc.exe", $"stop \"{svc}\"", log, ct, allowNonZero: true);
            await WaitForServiceStateAsync(svc, "STOPPED", log, ct);
        }
        if (action is "Restart" or "Start")
        {
            await RunProcessAsync("sc.exe", $"start \"{svc}\"", log, ct);
            await WaitForServiceStateAsync(svc, "RUNNING", log, ct);
        }
    }

    private async Task WaitForServiceStateAsync(string svc, string wantState, IProgress<string>? log, CancellationToken ct)
    {
        for (var i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (code, stdout, _) = await RunProcessAsync("sc.exe", $"query \"{svc}\"", log: null, ct, allowNonZero: true);
            if (code == 0 && stdout.Contains($"STATE", StringComparison.OrdinalIgnoreCase)
                          && stdout.Contains(wantState, StringComparison.OrdinalIgnoreCase))
                return;
            await Task.Delay(1000, ct);
        }
        log?.Report($"[Tasks] Uwaga: usługa '{svc}' nie osiągnęła stanu {wantState} w 30s.");
    }

    private async Task RunPowerShellAsync(CertificateTaskDefinition task, ManagedCertificate cert, IProgress<string>? log, CancellationToken ct)
    {
        var script = task.GetParam(CertificateTaskParameters.P_ScriptPath);
        if (string.IsNullOrWhiteSpace(script) || !File.Exists(script))
            throw new Exception(UIStrings.T("Task_Err_Script", script));
        var args = ExpandVars(task.GetParam(CertificateTaskParameters.P_Arguments), cert);
        await RunProcessAsync("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"{(string.IsNullOrWhiteSpace(args) ? "" : " " + args)}",
            log, ct);
    }

    private async Task RunProgramAsync(CertificateTaskDefinition task, ManagedCertificate cert, IProgress<string>? log, CancellationToken ct)
    {
        var exe = task.GetParam(CertificateTaskParameters.P_ExecutablePath);
        // Sama nazwa (np. cmd.exe, robocopy) = szukaj w PATH, bez File.Exists.
        if (string.IsNullOrWhiteSpace(exe)) throw new Exception(UIStrings.T("Task_Err_ExeMissing"));
        if (!IsBareExeName(exe) && !File.Exists(exe))
            throw new Exception(UIStrings.T("Task_Err_Exe", exe));
        var args = ExpandVars(task.GetParam(CertificateTaskParameters.P_Arguments), cert);
        var workDir = task.GetParam(CertificateTaskParameters.P_WorkingDirectory);
        await RunProcessAsync(exe, args, log, ct,
            workingDirectory: string.IsNullOrWhiteSpace(workDir) ? null : workDir);
    }

    private static async Task RunWaitAsync(CertificateTaskDefinition task, CancellationToken ct)
    {
        if (!int.TryParse(task.GetParam(CertificateTaskParameters.P_Seconds, "30"), out var sec) || sec < 0)
            throw new Exception(UIStrings.T("Task_Err_Seconds"));
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(sec, 3600)), ct);
    }

    private static async Task RunWebhookAsync(CertificateTaskDefinition task, ManagedCertificate cert, bool certSuccess, IProgress<string>? log, CancellationToken ct)
    {
        var url = task.GetParam(CertificateTaskParameters.P_Url);
        if (string.IsNullOrWhiteSpace(url)) throw new Exception(UIStrings.T("Task_Err_Url"));
        var onStatus = task.GetParam(CertificateTaskParameters.P_OnStatus, "Always");
        if (onStatus == "Success" && !certSuccess)
            throw new TaskSkippedException("porazka certyfikatu");
        if (onStatus == "Failure" && certSuccess)
            throw new TaskSkippedException("sukces certyfikatu");
        var payload = JsonSerializer.Serialize(new
        {
            certName = cert.Name,
            primaryDomain = cert.PrimaryDomain,
            domains = cert.Domains,
            success = certSuccess,
            status = cert.Status.ToString(),
            dateExpiry = cert.DateExpiry,
            timestampUtc = DateTime.UtcNow
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(url, content, ct);
        resp.EnsureSuccessStatusCode();
        log?.Report($"[Tasks] Webhook: {(int)resp.StatusCode}.");
    }

    /// <summary>Podmienia {Name} {PrimaryDomain} {Domains} {CertPath} {PfxPath} w argumentach.</summary>
    private static string ExpandVars(string args, ManagedCertificate cert)
    {
        if (string.IsNullOrEmpty(args)) return string.Empty;
        return args
            .Replace("{Name}", cert.Name)
            .Replace("{PrimaryDomain}", cert.PrimaryDomain)
            .Replace("{Domains}", string.Join(",", cert.Domains))
            .Replace("{CertPath}", cert.CertPath ?? "")
            .Replace("{KeyPath}", cert.KeyPath ?? "")
            .Replace("{PfxPath}", cert.PfxPath ?? "");
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunProcessAsync(
        string exe, string args, IProgress<string>? log, CancellationToken ct,
        string? workingDirectory = null, bool allowNonZero = false)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        using var proc = Process.Start(psi) ?? throw new Exception(UIStrings.T("Task_Err_NoStart", exe));
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Anulowanie: nie zostawiaj skryptu/programu dzialajacego w tle.
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        foreach (var line in (stdout + "\n" + stderr).Split('\n').Take(20))
        {
            var t = line.Trim();
            if (!string.IsNullOrEmpty(t)) log?.Report($"[Tasks]   {t}");
        }
        if (proc.ExitCode != 0 && !allowNonZero)
            throw new Exception($"{exe} exit code {proc.ExitCode}: {stderr.Trim().Split('\n').LastOrDefault()?.Trim()}");
        return (proc.ExitCode, stdout, stderr);
    }

    private sealed class TaskSkippedException(string message) : Exception(message);

    private static bool IsBareExeName(string p) =>
        !p.Contains(Path.DirectorySeparatorChar) && !p.Contains(Path.AltDirectorySeparatorChar);
}
