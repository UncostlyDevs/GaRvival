# Runs one scripted game instance on a throwaway profile.
#   .\tools\testrun.ps1 -Autorun <file> [-Battle <battle.cfg text file>] [-Build] [-Seconds 200] [-Profile t]
# Closes any running game first; restores the user's battle.cfg afterwards.
param([string]$Autorun, [string]$Battle, [switch]$Build, [int]$Seconds = 200, [string]$Profile = 't', [switch]$KeepSave)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$game = Split-Path $root -Parent
Get-Process GAR -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false
Start-Sleep -Seconds 2
if ($Build) {
    & (Join-Path $root 'build.ps1') | Select-Object -Last 1
    & (Join-Path $root 'bin\GarRevival.Patcher.exe') $game | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { throw "patcher failed" }
}
$cfg = Join-Path $root 'battle.cfg'
$backup = Join-Path $root 'battle.cfg.user'
if ($Battle) {
    if (-not (Test-Path $backup)) { Copy-Item $cfg $backup -Force }
    Copy-Item $Battle $cfg -Force
}
Remove-Item (Join-Path $root "revival-$Profile.log") -ErrorAction SilentlyContinue
Remove-Item (Join-Path $root 'shot_*.png') -ErrorAction SilentlyContinue
if (-not $KeepSave) { Remove-Item (Join-Path $root "save-$Profile") -Recurse -Force -ErrorAction SilentlyContinue }
Copy-Item $Autorun (Join-Path $root "autorun-$Profile.txt") -Force
$p = Start-Process (Join-Path $game 'GAR.exe') -ArgumentList '-garprofile', $Profile -WorkingDirectory $game -PassThru
$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline -and -not $p.HasExited) { Start-Sleep -Seconds 2 }
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -Confirm:$false }
Remove-Item (Join-Path $root "autorun-$Profile.txt") -ErrorAction SilentlyContinue
if (Test-Path $backup) { Copy-Item $backup $cfg -Force; Remove-Item $backup -Force }
Write-Host "test run finished"
