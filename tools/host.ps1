# Hosts an Ability Draft server. Friends join from the Deadlock console: connect <your ip>:27067
# -Lan keeps it to the local network (no Steam validation wait). Without it, forward UDP/TCP 27067 on the router.
param([switch]$Lan, [string]$Map = "dl_midtown")
$extra = @()
if (-not $Lan) { $extra = @('+sv_lan', '0') }
& "$PSScriptRoot\start-server.ps1" -Map $Map -Extra $extra
