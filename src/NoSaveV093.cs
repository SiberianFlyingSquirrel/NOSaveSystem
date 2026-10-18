using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.UI;
using UnityEngine;

namespace NOSaveSystem
{
    [Serializable]
    public class DatalinkDto
    {
        public string faction;
        public string uniqueName;
        public float x, y, z;
        public int attackers, missileAttacks; // v0.9.6 TrackingInfo counters
    }

    /// <summary>
    /// v0.9.3: datalink (FactionHQ.trackingDatabase) save/restore, suppress Join(faction select)+map popup
    /// during/after MissionReload, ensure saved player faction, pause (GameplayUI.PauseGame = ESC menu) after load.
    /// </summary>
    internal static class NoSaveV093
    {
        private static void Log(string m) { NOSaveSystemPlugin.Log?.LogInfo("v093: " + m); }

        // ---------------- Datalink ----------------
        internal static List<DatalinkDto> CaptureDatalink()
        {
            var list = new List<DatalinkDto>();
            int noName = 0, noUnit = 0;
            foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
            {
                if (hq == null || hq.trackingDatabase == null) continue;
                string fac = null;
                try { fac = hq.faction != null ? hq.faction.factionName : null; } catch { }
                if (string.IsNullOrEmpty(fac)) continue;
                foreach (var kv in hq.trackingDatabase)
                {
                    try
                    {
                        if (!UnitRegistry.TryGetUnit(kv.Key, out var unit) || unit == null) { noUnit++; continue; }
                        if (string.IsNullOrEmpty(unit.UniqueName)) { noName++; continue; }
                        var p = kv.Value != null ? kv.Value.lastKnownPosition : unit.GlobalPosition();
                        var dd = new DatalinkDto { faction = fac, uniqueName = unit.UniqueName, x = p.x, y = p.y, z = p.z };
                        if (kv.Value != null) { dd.attackers = kv.Value.attackers; dd.missileAttacks = kv.Value.missileAttacks; }
                        list.Add(dd);
                    }
                    catch { }
                }
            }
            Log("datalink captured=" + list.Count + " skippedNoUniqueName=" + noName + " skippedNoUnit=" + noUnit);
            return list;
        }

        internal static void RestoreDatalink(MidMissionSnapshot snap, StringBuilder plan)
        {
            var missing = new List<DatalinkDto>();
            RestoreDatalinkPass(snap, plan, null, missing, "pass1");
            if (missing.Count > 0 && NOSaveSystemPlugin.Instance != null)
                NOSaveSystemPlugin.Instance.StartCoroutine(NoSaveCoroutines.Safe(NoSaveVerify.Track(DatalinkRetryCo(snap, missing)), "DatalinkRetry"));
        }

        private static IEnumerator DatalinkRetryCo(MidMissionSnapshot snap, List<DatalinkDto> missing)
        {
            float[] waits = { 0.5f, 1.5f, 3f };
            foreach (var w in waits)
            {
                float t = 0f; while (t < w) { t += NoSaveWait.Dt(); yield return null; } // v0.11.0: pause-aware
                var still = new List<DatalinkDto>();
                RestoreDatalinkPass(snap, null, missing, still, "retry+" + w + "s");
                missing = still;
                if (missing.Count == 0) yield break;
            }
            var sb = new StringBuilder();
            foreach (var d in missing) sb.Append(d.faction).Append(':').Append(d.uniqueName).Append(' ');
            Log("datalink still missing after retries (" + missing.Count + "): " + sb);
            NoSaveWait.Timeout("datalink-retry", 5f, "give up on " + missing.Count + " entries (logged)");
        }

        private static void RestoreDatalinkPass(MidMissionSnapshot snap, StringBuilder plan, List<DatalinkDto> source, List<DatalinkDto> retryList, string pass)
        {
            if (snap == null || snap.datalink == null || snap.datalink.Count == 0)
            {
                plan?.AppendLine("  DATALINK none in save (pre-0.9.3 save or empty)");
                Log("datalink restore skip (none saved)");
                return;
            }
            var hqs = new Dictionary<string, FactionHQ>(StringComparer.Ordinal);
            foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
            {
                try { if (hq != null && hq.faction != null && hq.faction.factionName != null) hqs[hq.faction.factionName] = hq; } catch { }
            }
            // v0.9.5: respawned vehicles are not always in customIDLookup -> scan live units by UniqueName.
            var byName = new Dictionary<string, Unit>(StringComparer.Ordinal);
            try
            {
                foreach (var u in UnitRegistry.allUnits)
                {
                    try { if (u != null && !u.disabled && !string.IsNullOrEmpty(u.UniqueName) && !byName.ContainsKey(u.UniqueName)) byName[u.UniqueName] = u; } catch { }
                }
            }
            catch { }
            Dictionary<string, GlobalPosDto> savedPos = null;
            try
            {
                savedPos = new Dictionary<string, GlobalPosDto>(StringComparer.Ordinal);
                void AddAll<T>(List<T> l) where T : SavedUnitDto { if (l != null) foreach (var x in l) if (x != null && !string.IsNullOrEmpty(x.private_uniqueName) && x.globalPosition != null) savedPos[x.private_uniqueName] = x.globalPosition; }
                AddAll(snap.aircraft); AddAll(snap.vehicles); AddAll(snap.ships);
            }
            catch { }
            var missingNames = retryList ?? new List<DatalinkDto>();
            int added = 0, updated = 0, missingUnit = 0, missingHq = 0, failed = 0;
            foreach (var d in (source ?? snap.datalink))
            {
                if (d == null || string.IsNullOrEmpty(d.uniqueName)) continue;
                if (d.faction == null || !hqs.TryGetValue(d.faction, out var hq) || hq == null) { missingHq++; continue; }
                Unit unit = null;
                try { UnitRegistry.customIDLookup.TryGetValue(d.uniqueName, out unit); } catch { }
                if (unit == null && byName != null) byName.TryGetValue(d.uniqueName, out unit);
                if (unit == null && savedPos != null && savedPos.TryGetValue(d.uniqueName, out var sp))
                {
                    // fallback: respawned unit under another name -> nearest live unit within 15 m of its saved position
                    float best = 15f * 15f;
                    foreach (var u in byName.Values)
                    {
                        try
                        {
                            if (u is Building) continue;
                            var g = u.GlobalPosition(); float dx = g.x - sp.x, dy = g.y - sp.y, dz = g.z - sp.z;
                            float d2 = dx * dx + dy * dy + dz * dz;
                            if (d2 < best) { best = d2; unit = u; }
                        }
                        catch { }
                    }
                    if (unit != null) plan?.AppendLine("    DATALINK pos-match " + d.uniqueName + " -> " + unit.UniqueName);
                }
                if (unit == null) { missingUnit++; if (missingNames != null) missingNames.Add(d); continue; }
                try
                {
                    var id = unit.persistentID;
                    bool had = hq.trackingDatabase != null && hq.trackingDatabase.ContainsKey(id);
                    // Game-native: same ClientRpc the server uses when a unit is spotted (adds TrackingInfo,
                    // hooks onDisableUnit, strategic targets, map icon). Host invokes locally + replicates.
                    hq.RpcUpdateTrackingInfo(id);
                    if (hq.trackingDatabase != null && hq.trackingDatabase.TryGetValue(id, out var ti) && ti != null)
                    {
                        ti.lastKnownPosition = new GlobalPosition(d.x, d.y, d.z);
                        try { ti.attackers = (sbyte)Mathf.Clamp(d.attackers, 0, 127); ti.missileAttacks = (sbyte)Mathf.Clamp(d.missileAttacks, 0, 127); } catch { }
                        if (had) updated++; else added++;
                    }
                    else failed++;
                }
                catch (Exception ex) { failed++; plan?.AppendLine("    DATALINK FAIL " + d.uniqueName + ": " + ex.Message); }
            }
            string msg = pass + " tried=" + (source ?? snap.datalink).Count + " added=" + added + " alreadyKnown=" + updated +
                         " missingUnit=" + missingUnit + " missingHQ=" + missingHq + " failed=" + failed;
            plan?.AppendLine("  DATALINK restore " + msg);
            Log("datalink restore " + msg);
        }

        // ---------------- Faction ----------------
        internal static void EnsurePlayerFaction(MidMissionSnapshot snap, StringBuilder plan)
        {
            if (snap?.aircraft == null) return;
            string fac = null;
            foreach (var a in snap.aircraft)
                if (a != null && (a.isPlayerAircraft || a.playerControlled) && !string.IsNullOrEmpty(a.faction)) { fac = a.faction; break; }
            if (fac == null) fac = NoSaveV0910.SavedFaction(snap); // v0.9.10: no-aircraft save
            if (fac == null) { plan?.AppendLine("  FACTION no saved player faction - leave"); Log("faction: none saved"); return; }
            Player lp = null;
            try { lp = MidMissionV03Helpers.FindLocalPlayer(); } catch { }
            if (lp == null) { Log("faction: no local player"); return; }
            FactionHQ target = null;
            foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
                try { if (hq != null && hq.faction != null && hq.faction.factionName == fac) target = hq; } catch { }
            if (target == null) { Log("faction: HQ '" + fac + "' not found"); return; }
            try
            {
                if (lp.HQ != target)
                {
                    lp.SetFaction(target, true);
                    plan?.AppendLine("  FACTION Player.SetFaction(" + fac + ")");
                    Log("faction: Player.SetFaction(" + fac + ")");
                }
                else Log("faction: already " + fac);
            }
            catch (Exception ex) { Log("faction: SetFaction FAIL " + ex.Message); }
            try { SceneSingleton<DynamicMap>.i?.SetFaction(target); } catch (Exception ex) { Log("faction: DynamicMap.SetFaction FAIL " + ex.Message); }
        }

        // ---------------- Join menu / map suppression ----------------
        private static float _suppressUntil = -1f; // unscaled time
        internal static bool Suppressing { get { return Time.unscaledTime < _suppressUntil; } }

        internal static void BeginSuppressJoinUi() { _suppressUntil = float.MaxValue; Log("suppress Join UI (faction select) during reload: ON (map no longer suppressed)"); }

        internal static void EndSuppressJoinUiSoon(bool ok)
        {
            // keep a short tail so Player.WaitShowJoinMenu (1s delay) cannot fire after we finish
            if (NoSaveV0910.NoAircraftLoad)
            {
                // v0.9.10: spectator/dead save -> leave the native join/respawn flow alone and open it.
                _suppressUntil = -1f;
                Log("suppress Join UI: OFF (save had no player aircraft)");
                if (ok) NoSaveV0910.OpenJoinMenuSoon(); else NoSaveV0910.NoAircraftLoad = false;
                return;
            }
            _suppressUntil = Time.unscaledTime + (ok ? 2f : 0f);
            Log("suppress Join UI: ends in " + (ok ? 2f : 0f) + "s");
            if (ok) CloseJoinUiIfOpen("end-of-restore");
        }

        internal static void CloseJoinUiIfOpen(string why)
        {
            try
            {
                var lm = SceneSingleton<LeaderboardMenu>.i;
                if (lm != null && lm.gameObject.activeSelf && !GameplayUI.GameIsPaused)
                {
                    lm.Close(); // Join mode -> GameplayUI.CloseJoinMenu (native)
                    Log("closed Join menu (" + why + ")");
                }
            }
            catch (Exception ex) { Log("close Join menu FAIL " + ex.Message); }
            try
            {
                var map = SceneSingleton<DynamicMap>.i;
                bool maxed = false;
                try { var f = typeof(DynamicMap).GetField("mapMaximized", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); if (f != null) maxed = (bool)f.GetValue(map); } catch { }
                if (map != null && maxed && GameManager.GetLocalAircraft(out var _)) { map.Minimize(); Log("DynamicMap.Minimize native (" + why + "), was maximized"); }
                else Log("map left as-is (" + why + ") maximized=" + maxed);
            }
            catch (Exception ex) { Log("map minimize FAIL " + ex.Message); }
        }

        // ---------------- Pause after load ----------------
        /// <summary>v0.10.1 (M5): true only after PauseAfterLoad actually paused the game; the first ResumeGame consumes it.</summary>
        internal static bool LoadPauseArmed;

        internal static void SchedulePauseAfterLoad(StringBuilder plan)
        {
            if (!NOSaveSystemPlugin.PauseAfterLoad) { plan?.AppendLine("  PAUSE after load disabled by config"); return; }
            if (NOSaveSystemPlugin.Instance == null) return;
            plan?.AppendLine("  PAUSE after load scheduled (after PilotFix settle)");
            PausePending = true; // v0.11.0: VERIFY waits for this pause + the player's resume
            NOSaveSystemPlugin.Instance.StartCoroutine(NoSaveCoroutines.Safe(PauseCoTracked(), "PauseAfterLoad"));
        }

        /// <summary>v0.11.0: true from scheduling until PauseCo finished (paused or gave up).</summary>
        internal static bool PausePending;

        private static IEnumerator PauseCoTracked()
        {
            try { yield return PauseCo(); }
            finally { PausePending = false; }
        }

        private static IEnumerator PauseCo()
        {
            // Let post-restore coroutines (PilotFix waits for Pilot init, max ~5s) run with time ticking.
            float delay = NOSaveSystemPlugin.PauseAfterLoadDelay;
            Log("pause: delay=" + delay + "s realtime (next frame minimum)");
            yield return null;
            float t = 0f;
            while (t < delay) { t += Time.unscaledDeltaTime; if (t >= delay) break; yield return null; }
            yield return null;
            try
            {
                var ui = SceneSingleton<GameplayUI>.i;
                if (ui == null) { Log("pause: GameplayUI null"); yield break; }
                if (GameplayUI.GameIsPaused) { Log("pause: already paused"); yield break; }
                ui.PauseGame(); // == ESC: LeaderboardMenu(Leaderboard) -> SP TimeScale 0 + audio pause
                LoadPauseArmed = GameplayUI.GameIsPaused; // v0.10.1 (M5)
                Log("pause: GameplayUI.PauseGame() called; paused=" + GameplayUI.GameIsPaused + " reassertArmed=" + LoadPauseArmed);
            }
            catch (Exception ex) { Log("pause FAIL " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(GameplayUI), nameof(GameplayUI.ResumeGame))]
    internal static class Patch_GameplayUI_ResumeGame
    {
        static void Postfix()
        {
            // v0.10.1 (M5): reassert only on the first resume after the post-load pause; any other resume just clears it.
            bool armed = NoSaveV093.LoadPauseArmed;
            NoSaveV093.LoadPauseArmed = false;
            try { NoSavePause0.OnResume(); } catch { }
            if (armed)
            {
                try { NoSaveV096.ReassertAmmoOnResume(); } catch (Exception ex) { NOSaveSystemPlugin.Log?.LogWarning("v098: resume ammo FAIL " + ex.Message); }
                try { NoSavePilotFix.ReassertOnResume(); }
                catch (Exception ex) { NOSaveSystemPlugin.Log?.LogWarning("v094: resume reassert FAIL " + ex.Message); }
            }
            try { NoSaveV096.ClearAmmoReassert(); NoSavePilotFix.ClearPending(); } catch { }
        }
    }

    [HarmonyPatch(typeof(GameplayUI), nameof(GameplayUI.ShowJoinMenu))]
    internal static class Patch_GameplayUI_ShowJoinMenu
    {
        static bool Prefix()
        {
            try
            {
                if (NoSaveV093.Suppressing)
                {
                    NOSaveSystemPlugin.Log?.LogInfo("v093: suppressed GameplayUI.ShowJoinMenu during/after NOSave restore");
                    return false;
                }
            }
            catch { }
            return true;
        }
    }

}
