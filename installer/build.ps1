<#
.SYNOPSIS
    Produce l'installer di Disk Temp Monitor.

.DESCRIPTION
    Pubblica l'applicazione self-contained (porta con sé il runtime .NET, così sul
    computer di destinazione non serve installare nulla) in installer\stage, poi
    compila installer\DiskTempMonitor.iss con Inno Setup.

    Il risultato è installer\Output\DiskTempMonitor-<versione>-setup.exe.

.PARAMETER Version
    Sovrascrive la versione letta da DiskTempMonitor.csproj.

.PARAMETER SkipPublish
    Riusa quanto c'è già in stage e ricompila solo l'installer: utile mentre si
    modifica lo script .iss.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\build.ps1
#>

[CmdletBinding()]
param(
    [string] $Version,
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'

$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$root    = Split-Path -Parent $here
$csproj  = Join-Path $root 'DiskTempMonitor.csproj'
$stage   = Join-Path $here 'stage'
$output  = Join-Path $here 'Output'
$script  = Join-Path $here 'DiskTempMonitor.iss'

# ------------------------------------------------------------------ versione

if (-not $Version) {
    $xml = [xml](Get-Content -LiteralPath $csproj)
    $Version = ($xml.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
    if (-not $Version) { throw "Versione non trovata in $csproj" }
}
Write-Host "Disk Temp Monitor $Version" -ForegroundColor Cyan

# ------------------------------------------------------------------ Inno Setup

$iscc = (Get-Command 'iscc.exe' -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $candidati = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $iscc = $candidati | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $iscc) {
    throw "Inno Setup 6 non trovato. Installalo con:  winget install --id JRSoftware.InnoSetup -e"
}

# ------------------------------------------------------------------ pubblicazione

if (-not $SkipPublish) {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }

    Write-Host 'Pubblicazione self-contained...' -ForegroundColor Cyan
    # Solo le risorse localizzate che servono: le altre lingue del framework sono un
    # centinaio di DLL inutili nel pacchetto. Passata dall'ambiente perché sulla riga di
    # comando il punto e virgola verrebbe letto da PowerShell come separatore.
    $env:SatelliteResourceLanguages = 'en;it'

    # Niente PublishSingleFile: i file stanno già dentro l'installer, e il file unico
    # si limiterebbe a riestrarre le librerie native in %TEMP% a ogni avvio.
    # Niente trimming: LibreHardwareMonitor carica i sensori per riflessione.
    & dotnet publish $csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -p:Version=$Version `
        -o $stage
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ha restituito $LASTEXITCODE" }

    # Il .pdb non serve a chi installa e pesa qualche megabyte.
    Get-ChildItem -LiteralPath $stage -Filter '*.pdb' -Recurse | Remove-Item -Force
}

$exe = Join-Path $stage 'DiskTempMonitor.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Manca ${exe}: esegui senza -SkipPublish" }

# ------------------------------------------------------------------ installer

Write-Host 'Compilazione installer...' -ForegroundColor Cyan
& $iscc "/DAppVersion=$Version" "/DSourceDir=$stage" $script
if ($LASTEXITCODE -ne 0) { throw "ISCC ha restituito $LASTEXITCODE" }

$setup = Join-Path $output "DiskTempMonitor-$Version-setup.exe"
$mb = [math]::Round((Get-Item -LiteralPath $setup).Length / 1MB, 1)
Write-Host "Pronto: $setup ($mb MB)" -ForegroundColor Green
