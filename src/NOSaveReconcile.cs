using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NuclearOption.Networking;
using UnityEngine;

namespace NOSaveSystem
{
    /// <summary>
    /// v0.8.9: load re-entrancy guard + VERIFIED purge of live mobile units before snapshot spawn.
    ///
    /// Why: DespawnUnitForSwap removes the UniqueName from customIDLookup and calls UnregisterUnit
    /// BEFORE the GameObject is gone, so registry-based "settle" checks (WaitSavedUniqueNamesCleared)
    /// report 0 remaining even when the old GameObject is still alive in the scene. And
    /// ServerObjectManager.Destroy(go, false) only unspawns the network identity (the debug-GUI unload
    /// it copies is followed by a scene unload that actually destroys the objects).
    /// Here we hold the GameObject references we despawned and wait until Unity reports them destroyed
    /// (fake-null), plus scan the scene with Object.FindObjectsOfType&lt;Unit&gt;() which still sees
    /// units that were unregistered from UnitRegistry.
    ///
    /// Only game APIs already used elsewhere in this mod are referenced:
    /// UnitRegistry.allAircraft/allUnits/customIDLookup, Unit.UniqueName/persistentID/disabled/DisableUnit,
    /// Player.Aircraft, MidMissionV03Helpers.DespawnUnitForSwap/IsStaticUnitKind/IsSwapSafeMobileOther.
    /// </summary>
    internal static class NoSaveReconcile
    {
        // ---------------- re-entrancy guard ----------------

        private static bool _loadInProgress;
        private static string _loadOwner;
        private static float _loadStartedAt;
        private static int _loadToken;
        /// <summary>A held lock older than this is treated as stale (coroutine killed without finally).</summary>
        private const float StaleLockSec = 600f; // v0.9.1: > worst-case MissionReload (~310s); 300s could be stolen mid-reload

        internal static bool LoadInProgress
        {
            get { return _loadInProgress && !IsStale(); }
        }

        private static float Now()
        {
            try { return Time.unscaledTime; } catch { return 0f; }
        }

        private static bool IsStale()
        {
            if (!_loadInProgress) return false;
            return (Now() - _loadStartedAt) > StaleLockSec;
        }

        /// <summary>
        /// Single in-progress flag for ANY load path (F6 hotkey, pause-menu Load, Continue, MissionReload, Swap core).
        /// Returns 0 when refused, else a token to pass to ReleaseLoad.
        /// </summary>
        internal static int TryAcquireLoad(string owner)
        {
            if (_loadInProgress)
            {
                if (IsStale())
                {
                    NOSaveSystemPlugin.Log?.LogWarning("NOSave load lock STALE (owner=" + _loadOwner + ", held "
                        + (Now() - _loadStartedAt).ToString("F0", CultureInfo.InvariantCulture) + "s) - stealing for " + owner);
                }
                else
                {
                    NOSaveSystemPlugin.Log?.LogWarning("NOSave load REFUSED for " + owner + ": load already in progress (owner=" + _loadOwner + ")");
                    return 0;
                }
            }
            _loadInProgress = true;
            _loadOwner = owner;
            _loadStartedAt = Now();
            _loadToken++;
            if (_loadToken <= 0) _loadToken = 1;
            NOSaveSystemPlugin.Log?.LogInfo("NOSave load lock ACQUIRED owner=" + owner + " token=" + _loadToken);
            return _loadToken;
        }

        internal static void ReleaseLoad(int token)
        {
            if (token == 0) return;
            if (!_loadInProgress || token != _loadToken)
            {
                NOSaveSystemPlugin.Log?.LogInfo("NOSave load lock release ignored (token=" + token + " current=" + _loadToken + ")");
                return;
            }
            NOSaveSystemPlugin.Log?.LogInfo("NOSave load lock RELEASED owner=" + _loadOwner + " token=" + token);
            _loadInProgress = false;
            _loadOwner = null;
        }

        // ---------------- classification ----------------

        /// <summary>
        /// Mobile units that the snapshot can recreate: aircraft, ground vehicles, ships, and the
        /// swap-safe "other" kinds (PilotDismounted / Missile). Statics (Building/Scenery/...) are never touched.
        /// </summary>
        internal static bool IsPurgeCandidate(Unit u)
        {
            if (u == null) return false;
            string kind = "?";
            try { kind = u.GetType().Name; } catch { }
            try { if (MidMissionV03Helpers.IsStaticUnitKind(kind, u)) return false; } catch { return false; }
            if (u is Aircraft || u is GroundVehicle || u is Ship) return true;
            try { return MidMissionV03Helpers.IsSwapSafeMobileOther(kind, u); } catch { return false; }
        }

        private static bool IsKept(Unit u, Aircraft keepAircraft)
        {
            return keepAircraft != null && u is Aircraft && ReferenceEquals(u, keepAircraft);
        }

        /// <summary>Unity-alive (not destroyed) and not yet marked by us.</summary>
        private static bool IsAlive(UnityEngine.Object o)
        {
            return o != null; // Unity overloaded == : false once Destroy has completed
        }

        internal struct MobileCounts
        {
            public int aircraft, vehicles, ships, other, total, registryTotal, sceneOnly;
            public string otherTypes;
            public override string ToString()
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "total={0} (aircraft={1} vehicles={2} ships={3} other={4}) registry={5} sceneOnly(unregistered)={6}{7}",
                    total, aircraft, vehicles, ships, other, registryTotal, sceneOnly,
                    string.IsNullOrEmpty(otherTypes) ? "" : " otherTypes=[" + otherTypes + "]");
            }
        }

        /// <summary>
        /// Collect every live purge-candidate unit: UnitRegistry lists + scene scan (FindObjectsOfType)
        /// so units already unregistered but not yet destroyed are still counted.
        /// </summary>
        // v0.9.0: reused buffers (main-thread only) - no per-call HashSet/List churn during the purge wait loop.
        private static readonly HashSet<int> _seenBuf = new HashSet<int>();
        private static readonly HashSet<int> _registryBuf = new HashSet<int>();
        private static readonly List<Unit> _candBuf = new List<Unit>(512);
        private static readonly SortedDictionary<string, int> _otherTypesBuf = new SortedDictionary<string, int>(StringComparer.Ordinal);
        /// <summary>Scene scans (FindObjectsOfType) are throttled to this interval while waiting.</summary>
        internal const float ScanIntervalSec = 0.1f;

        internal static List<Unit> CollectLiveMobiles(Aircraft keepAircraft, out MobileCounts counts)
        {
            counts = new MobileCounts();
            var result = new List<Unit>();
            var seen = _seenBuf; seen.Clear();
            var registryIds = _registryBuf; registryIds.Clear();

            try
            {
                if (UnitRegistry.allAircraft != null)
                    foreach (var ac in UnitRegistry.allAircraft)
                        if (ac != null) registryIds.Add(ac.GetInstanceID());
            }
            catch { }
            try
            {
                if (UnitRegistry.allUnits != null)
                    foreach (var u in UnitRegistry.allUnits)
                        if (u != null) registryIds.Add(u.GetInstanceID());
            }
            catch { }

            var candidates = _candBuf; candidates.Clear();
            try
            {
                if (UnitRegistry.allAircraft != null)
                    foreach (var ac in UnitRegistry.allAircraft) if (ac != null) candidates.Add(ac);
            }
            catch { }
            try
            {
                if (UnitRegistry.allUnits != null)
                    foreach (var u in UnitRegistry.allUnits) if (u != null) candidates.Add(u);
            }
            catch { }
            try
            {
                var scene = UnityEngine.Object.FindObjectsOfType<Unit>();
                if (scene != null) candidates.AddRange(scene);
            }
            catch { }

            var otherTypes = _otherTypesBuf; otherTypes.Clear();
            foreach (var u in candidates)
            {
                if (!IsAlive(u)) continue;
                int id = u.GetInstanceID();
                if (!seen.Add(id)) continue;
                if (IsKept(u, keepAircraft)) continue;
                if (!IsPurgeCandidate(u))
                {
                    // Log non-static, non-purged types so a PC run tells us what "people" are in this build.
                    try
                    {
                        string tn = u.GetType().Name;
                        if (!MidMissionV03Helpers.IsStaticUnitKind(tn, u))
                        {
                            int c; otherTypes.TryGetValue(tn, out c); otherTypes[tn] = c + 1;
                        }
                    }
                    catch { }
                    continue;
                }
                result.Add(u);
                if (u is Aircraft) counts.aircraft++;
                else if (u is GroundVehicle) counts.vehicles++;
                else if (u is Ship) counts.ships++;
                else counts.other++;
                if (registryIds.Contains(id)) counts.registryTotal++;
                else counts.sceneOnly++;
            }
            candidates.Clear(); // drop references so destroyed units are not kept alive by the buffer
            counts.total = result.Count;
            if (otherTypes.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var kv in otherTypes)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(kv.Key).Append('=').Append(kv.Value);
                }
                counts.otherTypes = "not purged: " + sb;
            }
            return result;
        }

        // ---------------- verified purge ----------------

        internal sealed class PurgeResult
        {
            public bool ok;
            public int despawnCalls;
            public int forced;
            public int rounds;
            public MobileCounts before;
            public MobileCounts after;
        }

        /// <summary>
        /// Hard-destroy fallback, same calls the mod's DespawnUnitForSwap fallback uses (RemoveUnitOutcome path).
        /// Also deactivates so AI/physics stop this frame even though Destroy completes end-of-frame.
        /// </summary>
        internal static bool ForceDestroy(Unit u, StringBuilder plan)
        {
            if (!IsAlive(u)) return true;
            string label = "?";
            try { label = (u.UniqueName ?? u.name) + " (" + u.GetType().Name + ")"; } catch { }
            try { if (!u.disabled) u.DisableUnit(); } catch { }
            try
            {
                string uname = u.UniqueName;
                if (!string.IsNullOrEmpty(uname) && UnitRegistry.customIDLookup != null &&
                    UnitRegistry.customIDLookup.TryGetValue(uname, out var reg) && ReferenceEquals(reg, u))
                    UnitRegistry.customIDLookup.Remove(uname);
            }
            catch { }
            try { UnitRegistry.UnregisterUnit(u); } catch { }
            try
            {
                var go = u.gameObject;
                if (go != null)
                {
                    go.SetActive(false);
                    UnityEngine.Object.Destroy(go);
                    plan?.AppendLine("    FORCE DisableUnit+SetActive(false)+Object.Destroy " + label);
                    return true;
                }
            }
            catch (Exception ex)
            {
                plan?.AppendLine("    FORCE destroy FAIL " + label + ": " + ex.Message);
            }
            return false;
        }

        /// <summary>
        /// Despawn every live mobile (except keepAircraft), then WAIT until the scene shows zero remaining
        /// for a few consecutive frames. Retries DespawnUnitForSwap, then force-destroys. Never returns ok=true
        /// while old mobiles still exist. Caller must not spawn when result.ok == false.
        /// </summary>
        internal static IEnumerator PurgeMobilesVerified(string phase, Player localPlayer, Aircraft keepAircraft,
            StringBuilder plan, PurgeResult result, float timeoutSec = 8f)
        {
            if (result == null) result = new PurgeResult();
            MobileCounts c0;
            var initial = CollectLiveMobiles(keepAircraft, out c0);
            result.before = c0;
            string head = "NOSave purge[" + phase + "]";
            plan?.AppendLine("  " + head + " BEFORE " + c0 + (keepAircraft != null ? " (keeping local player aircraft)" : ""));
            NOSaveSystemPlugin.Log?.LogInfo(head + " BEFORE " + c0);

            var tracked = new List<GameObject>();
            var trackedIds = new HashSet<int>();
            foreach (var u in initial)
            {
                GameObject go = null;
                try { go = u.gameObject; } catch { }
                bool okOne = false;
                try { okOne = MidMissionV03Helpers.DespawnUnitForSwap(u, localPlayer, plan); } catch (Exception ex) { plan?.AppendLine("    purge despawn EX: " + ex.Message); }
                result.despawnCalls++;
                // DespawnUnitForSwap may only unspawn (ServerObjectManager.Destroy(go,false)); make sure the GO dies.
                if (IsAlive(u))
                {
                    try { if (!u.disabled) u.DisableUnit(); } catch { }
                    try { if (go != null) UnityEngine.Object.Destroy(go); } catch { }
                }
                if (!okOne) plan?.AppendLine("    purge despawn returned false (will verify/force)");
                if (go != null) tracked.Add(go);
                trackedIds.Add(u.GetInstanceID());
            }

            const int stableFramesNeeded = 5;
            const int maxRounds = 3;
            float waited = 0f;
            float roundStart = 0f;
            int stable = 0;
            int round = 1;
            float lastScan = -1f;
            MobileCounts cn = new MobileCounts();
            List<Unit> remaining = null;
            float roundSec = Mathf.Max(1f, timeoutSec / maxRounds);

            while (true)
            {
                yield return null;
                waited += Time.unscaledDeltaTime;

                int trackedAlive = 0;
                for (int i = 0; i < tracked.Count; i++) if (IsAlive(tracked[i])) trackedAlive++;
                // Throttle the expensive scene scan: every frame only once tracked objects are all gone
                // (needed for the stable-frames check), otherwise every ScanIntervalSec or at round boundaries.
                bool roundDue = waited - roundStart >= roundSec;
                if (trackedAlive == 0 || roundDue || remaining == null || waited - lastScan >= ScanIntervalSec)
                {
                    remaining = CollectLiveMobiles(keepAircraft, out cn);
                    lastScan = waited;
                }
                else
                {
                    stable = 0;
                    continue;
                }

                if (trackedAlive == 0 && remaining.Count == 0)
                {
                    stable++;
                    if (stable >= stableFramesNeeded)
                    {
                        result.ok = true;
                        result.after = cn;
                        break;
                    }
                    continue;
                }
                stable = 0;

                // New mobiles appearing (mission default spawns arriving late) or stragglers: despawn them too.
                foreach (var u in remaining)
                {
                    if (trackedIds.Contains(u.GetInstanceID())) continue;
                    GameObject go = null;
                    try { go = u.gameObject; } catch { }
                    try { MidMissionV03Helpers.DespawnUnitForSwap(u, localPlayer, plan); } catch { }
                    result.despawnCalls++;
                    if (IsAlive(u)) { try { if (!u.disabled) u.DisableUnit(); } catch { } try { if (go != null) UnityEngine.Object.Destroy(go); } catch { } }
                    if (go != null) tracked.Add(go);
                    trackedIds.Add(u.GetInstanceID());
                    plan?.AppendLine("    purge late/straggler despawn " + SafeLabel(u));
                }

                if (waited - roundStart >= roundSec)
                {
                    if (round >= maxRounds)
                    {
                        result.ok = false;
                        result.after = cn;
                        plan?.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "  {0} TIMEOUT after {1:F1}s rounds={2} trackedAlive={3} remaining {4}", head, waited, round, trackedAlive, cn));
                        NOSaveSystemPlugin.Log?.LogError(head + " TIMEOUT remaining " + cn + " trackedAlive=" + trackedAlive + " - spawning will be BLOCKED");
                        NoSaveWait.Timeout("purge[" + phase + "]", waited, "spawning BLOCKED, restore aborted (no ghost duplicates)");
                        break;
                    }
                    round++;
                    roundStart = waited;
                    plan?.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  {0} round {1}: force-destroy remaining={2} trackedAlive={3}", head, round, remaining.Count, trackedAlive));
                    NOSaveSystemPlugin.Log?.LogWarning(head + " round " + round + " force-destroy remaining=" + remaining.Count + " trackedAlive=" + trackedAlive);
                    foreach (var u in remaining) { if (ForceDestroy(u, plan)) result.forced++; }
                    for (int i = 0; i < tracked.Count; i++)
                    {
                        var go = tracked[i];
                        if (!IsAlive(go)) continue;
                        try { go.SetActive(false); UnityEngine.Object.Destroy(go); result.forced++; } catch { }
                    }
                }
            }

            result.rounds = round;
            plan?.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  {0} AFTER ok={1} {2} despawnCalls={3} forced={4} rounds={5} waited={6:F2}s",
                head, result.ok, result.after, result.despawnCalls, result.forced, round, waited));
            if (result.ok)
                NOSaveSystemPlugin.Log?.LogInfo(head + " AFTER ok " + result.after + " despawnCalls=" + result.despawnCalls + " forced=" + result.forced);
        }

        private static string SafeLabel(Unit u)
        {
            try { return (u.UniqueName ?? u.name) + " (" + u.GetType().Name + ")"; } catch { return "?"; }
        }

        /// <summary>Log-only count snapshot (used before/after spawn and after tear-down).</summary>
        internal static MobileCounts LogCounts(string tag, StringBuilder plan)
        {
            MobileCounts c;
            CollectLiveMobiles(null, out c);
            plan?.AppendLine("  NOSave counts[" + tag + "] " + c);
            NOSaveSystemPlugin.Log?.LogInfo("NOSave counts[" + tag + "] " + c);
            return c;
        }

        /// <summary>Snapshot mobile totals (aircraft+vehicles+ships+swap-safe others) for post-spawn sanity check.</summary>
        internal static int ExpectedMobileCount(MidMissionSnapshot snap)
        {
            if (snap == null) return 0;
            int n = 0;
            if (snap.aircraft != null) n += snap.aircraft.Count;
            if (snap.vehicles != null) n += snap.vehicles.Count;
            if (snap.ships != null) n += snap.ships.Count;
            if (snap.otherUnits != null)
                foreach (var o in snap.otherUnits)
                {
                    if (o == null) continue;
                    try { if (MidMissionV03Helpers.IsSwapSafeMobileOther(o.kind, null)) n++; } catch { }
                }
            return n;
        }
    }
}
