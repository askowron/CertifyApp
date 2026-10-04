using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace Certify.ACME;

/// <summary>
/// Automatyczny dns-01 przez AWS Route53 (ChangeResourceRecordSets + wait INSYNC + DoH).
/// Creds: Access Key ID + Secret (IAM: route53:ChangeResourceRecordSets,
/// route53:ListHostedZonesByName, route53:GetChange na strefach). Bez AWS SDK.
/// </summary>
public class Route53Dns01Handler : IChallengeHandler
{
    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly int _propagationTimeoutSec;
    private readonly HttpClient _http;
    // domena|token -> (zoneId, wartosc TXT). Route53 trzyma wszystkie wartosci TXT
    // jednej nazwy w jednym rrset (example.com + *.example.com = 2 wartosci).
    private readonly Dictionary<string, (string ZoneId, string Value)> _created = new();

    private static string CreatedKey(string domain, string token) => $"{domain}|{token}";

    public Route53Dns01Handler(string accessKey, string secretKey, int propagationTimeoutSec = 90, HttpClient? http = null)
    {
        _accessKey = accessKey;
        _secretKey = secretKey;
        _propagationTimeoutSec = Math.Clamp(propagationTimeoutSec, 10, 1800);
        _http = http ?? new HttpClient();
    }

    public async Task PrepareAsync(string domain, string token, string keyAuthz, IProgress<string>? log, CancellationToken ct)
    {
        var recordName = CloudflareDns01Handler.BuildRecordName(domain);
        log?.Report($"[DNS-01/R53] Szukam strefy dla {domain} ...");
        var zoneId = await FindZoneIdAsync(domain, ct)
            ?? throw new InvalidOperationException(Certify.Core.Localization.UIStrings.T("Acme_Err_NoZoneR53", domain));
        // UPSERT z suma istniejacych wartosci: CREATE na istniejacym rrset konczy sie
        // bledem (drugi challenge dla tej samej nazwy, np. wildcard + apex).
        var values = await ListTxtValuesAsync(zoneId, recordName, ct);
        if (!values.Contains(keyAuthz)) values.Add(keyAuthz);
        lock (_created) _created[CreatedKey(domain, token)] = (zoneId, keyAuthz);
        log?.Report($"[DNS-01/R53] UPSERT TXT {recordName} ({values.Count} wartości) ...");
        var changeId = await ChangeTxtAsync(zoneId, recordName, values, "UPSERT", ct);
        await WaitInSyncAsync(changeId, log, ct);
        await WaitDohAsync(recordName, keyAuthz, log, ct);
    }

    public async Task CleanupAsync(string domain, string token, IProgress<string>? log, CancellationToken ct)
    {
        (string ZoneId, string Value) entry;
        lock (_created)
        {
            var key = CreatedKey(domain, token);
            if (!_created.TryGetValue(key, out entry)) return;
            _created.Remove(key);
        }
        try
        {
            var recordName = CloudflareDns01Handler.BuildRecordName(domain);
            var current = await ListTxtValuesAsync(entry.ZoneId, recordName, ct);
            if (!current.Contains(entry.Value)) return;
            var remaining = current.Where(v => v != entry.Value).ToList();
            // DELETE wymaga dokladnie calego rrset; gdy zostaja inne wartosci - UPSERT bez naszej.
            if (remaining.Count == 0)
                await ChangeTxtAsync(entry.ZoneId, recordName, current, "DELETE", ct);
            else
                await ChangeTxtAsync(entry.ZoneId, recordName, remaining, "UPSERT", ct);
            log?.Report($"[DNS-01/R53] Usunięto TXT {recordName}.");
        }
        catch (Exception ex) { log?.Report($"[DNS-01/R53] Cleanup warn: {ex.Message}"); }
    }

    /// <summary>Usuwa WSZYSTKIE rekordy TXT _acme-challenge.domena (osierocone).</summary>
    public async Task<int> CleanupStaleAsync(string domain, IProgress<string>? log, CancellationToken ct)
    {
        var recordName = CloudflareDns01Handler.BuildRecordName(domain);
        var zoneId = await FindZoneIdAsync(domain, ct)
            ?? throw new InvalidOperationException(Certify.Core.Localization.UIStrings.T("Acme_Err_NoZoneR53", domain));
        var current = await ListTxtValuesAsync(zoneId, recordName, ct);
        if (current.Count == 0) return 0;
        // Jeden DELETE na caly rrset (DELETE pojedynczej wartosci z wielu nie przechodzi).
        await ChangeTxtAsync(zoneId, recordName, current, "DELETE", ct);
        log?.Report($"[DNS-01/R53] Usunięto osierocony TXT {recordName} ({current.Count})");
        return current.Count;
    }

    private async Task<string?> FindZoneIdAsync(string domain, CancellationToken ct)
    {
        foreach (var candidate in CloudflareDns01Handler.CandidateZones(domain))
        {
            var query = new SortedDictionary<string, string> { ["dnsname"] = candidate + ".", ["maxitems"] = "10" };
            var body = await SignedAsync(HttpMethod.Get, "/2013-04-01/hostedzonesbyname", query, "", ct);
            var id = PickZoneId(body, candidate);
            if (id != null) return id;
        }
        return null;
    }

    private async Task<string> ChangeTxtAsync(string zoneId, string recordName, IEnumerable<string> values, string action, CancellationToken ct)
    {
        var payload = BuildChangeBatch(recordName, values, action);
        var body = await SignedAsync(HttpMethod.Post, $"/2013-04-01/hostedzone/{zoneId}/rrset/", new(), payload, ct);
        return ParseChangeId(body);
    }

    private async Task WaitInSyncAsync(string changeId, IProgress<string>? log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Min(_propagationTimeoutSec, 300));
        log?.Report("[DNS-01/R53] Czekam na INSYNC ...");
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var body = await SignedAsync(HttpMethod.Get, $"/2013-04-01/change/{changeId}", new(), "", ct);
            if (ParseStatus(body) == "INSYNC")
            {
                log?.Report("[DNS-01/R53] INSYNC.");
                return;
            }
            await Task.Delay(5000, ct);
        }
        log?.Report("[DNS-01/R53] UWAGA: brak INSYNC w czasie - próbuję dalej.");
    }

    private async Task WaitDohAsync(string recordName, string expectedTxt, IProgress<string>? log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_propagationTimeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(recordName)}&type=TXT");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
                using var resp = await _http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode &&
                    CloudflareDns01Handler.DohAnswerMatches(await resp.Content.ReadAsStringAsync(ct), expectedTxt))
                {
                    log?.Report("[DNS-01/R53] TXT widoczny w DNS.");
                    return;
                }
            }
            catch (Exception ex) { log?.Report($"[DNS-01/R53] DoH check warn: {ex.Message}"); }
            await Task.Delay(5000, ct);
        }
        log?.Report("[DNS-01/R53] UWAGA: TXT niepotwierdzony w czasie - próbuję walidację mimo to.");
    }

    private async Task<List<string>> ListTxtValuesAsync(string zoneId, string recordName, CancellationToken ct)
    {
        var query = new SortedDictionary<string, string>
        {
            ["name"] = recordName + ".",
            ["type"] = "TXT",
            ["maxitems"] = "10"
        };
        var body = await SignedAsync(HttpMethod.Get, $"/2013-04-01/hostedzone/{zoneId}/rrset", query, "", ct);
        return ParseTxtValues(body, recordName);
    }

    private async Task<string> SignedAsync(HttpMethod method, string path, SortedDictionary<string, string> query, string payload, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString("yyyyMMddTHHmmssZ");
        var dateStamp = now.ToString("yyyyMMdd");
        var uri = new Uri(SigV4Signer.Endpoint + path + (query.Count > 0 ? "?" + SigV4Signer.CanonicalQueryString(query) : ""));
        using var req = new HttpRequestMessage(method, uri);
        var auth = SigV4Signer.AuthorizationHeader(method.Method, path, query,
            uri.Host, payload, _accessKey, _secretKey, amzDate, dateStamp);
        req.Headers.TryAddWithoutValidation("Authorization", auth);
        req.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        if (payload.Length > 0)
            req.Content = new StringContent(payload, Encoding.UTF8, "application/xml");
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Route53 {(int)resp.StatusCode}: {Short(ExtractError(body))}");
        return body;
    }

    // ---- czyste funkcje (testowalne) ----

    public static string BuildChangeBatch(string recordName, string txt, string action) =>
        BuildChangeBatch(recordName, [txt], action);

    public static string BuildChangeBatch(string recordName, IEnumerable<string> values, string action) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <ChangeResourceRecordSetsRequest xmlns="https://route53.amazonaws.com/doc/2013-04-01/">
          <ChangeBatch>
            <Changes>
              <Change>
                <Action>{action}</Action>
                <ResourceRecordSet>
                  <Name>{System.Security.SecurityElement.Escape(recordName)}.</Name>
                  <Type>TXT</Type>
                  <TTL>120</TTL>
                  <ResourceRecords>
                    {string.Join("", values.Select(v => $"<ResourceRecord><Value>\"{System.Security.SecurityElement.Escape(v)}\"</Value></ResourceRecord>"))}
                  </ResourceRecords>
                </ResourceRecordSet>
              </Change>
            </Changes>
          </ChangeBatch>
        </ChangeResourceRecordSetsRequest>
        """;

    /// <summary>Wybierz publiczna strefe dokladnie pasujaca do kandydata (Name ma trailing dot).</summary>
    public static string? PickZoneId(string listXml, string candidate)
    {
        try
        {
            var doc = XDocument.Parse(listXml);
            XNamespace ns = "https://route53.amazonaws.com/doc/2013-04-01/";
            var want = candidate.TrimEnd('.') + ".";
            foreach (var z in doc.Descendants(ns + "HostedZone"))
            {
                var name = z.Element(ns + "Name")?.Value;
                var isPrivate = z.Element(ns + "Config")?.Element(ns + "PrivateZone")?.Value == "true";
                if (!isPrivate && name != null && name.Equals(want, StringComparison.OrdinalIgnoreCase))
                {
                    var id = z.Element(ns + "Id")?.Value; // /hostedzone/Z123
                    if (id != null) return id.Split('/').Last();
                }
            }
        }
        catch { }
        return null;
    }

    public static string ParseChangeId(string changeXml)
    {
        try
        {
            var doc = XDocument.Parse(changeXml);
            XNamespace ns = "https://route53.amazonaws.com/doc/2013-04-01/";
            var id = doc.Descendants(ns + "Id").FirstOrDefault()?.Value;
            if (id != null) return id.Split('/').Last();
        }
        catch { }
        throw new InvalidOperationException(Certify.Core.Localization.UIStrings.T("Acme_Err_NoChangeId"));
    }

    public static string ParseStatus(string changeXml)
    {
        try
        {
            var doc = XDocument.Parse(changeXml);
            XNamespace ns = "https://route53.amazonaws.com/doc/2013-04-01/";
            return doc.Descendants(ns + "Status").FirstOrDefault()?.Value ?? "?";
        }
        catch { return "?"; }
    }

    public static List<string> ParseTxtValues(string rrsetXml, string recordName)
    {
        var list = new List<string>();
        try
        {
            var doc = XDocument.Parse(rrsetXml);
            XNamespace ns = "https://route53.amazonaws.com/doc/2013-04-01/";
            var want = recordName.TrimEnd('.') + ".";
            foreach (var rs in doc.Descendants(ns + "ResourceRecordSet"))
            {
                if (rs.Element(ns + "Name")?.Value.Equals(want, StringComparison.OrdinalIgnoreCase) != true) continue;
                if (rs.Element(ns + "Type")?.Value != "TXT") continue;
                foreach (var rr in rs.Descendants(ns + "ResourceRecord"))
                {
                    var v = rr.Element(ns + "Value")?.Value.Trim().Trim('"');
                    if (!string.IsNullOrEmpty(v)) list.Add(v);
                }
            }
        }
        catch { }
        return list;
    }

    internal static string ExtractError(string body)
    {
        try
        {
            var doc = XDocument.Parse(body);
            var msg = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value;
            var code = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Code")?.Value;
            if (!string.IsNullOrEmpty(code) || !string.IsNullOrEmpty(msg)) return $"{code}: {msg}";
        }
        catch { }
        return body.Length > 200 ? body[..200] : body;
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] : s;
}
