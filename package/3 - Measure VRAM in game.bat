@echo off
rem Records the game's real VRAM use while you play. Run once before and once after a change.
cd /d "%~dp0"
if not exist "%~dp0WhiteHoney.exe" goto :missing
set "label="
set /p "label=Name this measurement (for example: before or after): "
if "%label%"=="" set "label=session"
echo.
echo Now start the game and load a save. Play for a minute or two, then close the game to finish.
"%~dp0WhiteHoney.exe" --live --label "%label%"
echo.
pause
goto :eof

:missing
echo.
echo WhiteHoney.exe is missing from this folder, so the tool can't start.
echo.
echo Most often your antivirus removed it. Open Windows Security,
echo "Virus & threat protection", then "Protection history". If WhiteHoney.exe
echo is listed, choose Actions, then Restore (or Allow on device), and double-click
echo this file again.
echo.
echo If it isn't listed, download the mod again and unpack the whole zip
echo into a normal folder, for example your Desktop.
echo.
pause
exit /b 1
