# Host/join test: two scripted game instances (profiles h and c); restores the user's battle.cfg afterwards.
#   .\tools\testrun2.ps1 -HostAutorun <file> -ClientAutorun <file> [-Battle <file>] [-Build] [-Seconds 150]
#   -Dedicated [-Headless] runs the host as a dedicated server (-garserver), with -ServerCfg as its dedicated.cfg
param([string]$HostAutorun, [string]$ClientAutorun, [string]$Battle, [switch]$Build, [int]$Seconds = 150, [switch]$Dedicated, [string]$ServerCfg, [switch]$Headless)
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
Remove-Item (Join-Path $root 'shot_*.png') -ErrorAction SilentlyContinue
$procs = @()
foreach ($pair in @(@('h', $HostAutorun), @('c', $ClientAutorun))) {
    $name = $pair[0]
    Remove-Item (Join-Path $root "revival-$name.log") -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $root "save-$name") -Recurse -Force -ErrorAction SilentlyContinue
    if ($pair[1]) { Copy-Item $pair[1] (Join-Path $root "autorun-$name.txt") -Force }
    $gameArgs = @('-garprofile', $name)
    if ($Dedicated -and $name -eq 'h') {
        $gameArgs += '-garserver'
        if ($Headless) { $gameArgs += '-batchmode', '-nographics' }
        if ($ServerCfg) { Copy-Item $ServerCfg (Join-Path $root 'dedicated.cfg') -Force }
        if ($Battle) { Copy-Item $Battle (Join-Path $root 'dedicated-battle.cfg') -Force }
    }
    $procs += Start-Process (Join-Path $game 'GAR.exe') -ArgumentList $gameArgs -WorkingDirectory $game -PassThru
    Start-Sleep -Seconds 3
}
Start-Sleep -Seconds $Seconds
foreach ($p in $procs) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -Confirm:$false } }
Remove-Item (Join-Path $root 'autorun-h.txt'), (Join-Path $root 'autorun-c.txt') -ErrorAction SilentlyContinue
if ($Dedicated) { Remove-Item (Join-Path $root 'dedicated.cfg'), (Join-Path $root 'dedicated-battle.cfg') -ErrorAction SilentlyContinue }
if (Test-Path $backup) { Copy-Item $backup $cfg -Force; Remove-Item $backup -Force }
Write-Host "test run finished"
