@echo off
rem Installs GAR Revival into the Guns and Robots folder this folder sits in.
rem Safe to run again, for example after Steam has verified or updated the game.
echo Installing GAR Revival...
"%~dp0bin\GarRevival.Patcher.exe" "%~dp0.."
if errorlevel 1 (
  echo.
  echo Install failed. The GARRevival folder must be inside the Guns and Robots folder,
  echo next to GAR.exe ^(Steam: right-click Guns and Robots, Manage, Browse local files^).
) else (
  echo.
  echo Installed. Start Guns and Robots from Steam as usual.
)
pause
