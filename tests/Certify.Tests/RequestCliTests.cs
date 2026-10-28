using System.Text.Json;
using Certify.Core.Services;
using Certify.Service;

namespace Certify.Tests;

public class RequestCliTests
{
    [Fact]
    public void Parse_Full()
    {
        var (a, err) = RequestCli.Parse(new[] { "--request", "x.appit.pl", "--site", "x.appit.pl",
            "--template", "garage.appit.pl", "--webroot", @"C:\w" });
        Assert.Null(err);
        Assert.Equal(new RequestArgs("x.appit.pl", "x.appit.pl", "garage.appit.pl", @"C:\w"), a);
    }

    [Fact]
    public void Parse_MissingSite_Error()
    {
        var (a, err) = RequestCli.Parse(new[] { "--request", "x.appit.pl" });
        Assert.Null(a);
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void Parse_UnknownOption_Error()
    {
        var (a, err) = RequestCli.Parse(new[] { "--request", "x.appit.pl", "--site", "s", "--foo" });
        Assert.Null(a);
        Assert.Contains("--foo", err);
    }

    [Fact]
    public void Parse_MissingValue_Error()
    {
        var (a, err) = RequestCli.Parse(new[] { "--request", "x.appit.pl", "--site" });
        Assert.Null(a);
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void Parse_HostLowercased()
    {
        var (a, _) = RequestCli.Parse(new[] { "--request", "X.Appit.PL", "--site", "s" });
        Assert.Equal("x.appit.pl", a!.Host);
    }

    [Fact]
    public void ResultLine_Format()
    {
        var exp = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var line = RequestCli.ResultLine(new RequestOutcome(RequestResult.Issued, "x.appit.pl", exp, "AB12", "ok"));
        Assert.StartsWith("CERTIFYAPP-REQUEST {", line);
        using var doc = JsonDocument.Parse(line["CERTIFYAPP-REQUEST ".Length..]);
        var r = doc.RootElement;
        Assert.Equal("issued", r.GetProperty("result").GetString());
        Assert.Equal("x.appit.pl", r.GetProperty("host").GetString());
        Assert.Equal(exp, r.GetProperty("expiry").GetDateTime().ToUniversalTime());
        Assert.Equal("AB12", r.GetProperty("thumbprint").GetString());
        Assert.Equal("ok", r.GetProperty("message").GetString());
    }

    [Fact]
    public async Task RunAsync_BadArgs_Exit2_WithResultLine()
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await RequestCli.RunAsync(new[] { "--request" }, o, e);
        Assert.Equal(2, code);
        var last = o.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last();
        Assert.StartsWith("CERTIFYAPP-REQUEST", last);
        Assert.Contains("\"result\":\"badargs\"", last);
        Assert.NotEmpty(e.ToString());
    }

    [Fact]
    public void Parse_ValueStartingWithDashes_Error()
    {
        var (a, err) = RequestCli.Parse(new[] { "--request", "x.appit.pl", "--site", "s", "--webroot", "--site" });
        Assert.Null(a);
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void ResultLine_PolishMessage_IsAsciiAndRoundTrips()
    {
        var msg = "Nie udało się: zażółć gęślą jaźń";
        var line = RequestCli.ResultLine(new RequestOutcome(RequestResult.Failed, "x.appit.pl", null, null, msg));
        Assert.All(line, c => Assert.True(c <= 0x7F));
        using var doc = JsonDocument.Parse(line["CERTIFYAPP-REQUEST ".Length..]);
        Assert.Equal(msg, doc.RootElement.GetProperty("message").GetString());
    }
}
