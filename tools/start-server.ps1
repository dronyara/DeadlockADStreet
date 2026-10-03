# Starts a local Deadworks dedicated server (insecure, LAN). Prints the PID.
# Usage: .\start-server.ps1 [-Map dl_midtown] [-Extra '+some_cvar','1']
param(
    [string]$Map = "dl_midtown",
    [string]$Deadlock = "C:\Program Files (x86)\Steam\steamapps\common\Deadlock",
    [string[]]$Extra = @()
)
if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet" }
$wd = "$Deadlock\game\bin\win64"
Remove-Item "$Deadlock\game\citadel\console.log" -ErrorAction SilentlyContinue
$argList = @('-dedicated', '-console', '-condebug', '-insecure', '-allow_no_lobby_connect',
    '+sv_lan', '1', '+hostport', '27067', '+sv_hibernate_when_empty', '0', '+map', $Map) + $Extra
$p = Start-Process -FilePath "$wd\deadworks.exe" -WorkingDirectory $wd -ArgumentList $argList -PassThru
"PID=$($p.Id)"
