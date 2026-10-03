# Decompiles heroes/abilities vdata from the local install into data\ (gitignored) and regenerates AbilityPool.g.cs.
# Needs the small VRF reader (tools\vrf from the ClashArena project) - pass its vrf.dll path.
param([Parameter(Mandatory)][string]$Vrf, [string]$Deadlock = "C:\Program Files (x86)\Steam\steamapps\common\Deadlock")
if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet" }
$dn = "$env:DOTNET_ROOT\dotnet.exe"; $out = "$PSScriptRoot\..\data"; New-Item -ItemType Directory -Force $out | Out-Null
$pak = "$Deadlock\game\citadel\pak01_dir.vpk"
& $dn $Vrf cat $pak scripts/heroes.vdata_c | Out-File "$out\heroes.vdata" -Encoding utf8
& $dn $Vrf cat $pak scripts/abilities.vdata_c | Out-File "$out\abilities.vdata" -Encoding utf8
python "$PSScriptRoot\gen_pool.py" $out $Deadlock "$PSScriptRoot\..\AbilityDraft\AbilityPool.g.cs"
