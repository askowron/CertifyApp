using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Certify.ACME;

/// <summary>
/// Automatyczny dns-01 przez Cloudflare API: tworzy rekord TXT
/// _acme-challenge.domena, czeka na propagacje (DoH), sprzata po sobie.
/// Token API (Bearer) potrzebuje: Zone DNS Edit + Zone Read (lista stref).
/// </summary>
public class CloudflareDns01Handler : IChallengeHandler
{
    private readonly string _apiToken;
    private readonly int _propagationTimeoutSec;
    private readonly HttpClient _http;
    // Klucz domena|token: example.com i *.example.com maja dwa challenge'e
    // na tej samej nazwie - klucz po samej domenie gubil jeden rekord (osierocony TXT).
    private readonly Dictionary<string, (string ZoneId, string RecordId)> _created = new();

    private static string CreatedKey(string domain, string token) => $"{domain}|{token}";

    public CloudflareDns01Handler(string apiToken, int propagationTimeoutSec = 90, HttpClient? http = null)
    {
        _apiToken = apiToken;
        _propagationTimeoutSec = Math.Clamp(propagationTimeoutSec, 10, 1800);
        _http = http ?? new HttpClient();
    }

    public async Task PrepareAsync(string domain, string token, string keyAuthz, IProgress<string>? log, CancellationToken ct)
    {
        // keyAuthz = wartosc TXT (digest dla raw flow, DnsTxt z Certes).
        var recordName = BuildRecordName(domain);
        log?.Report($"[DNS-01/CF] Szukam strefy dla {domain} ...");
        var zoneId = await FindZoneIdAsync(domain, ct)
            ?? throw new InvalidOperationException(Certify.Core.Localization.UIStrings.T("Acme_Err_NoZone", domain));
        log?.Report($"[DNS-01/CF] Tworzę TXT {recordName} ...");
        var recordId = await CreateTxtAsync(zoneId, recordName, keyAuthz, ct);
        lock (_created) _created[CreatedKey(domain, token)] = (zoneId, recordId);
        await WaitPropagationAsync(recordName, keyAuthz, log, ct);
    }

    public async Task CleanupAsync(string domain, string token, IProgress<string>? log, CancellationToken ct)
    {
        (string ZoneId, string RecordId) entry;
        lock (_created)
        {
            var key = CreatedKey(domain, token);
            if (!_created.TryGetValue(key, out entry)) return;
            _created.Remove(key);
        }
        try
        {
            await DeleteRecordAsync(entry.ZoneId, entry.RecordId, ct);
            log?.Report($"[DNS-01/CF] Usunięto TXT _acme-challenge.{domain}");
        }
        catch (Exception ex) { log?.Report($"[DNS-01/CF] Cleanup warn: {ex.Message}"); }
    }

    /// <summary>Usuwa WSZYSTKIE rekordy TXT _acme-challenge.domena (osierocone po manualach/padach).</summary>
    public async Task<int> CleanupStaleAsync(string domain, IProgress<string>? log, CancellationToken ct)
    {
        var recordName = BuildRecordName(domain);
        var zoneId = await FindZoneIdAsync(domain, ct)
            ?? throw new InvalidOperationException(Certify.Core.Localization.UIStrings.T("Acme_Err_NoZone", domain));
        var records = await ListTxtAsync(zoneId, recordName, ct);
        foreach (var (id, content) in records)
        {
            ct.ThrowIfCancellationRequested();
            await DeleteRecordAsync(zoneId, id, ct);
            log?.Report($"[DNS-01/CF] Usunięto osierocony TXT {recordName} ({Short(content)})");
        }
        return records.Count;
    }

    private async Task<List<(string Id, string Content)>> ListTxtAsync(string zoneId, string recordName, CancellationToken ct)
    {
        using var req = Authed(new HttpRequestMessage(HttpMethod.Get,
            $"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records?type=TXT&name={Uri.EscapeDataString(recordName)}&per_page=100"));
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        EnsureCloudflareSuccess(body, (int)resp.StatusCode);
        return ParseTxtRecords(body);
    }

    private async Task<string?> FindZoneIdAsync(string domain, CancellationToken ct)
    {
        foreach (var candidate in CandidateZones(domain))
        {
            using var req = Authed(new HttpRequestMessage(HttpMethod.Get,
                $"https://api.cloudflare.com/client/v4/zones?name={Uri.EscapeDataString(candidate)}&status=active"));
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var id = FirstResultId(body);
            if (id != null) return id;
        }
        return null;
    }

    private async Task<string> CreateTxtAsync(string zoneId, string name, string content, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = "TXT",
            name,
            content,
            ttl = 120,
            comment = "CertifyApp ACME dns-01 (auto-delete)"
        });
        using var req = Authed(new HttpRequestMessage(HttpMethod.Post,
            $"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records")
        { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        EnsureCloudflareSuccess(body, (int)resp.StatusCode);
        return ResultId(body) ?? throw new InvalidOperationException(Certify.Core.Localization.UIStrings.T("Acme_Err_NoRecordId"));
    }

    private async Task DeleteRecordAsync(string zoneId, string recordId, CancellationToken ct)
    {
        using var req = Authed(new HttpRequestMessage(HttpMethod.Delete,
            $"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records/{recordId}"));
        using var resp = await _http.SendAsync(req, ct);
        EnsureCloudflareSuccess(await resp.Content.ReadAsStringAsync(ct), (int)resp.StatusCode);
    }

    private async Task WaitPropagationAsync(string recordName, string expectedTxt, IProgress<string>? log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_propagationTimeoutSec);
        log?.Report($"[DNS-01/CF] Czekam na propagację TXT (max {_propagationTimeoutSec}s) ...");
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await DohTxtVisibleAsync(recordName, expectedTxt, ct))
                {
                    log?.Report("[DNS-01/CF] TXT widoczny w DNS.");
                    return;
                }
            }
            catch (Exception ex) { log?.Report($"[DNS-01/CF] DoH check warn: {ex.Message}"); }
            await Task.Delay(5000, ct);
        }
        log?.Report("[DNS-01/CF] UWAGA: TXT niepotwierdzony w czasie - próbuję walidację mimo to.");
    }

    private async Task<bool> DohTxtVisibleAsync(string recordName, string expectedTxt, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(recordName)}&type=TXT");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return DohAnswerMatches(await resp.Content.ReadAsStringAsync(ct), expectedTxt);
    }

    private HttpRequestMessage Authed(HttpRequestMessage req)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
        return req;
    }

    // ---- czyste funkcje (testowalne) ----

    public static string BuildRecordName(string domain) =>
        "_acme-challenge." + domain.Trim().TrimEnd('.');

    /// <summary>Kandydaci na strefe: od pelnej nazwy w gore (bez _acme-challenge).</summary>
    public static List<string> CandidateZones(string domain)
    {
        var labels = domain.Trim().TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<string>();
        for (var i = 0; i < labels.Length - 1; i++)
            list.Add(string.Join(".", labels.Skip(i)));
        return list;
    }

    public static bool DohAnswerMatches(string dohJson, string expectedTxt)
    {
        try
        {
            using var doc = JsonDocument.Parse(dohJson);
            if (!doc.RootElement.TryGetProperty("Answer", out var answers)) return false;
            foreach (var a in answers.EnumerateArray())
            {
                if (a.TryGetProperty("type", out var t) && t.GetInt32() == 16 &&
                    a.TryGetProperty("data", out var d))
                {
                    var txt = (d.GetString() ?? "").Trim().Trim('"');
                    if (txt == expectedTxt) return true;
                }
            }
        }
        catch { }
        return false;
    }

    public static string? FirstResultId(string cfJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(cfJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var s) && s.GetBoolean() &&
                root.TryGetProperty("result", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var first = arr.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("id", out var id))
                    return id.GetString();
            }
        }
        catch { }
        return null;
    }

    /// <summary>Id z result: array (list) albo obiekt (create).</summary>
    public static string? ResultId(string cfJson)
    {
        var fromArray = FirstResultId(cfJson);
        if (fromArray != null) return fromArray;
        try
        {
            using var doc = JsonDocument.Parse(cfJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var s) && s.GetBoolean() &&
                root.TryGetProperty("result", out var obj) && obj.ValueKind == JsonValueKind.Object &&
                obj.TryGetProperty("id", out var id))
                    return id.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>Lista (id, content) rekordow TXT z odpowiedzi dns_records.</summary>
    public static List<(string Id, string Content)> ParseTxtRecords(string cfJson)
    {
        var list = new List<(string, string)>();
        try
        {
            using var doc = JsonDocument.Parse(cfJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var s) || !s.GetBoolean()) return list;
            if (!root.TryGetProperty("result", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var r in arr.EnumerateArray())
            {
                if (r.TryGetProperty("id", out var id) && r.TryGetProperty("content", out var c))
                    list.Add((id.GetString() ?? "", c.GetString() ?? ""));
            }
        }
        catch { }
        return list;
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] : s;


    public static void EnsureCloudflareSuccess(string body, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (status is >= 200 and < 300 &&
                doc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean()) return;
            var errs = doc.RootElement.TryGetProperty("errors", out var e) ? e.ToString() : body;
            throw new InvalidOperationException($"Cloudflare API ({status}): {(errs.Length > 200 ? errs[..200] : errs)}");
        }
        catch (InvalidOperationException) { throw; }
        catch { throw new InvalidOperationException($"Cloudflare API ({status}): {body}"); }
    }
}
