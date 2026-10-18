using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using NuclearOption.SavedMission;
using NuclearOption.SavedMission.Objectives;

namespace NOSaveSystem
{
    /// <summary>
    /// v0.9.11: objective / UniqueName registry repair after a NOSave restore.
    /// Spawner.TrySpawnShip/TrySpawnVehicle (used by NOSave respawn) skip Spawner.AfterSpawn, so the
    /// respawned unit is never put into UnitRegistry.customIDLookup (host RegisterUnit doesn't do it).
    /// DestroyUnitObjective.CheckComplete only completes when the name IS in customIDLookup with a
    /// null/disabled unit, so Carrier Duel "Sink AssaultCarrier1/FleetCarrier1" stayed Running forever.
    /// </summary>
    internal static class NoSaveV0911
    {
        private static void Log(string m) { NOSaveSystemPlugin.Log?.LogInfo("v0911: " + m); }

        /// <summary>Mimics Spawner.AfterSpawn for a unit spawned via TrySpawnShip/TrySpawnVehicle.</summary>
        internal static void RegisterRespawned(SavedUnit saved, Unit unit)
        {
            if (unit == null) return;
            try { if (saved != null && unit.SavedUnit == null) unit.LinkSavedUnit(saved); } catch (Exception ex) { NOSaveSystemPlugin.Log?.LogWarning("v0911 LinkSavedUnit: " + ex.Message); }
            string name = null;
            try { name = saved != null && !string.IsNullOrEmpty(saved.UniqueName) ? saved.UniqueName : unit.UniqueName; } catch { }
            TryRegister(name, unit);
        }

        /// <returns>true if newly registered.</returns>
        internal static bool TryRegister(string name, Unit unit)
        {
            if (string.IsNullOrEmpty(name) || unit == null || UnitRegistry.customIDLookup == null) return false;
            try
            {
                if (UnitRegistry.customIDLookup.TryGetValue(name, out var cur))
                {
                    if (ReferenceEquals(cur, unit)) return false;
                    if (cur != null && !cur.disabled) return false; // a different live unit owns it; leave it
                    UnitRegistry.customIDLookup[name] = unit;     // stale/destroyed holder -> replace
                    return true;
                }
                UnitRegistry.RegisterCustomID(name, unit);
                return true;
            }
            catch (Exception ex) { NOSaveSystemPlugin.Log?.LogWarning("v0911 RegisterCustomID '" + name + "': " + ex.Message); return false; }
        }

        private static Unit FindLiveByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                foreach (var u in UnitRegistry.allUnits)
                    if (u != null && u.UniqueName == name) return u;
                foreach (var a in UnitRegistry.allAircraft)
                    if (a != null && a.UniqueName == name) return a;
            }
            catch { }
            return null;
        }

        private static HashSet<string> SavedNames(MidMissionSnapshot snap)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (snap == null) return set;
            try
            {
                if (snap.aircraft != null) foreach (var x in snap.aircraft) if (x != null && !string.IsNullOrEmpty(x.private_uniqueName)) set.Add(x.private_uniqueName);
                if (snap.vehicles != null) foreach (var x in snap.vehicles) if (x != null && !string.IsNullOrEmpty(x.private_uniqueName)) set.Add(x.private_uniqueName);
                if (snap.ships != null) foreach (var x in snap.ships) if (x != null && !string.IsNullOrEmpty(x.private_uniqueName)) set.Add(x.private_uniqueName);
                if (snap.otherUnits != null) foreach (var x in snap.otherUnits) if (x != null && !string.IsNullOrEmpty(x.private_uniqueName)) set.Add(x.private_uniqueName);
            }
            catch { }
            return set;
        }

        private static FieldInfo _allItemsField;
        private static List<SavedUnit> GetTargets(DestroyUnitObjective o)
        {
            try
            {
                if (_allItemsField == null)
                {
                    for (var t = typeof(DestroyUnitObjective); t != null && _allItemsField == null; t = t.BaseType)
                        _allItemsField = t.GetField("allItems", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                }
                return _allItemsField?.GetValue(o) as List<SavedUnit>;
            }
            catch { return null; }
        }

        private static string ObjName(Objective o)
        {
            try { return o.SavedObjective?.UniqueName ?? o.GetType().Name; } catch { return "?"; }
        }

        /// <summary>
        /// Call at the end of a restore (after respawns + objective status apply).
        /// 1) every saved unit name with a live unit but no customIDLookup entry gets registered;
        /// 2) every DestroyUnitObjective target is either bound to its live unit, or - if the unit is
        ///    absent from the save (destroyed before F5) and no live unit holds the name - marked
        ///    destroyed (customIDLookup[name] = null), exactly the state vanilla CheckComplete treats as destroyed,
        ///    so MissionRunner.Update completes the objective on the next tick and fires its outcomes.
        /// </summary>
        internal static void FixObjectiveRegistrations(MidMissionSnapshot snap, StringBuilder plan)
        {
            var reg = new List<string>();
            var markedDestroyed = new List<string>();
            var saved = SavedNames(snap);
            if (UnitRegistry.customIDLookup == null) { Log("customIDLookup null - skipped"); return; }

            foreach (var name in saved)
            {
                if (UnitRegistry.customIDLookup.TryGetValue(name, out var cur) && cur != null) continue;
                var live = FindLiveByName(name);
                if (live != null && !live.disabled && TryRegister(name, live)) reg.Add(name);
            }

            var status = new StringBuilder();
            var runner = MissionManager.Runner;
            if (runner == null) status.Append(" (no MissionRunner)");
            else
            {
                List<Objective> active;
                try { active = new List<Objective>(runner.ActiveObjectives); } catch { active = new List<Objective>(); }
                foreach (var obj in active)
                {
                    var duo = obj as DestroyUnitObjective;
                    if (duo == null) continue;
                    var targets = GetTargets(duo);
                    if (targets == null) continue;
                    foreach (var su in targets)
                    {
                        if (su == null) continue;
                        string n = null;
                        try { n = su.UniqueName; } catch { }
                        if (string.IsNullOrEmpty(n)) continue;
                        UnitRegistry.customIDLookup.TryGetValue(n, out var holder);
                        bool present = UnitRegistry.customIDLookup.ContainsKey(n);
                        if (present && holder != null)
                        {
                            try { if (holder.SavedUnit == null) holder.LinkSavedUnit(su); } catch { }
                            continue;
                        }
                        if (present) continue; // already null = destroyed state
                        var live = FindLiveByName(n);
                        if (live != null)
                        {
                            try { if (live.SavedUnit == null) live.LinkSavedUnit(su); } catch { }
                            if (TryRegister(n, live)) reg.Add(n);
                        }
                        else if (!saved.Contains(n))
                        {
                            UnitRegistry.customIDLookup[n] = null; // destroyed before save -> vanilla "destroyed" state
                            markedDestroyed.Add(n);
                        }
                        else
                        {
                            status.Append(" WARN target '" + n + "' is in save but has no live unit (respawn failed?)");
                        }
                    }
                }
                foreach (var obj in active)
                {
                    status.Append(" [" + ObjName(obj) + "=" + SafeStatus(obj));
                    var duo = obj as DestroyUnitObjective;
                    var targets = duo != null ? GetTargets(duo) : null;
                    if (targets != null)
                    {
                        status.Append(" targets:");
                        foreach (var su in targets)
                        {
                            if (su == null) continue;
                            string n = su.UniqueName;
                            string st = !UnitRegistry.customIDLookup.TryGetValue(n, out var h) ? "UNREGISTERED" : (h == null ? "destroyed" : (h.disabled ? "disabled" : "alive"));
                            status.Append(" " + n + "(" + st + ")");
                        }
                    }
                    status.Append("]");
                }
            }

            string line = "post-restore re-registered=[" + string.Join(",", reg) + "] markedDestroyed(absent from save)=[" + string.Join(",", markedDestroyed) + "] objectives:" + status;
            Log(line);
            plan?.AppendLine("  V0911 " + line);
            try { NOSaveSystemPlugin.Instance?.StartCoroutine(LogLater()); } catch { }
        }

        private static string SafeStatus(Objective o) { try { return o.Status.ToString(); } catch { return "?"; } }

        private static IEnumerator LogLater()
        {
            yield return new UnityEngine.WaitForSecondsRealtime(3f);
            try
            {
                var runner = MissionManager.Runner;
                if (runner == null) yield break;
                var sb = new StringBuilder();
                foreach (var o in runner.ActiveObjectives) sb.Append(" [" + ObjName(o) + "=" + SafeStatus(o) + "]");
                Log("objectives 3s after restore (still active):" + (sb.Length == 0 ? " none" : sb.ToString()));
            }
            catch { }
        }
    }
}
