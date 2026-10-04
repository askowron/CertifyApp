using Certify.Deployment;

namespace Certify.Tests;

public class WebServerScannerTests
{
    [Fact]
    public void Apache_VirtualHosts_WithIncludeDefineAndAliases()
    {
        using var tmp = new TempDir();
        var root = tmp.Path.Replace('\\', '/');
        Directory.CreateDirectory(tmp.File("conf/extra"));
        File.WriteAllText(tmp.File("conf/httpd.conf"), $$"""
            Define SRVROOT "{{root}}"
            ServerRoot "${SRVROOT}"
            DocumentRoot "${SRVROOT}/htdocs"
            # Include conf/extra/nie-ma.conf
            IncludeOptional conf/extra/*.conf
            """);
        File.WriteAllText(tmp.File("conf/extra/vhosts.conf"), """
            <VirtualHost *:80>
                ServerName example.com:80
                ServerAlias www.example.com *.example.com
                DocumentRoot "htdocs/example"
            </VirtualHost>
            <VirtualHost *:443>
                ServerName shop.example.com
                DocumentRoot C:/sites/shop
                SSLEngine on
                SSLCertificateFile conf/ssl/old.crt
            </VirtualHost>
            """);

        var sites = WebServerConfigScanner.ParseApache(tmp.File("conf/httpd.conf"));

        var main = Assert.Single(sites, s => s.ConfigFile.EndsWith("httpd.conf"));
        Assert.Empty(main.Hostnames);
        Assert.Equal(Path.GetFullPath(tmp.File("htdocs")), main.DocumentRoot);

        var ex = Assert.Single(sites, s => s.Hostnames.Contains("example.com"));
        Assert.Equal(["example.com", "www.example.com", "*.example.com"], ex.Hostnames);
        Assert.Equal(Path.GetFullPath(tmp.File("htdocs/example")), ex.DocumentRoot);
        Assert.False(ex.Ssl);
        Assert.EndsWith("vhosts.conf", ex.ConfigFile);

        var shop = Assert.Single(sites, s => s.Hostnames.Contains("shop.example.com"));
        Assert.True(shop.Ssl);
        Assert.Equal(Path.GetFullPath("C:/sites/shop"), shop.DocumentRoot);
    }

    [Fact]
    public void Apache_IncludeCycle_DoesNotLoop()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("a.conf"), "Include b.conf\n<VirtualHost *:80>\nServerName a.test\n</VirtualHost>");
        File.WriteAllText(tmp.File("b.conf"), "Include a.conf");
        var sites = WebServerConfigScanner.ParseApache(tmp.File("a.conf"), serverRoot: tmp.Path);
        Assert.Single(sites, s => s.Hostnames.Contains("a.test"));
    }

    [Fact]
    public void Nginx_ServerBlocks_WithIncludeRootAndSsl()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.File("conf/sites"));
        File.WriteAllText(tmp.File("conf/nginx.conf"), """
            worker_processes 1;
            http {
                include mime.types;   # nie istnieje - pomijamy
                server {
                    listen 80;
                    server_name _;
                    root html;
                }
                include sites/*.conf;
            }
            """);
        File.WriteAllText(tmp.File("conf/sites/app.conf"), """
            server {
                listen 443 ssl;
                server_name app.example.com .example.org "~^www\d+$";
                root "D:/www/app";
                ssl_certificate     old.crt;
                ssl_certificate_key old.key;
                location /static { root D:/other; }
            }
            """);

        var sites = WebServerConfigScanner.ParseNginx(tmp.File("conf/nginx.conf"));

        Assert.Equal(2, sites.Count);
        var def = sites[0];
        Assert.Empty(def.Hostnames);
        Assert.Equal(Path.GetFullPath(tmp.File("html")), def.DocumentRoot);
        Assert.False(def.Ssl);

        var app = sites[1];
        Assert.Equal(["app.example.com", "example.org"], app.Hostnames);
        Assert.Equal(Path.GetFullPath("D:/www/app"), app.DocumentRoot);
        Assert.True(app.Ssl);
        Assert.EndsWith("app.conf", app.ConfigFile);
    }

    [Theory]
    [InlineData("        BINARY_PATH_NAME   : \"C:\\Apache24\\bin\\httpd.exe\" -k runservice", "C:\\Apache24\\bin\\httpd.exe")]
    [InlineData("        BINARY_PATH_NAME   : C:\\nginx\\nginx.exe", "C:\\nginx\\nginx.exe")]
    [InlineData("        BINARY_PATH_NAME   : C:\\Program Files\\nginx\\nginx.exe -p x", "C:\\Program Files\\nginx\\nginx.exe")]
    public void ParseScQcBinary(string line, string expected) =>
        Assert.Equal(expected, WebServerConfigScanner.ParseScQcBinary("SERVICE_NAME: x\r\n" + line + "\r\n"));

    [Fact]
    public void ConfigForBinary_MapsExeToMainConfig()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.File("bin"));
        Directory.CreateDirectory(tmp.File("conf"));
        File.WriteAllText(tmp.File("conf/httpd.conf"), "");
        File.WriteAllText(tmp.File("conf/nginx.conf"), "");
        Assert.Equal(Path.Combine(tmp.Path, "conf", "httpd.conf"), WebServerConfigScanner.ConfigForBinary(WebServerKind.Apache, Path.Combine(tmp.Path, "bin", "httpd.exe")));
        Assert.Equal(Path.Combine(tmp.Path, "conf", "nginx.conf"), WebServerConfigScanner.ConfigForBinary(WebServerKind.Nginx, tmp.File("nginx.exe")));
        Assert.Null(WebServerConfigScanner.ConfigForBinary(WebServerKind.Nginx, tmp.File("bin/nssm.exe")));
    }
}
