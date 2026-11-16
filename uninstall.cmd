@echo off
rem Puts the game's original files back. Your GAR Revival saves stay in this folder.
rem Compiles the installer from installer\Program.cs first, like install.cmd.
call "%~dp0build-installer.cmd" || goto end
"%~dp0bin\GarRevival.Installer.exe" --uninstall "%~dp0.."
del "%~dp0bin\GarRevival.Installer.exe" >nul 2>&1
echo.
echo You can now delete the GARRevival folder if you don't want your saves.
:end
pause
