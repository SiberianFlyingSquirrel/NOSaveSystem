using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace NOSaveSystem
{
    /// <summary>One flat property-bag entry. All values are strings + a type tag so the schema never breaks.</summary>
    [Serializable]
    public class StateEntryDto
    {
        public string scope;   // mission | missionTime | faction | objective | objectiveAll | unit | unitAmmo | unitOwner | airbase | player
        public string target;  // faction name / objective UniqueName / unit UniqueName / airbase id / "local"
        public string key;     // member name (or Component#idx/member for ammo)
        public string vtype;   // f i b s e faction
        public string value;
    }

    /// <summary>
    /// v0.9.0 full-state extension. CONFIRMED APIs (already used elsewhere in the mod) are called directly;
    /// everything else goes through NoSaveReflect candidate lookups and is logged FOUND/MISS on first use.
    /// Restore never calls Objective.Complete()/CompleteObjective/StartObjective (Outcomes would fire).
    /// </summary>
    internal static class NoSaveExtState
    {
        internal const int FormatVersion = 2;

        // ---- candidate member names (reflection guesses; logged at runtime) ----
        static readonly string[] MissionTimeOffsetCands = { "singlePlayerStartTime", "missionStartTime", "startTime", "levelStartTime", "missionTimeOffset", "timeOffset" };
        static readonly string[] FactionExtraCands = { "Networkreserves", "reserves", "reservePoints", "NetworksupplyPoints", "supplyPoints", "victoryPoints", "NetworkvictoryPoints", "Networkwarheads", "warheads", "nuclearWarheads", "killScore", "NetworkkillScore" };
        static readonly string[] ObjectiveProgressCands = { "progress", "Networkprogress", "currentProgress", "completePercent", "count", "currentCount", "killCount", "destroyedCount", "timer", "timeRemaining", "elapsed" };
        static readonly string[] RunnerAllObjectiveCands = { "CompletedObjectives", "completedObjectives", "FinishedObjectives", "finishedObjectives", "AllObjectives", "allObjectives", "objectives", "Objectives" };
        static readonly string[] AmmoComponentHints = { "Weapon", "Gun", "Launcher", "Turret", "Magazine", "Ammo", "Cannon", "Countermeasure", "Flare", "Chaff" };
        static readonly string[] AmmoMemberCands = { "Networkammo", "ammo", "currentAmmo", "ammoCount", "ammunition", "rounds", "roundsLeft", "magazineAmmo", "missilesLeft", "ammoRemaining" };
        static readonly string[] AirbaseTypeCands = { "Airbase", "AirBase", "CapturePoint", "CaptureZone", "ControlPoint", "Outpost" };
        static readonly string[] AirbaseIdCands = { "UniqueName", "uniqueName", "airbaseName", "Name", "displayName", "DisplayName" };
        static readonly string[] AirbaseOwnerCands = { "CurrentHQ", "currentHQ", "NetworkCurrentHQ", "NetworkHQ", "HQ", "hq", "Networkhq", "owner", "controllingFaction", "faction" };
        static readonly string[] AirbaseProgressCands = { "NetworkcaptureProgress", "captureProgress", "captureAmount", "controlBalance", "progress" };
        static readonly string[] AirbaseCaptureMethodCands = { "SetHQ", "SetFaction", "ChangeHQ", "SetOwner", "Capture", "ServerCapture" };
        static readonly string[] UnitOwnerSetCands = { "NetworkHQ" };
        static readonly string[] PlayerCands = { "Networkscore", "score", "PlayerScore", "Networkfunds", "funds", "Networkallocation", "allocation", "Networkkills", "kills", "Networkrank", "rank" };

        // ===================== CAPTURE =====================

        internal static List<StateEntryDto> Capture(MidMissionSnapshot snap)
        {
            var list = new List<StateEntryDto>(256);
            Try("missionTime", () => CaptureMissionTime(list));
            Try("faction", () => CaptureFactions(list));
            Try("objectives", () => CaptureObjectives(list));
            Try("units", () => CaptureUnits(list));
            Try("airbases", () => CaptureAirbases(list));
            Try("player", () => CapturePlayer(list));
            NOSaveSystemPlugin.Log?.LogInfo("NOSave ext-state captured entries=" + list.Count);
            return list;
        }

        private static void Try(string what, Action a)
        {
            try { a(); }
            catch (Exception ex) { NOSaveSystemPlugin.Log?.LogWarning("NOSave ext-state " + what + " FAIL: " + ex.Message); }
        }

        private static void Add(List<StateEntryDto> list, string scope, string target, string key, object v)
        {
            if (v == null) return;
            string vt, text;
            if (v is FactionHQ)
            {
                string fn = FactionName((FactionHQ)v);
                if (fn == null) return;
                list.Add(new StateEntryDto { scope = scope, target = target ?? "", key = key, vtype = "faction", value = fn });
                return;
            }
            if (!NoSaveReflect.Encode(v, out vt, out text)) return;
            list.Add(new StateEntryDto { scope = scope, target = target ?? "", key = key, vtype = vt, value = text });
        }

        private static string FactionName(FactionHQ hq)
        {
            try { return hq != null && hq.faction != null ? hq.faction.factionName : null; } catch { return null; }
        }

        /// <summary>Capture every simple-typed member found among candidates (key = real member name).</summary>
        private static void CaptureMembers(List<StateEntryDto> list, string scope, string target, object obj, string[] cands)
        {
            if (obj == null) return;
            var t = obj.GetType();
            var done = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in cands)
            {
                var m = NoSaveReflect.FindMember(t, false, c);
                if (m == null || !done.Add(m.Name)) continue;
                var mt = NoSaveReflect.MemberType(m);
                if (!NoSaveReflect.IsSimple(mt) && !typeof(FactionHQ).IsAssignableFrom(mt)) continue;
                object v;
                if (NoSaveReflect.TryGet(obj, m, out v)) Add(list, scope, target, m.Name, v);
            }
        }

        private static void CaptureMissionTime(List<StateEntryDto> list)
        {
            var mm = MissionManager.i;
            if (mm == null) return;
            // Confirmed read: MissionManager.i.MissionTime (also in mission.missionTime). Candidate offsets for SP restore:
            CaptureMembers(list, "missionTime", "mm", mm, MissionTimeOffsetCands);
        }

        private static void CaptureFactions(List<StateEntryDto> list)
        {
            foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
            {
                if (hq == null) continue;
                string fn = FactionName(hq);
                if (fn == null) continue;
                CaptureMembers(list, "faction", fn, hq, FactionExtraCands);
            }
        }

        private static string ObjectiveId(Objective o)
        {
            try
            {
                var so = o.SavedObjective;
                if (so == null) return null;
                if (!string.IsNullOrEmpty(so.UniqueName)) return so.UniqueName;
                return so.DisplayName;
            }
            catch { return null; }
        }

        private static void CaptureObjectives(List<StateEntryDto> list)
        {
            var runner = MissionManager.Runner;
            if (runner == null) return;
            if (runner.ActiveObjectives != null)
            {
                foreach (var o in runner.ActiveObjectives)
                {
                    if (o == null) continue;
                    string id = ObjectiveId(o);
                    if (string.IsNullOrEmpty(id)) continue;
                    CaptureMembers(list, "objective", id, o, ObjectiveProgressCands);
                }
            }
            // Completed / all objectives (Status is a confirmed public field on Objective).
            foreach (var cand in RunnerAllObjectiveCands)
            {
                var m = NoSaveReflect.FindMember(runner.GetType(), false, cand);
                if (m == null) continue;
                object v;
                if (!NoSaveReflect.TryGet(runner, m, out v)) continue;
                var items = NoSaveReflect.AsList(v);
                if (items == null) continue;
                foreach (var it in items)
                {
                    var o = it as Objective;
                    if (o == null) continue;
                    string id = ObjectiveId(o);
                    if (string.IsNullOrEmpty(id)) continue;
                    try { list.Add(new StateEntryDto { scope = "objectiveAll", target = id, key = "Status", vtype = "e", value = o.Status.ToString() }); } catch { }
                }
            }
        }

        // v0.9.1: per-Type caches. CaptureAmmo/RestoreAmmo walk every MonoBehaviour of every unit; v0.9.0 did a
        // 10-hint IndexOf scan per component plus a string.Join cache-key build in FindMember per ammo component.
        private static readonly Dictionary<Type, bool> _ammoTypeCache = new Dictionary<Type, bool>();
        private static readonly Dictionary<Type, MemberInfo> _ammoMemberCache = new Dictionary<Type, MemberInfo>();

        private static bool IsAmmoComponent(Type t)
        {
            bool r;
            if (_ammoTypeCache.TryGetValue(t, out r)) return r;
            r = IsAmmoComponent(t.Name);
            _ammoTypeCache[t] = r;
            return r;
        }

        private static MemberInfo AmmoMember(Type t)
        {
            MemberInfo m;
            if (_ammoMemberCache.TryGetValue(t, out m)) return m;
            m = NoSaveReflect.FindMember(t, false, AmmoMemberCands);
            _ammoMemberCache[t] = m;
            return m;
        }

        private static bool IsAmmoComponent(string typeName)
        {
            foreach (var h in AmmoComponentHints)
                if (typeName.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // v0.11.1: sectioned capture so VERIFY can spread it over frames (2 ms budget): sections 0 missionTime,
        // 1 faction, 2 objectives, 3 units (chunked via UnitList/CaptureUnitsRange), 4 airbases, 5 player.
        internal const int SectionCount = 6;
        internal const int UnitsSection = 3;

        /// <summary>Capture one non-unit section into list (same code as Capture).</summary>
        internal static void CaptureSection(int i, List<StateEntryDto> list)
        {
            switch (i)
            {
                case 0: Try("missionTime", () => CaptureMissionTime(list)); break;
                case 1: Try("faction", () => CaptureFactions(list)); break;
                case 2: Try("objectives", () => CaptureObjectives(list)); break;
                case 3: Try("units", () => CaptureUnits(list)); break;
                case 4: Try("airbases", () => CaptureAirbases(list)); break;
                case 5: Try("player", () => CapturePlayer(list)); break;
            }
        }

        /// <summary>The unit list CaptureUnits walks (aircraft first, then all units, no duplicates).</summary>
        internal static List<Unit> UnitList()
        {
            var units = new List<Unit>();
            if (UnitRegistry.allUnits == null) return units;
            var seen = new HashSet<int>();
            try { if (UnitRegistry.allAircraft != null) foreach (var a in UnitRegistry.allAircraft) if (a != null && seen.Add(a.GetInstanceID())) units.Add(a); } catch { }
            try { foreach (var u in UnitRegistry.allUnits) if (u != null && seen.Add(u.GetInstanceID())) units.Add(u); } catch { }
            return units;
        }

        private static void CaptureUnits(List<StateEntryDto> list)
        {
            if (UnitRegistry.allUnits == null) return;
            var units = UnitList();
            CaptureUnitsRange(list, units, 0, units.Count);
        }

        /// <summary>Units [start, start+count) of a UnitList() result.</summary>
        internal static void CaptureUnitsRange(List<StateEntryDto> list, List<Unit> units, int start, int count)
        {
            int end = Math.Min(units.Count, start + count);
            for (int ui = start; ui < end; ui++)
            {
                var u = units[ui];
                if (u == null) continue;
                string id = null;
                try { id = u.UniqueName; } catch { }
                if (string.IsNullOrEmpty(id)) continue; // F5 assigns nosave_pid_* to blanks before this runs
                bool isStatic = false;
                try { isStatic = MidMissionV03Helpers.IsStaticUnitKind(u.GetType().Name, u); } catch { }
                // Ownership (statics included: buildings captured / factories flipped). NetworkHQ read is confirmed.
                try
                {
                    var hq = u.NetworkHQ;
                    string fn = FactionName(hq);
                    if (fn != null) list.Add(new StateEntryDto { scope = "unitOwner", target = id, key = "NetworkHQ", vtype = "faction", value = fn });
                }
                catch { }
                if (isStatic) continue; // no component scans on thousands of statics
                CaptureAmmo(list, u, id);
            }
        }

        private static void CaptureAmmo(List<StateEntryDto> list, Unit u, string id)
        {
            MonoBehaviour[] comps;
            try { comps = u.GetComponentsInChildren<MonoBehaviour>(true); } catch { return; }
            if (comps == null) return;
            var perType = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var c in comps)
            {
                if (c == null) continue;
                var ct = c.GetType();
                if (!IsAmmoComponent(ct)) continue;
                string tn = ct.Name;
                int idx; perType.TryGetValue(tn, out idx); perType[tn] = idx + 1;
                var m = AmmoMember(ct);
                if (m == null) continue;
                var mt = NoSaveReflect.MemberType(m);
                if (!(mt == typeof(int) || mt == typeof(float) || mt == typeof(short) || mt == typeof(byte) || mt == typeof(uint) || mt == typeof(ushort) || mt == typeof(long) || mt == typeof(double))) continue;
                object v;
                if (NoSaveReflect.TryGet(c, m, out v))
                    Add(list, "unitAmmo", id, tn + "#" + idx.ToString(CultureInfo.InvariantCulture) + "/" + m.Name, v);
            }
        }

        private static List<Component> FindAirbases(out Type abType)
        {
            abType = NoSaveReflect.FindType(AirbaseTypeCands);
            var res = new List<Component>();
            if (abType == null || !typeof(Component).IsAssignableFrom(abType)) return res;
            try
            {
                var objs = UnityEngine.Object.FindObjectsOfType(abType);
                if (objs != null) foreach (var o in objs) { var c = o as Component; if (c != null) res.Add(c); }
            }
            catch { }
            return res;
        }

        private static string AirbaseId(Component c)
        {
            var m = NoSaveReflect.FindMember(c.GetType(), false, AirbaseIdCands);
            object v;
            if (m != null && NoSaveReflect.TryGet(c, m, out v) && v is string && !string.IsNullOrEmpty((string)v)
                && !string.Equals(m.Name, "name", StringComparison.Ordinal))
                return (string)v;
            // Fallback: GameObject name + rounded position (stable across a MissionReload of the same map)
            var p = c.transform.position;
            return c.gameObject.name + "@" + Mathf.RoundToInt(p.x).ToString(CultureInfo.InvariantCulture) + "," + Mathf.RoundToInt(p.z).ToString(CultureInfo.InvariantCulture);
        }

        private static void CaptureAirbases(List<StateEntryDto> list)
        {
            Type t;
            var abs = FindAirbases(out t);
            foreach (var c in abs)
            {
                if (c == null) continue;
                string id = AirbaseId(c);
                var owner = NoSaveReflect.FindMember(c.GetType(), false, AirbaseOwnerCands);
                if (owner != null)
                {
                    object v;
                    if (NoSaveReflect.TryGet(c, owner, out v))
                    {
                        if (v is FactionHQ) Add(list, "airbase", id, owner.Name, v);
                        else if (v is string) Add(list, "airbase", id, owner.Name, v);
                        else if (v == null) list.Add(new StateEntryDto { scope = "airbase", target = id, key = owner.Name, vtype = "faction", value = "" });
                    }
                }
                CaptureMembers(list, "airbase", id, c, AirbaseProgressCands);
            }
            if (abs.Count > 0) NOSaveSystemPlugin.Log?.LogInfo("NOSave ext-state airbases captured=" + abs.Count + " type=" + (t != null ? t.Name : "?"));
        }

        private static void CapturePlayer(List<StateEntryDto> list)
        {
            var p = MidMissionV03Helpers.FindLocalPlayer();
            if (p == null) return;
            CaptureMembers(list, "player", "local", p, PlayerCands);
        }

        // ===================== RESTORE =====================

        internal sealed class Counters { public int applied, unchanged, missing, failed; }

        internal static void Restore(MidMissionSnapshot snap, StringBuilder plan, bool restoreObjectiveStatus)
        {
            if (snap == null || snap.extState == null || snap.extState.Count == 0)
            {
                plan?.AppendLine("  EXT-STATE none in save (formatVersion=" + (snap != null ? snap.formatVersion : 0) + ") - skipped");
                return;
            }
            var c = new Counters();
            var byScope = new Dictionary<string, List<StateEntryDto>>(StringComparer.Ordinal);
            foreach (var e in snap.extState)
            {
                if (e == null || string.IsNullOrEmpty(e.scope)) continue;
                List<StateEntryDto> l;
                if (!byScope.TryGetValue(e.scope, out l)) { l = new List<StateEntryDto>(); byScope[e.scope] = l; }
                l.Add(e);
            }
            List<StateEntryDto> s;
            if (byScope.TryGetValue("missionTime", out s)) TryR(plan, "missionTime", () => RestoreMissionTime(s, snap, plan, c));
            if (byScope.TryGetValue("faction", out s)) TryR(plan, "faction", () => RestoreFactions(s, plan, c));
            if (byScope.TryGetValue("objective", out s)) TryR(plan, "objective", () => RestoreObjectives(s, plan, c));
            if (restoreObjectiveStatus && byScope.TryGetValue("objectiveAll", out s)) TryR(plan, "objectiveAll", () => RestoreObjectiveStatusAll(s, plan, c));
            if (byScope.TryGetValue("unitOwner", out s)) TryR(plan, "unitOwner", () => RestoreUnitOwners(s, plan, c));
            if (byScope.TryGetValue("unitAmmo", out s)) TryR(plan, "unitAmmo", () => RestoreAmmo(s, plan, c));
            if (byScope.TryGetValue("airbase", out s)) TryR(plan, "airbase", () => RestoreAirbases(s, plan, c));
            if (byScope.TryGetValue("player", out s)) TryR(plan, "player", () => RestoreMembersOn(MidMissionV03Helpers.FindLocalPlayer(), s, plan, c, "PLAYER"));
            plan?.AppendLine("  EXT-STATE applied=" + c.applied + " unchanged=" + c.unchanged + " missing=" + c.missing + " failed=" + c.failed + " entries=" + snap.extState.Count);
            NOSaveSystemPlugin.Log?.LogInfo("NOSave ext-state restore applied=" + c.applied + " unchanged=" + c.unchanged + " missing=" + c.missing + " failed=" + c.failed);
        }

        private static void TryR(StringBuilder plan, string what, Action a)
        {
            try { a(); }
            catch (Exception ex) { plan?.AppendLine("  EXT-STATE " + what + " FAIL: " + ex.Message); }
        }

        /// <summary>Write one entry onto obj by exact member name, converting to the declared type.</summary>
        private static void ApplyEntry(object obj, StateEntryDto e, StringBuilder plan, Counters c, string tag)
        {
            if (obj == null) { c.missing++; return; }
            var m = NoSaveReflect.FindMember(obj.GetType(), false, e.key);
            if (m == null) { c.missing++; return; }
            if (!NoSaveReflect.CanWrite(m)) { c.missing++; return; }
            var want = NoSaveReflect.MemberType(m);
            object cur; NoSaveReflect.TryGet(obj, m, out cur);
            object val;
            if (e.vtype == "faction")
            {
                if (!typeof(FactionHQ).IsAssignableFrom(want)) { c.failed++; return; }
                FactionHQ hq = null;
                if (!string.IsNullOrEmpty(e.value)) { try { hq = FactionRegistry.HqFromName(e.value); } catch { } if (hq == null) { c.missing++; return; } }
                if (ReferenceEquals(cur, hq)) { c.unchanged++; return; }
                val = hq;
            }
            else
            {
                if (!NoSaveReflect.Decode(e.vtype, e.value, want, out val)) { c.failed++; return; }
                if (cur != null && cur.Equals(val)) { c.unchanged++; return; }
            }
            if (NoSaveReflect.TrySet(obj, m, val))
            {
                c.applied++;
                plan?.AppendLine("  " + tag + " " + e.target + "." + e.key + " " + (cur ?? "null") + " -> " + e.value);
            }
            else c.failed++;
        }

        private static void RestoreMembersOn(object obj, List<StateEntryDto> entries, StringBuilder plan, Counters c, string tag)
        {
            foreach (var e in entries) ApplyEntry(obj, e, plan, c, tag);
        }

        /// <summary>
        /// SP mission clock: MissionTime is confirmed readable but has no setter. If a candidate start-time offset
        /// field exists, shift it by (saved - current) and VERIFY by re-reading MissionTime; revert if it didn't help.
        /// </summary>
        private static void RestoreMissionTime(List<StateEntryDto> entries, MidMissionSnapshot snap, StringBuilder plan, Counters c)
        {
            var mm = MissionManager.i;
            if (mm == null || snap.mission == null || snap.mission.missionTime <= 0f) return;
            float savedT = snap.mission.missionTime;
            float before;
            try { before = mm.MissionTime; } catch { return; }
            if (Math.Abs(before - savedT) < 2f) { c.unchanged++; return; }
            foreach (var e in entries)
            {
                var m = NoSaveReflect.FindMember(mm.GetType(), false, e.key);
                if (m == null || !NoSaveReflect.CanWrite(m)) continue;
                var mt = NoSaveReflect.MemberType(m);
                if (mt != typeof(float) && mt != typeof(double)) continue;
                object curObj;
                if (!NoSaveReflect.TryGet(mm, m, out curObj) || curObj == null) continue;
                double cur = Convert.ToDouble(curObj, CultureInfo.InvariantCulture);
                double shifted = cur - (savedT - before);
                NoSaveReflect.TrySet(mm, m, Convert.ChangeType(shifted, mt, CultureInfo.InvariantCulture));
                float after = before;
                try { after = mm.MissionTime; } catch { }
                if (Math.Abs(after - savedT) < 2f)
                {
                    c.applied++;
                    plan?.AppendLine("  MISSIONTIME via " + m.Name + ": " + before.ToString("F1", CultureInfo.InvariantCulture) + " -> " + after.ToString("F1", CultureInfo.InvariantCulture) + " (saved " + savedT.ToString("F1", CultureInfo.InvariantCulture) + ")");
                    return;
                }
                NoSaveReflect.TrySet(mm, m, curObj); // revert: this field is not the clock base
                plan?.AppendLine("  MISSIONTIME candidate " + m.Name + " did not move MissionTime (reverted)");
            }
            c.missing++;
            plan?.AppendLine("  MISSIONTIME not restorable (no working offset field); live=" + before.ToString("F1", CultureInfo.InvariantCulture) + " saved=" + savedT.ToString("F1", CultureInfo.InvariantCulture));
        }

        private static void RestoreFactions(List<StateEntryDto> entries, StringBuilder plan, Counters c)
        {
            foreach (var e in entries)
            {
                FactionHQ hq = null;
                try { hq = FactionRegistry.HqFromName(e.target); } catch { }
                ApplyEntry(hq, e, plan, c, "FACTION");
            }
        }

        private static Dictionary<string, Objective> LiveObjectiveIndex()
        {
            var d = new Dictionary<string, Objective>(StringComparer.Ordinal);
            var runner = MissionManager.Runner;
            if (runner == null) return d;
            if (runner.ActiveObjectives != null)
                foreach (var o in runner.ActiveObjectives) { var id = o != null ? ObjectiveId(o) : null; if (id != null && !d.ContainsKey(id)) d[id] = o; }
            foreach (var cand in RunnerAllObjectiveCands)
            {
                var m = NoSaveReflect.FindMember(runner.GetType(), false, cand);
                object v;
                if (m == null || !NoSaveReflect.TryGet(runner, m, out v)) continue;
                var items = NoSaveReflect.AsList(v);
                if (items == null) continue;
                foreach (var it in items) { var o = it as Objective; var id = o != null ? ObjectiveId(o) : null; if (id != null && !d.ContainsKey(id)) d[id] = o; }
            }
            return d;
        }

        private static void RestoreObjectives(List<StateEntryDto> entries, StringBuilder plan, Counters c)
        {
            var idx = LiveObjectiveIndex();
            foreach (var e in entries)
            {
                Objective o; idx.TryGetValue(e.target ?? "", out o);
                ApplyEntry(o, e, plan, c, "OBJECTIVE");
            }
        }

        /// <summary>Status field write for ALL objectives found (confirmed public field). Never Complete().</summary>
        private static void RestoreObjectiveStatusAll(List<StateEntryDto> entries, StringBuilder plan, Counters c)
        {
            var idx = LiveObjectiveIndex();
            foreach (var e in entries)
            {
                Objective o;
                if (!idx.TryGetValue(e.target ?? "", out o) || o == null) { c.missing++; continue; }
                ObjectiveStatus want;
                if (!Enum.TryParse(e.value, true, out want)) { c.failed++; continue; }
                try
                {
                    if (o.Status.Equals(want)) { c.unchanged++; continue; }
                    var was = o.Status;
                    o.Status = want;
                    c.applied++;
                    plan?.AppendLine("  OBJECTIVE-ALL Status " + e.target + " " + was + " -> " + want + " (field only, no Outcomes)");
                }
                catch (Exception ex) { c.failed++; plan?.AppendLine("  OBJECTIVE-ALL FAIL " + e.target + ": " + ex.Message); }
            }
        }

        private static Unit FindUnitByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                if (UnitRegistry.customIDLookup != null && UnitRegistry.customIDLookup.TryGetValue(name, out var u) && u != null)
                    return u as Unit;
            }
            catch { }
            try { foreach (var x in UnitRegistry.allUnits) if (x != null && x.UniqueName == name) return x; } catch { } // v0.9.8: respawned units often not in customIDLookup
            return null;
        }

        private static void RestoreUnitOwners(List<StateEntryDto> entries, StringBuilder plan, Counters c)
        {
            foreach (var e in entries)
            {
                var u = FindUnitByName(e.target);
                if (u == null) { c.missing++; continue; }
                string liveFn = null;
                try { liveFn = FactionName(u.NetworkHQ); } catch { }
                if (string.Equals(liveFn, e.value, StringComparison.Ordinal)) { c.unchanged++; continue; }
                var m = NoSaveReflect.FindMember(u.GetType(), false, UnitOwnerSetCands);
                if (m == null) { c.missing++; continue; }
                ApplyEntry(u, new StateEntryDto { scope = e.scope, target = e.target, key = m.Name, vtype = "faction", value = e.value }, plan, c, "OWNER");
            }
        }

        private static void RestoreAmmo(List<StateEntryDto> entries, StringBuilder plan, Counters c)
        {
            // Group by unit so each unit's components are scanned once.
            var byUnit = new Dictionary<string, List<StateEntryDto>>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                List<StateEntryDto> l;
                if (!byUnit.TryGetValue(e.target ?? "", out l)) { l = new List<StateEntryDto>(); byUnit[e.target ?? ""] = l; }
                l.Add(e);
            }
            foreach (var kv in byUnit)
            {
                var u = FindUnitByName(kv.Key);
                if (u == null) { c.missing += kv.Value.Count; continue; }
                MonoBehaviour[] comps;
                try { comps = u.GetComponentsInChildren<MonoBehaviour>(true); } catch { c.failed += kv.Value.Count; continue; }
                var keyed = new Dictionary<string, MonoBehaviour>(StringComparer.Ordinal);
                var perType = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var comp in comps)
                {
                    if (comp == null) continue;
                    var ct = comp.GetType();
                    if (!IsAmmoComponent(ct)) continue;
                    string tn = ct.Name;
                    int idx; perType.TryGetValue(tn, out idx); perType[tn] = idx + 1;
                    keyed[tn + "#" + idx.ToString(CultureInfo.InvariantCulture)] = comp;
                }
                int applied0 = c.applied;
                foreach (var e in kv.Value)
                {
                    int slash = e.key != null ? e.key.LastIndexOf('/') : -1;
                    if (slash <= 0) { c.failed++; continue; }
                    MonoBehaviour comp;
                    if (!keyed.TryGetValue(e.key.Substring(0, slash), out comp)) { c.missing++; continue; }
                    ApplyEntry(comp, new StateEntryDto { scope = e.scope, target = e.target, key = e.key.Substring(slash + 1), vtype = e.vtype, value = e.value }, null, c, "AMMO");
                }
                if (c.applied > applied0) plan?.AppendLine("  AMMO " + kv.Key + " applied=" + (c.applied - applied0));
            }
        }

        private static void RestoreAirbases(List<StateEntryDto> entries, StringBuilder plan, Counters c)
        {
            Type t;
            var abs = FindAirbases(out t);
            var byId = new Dictionary<string, Component>(StringComparer.Ordinal);
            foreach (var ab in abs) { if (ab == null) continue; var id = AirbaseId(ab); if (!byId.ContainsKey(id)) byId[id] = ab; }
            foreach (var e in entries)
            {
                Component ab;
                if (!byId.TryGetValue(e.target ?? "", out ab)) { c.missing++; continue; }
                if (e.vtype == "faction")
                {
                    var m = NoSaveReflect.FindMember(ab.GetType(), false, e.key);
                    object cur = null;
                    if (m != null) NoSaveReflect.TryGet(ab, m, out cur);
                    FactionHQ want = null;
                    if (!string.IsNullOrEmpty(e.value)) { try { want = FactionRegistry.HqFromName(e.value); } catch { } }
                    if (ReferenceEquals(cur, want)) { c.unchanged++; continue; }
                    // Prefer a capture method (fires the game's own ownership change), then direct member write.
                    if (want != null && NoSaveReflect.TryInvoke1(ab, want, typeof(FactionHQ), AirbaseCaptureMethodCands))
                    {
                        c.applied++;
                        plan?.AppendLine("  AIRBASE " + e.target + " owner -> " + e.value + " (method)");
                        continue;
                    }
                    ApplyEntry(ab, e, plan, c, "AIRBASE");
                }
                else ApplyEntry(ab, e, plan, c, "AIRBASE");
            }
        }
    }
}
