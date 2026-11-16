@echo off
rem Compiles the installer from source with Windows' own C# compiler (.NET Framework 4, C# 5).
rem Used by install.cmd and uninstall.cmd, which delete bin\GarRevival.Installer.exe after use.
set "SRC=%~dp0installer\Program.cs"
if not exist "%SRC%" set "SRC=%~dp0src\Patcher\Program.cs"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo The C# compiler that comes with Windows ^(.NET Framework 4^) was not found.
  exit /b 1
)
"%CSC%" /nologo /optimize+ /target:exe "/out:%~dp0bin\GarRevival.Installer.exe" "/reference:%~dp0bin\Mono.Cecil.dll" "%SRC%"
if errorlevel 1 (
  echo Could not compile the installer.
  exit /b 1
)
exit /b 0
