# PlayStation 2 Cheat Patcher

Program to embed `.pnach` files into PS2 game ELFs.

## Features
- **Permanently patch cheat into the game**
- **Support `word` and `extended` cheat type**
- **Protect Games Mastercode**
- **Will extend ELF's if some cheats requires it**
- **Exports cheats that can't be patched directly**
- **Works with raw ELFs or full ISOs**
- **Works on any PS2 game**

## How to Use

### GUI

1. Open **CheatPatcherGui**.
2. Browse and select your ELF or ISO file.
3. Choose an output location.
4. Add your `.pnach` file(s).
5. (Optional) Enter a CodeBreaker mastercode.
6. Click **Patch**.

### Console — ELF Mode

1. Run **CheatPatcher**.
2. Enter the ELF path.
3. Enter the output path.
4. (Optional) Enter a mastercode.
5. Enter your `.pnach` path(s).

### Console — ISO Mode

1. Run **CheatPatcher** and enter the ISO path.
2. Enter an output folder.
3. Enter the ELF filename inside the ISO (e.g. `SLXX_XXX.XX`).
4. (Optional) Enter a mastercode.
5. Enter your `.pnach` path(s).
6. Rebuild the ISO with **CD-DVD GenTool** + `iml2iso` or just use imgburn, then test in PCSX2 first.

## Notes
- Some cheats can't be patched directly and are exported separately instead.
- Always test on PCSX2 before using on real hardware.

## Building

```bash
dotnet publish CheatPatcher -c Release -r win-x64
dotnet publish CheatPatcher.Gui -c Release -r win-x64
```