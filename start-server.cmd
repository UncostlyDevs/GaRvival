@echo off
rem GAR Revival dedicated server: hosts matches without playing in them.
rem Settings are in dedicated.cfg (hosting, start rules, rewards) and
rem dedicated-battle.cfg (map, mode, bots); both are created on the first start.
rem The log is revival-server.log. Stop it with stop-server.cmd.
rem To watch the server in a window instead, remove "-batchmode -nographics".
cd /d "%~dp0.."
rem Lets GAR.exe start without going through the Steam client (Steam must be running).
if not exist steam_appid.txt echo 293540> steam_appid.txt
start "" GAR.exe -garserver -batchmode -nographics
echo GAR Revival server started. Log: %~dp0revival-server.log
