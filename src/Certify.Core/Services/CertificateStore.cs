using System.Text.Json;
using Certify.Core.Models;

namespace Certify.Core.Services;

public class CertificateStore
{
    // Jeden zamek na proces: UI i odnawianie w tle pisza ten sam plik
    // (bez tego read-modify-write gubil zmiany albo konczyl sie IOException).
    private static readonly SemaphoreSlim FileLock = new(1, 1);

    private readonly AppSettings _settings;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CertificateStore(AppSettings settings)
    {
        _settings = settings;
        Directory.CreateDirectory(_settings.DataDirectory);
        Directory.CreateDirectory(_settings.LogsDirectory);
    }

    public async Task<List<ManagedCertificate>> LoadAllAsync()
    {
        await FileLock.WaitAsync();
        try { return await LoadUnlockedAsync(); }
        finally { FileLock.Release(); }
    }

    public async Task SaveAllAsync(List<ManagedCertificate> certs)
    {
        await FileLock.WaitAsync();
        try { await SaveUnlockedAsync(certs); }
        finally { FileLock.Release(); }
    }

    public async Task UpsertAsync(ManagedCertificate cert)
    {
        await FileLock.WaitAsync();
        try
        {
            var all = await LoadUnlockedAsync();
            var idx = all.FindIndex(c => c.Id == cert.Id);
            if (idx >= 0) all[idx] = cert;
            else all.Add(cert);
            await SaveUnlockedAsync(all);
        }
        finally { FileLock.Release(); }
    }

    public async Task DeleteAsync(string id)
    {
        await FileLock.WaitAsync();
        try
        {
            var all = await LoadUnlockedAsync();
            all.RemoveAll(c => c.Id == id);
            await SaveUnlockedAsync(all);
        }
        finally { FileLock.Release(); }
    }

    private async Task<List<ManagedCertificate>> LoadUnlockedAsync()
    {
        var path = _settings.CertificatesJsonPath;
        if (!File.Exists(path)) return new List<ManagedCertificate>();
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<List<ManagedCertificate>>(json, _jsonOptions) ?? new();
    }

    private async Task SaveUnlockedAsync(List<ManagedCertificate> certs)
    {
        var path = _settings.CertificatesJsonPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(certs, _jsonOptions);
        // Zapis atomowy: plik tymczasowy + podmiana (crash w trakcie nie psuje JSON).
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }
}
