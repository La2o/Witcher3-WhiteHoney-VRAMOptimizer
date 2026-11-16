## 0.3.0

- New: a window. Double-click `WhiteHoneyUI.exe` to scan, pick mods, apply or undo the budget fix, open the report and measure VRAM with the mouse. The numbered `.bat` shortcuts and `WhiteHoney.exe` work as before.
- In the window, every mod has a "Fix may trim" checkbox. Untick a mod to keep it out of the fix. The choice is saved at once in the same `White Honey - Excluded mods.txt` the shortcuts use.
- The window shows what the fix would do before you apply it (how many textures, the VRAM saving, the size on disk), and warns when an applied fix is out of date.
- The window opens as an app window in Microsoft Edge (Chrome or your default browser if Edge is missing). The page comes from the tool on your own PC and nothing is sent anywhere. Closing the window closes the tool.

## 0.2.0

- You can now keep chosen mods out of the budget fix. `2 - Apply budget fix.bat` shows a numbered list of the mods it would touch. Type the numbers of the ones to leave alone (for example `1,3`), or press Enter to trim them all.
- Your choice can be remembered in `White Honey - Excluded mods.txt` next to the tool. You can edit it in Notepad: one mod per line, `*` matches anything.
- The scan now tells you when an applied fix is out of date because you have since excluded a mod. Run `2 - Apply budget fix.bat` again to rebuild it.

## 0.1.0

- First release.
- Scan your texture and mesh mods and see which ones use the most VRAM.
- Optional budget fix: one extra mod folder with half-size copies of only the heaviest textures, removable with `Undo budget fix.bat`.
- Measure the game's real VRAM while you play, to compare before and after.
