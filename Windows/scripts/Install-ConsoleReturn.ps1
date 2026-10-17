# Copyright (c) 2026 dabbers (https://github.com/dabbers)
# SPDX-License-Identifier: MIT
#
# Registers (or removes) a scheduled task that runs Return-SessionToConsole.ps1
# as SYSTEM whenever a Remote Desktop session disconnects, so the desktop goes
# back to the console instead of being left without a display. See README,
# "Using the Windows host remotely". Run from an elevated PowerShell:
#
#   .\Install-ConsoleReturn.ps1              # install
#   .\Install-ConsoleReturn.ps1 -Uninstall   # remove
#Requires -RunAsAdministrator
param([switch]$Uninstall)

$ErrorActionPreference = "Stop"
$taskName = "RemoteApp Return Session To Console"

if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "removed task '$taskName'"
    return
}

$script = Join-Path $PSScriptRoot "Return-SessionToConsole.ps1"
if (-not (Test-Path $script)) { throw "missing $script" }

$action = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`""

# Trigger: a remote (RDP) session disconnected. StateChange 4 = RemoteDisconnect;
# the delay lets the disconnect finish before the session is moved.
$triggerClass = Get-CimClass -ClassName MSFT_TaskSessionStateChangeTrigger -Namespace Root/Microsoft/Windows/TaskScheduler
$trigger = New-CimInstance -CimClass $triggerClass -ClientOnly -Property @{ StateChange = 4; Delay = "PT3S"; Enabled = $true }

$principal = New-ScheduledTaskPrincipal -UserId "NT AUTHORITY\SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Write-Output "registered task '$taskName': on Remote Desktop disconnect, run $script as SYSTEM"
