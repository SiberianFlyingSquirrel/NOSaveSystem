using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace NOSaveSystem
{
    internal static class NoSaveV0910
    {
        private static void Log(string m) { NOSaveSystemPlugin.Log?.LogInfo("v0910: " + m); }

        internal static bool NoAircraftLoad;

        internal static string CapturePlayerState(Aircraft playerAc)
        {
            string fac = null;
            try { var lp = MidMissionV03Helpers.FindLocalPlayer(); if (lp != null && lp.HQ != null && lp.HQ.faction != null) fac = lp.HQ.faction.factionName; } catch { }
            string s = (playerAc == null ? "noAircraft" : "aircraft") + "|" + (fac ?? "");
            Log("player state captured: " + s);
            return s;
        }

        internal static bool IsNoAircraft(MidMissionSnapshot snap) { return snap?.playerState != null && snap.playerState.StartsWith("noAircraft", StringComparison.Ordinal); }
        internal static string SavedFaction(MidMissionSnapshot snap)
        {
            if (snap?.playerState == null) return null;
            int bar = snap.playerState.IndexOf('|');
            var f = bar >= 0 ? snap.playerState.Substring(bar + 1) : null;
            return string.IsNullOrEmpty(f) ? null : f;
        }

        internal static void OpenJoinMenuSoon()
        {
            if (NOSaveSystemPlugin.Instance == null) return;
            NOSaveSystemPlugin.Instance.StartCoroutine(NoSaveCoroutines.Safe(OpenJoinCo(), "v0910 OpenJoin"));
        }

        private static IEnumerator OpenJoinCo()
        {
            float t = 0f; while (t < 0.3f) { t += Time.unscaledDeltaTime; yield return null; }
            try
            {
                var ui = SceneSingleton<GameplayUI>.i;
                if (ui == null) { Log("join menu: GameplayUI missing"); yield break; }
                ui.ShowJoinMenu();
                Log("opened native Join/respawn menu (save had no player aircraft)");
            }
            catch (Exception ex) { Log("join menu open FAIL " + ex.Message); }
            NoAircraftLoad = false;
        }

        private static readonly HashSet<string> _baseline = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> _carry = new HashSet<string>(StringComparer.Ordinal);

        internal static bool IsStatic(Unit u) { return u is Building; }

        internal static void OnMissionStart()
        {
            _baseline.Clear();
            if (NOSaveSystemPlugin.Instance != null)
                NOSaveSystemPlugin.Instance.StartCoroutine(NoSaveCoroutines.Safe(BaselineCo(), "v0910 baseline"));
        }

        private static IEnumerator BaselineCo()
        {
            yield return null;
            int n = 0;
            try
            {
                foreach (var u in UnitRegistry.allUnits)
                    if (u != null && IsStatic(u) && !string.IsNullOrEmpty(u.UniqueName) && _baseline.Add(u.UniqueName)) n++;
            }
            catch { }
            Log("static baseline=" + n);
        }

        internal static string CaptureDestroyedStatics()
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            var dead = new HashSet<string>(_carry, StringComparer.Ordinal);
            int disabledN = 0;
            try
            {
                foreach (var u in UnitRegistry.allUnits)
                {
                    if (u == null || !IsStatic(u) || string.IsNullOrEmpty(u.UniqueName)) continue;
                    present.Add(u.UniqueName);
                    _baseline.Add(u.UniqueName);
                    bool d = false;
                    try { d = u.disabled || u.unitState == Unit.UnitState.Destroyed; } catch { }
                    if (d) { dead.Add(u.UniqueName); disabledN++; }
                    else dead.Remove(u.UniqueName);
                }
            }
            catch { }
            int goneN = 0;
            foreach (var b in _baseline) if (!present.Contains(b) && dead.Add(b)) goneN++;
            Log("destroyed statics captured=" + dead.Count + " (disabled=" + disabledN + " goneFromRegistry=" + goneN + " baseline=" + _baseline.Count + ")");
            return string.Join("|", dead);
        }

        internal static void RestoreDestroyedStatics(MidMissionSnapshot snap, StringBuilder plan)
        {
            _carry.Clear();
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(snap?.destroyedStatics))
                foreach (var n in snap.destroyedStatics.Split('|')) if (!string.IsNullOrEmpty(n)) names.Add(n);
            if (snap?.otherUnits != null)
                foreach (var d in snap.otherUnits) if (d != null && !string.IsNullOrEmpty(d.private_uniqueName) && (d.disabled || d.unitState == "Destroyed")) names.Add(d.private_uniqueName);
            if (names.Count == 0) { plan?.AppendLine("  STATICS destroyed=0"); Log("destroyed statics: none in save"); return; }
            foreach (var n in names) _carry.Add(n);
            var live = new Dictionary<string, Unit>(StringComparer.Ordinal);
            try { foreach (var u in UnitRegistry.allUnits) if (u != null && !string.IsNullOrEmpty(u.UniqueName) && !live.ContainsKey(u.UniqueName)) live[u.UniqueName] = u; } catch { }
            int killed = 0, already = 0, missing = 0, failed = 0;
            foreach (var n in names)
            {
                Unit u;
                if (!live.TryGetValue(n, out u) || u == null) { missing++; continue; }
                if (!IsStatic(u)) continue;
                try
                {
                    if (u.disabled) { already++; continue; }
                    u.Networkdisabled = true;
                    killed++;
                    plan?.AppendLine("    STATIC destroyed " + n);
                }
                catch (Exception ex) { failed++; Log("static destroy FAIL " + n + ": " + ex.Message); }
            }
            string msg = "destroyed statics saved=" + names.Count + " destroyedNow=" + killed + " alreadyDown=" + already + " notInScene=" + missing + " failed=" + failed;
            plan?.AppendLine("  STATICS " + msg);
            Log(msg);
        }
    }
}
