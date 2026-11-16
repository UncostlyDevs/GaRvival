@echo off
rem GAR Revival dedicated server: hosts matches without playing in them. Stop it with stop-server.cmd.
rem Settings: dedicated.cfg and dedicated-battle.cfg (made on the first start). Log: revival-server.log.
rem To watch it in a window, remove "-batchmode -nographics".
cd /d "%~dp0.."
rem Lets GAR.exe start directly, not through Steam (Steam must be running).
if not exist steam_appid.txt echo 293540> steam_appid.txt
start "" GAR.exe -garserver -batchmode -nographics
echo GAR Revival server started. Log: %~dp0revival-server.log
