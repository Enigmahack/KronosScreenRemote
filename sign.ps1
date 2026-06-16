<#
.SYNOPSIS
    Authenticode-sign KronosScreenRemote.exe.

.DESCRIPTION
    TWO MODES:

    Self-signed certificate (default, codesign.pfx):
      - Removes "Unknown publisher" in UAC dialogs on machines that trust the cert.
      - Does NOT bypass Windows SmartScreen for other users. SmartScreen will still
        warn until the file builds download reputation, or until an EV cert is used.
      - Use this for local dev/testing or for controlled distribution to users who
        manually install the certificate.

    Commercial EV Authenticode certificate:
      - Immediately bypasses SmartScreen for all users - no reputation wait.
      - Obtain from DigiCert, Sectigo, GlobalSign (~$300-700/year, requires business entity).
      - Use the same -PfxPath flag pointing to your commercial .pfx file.

.PARAMETER Setup
    One-time setup: create a self-signed code-signing certificate, export to
    codesign.pfx, and install in LocalMachine trusted stores so signatures are
    recognized on this machine. Must be run as Administrator.

.PARAMETER PfxPath
    Path to the PFX certificate. Default: codesign.pfx (next to this script).
    Set env:CODESIGN_PFX to override without a command-line argument.

.PARAMETER ExePath
    Path to the EXE to sign.
    Default: bin\Release\net10.0-windows\win-x64\publish\KronosScreenRemote.exe

.EXAMPLE
    # One-time setup (run as Administrator):
    .\sign.ps1 -Setup

    # Sign after publish:
    dotnet publish -p:PublishProfile=win-x64
    .\sign.ps1

    # CI / non-interactive (set env vars before running):
    $env:CODESIGN_PASSWORD = 'secret'
    .\sign.ps1

    # Sign with a commercial EV certificate:
    .\sign.ps1 -PfxPath C:\certs\my_ev_cert.pfx
#>
param(
    [switch] $Setup,
    [string] $PfxPath     = $(if ($env:CODESIGN_PFX) { $env:CODESIGN_PFX } else { "$PSScriptRoot\codesign.pfx" }),
    [string] $ExePath     = "$PSScriptRoot\bin\Release\net10.0-windows\win-x64\publish\KronosScreenRemote.exe",
    [string] $CertSubject = "CN=Kronos ScreenRemote, O=KronosMods",
    [string] $TimestampUrl = "http://timestamp.digicert.com"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Find-SignTool {
    $kitsRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $kitsRoot) {
        $found = Get-ChildItem $kitsRoot -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
                 Sort-Object FullName | Select-Object -Last 1
        if ($found) { return $found.FullName }
    }
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw "signtool.exe not found. Install Windows SDK: https://developer.microsoft.com/windows/downloads/windows-sdk/"
}

function Get-PfxPassword {
    if ($env:CODESIGN_PASSWORD) { return $env:CODESIGN_PASSWORD }
    $ss = Read-Host -Prompt "PFX password" -AsSecureString
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($ss)
    try { return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

# Setup: create self-signed cert ------------------------------------------

if ($Setup) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
                [Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Error "-Setup requires Administrator (right-click > Run as Administrator)."
        exit 1
    }

    Write-Host "Creating self-signed code-signing certificate ($CertSubject)..."
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $CertSubject `
        -KeyAlgorithm RSA `
        -KeyLength 4096 `
        -HashAlgorithm SHA256 `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -NotAfter (Get-Date).AddYears(5)

    $ss = Read-Host -Prompt "Choose a PFX password (stored on disk - keep it safe)" -AsSecureString
    Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password $ss | Out-Null
    Write-Host "PFX written to: $PfxPath"

    foreach ($storeName in @("TrustedPublisher", "Root")) {
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($storeName, "LocalMachine")
        $store.Open("ReadWrite")
        $store.Add($cert)
        $store.Close()
        Write-Host "  Installed in LocalMachine\$storeName"
    }

    Write-Host ""
    Write-Host "Setup complete. Workflow:"
    Write-Host "  1. Build:    dotnet publish -p:PublishProfile=win-x64"
    Write-Host "  2. Sign:     .\sign.ps1"
    Write-Host "  3. Verify:   Right-click EXE > ’ Properties > ’ Digital Signatures"
    Write-Host ""
    Write-Host "IMPORTANT: codesign.pfx is in .gitignore - never commit it."
    Write-Host "NOTE: Self-signed cert removes 'Unknown publisher' on THIS machine only."
    Write-Host "      For SmartScreen bypass on all machines, use a commercial EV cert."
    exit 0
}

# --- SIGN ---

if (-not (Test-Path $ExePath)) {
    Write-Error "EXE not found: $ExePath`nRun: dotnet publish -p:PublishProfile=win-x64"
    exit 1
}
if (-not (Test-Path $PfxPath)) {
    Write-Error "PFX not found: $PfxPath`nRun: .\sign.ps1 -Setup   (or set -PfxPath to your certificate)"
    exit 1
}

$signtool = Find-SignTool
$password  = Get-PfxPassword

Write-Host "Signing: $ExePath"
& $signtool sign /fd sha256 /f $PfxPath /p $password /tr $TimestampUrl /td sha256 $ExePath
if ($LASTEXITCODE -ne 0) { throw "signtool sign failed (exit $LASTEXITCODE)" }

& $signtool verify /pa $ExePath
if ($LASTEXITCODE -ne 0) { throw "signtool verify failed (exit $LASTEXITCODE)" }

Write-Host "Signed successfully: $ExePath"
