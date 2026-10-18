using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using NuclearOption.SavedMission;
using UnityEngine;

namespace NOSaveSystem
{
    internal static class NoSaveDeepState
    {
        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static readonly Assembly GameAsm = typeof(Unit).Assembly;
        private static void Log(string m) { NOSaveSystemPlugin.Log?.LogInfo("deep: " + m); }

        private static readonly HashSet<string> BlockNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "persistentID", "UniqueName", "NetworkUniqueName", "gearState", "gearDeployed", "netId", "syncVarDirtyBits",
            "_targetID", "ownerID", "startPosition", "startRotation", "startingVelocity", "startOffsetFromOwner",
            "playerRef", "PlayerRef", "networked", "spawnTime", "pilotNumber", "simulatedThrottle", "powerRequests", "totalPowerRequested", "ammo", "Ammo",
            "trackingDatabase", "strategicTargets", "hangars", "registered", "initialized", "isInitialized",
        };
        private static readonly HashSet<string> BlockRecurseTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "AIPilotTaxiState", "AIPilotTakeoffState", "AIPilotLandingState", "AIPilotShortLandingState",
            "AIHeloTakeoffState", "AIHeloLandingState", "PathfindingAgent", "RoadNetwork", "ControlInputs", "PilotPlayerState",
        };

        internal static int Skipped, Written, Failed, Rebased, EngineSkipped;

        private static bool EngineOwned(string compKey)
        {
            int c = compKey.LastIndexOf(':'); if (c < 0) return false;
            int h = compKey.IndexOf('#', c); string tn = h > c ? compKey.Substring(c + 1, h - c - 1) : compKey.Substring(c + 1);
            return NoSaveEngineGear.EngineTypes.Contains(tn);
        }

        private static string F(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }
        private static string Esc(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r"); }
        private static string Unesc(string s)
        {
            if (s == null || s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length) { char c = s[++i]; sb.Append(c == 't' ? '\t' : c == 'n' ? '\n' : c == 'r' ? '\r' : c); }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private static bool IsPrim(Type t) { return t.IsPrimitive || t.IsEnum || t == typeof(string); }

        private static string Encode(Type t, object v, out string tag)
        {
            tag = null;
            if (t == typeof(float)) { tag = "f"; return F((float)v); }
            if (t == typeof(double)) { tag = "d"; return ((double)v).ToString("R", CultureInfo.InvariantCulture); }
            if (t == typeof(bool)) { tag = "b"; return (bool)v ? "1" : "0"; }
            if (t == typeof(string)) { tag = "s"; return v == null ? "\u0000" : (string)v; }
            if (t.IsEnum) { tag = "e"; return Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture); }
            if (t.IsPrimitive) { tag = "i"; return Convert.ToString(v, CultureInfo.InvariantCulture); }
            if (t == typeof(Vector3)) { tag = "v3"; var x = (Vector3)v; return F(x.x) + "," + F(x.y) + "," + F(x.z); }
            if (t == typeof(Vector2)) { tag = "v2"; var x = (Vector2)v; return F(x.x) + "," + F(x.y); }
            if (t == typeof(Vector4)) { tag = "v4"; var x = (Vector4)v; return F(x.x) + "," + F(x.y) + "," + F(x.z) + "," + F(x.w); }
            if (t == typeof(Quaternion)) { tag = "q"; var x = (Quaternion)v; return F(x.x) + "," + F(x.y) + "," + F(x.z) + "," + F(x.w); }
            if (t == typeof(Color)) { tag = "c"; var x = (Color)v; return F(x.r) + "," + F(x.g) + "," + F(x.b) + "," + F(x.a); }
            if (t == typeof(GlobalPosition)) { tag = "g"; var x = (GlobalPosition)v; return F(x.x) + "," + F(x.y) + "," + F(x.z); }
            if (t == typeof(PersistentID)) { tag = "p"; try { if (UnitRegistry.TryGetUnit((PersistentID)v, out var u) && u != null) return u.UniqueName ?? ""; } catch { } return ""; }
            if (typeof(Unit).IsAssignableFrom(t)) { tag = "u"; var u = v as Unit; try { return (u != null && !u.Equals(null)) ? (u.UniqueName ?? "") : ""; } catch { return ""; } }
            if (t.IsArray && IsPrim(t.GetElementType()) && t.GetElementType() != typeof(string))
            {
                tag = "a:" + t.GetElementType().FullName;
                var arr = v as Array; if (arr == null) return "\u0000";
                var parts = new string[arr.Length];
                for (int i = 0; i < arr.Length; i++) parts[i] = Convert.ToString(arr.GetValue(i), CultureInfo.InvariantCulture);
                return string.Join("\u001f", parts);
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                var et = t.GetGenericArguments()[0];
                var list = v as IList;
                if (IsPrim(et) && et != typeof(string) && !et.IsEnum)
                {
                    tag = "l:" + et.FullName; if (list == null) return "\u0000";
                    var parts = new string[list.Count];
                    for (int i = 0; i < list.Count; i++) parts[i] = Convert.ToString(list[i], CultureInfo.InvariantCulture);
                    return string.Join("\u001f", parts);
                }
                if (typeof(Unit).IsAssignableFrom(et))
                {
                    tag = "lu"; if (list == null) return "\u0000";
                    var parts = new List<string>();
                    foreach (var o in list) { var u = o as Unit; try { if (u != null && !u.Equals(null) && !string.IsNullOrEmpty(u.UniqueName)) parts.Add(u.UniqueName); } catch { } }
                    return string.Join("\u001f", parts.ToArray());
                }
            }
            return null;
        }

        private static float[] Fl(string s) { var p = s.Split(','); var r = new float[p.Length]; for (int i = 0; i < p.Length; i++) r[i] = float.Parse(p[i], CultureInfo.InvariantCulture); return r; }

        private static bool Decode(Type t, string tag, string s, out object v, out bool unresolved)
        {
            v = null; unresolved = false;
            switch (tag)
            {
                case "f": v = float.Parse(s, CultureInfo.InvariantCulture); return t == typeof(float);
                case "d": v = double.Parse(s, CultureInfo.InvariantCulture); return t == typeof(double);
                case "b": v = s == "1"; return t == typeof(bool);
                case "s": v = s == "\u0000" ? null : s; return t == typeof(string);
                case "e": if (!t.IsEnum) return false; v = Enum.ToObject(t, long.Parse(s, CultureInfo.InvariantCulture)); return true;
                case "i": if (!t.IsPrimitive) return false; v = Convert.ChangeType(s, t, CultureInfo.InvariantCulture); return true;
                case "v3": { var f = Fl(s); v = new Vector3(f[0], f[1], f[2]); return t == typeof(Vector3); }
                case "v2": { var f = Fl(s); v = new Vector2(f[0], f[1]); return t == typeof(Vector2); }
                case "v4": { var f = Fl(s); v = new Vector4(f[0], f[1], f[2], f[3]); return t == typeof(Vector4); }
                case "q": { var f = Fl(s); v = new Quaternion(f[0], f[1], f[2], f[3]); return t == typeof(Quaternion); }
                case "c": { var f = Fl(s); v = new Color(f[0], f[1], f[2], f[3]); return t == typeof(Color); }
                case "g": { var f = Fl(s); v = new GlobalPosition(f[0], f[1], f[2]); return t == typeof(GlobalPosition); }
                case "p":
                    if (t != typeof(PersistentID)) return false;
                    if (s == "") { v = PersistentID.None; return true; }
                    { var u = Find(s); if (u == null) { unresolved = true; return false; } v = u.persistentID; return true; }
                case "u":
                    if (!typeof(Unit).IsAssignableFrom(t)) return false;
                    if (s == "") { v = null; return true; }
                    { var u = Find(s); if (u == null || !t.IsInstanceOfType(u)) { unresolved = true; return false; } v = u; return true; }
                case "lu":
                    {
                        if (!(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))) return false;
                        var et = t.GetGenericArguments()[0];
                        var list = (IList)Activator.CreateInstance(t);
                        if (s != "\u0000" && s != "")
                            foreach (var n in s.Split('\u001f')) { var u = Find(n); if (u == null || !et.IsInstanceOfType(u)) unresolved = true; else list.Add(u); }
                        v = list; return true;
                    }
            }
            if (tag.StartsWith("a:", StringComparison.Ordinal) || tag.StartsWith("l:", StringComparison.Ordinal))
            {
                bool isArr = tag[0] == 'a';
                Type et = isArr ? (t.IsArray ? t.GetElementType() : null) : (t.IsGenericType ? t.GetGenericArguments()[0] : null);
                if (et == null || et.FullName != tag.Substring(2)) return false;
                if (s == "\u0000") { v = null; return true; }
                var parts = s.Length == 0 ? new string[0] : s.Split('\u001f');
                if (isArr) { var arr = Array.CreateInstance(et, parts.Length); for (int i = 0; i < parts.Length; i++) arr.SetValue(Convert.ChangeType(parts[i], et, CultureInfo.InvariantCulture), i); v = arr; }
                else { var list = (IList)Activator.CreateInstance(t); foreach (var p in parts) list.Add(Convert.ChangeType(p, et, CultureInfo.InvariantCulture)); v = list; }
                return true;
            }
            return false;
        }

        private static Dictionary<string, Unit> _byName;
        private static Unit Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Unit u = null;
            try { UnitRegistry.customIDLookup.TryGetValue(name, out u); } catch { }
            if (u != null) return u;
            if (_byName == null)
            {
                _byName = new Dictionary<string, Unit>(StringComparer.Ordinal);
                try { foreach (var x in UnitRegistry.allUnits) if (x != null && !string.IsNullOrEmpty(x.UniqueName) && !_byName.ContainsKey(x.UniqueName)) _byName[x.UniqueName] = x; } catch { }
            }
            _byName.TryGetValue(name, out u);
            return u;
        }

        private static readonly Dictionary<Type, FieldInfo[]> _fieldCache = new Dictionary<Type, FieldInfo[]>();
        private static FieldInfo[] Fields(Type t)
        {
            if (t == null) return Array.Empty<FieldInfo>();
            if (_fieldCache.TryGetValue(t, out var cached)) return cached;
            var list = new List<FieldInfo>();
            for (var cur = t; cur != null && cur.Assembly == GameAsm; cur = cur.BaseType)
                foreach (var f in cur.GetFields(BF))
                {
                    if (f.IsLiteral || f.IsInitOnly) continue;
                    if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                    list.Add(f);
                }
            var arr = list.ToArray();
            _fieldCache[t] = arr;
            return arr;
        }

        private static void Collect(object obj, string prefix, Dictionary<string, string> outMap, int depth, HashSet<object> seen)
        {
            if (obj == null || depth > 2) return;
            if (!seen.Add(obj)) return;
            foreach (var f in Fields(obj.GetType()))
            {
                if (BlockNames.Contains(f.Name)) { Skipped++; continue; }
                object v;
                try { v = f.GetValue(obj); } catch { Skipped++; continue; }
                var ft = f.FieldType;
                string tag; string enc = null;
                try { enc = Encode(ft, v, out tag); } catch { tag = null; }
                if (enc != null) { outMap[prefix + f.Name] = tag + "\t" + enc; continue; }
                if (v != null && !(v is UnityEngine.Object) && v.GetType().IsClass && v.GetType().Assembly == GameAsm
                    && !BlockRecurseTypes.Contains(v.GetType().Name) && !(v is Unit))
                {
                    Collect(v, prefix + f.Name + "{" + v.GetType().Name + "}.", outMap, depth + 1, seen);
                    continue;
                }
                Skipped++;
            }
        }

        private static string Path(Transform root, Transform t)
        {
            var parts = new List<string>();
            for (var c = t; c != null && c != root; c = c.parent) parts.Add(c.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private static Dictionary<string, string> CollectGameObject(GameObject go)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var counter = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || mb.GetType().Assembly != GameAsm) continue;
                if (NoSaveEngineGear.EngineTypes.Contains(mb.GetType().Name)) continue;
                string ck = Path(go.transform, mb.transform) + ":" + mb.GetType().Name;
                counter.TryGetValue(ck, out int n); counter[ck] = n + 1;
                Collect(mb, ck + "#" + n + "|", map, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
            }
            return map;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }

        private static readonly Dictionary<string, Dictionary<string, string>> PrefabCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private static Dictionary<string, string> PrefabMap(Unit u)
        {
            string key = null;
            try { var def = u.definition; key = def != null ? (string.IsNullOrEmpty(def.jsonKey) ? def.unitName : def.jsonKey) : null; } catch { }
            if (key == null) return null;
            if (PrefabCache.TryGetValue(key, out var m)) return m;
            m = null;
            try { if (Encyclopedia.i.TryGetPrefab(key, out var prefab) && prefab != null) m = CollectGameObject(prefab); } catch { }
            PrefabCache[key] = m;
            return m;
        }

        private static void DeleteSidecar(string path, string why)
        {
            foreach (var p in new[] { path, path + ".tmp" })
            {
                try { if (File.Exists(p)) { File.Delete(p); Log("removed " + p + " (" + why + ")"); } }
                catch (Exception ex) { Log("remove FAIL " + p + ": " + ex.Message); }
            }
        }

        internal static void Save(string snapshotPath) { Save(snapshotPath, null); }

        internal static void Save(string snapshotPath, string manifestUtc)
        {
            if (!NOSaveSystemPlugin.DeepStateEnabled) { Log("disabled by config"); DeleteSidecar(snapshotPath + ".deep", "deep state disabled"); return; }
            var sw = Stopwatch.StartNew();
            Skipped = 0;
            var sb = new StringBuilder(1 << 20);
            float now = Time.timeSinceLevelLoad;
            float mt = 0f; try { mt = NetworkSceneSingleton<MissionManager>.i.MissionTime; } catch { }
            sb.Append("#NOSaveDeep\t2\t").Append(F(now)).Append('\t').Append(F(mt)).Append('\t').Append(Esc(manifestUtc ?? "")).Append('\n');
            int objs = 0, lines = 0;
            void Emit(string owner, Dictionary<string, string> map, Dictionary<string, string> prefab)
            {
                foreach (var kv in map)
                {
                    if (prefab != null && prefab.TryGetValue(kv.Key, out var pv) && pv == kv.Value) continue;
                    sb.Append(Esc(owner)).Append('\t').Append(Esc(kv.Key)).Append('\t').Append(kv.Value.Substring(0, kv.Value.IndexOf('\t')))
                      .Append('\t').Append(Esc(kv.Value.Substring(kv.Value.IndexOf('\t') + 1))).Append('\n');
                    lines++;
                }
                objs++;
            }
            try
            {
                foreach (var u in UnitRegistry.allUnits.ToArray())
                {
                    try
                    {
                        if (u == null || u.Equals(null) || string.IsNullOrEmpty(u.UniqueName)) continue;
                        Emit("U:" + u.UniqueName, CollectGameObject(u.gameObject), PrefabMap(u));
                    }
                    catch (Exception ex) { Log("save unit FAIL " + ex.Message); }
                }
                foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
                {
                    try
                    {
                        if (hq == null || hq.faction == null) continue;
                        var m = new Dictionary<string, string>(StringComparer.Ordinal);
                        Collect(hq, "HQ|", m, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
                        Emit("HQ:" + hq.faction.factionName, m, null);
                    }
                    catch { }
                }
                foreach (var ab in UnityEngine.Object.FindObjectsOfType<Airbase>())
                {
                    try
                    {
                        var m = new Dictionary<string, string>(StringComparer.Ordinal);
                        Collect(ab, "AB|", m, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
                        Emit("AB:" + AirbaseKey(ab), m, null);
                    }
                    catch { }
                }
                try
                {
                    foreach (var o in MissionManager.Objectives.AllObjectives)
                    {
                        if (o == null) continue;
                        var m = new Dictionary<string, string>(StringComparer.Ordinal);
                        Collect(o, "OBJ|", m, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
                        Emit("OBJ:" + o.SavedObjective.UniqueName, m, null);
                    }
                }
                catch (Exception ex) { Log("objectives FAIL " + ex.Message); }
                foreach (var u in UnitRegistry.allUnits.ToArray())
                {
                    try
                    {
                        if (u is GroundVehicle gv && !string.IsNullOrEmpty(gv.UniqueName))
                        {
                            var f = typeof(GroundVehicle).GetField("savedWaypoints", BF);
                            var wps = f?.GetValue(gv) as List<VehicleWaypoint>;
                            var wsb = new StringBuilder();
                            if (wps != null) foreach (var w in wps) { if (w == null) continue; if (wsb.Length > 0) wsb.Append('\u001e'); wsb.Append(F(w.position.x)).Append(',').Append(F(w.position.y)).Append(',').Append(F(w.position.z)).Append('\u001d').Append(w.objective ?? ""); }
                            var d = gv.GetDestination();
                            sb.Append("X:route\t").Append(Esc(gv.UniqueName)).Append('\t').Append(F(d.x)).Append(',').Append(F(d.y)).Append(',').Append(F(d.z))
                              .Append('\t').Append(Esc(wsb.ToString())).Append('\n'); lines++;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log("save FAIL " + ex); }
            var path = snapshotPath + ".deep";
            try
            {
                string tmp = path + ".tmp";
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                var bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }
                if (File.Exists(path))
                {
                    bool replaced = false;
                    try { File.Replace(tmp, path, null); replaced = true; }
                    catch (Exception rex) { Log("File.Replace unavailable (" + rex.Message + ") - copy fallback"); }
                    if (!replaced) { File.Copy(tmp, path, true); try { File.Delete(tmp); } catch { } }
                }
                else File.Move(tmp, path);
            }
            catch (Exception ex) { Log("write FAIL " + ex.Message); DeleteSidecar(path, "write failed"); return; }
            sw.Stop();
            long size = 0; try { size = new FileInfo(path).Length; } catch { }
            Log("saved objects=" + objs + " lines=" + lines + " skippedFields=" + Skipped + " size=" + (size / 1024) + "KB time=" + sw.ElapsedMilliseconds + "ms -> " + path);
            try { var arch = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(snapshotPath), "archive"); if (Directory.Exists(arch)) File.Copy(path, System.IO.Path.Combine(arch, "midmission_" + (NOSaveSystemPlugin.PendingArchiveStamp ?? DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)) + ".json.deep"), true); } catch { }
        }

        private static string AirbaseKey(Airbase ab)
        {
            try { var sa = ab.SavedAirbase; if (sa != null && !string.IsNullOrEmpty(sa.UniqueName)) return sa.UniqueName; } catch { }
            return ab.name;
        }

        private class Line { public string owner, key, tag, val; }
        private static List<Line> _pending;
        internal static float SavedLevelTime, SavedMissionTime;
        internal static bool HaveClock;
        internal static string SavedUtc;

        private static List<Line> Read(string snapshotPath)
        {
            var path = snapshotPath + ".deep";
            var list = new List<Line>();
            HaveClock = false;
            SavedUtc = null;
            if (!File.Exists(path)) { Log("no sidecar " + path + " (pre-0.9.7 save)"); return list; }
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (raw.StartsWith("#NOSaveDeep", StringComparison.Ordinal))
                {
                    var h = raw.Split('\t');
                    if (h.Length >= 4) { SavedLevelTime = float.Parse(h[2], CultureInfo.InvariantCulture); SavedMissionTime = float.Parse(h[3], CultureInfo.InvariantCulture); HaveClock = true; }
                    if (h.Length >= 5) { var u = Unesc(h[4]); SavedUtc = string.IsNullOrEmpty(u) ? null : u; }
                    continue;
                }
                var p = raw.Split('\t');
                if (p.Length < 4) continue;
                list.Add(new Line { owner = Unesc(p[0]), key = Unesc(p[1]), tag = p[2], val = Unesc(p[3]) });
            }
            Log("read lines=" + list.Count + " savedLevelTime=" + SavedLevelTime + " savedMissionTime=" + SavedMissionTime);
            return list;
        }

        internal static void CompleteObjectivesEarly(MidMissionSnapshot snap, StringBuilder plan)
        {
            if (!NOSaveSystemPlugin.RunObjectiveOutcomes || snap?.activeObjectives == null) return;
            int n = 0;
            try
            {
                var saved = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var o in snap.activeObjectives) if (o != null && o.uniqueName != null) saved[o.uniqueName] = o.status;
                foreach (var o in MissionManager.Objectives.AllObjectives)
                {
                    if (o == null) continue;
                    string un = o.SavedObjective.UniqueName;
                    if (!saved.TryGetValue(un, out var st) || st != "Complete" || o.Status == ObjectiveStatus.Complete) continue;
                    try { o.Status = ObjectiveStatus.Complete; o.Complete(); n++; plan?.AppendLine("  OBJECTIVE Complete() native " + un); Log("objective Complete() " + un); }
                    catch (Exception ex) { plan?.AppendLine("  OBJECTIVE Complete FAIL " + un + ": " + ex.Message); }
                }
            }
            catch (Exception ex) { Log("objectives early FAIL " + ex.Message); }
            plan?.AppendLine("  OBJECTIVES completed-early=" + n);
        }

        internal static void Restore(string snapshotPath, StringBuilder plan) { Restore(snapshotPath, null, plan); }

        internal static void Restore(string snapshotPath, string expectedUtc, StringBuilder plan)
        {
            if (!NOSaveSystemPlugin.DeepStateEnabled) { plan?.AppendLine("  DEEP disabled by config"); return; }
            var sw = Stopwatch.StartNew();
            var lines = Read(snapshotPath);
            if (lines.Count > 0 || HaveClock)
            {
                string skip = null;
                if (SavedUtc != null && !string.IsNullOrEmpty(expectedUtc))
                {
                    if (!string.Equals(SavedUtc, expectedUtc, StringComparison.Ordinal)) skip = "sidecar manifest.utc=" + SavedUtc + " != save manifest.utc=" + expectedUtc;
                }
                else if (SavedUtc == null)
                {
                    try
                    {
                        var jt = File.GetLastWriteTimeUtc(snapshotPath); var dt = File.GetLastWriteTimeUtc(snapshotPath + ".deep");
                        if ((jt - dt).TotalSeconds > 60) skip = "legacy sidecar is older than the save (" + dt.ToString("o") + " < " + jt.ToString("o") + ")";
                    }
                    catch { }
                }
                if (skip != null)
                {
                    plan?.AppendLine("  DEEP SKIPPED: " + skip);
                    Log("restore SKIPPED: " + skip);
                    HaveClock = false; _pending = null;
                    return;
                }
            }
            if (HaveClock)
            {
                MissionClockOffset = SavedMissionTime - Time.timeSinceLevelLoad;
                plan?.AppendLine("  CLOCK mission time offset=" + F(MissionClockOffset) + " (saved MissionTime=" + F(SavedMissionTime) + ")");
                Log("mission clock offset=" + MissionClockOffset);
            }
            if (lines.Count == 0) return;
            _pending = Apply(lines, plan, "pass1");
            ApplyRoutesAndFires(lines, plan);
            sw.Stop();
            plan?.AppendLine("  DEEP pass1 written=" + Written + " failed=" + Failed + " rebasedTimestamps=" + Rebased + " unresolved=" + _pending.Count + " engineOwnedSkipped=" + EngineSkipped + " time=" + sw.ElapsedMilliseconds + "ms");
            Log("restore pass1 written=" + Written + " failed=" + Failed + " rebased=" + Rebased + " unresolved=" + _pending.Count + " engineOwnedSkipped=" + EngineSkipped + " time=" + sw.ElapsedMilliseconds + "ms");
            if (NOSaveSystemPlugin.Instance != null)
                NOSaveSystemPlugin.Instance.StartCoroutine(NoSaveCoroutines.Safe(NoSaveVerify.Track(RetryCo()), "DeepRetry"));
        }

        private static IEnumerator RetryCo()
        {
            foreach (var w in new[] { 0.5f, 1.5f, 3f })
            {
                float t = 0f; while (t < w) { t += NoSaveWait.Dt(); yield return null; }
                if (_pending == null || _pending.Count == 0) yield break;
                int before = _pending.Count;
                _pending = Apply(_pending, null, "retry+" + w + "s");
                Log("retry+" + w + "s resolved=" + (before - _pending.Count) + " remaining=" + _pending.Count);
            }
            if (_pending != null && _pending.Count > 0)
            {
                NoSaveWait.Timeout("deep-retry", 5f, "give up on " + _pending.Count + " unresolved deep-state entries (logged)");
                var sb = new StringBuilder();
                for (int i = 0; i < _pending.Count && i < 40; i++) sb.Append(_pending[i].owner).Append(' ').Append(_pending[i].key).Append("; ");
                Log("unresolved after retries (" + _pending.Count + "): " + sb);
            }
        }

        private static readonly Dictionary<string, object> _ownerCache = new Dictionary<string, object>(StringComparer.Ordinal);

        private static List<Line> Apply(List<Line> lines, StringBuilder plan, string pass)
        {
            _byName = null; _ownerCache.Clear();
            Written = 0; Failed = 0; Rebased = 0; EngineSkipped = 0;
            float delta = Time.timeSinceLevelLoad - SavedLevelTime;
            var unresolved = new List<Line>();
            var goMaps = new Dictionary<string, Dictionary<string, Component>>(StringComparer.Ordinal);
            int missingOwner = 0;
            foreach (var ln in lines)
            {
                if (ln.owner.StartsWith("X:", StringComparison.Ordinal)) continue;
                object target = null; string fieldPath = null;
                try
                {
                    int bar = ln.key.IndexOf('|');
                    if (bar < 0) continue;
                    string compKey = ln.key.Substring(0, bar);
                    fieldPath = ln.key.Substring(bar + 1);
                    if (EngineOwned(compKey)) { EngineSkipped++; continue; }
                    if (ln.owner.StartsWith("U:", StringComparison.Ordinal))
                    {
                        var u = Find(ln.owner.Substring(2));
                        if (u == null) { missingOwner++; unresolved.Add(ln); continue; }
                        if (!goMaps.TryGetValue(ln.owner, out var comps)) { comps = ComponentMap(u.gameObject); goMaps[ln.owner] = comps; }
                        comps.TryGetValue(compKey, out var c); target = c;
                    }
                    else if (ln.owner.StartsWith("HQ:", StringComparison.Ordinal)) target = FactionRegistry.HqFromName(ln.owner.Substring(3));
                    else if (ln.owner.StartsWith("AB:", StringComparison.Ordinal)) target = FindAirbase(ln.owner.Substring(3));
                    else if (ln.owner.StartsWith("OBJ:", StringComparison.Ordinal)) target = FindObjective(ln.owner.Substring(4));
                    if (target == null) { Failed++; continue; }
                    object obj = target;
                    var segs = fieldPath.Split('.');
                    bool ok = true;
                    for (int i = 0; i < segs.Length - 1 && ok; i++)
                    {
                        string seg = segs[i]; string want = null;
                        int br = seg.IndexOf('{'); if (br > 0) { want = seg.Substring(br + 1, seg.Length - br - 2); seg = seg.Substring(0, br); }
                        var f = FindField(obj.GetType(), seg);
                        var child = f?.GetValue(obj);
                        if (child == null || (want != null && child.GetType().Name != want)) ok = false; else obj = child;
                    }
                    if (!ok) { Failed++; continue; }
                    var leaf = FindField(obj.GetType(), segs[segs.Length - 1]);
                    if (leaf == null || BlockNames.Contains(leaf.Name)) { Failed++; continue; }
                    if (!Decode(leaf.FieldType, ln.tag, ln.val, out var v, out bool unr))
                    {
                        if (unr) unresolved.Add(ln); else Failed++;
                        continue;
                    }
                    if (unr) unresolved.Add(ln);
                    if (v is float fv && LooksLikeTimestamp(leaf.Name) && fv > 0.5f && fv <= SavedLevelTime + 0.5f) { v = fv + delta; Rebased++; }
                    var cur = leaf.GetValue(obj);
                    if (Equals(cur, v)) continue;
                    leaf.SetValue(obj, v);
                    Written++;
                }
                catch (Exception ex) { Failed++; if (Failed <= 20) Log(pass + " write FAIL " + ln.owner + " " + ln.key + ": " + ex.Message); }
            }
            if (Failed > 20) Log(pass + " write FAIL: +" + (Failed - 20) + " more not logged individually (total failed=" + Failed + ")");
            plan?.AppendLine("  DEEP " + pass + " ownersMissing=" + missingOwner);
            return unresolved;
        }

        private static bool LooksLikeTimestamp(string n)
        {
            string l = n.ToLowerInvariant();
            if (l.Contains("timer") || l.Contains("duration") || l.Contains("delay") || l.Contains("interval") || l.Contains("burntime") || l.Contains("timesince")) return false;
            return l.StartsWith("last") || l.EndsWith("time") || l.Contains("timestamp") || l.Contains("starttime") || l.Contains("lastspotted");
        }

        private static FieldInfo FindField(Type t, string name)
        {
            for (var cur = t; cur != null; cur = cur.BaseType)
            {
                var f = cur.GetField(name, BF);
                if (f != null) return f;
            }
            return null;
        }

        private static Dictionary<string, Component> ComponentMap(GameObject go)
        {
            var map = new Dictionary<string, Component>(StringComparer.Ordinal);
            var counter = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || mb.GetType().Assembly != GameAsm) continue;
                string ck = Path(go.transform, mb.transform) + ":" + mb.GetType().Name;
                counter.TryGetValue(ck, out int n); counter[ck] = n + 1;
                map[ck + "#" + n] = mb;
            }
            return map;
        }

        private static object FindAirbase(string key)
        {
            if (_ownerCache.TryGetValue("AB:" + key, out var o)) return o;
            foreach (var ab in UnityEngine.Object.FindObjectsOfType<Airbase>()) if (AirbaseKey(ab) == key) { _ownerCache["AB:" + key] = ab; return ab; }
            return null;
        }

        private static object FindObjective(string un)
        {
            try { foreach (var o in MissionManager.Objectives.AllObjectives) if (o != null && o.SavedObjective.UniqueName == un) return o; } catch { }
            return null;
        }

        private static void ApplyRoutesAndFires(List<Line> lines, StringBuilder plan)
        {
            int routes = 0, fires = 0;
            foreach (var ln in lines)
            {
                if (ln.owner != "X:route") continue;
                try
                {
                    var gv = Find(ln.key) as GroundVehicle; if (gv == null) continue;
                    var d = Fl(ln.tag);
                    var wps = new List<VehicleWaypoint>();
                    if (!string.IsNullOrEmpty(ln.val))
                        foreach (var w in ln.val.Split('\u001e'))
                        {
                            var p = w.Split('\u001d'); var f = Fl(p[0]);
                            wps.Add(new VehicleWaypoint { position = new GlobalPosition(f[0], f[1], f[2]), objective = p.Length > 1 ? p[1] : "" });
                        }
                    typeof(GroundVehicle).GetField("savedWaypoints", BF)?.SetValue(gv, wps);
                    var dest = new GlobalPosition(d[0], d[1], d[2]);
                    var cur = gv.GetDestination();
                    if ((d[0] != 0f || d[2] != 0f) && gv.UnitCommand != null && (Mathf.Abs(cur.x - dest.x) > 1f || Mathf.Abs(cur.z - dest.z) > 1f))
                        gv.UnitCommand.SetDestination(dest, false);
                    routes++;
                }
                catch (Exception ex) { plan?.AppendLine("    ROUTE FAIL " + ln.key + ": " + ex.Message); }
            }
            try
            {
                var fOn = typeof(FuelTank).GetField("onFire", BF); var fRu = typeof(FuelTank).GetField("ruptured", BF); var fPart = typeof(FuelTank).GetField("part", BF);
                foreach (var ft in UnityEngine.Object.FindObjectsOfType<FuelTank>())
                {
                    bool on = (bool)(fOn?.GetValue(ft) ?? false), ru = (bool)(fRu?.GetValue(ft) ?? false);
                    if (!on && !ru) continue;
                    var part = fPart?.GetValue(ft) as UnitPart; var unit = ft.GetComponentInParent<Unit>();
                    if (part == null || unit == null) continue;
                    fOn.SetValue(ft, false); fRu.SetValue(ft, false);
                    unit.FuelTankStatus(part.id, ru, on);
                    fires++;
                }
            }
            catch (Exception ex) { plan?.AppendLine("    FIRES FAIL " + ex.Message); }
            plan?.AppendLine("  DEEP routes=" + routes + " fires/ruptures=" + fires);
            Log("routes=" + routes + " fires=" + fires);
        }

        internal static float MissionClockOffset;
        internal static void ResetClock() { MissionClockOffset = 0f; }
    }

    [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.MissionTime), MethodType.Getter)]
    internal static class Patch_MissionManager_MissionTime
    {
        static void Postfix(ref float __result)
        {
            try
            {
                if (NoSaveDeepState.MissionClockOffset != 0f && GameManager.gameState == GameState.SinglePlayer)
                    __result += NoSaveDeepState.MissionClockOffset;
            }
            catch { }
        }
    }
}
