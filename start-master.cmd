@echo off
rem GAR Revival master server: server list, matchmaking and ladder for game servers.
rem Compiles master\MasterServer.cs with Windows' own C# compiler and runs it. Ctrl+C stops it.
rem Settings: master\master.cfg (made on the first start). Log: master\master.log.
rem --local: 127.0.0.1 only (how the game starts it when settings.cfg has an empty master=).
rem Linux/macOS with mono:
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
"%MASTER%\bin\GarRevival.Master.exe" "%MASTER%" %*
