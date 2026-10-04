; Instalator NSIS dla CertifyApp (x64, framework-dependent .NET 8).
; Budowanie: powershell -File tools\Build-Installer.ps1
; Albo bezposrednio (MakeNSISW / "Compile NSIS Script"): wersja z Certify.WPF.csproj,
; dotnet publish do ..\artifacts\publish, wynik w ..\artifacts\.
; Parametry opcjonalne: /DVERSION=26.10.4.3 /DSRCDIR=<katalog publish> /DOUTFILE=<plik.exe>

Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma

!ifndef VERSION
  !searchparse /file "..\src\Certify.WPF\Certify.WPF.csproj" "<AssemblyVersion>" VERSION "</AssemblyVersion>"
!endif
!ifndef SRCDIR
  !define SRCDIR "..\artifacts\publish"
  !system 'cmd /c if exist "${SRCDIR}" rmdir /s /q "${SRCDIR}"'
  !system 'dotnet publish "..\src\Certify.WPF\Certify.WPF.csproj" -c Release -r win-x64 --self-contained false -o "${SRCDIR}" -nologo -v q' = 0
!endif
!ifndef OUTFILE
  !system 'cmd /c if not exist "..\artifacts" mkdir "..\artifacts"'
  !define OUTFILE "..\artifacts\CertifyApp-Setup-${VERSION}.exe"
!endif

!define APPNAME     "CertifyApp"
!define APPEXE      "CertifyApp.exe"
!define PUBLISHER   "CertifyApp"
!define UNINSTKEY   "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"
!define SERVICENAME "CertifyAppRenewal"           ; ServiceInfo.Name
!define DOTNET_URL  "https://dotnet.microsoft.com/download/dotnet/8.0"

!include "MUI2.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

Name "${APPNAME} ${VERSION}"
OutFile "${OUTFILE}"
InstallDir "$PROGRAMFILES64\${APPNAME}"
InstallDirRegKey HKLM "${UNINSTKEY}" "InstallLocation"
RequestExecutionLevel admin
BrandingText "${APPNAME} ${VERSION}"

VIProductVersion "${VERSION}"
VIAddVersionKey /LANG=0 "ProductName" "${APPNAME}"
VIAddVersionKey /LANG=0 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=0 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=0 "FileDescription" "${APPNAME} Setup"
VIAddVersionKey /LANG=0 "CompanyName" "${PUBLISHER}"
VIAddVersionKey /LANG=0 "LegalCopyright" "(c) 2026 APPIT Adam Skowroński, PolyForm Noncommercial 1.0.0"

!define MUI_ICON   "..\src\Certify.WPF\Assets\certify.ico"
!define MUI_UNICON "..\src\Certify.WPF\Assets\certify.ico"
!define MUI_ABORTWARNING
!define MUI_LANGDLL_REGISTRY_ROOT "HKLM"
!define MUI_LANGDLL_REGISTRY_KEY "${UNINSTKEY}"
!define MUI_LANGDLL_REGISTRY_VALUENAME "InstallerLanguage"

!define MUI_FINISHPAGE_RUN "$INSTDIR\${APPEXE}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\LICENSE.txt"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Polish"
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_RESERVEFILE_LANGDLL

LangString SecApp       ${LANG_POLISH}  "CertifyApp (wymagane)"
LangString SecApp       ${LANG_ENGLISH} "CertifyApp (required)"
LangString SecDesktop   ${LANG_POLISH}  "Skrót na pulpicie"
LangString SecDesktop   ${LANG_ENGLISH} "Desktop shortcut"
LangString Need64       ${LANG_POLISH}  "CertifyApp wymaga 64-bitowego systemu Windows."
LangString Need64       ${LANG_ENGLISH} "CertifyApp requires 64-bit Windows."
LangString NoDotnet     ${LANG_POLISH}  "Nie znaleziono .NET Desktop Runtime 8 (x64), wymaganego przez CertifyApp.$\r$\n$\r$\nOtworzyć stronę pobierania? Instalacja będzie kontynuowana, ale aplikacja uruchomi się dopiero po zainstalowaniu środowiska."
LangString NoDotnet     ${LANG_ENGLISH} ".NET Desktop Runtime 8 (x64), required by CertifyApp, was not found.$\r$\n$\r$\nOpen the download page? Setup will continue, but the app will not start until the runtime is installed."
LangString AppRunning   ${LANG_POLISH}  "CertifyApp jest uruchomiony. Zamknij go i kliknij OK (Anuluj przerywa)."
LangString AppRunning   ${LANG_ENGLISH} "CertifyApp is running. Close it and click OK (Cancel aborts)."
LangString StopService  ${LANG_POLISH}  "Zatrzymywanie i usuwanie usługi ${SERVICENAME}..."
LangString StopService  ${LANG_ENGLISH} "Stopping and removing service ${SERVICENAME}..."
LangString RemoveData   ${LANG_POLISH}  "Usunąć także dane aplikacji z $APPDATA\${APPNAME}?$\r$\n$\r$\nZawierają zarządzane certyfikaty, klucze prywatne, konta ACME, ustawienia i logi. Tej operacji nie można cofnąć."
LangString RemoveData   ${LANG_ENGLISH} "Also delete application data in $APPDATA\${APPNAME}?$\r$\n$\r$\nIt contains managed certificates, private keys, ACME accounts, settings and logs. This cannot be undone."

; --- Pomocnicze -------------------------------------------------------------

; Czeka, az uzytkownik zamknie CertifyApp.exe (instalacja/aktualizacja nadpisuje jego pliki).
!macro WaitForAppClosed un
Function ${un}WaitForAppClosed
  loop:
    nsExec::ExecToStack 'cmd.exe /c tasklist /FI "IMAGENAME eq ${APPEXE}" /NH | find /I "${APPEXE}"'
    Pop $0
    Pop $1
    ${If} $0 == 0
      MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION "$(AppRunning)" /SD IDCANCEL IDOK loop
      Abort
    ${EndIf}
FunctionEnd
!macroend
!insertmacro WaitForAppClosed ""
!insertmacro WaitForAppClosed "un."

; --- Instalacja -------------------------------------------------------------

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "$(Need64)" /SD IDOK
    Abort
  ${EndIf}
  SetRegView 64
  SetShellVarContext all   ; skroty dla wszystkich, $APPDATA = ProgramData
  !insertmacro MUI_LANGDLL_DISPLAY
FunctionEnd

Section "$(SecApp)" SEC_APP
  SectionIn RO
  Call WaitForAppClosed
  SetOutPath "$INSTDIR"

  ; Aktualizacja: usun pliki poprzedniej wersji (publish jest plaski), zeby nie zostaly stare DLL.
  ; Katalog uslugi (Service\) zostaje - aplikacja odswieza go przy starcie (WindowsServiceManager.NeedsUpdate).
  ${If} ${FileExists} "$INSTDIR\${APPEXE}"
    Delete "$INSTDIR\*.dll"
    Delete "$INSTDIR\*.exe"
    Delete "$INSTDIR\*.json"
    Delete "$INSTDIR\*.pdb"
  ${EndIf}

  File /r "${SRCDIR}\*.*"
  File "..\LICENSE.txt"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateShortCut "$SMPROGRAMS\${APPNAME}.lnk" "$INSTDIR\${APPEXE}"

  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayName"     "${APPNAME}"
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayVersion"  "${VERSION}"
  WriteRegStr   HKLM "${UNINSTKEY}" "Publisher"       "${PUBLISHER}"
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayIcon"     "$INSTDIR\${APPEXE}"
  WriteRegStr   HKLM "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKLM "${UNINSTKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKLM "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${UNINSTKEY}" "EstimatedSize" "$0"

  ; .NET Desktop Runtime 8 x64 (zawiera tez Microsoft.NETCore.App potrzebny usludze)
  FindFirst $0 $1 "$PROGRAMFILES64\dotnet\shared\Microsoft.WindowsDesktop.App\8.*"
  FindClose $0
  ${If} $1 == ""
    MessageBox MB_YESNO|MB_ICONEXCLAMATION "$(NoDotnet)" /SD IDNO IDNO +2
    ExecShell "open" "${DOTNET_URL}"
  ${EndIf}
SectionEnd

Section "$(SecDesktop)" SEC_DESKTOP
  CreateShortCut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${APPEXE}"
SectionEnd

; --- Deinstalacja -----------------------------------------------------------

Function un.onInit
  SetRegView 64
  SetShellVarContext all
  !insertmacro MUI_UNGETLANGUAGE
FunctionEnd

Section "Uninstall"
  Call un.WaitForAppClosed

  ; Usluga Windows (instalowana z GUI do $PROGRAMFILES64\CertifyApp\Service)
  DetailPrint "$(StopService)"
  nsExec::ExecToLog 'sc.exe stop ${SERVICENAME}'
  Pop $0
  ; SCM potrzebuje chwili na zatrzymanie procesu
  StrCpy $2 0
  ${Do}
    nsExec::ExecToStack 'cmd.exe /c sc.exe query ${SERVICENAME} | find "STOPPED"'
    Pop $0
    Pop $1
    ${If} $0 == 0
      ${Break}
    ${EndIf}
    nsExec::ExecToStack 'sc.exe query ${SERVICENAME}'
    Pop $0
    Pop $1
    ${If} $0 != 0   ; usluga nie istnieje
      ${Break}
    ${EndIf}
    IntOp $2 $2 + 1
    ${If} $2 >= 60
      ${Break}
    ${EndIf}
    Sleep 500
  ${Loop}
  nsExec::ExecToLog 'sc.exe delete ${SERVICENAME}'
  Pop $0
  RMDir /r "$PROGRAMFILES64\${APPNAME}\Service"

  Delete "$SMPROGRAMS\${APPNAME}.lnk"
  Delete "$DESKTOP\${APPNAME}.lnk"

  ; Tylko pliki aplikacji (publish jest plaski) - bez RMDir /r, gdyby wybrano katalog wspolny.
  Delete "$INSTDIR\*.dll"
  Delete "$INSTDIR\*.exe"
  Delete "$INSTDIR\*.json"
  Delete "$INSTDIR\*.pdb"
  Delete "$INSTDIR\LICENSE.txt"
  RMDir "$INSTDIR"
  RMDir "$PROGRAMFILES64\${APPNAME}"

  DeleteRegKey HKLM "${UNINSTKEY}"

  ; Dane (certyfikaty, klucze, konta ACME) domyslnie zostaja - takze przy cichej deinstalacji.
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "$(RemoveData)" /SD IDNO IDNO +2
  RMDir /r "$APPDATA\${APPNAME}"
SectionEnd
