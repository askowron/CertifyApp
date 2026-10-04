namespace Certify.Core.Models;

using Certify.Core.Localization;

public enum HistoryKind
{
    Request,
    Test,
    Renew,
    Deploy,
    Export
}

public class CertificateHistoryEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public HistoryKind Kind { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;

    public string Display =>
        $"{TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {(Success ? "✓" : "✕")} {KindDisplay} - {Message}";

    public static string KindDisplayOf(HistoryKind k) => k switch
    {
        HistoryKind.Test => UIStrings.T("Hist_Kind_Test"),
        _ => k.ToString()
    };

    public string KindDisplay => KindDisplayOf(Kind);
}

public static class CertificateHistoryLog
{
    public const int MaxEntries = 100;

    public static void Add(ManagedCertificate cert, HistoryKind kind, bool success, string message)
    {
        cert.History.Add(new CertificateHistoryEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Kind = kind,
            Success = success,
            Message = message.Length > 200 ? message[..200] : message
        });
        if (cert.History.Count > MaxEntries)
            cert.History.RemoveRange(0, cert.History.Count - MaxEntries);
    }

    public static (int Total, int Success, int Failed) Stats(ManagedCertificate cert) =>
        (cert.History.Count, cert.History.Count(h => h.Success), cert.History.Count(h => !h.Success));
}
