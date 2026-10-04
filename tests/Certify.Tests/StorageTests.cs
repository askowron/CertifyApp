using System.IO.Compression;
using Certify.Core.Models;
using Certify.Core.Services;

namespace Certify.Tests;

public class CertificateStoreTests
{
    [Fact]
    public async Task UpsertLoadDelete_RoundTrips()
    {
        using var tmp = new TempDir();
        var store = new CertificateStore(tmp.Settings());
        Assert.Empty(await store.LoadAllAsync());
        var c = new ManagedCertificate { Name = "a", Domains = ["a.com"], DateExpiry = DateTime.UtcNow.AddDays(10) };
        await store.UpsertAsync(c);
        c.Name = "b";
        await store.UpsertAsync(c);
        var all = await store.LoadAllAsync();
        Assert.Single(all);
        Assert.Equal("b", all[0].Name);
        Assert.Equal(DateTimeKind.Utc, all[0].DateExpiry!.Value.Kind);
        await store.DeleteAsync(c.Id);
        Assert.Empty(await store.LoadAllAsync());
    }

    [Fact]
    public async Task ConcurrentUpserts_DoNotLoseEntries()
    {
        // Regresja: UI + odnawianie w tle robily rownolegle read-modify-write.
        using var tmp = new TempDir();
        var store = new CertificateStore(tmp.Settings());
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() => store.UpsertAsync(new ManagedCertificate { Name = "c" + i }))));
        Assert.Equal(50, (await store.LoadAllAsync()).Count);
        Assert.False(File.Exists(tmp.Settings().CertificatesJsonPath + ".tmp"));
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Load_MissingOrCorruptFile_ReturnsDefaults()
    {
        using var tmp = new TempDir();
        var defaults = tmp.Settings();
        Assert.Same(defaults, new SettingsStore(defaults).Load());
        File.WriteAllText(defaults.SettingsJsonPath, "{ not json");
        Assert.Same(defaults, new SettingsStore(defaults).Load());
    }

    [Fact]
    public void SaveLoad_RoundTripsAndKeepsLocalDataDirectory()
    {
        using var tmp = new TempDir();
        var defaults = tmp.Settings();
        var store = new SettingsStore(defaults);
        store.Save(new AppSettings { DataDirectory = @"C:\elsewhere", SmtpHost = "smtp.x", Language = "en", ExpiryWarningDays = 7 });
        var loaded = store.Load();
        Assert.Equal("smtp.x", loaded.SmtpHost);
        Assert.Equal("en", loaded.Language);
        Assert.Equal(7, loaded.ExpiryWarningDays);
        Assert.Equal(tmp.Path, loaded.DataDirectory);
    }
}

public class BackupEncryptionTests
{
    [Fact]
    public async Task EncryptDecrypt_RoundTrips()
    {
        using var tmp = new TempDir();
        var data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(5000);
        File.WriteAllBytes(tmp.File("in"), data);
        await BackupEncryption.EncryptFileAsync(tmp.File("in"), tmp.File("enc"), "password123");
        Assert.True(BackupEncryption.IsEncrypted(tmp.File("enc")));
        Assert.False(BackupEncryption.IsEncrypted(tmp.File("in")));
        Assert.NotEqual(data, File.ReadAllBytes(tmp.File("enc"))[^data.Length..]);
        await BackupEncryption.DecryptFileAsync(tmp.File("enc"), tmp.File("out"), "password123");
        Assert.Equal(data, File.ReadAllBytes(tmp.File("out")));
    }

    [Fact]
    public async Task Decrypt_WrongPasswordOrTampered_Fails()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("in"), "secret");
        await BackupEncryption.EncryptFileAsync(tmp.File("in"), tmp.File("enc"), "password123");
        await Assert.ThrowsAsync<InvalidOperationException>(() => BackupEncryption.DecryptFileAsync(tmp.File("enc"), tmp.File("out"), "wrongpass1"));
        var bytes = File.ReadAllBytes(tmp.File("enc"));
        bytes[^20] ^= 0xff;
        File.WriteAllBytes(tmp.File("enc"), bytes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => BackupEncryption.DecryptFileAsync(tmp.File("enc"), tmp.File("out"), "password123"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => BackupEncryption.DecryptFileAsync(tmp.File("in"), tmp.File("out"), "password123"));
    }

    [Fact]
    public void ValidatePassword()
    {
        Assert.NotNull(BackupEncryption.ValidatePassword(null));
        Assert.NotNull(BackupEncryption.ValidatePassword("short"));
        Assert.Null(BackupEncryption.ValidatePassword("long-enough"));
    }
}

public class BackupServiceTests
{
    private static async Task<ManagedCertificate> SeedCertAsync(AppSettings s, CertificateStore store, string name, string fileContent)
    {
        var c = new ManagedCertificate { Name = name, Domains = [name + ".com"] };
        var dir = Path.Combine(s.DataDirectory, "certs", c.Id);
        Directory.CreateDirectory(dir);
        c.CertPath = Path.Combine(dir, name + ".com.crt");
        File.WriteAllText(c.CertPath, fileContent);
        await store.UpsertAsync(c);
        return c;
    }

    [Fact]
    public async Task ExportPeekImportReplace_RelocalizesPaths()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var srcStore = new CertificateStore(src.Settings());
        var cert = await SeedCertAsync(src.Settings(), srcStore, "a", "CERT-A");
        File.WriteAllText(src.Settings().AccountsJsonPath, """{"url|a@b.pl":"PEM"}""");
        new SettingsStore(src.Settings()).Save(new AppSettings { SmtpHost = "smtp.src" });

        var zip = src.File("backup.zip");
        var exp = await new BackupService(src.Settings(), srcStore).ExportAsync(zip);
        Assert.True(exp.Success, exp.Message);
        Assert.False(exp.Encrypted);
        using (var archive = ZipFile.OpenRead(zip))
            Assert.Contains(archive.Entries, e => e.FullName == $"certs/{cert.Id}/a.com.crt");

        var dstSettings = dst.Settings();
        var dstStore = new CertificateStore(dstSettings);
        var svc = new BackupService(dstSettings, dstStore);
        var peek = svc.Peek(zip);
        Assert.True(peek.Success);
        Assert.Equal(1, peek.Manifest!.CertCount);

        var imp = await svc.ImportAsync(zip, BackupImportMode.Replace);
        Assert.True(imp.Success, imp.Message);
        Assert.Empty(imp.MissingFiles);
        var imported = Assert.Single(await dstStore.LoadAllAsync());
        Assert.StartsWith(dst.Path, imported.CertPath);
        Assert.Equal("CERT-A", File.ReadAllText(imported.CertPath!));
        Assert.Equal("smtp.src", new SettingsStore(dstSettings).Load().SmtpHost);
        Assert.Contains("a@b.pl", File.ReadAllText(dstSettings.AccountsJsonPath));
    }

    [Fact]
    public async Task ImportMerge_SkipsExistingCertsAndKeepsTheirLocalFiles()
    {
        // Regresja: Merge pomijal cert o istniejacym Id, ale nadpisywal jego pliki starszymi z backupu.
        using var tmp = new TempDir();
        var s = tmp.Settings();
        var store = new CertificateStore(s);
        var cert = await SeedCertAsync(s, store, "a", "OLD");
        var zip = Path.Combine(Path.GetTempPath(), "certify-merge-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            Assert.True((await new BackupService(s, store).ExportAsync(zip)).Success);
            File.WriteAllText(cert.CertPath!, "NEW");
            await SeedCertAsync(s, store, "b", "B"); // tylko lokalnie

            var imp = await new BackupService(s, store).ImportAsync(zip, BackupImportMode.Merge);
            Assert.True(imp.Success, imp.Message);
            Assert.Equal(0, imp.Imported);
            Assert.Equal(1, imp.Skipped);
            Assert.Equal("NEW", File.ReadAllText(cert.CertPath!));
            Assert.Equal(2, (await store.LoadAllAsync()).Count);
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public async Task EncryptedBackup_RequiresPassword()
    {
        using var tmp = new TempDir();
        var s = tmp.Settings();
        var store = new CertificateStore(s);
        await SeedCertAsync(s, store, "a", "X");
        var path = tmp.File("b.cbak");
        var svc = new BackupService(s, store);
        Assert.False((await svc.ExportAsync(path, password: "short")).Success);
        var exp = await svc.ExportAsync(path, password: "password123");
        Assert.True(exp.Success && exp.Encrypted);
        Assert.True(BackupEncryption.IsEncrypted(path));

        var noPass = svc.Peek(path);
        Assert.False(noPass.Success);
        Assert.True(noPass.Encrypted);
        Assert.False(svc.Peek(path, "wrongpass1").Success);
        Assert.True(svc.Peek(path, "password123").Success);
        Assert.True((await svc.ImportAsync(path, BackupImportMode.Replace, password: "password123")).Success);
    }

    [Fact]
    public async Task Export_EmptyPasswordIsNotEncrypted()
    {
        using var tmp = new TempDir();
        var s = tmp.Settings();
        var res = await new BackupService(s, new CertificateStore(s)).ExportAsync(tmp.File("b.zip"), password: "");
        Assert.True(res.Success);
        Assert.False(res.Encrypted);
    }

    [Fact]
    public void Peek_RejectsFileWithoutManifest()
    {
        using var tmp = new TempDir();
        var zip = tmp.File("x.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) z.CreateEntry("other.txt");
        var s = tmp.Settings();
        Assert.False(new BackupService(s, new CertificateStore(s)).Peek(zip).Success);
    }
}
