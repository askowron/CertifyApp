using Certify.Core.Models;
using Certify.Core.Services;
using Certify.Deployment;

namespace Certify.Tests;

public class IisHelperTests
{
    [Theory]
    [InlineData("www.example.com", "*:443:www.example.com", true)]
    [InlineData("*.example.com", "*:443:*.example.com", true)]
    [InlineData("203.0.113.5", "203.0.113.5:443:", false)]
    [InlineData("2001:db8::1", "[2001:db8::1]:443:", false)]
    public void BuildHttpsBindingInfo(string domain, string info, bool sni) =>
        Assert.Equal((info, sni), IisDeployer.BuildHttpsBindingInfo(domain));

    [Fact]
    public void ExtractHosts_FromHttpAndHttpsBindings()
    {
        var hosts = IisBindingHelper.ExtractHosts(
        [
            ("http", "*:80:Example.com"),
            ("https", "*:443:example.com"),
            ("https", "[::1]:443:www.example.com."),
            ("http", "*:80:"),
            ("net.tcp", "808:*"),
            ("https", "*:443:*")
        ]);
        Assert.Equal(["example.com", "www.example.com"], hosts);
    }

    [Fact]
    public void ParseAppCmdOutput_SkipsHeadersAndDuplicates()
    {
        var sites = IisSiteDiscovery.ParseAppCmdOutput("Default Web Site\r\n\"My Site\"\r\nSITE \"x\" (id:1)\r\n\r\nmy site\r\n");
        Assert.Equal(["Default Web Site", "My Site"], sites);
    }
}

public class FileDeployerTests
{
    private static async Task<ManagedCertificate> IssuedAsync(TempDir tmp, string domain = "example.com")
    {
        using var chain = new TestChain(domain);
        var cert = new ManagedCertificate { Domains = [domain] };
        await CertificateArtifactWriter.WriteAsync(tmp.Path, cert, chain.LeafPem, chain.FullChainPem, chain.KeyPem, chain.BuildPfx(), false);
        return cert;
    }

    [Fact]
    public async Task Nginx_CopiesFilesAndPatchesOnlyOwnDirectives()
    {
        using var tmp = new TempDir();
        var cert = await IssuedAsync(tmp, "*.example.com");
        var conf = tmp.File("site.conf");
        File.WriteAllText(conf, "server {\n  ssl_certificate /old/cert.pem;\n  ssl_certificate_key /old/key.pem;\n  proxy_ssl_certificate /upstream/client.pem;\n}\n");
        // '$' w sciezce: wczesniej interpretowany jako podstawienie grupy regex.
        var outDir = Path.Combine(tmp.Path, "out$1");

        var res = await new NginxDeployer().DeployAsync(cert,
            new DeploymentTarget { TargetType = DeploymentTargetType.Nginx, ConfigPath = conf, CertificateOutputPath = outDir }, null, CancellationToken.None);

        Assert.True(res.Success, res.Message);
        var fullchain = Path.Combine(outDir, "_wildcard.example.com-fullchain.crt");
        var key = Path.Combine(outDir, "_wildcard.example.com.key");
        Assert.True(File.Exists(fullchain));
        var text = File.ReadAllText(conf);
        Assert.Contains($"  ssl_certificate {fullchain};", text);
        Assert.Contains($"  ssl_certificate_key {key};", text);
        Assert.Contains("proxy_ssl_certificate /upstream/client.pem;", text);
    }

    [Fact]
    public async Task Apache_PatchesConfigKeepsCrlfAndAddsChain()
    {
        using var tmp = new TempDir();
        var cert = await IssuedAsync(tmp);
        var conf = tmp.File("vhost.conf");
        File.WriteAllText(conf, "<VirtualHost *:443>\r\n    SSLCertificateFile /old/c.crt\r\n    SSLCertificateKeyFile /old/k.key\r\n</VirtualHost>\r\n");
        var outDir = Path.Combine(tmp.Path, "apache");

        var res = await new ApacheDeployer().DeployAsync(cert, new DeploymentTarget
        {
            TargetType = DeploymentTargetType.Apache, ConfigPath = conf, CertificateOutputPath = outDir,
            ServiceName = "certify-test-no-such-service"
        }, null, CancellationToken.None);

        Assert.True(res.Success, res.Message);
        var text = File.ReadAllText(conf);
        Assert.Contains($"SSLCertificateFile {Path.Combine(outDir, "example.com.crt")}", text);
        Assert.Contains($"SSLCertificateKeyFile {Path.Combine(outDir, "example.com.key")}\r\n", text);
        Assert.Contains($"SSLCertificateChainFile {Path.Combine(outDir, "example.com-chain.crt")}", text);
        Assert.DoesNotContain("/old/", text);
    }

    [Fact]
    public async Task FileDeployers_FailWithoutAnyDestination()
    {
        // Regresja: bez ConfigPath i CertificateOutputPath pliki trafialy do katalogu roboczego (System32 dla --renew).
        using var tmp = new TempDir();
        var cert = await IssuedAsync(tmp);
        var target = new DeploymentTarget { TargetType = DeploymentTargetType.Nginx };
        Assert.False((await new NginxDeployer().DeployAsync(cert, target, null, CancellationToken.None)).Success);
        target.TargetType = DeploymentTargetType.Apache;
        Assert.False((await new ApacheDeployer().DeployAsync(cert, target, null, CancellationToken.None)).Success);
    }

    [Fact]
    public async Task FileDeployers_FailWithoutIssuedCertificate()
    {
        var target = new DeploymentTarget { CertificateOutputPath = Path.GetTempPath() };
        Assert.False((await new NginxDeployer().DeployAsync(new ManagedCertificate(), target, null, CancellationToken.None)).Success);
        Assert.False((await new ApacheDeployer().DeployAsync(new ManagedCertificate(), target, null, CancellationToken.None)).Success);
    }

    [Fact]
    public async Task Iis_FailsWithoutPfxOrSite()
    {
        var iis = new IisDeployer();
        Assert.False((await iis.DeployAsync(new ManagedCertificate(), new DeploymentTarget { SiteId = "x" }, null, CancellationToken.None)).Success);
        using var tmp = new TempDir();
        var cert = await IssuedAsync(tmp);
        Assert.False((await iis.DeployAsync(cert, new DeploymentTarget(), null, CancellationToken.None)).Success);
    }
}

public class WindowsServiceHelperTests
{
    [Fact]
    public void ParseServiceNames_ReadsScQueryOutput()
    {
        var output = "\r\nSERVICE_NAME: Apache2.4\r\nDISPLAY_NAME: Apache2.4\r\n        TYPE               : 10  WIN32_OWN_PROCESS\r\n\r\nSERVICE_NAME: W3SVC\r\nDISPLAY_NAME: World Wide Web Publishing Service\r\n";
        Assert.Equal(["Apache2.4", "W3SVC"], WindowsServiceHelper.ParseServiceNames(output));
    }

    [Fact]
    public void Pick_PrefersConfiguredThenPrefix()
    {
        string[] installed = ["W3SVC", "Apache2.4", "nginx"];
        Assert.Equal("nginx", WindowsServiceHelper.Pick(installed, "NGINX", "Apache"));
        // stary domyslny "apache2" nie istnieje na Windows -> wykryta usluga Apache*
        Assert.Equal("Apache2.4", WindowsServiceHelper.Pick(installed, "apache2", "Apache"));
        Assert.Equal("Apache2.4", WindowsServiceHelper.Pick(installed, null, "Apache"));
        Assert.Null(WindowsServiceHelper.Pick(["W3SVC"], null, "Apache"));
    }

    [Fact]
    public void FindNginxExe_WalksUpFromConfig()
    {
        using var tmp = new TempDir();
        var conf = Path.Combine(tmp.Path, "conf");
        Directory.CreateDirectory(conf);
        var exe = Path.Combine(tmp.Path, "nginx.exe");
        File.WriteAllText(exe, "");
        Assert.Equal(exe, NginxDeployer.FindNginxExe(Path.Combine(conf, "nginx.conf")));
        Assert.Null(NginxDeployer.FindNginxExe(null));
    }
}
