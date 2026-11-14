# Builds the mod and packs what players download: dist\GARRevival-<version>.zip.
# Players extract it into the Guns and Robots folder and run GARRevival\install.cmd.
#   .\tools\make-release.ps1 -Version 0.1.0
# The zip holds only GAR Revival's own files; it fails if any file of the game
# would end up in it.
param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$game = Split-Path $root -Parent

& (Join-Path $root 'build.ps1') | Select-Object -Last 1

$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'stage'
$mod = Join-Path $stage 'GARRevival'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $mod 'bin') | Out-Null

$binFiles = 'GarRevival.dll', 'GarRevival.Patcher.exe', 'GarRevival.Patcher.exe.config', 'Mono.Cecil.dll'
foreach ($f in $binFiles) { Copy-Item (Join-Path $root "bin\$f") (Join-Path $mod 'bin') }
$topFiles = 'README.md', 'THIRD-PARTY-NOTICES.txt', 'install.cmd', 'uninstall.cmd', 'start-server.cmd', 'stop-server.cmd'
foreach ($f in $topFiles) { Copy-Item (Join-Path $root $f) $mod }
Set-Content -Path (Join-Path $mod 'VERSION.txt') -Value "GAR Revival $Version" -Encoding ascii

# Nothing of the game's may be in a release: compare every file against the
# game's own files by content.
$gameHashes = @{}
Get-ChildItem (Join-Path $game 'GAR_Data\Managed') -File | ForEach-Object { $gameHashes[(Get-FileHash $_.FullName).Hash] = $_.Name }
foreach ($f in @('GAR.exe', 'steam_api.dll')) { $p = Join-Path $game $f; if (Test-Path $p) { $gameHashes[(Get-FileHash $p).Hash] = $f } }
$gameNames = (Get-ChildItem (Join-Path $game 'GAR_Data\Managed') -File).Name | Where-Object { $_ -ne 'GarRevival.dll' -and $_ -notlike '*.garrevival-orig' }
Get-ChildItem $stage -Recurse -File | ForEach-Object {
    $hash = (Get-FileHash $_.FullName).Hash
    if ($gameHashes.ContainsKey($hash) -and $_.Name -ne 'GarRevival.dll') { throw "$($_.Name) is a game file ($($gameHashes[$hash])); not packing it" }
    if ($gameNames -contains $_.Name) { throw "$($_.Name) has the name of a game file; not packing it" }
}

$zip = Join-Path $dist "GARRevival-$Version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
# Entries are written by hand: Windows PowerShell's ZipFile names them with
# backslashes, which some unzip tools do not treat as folders.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $entry = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry)
    }
}
finally { $archive.Dispose() }
Remove-Item $stage -Recurse -Force
$size = [math]::Round((Get-Item $zip).Length / 1KB)
Write-Host "Release: $zip ($size KB)"
