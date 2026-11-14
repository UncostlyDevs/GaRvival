@echo off
rem Stops the GAR Revival dedicated server, leaving any game you are playing open.
powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='GAR.exe'\" | Where-Object { $_.CommandLine -like '*-garserver*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; 'Stopped the server (process ' + $_.ProcessId + ')' }"
