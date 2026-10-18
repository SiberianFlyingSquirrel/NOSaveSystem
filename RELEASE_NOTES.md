# NOSaveSystem 1.0.1 - release notes

Version **1.0.1**, build **4**. Log fingerprint: `FINGERPRINT name=NOSaveSystem version=1.0.1 build=4 ... kind=release (portable default SaveDirectory)`.
Singleplayer only. Requires BepInEx 5.4.23.x.

## Checksums (SHA-256)

| File | Size (bytes) | SHA-256 |
|---|---|---|
| `BepInEx/plugins/NOSaveSystem.dll` | 427520 | `C99DB776471B2C8BBE9B03C2CAC7F1EE2893B2D30698569A9AA0E6E55253D212` |

Verify with `Get-FileHash <file> -Algorithm SHA256` (see README, "Verifying"). The DLL is a deterministic build (`-c Release -p:DebugType=none -p:Deterministic=true -p:PathMap=<projdir>=.`), so building `src/` per the README reproduces the same DLL hash.

## Changes since 1.0.0

- Source: all code comments removed from `src/` and `tests/` (no behavior change; the compiled output is unaffected).
- Fix (N1): the main-menu SAVE MENU button no longer shifts the game's own main-menu buttons a second time when the button is re-created under the same parent (after an external destroy, or when a fresh clone is rejected). The previous shift is undone before the state is reset, and undoing is idempotent and safe when sibling buttons were destroyed.
- Fix (N2): every restore (F6 / SAVE MENU LOAD, main-menu Continue / CONTINUE (LATEST), Swap fallback, legacy host Continue) now starts a new restore sequence, and the post-load throttle FOLLOW check (with its 1 s raise-only STUCK throttle fix) runs once per aircraft per restore. Before, a 2nd or later Swap-fallback load on the same aircraft could skip it.
- Config: the `AllowRespawnMissing` description no longer says "Multiplayer-safe: only when IsServer"; it now states that the mod is singleplayer-only.
- Build: `src/NOSaveSystem.csproj` sets `IncludeSourceRevisionInInformationalVersion=false`, so building from a git clone gives the same DLL as building from a plain source copy (the .NET 8 SDK otherwise appends `+<commit>` to the informational version inside the DLL). Fixed: with 1.0.0 a clone build did not reproduce the published hash.
- Fix: a new restore of an aircraft whose engine hold had already ended (or been released) in an earlier restore now re-arms the engine hold and the throttle FOLLOW check for that restore, instead of logging `LATE-WRITE blocked`. Late writes inside the same restore stay blocked. An older restore's still-running lock on that aircraft is retired first (log: `RE-ARM`).
- Fix: the delayed pilot/engine init of a restore (waits up to 5 s for the pilot) no longer applies its save data after a newer restore has started; it logs `PilotFix stale restore seq=X now=Y, skip` instead, so the newer restore's engine hold is armed from its own data.
- Fix: the main-menu SAVE MENU button (and main-menu Continue / F8 / AutoContinue) also works when the game's main menu never switches to the Menu game state, which happens when the game starts without the Steam client running (`MainMenu.StartAsync` stops at `SteamAPI_Init() failed`). The mod now treats a loaded main menu with game state `Uninitialized` as the main menu, and logs `main-menu SAVE MENU button waiting (gameState=... mainMenuState=...)` once while it waits.
- Restore plan file: the header line is now `F6 restore plan mode=...` (no stale version label).
- Tests: new FOLLOW once-per-restore key tests (U11), engine re-arm decision tests (U12) and PilotFix stale-restore tests (U13).

## Zip contents

- `BepInEx/plugins/NOSaveSystem.dll`
- `README.md`
- `LICENSE`

## Notes

- The throttle hold/blend/seed math and the FOLLOW once-per-restore key live in the Unity-free `src/NoSaveThrottleMath.cs` and are covered by the synthetic tests in `tests/` (not shipped).

## Known issues

- The first-frame position check in `NOSAVE-VERIFY` (1 m tolerance) can report `VERIFY FAIL` for an aircraft that the game snaps to the ground on load. Log line only; the restored state is not affected.
- Main-menu button placement for menu layouts without a layout group (manual placement) has not been tested in game.
- The sound re-wiring after a load uses reflection on game internals and may need an update after a game patch.
- Pad / axis throttle source detection has only been tested with synthetic input (no HOTAS / gamepad available for testing).
- A throttle lever resting within +/-0.5 of its centre and not moved since the load keeps the saved throttle until it is moved (game behaviour).

## Provenance

Branch `1.0.1` of this repository, based on tag `v1.0.0` (commit `4ff8518`): one commit that only removes comments, one commit with the first fixes and the version bump (build 1), one commit with the engine re-arm fix, the plan header label and build 2, one commit with the PilotFix stale-restore fix and build 3, and one commit with the main-menu state fix and build 4.
