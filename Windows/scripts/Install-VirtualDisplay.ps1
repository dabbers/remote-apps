# Copyright (c) 2026 dabbers (https://github.com/dabbers)
# SPDX-License-Identifier: MIT
#
# Installs (or removes) the Virtual Display Driver, so the host has a monitor
# even with no physical display attached and nobody connected over Remote
# Desktop. See README, "Using the Windows host remotely".
#
# It uses the driver that the winget package ships:
#
#   winget install --id=VirtualDrivers.Virtual-Display-Driver -e
#
# installs the "VDD Control" app as a portable package (no elevation), and the
# package folder also holds the signed driver (MttVDD.inf, MttVDD.dll,
# mttvdd.cat) and Microsoft's devcon.exe. This script installs the driver from
# there without the app, after checking both signatures. Run it from an
# elevated PowerShell (a Remote Desktop session is fine):
#
#   .\Install-VirtualDisplay.ps1                         # one 2560x1440 @ 60 Hz display
#   .\Install-VirtualDisplay.ps1 -Width 1920 -Height 1080
#   .\Install-VirtualDisplay.ps1 -DryRun                 # preview only; no elevation needed
#   .\Install-VirtualDisplay.ps1 -Uninstall
#
# The display's modes come from C:\VirtualDisplayDriver\vdd_settings.xml, which
# the script writes if it is missing (pass -ReplaceSettings to rewrite it).
param(
    # Folder holding SignedDrivers\ and Dependencies\devcon.exe (default: the winget package).
    [string]$PackagePath = "",
    [int]$Width = 2560,
    [int]$Height = 1440,
    [int]$RefreshRate = 60,
    [switch]$ReplaceSettings,
    [switch]$DryRun,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$hardwareId = "Root\MttVDD"
# The driver reads its settings here unless HKLM\SOFTWARE\MikeTheTech\VirtualDisplayDriver\VDDPATH says otherwise.
$settingsDir = "C:\VirtualDisplayDriver"
$settingsPath = Join-Path $settingsDir "vdd_settings.xml"

$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $elevated -and -not $DryRun) { throw "run this from an elevated PowerShell (or pass -DryRun to preview)" }
$would = if ($DryRun) { "would " } else { "" }

function Find-Package {
    if ($PackagePath) { return (Resolve-Path $PackagePath).Path }
    $root = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages"
    $dir = Get-ChildItem $root -Directory -Filter "VirtualDrivers.Virtual-Display-Driver_*" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $dir) {
        throw "Virtual Display Driver package not found under $root; install it with: winget install --id=VirtualDrivers.Virtual-Display-Driver -e  (or pass -PackagePath)"
    }
    return $dir.FullName
}

function Get-VirtualDisplayDevices {
    Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Where-Object { @($_.HardwareID) -contains $hardwareId }
}

function Get-StoreDriverNames {
    # Published names (oemNN.inf) of this driver in the driver store.
    $names = @()
    $published = $null
    foreach ($line in (pnputil /enum-drivers)) {
        if ($line -match '^Published Name:\s+(\S+)') { $published = $Matches[1] }
        elseif ($line -match '^Original Name:\s+mttvdd\.inf' -and $published) { $names += $published; $published = $null }
    }
    return $names
}

$package = Find-Package
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x86" }   # the package names its x64 build "x86"
$driverDir = Join-Path $package "SignedDrivers\$arch\VDD"
$inf = Join-Path $driverDir "MttVDD.inf"
$cat = Join-Path $driverDir "mttvdd.cat"
$devcon = Join-Path $package "Dependencies\devcon.exe"
foreach ($f in $inf, $cat, (Join-Path $driverDir "MttVDD.dll"), $devcon) {
    if (-not (Test-Path $f)) { throw "missing $f" }
}
Write-Output "package: $package"

# Refuse anything that is not what it claims to be: the driver catalog must carry a
# valid signature, and devcon must be Microsoft's.
$catSig = Get-AuthenticodeSignature $cat
if ($catSig.Status -ne "Valid") { throw "$cat is not validly signed ($($catSig.Status)); not installing it" }
$signer = $catSig.SignerCertificate
Write-Output "driver:  $inf"
Write-Output "         signed by $($signer.Subject) (thumbprint $($signer.Thumbprint))"
$devconSig = Get-AuthenticodeSignature $devcon
if ($devconSig.Status -ne "Valid" -or $devconSig.SignerCertificate.Subject -notmatch "O=Microsoft Corporation") {
    throw "$devcon is not signed by Microsoft ($($devconSig.Status)); not running it"
}

$devices = @(Get-VirtualDisplayDevices)

if ($Uninstall) {
    if ($devices.Count -gt 0) {
        Write-Output "${would}remove $($devices.Count) device(s): $(($devices | ForEach-Object { $_.InstanceId }) -join ', ')"
        if (-not $DryRun) { & $devcon remove $hardwareId | Out-Null }
    } else {
        Write-Output "no virtual display device present"
    }
    foreach ($name in Get-StoreDriverNames) {
        Write-Output "${would}delete $name (mttvdd.inf) from the driver store"
        if (-not $DryRun) { pnputil /delete-driver $name /uninstall /force | Out-Null }
    }
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("TrustedPublisher", "LocalMachine")
    $store.Open("ReadOnly")
    $trusted = $store.Certificates | Where-Object { $_.Thumbprint -eq $signer.Thumbprint }
    $store.Close()
    if ($trusted) {
        Write-Output "${would}remove the driver's signer from Trusted Publishers"
        if (-not $DryRun) {
            $store.Open("ReadWrite"); $store.Remove($trusted); $store.Close()
        }
    }
    Write-Output "settings left in place: $settingsPath"
    return
}

# Settings: one display at the requested mode. The driver reads the file when
# the device starts, so it is written before the install.
$settingsXml = @"
<?xml version="1.0" encoding="utf-8"?>
<vdd_settings>
    <monitors>
        <count>1</count>
    </monitors>
    <gpu>
        <friendlyname>default</friendlyname>
    </gpu>
    <global>
        <g_refresh_rate>$RefreshRate</g_refresh_rate>
    </global>
    <resolutions>
        <resolution>
            <width>$Width</width>
            <height>$Height</height>
            <refresh_rate>$RefreshRate</refresh_rate>
        </resolution>
    </resolutions>
    <options>
        <CustomEdid>false</CustomEdid>
        <PreventSpoof>false</PreventSpoof>
        <EdidCeaOverride>false</EdidCeaOverride>
        <HardwareCursor>true</HardwareCursor>
        <SDR10bit>false</SDR10bit>
        <HDRPlus>false</HDRPlus>
        <logging>false</logging>
        <debuglogging>false</debuglogging>
    </options>
</vdd_settings>
"@
if ((Test-Path $settingsPath) -and -not $ReplaceSettings) {
    Write-Output "settings: keeping existing $settingsPath (pass -ReplaceSettings to rewrite it for ${Width}x${Height} @ $RefreshRate Hz)"
} else {
    Write-Output "settings: ${would}write $settingsPath for one ${Width}x${Height} @ $RefreshRate Hz display"
    if (-not $DryRun) {
        New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
        [IO.File]::WriteAllText($settingsPath, $settingsXml, (New-Object System.Text.UTF8Encoding $false))
    }
}

# Trust the signer first, so the install does not stop at a "Would you like to
# install this device software?" dialog that nobody may be there to answer.
$store = New-Object System.Security.Cryptography.X509Certificates.X509Store("TrustedPublisher", "LocalMachine")
$store.Open("ReadOnly")
$alreadyTrusted = [bool]($store.Certificates | Where-Object { $_.Thumbprint -eq $signer.Thumbprint })
$store.Close()
if ($alreadyTrusted) {
    Write-Output "signer already in Trusted Publishers"
} else {
    Write-Output "${would}add the signer to Trusted Publishers (LocalMachine)"
    if (-not $DryRun) { $store.Open("ReadWrite"); $store.Add($signer); $store.Close() }
}

if ($devices.Count -gt 0) {
    Write-Output "device already present ($($devices[0].InstanceId), status $($devices[0].Status)); ${would}update its driver:"
    Write-Output "    devcon update `"$inf`" $hardwareId"
    if (-not $DryRun) { & $devcon update $inf $hardwareId }
} else {
    Write-Output "${would}create the device and install the driver:"
    Write-Output "    devcon install `"$inf`" $hardwareId"
    if (-not $DryRun) { & $devcon install $inf $hardwareId }
}
if ($DryRun) { Write-Output "dry run; nothing changed"; return }
$code = $LASTEXITCODE
# devcon: 0 ok, 1 ok but a reboot is needed, 2 failed, 3 usage.
if ($code -eq 2 -or $code -eq 3) { throw "devcon failed with exit code $code" }
if ($code -eq 1) { Write-Output "devcon asks for a reboot before the display appears" }

# Wait for the device to start.
$started = $null
for ($i = 0; $i -lt 30 -and -not $started; $i++) {
    Start-Sleep -Milliseconds 500
    $started = Get-VirtualDisplayDevices | Where-Object { $_.Status -eq "OK" } | Select-Object -First 1
}
if ($started) {
    Write-Output "installed: $($started.FriendlyName) ($($started.InstanceId)) is running"
} else {
    $now = @(Get-VirtualDisplayDevices)
    if ($now.Count -gt 0) { Write-Output "device present but not started yet: $($now[0].InstanceId) status $($now[0].Status)" }
    else { Write-Output "device not found after the install; check Device Manager > Display adapters" }
}
Write-Output ""
Write-Output "The virtual display belongs to the console session. A session that is disconnected from"
Write-Output "Remote Desktop still has no display until it is moved back to the console: register"
Write-Output "Install-ConsoleReturn.ps1 (same elevated prompt) so that happens on every disconnect."
