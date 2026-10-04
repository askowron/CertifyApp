using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Certify.Deployment;

public enum WebServerKind { Apache, Nginx }

/// <summary>
/// Witryna znaleziona w konfiguracji: &lt;VirtualHost&gt; (Apache) / server { } (Nginx),
/// a dla Apache tez "glowny serwer" (DocumentRoot poza VirtualHost, Hostnames puste).
/// </summary>
public sealed record WebServerSite(
    WebServerKind Kind,
    string ConfigFile,
    List<string> Hostnames,
    string? DocumentRoot,
    bool Ssl)
{
    /// <summary>Nazwa uslugi Windows, z ktorej wykryto instalacje (null = znaleziona po katalogu).</summary>
    public string? ServiceName { get; init; }

    public string Display =>
        $"{Kind}: {(Hostnames.Count > 0 ? string.Join(", ", Hostnames) : "(" + Path.GetFileName(ConfigFile) + ")")}"
        + (Ssl ? "  [SSL]" : "") + $"  —  {ConfigFile}";

    public override string ToString() => Display;
}

/// <summary>
/// Wykrywanie instalacji Apache/Nginx (uslugi Windows + typowe katalogi) i odczyt witryn
/// z konfiguracji z rozwinieciem Include/include. Tylko odczyt - nic nie zapisuje.
/// </summary>
public static class WebServerConfigScanner
{
    private const int MaxIncludeDepth = 16;

    // ---------- skan calosci ----------

    /// <summary>Wszystkie witryny ze wszystkich znalezionych instalacji danego typu. Bledy pomijane.</summary>
    public static List<WebServerSite> Scan(WebServerKind kind)
    {
        var result = new List<WebServerSite>();
        foreach (var (config, service) in FindInstallations(kind))
        {
            try
            {
                var sites = kind == WebServerKind.Apache ? ParseApache(config) : ParseNginx(config);
                result.AddRange(sites.Select(s => s with { ServiceName = service }));
            }
            catch { /* nieczytelny config - pomijamy instalacje */ }
        }
        return result;
    }

    /// <summary>Glowne pliki konfiguracji (+ nazwa uslugi, jesli z uslugi). Bez duplikatow.</summary>
    public static List<(string Config, string? Service)> FindInstallations(WebServerKind kind)
    {
        var found = new List<(string, string?)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? config, string? service)
        {
            if (config == null || !File.Exists(config)) return;
            var full = Path.GetFullPath(config);
            if (seen.Add(full)) found.Add((full, service));
        }

        if (OperatingSystem.IsWindows())
        {
            var prefix = kind == WebServerKind.Apache ? "Apache" : "nginx";
            foreach (var svc in WindowsServiceHelper.InstalledServices().Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                var exe = ParseScQcBinary(RunScQc(svc));
                if (exe != null) Add(ConfigForBinary(kind, exe), svc);
            }
            foreach (var dir in CandidateDirs(kind))
                Add(ConfigForBinary(kind, Path.Combine(dir, kind == WebServerKind.Apache ? Path.Combine("bin", "httpd.exe") : "nginx.exe")), null);
        }
        else
        {
            var paths = kind == WebServerKind.Apache
                ? new[] { "/etc/apache2/apache2.conf", "/etc/httpd/conf/httpd.conf" }
                : new[] { "/etc/nginx/nginx.conf", "/usr/local/etc/nginx/nginx.conf" };
            foreach (var p in paths) Add(p, null);
        }
        return found;
    }

    /// <summary>httpd.exe w X\bin -> X\conf\httpd.conf; nginx.exe w X -> X\conf\nginx.conf. Null = nie ten program / brak pliku.</summary>
    public static string? ConfigForBinary(WebServerKind kind, string exePath)
    {
        var name = Path.GetFileName(exePath);
        var dir = Path.GetDirectoryName(exePath);
        if (dir == null) return null;
        string? config = null;
        if (kind == WebServerKind.Apache && name.Equals("httpd.exe", StringComparison.OrdinalIgnoreCase))
        {
            var root = Path.GetDirectoryName(dir);
            if (root != null) config = Path.Combine(root, "conf", "httpd.conf");
        }
        else if (kind == WebServerKind.Nginx && name.Equals("nginx.exe", StringComparison.OrdinalIgnoreCase))
            config = Path.Combine(dir, "conf", "nginx.conf");
        return config != null && File.Exists(config) ? config : null;
    }

    /// <summary>Sciezka exe z wyjscia "sc qc" (pole BINARY_PATH_NAME nie jest lokalizowane).</summary>
    public static string? ParseScQcBinary(string scQcOutput)
    {
        var line = scQcOutput.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase));
        if (line == null) return null;
        var colon = line.IndexOf(':');
        if (colon < 0) return null;
        var value = line[(colon + 1)..].Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? value[1..end] : null;
        }
        // Bez cudzyslowu: do konca ".exe" (sciezka moze zawierac spacje).
        var exe = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? value[..(exe + 4)] : value.Split(' ')[0];
    }

    private static string RunScQc(string service)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            psi.ArgumentList.Add("qc");
            psi.ArgumentList.Add(service);
            using var p = Process.Start(psi);
            if (p == null) return "";
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return output;
        }
        catch { return ""; }
    }

    /// <summary>Typowe katalogi instalacji na Windows (Apache Lounge, XAMPP, WAMP, Laragon, nginx zip).</summary>
    private static IEnumerable<string> CandidateDirs(WebServerKind kind)
    {
        var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        IEnumerable<string> Glob(string parent, string pattern)
        {
            try { return Directory.Exists(parent) ? Directory.GetDirectories(parent, pattern) : []; }
            catch { return []; }
        }

        if (kind == WebServerKind.Apache)
        {
            foreach (var d in Glob(drive, "Apache*")) yield return d;
            foreach (var d in Glob(pf, "Apache*")) yield return d;
            if (!string.IsNullOrEmpty(pf86)) foreach (var d in Glob(pf86, "Apache*")) yield return d;
            // "Apache Software Foundation\Apache2.4"
            foreach (var asf in Glob(pf, "Apache Software Foundation")) foreach (var d in Glob(asf, "Apache*")) yield return d;
            yield return Path.Combine(drive, "xampp", "apache");
            foreach (var d in Glob(Path.Combine(drive, "wamp64", "bin", "apache"), "apache*")) yield return d;
            foreach (var d in Glob(Path.Combine(drive, "wamp", "bin", "apache"), "apache*")) yield return d;
            foreach (var d in Glob(Path.Combine(drive, "laragon", "bin", "apache"), "*")) yield return d;
        }
        else
        {
            foreach (var d in Glob(drive, "nginx*")) yield return d;
            foreach (var d in Glob(pf, "nginx*")) yield return d;
            foreach (var d in Glob(Path.Combine(drive, "tools"), "nginx*")) yield return d;
            foreach (var d in Glob(Path.Combine(drive, "laragon", "bin", "nginx"), "*")) yield return d;
        }
    }

    // ---------- Apache ----------

    /// <summary>
    /// Witryny z httpd.conf + Include/IncludeOptional (maski, katalogi), Define/${VAR}, ServerRoot.
    /// serverRoot null = ServerRoot z configu albo katalog nad conf\.
    /// </summary>
    public static List<WebServerSite> ParseApache(string mainConfig, string? serverRoot = null)
    {
        var state = new ApacheState
        {
            ServerRoot = serverRoot ?? Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(mainConfig))) ?? "."
        };
        var main = new ApacheBlock(Path.GetFullPath(mainConfig));
        ParseApacheFile(Path.GetFullPath(mainConfig), state, main, 0);
        var sites = new List<WebServerSite>();
        if (main.DocumentRoot != null || main.Hostnames.Count > 0)
            sites.Add(main.ToSite(state.ServerRoot));
        sites.AddRange(state.VHosts.Select(v => v.ToSite(state.ServerRoot)));
        return sites;
    }

    private sealed class ApacheState
    {
        public string ServerRoot = ".";
        public readonly Dictionary<string, string> Defines = new(StringComparer.Ordinal);
        public readonly HashSet<string> Visited = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<ApacheBlock> VHosts = new();
    }

    private sealed class ApacheBlock(string file)
    {
        public readonly string File = file;
        public readonly List<string> Hostnames = new();
        public string? DocumentRoot;
        public bool Ssl;

        public WebServerSite ToSite(string serverRoot) => new(
            WebServerKind.Apache, File, Hostnames.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            DocumentRoot == null ? null : ResolvePath(DocumentRoot, serverRoot), Ssl);
    }

    private static void ParseApacheFile(string file, ApacheState st, ApacheBlock main, int depth)
    {
        if (depth > MaxIncludeDepth || !st.Visited.Add(file) || !File.Exists(file)) return;
        ApacheBlock? vhost = null;
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            line = Regex.Replace(line, @"\$\{(\w+)\}", m => st.Defines.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
            var args = SplitArgs(line);
            if (args.Count == 0) continue;
            var dir = args[0];

            if (dir.StartsWith("<VirtualHost", StringComparison.OrdinalIgnoreCase))
            {
                vhost = new ApacheBlock(file) { Ssl = line.Contains(":443") };
                continue;
            }
            if (dir.StartsWith("</VirtualHost", StringComparison.OrdinalIgnoreCase))
            {
                if (vhost != null) st.VHosts.Add(vhost);
                vhost = null;
                continue;
            }
            var target = vhost ?? main;
            switch (dir.ToLowerInvariant())
            {
                case "define" when args.Count >= 3:
                    st.Defines[args[1]] = args[2];
                    break;
                case "serverroot" when args.Count >= 2 && vhost == null:
                    st.ServerRoot = args[1];
                    break;
                case "servername" when args.Count >= 2:
                    target.Hostnames.Insert(0, StripPort(args[1]));
                    break;
                case "serveralias":
                    target.Hostnames.AddRange(args.Skip(1).Select(StripPort));
                    break;
                case "documentroot" when args.Count >= 2:
                    target.DocumentRoot = args[1];
                    break;
                case "sslengine" when args.Count >= 2:
                    target.Ssl |= args[1].Equals("on", StringComparison.OrdinalIgnoreCase);
                    break;
                case "include" or "includeoptional" when args.Count >= 2:
                    foreach (var inc in ExpandInclude(args[1], st.ServerRoot))
                        ParseApacheFile(inc, st, main, depth + 1);
                    break;
            }
        }
    }

    private static string StripPort(string host)
    {
        // "example.com:80" -> "example.com"; IPv6 w nawiasach zostawiamy.
        if (host.StartsWith('[')) return host;
        var i = host.LastIndexOf(':');
        return i > 0 && host[(i + 1)..].All(char.IsDigit) ? host[..i] : host;
    }

    // ---------- Nginx ----------

    /// <summary>
    /// Bloki server { } z nginx.conf + include (maski). Zbiera tylko dyrektywy bezposrednio w server
    /// (root w location ignorowany). Relatywny root wzgledem prefiksu (katalog nad conf\).
    /// </summary>
    public static List<WebServerSite> ParseNginx(string mainConfig)
    {
        var full = Path.GetFullPath(mainConfig);
        var confDir = Path.GetDirectoryName(full)!;
        var prefix = Path.GetDirectoryName(confDir) ?? confDir;
        var tokens = new List<(string Text, string File)>();
        TokenizeNginx(full, confDir, tokens, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);

        var sites = new List<WebServerSite>();
        // stos: dla kazdego otwartego bloku - czy to server (i jego dane)
        var stack = new Stack<NginxServer?>();
        var stmt = new List<string>();
        string stmtFile = full;
        foreach (var (text, file) in tokens)
        {
            if (stmt.Count == 0) stmtFile = file;
            if (text == "{")
            {
                stack.Push(stmt.Count == 1 && stmt[0] == "server" ? new NginxServer(stmtFile) : null);
                stmt.Clear();
            }
            else if (text == "}")
            {
                stmt.Clear();
                if (stack.Count == 0) continue;
                var closed = stack.Pop();
                if (closed != null)
                    sites.Add(new WebServerSite(WebServerKind.Nginx, closed.File,
                        closed.Hostnames.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        closed.Root == null ? null : ResolvePath(closed.Root, prefix), closed.Ssl));
            }
            else if (text == ";")
            {
                if (stmt.Count > 0 && stack.Count > 0 && stack.Peek() is { } srv)
                    srv.Apply(stmt);
                stmt.Clear();
            }
            else stmt.Add(text);
        }
        return sites;
    }

    private sealed class NginxServer(string file)
    {
        public readonly string File = file;
        public readonly List<string> Hostnames = new();
        public string? Root;
        public bool Ssl;

        public void Apply(List<string> s)
        {
            switch (s[0])
            {
                case "server_name":
                    foreach (var n in s.Skip(1))
                    {
                        // "_" / "" = catch-all, "~" = regex - nie sa nazwami hostow.
                        if (n is "_" or "" || n.StartsWith('~')) continue;
                        Hostnames.Add(n.TrimStart('.'));
                    }
                    break;
                case "root" when s.Count >= 2:
                    Root = s[1];
                    break;
                case "listen":
                    Ssl |= s.Skip(1).Any(a => a == "ssl" || a == "443" || a.EndsWith(":443"));
                    break;
                case "ssl_certificate":
                    Ssl = true;
                    break;
            }
        }
    }

    private static void TokenizeNginx(string file, string confDir, List<(string, string)> tokens, HashSet<string> visited, int depth)
    {
        if (depth > MaxIncludeDepth || !visited.Add(file) || !File.Exists(file)) return;
        var text = File.ReadAllText(file);
        var local = new List<string>();
        var sb = new StringBuilder();
        void Flush() { if (sb.Length > 0) { local.Add(sb.ToString()); sb.Clear(); } }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '#') { Flush(); while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c is '"' or '\'')
            {
                Flush();
                var end = text.IndexOf(c, i + 1);
                if (end < 0) end = text.Length;
                local.Add(text[(i + 1)..end]);
                i = end;
                continue;
            }
            if (char.IsWhiteSpace(c)) { Flush(); continue; }
            if (c is '{' or '}' or ';') { Flush(); local.Add(c.ToString()); continue; }
            sb.Append(c);
        }
        Flush();

        // include X; -> tokeny wlaczonych plikow w tym miejscu (relatywnie do katalogu nginx.conf)
        for (var i = 0; i < local.Count; i++)
        {
            var atStmtStart = i == 0 || local[i - 1] is ";" or "{" or "}";
            if (atStmtStart && local[i] == "include" && i + 2 < local.Count && local[i + 2] == ";")
            {
                foreach (var inc in ExpandInclude(local[i + 1], confDir))
                    TokenizeNginx(inc, confDir, tokens, visited, depth + 1);
                i += 2;
                continue;
            }
            tokens.Add((local[i], file));
        }
    }

    // ---------- wspolne ----------

    /// <summary>Plik / katalog (wszystkie pliki rekursywnie) / maska w nazwie lub katalogu. Posortowane jak Apache.</summary>
    private static IEnumerable<string> ExpandInclude(string pattern, string baseDir)
    {
        var path = ResolvePath(pattern, baseDir);
        if (File.Exists(path)) return [path];
        if (Directory.Exists(path))
            return Directory.GetFiles(path, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal);
        var dir = Path.GetDirectoryName(path);
        var mask = Path.GetFileName(path);
        if (dir == null || mask.Length == 0) return [];
        IEnumerable<string> dirs;
        try
        {
            // maska w ostatnim katalogu: sites/*/site.conf
            var dirName = Path.GetFileName(dir);
            var parent = Path.GetDirectoryName(dir);
            dirs = dirName.IndexOfAny(['*', '?']) >= 0 && parent != null && Directory.Exists(parent)
                ? Directory.GetDirectories(parent, dirName)
                : Directory.Exists(dir) ? [dir] : [];
            return dirs.SelectMany(d => Directory.GetFiles(d, mask)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        }
        catch { return []; }
    }

    /// <summary>Sciezka z configu (moze byc relatywna, z '/') -> pelna sciezka lokalna.</summary>
    private static string ResolvePath(string p, string baseDir)
    {
        p = p.Trim().Trim('"');
        if (OperatingSystem.IsWindows()) p = p.Replace('/', '\\');
        try { return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p)); }
        catch { return p; }
    }

    /// <summary>Argumenty dyrektywy Apache: spacje rozdzielaja, cudzyslowy grupuja.</summary>
    private static List<string> SplitArgs(string line)
    {
        var args = new List<string>();
        foreach (Match m in Regex.Matches(line, "\"([^\"]*)\"|(\\S+)"))
            args.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        return args;
    }
}
