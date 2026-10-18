# NOSaveSystem v1.0.1

**Singleplayer only - automatically inactive in multiplayer**

Mid-mission save/load for Nuclear Option. Saves a snapshot of the running mission (units, positions, velocities, fuel, damage, loadouts, faction funds/score, escalation, objective state) and restores it later, including after a game restart.
## Requirements

- A legitimate Steam copy of Nuclear Option
- BepInEx 5.4.23.x installed in the game folder

## Install

[Download the latest release](https://github.com/SiberianFlyingSquirrel/NOSaveSystem/releases/latest) and grab the `.zip` under Assets. Extract the zip into the game folder (e.g. `C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option`). This places the mod at `BepInEx/plugins/NOSaveSystem.dll`.
## Usage

The only NOSave UI is the **SAVE MENU** (pause menu and main menu). 

### In a mission (pause menu)

Press ESC to open the pause menu. A **SAVE MENU** button is added directly under the game's Exit/Quit button. It opens the **SAVE MENU** list (scrollable; the footer shows the number of saves):

- each entry shows the save's timestamp, mission and aircraft;
- **LOAD** restores that save (the mission is re-entered, then the snapshot is applied). If the save cannot be used (damaged file, mission not installed) the load is aborted with a message before anything is torn down;
- **DELETE** removes it (asks for confirmation);
- **NEW SAVE** writes a new snapshot; **CLOSE** (or ESC) closes the list.

### On the main menu

A **SAVE MENU** button (same look as the game's own main-menu buttons) is added under MISSIONS. It opens the same list:

- **LOAD** starts an offline singleplayer session, loads the saved mission (normal loading screen) and applies the snapshot;
- **DELETE** removes a save (asks for confirmation);
- **CONTINUE (LATEST)** loads the latest save in one click (what the old Continue box did).

### After a load

The game opens the pause menu on the first frame of the loaded mission, before anything has moved (no simulation step runs before the pause). Resume (ESC / Resume) to continue from exactly the saved state.

### Hotkeys (defaults, rebindable in the config; none of them draws any UI)

- **F5** - save a mid-mission snapshot
- **F6** - load the latest snapshot, or, if you loaded a save from the SAVE MENU list, that save again (until you make a new save)
- **F8** - on the main menu: continue the latest mid-mission save (same as SAVE MENU > CONTINUE (LATEST))

All save/load/delete actions and hotkeys are no-ops, and all NOSave UI is hidden, outside a locally hosted singleplayer session and during mission-editor test play (no config option changes this); main-menu loads only work from a main menu that can start a singleplayer mission and always start an offline singleplayer session.

### What is restored in flight

- Engines resume at their saved RPM/power/thrust from the first frame after loading (no spool-up). The saved state is held until the engine simulation, left running on its own for a short probe, stays at the saved state (at most 3 s; AI aircraft 1.5 s). The saved throttle is held during this time; keyboard throttle keys pressed during the hold are added on top and take over after a 0.75 s blend. 
- An engine that was destroyed when you saved stays dead from the first frame (no spool-up and die), the other engines keep their full power.
- Landing gear and flaps that were moving when you saved continue moving from the same point.
- After every load a `NOSAVE-VERIFY ... VERIFY PASS n/n` (or `VERIFY FAIL k/n`) line in the BepInEx log compares the live game against the save, field by field.
- The BepInEx log also shows a `NOSAVE-ENG ... RESULT PASS/FAIL/SKIP` line per aircraft and a `FINGERPRINT` line (version, build, SHA-256) at startup.

### Config and save location

Config file: `BepInEx/config/com.lorenzo.nosavesystem.cfg` (created on first launch)

- Saves are written to `BepInEx/NOSaveSystem/saves/` by default (`midmission_autosave.json` = latest, `archive/` = the save list). Change it with `[Paths] SaveDirectory`.
- If you used an earlier build, your existing .cfg keeps its old `SaveDirectory` value, so your old saves keep working; delete that line to switch to the new default.
- Other key settings: `[Hotkeys] SaveKey / LoadKey / ContinueKey`, `[Load] LoadMode` (MissionReload / Swap), `[Load] PauseAfterLoad`, `[UI] ShowHudFeedback`. See the comments in the generated .cfg for all options.
- The legacy `RestoreMode=Teleport` was removed in v0.10.0; any value now uses Swap.
## Input devices

Tested with keyboard and mouse; HOTAS, joystick and gamepad are supported by design via the game's input system - please report issues.

Every device reaches the throttle through the game's Rewired "Throttle" action, so the mod sees keyboard, HOTAS, joystick and gamepad input the same way the game does.

## Singleplayer only

The mod only works in a locally hosted singleplayer session. If another player joins, all NOSave UI is hidden and save/load/delete are blocked; there is no config option or hotkey to bypass this.

## Building from source

Requires the .NET SDK (8.x tested). The plugin project is `src/NOSaveSystem.csproj`. References are resolved from your game install via the `GameDir` MSBuild property (defaults to the standard Steam path; only reference DLLs are read):

```
dotnet build -c Release -p:DebugType=none -p:Deterministic=true -p:PathMap=<projdir>=. -p:GameDir=<game folder>
```

For example, in PowerShell from the repository root:

```powershell
cd src
dotnet build NOSaveSystem.csproj -c Release -p:DebugType=none -p:Deterministic=true "-p:PathMap=$((Get-Location).Path)=." -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option"
Get-FileHash .\bin\NOSaveSystem.dll -Algorithm SHA256
```

The build is deterministic: with the same SDK and game version, `src\bin\NOSaveSystem.dll` has the SHA-256 listed in `RELEASE_NOTES.md`.

### Tests

`tests/` holds a synthetic test harness for the Unity-free throttle math (`src/NoSaveThrottleMath.cs`, linked, not copied). It is a separate console project, not part of the plugin build or the release zip, and needs no game files or NuGet packages:

```
dotnet run -c Release --project tests/NOSaveSystem.Tests.csproj
```

## Verifying

1. **Log fingerprint:** start the game and open `BepInEx\LogOutput.log`. The `FINGERPRINT` line of this release reads (`editorFlag` depends on the game build):

   ```
   [Info   :NOSaveSystem] FINGERPRINT name=NOSaveSystem version=1.0.1 build=3 config=Release sha256=76a60e0c9d9b6ddd1ac9b87100af301321b803bc5cb30143170c58bad7b1c90e kind=release (portable default SaveDirectory) file=NOSaveSystem.dll spLock=SinglePlayer+localServer+IsHost+LocalPlayer+AllPlayers<=1+!editor+mpLatch (no config bypass) editorFlag=...
   ```

   Check `version=1.0.0 build=1`, `kind=release` and the `sha256=` value (the hash of the DLL that was actually loaded).

2. **DLL hash:** in PowerShell, in the game folder:

   ```powershell
   Get-FileHash .\BepInEx\plugins\NOSaveSystem.dll -Algorithm SHA256
   ```

   Expected: `76A60E0C9D9B6DDD1AC9B87100AF301321B803BC5CB30143170C58BAD7B1C90E` (also listed in `RELEASE_NOTES.md`).

## License

MIT (see `LICENSE`). Covers this mod's own code only; no game assets or game code are included.

## Built with AI assistance

This mod was developed with the help of AI coding tools.

## Bugs

Please report any bugs to me so they can be squashed.

## Disclaimer

This project is an unofficial community modification and is not affiliated with, sponsored by, or endorsed by Shockfront Studios Pty Ltd. Original Nuclear Option assets, vehicle designs, audio, and code are Copyright (c) 2026 Shockfront Studios Pty Ltd. All rights reserved. Nuclear Option and Shockfront Studios are trademarks or registered trademarks of Shockfront Studios Pty Ltd. Original mod content and all other trademarks belong to their respective owners.
