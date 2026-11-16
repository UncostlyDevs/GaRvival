# Compile check only: builds src\GarRevival\*.cs plus any extra .cs files or
# folders given as arguments into a throwaway DLL. Nothing is installed.
#   .\tools\check.ps1 src\Staging\Workshop
param([string[]]$Extra = @())
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$refs = Join-Path $root 'build\refs'
$refNames = 'mscorlib','System','System.Core','System.Xml','UnityEngine','Assembly-CSharp','Assembly-CSharp-firstpass','JunkWars','Photon3Unity3D','protobuf-net'
$sdkLine = (& dotnet --list-sdks | Select-Object -Last 1)
if ($sdkLine -notmatch '^(\S+) \[(.+)\]$') { throw "No .NET SDK found" }
$csc = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
$out = Join-Path ([IO.Path]::GetTempPath()) ("garrevival-check-" + [Guid]::NewGuid().ToString('N') + ".dll")
$cscArgs = @('/noconfig', '/nostdlib+', '/target:library', '/langversion:14', '/optimize+', '/nologo', "/out:$out")
$cscArgs += $refNames | ForEach-Object { "/reference:$(Join-Path $refs "$_.dll")" }
$cscArgs += Get-ChildItem (Join-Path $root 'src\GarRevival') -Filter *.cs | ForEach-Object { $_.FullName }
foreach ($item in $Extra) {
    $path = if ([IO.Path]::IsPathRooted($item)) { $item } else { Join-Path $root $item }
    if (Test-Path $path -PathType Container) { $cscArgs += Get-ChildItem $path -Filter *.cs | ForEach-Object { $_.FullName } }
    else { $cscArgs += $path }
}
$output = & dotnet $csc @cscArgs
$code = $LASTEXITCODE
$output | Write-Host
Remove-Item $out -ErrorAction SilentlyContinue
if ($code -ne 0) { Write-Host "CHECK FAILED"; exit 1 }
Write-Host "CHECK OK"
