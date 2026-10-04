using Certify.Core.Localization;
using Certify.Core.Services;

namespace Certify.Core.Models;

/// <summary>
/// Typ zadania (jak w CTW: Deployment Tasks / Pre-Request Tasks).
/// </summary>
public enum CertificateTaskType
{
    ExportCertificate,
    RestartService,
    RunPowerShellScript,
    RunProgram,
    Wait,
    Webhook
}

/// <summary>
/// Etap wykonania: przed requestem (Pre-Request) albo po standardowym
/// wdrozeniu (Deployment). Kolejnosc = kolejnosc na liscie.
/// </summary>
public enum CertificateTaskStage
{
    PreRequest,
    Deployment
}

/// <summary>
/// Kiedy uruchamiac zadanie Deployment: tylko po sukcesie (CTW "Run On Success")
/// albo zawsze (tez po bledzie).
/// </summary>
public enum CertificateTaskTrigger
{
    OnSuccess,
    Always
}

public enum CertificateTaskLastStatus
{
    NeverRun,
    Success,
    Failed,
    Skipped
}

public class CertificateTaskDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public CertificateTaskType TaskType { get; set; } = CertificateTaskType.ExportCertificate;
    public CertificateTaskStage Stage { get; set; } = CertificateTaskStage.Deployment;
    public CertificateTaskTrigger Trigger { get; set; } = CertificateTaskTrigger.OnSuccess;
    public bool Enabled { get; set; } = true;

    public Dictionary<string, string> Parameters { get; set; } = new();

    public DateTime? LastRunUtc { get; set; }
    public CertificateTaskLastStatus LastStatus { get; set; } = CertificateTaskLastStatus.NeverRun;
    public string LastMessage { get; set; } = string.Empty;

    public string GetParam(string key, string? defaultValue = null) =>
        Parameters.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : (defaultValue ?? string.Empty);

    // Do wyswietlenia na liscie w oknie edycji (DisplayMemberPath).
    public string TaskDisplay =>
        $"{Name} [{CertificateTaskParameters.TaskTypeDisplay(TaskType)}] \u2022 " +
        $"{CertificateTaskParameters.TriggerDisplay(Trigger)} \u2022 " +
        $"{(Enabled ? UIStrings.T("Task_On") : UIStrings.T("Task_Off"))} \u2022 " +
        $"{UIStrings.T("Task_Last", CertificateTaskParameters.StatusDisplay(LastStatus, LastRunUtc, LastMessage))}";
}

public static class CertificateTaskParameters
{
    // Klucze parametrow per typ
    public const string P_Format = "Format";
    public const string P_DestinationPath = "DestinationPath";
    public const string P_ServiceName = "ServiceName";
    public const string P_ServiceAction = "Action";
    public const string P_ScriptPath = "ScriptPath";
    public const string P_Arguments = "Arguments";
    public const string P_ExecutablePath = "ExecutablePath";
    public const string P_WorkingDirectory = "WorkingDirectory";
    public const string P_Seconds = "Seconds";
    public const string P_Url = "Url";
    public const string P_OnStatus = "OnStatus";

    public static string TaskTypeDisplay(CertificateTaskType t) => t switch
    {
        CertificateTaskType.ExportCertificate => UIStrings.T("TaskType_Export"),
        CertificateTaskType.RestartService => UIStrings.T("TaskType_Restart"),
        CertificateTaskType.RunPowerShellScript => UIStrings.T("TaskType_Ps"),
        CertificateTaskType.RunProgram => UIStrings.T("TaskType_Run"),
        CertificateTaskType.Wait => UIStrings.T("TaskType_Wait"),
        CertificateTaskType.Webhook => UIStrings.T("TaskType_Webhook"),
        _ => t.ToString()
    };

    public static string TriggerDisplay(CertificateTaskTrigger t) => t switch
    {
        CertificateTaskTrigger.OnSuccess => UIStrings.T("Trig_OnSuccess"),
        CertificateTaskTrigger.Always => UIStrings.T("Trig_Always"),
        _ => t.ToString()
    };

    public static string StageDisplay(CertificateTaskStage s) => s switch
    {
        CertificateTaskStage.PreRequest => UIStrings.T("Stage_Pre"),
        CertificateTaskStage.Deployment => UIStrings.T("Stage_Depl"),
        _ => s.ToString()
    };

    public static string StatusDisplay(CertificateTaskLastStatus s, DateTime? when, string msg) => s switch
    {
        CertificateTaskLastStatus.Success =>
            $"Success {(when?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "")} {msg}".Trim(),
        CertificateTaskLastStatus.Failed =>
            $"Failed {(when?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "")} {msg}".Trim(),
        CertificateTaskLastStatus.Skipped => UIStrings.T("Task_Skipped"),
        _ => UIStrings.T("Task_Never")
    };

    public static IReadOnlyList<TaskParameterDescriptor> GetDescriptors(CertificateTaskType type) => type switch
    {
        CertificateTaskType.ExportCertificate =>
        [
            new(P_Format, UIStrings.T("Param_Format"), true, nameof(CertificateExportFormat.PemFullChainExcludingKey),
                Enum.GetNames<CertificateExportFormat>()),
            new(P_DestinationPath, UIStrings.T("Param_Dest"), true, Browse: TaskParamBrowse.SaveFile,
                FileFilter: "Cert (*.crt;*.pem;*.pfx;*.key;*.chain)|*.crt;*.pem;*.pfx;*.key;*.chain|All files (*.*)|*.*")
        ],
        CertificateTaskType.RestartService =>
        [
            new(P_ServiceName, UIStrings.T("Param_Svc"), true),
            new(P_ServiceAction, UIStrings.T("Param_Action"), true, "Restart", ["Restart", "Start", "Stop"])
        ],
        CertificateTaskType.RunPowerShellScript =>
        [
            new(P_ScriptPath, UIStrings.T("Param_Script"), true, Browse: TaskParamBrowse.OpenFile,
                FileFilter: "PowerShell (*.ps1)|*.ps1|All files (*.*)|*.*"),
            new(P_Arguments, UIStrings.T("Param_Args"))
        ],
        CertificateTaskType.RunProgram =>
        [
            new(P_ExecutablePath, UIStrings.T("Param_Exe"), true, Browse: TaskParamBrowse.OpenFile,
                FileFilter: "Exe/Bat (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|All files (*.*)|*.*"),
            new(P_Arguments, UIStrings.T("Param_Args")),
            new(P_WorkingDirectory, UIStrings.T("Param_WorkDir"), Browse: TaskParamBrowse.Folder)
        ],
        CertificateTaskType.Wait =>
        [
            new(P_Seconds, UIStrings.T("Param_Seconds"), true, "30")
        ],
        CertificateTaskType.Webhook =>
        [
            new(P_Url, "URL", true),
            new(P_OnStatus, UIStrings.T("Param_OnStatus"), true, "Always", ["Always", "Success", "Failure"])
        ],
        _ => []
    };
}

public enum TaskParamBrowse
{
    None,
    OpenFile,
    SaveFile,
    Folder
}

public record TaskParameterDescriptor(
    string Key,
    string Label,
    bool Required = false,
    string? Default = null,
    string[]? Options = null,
    TaskParamBrowse Browse = TaskParamBrowse.None,
    string? FileFilter = null);
