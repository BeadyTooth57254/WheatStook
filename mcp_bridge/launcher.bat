@echo off
setlocal
cd /d "%~dp0"

rem ==== BRIDGE PORT: change this one line to move the whole stack ====
rem (server and client both follow it; then update operit + the mod's
rem  OperitBridgeUrl to the same number)
set "BRIDGE_PORT=14159"
set "LAN_IP=192.168.100.236"

set "TOKEN="
if exist token.txt set /p TOKEN=<token.txt
if "%TOKEN%"=="" (
  echo [x] No token yet. Double-click gen_token.bat first.
  pause
  exit /b 1
)

echo.
echo   [REMINDER] Make sure Stardew Valley is ALREADY running and you have
echo              loaded your save (you are IN the farm), then continue.
echo              If the game is not up yet, the client will just keep
echo              retrying http://localhost:58331 and stay disconnected.
echo.
echo   Press any key once the game is ready...
pause >nul

echo.
echo Starting WheatStook MCP bridge and tunnel client (2 windows)...
echo.

start "WheatStook MCP bridge (%BRIDGE_PORT%)" cmd /k "set WHEATSTOOK_BRIDGE_TOKEN=%TOKEN% && set PORT=%BRIDGE_PORT% && python server.py"
start "WheatStook tunnel client" cmd /k "set WHEATSTOOK_BRIDGE_TOKEN=%TOKEN% && set WHEATSTOOK_BRIDGE_URL=ws://127.0.0.1:%BRIDGE_PORT%/tunnel && set WHEATSTOOK_GAME_URL=http://localhost:58332 && python client.py"

echo.
echo   Phone operit  -  streamable HTTP  -  http://%LAN_IP%:%BRIDGE_PORT%/mcp
echo   Auth Bearer token = %TOKEN%
echo   Keep BOTH windows open while you play. This launcher can stay closed.
echo.
echo   If Windows ever reports a port conflict, change BRIDGE_PORT at the
echo   top of this file and update operit + OperitBridgeUrl to match.
echo.
pause
