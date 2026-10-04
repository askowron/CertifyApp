# CertifyApp — Certify The Web clone (C# .NET 8 WPF)

Aplikacja desktopowa WPF do zarządzania certyfikatami **ACME v2** z automatycznym
wdrożeniem na **IIS / Apache / Nginx** — analog **Certify The Web**.

## Urzędy CA

- **Let's Encrypt** (+ Staging do testów)
- **ZeroSSL** — EAB pobierany automatycznie z adresu email (API ZeroSSL, jak w CTW)
  albo wpisany ręcznie z panelu ZeroSSL; dłuższe odpytywanie (do 10 min), bo ZeroSSL
  potrafi długo trzymać order w `processing`; brak publicznego stagingu
- **Google Public CA** (+ Staging, wymaga EAB z GCP)
- **Custom ACME server** (własny Directory URL, np. Pebble / step-ca)

## Challenge

- **http-01** — plik w webroot (`/.well-known/acme-challenge/`), wymagany port 80
- **dns-01 Manual** — wartość TXT w logu, do ręcznego wpisania w DNS
- **dns-01 Cloudflare** (auto) — tworzy TXT, czeka na propagację (DoH), sprząta;
  token Bearer: Zone DNS Edit + Zone Read
- **dns-01 Route53** (auto) — SigV4 bez AWS SDK, czeka na INSYNC + DoH, sprząta;
  IAM: ChangeResourceRecordSets + ListHostedZonesByName + GetChange
- **Wildcard** (`*.example.com`) wymaga dns-01
- **Adresy IP** (RFC 8738, tylko Let's Encrypt) — własny raw-ACME
  (Certes wspiera tylko identyfikatory DNS); IP zawsze przez http-01
- Przycisk **Wyczyść stare TXT** usuwa osierocone `_acme-challenge.*` (CF/Route53)

## Odporność ACME

- Retry do 5 prób, backoff 2→4→8…→60 s + jitter; honoruje `Retry-After` (cap 300 s)
- Ponawia: 429/rateLimited, 5xx, błędy sieci/timeouty; **nie** ponawia 4xx i cancel
- Dotyczy obu flow (Certes i raw-IP): newOrder, validate, finalize, download, konto

## Certyfikat — cykl życia

- **Podgląd** (Preview) — suche podsumowanie requestu + walidacja przed akcją
- **Status** — aktywny/wygasł/błąd, % wykorzystania życia, ostatnie/następne
  odnowienie, ostatnie CA; **Historia** operacji (max 100)
- **Alerty** — banner (wygasłe/wygasające/błędy) + okno szczegółów
- **Kalendarz** — miesięczna siatka odnowień i wygasań
- **In Progress** — jedna operacja naraz, licznik czasu, przycisk Anuluj
- **Auto-renew** — gdy `DaysUntilExpiry <= RenewalDaysBeforeExpiry` (domyślnie 30):
  w tle co 12 h albo headless `CertifyApp.exe --renew` (Task Scheduler, 03:00)
- **Powiadomienia e-mail** (SMTP) — digest po Renew All (tylko błędy/wygasające)
  + mail o błędzie Request; próg ostrzegania konfigurowalny (domyślnie 14 dni)

## Wdrożenie i zadania

- **Deployment**: IIS (ServerManager + `LocalMachine\My`; przy braku uprawnień
  tylko loguje komendę `netsh http add sslcert` do ręcznego wykonania),
  Apache/Nginx (kopie + patch vhost + reload serwisu)
- **Export** do pliku: 8 formatów (PEM primary/chain/intermediate/key/full±key,
  primary+intermediate, PFX), lokalnie
- **Tasks** (jak CTW): Pre-Request (przerywają request przy błędzie)
  i Deployment (po wdrożeniu, trigger Po sukcesie/Zawsze):
  Export, Restart usługi, PowerShell, Uruchom program (`{Name}` `{Domains}`
  `{CertPath}` `{PfxPath}`), Czekaj, Webhook (POST JSON, filtr Success/Failure)

## Backup

- Zip: modele, `appsettings.json`, klucze kont ACME, `certs/**` (bez logów)
- **Szyfrowany `.cbak`**: PBKDF2-SHA256 (200k) → AES-256-GCM, hasło min 8 znaków
- Import: **Połącz** (nowe Id) / **Zamień** (wszystko); ścieżki przepisywane
  na lokalny komputer; brakujące pliki raportowane

## Architektura

```
CertifyApp.slnx
 ├─ src/Certify.Core            # Modele + persistence + logika
 │   ├─ Models/                 # ManagedCertificate (+Tasks, History, EAB),
 │   │                           AppSettings (+SMTP), Enums, CertificateTask,
 │   │                           CertificateAuthorityCatalog, CertificatePreview,
 │   │                           CertificateAlerts, RenewalCalendar
 │   └─ Services/               # CertificateStore, SettingsStore,
 │                               CertificateService (RenewalService +
 │                               ICertificateAuthorityProvider, IDeploymentTarget),
 │                               BackgroundRenewalService, CertificateExporter,
 │                               CertificateArtifactWriter, CertificateTaskRunner,
 │                               EmailNotifier, BackupService, BackupEncryption,
 │                               AcmeRetryPolicy
 ├─ src/Certify.ACME            # ACME via Certes 3.0.4 + własny raw-ACME
 │   ├─ AcmeAccountManager      # konta ES256 (+EAB HS256), cache per URL+email
 │   ├─ LetsEncryptService      # flow DNS (Certes), hook IP -> IpOrderProcessor
 │   ├─ IpOrderProcessor        # RFC 8738: JWS, newOrder ip/dns, CSR z SAN IP
 │   ├─ AcmeJws / RawAcmeClient # JWS ES256, thumbprint, nonce, POST-as-GET
 │   ├─ AcmeRetry               # klasyfikacja Certes/raw + Retry-After
 │   ├─ ChallengeHandlers       # Http01Filesystem, Dns01Manual, factory
 │   ├─ CloudflareDns / Route53Dns (+SigV4) / DnsJanitor
 ├─ src/Certify.Deployment      # IDeploymentTarget: IIS / Apache / Nginx
 │                              # (+ DiscoverSites/Bindings, IisBindingHelper)
 └─ src/Certify.WPF             # UI (WPF, PerMonitorV2, ostre fonty Display)
     ├─ MainWindow              # tabela + status + alerty + log + operacje
     ├─ CertificateEditWindow   # domeny (import z IIS), CA/EAB, challenge,
     │                           deployment, zadania, czyszczenie TXT
     ├─ TaskEditWindow / ExportCertificateWindow / PreviewWindow
     ├─ AlertsWindow / HistoryWindow / CalendarWindow
     └─ SettingsWindow (SMTP) / ImportWindow / PasswordWindow
```

## Ustawienia

- `appsettings.json` w `%ProgramData%\CertifyApp\` (SMTP, próg ostrzegania,
  język UI: **Auto** = z systemu / **pl** / **en**)
- UI w całości po polsku i angielsku (XAML + dialogi + statusy);
  logi techniczne celowo po polsku

## Dane na dysku

`%ProgramData%\CertifyApp\` — `managed_certificates.json`, `appsettings.json`,
`acme_accounts.json`, `certs/<id>/` (`*.pfx` hasło `certify`, `*.crt`,
`*.key`, `chain.pem`), `logs/`.

## Wymagania

- .NET 8 SDK, Windows 10/11
- Uprawnienia **Administratora** — aplikacja sama prosi o nie (UAC) przy starcie
  (IIS / ServerManager, `LocalMachine\My`, zapis w `C:\inetpub`); debugowanie: Visual Studio jako Administrator
- http-01: port 80 publiczny, domena/IP wskazuje na serwer
- Cloudflare: token (DNS Edit + Zone Read); Route53: Key ID + Secret
- Google: EAB Key ID + HMAC z GCP; ZeroSSL: wystarczy email (EAB opcjonalny)

## Build & Run

```powershell
dotnet build
dotnet run --project src/Certify.WPF/Certify.WPF.csproj
dotnet test
# publish single exe
dotnet publish src/Certify.WPF -c Release -r win-x64 --self-contained false
# instalator NSIS (wymaga NSIS 3) -> artifacts\CertifyApp-Setup-<wersja>.exe
powershell -File tools\Build-Installer.ps1
# headless renew (Task Scheduler):
schtasks /Create /SC DAILY /TN "CertifyApp Renew" /TR "CertifyApp.exe --renew" /ST 03:00 /RU SYSTEM
```

## Użycie

1. **Nowy certyfikat** → nazwa, email (ACME), domeny (ręcznie, z IIS albo z IP),
   CA (+EAB / custom URL jeśli trzeba).
2. **Challenge** → http-01 + WebRoot, albo dns-01 (Manual / Cloudflare / Route53).
3. **Deployment** → IIS (Site Name; „Pobierz z IIS” dopisuje domeny z bindingów i ustawia WebRoot
   na ścieżkę fizyczną site) / Apache / Nginx (ConfigPath z istniejącymi dyrektywami SSL + usługa,
   puste = wykryj, np. `Apache2.4`) + opcjonalne Tasks.
4. **Podgląd** → sprawdź walidację → **Request / Renew** (log na dole).
5. **Staging Test** → staging danego CA (ZeroSSL/Custom go nie mają — log powie).
6. **Renew All / Task Scheduler / e-mail** → digest po przebiegu.
7. **Backup** (`.zip` / szyfrowany `.cbak`) przed migracją maszyny.

## Ograniczenia (szczerze)

- Live flow (prawdziwy order u CA) wymaga publicznego serwera/domeny z portem 80
  albo strefy DNS na CF/Route53.
- Testy jednostkowe (`dotnet test`, `tests/Certify.Tests`) pokrywają kryptografię,
  parsowanie, reguły, magazyny, backup, eksport, zadania i deployery plikowe — bez sieci
  i bez IIS. Live ACME weryfikuj ręcznie na Staging (Let's Encrypt / Google) albo Pebble.
- Sekrety lokalne (tokeny, EAB, SMTP, klucze) trzymane plain text w JSON
  (poza backupem `.cbak`).
- Buypass pominięty świadomie (GoSSL ACME wygaszane); zamiast niego Custom ACME.
- Certyfikaty na IP wydaje w praktyce tylko Let's Encrypt (ZeroSSL nie wspiera IP).

## Instalator (NSIS)

`tools\Build-Installer.ps1` robi `dotnet publish` (win-x64, framework-dependent)
i buduje `installer\CertifyApp.nsi` → `artifacts\CertifyApp-Setup-<AssemblyVersion>.exe`.
Instalator (PL/EN) instaluje do `%ProgramFiles%\CertifyApp`, tworzy skrót w menu Start
(opcjonalnie na pulpicie), wpis w „Aplikacje i funkcje” i ostrzega, gdy brak
.NET Desktop Runtime 8 (x64). Deinstalator zatrzymuje i usuwa usługę `CertifyAppRenewal`,
a dane z `%ProgramData%\CertifyApp` usuwa tylko na wyraźne potwierdzenie.

## Licencja

Darmowa do użytku niekomercyjnego — [PolyForm Noncommercial 1.0.0](LICENSE.txt).
Użycie komercyjne wymaga osobnej licencji od autora.
