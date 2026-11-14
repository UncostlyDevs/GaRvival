@echo off
rem Puts the game's original files back. Your GAR Revival saves stay in this folder.
"%~dp0bin\GarRevival.Patcher.exe" --uninstall "%~dp0.."
echo.
echo You can now delete the GARRevival folder if you don't want your saves.
pause
