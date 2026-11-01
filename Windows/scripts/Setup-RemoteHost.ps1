# Copyright (c) 2026 dabbers (https://github.com/dabbers)
# SPDX-License-Identifier: MIT
#
# One elevated run for what a remotely used host needs (README, "Using the
# Windows host remotely"): the virtual display driver and the task that moves
# a disconnected Remote Desktop session back to the console. Both installers
# need elevation; this script asks for it once (a UAC prompt on the signed-in
# desktop, which can be a Remote Desktop session) and runs them in the elevated
# window, which stays open so the result can be read.
#
#   .\Setup-RemoteHost.ps1                        # install the virtual display and the console-return task
#   .\Setup-RemoteHost.ps1 -WithStartAtLogon      # ... and the start-at-logon task for this user
#   .\Setup-RemoteHost.ps1 -Transcript <file>     # also write everything to this log file
#   .\Setup-RemoteHost.ps1 -Uninstall             # remove what the same switches installed
param(
    [switch]$WithStartAtLogon,
    [string]$Transcript = "",
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $elevated) {
    # Re-run this script elevated. The new window stays open (-NoExit) because
    # it is the only place the result is shown.
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", "`"$PSCommandPath`"")
    if ($WithStartAtLogon) { $argList += "-WithStartAtLogon" }
    if ($Uninstall) { $argList += "-Uninstall" }
    if ($Transcript) { $argList += @("-Transcript", "`"$Transcript`"") }
    try {
        $p = Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList $argList -PassThru
        Write-Output "elevation granted; the elevated window (pid $($p.Id)) is running the installers"
        if ($Transcript) { Write-Output "transcript: $Transcript" }
    } catch {
        Write-Output "elevation refused or unavailable: $($_.Exception.Message)"
        exit 1
    }
    return
}

if ($Transcript) { Start-Transcript -Path $Transcript -Force | Out-Null }
$failed = 0
$extra = @{}
if ($Uninstall) { $extra.Uninstall = $true }
$steps = @(
    @{ Name = "virtual display"; Script = "Install-VirtualDisplay.ps1"; Run = $true },
    @{ Name = "return the session to the console on disconnect"; Script = "Install-ConsoleReturn.ps1"; Run = $true },
    @{ Name = "start the server at sign-in"; Script = "Install-StartAtLogon.ps1"; Run = [bool]$WithStartAtLogon }
)
foreach ($step in $steps) {
    if (-not $step.Run) { continue }
    Write-Output ""
    Write-Output "== $($step.Name) =="
    try {
        & (Join-Path $PSScriptRoot $step.Script) @extra
    } catch {
        $failed++
        Write-Output "FAILED: $($_.Exception.Message)"
    }
}
Write-Output ""
if ($failed -eq 0) { Write-Output "Done." } else { Write-Output "Finished with $failed failed step(s); see above." }
if ($Transcript) { Stop-Transcript | Out-Null }
