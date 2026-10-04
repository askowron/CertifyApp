using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Certify.Core.Localization;
using Certify.Core.Models;

namespace Certify.Core.Services;

/// <summary>
/// Format docelowy eksportu (lista jak w Certify The Web: Export Certificate).
/// </summary>
public enum CertificateExportFormat
{
    PemPrimary,
    PemIntermediateWithRoot,
    PemIntermediateOnly,
    PemPrivateKey,
    PemFullChainExcludingKey,
    PemFullChainIncludingKey,
    PemPrimaryWithIntermediate,
    Pfx
}

public static class CertificateExportFormatExtensions
{
    public static string DisplayName(this CertificateExportFormat f) => f switch
    {
        CertificateExportFormat.PemPrimary => "PEM - Primary Certificate (e.g. .crt)",
        CertificateExportFormat.PemIntermediateWithRoot => "PEM - Intermediate Certificate Chain + Root CA Cert (e.g. .chain)",
        CertificateExportFormat.PemIntermediateOnly => "PEM - Intermediate Certificate Chain (e.g. .pem)",
        CertificateExportFormat.PemPrivateKey => "PEM - Private Key (e.g. .key)",
        CertificateExportFormat.PemFullChainExcludingKey => "PEM - Full Certificate Chain (Excluding Key)",
        CertificateExportFormat.PemFullChainIncludingKey => "PEM - Full Certificate Chain (Including Key)",
        CertificateExportFormat.PemPrimaryWithIntermediate => "PEM - Primary Certificate + Intermediate Certificate Chain (e.g. .crt)",
        CertificateExportFormat.Pfx => "PFX (PKCS#12), Full certificate including private key",
        _ => f.ToString()
    };

    public static string DefaultExtension(this CertificateExportFormat f) => f switch
    {
        CertificateExportFormat.PemPrivateKey => ".key",
        CertificateExportFormat.PemIntermediateOnly => ".pem",
        CertificateExportFormat.PemIntermediateWithRoot => ".chain",
        CertificateExportFormat.Pfx => ".pfx",
        _ => ".crt"
    };

    public static string DialogFilter(this CertificateExportFormat f) => f switch
    {
        CertificateExportFormat.PemPrivateKey => "Private key (*.key)|*.key|All files (*.*)|*.*",
        CertificateExportFormat.Pfx => "PFX (*.pfx)|*.pfx|All files (*.*)|*.*",
        CertificateExportFormat.PemIntermediateOnly => "PEM (*.pem)|*.pem|All files (*.*)|*.*",
        CertificateExportFormat.PemIntermediateWithRoot => "Chain (*.chain)|*.chain|All files (*.*)|*.*",
        _ => "Certificate (*.crt)|*.crt|PEM (*.pem)|*.pem|All files (*.*)|*.*"
    };
}

public class CertificateExportResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ExportedPath { get; set; }
}

/// <summary>
/// Eksport wystawionego certyfikatu do pliku w wybranym formacie.
/// Zrodla: katalog certyfikatu (certs/&lt;id&gt;/): *.crt (leaf),
/// chain.pem (full chain), *.key (klucz), *.pfx.
/// </summary>
public class CertificateExporter
{
    private static readonly Regex PemBlockRegex = new(
        @"-----BEGIN CERTIFICATE-----.*?-----END CERTIFICATE-----",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public async Task<CertificateExportResult> ExportAsync(
        ManagedCertificate cert,
        CertificateExportFormat format,
        string destinationPath,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cert.CertPath) || !File.Exists(cert.CertPath))
                return Fail(UIStrings.T("Exp_Err_NoCert"));
            if (string.IsNullOrWhiteSpace(destinationPath))
                return Fail(UIStrings.T("Exp_Err_NoDest"));

            var dir = Path.GetDirectoryName(cert.CertPath)!;
            var chainPath = Path.Combine(dir, "chain.pem");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);

            switch (format)
            {
                case CertificateExportFormat.PemPrimary:
                    File.Copy(cert.CertPath, destinationPath, true);
                    break;

                case CertificateExportFormat.PemPrivateKey:
                    if (string.IsNullOrWhiteSpace(cert.KeyPath) || !File.Exists(cert.KeyPath))
                        return Fail(UIStrings.T("Exp_Err_NoKey"));
                    File.Copy(cert.KeyPath, destinationPath, true);
                    break;

                case CertificateExportFormat.Pfx:
                    if (string.IsNullOrWhiteSpace(cert.PfxPath) || !File.Exists(cert.PfxPath))
                        return Fail(UIStrings.T("Exp_Err_NoPfx"));
                    File.Copy(cert.PfxPath, destinationPath, true);
                    break;

                case CertificateExportFormat.PemFullChainExcludingKey:
                    RequireChain(chainPath);
                    File.Copy(chainPath, destinationPath, true);
                    break;

                case CertificateExportFormat.PemFullChainIncludingKey:
                    RequireChain(chainPath);
                    if (string.IsNullOrWhiteSpace(cert.KeyPath) || !File.Exists(cert.KeyPath))
                        return Fail(UIStrings.T("Exp_Err_NoKey"));
                    var fullchain = await File.ReadAllTextAsync(chainPath, ct);
                    var key = await File.ReadAllTextAsync(cert.KeyPath!, ct);
                    await File.WriteAllTextAsync(destinationPath, fullchain.TrimEnd() + "\n" + key.Trim() + "\n", ct);
                    break;

                case CertificateExportFormat.PemIntermediateWithRoot:
                case CertificateExportFormat.PemIntermediateOnly:
                case CertificateExportFormat.PemPrimaryWithIntermediate:
                    RequireChain(chainPath);
                    var leafPem = await File.ReadAllTextAsync(cert.CertPath, ct);
                    var chainPem = await File.ReadAllTextAsync(chainPath, ct);
                    var built = BuildFromChain(leafPem, chainPem, format);
                    if (built == null)
                        return Fail(UIStrings.T("Exp_Err_NoChain"));
                    await File.WriteAllTextAsync(destinationPath, built, ct);
                    break;

                default:
                    return Fail(UIStrings.T("Exp_Err_Format", format));
            }

            return new CertificateExportResult { Success = true, Message = "OK", ExportedPath = destinationPath };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static CertificateExportResult Fail(string message) =>
        new() { Success = false, Message = message };

    private static void RequireChain(string chainPath)
    {
        if (!File.Exists(chainPath))
            throw new FileNotFoundException(UIStrings.T("Exp_Err_ChainFile"));
    }

    private static string? BuildFromChain(string leafPem, string chainPem, CertificateExportFormat format)
    {
        var leafBlocks = SplitBlocks(leafPem);
        var chainBlocks = SplitBlocks(chainPem);
        if (chainBlocks.Count == 0) return null;

        var leafSet = new HashSet<string>(leafBlocks.Select(Normalize), StringComparer.Ordinal);
        // intermediate + root = wszystko z chain oprocz leaf
        var nonLeaf = chainBlocks.Where(b => !leafSet.Contains(Normalize(b))).ToList();
        if (nonLeaf.Count == 0) return null;

        return format switch
        {
            CertificateExportFormat.PemIntermediateWithRoot =>
                Join(nonLeaf),
            CertificateExportFormat.PemIntermediateOnly =>
                Join(nonLeaf.Where(b => !IsSelfSigned(b)).ToList()) is { } s && s.Length > 0 ? s : Join(nonLeaf),
            CertificateExportFormat.PemPrimaryWithIntermediate =>
                leafPem.TrimEnd() + "\n" + Join(nonLeaf.Where(b => !IsSelfSigned(b)).ToList()),
            _ => null
        };
    }

    private static List<string> SplitBlocks(string pem) =>
        PemBlockRegex.Matches(pem).Select(m => m.Value.Trim()).ToList();

    private static string Normalize(string block) =>
        new(block.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static string Join(List<string> blocks) =>
        string.Join("\n", blocks) + "\n";

    private static bool IsSelfSigned(string pemBlock)
    {
        try
        {
            var x509 = X509Certificate2.CreateFromPem(pemBlock);
            return string.Equals(x509.Subject, x509.Issuer, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
