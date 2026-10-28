using System.Security.Principal;
using System.Text.Json;
using Certify.Core.Models;
using Certify.Core.Services;
using Certify.Deployment;

namespace Certify.Service;

public sealed record RequestArgs(string Host, string Site, string? Template, string? Webroot);

/// <summary>
/// Polecenie: Certify.Service.exe --request &lt;host&gt; --site &lt;strona IIS&gt; [--template &lt;domena&gt;] [--webroot &lt;sciezka&gt;].
/// Bez okien. Ostatnia linia stdout to zawsze CERTIFYAPP-REQUEST {json}. Kody: 0 ok, 1 blad,
/// 2 zle argumenty, 3 zajete, 4 brak admina, 5 wystawiony, ale wdrozenie nieudane.
/// </summary>
public static class RequestCli
{
    private const string Usage =
        "Uzycie: Certify.Service.exe --request <host> --site <strona IIS> [--template <domena>] [--webroot <sciezka>]";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static (RequestArgs? Args, string? Error) Parse(string[] args)
    {
        if (args.Length < 2 || args[0] != "--request" || args[1].StartsWith("--"))
            return (null, "Brak hosta po --request.");
        var host = args[1].Trim().ToLowerInvariant();
        string? site = null, template = null, webroot = null;
        for (var i = 2; i < args.Length; i++)
        {
            var opt = args[i];
            if (opt is not ("--site" or "--template" or "--webroot"))
                return (null, $"Nieznana opcja: {opt}");
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
                return (null, $"Brak wartosci dla {opt}.");
            var value = args[++i];
            switch (opt)
            {
                case "--site": site = value; break;
                case "--template": template = value; break;
                default: webroot = value; break;
            }
        }
        if (string.IsNullOrWhiteSpace(host)) return (null, "Brak hosta po --request.");
        if (string.IsNullOrWhiteSpace(site)) return (null, "Brak wymaganej opcji --site.");
        return (new RequestArgs(host, site, template, webroot), null);
    }

    public static string ResultLine(RequestOutcome o) =>
        FormatLine(o.Result.ToString().ToLowerInvariant(), o.Host, o.Expiry, o.Thumbprint, o.Message);

    private static string FormatLine(string result, string host, DateTime? expiry, string? thumbprint, string message)
    {
        var payload = new
        {
            result,
            host,
            expiry = expiry?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            thumbprint,
            message
        };
        return "CERTIFYAPP-REQUEST " + JsonSerializer.Serialize(payload, Json);
    }

    private static int Fail(TextWriter stdout, TextWriter stderr, string result, string host, string message, int code)
    {
        stderr.WriteLine(message);
        stdout.WriteLine(FormatLine(result, host, null, null, message));
        return code;
    }

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var (parsed, error) = Parse(args);
        if (parsed == null)
        {
            stderr.WriteLine(error);
            stderr.WriteLine(Usage);
            var host = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "";
            stdout.WriteLine(FormatLine("badargs", host, null, null, error ?? "Zle argumenty."));
            return 2;
        }

        try
        {
            if (!IsAdministrator())
                return Fail(stdout, stderr, "notadmin", parsed.Host, "--request wymaga uprawnień administratora.", 4);

            var settings = new SettingsStore(new AppSettings()).Load();
            var (store, ca, renewal) = RenewalHost.CreateParts(settings);

            var webroot = parsed.Webroot ?? new IisDeployer().GetSitePhysicalPath(parsed.Site);
            if (string.IsNullOrWhiteSpace(webroot))
                return Fail(stdout, stderr, "badargs", parsed.Host, $"Strona IIS {parsed.Site} nie istnieje.", 2);

            var log = new TeeProgress(stdout, new FileLog(settings.LogsDirectory, "request"));
            var runner = new CertificateRequestRunner(store, ca, renewal);
            var outcome = await runner.RunAsync(parsed.Host, parsed.Site, webroot, parsed.Template,
                TimeSpan.FromMinutes(30), log, CancellationToken.None);
            stdout.WriteLine(ResultLine(outcome));
            return outcome.ExitCode;
        }
        catch (Exception ex)
        {
            return Fail(stdout, stderr, "failed", parsed.Host, ex.Message, 1);
        }
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Kazda linia postepu na stdout i do pliku logu (synchronicznie, kolejnosc zachowana).</summary>
    private sealed class TeeProgress : IProgress<string>
    {
        private readonly TextWriter _out;
        private readonly IProgress<string> _file;
        private readonly object _gate = new();

        public TeeProgress(TextWriter @out, IProgress<string> file) { _out = @out; _file = file; }

        public void Report(string value)
        {
            lock (_gate)
            {
                _out.WriteLine(value);
                _out.Flush();
                _file.Report(value);
            }
        }
    }
}
