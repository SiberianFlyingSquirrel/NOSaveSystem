# NOSaveSystem 1.0.0 - release notes

Version **1.0.0**, build **1**. Log fingerprint: `FINGERPRINT name=NOSaveSystem version=1.0.0 build=1 ... kind=release (portable default SaveDirectory)`.
Singleplayer only. Requires BepInEx 5.4.23.x.

## Checksums (SHA-256)

| File | Size (bytes) | SHA-256 |
|---|---|---|
| `BepInEx/plugins/NOSaveSystem.dll` | 425472 | `EBB1B1A03679A7B1363A1ED52E83AABD96DB535CD353D895B401B0215C3F243F` |
| `NOSaveSystem-1.0.0.zip` | 175075 | `66185517AED24FB88CCBB7E418FBB316091BC065C2943B745FA58AC5543F62BB` |

Verify with `Get-FileHash <file> -Algorithm SHA256` (see README, "Verifying"). The DLL is a deterministic build (`-c Release -p:DebugType=none -p:Deterministic=true -p:PathMap=<projdir>=.`), so building `src/` per the README reproduces the same DLL hash.

## Zip contents

- `BepInEx/plugins/NOSaveSystem.dll`
- `README.md`
- `LICENSE`

## Notes

- The throttle hold/blend/seed math lives in the Unity-free `src/NoSaveThrottleMath.cs` and is covered by the synthetic tests in `tests/` (not shipped).

## Known issues (fix planned for 1.0.1)

- N1 (LOW): a main-menu clone re-made under the same parent can re-shift sibling objects. Rare; only after an external destroy. Fix planned for 1.0.1.
- N2 (LOW): the 2nd and later swap-fallback loads on the same aircraft skip the FOLLOW check and its 1 s raise-only STUCK throttle fix (fallback path only). Fix planned for 1.0.1.
- The `AllowRespawnMissing` config description says "Multiplayer-safe/IsServer", but the mod is singleplayer-only and gated. Description fix planned for 1.0.1.

## Provenance

The source in `src/` is the frozen 1.0.0 build 1 source. Its content equals commit `49ab7396ddd46cb9fd85cdeedb1b9b199f14c639` of the developer's private work repository (no remote; parent `dcc6f865616a6e167f7199514592cf9db4668a40`, diff = `PLUGIN_VERSION` and csproj `Version` only), plus the portable default SaveDirectory in `NOSaveSystem.cs` / `MissionReload.cs`.
Source tree hash (SHA-256 over the ordinally sorted lines `<file>\t<lowercase sha256>` of the 21 files in `src/`, joined with LF): `dbcab15e0a690f0f1a8264778add708d49334732b12e0c8a92d4cd05222cff9f`.
This repository is a fresh history that starts at that freeze; earlier development history is not included.