@echo off
rem Scans your mods, shows what it would shrink (count, size, time) and asks before writing anything.
rem The fix is ONE extra mod folder; your mods are never modified. Undo with "Undo budget fix.bat".
cd /d "%~dp0"
if not exist "%~dp0WhiteHoney.exe" goto :missing
echo This can add ONE new folder to your mods folder with half-size copies of your heaviest textures.
echo Your mods are not modified. It scans first, then shows what it would do and asks before writing.
echo.
"%~dp0WhiteHoney.exe" --make-budget-fix --ask --pause
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
