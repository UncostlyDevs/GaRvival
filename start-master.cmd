@echo off
rem GAR Revival master server: the server list, matchmaking and ladder that
rem game servers register with. Compiles master\MasterServer.cs with the C#
rem compiler that is part of Windows (.NET Framework 4, C# 5) into
rem master\bin\GarRevival.Master.exe and runs it. Settings are in
rem master\master.cfg (created on the first start); the log is master\master.log.
rem Stop it with Ctrl+C. On Linux/macOS with mono:
rem   mcs -out:master/bin/GarRevival.Master.exe master/MasterServer.cs
rem   mono master/bin/GarRevival.Master.exe master
set "MASTER=%~dp0master"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo The C# compiler that comes with Windows ^(.NET Framework 4^) was not found.
  exit /b 1
)
if not exist "%MASTER%\bin" mkdir "%MASTER%\bin"
"%CSC%" /nologo /optimize+ /target:exe "/out:%MASTER%\bin\GarRevival.Master.exe" "%MASTER%\MasterServer.cs"
if errorlevel 1 (
  echo Could not compile the master server.
  exit /b 1
)
"%MASTER%\bin\GarRevival.Master.exe" "%MASTER%"
