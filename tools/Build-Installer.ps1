# Buduje instalator NSIS: dotnet publish (win-x64, framework-dependent) + makensis.
# Uzycie: powershell -File tools\Build-Installer.ps1 [-OutDir artifacts] [-MakeNsis <sciezka>]
# Wynik:  <OutDir>\CertifyApp-Setup-<AssemblyVersion>.exe
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\artifacts'),
    [string]$MakeNsis
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$csproj = Join-Path $root 'src\Certify.WPF\Certify.WPF.csproj'

if (-not $MakeNsis) {
    $cmd = Get-Command makensis -ErrorAction SilentlyContinue
    $MakeNsis = if ($cmd) { $cmd.Source } else {
        @("${env:ProgramFiles(x86)}\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe") |
            Where-Object { Test-Path $_ } | Select-Object -First 1
    }
}
if (-not $MakeNsis -or -not (Test-Path $MakeNsis)) { throw 'Nie znaleziono makensis.exe (zainstaluj NSIS 3 albo podaj -MakeNsis).' }

$version = ([xml](Get-Content $csproj -Raw)).Project.PropertyGroup.AssemblyVersion | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "Brak <AssemblyVersion> w $csproj" }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$publishDir = Join-Path $OutDir 'publish'
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

Write-Host "Publish $version -> $publishDir"
dotnet publish $csproj -c Release -r win-x64 --self-contained false -o $publishDir -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish zakonczone kodem $LASTEXITCODE" }
# Instalator zaklada plaski katalog publish (aktualizacja/deinstalacja usuwa *.dll, *.exe, *.json, *.pdb)
if (Get-ChildItem $publishDir -Directory) { Write-Warning 'Publish zawiera podkatalogi - deinstalator ich nie usunie; uzupelnij installer\CertifyApp.nsi.' }

$setup = Join-Path $OutDir "CertifyApp-Setup-$version.exe"
& $MakeNsis /V2 "/DVERSION=$version" "/DSRCDIR=$publishDir" "/DOUTFILE=$setup" (Join-Path $root 'installer\CertifyApp.nsi')
if ($LASTEXITCODE -ne 0) { throw "makensis zakonczone kodem $LASTEXITCODE" }
Write-Host "Instalator: $setup"
