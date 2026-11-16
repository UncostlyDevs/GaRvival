# Builds GarRevival.dll and the patcher into .\bin
# Needs the .NET SDK (8 or newer) and a Guns and Robots install one folder up.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$managed = Join-Path (Split-Path $root -Parent) 'GAR_Data\Managed'
$bin = Join-Path $root 'bin'
$refs = Join-Path $root 'build\refs'
New-Item -ItemType Directory -Force $bin, $refs | Out-Null

# Compile against the game's original assemblies, not patched copies.
$refNames = 'mscorlib','System','System.Core','System.Xml','UnityEngine','Assembly-CSharp','Assembly-CSharp-firstpass','JunkWars','Photon3Unity3D','protobuf-net'
foreach ($name in $refNames) {
    $src = Join-Path $managed "$name.dll"
    if (Test-Path "$src.garrevival-orig") { $src = "$src.garrevival-orig" }
    Copy-Item $src (Join-Path $refs "$name.dll") -Force
}

$sdkLine = (& dotnet --list-sdks | Select-Object -Last 1)
if ($sdkLine -notmatch '^(\S+) \[(.+)\]$') { throw "No .NET SDK found" }
$csc = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'

$cscArgs = @('/noconfig', '/nostdlib+', '/target:library', '/langversion:14', '/optimize+', '/deterministic', '/nologo',
             "/out:$(Join-Path $bin 'GarRevival.dll')")
$cscArgs += $refNames | ForEach-Object { "/reference:$(Join-Path $refs "$_.dll")" }
$cscArgs += Get-ChildItem (Join-Path $root 'src\GarRevival') -Filter *.cs | ForEach-Object { $_.FullName }
$cscOutput = & dotnet $csc @cscArgs
if ($LASTEXITCODE -ne 0) { $cscOutput | Write-Host; throw "GarRevival.dll failed to compile" }

& dotnet build (Join-Path $root 'src\Patcher\Patcher.csproj') -c Release -o $bin --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Patcher failed to build" }

Write-Host "Built into $bin"
