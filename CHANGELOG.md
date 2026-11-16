## 0.2.0

- You can now keep chosen mods out of the budget fix. `2 - Apply budget fix.bat` shows a numbered list of the mods it would touch. Type the numbers of the ones to leave alone (for example `1,3`), or press Enter to trim them all.
- Your choice can be remembered in `White Honey - Excluded mods.txt` next to the tool. You can edit it in Notepad: one mod per line, `*` matches anything.
- The scan now tells you when an applied fix is out of date because you have since excluded a mod. Run `2 - Apply budget fix.bat` again to rebuild it.

## 0.1.0

- First release.
- Scan your texture and mesh mods and see which ones use the most VRAM.
- Optional budget fix: one extra mod folder with half-size copies of only the heaviest textures, removable with `Undo budget fix.bat`.
- Measure the game's real VRAM while you play, to compare before and after.
