# Copyright (c) 2026 dabbers (https://github.com/dabbers)
# SPDX-License-Identifier: MIT
#
# Registers (or removes) a per-user scheduled task that starts the RemoteApp
# server when this user signs in (at the console or over Remote Desktop), so
# the host is reachable without anyone launching it. No elevation needed. See
# README, "Using the Windows host remotely".
#
#   .\Install-StartAtLogon.ps1                       # Debug build of this checkout
#   .\Install-StartAtLogon.ps1 -ExePath <path>       # e.g. a published build
#   .\Install-StartAtLogon.ps1 -Uninstall
param(
    [string]$ExePath = (Join-Path (Split-Path $PSScriptRoot -Parent) "RemoteApp.Server\bin\Debug\net10.0-windows\RemoteAppServer.exe"),
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$taskName = "RemoteApp Server"

if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "removed task '$taskName'"
    return
}

if (-not (Test-Path $ExePath)) { throw "server executable not found: $ExePath (build the solution first, or pass -ExePath)" }
$ExePath = (Resolve-Path $ExePath).Path

$action = New-ScheduledTaskAction -Execute $ExePath -WorkingDirectory (Split-Path $ExePath -Parent)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
# Interactive: runs in the signed-in session, so the server window (with the
# pairing code) is visible and the desktop it captures is this one.
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew
$settings.ExecutionTimeLimit = "PT0S"   # never stop it (the default would kill it after 3 days)

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Write-Output "registered task '$taskName': starts $ExePath when $env:USERNAME signs in"
