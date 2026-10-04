using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Certes;
using Certify.ACME;
using Certify.Core.Models;
using Certify.Core.Services;
// Certes ma wlasny Certes.AcmeException - tu chodzi o wyjatek raw-ACME.
using AcmeException = Certify.ACME.AcmeException;

namespace Certify.Tests;

public class AcmeJwsTests
{
    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0xfb, 0xff })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5 })]
    public void B64U_RoundTrips_WithoutPaddingOrUnsafeChars(byte[] data)
    {
        var s = AcmeJws.B64U(data);
        Assert.DoesNotContain('=', s);
        Assert.DoesNotContain('+', s);
        Assert.DoesNotContain('/', s);
        Assert.Equal(data, AcmeJws.FromB64U(s));
    }

    [Fact]
    public void Thumbprint_MatchesCertes()
    {
        var key = KeyFactory.NewKey(KeyAlgorithm.ES256);
        using var ec = ECDsa.Create();
        ec.ImportFromPem(key.ToPem());
        Assert.Equal(key.Thumbprint(), AcmeJws.AccountThumbprint(ec));
    }

    [Fact]
    public void EncodeJws_SignatureVerifiesWithAccountKey()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var jws = AcmeJws.EncodeJws(ec, """{"alg":"ES256","nonce":"n","url":"u"}""", """{"a":1}""");
        using var doc = JsonDocument.Parse(jws);
        var p = doc.RootElement.GetProperty("protected").GetString()!;
        var pay = doc.RootElement.GetProperty("payload").GetString()!;
        var sig = AcmeJws.FromB64U(doc.RootElement.GetProperty("signature").GetString()!);
        Assert.True(ec.VerifyData(Encoding.ASCII.GetBytes(p + "." + pay), sig, HashAlgorithmName.SHA256));
        Assert.Equal("""{"a":1}""", Encoding.UTF8.GetString(AcmeJws.FromB64U(pay)));
    }

    [Fact]
    public void EncodeJws_PostAsGet_HasEmptyPayload()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var doc = JsonDocument.Parse(AcmeJws.EncodeJws(ec, "{}", ""));
        Assert.Equal("", doc.RootElement.GetProperty("payload").GetString());
    }

    [Fact]
    public void EncodeEab_IsHmacOfProtectedAndJwk()
    {
        var hmacKey = RandomNumberGenerator.GetBytes(32);
        var jwk = """{"crv":"P-256","kty":"EC","x":"x","y":"y"}""";
        var eab = AcmeJws.EncodeEab("kid-1", AcmeJws.B64U(hmacKey), jwk, "https://ca/new-acct");
        using var doc = JsonDocument.Parse(eab);
        var p = doc.RootElement.GetProperty("protected").GetString()!;
        var pay = doc.RootElement.GetProperty("payload").GetString()!;
        var expected = HMACSHA256.HashData(hmacKey, Encoding.ASCII.GetBytes(p + "." + pay));
        Assert.Equal(AcmeJws.B64U(expected), doc.RootElement.GetProperty("signature").GetString());
        Assert.Contains("\"kid\":\"kid-1\"", Encoding.UTF8.GetString(AcmeJws.FromB64U(p)));
        Assert.Equal(jwk, Encoding.UTF8.GetString(AcmeJws.FromB64U(pay)));
    }

    [Fact]
    public void Escape_EscapesQuotesAndBackslashes() =>
        Assert.Equal("a\\\"b\\\\c", AcmeJws.Escape("a\"b\\c"));

    [Fact]
    public void KeyAuthorization_IsTokenDotThumbprint() =>
        Assert.Equal("tok.thumb", AcmeJws.KeyAuthorization("tok", "thumb"));
}

public class AcmeRetryTests
{
    [Fact]
    public void ComputeDelay_IsExponentialAndCapped()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), AcmeRetryPolicy.ComputeDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(4), AcmeRetryPolicy.ComputeDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(8), AcmeRetryPolicy.ComputeDelay(2));
        Assert.Equal(AcmeRetryPolicy.MaxDelay, AcmeRetryPolicy.ComputeDelay(20));
    }

    [Fact]
    public void ComputeDelay_HonoursAndCapsRetryAfter()
    {
        Assert.Equal(TimeSpan.FromSeconds(7), AcmeRetryPolicy.ComputeDelay(0, TimeSpan.FromSeconds(7)));
        Assert.Equal(AcmeRetryPolicy.MaxRetryAfter, AcmeRetryPolicy.ComputeDelay(0, TimeSpan.FromHours(2)));
        Assert.Equal(TimeSpan.Zero, AcmeRetryPolicy.ComputeDelay(0, TimeSpan.FromSeconds(-3)));
    }

    [Fact]
    public void WithJitter_AddsLessThanOneSecond()
    {
        var d = AcmeRetryPolicy.WithJitter(TimeSpan.FromSeconds(5));
        Assert.InRange(d, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void Classify_RawAcme429_RetriesWithRetryAfter()
    {
        var ex = new AcmeException("urn:ietf:params:acme:error:rateLimited", "slow down", 429) { RetryAfter = TimeSpan.FromSeconds(30) };
        var c = AcmeRetry.Classify(ex);
        Assert.NotNull(c);
        Assert.True(c!.Value.Retry);
        Assert.Equal(TimeSpan.FromSeconds(30), c.Value.ServerDelay);
    }

    [Theory]
    [InlineData(500, "urn:ietf:params:acme:error:serverInternal", true)]
    [InlineData(503, "about:blank", true)]
    [InlineData(400, "urn:ietf:params:acme:error:malformed", false)]
    [InlineData(403, "urn:ietf:params:acme:error:unauthorized", false)]
    public void Classify_RawAcme_RetriesOnlyServerErrors(int status, string type, bool retry)
    {
        var c = AcmeRetry.Classify(new AcmeException(type, "x", status));
        Assert.Equal(retry, c?.Retry ?? false);
    }

    [Fact]
    public void Classify_CertesRequestException_UsesErrorStatus()
    {
        var err = new Certes.Acme.AcmeError { Status = HttpStatusCode.TooManyRequests, Type = "urn:ietf:params:acme:error:rateLimited" };
        Assert.True(AcmeRetry.Classify(new AcmeRequestException("limit", err))?.Retry);
        var bad = new Certes.Acme.AcmeError { Status = HttpStatusCode.BadRequest, Type = "urn:ietf:params:acme:error:malformed" };
        Assert.Null(AcmeRetry.Classify(new AcmeRequestException("bad", bad)));
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryNonRetryableErrors()
    {
        var calls = 0;
        await Assert.ThrowsAsync<AcmeException>(() => AcmeRetry.ExecuteAsync<int>(_ =>
        {
            calls++;
            throw new AcmeException("urn:ietf:params:acme:error:malformed", "x", 400);
        }, null, CancellationToken.None, "op"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExecuteAsync_RetriesRateLimitThenSucceeds()
    {
        var calls = 0;
        var log = new ListLog();
        var result = await AcmeRetry.ExecuteAsync(_ =>
        {
            // Retry-After 0 = ponowienie po samym jitterze (test bez 2s czekania).
            if (++calls == 1) throw new AcmeException("urn:ietf:params:acme:error:rateLimited", "slow", 429) { RetryAfter = TimeSpan.Zero };
            return Task.FromResult(42);
        }, log, CancellationToken.None, "op");
        Assert.Equal(42, result);
        Assert.Equal(2, calls);
        Assert.True(log.Contains("[Retry] op"));
    }

    [Fact]
    public async Task ExecuteAsync_StopsAfterMaxAttempts()
    {
        var calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => AcmeRetryPolicy.ExecuteAsync<int>(_ =>
        {
            calls++;
            throw new HttpRequestException("net");
        }, _ => (true, TimeSpan.Zero, "test"), null, CancellationToken.None, "op", maxAttempts: 3));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryUserCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AcmeRetryPolicy.ExecuteAsync<int>(c =>
        {
            calls++;
            c.ThrowIfCancellationRequested();
            return Task.FromResult(1);
        }, null, null, cts.Token, "op"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void NetworkReason_ClassifiesNetworkErrors()
    {
        Assert.NotNull(AcmeRetryPolicy.NetworkReason(new HttpRequestException()));
        Assert.NotNull(AcmeRetryPolicy.NetworkReason(new TimeoutException()));
        Assert.Null(AcmeRetryPolicy.NetworkReason(new InvalidOperationException()));
    }
}

public class DnsChallengeTests
{
    [Fact]
    public void DnsTxtValue_MatchesCertesDnsTxt()
    {
        // Regresja: wczesniej do DNS trafial surowy keyAuthorization.
        var key = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var token = "evaGxfADs6pSRb2LAv9IZf17Dt3juxGJ-PCt92wr-oA";
        var keyAuthz = AcmeJws.KeyAuthorization(token, key.Thumbprint());
        Assert.Equal(key.DnsTxt(token), ChallengeHandlerFactory.DnsTxtValue(keyAuthz));
        Assert.NotEqual(keyAuthz, ChallengeHandlerFactory.DnsTxtValue(keyAuthz));
    }

    [Theory]
    [InlineData("example.com", "_acme-challenge.example.com")]
    [InlineData(" www.example.com. ", "_acme-challenge.www.example.com")]
    public void BuildRecordName(string domain, string expected) =>
        Assert.Equal(expected, CloudflareDns01Handler.BuildRecordName(domain));

    [Fact]
    public void CandidateZones_WalkUpToRegistrableDomain() =>
        Assert.Equal(["a.b.example.com", "b.example.com", "example.com"], CloudflareDns01Handler.CandidateZones("a.b.example.com"));

    [Fact]
    public void DohAnswerMatches_FindsQuotedTxt()
    {
        var json = """{"Status":0,"Answer":[{"name":"_acme-challenge.example.com","type":16,"data":"\"other\""},{"type":16,"data":"\"VALUE\""}]}""";
        Assert.True(CloudflareDns01Handler.DohAnswerMatches(json, "VALUE"));
        Assert.False(CloudflareDns01Handler.DohAnswerMatches(json, "MISSING"));
        Assert.False(CloudflareDns01Handler.DohAnswerMatches("""{"Status":3}""", "VALUE"));
        Assert.False(CloudflareDns01Handler.DohAnswerMatches("not json", "VALUE"));
    }

    [Fact]
    public void Cloudflare_ResultIdFromArrayOrObject()
    {
        Assert.Equal("z1", CloudflareDns01Handler.ResultId("""{"success":true,"result":[{"id":"z1"}]}"""));
        Assert.Equal("r1", CloudflareDns01Handler.ResultId("""{"success":true,"result":{"id":"r1"}}"""));
        Assert.Null(CloudflareDns01Handler.ResultId("""{"success":false,"result":{"id":"r1"}}"""));
        Assert.Null(CloudflareDns01Handler.FirstResultId("""{"success":true,"result":[]}"""));
    }

    [Fact]
    public void Cloudflare_ParseTxtRecords()
    {
        var list = CloudflareDns01Handler.ParseTxtRecords("""{"success":true,"result":[{"id":"1","content":"a"},{"id":"2","content":"b"}]}""");
        Assert.Equal([("1", "a"), ("2", "b")], list);
    }

    [Fact]
    public void Cloudflare_EnsureSuccess_ThrowsWithErrors()
    {
        CloudflareDns01Handler.EnsureCloudflareSuccess("""{"success":true}""", 200);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            CloudflareDns01Handler.EnsureCloudflareSuccess("""{"success":false,"errors":[{"code":9109,"message":"Invalid token"}]}""", 403));
        Assert.Contains("403", ex.Message);
        Assert.Contains("Invalid token", ex.Message);
        Assert.Throws<InvalidOperationException>(() => CloudflareDns01Handler.EnsureCloudflareSuccess("<html>", 502));
    }

    private const string R53Ns = "https://route53.amazonaws.com/doc/2013-04-01/";

    [Fact]
    public void Route53_ChangeBatch_SupportsMultipleValues()
    {
        // Regresja: example.com + *.example.com = dwie wartosci TXT w jednym rrset.
        var xml = Route53Dns01Handler.BuildChangeBatch("_acme-challenge.example.com", ["v1", "v<2>"], "UPSERT");
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace ns = R53Ns;
        Assert.Equal("UPSERT", doc.Descendants(ns + "Action").Single().Value);
        Assert.Equal("_acme-challenge.example.com.", doc.Descendants(ns + "Name").Single().Value);
        Assert.Equal(["\"v1\"", "\"v<2>\""], doc.Descendants(ns + "Value").Select(v => v.Value));
    }

    [Fact]
    public void Route53_PickZoneId_SkipsPrivateAndPartialMatches()
    {
        var xml = $"""
            <ListHostedZonesByNameResponse xmlns="{R53Ns}"><HostedZones>
              <HostedZone><Id>/hostedzone/ZPRIV</Id><Name>example.com.</Name><Config><PrivateZone>true</PrivateZone></Config></HostedZone>
              <HostedZone><Id>/hostedzone/ZOTHER</Id><Name>example.com.au.</Name><Config><PrivateZone>false</PrivateZone></Config></HostedZone>
              <HostedZone><Id>/hostedzone/ZPUB</Id><Name>example.com.</Name><Config><PrivateZone>false</PrivateZone></Config></HostedZone>
            </HostedZones></ListHostedZonesByNameResponse>
            """;
        Assert.Equal("ZPUB", Route53Dns01Handler.PickZoneId(xml, "example.com"));
        Assert.Null(Route53Dns01Handler.PickZoneId(xml, "nope.org"));
    }

    [Fact]
    public void Route53_ParseChangeIdAndStatus()
    {
        var xml = $"""<ChangeResourceRecordSetsResponse xmlns="{R53Ns}"><ChangeInfo><Id>/change/C123</Id><Status>PENDING</Status></ChangeInfo></ChangeResourceRecordSetsResponse>""";
        Assert.Equal("C123", Route53Dns01Handler.ParseChangeId(xml));
        Assert.Equal("PENDING", Route53Dns01Handler.ParseStatus(xml));
        Assert.Throws<InvalidOperationException>(() => Route53Dns01Handler.ParseChangeId("<x/>"));
    }

    [Fact]
    public void Route53_ParseTxtValues_OnlyExactNameAndTxt()
    {
        var xml = $"""
            <ListResourceRecordSetsResponse xmlns="{R53Ns}"><ResourceRecordSets>
              <ResourceRecordSet><Name>_acme-challenge.example.com.</Name><Type>TXT</Type>
                <ResourceRecords><ResourceRecord><Value>"a"</Value></ResourceRecord><ResourceRecord><Value>"b"</Value></ResourceRecord></ResourceRecords></ResourceRecordSet>
              <ResourceRecordSet><Name>_acme-challenge.www.example.com.</Name><Type>TXT</Type>
                <ResourceRecords><ResourceRecord><Value>"c"</Value></ResourceRecord></ResourceRecords></ResourceRecordSet>
            </ResourceRecordSets></ListResourceRecordSetsResponse>
            """;
        Assert.Equal(["a", "b"], Route53Dns01Handler.ParseTxtValues(xml, "_acme-challenge.example.com"));
    }

    [Fact]
    public void DnsJanitor_TargetDomains_StripsWildcardAndIps()
    {
        var cert = new ManagedCertificate { Domains = ["*.Example.com", "example.com", "www.example.com.", "203.0.113.1"] };
        Assert.Equal(["example.com", "www.example.com"], DnsJanitor.TargetDomains(cert));
    }

    [Fact]
    public void ChallengeHandlerFactory_PicksProviderOnlyWithCredentials()
    {
        var cert = new ManagedCertificate();
        cert.ChallengeConfig.DnsProvider = "Cloudflare";
        Assert.IsType<Dns01ManualHandler>(ChallengeHandlerFactory.CreateDns(cert));
        cert.ChallengeConfig.DnsProviderCredentials[ChallengeHandlerFactory.CloudflareTokenKey] = "tok";
        Assert.IsType<CloudflareDns01Handler>(ChallengeHandlerFactory.CreateDns(cert));

        cert.ChallengeConfig.DnsProvider = "Route53";
        cert.ChallengeConfig.DnsProviderCredentials[ChallengeHandlerFactory.AwsAccessKeyId] = "AK";
        Assert.False(ChallengeHandlerFactory.IsRoute53(cert));
        cert.ChallengeConfig.DnsProviderCredentials[ChallengeHandlerFactory.AwsSecretKey] = "SK";
        Assert.IsType<Route53Dns01Handler>(ChallengeHandlerFactory.CreateDns(cert));
    }

    [Fact]
    public async Task Http01Handler_WritesAndCleansChallengeFile()
    {
        using var tmp = new TempDir();
        var handler = new Http01FilesystemHandler(_ => tmp.Path);
        await handler.PrepareAsync("example.com", "tok", "tok.thumb", null, CancellationToken.None);
        var file = Path.Combine(tmp.Path, ".well-known", "acme-challenge", "tok");
        Assert.Equal("tok.thumb", File.ReadAllText(file));
        await handler.CleanupAsync("example.com", "tok", null, CancellationToken.None);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Http01Handler_CreatesIisWebConfigOnce()
    {
        // IIS bez web.config nie serwuje tokenu (plik bez rozszerzenia) -> 404 przy walidacji.
        using var tmp = new TempDir();
        var handler = new Http01FilesystemHandler(_ => tmp.Path);
        await handler.PrepareAsync("example.com", "tok", "k", null, CancellationToken.None);
        var webConfig = Path.Combine(tmp.Path, ".well-known", "acme-challenge", "web.config");
        var xml = System.Xml.Linq.XDocument.Load(webConfig);
        Assert.Contains(xml.Descendants("remove"), e => (string?)e.Attribute("fileExtension") == ".");
        Assert.Contains(xml.Descendants("mimeMap"), e => (string?)e.Attribute("fileExtension") == ".");

        File.WriteAllText(webConfig, "<configuration>user</configuration>");
        await handler.PrepareAsync("example.com", "tok2", "k", null, CancellationToken.None);
        Assert.Equal("<configuration>user</configuration>", File.ReadAllText(webConfig));
    }

    [Fact]
    public async Task Http01Handler_Cleanup_LogsOnlyRealDeletion()
    {
        using var tmp = new TempDir();
        var log = new ListLog();
        await new Http01FilesystemHandler(_ => tmp.Path).CleanupAsync("example.com", "never-written", log, CancellationToken.None);
        Assert.False(log.Contains("Usunięto"));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Http01Handler_AccessDenied_GivesActionableMessage()
    {
        using var tmp = new TempDir();
        var webroot = Path.Combine(tmp.Path, "garage");
        Directory.CreateDirectory(webroot);
        var dir = new DirectoryInfo(webroot);
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(sid,
            System.Security.AccessControl.FileSystemRights.CreateDirectories | System.Security.AccessControl.FileSystemRights.CreateFiles,
            System.Security.AccessControl.AccessControlType.Deny);
        var acl = dir.GetAccessControl();
        acl.AddAccessRule(deny);
        dir.SetAccessControl(acl);
        try
        {
            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                new Http01FilesystemHandler(_ => webroot).PrepareAsync("garage.example.com", "tok", "k", null, CancellationToken.None));
            Assert.Contains(webroot, ex.Message);
            Assert.Contains("icacls", ex.Message);
            Assert.Contains(Environment.UserName, ex.Message);
            Assert.IsType<UnauthorizedAccessException>(ex.InnerException);
        }
        finally
        {
            acl = dir.GetAccessControl();
            acl.RemoveAccessRule(deny);
            dir.SetAccessControl(acl);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task Http01Handler_SelfCheck_LogsOkWhenServedContentMatches()
    {
        using var tmp = new TempDir();
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("tok.thumb\n") });
        var log = new ListLog();
        await new Http01FilesystemHandler(_ => tmp.Path, new HttpClient(stub)).PrepareAsync("example.com", "tok", "tok.thumb", log, CancellationToken.None);
        Assert.Equal("http://example.com/.well-known/acme-challenge/tok", Assert.Single(stub.Requests).ToString());
        Assert.True(log.Contains("Self-check OK"));
    }

    [Fact]
    public async Task Http01Handler_SelfCheck_WarnsOn404WithoutFailing()
    {
        using var tmp = new TempDir();
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var log = new ListLog();
        await new Http01FilesystemHandler(_ => tmp.Path, new HttpClient(stub)).PrepareAsync("example.com", "tok", "tok.thumb", log, CancellationToken.None);
        Assert.True(log.Contains("404"));
        Assert.True(File.Exists(Path.Combine(tmp.Path, ".well-known", "acme-challenge", "tok")));
    }

    [Fact]
    public async Task Http01Handler_SelfCheck_BracketsIpv6()
    {
        using var tmp = new TempDir();
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("k") });
        await new Http01FilesystemHandler(_ => tmp.Path, new HttpClient(stub)).PrepareAsync("2001:db8::1", "tok", "k", null, CancellationToken.None);
        Assert.Equal("http://[2001:db8::1]/.well-known/acme-challenge/tok", Assert.Single(stub.Requests).ToString());
    }

    [Fact]
    public async Task Http01Handler_ThrowsWithoutWebroot()
    {
        var handler = new Http01FilesystemHandler(_ => null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.PrepareAsync("example.com", "t", "k", null, CancellationToken.None));
    }
}

public class SigV4Tests
{
    [Fact]
    public void Encode_FollowsRfc3986()
    {
        Assert.Equal("a-b_c.d~e", SigV4Signer.Encode("a-b_c.d~e", true));
        Assert.Equal("a%20b%2Fc%2A", SigV4Signer.Encode("a b/c*", true));
        Assert.Equal("a/b", SigV4Signer.Encode("a/b", false));
        Assert.Equal("%C5%BC", SigV4Signer.Encode("ż", true));
    }

    [Fact]
    public void CanonicalQueryString_IsSortedAndEncoded()
    {
        var q = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["type"] = "TXT", ["name"] = "_acme-challenge.example.com." };
        Assert.Equal("name=_acme-challenge.example.com.&type=TXT", SigV4Signer.CanonicalQueryString(q));
    }

    [Fact]
    public void Sha256Hex_OfEmptyString() =>
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", SigV4Signer.Sha256Hex(""));

    [Fact]
    public void AuthorizationHeader_IsDeterministicAndWellFormed()
    {
        var q = new SortedDictionary<string, string>();
        string Sign(string secret) => SigV4Signer.AuthorizationHeader("GET", "/2013-04-01/change/C1", q,
            "route53.amazonaws.com", "", "AKIDEXAMPLE", secret, "20260101T000000Z", "20260101");
        var a = Sign("secret");
        Assert.Equal(a, Sign("secret"));
        Assert.NotEqual(a, Sign("other"));
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20260101/us-east-1/route53/aws4_request, SignedHeaders=host;x-amz-date, Signature=", a);
        Assert.Matches("Signature=[0-9a-f]{64}$", a);
    }
}

public class ZeroSslEabTests
{
    [Fact]
    public void ParseResponse_Success()
    {
        var (kid, hmac) = ZeroSslEab.ParseResponse("""{"success":true,"eab_kid":"KID","eab_hmac_key":"HMAC"}""");
        Assert.Equal("KID", kid);
        Assert.Equal("HMAC", hmac);
    }

    [Theory]
    [InlineData("""{"success":false,"error":{"code":2900,"type":"invalid_email"}}""", "invalid_email")]
    [InlineData("""{"success":false,"error":{"code":2901}}""", "2901")]
    public void ParseResponse_ErrorCarriesTypeOrCode(string json, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ZeroSslEab.ParseResponse(json));
        Assert.Equal(expected, ex.Message);
    }

    [Fact]
    public void ParseResponse_MissingFields_Throws() =>
        Assert.Throws<InvalidOperationException>(() => ZeroSslEab.ParseResponse("""{"success":true,"eab_kid":""}"""));
}

public class IpIdentifierTests
{
    [Theory]
    [InlineData("203.0.113.5", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("[2001:db8::1]", true)]
    [InlineData("example.com", false)]
    [InlineData("*.example.com", false)]
    public void IsIp(string value, bool expected) => Assert.Equal(expected, IdentifierClassifier.IsIp(value));

    [Fact]
    public void Split_TrimsAndDropsEmpty() =>
        Assert.Equal([("example.com", false), ("203.0.113.5", true)], IdentifierClassifier.Split([" example.com ", "", "203.0.113.5"]));

    [Fact]
    public void BuildCsr_ContainsDnsAndIpSans()
    {
        var cert = new ManagedCertificate { Domains = ["example.com", "203.0.113.5", "[2001:db8::1]"] };
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = CertificateRequest.LoadSigningRequest(IpOrderProcessor.BuildCsr(cert, key), HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        Assert.Equal("CN=example.com", req.SubjectName.Name);
        var san = req.CertificateExtensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Equal(["example.com"], san.EnumerateDnsNames());
        Assert.Equal([IPAddress.Parse("203.0.113.5"), IPAddress.Parse("2001:db8::1")], san.EnumerateIPAddresses());
    }

    [Fact]
    public async Task IpOrder_RejectsNonLetsEncryptCa()
    {
        using var tmp = new TempDir();
        var cert = new ManagedCertificate { Domains = ["203.0.113.5"], CertificateAuthority = CertificateAuthority.ZeroSsl, EmailAddress = "a@b.pl" };
        var res = await new IpOrderProcessor(tmp.Settings()).ProcessAsync(cert, null, CancellationToken.None, false);
        Assert.False(res.Success);
        Assert.Equal(CertificateStatus.Error, cert.Status);
    }

    [Fact]
    public void BuildPfx_AcceptsChainWithUnknownTopIssuer()
    {
        // Regresja (ZeroSSL): ostatni cert lancucha (Sectigo Root E46) ma wystawce spoza
        // listy Certes - Certes PfxBuilder rzucal "Can not find issuer", nasz PFX ma powstac.
        var certesKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var ec = ECDsa.Create();
        ec.ImportFromPem(certesKey.ToPem());
        using var chain = new TestChain(leafKey: ec);
        var chainWithoutRoot = chain.LeafPem + "\n" + chain.IntermediatePem + "\n";

        var certesChain = new Certes.Acme.CertificateChain(chainWithoutRoot);
        // Oba helpery Certes uzywane wczesniej odrzucaja taki lancuch.
        Assert.Throws<Certes.AcmeException>(() => certesChain.ToPfx(certesKey).Build("x", "certify"));
        Assert.Throws<Certes.AcmeException>(() => certesChain.ToPem());

        // Cala sciezka z LetsEncryptService: pobrany lancuch -> leaf PEM, chain PEM, PFX.
        var (leafPem, chainPem, pfx) = LetsEncryptService.BuildArtifacts(certesChain, certesKey);
        Assert.Equal(chain.Leaf.Thumbprint, X509Certificate2.CreateFromPem(leafPem).Thumbprint);
        Assert.Equal([chain.Leaf.Thumbprint, chain.Intermediate.Thumbprint],
            CertificateArtifactWriter.SplitPemBlocks(chainPem).Select(b => X509Certificate2.CreateFromPem(b).Thumbprint));
        var col = new X509Certificate2Collection();
        col.Import(pfx, CertificateArtifactWriter.PfxPassword, X509KeyStorageFlags.EphemeralKeySet);
        Assert.Equal(2, col.Count);
        Assert.Single(col.Cast<X509Certificate2>(), c => c.HasPrivateKey && c.Thumbprint == chain.Leaf.Thumbprint);
    }

    [Fact]
    public async Task LetsEncryptService_ValidatesInputWithoutNetwork()
    {
        using var tmp = new TempDir();
        var svc = new LetsEncryptService(tmp.Settings());
        Assert.False((await svc.RequestCertificateAsync(new ManagedCertificate { EmailAddress = "a@b.pl" })).Success);
        Assert.False((await svc.RequestCertificateAsync(new ManagedCertificate { Domains = ["example.com"] })).Success);
    }
}
