@echo off
rem Installs GAR Revival into the Guns and Robots folder this folder sits in.
rem Safe to run again, e.g. after Steam verified or updated the game.
rem The installer ships as source (installer\Program.cs, read it if you like): this compiles it
rem with the C# compiler that comes with Windows, runs it and deletes it again.
call "%~dp0build-installer.cmd" || goto failed
echo Installing GAR Revival...
"%~dp0bin\GarRevival.Installer.exe" "%~dp0.."
set RESULT=%ERRORLEVEL%
del "%~dp0bin\GarRevival.Installer.exe" >nul 2>&1
if not "%RESULT%"=="0" goto failed
echo.
echo Installed. Start Guns and Robots from Steam as usual.
pause
exit /b 0

:failed
echo.
echo Install failed. The GARRevival folder must be inside the Guns and Robots folder,
echo next to GAR.exe ^(Steam: right-click Guns and Robots, Manage, Browse local files^).
pause
exit /b 1
