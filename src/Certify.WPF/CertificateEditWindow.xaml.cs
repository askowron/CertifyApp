using System.IO;
using System.Windows;
using System.Windows.Controls;
using Certify.Core.Models;
using Certify.Core.Services;
using Certify.Deployment;
using Microsoft.Win32;

namespace Certify.WPF;

public partial class CertificateEditWindow : Window
{
    private readonly ManagedCertificate _cert;
    private readonly AppSettings _settings;
    private readonly List<IDeploymentTarget> _deployers;
    // Kopia robocza zadan - zapisywana do _cert dopiero przy Save (Anuluj odrzuca).
    private List<CertificateTaskDefinition> _tasksEdit = new();

    public CertificateEditWindow(ManagedCertificate cert, AppSettings settings, IEnumerable<IDeploymentTarget> deployers)
    {
        InitializeComponent();
        _cert = cert;
        _settings = settings;
        _deployers = deployers.ToList();

        CaBox.DisplayMemberPath = "Display";
        CaBox.SelectedValuePath = "Value";
        CaBox.ItemsSource = CertificateAuthorityCatalog.All.ToList();
        CaBox.SelectedValue = _cert.CertificateAuthority;
        ChallengeBox.ItemsSource = Enum.GetValues<ChallengeType>();

        NameBox.Text = _cert.Name;
        EmailBox.Text = _cert.EmailAddress;
        EabKeyIdBox.Text = _cert.EabKeyId ?? "";
        EabHmacBox.Password = _cert.EabHmacKey ?? "";
        CustomUrlBox.Text = _cert.CustomAcmeDirectoryUrl ?? "";
        UpdateCaVisibility();
        ChallengeBox.SelectedItem = _cert.ChallengeType;
        DomainsBox.Text = string.Join(Environment.NewLine, _cert.Domains);
        WebRootBox.Text = _cert.ChallengeConfig.HttpChallengeRoot ?? "";

        DnsProviderBox.ItemsSource = new List<string> { "Manual", "Cloudflare", "Route53" };
        var dp = _cert.ChallengeConfig.DnsProvider;
        DnsProviderBox.SelectedItem = dp == "Cloudflare" ? "Cloudflare" : dp == "Route53" ? "Route53" : "Manual";
        if (_cert.ChallengeConfig.DnsProviderCredentials.TryGetValue("CloudflareApiToken", out var tok))
            CloudflareTokenBox.Password = tok;
        if (_cert.ChallengeConfig.DnsProviderCredentials.TryGetValue("AwsAccessKeyId", out var ak))
            AwsKeyBox.Text = ak;
        if (_cert.ChallengeConfig.DnsProviderCredentials.TryGetValue("AwsSecretAccessKey", out var sk))
            AwsSecretBox.Password = sk;
        DnsPropBox.Text = _cert.ChallengeConfig.DnsPropagationSeconds.ToString();
        UpdateDnsVisibility();
        AutoRenewCheck.IsChecked = _cert.RenewalMode == RenewalMode.Auto;
        RenewDaysBox.Value = _cert.RenewalDaysBeforeExpiry;

        ChkIIS.IsChecked = _cert.DeploymentTargets.Any(d => d.TargetType == DeploymentTargetType.IIS);
        ChkApache.IsChecked = _cert.DeploymentTargets.Any(d => d.TargetType == DeploymentTargetType.Apache);
        ChkNginx.IsChecked = _cert.DeploymentTargets.Any(d => d.TargetType == DeploymentTargetType.Nginx);

        var iisTarget = _cert.DeploymentTargets.FirstOrDefault(d => d.TargetType == DeploymentTargetType.IIS);
        IisSiteBox.Text = iisTarget?.SiteId ?? "";
        var fileTarget = _cert.DeploymentTargets.FirstOrDefault(d => d.TargetType == DeploymentTargetType.Apache || d.TargetType == DeploymentTargetType.Nginx);
        ConfigPathBox.Text = fileTarget?.ConfigPath ?? "";
        // "apache2"/"nginx" to dawne sztywne wartosci domyslne - pokazujemy jako puste (= wykryj).
        ServiceBox.Text = fileTarget?.ServiceName is { } svc && svc is not ("apache2" or "nginx") ? svc : "";
        UpdateDeployVisibility();

        // deep copy zadan (System.Text.Json roundtrip)
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_cert.Tasks);
            _tasksEdit = System.Text.Json.JsonSerializer.Deserialize<List<CertificateTaskDefinition>>(json) ?? new();
        }
        catch { _tasksEdit = new(); }
        RefreshTaskLists();

        Loaded += async (_, _) => await RefreshWebSitesAsync();
        // try discover IIS sites (z diagnostyka - wczesniej puste przy braku uprawnien)
        Loaded += async (_, _) =>
        {
            try
            {
                var iis = _deployers.FirstOrDefault(d => d.TargetType == DeploymentTargetType.IIS);
                if (iis is Certify.Deployment.IisDeployer iisDepl)
                {
                    var (sites, error) = await iisDepl.DiscoverSitesDetailedAsync();
                    IisSiteBox.ItemsSource = sites;
                    _iisSitesLoaded = true;
                    IisStatusText.Text = sites.Count > 0
                        ? WpfLocalizer.T("Edit_Iis_Found", string.Join(", ", sites))
                        : WpfLocalizer.T("Edit_Iis_None", error ?? "?");
                }
                else if (iis != null)
                {
                    var sites = await iis.DiscoverSitesAsync();
                    IisSiteBox.ItemsSource = sites;
                    _iisSitesLoaded = true;
                }
            }
            catch (Exception ex)
            {
                IisStatusText.Text = WpfLocalizer.T("Edit_Iis_None", ex.Message);
            }
        };
    }

    /// <summary>IIS: pola site'u; Apache/Nginx: config + usluga. Niezaznaczone cele - ukryte.</summary>
    private void UpdateDeployVisibility()
    {
        var iis = ChkIIS.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        var file = ChkApache.IsChecked == true || ChkNginx.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        IisSiteLabel.Visibility = IisSiteRow.Visibility = IisStatusText.Visibility = iis;
        ConfigPathLabel.Visibility = ConfigPathRow.Visibility = ServiceLabel.Visibility = ServiceBox.Visibility = file;
        WebSiteLabel.Visibility = WebSiteBox.Visibility = WebStatusText.Visibility = file;
    }

    private async void DeployTarget_Click(object sender, RoutedEventArgs e)
    {
        UpdateDeployVisibility();
        await RefreshWebSitesAsync();
    }

    // WebRoot ustawiony automatycznie z poprzednio wybranego site'u - tylko taki nadpisujemy.
    private string? _autoWebRoot;
    // false do czasu zaladowania listy site'ow - wybor przy otwarciu okna nie dopisuje domen.
    private bool _iisSitesLoaded;

    /// <summary>
    /// Wybor site'u z listy = automatyczny "Pobierz z IIS": hostnames -> domeny,
    /// sciezka fizyczna -> WebRoot (o ile pole puste albo ustawione wczesniej automatycznie).
    /// Wynik w IisStatusText zamiast MessageBox.
    /// </summary>
    private async void IisSiteBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IisSiteBox.SelectedItem is not string site || string.IsNullOrWhiteSpace(site)) return;
        if (_deployers.FirstOrDefault(d => d.TargetType == DeploymentTargetType.IIS) is not { } iis) return;

        TrySetAutoWebRoot((iis as Certify.Deployment.IisDeployer)?.GetSitePhysicalPath(site));
        if (!_iisSitesLoaded) return;

        List<string> hosts;
        try { hosts = await iis.DiscoverSiteBindingsAsync(site); }
        catch (Exception ex) { IisStatusText.Text = WpfLocalizer.T("Edit_Msg_BindErr", ex.Message); return; }
        // uzytkownik zdazyl wybrac inny site
        if (!Equals(IisSiteBox.SelectedItem, site)) return;
        if (hosts.Count == 0) { IisStatusText.Text = WpfLocalizer.T("Edit_Msg_NoBind", site); return; }
        var added = AppendDomains(hosts);
        IisStatusText.Text = WpfLocalizer.T("Edit_Msg_Imported", hosts.Count, site, added);
    }

    /// <summary>WebRoot = path, o ile pole puste albo ustawione wczesniej automatycznie (recznej sciezki nie ruszamy).</summary>
    private void TrySetAutoWebRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var current = WebRootBox.Text.Trim();
        var isAuto = current.Length == 0
            || (_autoWebRoot != null && string.Equals(current.TrimEnd('\\'), _autoWebRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (!isAuto) return;
        WebRootBox.Text = path;
        _autoWebRoot = path;
    }

    // Wyniki skanu Apache/Nginx (cache per typ - ponowne zaznaczenie checkboxa nie skanuje od nowa).
    private readonly Dictionary<WebServerKind, List<WebServerSite>> _webScans = new();
    private bool _webScanning;

    /// <summary>
    /// Skan konfiguracji dla zaznaczonych Apache/Nginx (w tle - sc.exe + odczyt plikow).
    /// Lista "Witryna" pokazuje witryny tylko zaznaczonych typow.
    /// </summary>
    private async Task RefreshWebSitesAsync()
    {
        var kinds = new List<WebServerKind>();
        if (ChkApache.IsChecked == true) kinds.Add(WebServerKind.Apache);
        if (ChkNginx.IsChecked == true) kinds.Add(WebServerKind.Nginx);
        if (kinds.Count == 0 || _webScanning) return;
        var missing = kinds.Where(k => !_webScans.ContainsKey(k)).ToList();
        if (missing.Count > 0)
        {
            _webScanning = true;
            WebStatusText.Text = WpfLocalizer.T("Edit_Web_Scanning");
            try
            {
                foreach (var k in missing)
                    _webScans[k] = await Task.Run(() => WebServerConfigScanner.Scan(k));
            }
            catch (Exception ex) { WebStatusText.Text = ex.Message; return; }
            finally { _webScanning = false; }
        }
        // Typy mogly sie zmienic w trakcie skanu.
        kinds = kinds.Where(k => (k == WebServerKind.Apache ? ChkApache : ChkNginx).IsChecked == true).ToList();
        var sites = kinds.SelectMany(k => _webScans.TryGetValue(k, out var s) ? s : []).ToList();
        _webSitesLoading = true;
        WebSiteBox.ItemsSource = sites;
        // Pokaz witryne z zapisanego configu (bez importu - to tylko podglad stanu).
        var cfg = ConfigPathBox.Text.Trim();
        WebSiteBox.SelectedItem = cfg.Length == 0 ? null
            : sites.FirstOrDefault(s => string.Equals(s.ConfigFile, cfg, StringComparison.OrdinalIgnoreCase));
        _webSitesLoading = false;
        var names = string.Join("/", kinds);
        WebStatusText.Text = sites.Count > 0
            ? WpfLocalizer.T("Edit_Web_Found", sites.Count, names)
            : WpfLocalizer.T("Edit_Web_None", names);
    }

    private bool _webSitesLoading;

    /// <summary>
    /// Wybor witryny Apache/Nginx: ConfigPath = plik witryny, hostnames -> domeny, DocumentRoot/root -> WebRoot,
    /// usluga (jesli wykryta z uslugi i pole puste). Ostrzezenie, gdy plik ma kilka witryn SSL -
    /// deployer podmienia dyrektywy certyfikatu w calym pliku.
    /// </summary>
    private void WebSiteBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_webSitesLoading || WebSiteBox.SelectedItem is not WebServerSite site) return;
        ConfigPathBox.Text = site.ConfigFile;
        TrySetAutoWebRoot(site.DocumentRoot);
        if (string.IsNullOrWhiteSpace(ServiceBox.Text) && site.ServiceName != null) ServiceBox.Text = site.ServiceName;

        // Wildcard nie przejdzie http-01 - przy http-01 nie dopisujemy; localhost nigdy.
        var http01 = ChallengeBox.SelectedItem is ChallengeType ct && ct == ChallengeType.Http01;
        var hosts = site.Hostnames
            .Where(h => !h.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            .Where(h => !(http01 && h.Contains('*')))
            .ToList();
        var lines = new List<string>();
        if (hosts.Count > 0)
            lines.Add(WpfLocalizer.T("Edit_Msg_Imported", hosts.Count, site.Hostnames.FirstOrDefault() ?? site.ConfigFile, AppendDomains(hosts)));
        var sslInFile = (WebSiteBox.ItemsSource as IEnumerable<WebServerSite> ?? [])
            .Count(s => s.Ssl && string.Equals(s.ConfigFile, site.ConfigFile, StringComparison.OrdinalIgnoreCase));
        if (sslInFile > 1)
            lines.Add(WpfLocalizer.T("Edit_Web_Shared", Path.GetFileName(site.ConfigFile), sslInFile));
        WebStatusText.Text = string.Join("\n", lines);
    }

    /// <summary>Dopisuje brakujace hostnames do pola domen; zwraca liczbe dopisanych.</summary>
    private int AppendDomains(IEnumerable<string> hosts)
    {
        var current = DomainsBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var added = 0;
        foreach (var h in hosts)
        {
            if (!current.Any(c => c.Equals(h, StringComparison.OrdinalIgnoreCase)))
            { current.Add(h); added++; }
        }
        DomainsBox.Text = string.Join(Environment.NewLine, current);
        return added;
    }

    /// <summary>
    /// Hostnames z bindingow site'u -> lista domen, sciezka fizyczna site'u -> WebRoot
    /// (zly WebRoot = 404 przy http-01, ZeroSSL wisi wtedy w "processing").
    /// </summary>
    private async void ImportFromIis_Click(object sender, RoutedEventArgs e)
    {
        var site = IisSiteBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(site)) { MessageBox.Show(WpfLocalizer.T("Edit_Msg_SiteReq")); return; }
        var iis = _deployers.FirstOrDefault(d => d.TargetType == DeploymentTargetType.IIS);
        if (iis == null) { MessageBox.Show(WpfLocalizer.T("Edit_Msg_NoIisDepl")); return; }
        List<string> hosts;
        try { hosts = await iis.DiscoverSiteBindingsAsync(site); }
        catch (Exception ex) { MessageBox.Show(WpfLocalizer.T("Edit_Msg_BindErr", ex.Message)); return; }
        var physicalPath = (iis as Certify.Deployment.IisDeployer)?.GetSitePhysicalPath(site);
        var webrootMsg = "";
        if (physicalPath != null && !string.Equals(WebRootBox.Text.Trim().TrimEnd('\\'), physicalPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            WebRootBox.Text = physicalPath;
            _autoWebRoot = physicalPath;
            webrootMsg = "\n\n" + WpfLocalizer.T("Edit_Msg_WebrootSet", site, physicalPath);
        }
        if (hosts.Count == 0)
        {
            MessageBox.Show(WpfLocalizer.T("Edit_Msg_NoBind", site) + webrootMsg);
            return;
        }
        var added = AppendDomains(hosts);
        MessageBox.Show(WpfLocalizer.T("Edit_Msg_Imported", hosts.Count, site, added) + webrootMsg);
    }

    private void BrowseWebRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = WpfLocalizer.T("Edit_Msg_WebrootTitle") };
        if (dlg.ShowDialog(this) == true) WebRootBox.Text = dlg.FolderName;
    }

    private void BrowseConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = WpfLocalizer.T("Edit_Msg_ConfigTitle"), Filter = "Config|*.conf;*.config;*.*" };
        if (dlg.ShowDialog(this) == true) ConfigPathBox.Text = dlg.FileName;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// Buduje ChallengeConfig z formularza DNS jako KOPIE (nie rusza _cert - wczesniej
    /// "Wyczysc TXT" albo nieudany Zapisz zmienialy model mimo Anuluj). Null = blad.
    /// </summary>
    private ChallengeConfig? ReadChallengeForm(bool showErrors)
    {
        ChallengeConfig? Fail(string msg) { if (showErrors) MessageBox.Show(this, msg); return null; }
        var src = _cert.ChallengeConfig;
        var cfg = new ChallengeConfig
        {
            HttpChallengeRootPaths = new Dictionary<string, string>(src.HttpChallengeRootPaths),
            HttpChallengeRoot = string.IsNullOrWhiteSpace(WebRootBox.Text) ? null : WebRootBox.Text.Trim(),
            DnsProviderCredentials = new Dictionary<string, string>(src.DnsProviderCredentials),
            DnsPropagationSeconds = src.DnsPropagationSeconds
        };
        var dnsProvider = DnsProviderBox.SelectedItem?.ToString() ?? "Manual";
        cfg.DnsProvider = dnsProvider;
        if (dnsProvider == "Cloudflare")
        {
            if (string.IsNullOrWhiteSpace(CloudflareTokenBox.Password)) return Fail("Cloudflare wymaga API Token.");
            cfg.DnsProviderCredentials["CloudflareApiToken"] = CloudflareTokenBox.Password.Trim();
        }
        if (dnsProvider == "Route53")
        {
            if (string.IsNullOrWhiteSpace(AwsKeyBox.Text) || string.IsNullOrWhiteSpace(AwsSecretBox.Password))
                return Fail("Route53 wymaga AWS Key ID + Secret.");
            cfg.DnsProviderCredentials["AwsAccessKeyId"] = AwsKeyBox.Text.Trim();
            cfg.DnsProviderCredentials["AwsSecretAccessKey"] = AwsSecretBox.Password.Trim();
        }
        if (int.TryParse(DnsPropBox.Text, out var ps) && ps >= 10 && ps <= 1800)
            cfg.DnsPropagationSeconds = ps;
        return cfg;
    }

    private async void CleanupTxt_Click(object sender, RoutedEventArgs e)
    {
        var challengeConfig = ReadChallengeForm(showErrors: true);
        if (challengeConfig == null) return;
        // Domeny z formularza (niezapisane tez dzialaja).
        var domains = DomainsBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (domains.Count == 0) { MessageBox.Show(this, WpfLocalizer.T("Edit_Msg_NoDomains")); return; }
        var btn = (System.Windows.Controls.Button)sender;
        btn.IsEnabled = false;
        try
        {
            var tmp = new ManagedCertificate
            {
                Domains = domains,
                ChallengeType = ChallengeType.Dns01,
                ChallengeConfig = challengeConfig
            };
            var results = await Certify.ACME.DnsJanitor.CleanupForCertAsync(tmp, null, CancellationToken.None);
            if (results.Count == 0) { MessageBox.Show(this, WpfLocalizer.T("Edit_Msg_NoApiProvider")); return; }
            var lines = results.Select(r => r.Error != null ? WpfLocalizer.T("Edit_Msg_JanitorErr", r.Domain, r.Error) : WpfLocalizer.T("Edit_Msg_JanitorOk", r.Domain, r.Deleted));
            MessageBox.Show(this, string.Join("\n", lines), WpfLocalizer.T("Edit_Title_Clean"));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, WpfLocalizer.T("Title_Error"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { btn.IsEnabled = true; }
    }

    private void ChallengeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDnsVisibility();

    private void UpdateDnsVisibility()
    {
        if (DnsCard == null || ChallengeBox.SelectedItem == null) return;
        DnsCard.Visibility = (ChallengeType)ChallengeBox.SelectedItem == ChallengeType.Dns01
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateDnsTokenVisibility();
    }

    private void DnsProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDnsTokenVisibility();

    private void UpdateDnsTokenVisibility()
    {
        if (CloudflareTokenBox == null || DnsProviderBox.SelectedItem == null) return;
        var sel = DnsProviderBox.SelectedItem.ToString();
        CloudflareTokenBox.IsEnabled = sel == "Cloudflare";
        Route53Group.Visibility = sel == "Route53" ? Visibility.Visible : Visibility.Collapsed;
        DnsPropBox.IsEnabled = sel != "Manual";
    }

    private CertificateAuthority SelectedCa =>
        CaBox.SelectedValue is CertificateAuthority ca ? ca : CertificateAuthority.LetsEncrypt;

    private void CaBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCaVisibility();

    private void UpdateCaVisibility()
    {
        if (CaSettingsCard == null) return;
        var ca = SelectedCa;
        var needsEab = CertificateAuthorityCatalog.RequiresEab(ca);
        var isCustom = ca == CertificateAuthority.CustomAcme;
        CaSettingsCard.Visibility = (needsEab || isCustom) ? Visibility.Visible : Visibility.Collapsed;
        EabGroup.Visibility = needsEab ? Visibility.Visible : Visibility.Collapsed;
        CustomCaGroup.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        if (needsEab)
            EabHelpText.Text = CertificateAuthorityCatalog.Get(ca).EabHelp ?? "";
    }

    private void RefreshTaskLists()
    {
        DeploymentTasksList.ItemsSource = _tasksEdit.Where(t => t.Stage == CertificateTaskStage.Deployment).ToList();
        PreRequestTasksList.ItemsSource = _tasksEdit.Where(t => t.Stage == CertificateTaskStage.PreRequest).ToList();
    }

    private void AddTask(CertificateTaskStage stage)
    {
        var def = new CertificateTaskDefinition
        {
            Name = stage == CertificateTaskStage.PreRequest ? "Nowe zadanie pre-request" : "Nowe zadanie deployment",
            Stage = stage,
            Trigger = CertificateTaskTrigger.OnSuccess
        };
        var dlg = new TaskEditWindow(def) { Owner = this };
        if (dlg.ShowDialog() == true) { _tasksEdit.Add(def); RefreshTaskLists(); }
    }

    private void EditTask(ListBox list)
    {
        if (list.SelectedItem is not CertificateTaskDefinition def)
        { MessageBox.Show(WpfLocalizer.T("Edit_Msg_SelTask")); return; }
        var dlg = new TaskEditWindow(def) { Owner = this };
        if (dlg.ShowDialog() == true) RefreshTaskLists();
    }

    private void DelTask(ListBox list)
    {
        if (list.SelectedItem is not CertificateTaskDefinition def) return;
        if (MessageBox.Show(WpfLocalizer.T("Edit_Msg_DelTask", def.Name), WpfLocalizer.T("Dlg_ConfirmTitle"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _tasksEdit.RemoveAll(t => t.Id == def.Id);
        RefreshTaskLists();
    }

    private void AddDepTask_Click(object sender, RoutedEventArgs e) => AddTask(CertificateTaskStage.Deployment);
    private void EditDepTask_Click(object sender, RoutedEventArgs e) => EditTask(DeploymentTasksList);
    private void DelDepTask_Click(object sender, RoutedEventArgs e) => DelTask(DeploymentTasksList);
    private void AddPreTask_Click(object sender, RoutedEventArgs e) => AddTask(CertificateTaskStage.PreRequest);
    private void EditPreTask_Click(object sender, RoutedEventArgs e) => EditTask(PreRequestTasksList);
    private void DelPreTask_Click(object sender, RoutedEventArgs e) => DelTask(PreRequestTasksList);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Najpierw CALA walidacja, potem zapis do modelu. Wczesniej model byl zmieniany
        // przed kolejnymi walidacjami (np. czyszczenie DeploymentTargets przed sprawdzeniem
        // IIS site) i Anuluj zostawial w pamieci czesciowo zmieniony certyfikat.
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { MessageBox.Show(WpfLocalizer.T("Edit_Msg_NameReq")); return; }
        var domains = DomainsBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().Trim(',').Trim()).Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!domains.Any()) { MessageBox.Show(WpfLocalizer.T("Edit_Msg_DomainsReq")); return; }
        if (string.IsNullOrWhiteSpace(EmailBox.Text) || !EmailBox.Text.Contains("@")) { MessageBox.Show(WpfLocalizer.T("Edit_Msg_EmailReq")); return; }

        var ca = SelectedCa;
        var needsEab = CertificateAuthorityCatalog.RequiresEab(ca);
        var eabKid = EabKeyIdBox.Text.Trim();
        var eabHmac = EabHmacBox.Password.Trim();
        // ZeroSSL: puste EAB = pobranie automatyczne z emaila przy pierwszym Request.
        // Wpisane tylko w jednym polu to blad w kazdym przypadku.
        var eabEmpty = eabKid.Length == 0 && eabHmac.Length == 0;
        var eabPartial = !eabEmpty && (eabKid.Length == 0 || eabHmac.Length == 0);
        if (needsEab && (eabPartial || (eabEmpty && !CertificateAuthorityCatalog.CanAutoFetchEab(ca))))
        { MessageBox.Show(WpfLocalizer.T("Edit_Msg_EabReq", CertificateAuthorityCatalog.DisplayName(ca))); return; }
        if (ca == CertificateAuthority.CustomAcme
            && (!Uri.TryCreate(CustomUrlBox.Text.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        { MessageBox.Show(WpfLocalizer.T("Edit_Msg_UrlReq")); return; }
        if (ChkIIS.IsChecked == true && string.IsNullOrWhiteSpace(IisSiteBox.Text))
        { MessageBox.Show(WpfLocalizer.T("Edit_Msg_IisSiteReq")); return; }

        // DNS-01 provider
        var challengeConfig = ReadChallengeForm(showErrors: true);
        if (challengeConfig == null) return;

        _cert.Name = NameBox.Text.Trim();
        _cert.EmailAddress = EmailBox.Text.Trim();
        _cert.CertificateAuthority = ca;
        _cert.ChallengeType = (ChallengeType)ChallengeBox.SelectedItem;
        if (needsEab)
        {
            _cert.EabKeyId = eabEmpty ? null : eabKid;
            _cert.EabHmacKey = eabEmpty ? null : eabHmac;
        }
        if (ca == CertificateAuthority.CustomAcme)
            _cert.CustomAcmeDirectoryUrl = CustomUrlBox.Text.Trim();
        _cert.Domains = domains;
        _cert.ChallengeConfig = challengeConfig;
        _cert.RenewalMode = AutoRenewCheck.IsChecked == true ? RenewalMode.Auto : RenewalMode.Manual;
        _cert.RenewalDaysBeforeExpiry = RenewDaysBox.Value;
        _cert.Tasks = _tasksEdit;

        // Deployment targets (puste ServiceName = wykryj usluge przy wdrozeniu)
        var serviceName = string.IsNullOrWhiteSpace(ServiceBox.Text) ? null : ServiceBox.Text.Trim();
        _cert.DeploymentTargets.Clear();
        if (ChkIIS.IsChecked == true)
            _cert.DeploymentTargets.Add(new DeploymentTarget { TargetType = DeploymentTargetType.IIS, SiteId = IisSiteBox.Text.Trim() });
        if (ChkApache.IsChecked == true)
        {
            _cert.DeploymentTargets.Add(new DeploymentTarget
            {
                TargetType = DeploymentTargetType.Apache,
                ConfigPath = string.IsNullOrWhiteSpace(ConfigPathBox.Text) ? null : ConfigPathBox.Text.Trim(),
                CertificateOutputPath = Path.Combine(_settings.DataDirectory, "certs", _cert.Id, "apache"),
                ServiceName = serviceName
            });
        }
        if (ChkNginx.IsChecked == true)
        {
            _cert.DeploymentTargets.Add(new DeploymentTarget
            {
                TargetType = DeploymentTargetType.Nginx,
                ConfigPath = string.IsNullOrWhiteSpace(ConfigPathBox.Text) ? null : ConfigPathBox.Text.Trim(),
                CertificateOutputPath = Path.Combine(_settings.DataDirectory, "certs", _cert.Id, "nginx"),
                ServiceName = serviceName
            });
        }

        DialogResult = true;
    }
}
