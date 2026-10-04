using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Certify.Core.Models;
using Certify.Core.Services;

namespace Certify.Tests;

/// <summary>Katalog tymczasowy usuwany po tescie.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "certify-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public AppSettings Settings() => new() { DataDirectory = Path };

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}

/// <summary>Lancuch testowy: self-signed root -> intermediate -> leaf (ECDSA P-256).</summary>
public sealed class TestChain : IDisposable
{
    public X509Certificate2 Root { get; }
    public X509Certificate2 Intermediate { get; }
    public X509Certificate2 Leaf { get; }
    public ECDsa LeafKey { get; }

    public string LeafPem => Leaf.ExportCertificatePem();
    public string IntermediatePem => Intermediate.ExportCertificatePem();
    public string RootPem => Root.ExportCertificatePem();
    /// <summary>Jak chain.pem z CA: leaf + intermediate + root.</summary>
    public string FullChainPem => LeafPem + "\n" + IntermediatePem + "\n" + RootPem + "\n";
    public string KeyPem => LeafKey.ExportPkcs8PrivateKeyPem();

    /// <param name="leafKey">Klucz leafa (np. zaimportowany z Certes); domyslnie nowy P-256.</param>
    public TestChain(string leafDns = "example.com", DateTimeOffset? notAfter = null, ECDsa? leafKey = null)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var end = notAfter ?? now.AddDays(90);

        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootReq = new CertificateRequest("CN=Test Root", rootKey, HashAlgorithmName.SHA256);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        Root = rootReq.CreateSelfSigned(now, now.AddYears(5));

        using var intKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intReq = new CertificateRequest("CN=Test Intermediate", intKey, HashAlgorithmName.SHA256);
        intReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var intPublic = intReq.Create(Root, now, now.AddYears(4), [1, 2, 3, 4]);
        Intermediate = intPublic.CopyWithPrivateKey(intKey);

        LeafKey = leafKey ?? ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafReq = new CertificateRequest($"CN={leafDns}", LeafKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(leafDns);
        leafReq.CertificateExtensions.Add(san.Build());
        Leaf = leafReq.Create(Intermediate, now, end, [5, 6, 7, 8]);
    }

    public byte[] BuildPfx() =>
        CertificateArtifactWriter.BuildPfx(LeafPem, FullChainPem, LeafKey, CertificateArtifactWriter.PfxPassword);

    public void Dispose()
    {
        Root.Dispose();
        Intermediate.Dispose();
        Leaf.Dispose();
        LeafKey.Dispose();
    }
}

/// <summary>Synchroniczny IProgress (Progress&lt;T&gt; raportuje asynchronicznie przez SynchronizationContext).</summary>
public sealed class ListLog : IProgress<string>
{
    public List<string> Lines { get; } = new();
    public void Report(string value) { lock (Lines) Lines.Add(value); }
    public bool Contains(string fragment) { lock (Lines) return Lines.Any(l => l.Contains(fragment)); }
}
