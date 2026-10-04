using System.IO.Compression;
using System.Text.Json;
using Certify.Core.Localization;
using Certify.Core.Models;

namespace Certify.Core.Services;

public enum BackupImportMode
{
    Merge,
    Replace
}

public record BackupManifest(
    int Version,
    DateTime ExportedAtUtc,
    string Machine,
    int CertCount,
    bool HasAccounts,
    bool HasSettings);

public class BackupResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int Imported { get; set; }
    public int Skipped { get; set; }
    public List<string> MissingFiles { get; set; } = new();
    public BackupManifest? Manifest { get; set; }
    public bool Encrypted { get; set; }
}

/// <summary>
/// Backup i przenoszenie miedzy maszynami: zip z manifestem, modelami,
/// ustawieniami, kluczami kont ACME i plikami certs/. Sciezki artefaktow
/// sa przy imporcie przepisywane na lokalny DataDirectory (przenosnosc).
/// Logi celowo pomijane. Paczka zawiera sekrety (klucze, tokeny) - chronic plik.
/// </summary>
public class BackupService(AppSettings settings, CertificateStore store)
{
    private const int ManifestVersion = 1;
    private const string ManifestName = "manifest.json";
    private const string CertsJson = "managed_certificates.json";
    private const string SettingsJson = "appsettings.json";
    private const string AccountsJson = "acme_accounts.json";
    private const string CertsDir = "certs";

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task<BackupResult> ExportAsync(string zipPath, IProgress<string>? log = null, CancellationToken ct = default, string? password = null)
    {
        try
        {
            if (!string.IsNullOrEmpty(password))
            {
                var err = BackupEncryption.ValidatePassword(password);
                if (err != null) return new BackupResult { Success = false, Message = err };
            }
            var certs = await store.LoadAllAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
            var tmpZip = Path.Combine(Path.GetTempPath(), "certify-export-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                BuildZip(tmpZip, certs, log, ct);
                if (!string.IsNullOrEmpty(password))
                {
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                    await BackupEncryption.EncryptFileAsync(tmpZip, zipPath, password, ct);
                    log?.Report($"[Backup] Zapisano SZYFROWANY backup -> {zipPath}");
                }
                else
                {
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                    File.Move(tmpZip, zipPath);
                }
            }
            finally
            {
                try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
            }

            var manifest = new BackupManifest(ManifestVersion, DateTime.UtcNow,
                Environment.MachineName, certs.Count,
                File.Exists(settings.AccountsJsonPath), File.Exists(settings.SettingsJsonPath));
            var encrypted = !string.IsNullOrEmpty(password);
            return new BackupResult { Success = true, Message = encrypted ? UIStrings.T("Bk_Msg_ExportedEnc", certs.Count) : UIStrings.T("Bk_Msg_Exported", certs.Count), Manifest = manifest, Encrypted = encrypted };
        }
        catch (Exception ex)
        {
            return new BackupResult { Success = false, Message = ex.Message };
        }
    }

    private void BuildZip(string zipPath, List<ManagedCertificate> certs, IProgress<string>? log, CancellationToken ct)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var manifest = new BackupManifest(ManifestVersion, DateTime.UtcNow,
            Environment.MachineName, certs.Count,
            File.Exists(settings.AccountsJsonPath), File.Exists(settings.SettingsJsonPath));
        zip.CreateEntryFromString(ManifestName, JsonSerializer.Serialize(manifest, Opts));
        zip.CreateEntryFromString(CertsJson, JsonSerializer.Serialize(certs, Opts));
        if (File.Exists(settings.SettingsJsonPath))
            zip.CreateEntryFromFile(settings.SettingsJsonPath, SettingsJson);
        if (File.Exists(settings.AccountsJsonPath))
            zip.CreateEntryFromFile(settings.AccountsJsonPath, AccountsJson);
        var certsDir = Path.Combine(settings.DataDirectory, CertsDir);
        var files = 0;
        if (Directory.Exists(certsDir))
        {
            foreach (var file in Directory.GetFiles(certsDir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                zip.CreateEntryFromFile(file, Path.GetRelativePath(settings.DataDirectory, file).Replace('\\', '/'));
                files++;
            }
        }
        log?.Report($"[Backup] Spakowano {certs.Count} certyfikatów + {files} plików.");
    }

    public BackupResult Peek(string zipPath, string? password = null)
    {
        try
        {
            var encrypted = BackupEncryption.IsEncrypted(zipPath);
            if (encrypted && string.IsNullOrEmpty(password))
                return new BackupResult { Success = false, Message = UIStrings.T("Bk_Err_Encrypted"), Encrypted = true };
            string effective = zipPath;
            string? tmp = null;
            try
            {
                if (encrypted)
                {
                    tmp = Path.Combine(Path.GetTempPath(), "certify-peek-" + Guid.NewGuid().ToString("N") + ".zip");
                    BackupEncryption.DecryptFileAsync(zipPath, tmp, password!).GetAwaiter().GetResult();
                    effective = tmp;
                }
                using var zip = ZipFile.OpenRead(effective);
                var entry = zip.GetEntry(ManifestName)
                    ?? throw new InvalidOperationException(UIStrings.T("Bk_Err_NoManifest"));
                using var sr = new StreamReader(entry.Open());
                var manifest = JsonSerializer.Deserialize<BackupManifest>(sr.ReadToEnd(), Opts)
                    ?? throw new InvalidOperationException(UIStrings.T("Bk_Err_BadManifest"));
                if (manifest.Version != ManifestVersion)
                    throw new InvalidOperationException(UIStrings.T("Bk_Err_Version", manifest.Version));
                return new BackupResult { Success = true, Message = "OK", Manifest = manifest, Encrypted = encrypted };
            }
            finally
            {
                try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
        catch (Exception ex)
        {
            return new BackupResult { Success = false, Message = ex.Message, Encrypted = BackupEncryption.IsEncrypted(zipPath) };
        }
    }

    public async Task<BackupResult> ImportAsync(string zipPath, BackupImportMode mode, IProgress<string>? log = null, CancellationToken ct = default, string? password = null)
    {
        var res = new BackupResult();
        string effective = zipPath;
        string? tmpZip = null;
        string tmp = Path.Combine(Path.GetTempPath(), "certify-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (BackupEncryption.IsEncrypted(zipPath))
            {
                if (string.IsNullOrEmpty(password))
                    return new BackupResult { Success = false, Message = UIStrings.T("Bk_Err_Encrypted"), Encrypted = true };
                tmpZip = Path.Combine(Path.GetTempPath(), "certify-import-" + Guid.NewGuid().ToString("N") + ".zip");
                try { await BackupEncryption.DecryptFileAsync(zipPath, tmpZip, password, ct); }
                catch (Exception ex) { return new BackupResult { Success = false, Message = ex.Message, Encrypted = true }; }
                effective = tmpZip;
            }
            var peek = Peek(effective);
            if (!peek.Success) return peek;
            res.Manifest = peek.Manifest;
            res.Encrypted = BackupEncryption.IsEncrypted(zipPath);

            Directory.CreateDirectory(tmp);
            ZipFile.ExtractToDirectory(effective, tmp, overwriteFiles: true);

            var certsJson = Path.Combine(tmp, CertsJson);
            if (!File.Exists(certsJson))
                return new BackupResult { Success = false, Message = UIStrings.T("Bk_Err_NoCerts") };
            var imported = JsonSerializer.Deserialize<List<ManagedCertificate>>(await File.ReadAllTextAsync(certsJson, ct), Opts) ?? new();

            // W Merge certy o istniejacym Id sa pomijane - ich lokalnych (nowszych)
            // plikow nie wolno nadpisac starszymi z backupu.
            var existingBefore = mode == BackupImportMode.Merge
                ? new HashSet<string>((await store.LoadAllAsync()).Select(c => c.Id))
                : new HashSet<string>();

            // Pliki certs/ -> lokalny DataDirectory (nadpisz).
            var srcCerts = Path.Combine(tmp, CertsDir);
            if (Directory.Exists(srcCerts))
            {
                foreach (var file in Directory.GetFiles(srcCerts, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = Path.GetRelativePath(srcCerts, file);
                    var certId = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                    if (existingBefore.Contains(certId)) continue;
                    var dest = Path.Combine(settings.DataDirectory, CertsDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, overwrite: true);
                }
            }

            // Konta ACME: polacz (klucz = directoryUrl|email).
            var accFile = Path.Combine(tmp, AccountsJson);
            if (File.Exists(accFile))
            {
                var incoming = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(accFile, ct), Opts) ?? new();
                Dictionary<string, string> current = new();
                if (File.Exists(settings.AccountsJsonPath))
                {
                    try { current = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(settings.AccountsJsonPath, ct), Opts) ?? new(); }
                    catch { }
                }
                if (mode == BackupImportMode.Replace) current.Clear();
                foreach (var kv in incoming) current[kv.Key] = kv.Value;
                Directory.CreateDirectory(Path.GetDirectoryName(settings.AccountsJsonPath)!);
                await File.WriteAllTextAsync(settings.AccountsJsonPath, JsonSerializer.Serialize(current, Opts), ct);
            }

            // Ustawienia: tylko w Replace (oprocz DataDirectory).
            if (mode == BackupImportMode.Replace)
            {
                var setFile = Path.Combine(tmp, SettingsJson);
                if (File.Exists(setFile))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(setFile, ct), Opts);
                    if (loaded != null)
                    {
                        loaded.DataDirectory = settings.DataDirectory;
                        new SettingsStore(settings).Save(loaded);
                    }
                }
            }

            var existing = await store.LoadAllAsync();
            if (mode == BackupImportMode.Replace)
            {
                existing.Clear();
            }
            var existingIds = new HashSet<string>(existing.Select(c => c.Id));
            foreach (var cert in imported)
            {
                ct.ThrowIfCancellationRequested();
                if (mode == BackupImportMode.Merge && existingIds.Contains(cert.Id))
                {
                    res.Skipped++;
                    continue;
                }
                Relocalize(cert, res);
                var idx = existing.FindIndex(c => c.Id == cert.Id);
                if (idx >= 0) existing[idx] = cert; else existing.Add(cert);
                res.Imported++;
            }
            await store.SaveAllAsync(existing);

            res.Success = true;
            res.Message = UIStrings.T("Bk_Msg_Imported", res.Imported, res.Skipped, res.MissingFiles.Count);
            log?.Report($"[Backup] Import ({mode}): {res.Message}");
            foreach (var m in res.MissingFiles.Take(10)) log?.Report($"[Backup] Brak pliku: {m}");
            return res;
        }
        catch (Exception ex)
        {
            return new BackupResult { Success = false, Message = ex.Message };
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            try { if (tmpZip != null && File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
        }
    }

    /// <summary>Przepisz sciezki artefaktow na lokalny DataDirectory + weryfikacja.</summary>
    private void Relocalize(ManagedCertificate cert, BackupResult res)
    {
        var dir = Path.Combine(settings.DataDirectory, CertsDir, cert.Id);
        cert.PfxPath = Relocate(cert.PfxPath, dir, res);
        cert.CertPath = Relocate(cert.CertPath, dir, res);
        cert.KeyPath = Relocate(cert.KeyPath, dir, res);
    }

    private string? Relocate(string? oldPath, string localDir, BackupResult res)
    {
        if (string.IsNullOrWhiteSpace(oldPath)) return oldPath;
        var local = Path.Combine(localDir, Path.GetFileName(oldPath));
        if (!File.Exists(local)) res.MissingFiles.Add(local);
        return local;
    }
}

internal static class ZipEntryExtensions
{
    public static void CreateEntryFromString(this ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var w = new StreamWriter(entry.Open());
        w.Write(content);
    }
}
