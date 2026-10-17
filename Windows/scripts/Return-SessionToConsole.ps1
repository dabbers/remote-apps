# Copyright (c) 2026 dabbers (https://github.com/dabbers)
# SPDX-License-Identifier: MIT
#
# Moves a disconnected Remote Desktop session back to the physical console so
# the desktop keeps a display and input focus. A session that was disconnected
# (not signed out) has neither: Desktop Duplication finds no output and nothing
# can be brought to the foreground, so the RemoteApp server reports "no display
# is attached to the host's session". On the console the session uses the
# monitor, or the virtual display (see README, "Using the Windows host
# remotely"), and the server works unattended.
#
# Run by the scheduled task that Install-ConsoleReturn.ps1 registers (as SYSTEM,
# a few seconds after a remote session disconnects). Safe to run by hand from an
# elevated prompt. Note that the console is then an unlocked desktop: anyone at
# the machine's keyboard sees the session.
param(
    # Only move this user's session (default: any disconnected user session).
    [string]$User = ""
)

$ErrorActionPreference = "Stop"
$lines = quser 2>$null
if (-not $lines) { Write-Output "no user sessions"; exit 0 }

$moved = $false
foreach ($line in $lines) {
    # quser columns: USERNAME SESSIONNAME ID STATE ...; a disconnected session has no session name.
    if ($line -notmatch '^[\s>]?(?<user>\S+)\s+(?:(?<name>\S+)\s+)?(?<id>\d+)\s+(?<state>Disc|Active)') { continue }
    if ($Matches.state -ne "Disc") { continue }
    if ($User -and $Matches.user -ne $User) { continue }
    $id = [int]$Matches.id
    Write-Output "moving session $id ($($Matches.user)) to the console"
    & tscon.exe $id /dest:console
    if ($LASTEXITCODE -ne 0) { Write-Output "tscon failed with exit code $LASTEXITCODE (is someone signed in at the console?)" }
    else { $moved = $true }
}
if (-not $moved) { Write-Output "no disconnected session to move" }
