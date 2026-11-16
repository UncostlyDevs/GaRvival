# Checks a release zip against this checkout (first: git checkout <commit from its BUILDINFO.txt>).
#   .\tools\verify-release.ps1 -Zip GARRevival-0.1.3.zip
# Needs the game one folder up (build.ps1 compiles against its assemblies) and the .NET SDK.
# Checks SHA256SUMS.txt, a byte-identical GarRevival.dll rebuild, the NuGet Mono.Cecil.dll,
# and that the installer, master and scripts match this checkout.
param([Parameter(Mandatory = $true)][string]$Zip)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$failed = 0
function Check([string]$what, [bool]$ok, [string]$detail) {
    Write-Host ("{0,-6} {1}{2}" -f $(if ($ok) { 'OK' } else { 'FAIL' }), $what, $(if ($detail) { " ($detail)" }))
    if (-not $ok) { $script:failed++ }
}
function Sha([string]$path) { (Get-FileHash $path -Algorithm SHA256).Hash.ToLower() }

$temp = Join-Path ([IO.Path]::GetTempPath()) ("garrevival-verify-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $temp | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path $Zip), $temp)
    $mod = Join-Path $temp 'GARRevival'
    Write-Host (Get-Content (Join-Path $mod 'BUILDINFO.txt') -Raw)

    # 1. The zip's own checksum list.
    foreach ($line in Get-Content (Join-Path $mod 'SHA256SUMS.txt')) {
        $hash, $name = $line -split '  ', 2
        $file = Join-Path $temp $name
        Check "checksum $name" ((Test-Path $file) -and (Sha $file) -eq $hash) $null
    }
    $listed = (Get-Content (Join-Path $mod 'SHA256SUMS.txt')).Count
    $present = (Get-ChildItem $mod -Recurse -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' }).Count
    Check "no files beyond the checksum list" ($listed -eq $present) "$present files, $listed listed"

    # 2. Rebuild the mod from this checkout and compare.
    & (Join-Path $root 'build.ps1') | Out-Null
    Check "GarRevival.dll rebuilt from source is identical" ((Sha (Join-Path $root 'bin\GarRevival.dll')) -eq (Sha (Join-Path $mod 'bin\GarRevival.dll'))) "needs the same .NET SDK version as BUILDINFO.txt; yours: $(& dotnet --list-sdks | Select-Object -Last 1)"

    # 3. Mono.Cecil from NuGet (build.ps1 restored it into the package cache).
    $nuget = Join-Path $env:USERPROFILE '.nuget\packages\mono.cecil\0.11.6\lib\net40\Mono.Cecil.dll'
    Check "Mono.Cecil.dll is the NuGet package's file" ((Test-Path $nuget) -and (Sha $nuget) -eq (Sha (Join-Path $mod 'bin\Mono.Cecil.dll'))) $nuget

    # 4. Installer source and scripts.
    Check "installer\Program.cs is src\Patcher\Program.cs" ((Sha (Join-Path $root 'src\Patcher\Program.cs')) -eq (Sha (Join-Path $mod 'installer\Program.cs'))) $null
    Check "master\MasterServer.cs is this checkout's" ((Sha (Join-Path $root 'master\MasterServer.cs')) -eq (Sha (Join-Path $mod 'master\MasterServer.cs'))) $null
    foreach ($f in 'install.cmd', 'uninstall.cmd', 'build-installer.cmd', 'start-server.cmd', 'stop-server.cmd', 'start-master.cmd') {
        Check "$f is this checkout's" ((Sha (Join-Path $root $f)) -eq (Sha (Join-Path $mod $f))) $null
    }
    $exes = Get-ChildItem $mod -Recurse -Include *.exe, *.bat, *.ps1, *.vbs, *.js
    Check "no programs or other scripts in the zip" ($exes.Count -eq 0) (($exes | ForEach-Object Name) -join ', ')
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host $(if ($failed -eq 0) { "All checks passed." } else { "$failed check(s) failed." })
exit $failed
