# Builds the mod and packs what players download: dist\GARRevival-<version>.zip.
# Players extract it into the Guns and Robots folder and run GARRevival\install.cmd.
#   .\tools\make-release.ps1 -Version 0.1.0
# The zip holds only GAR Revival's own files, and no installer program: the
# installer ships as source (installer\Program.cs) and install.cmd compiles it
# with the C# compiler that comes with Windows. The packing fails if any file
# of the game would end up in it. SHA256SUMS.txt and BUILDINFO.txt let anyone
# check the files (tools\verify-release.ps1 rebuilds and compares them).
param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$game = Split-Path $root -Parent

& (Join-Path $root 'build.ps1') | Select-Object -Last 1

$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'stage'
$mod = Join-Path $stage 'GARRevival'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $mod 'bin'), (Join-Path $mod 'installer') | Out-Null

foreach ($f in 'GarRevival.dll', 'Mono.Cecil.dll') { Copy-Item (Join-Path $root "bin\$f") (Join-Path $mod 'bin') }
Copy-Item (Join-Path $root 'src\Patcher\Program.cs') (Join-Path $mod 'installer\Program.cs')
$topFiles = 'README.md', 'THIRD-PARTY-NOTICES.txt', 'install.cmd', 'uninstall.cmd', 'build-installer.cmd', 'start-server.cmd', 'stop-server.cmd'
foreach ($f in $topFiles) { Copy-Item (Join-Path $root $f) $mod }
Set-Content -Path (Join-Path $mod 'VERSION.txt') -Value "GAR Revival $Version" -Encoding ascii

# Nothing of the game's may be in a release: compare every file against the
# game's own files by content and by name.
$gameHashes = @{}
Get-ChildItem (Join-Path $game 'GAR_Data\Managed') -File | ForEach-Object { $gameHashes[(Get-FileHash $_.FullName).Hash] = $_.Name }
foreach ($f in @('GAR.exe', 'steam_api.dll')) { $p = Join-Path $game $f; if (Test-Path $p) { $gameHashes[(Get-FileHash $p).Hash] = $f } }
$gameNames = (Get-ChildItem (Join-Path $game 'GAR_Data\Managed') -File).Name | Where-Object { $_ -ne 'GarRevival.dll' -and $_ -notlike '*.garrevival-orig' }
Get-ChildItem $stage -Recurse -File | ForEach-Object {
    $hash = (Get-FileHash $_.FullName).Hash
    if ($gameHashes.ContainsKey($hash) -and $_.Name -ne 'GarRevival.dll') { throw "$($_.Name) is a game file ($($gameHashes[$hash])); not packing it" }
    if ($gameNames -contains $_.Name) { throw "$($_.Name) has the name of a game file; not packing it" }
}

# How it was built, so a rebuild can be compared byte for byte.
$commit = (& git -C $root rev-parse HEAD 2>$null)
$dirty = (& git -C $root status --porcelain 2>$null)
$sdk = (& dotnet --list-sdks | Select-Object -Last 1)
$info = @(
    "GAR Revival $Version",
    "Source commit: $commit$(if ($dirty) { ' (with uncommitted changes)' })",
    "GarRevival.dll built by build.ps1 with the .NET SDK $sdk (Roslyn, /deterministic):",
    "  the same commit built with the same SDK version gives the same bytes.",
    "Mono.Cecil.dll is the unchanged lib\net40 file of the NuGet package Mono.Cecil 0.11.6.",
    "installer\Program.cs is src\Patcher\Program.cs of the same commit."
)
Set-Content -Path (Join-Path $mod 'BUILDINFO.txt') -Value $info -Encoding ascii

$sums = Get-ChildItem $mod -Recurse -File | Sort-Object FullName | ForEach-Object {
    (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower() + '  ' + $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
}
Set-Content -Path (Join-Path $mod 'SHA256SUMS.txt') -Value $sums -Encoding ascii

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
Write-Host "SHA-256 of the zip: $((Get-FileHash $zip -Algorithm SHA256).Hash.ToLower())"
