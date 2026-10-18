using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace NOSaveSystem
{
    internal static class NoSaveVerify
    {
        private static int _busy;
        internal static int Busy { get { return _busy; } }
        internal static void BusyEnter() { _busy++; }
        internal static void BusyExit() { if (_busy > 0) _busy--; }

        internal static IEnumerator Track(IEnumerator inner)
        {
            BusyEnter();
            try { yield return inner; }
            finally { BusyExit(); }
        }

        internal const float SettleWaitMax = 3f;
        internal const float FrameBudgetMs = 2f;
        internal const float TotalMax = 5f;
        internal const float UnpauseWaitMax = 600f;
        internal const float EngineWaitMax = 15f;
        internal const int ReassertRetry = 1;

        internal const float PosFloor = 1.0f;
        internal const float PosVelK = 0.25f;
        internal const float AccAircraft = 40f;
        internal const float AccSurface = 5f;
        internal const float AccOther = 2f;
        internal const float RotFloorDeg = 1.0f;
        internal const float RotWK = 1.25f;
        internal const float AlphaAircraft = 3f;
        internal const float AlphaOther = 0.5f;
        internal const float VelFloor = 0.5f;
        internal const float VelRel = 0.05f;
        internal const float AngFloor = 0.05f;
        internal const float AngRel = 0.5f;
        internal const float FuelRel = 0.005f;
        internal const float FuelFloor = 0.001f;
        internal const float FuelRateK = 0.5f;
        internal const float TimeFloor = 0.25f;
        internal const float TimeRel = 0.02f;
        internal const float PartHpEps = 0.05f;
        internal const float FlapFloorDeg = 0.5f;
        internal const float FlapServo = 20f;
        internal const float GearHingeDeg = 1.0f;
        internal const float GearDoorEps = 0.02f;
        internal const float EngineRelFloor = 0.01f;
        internal const float EngineAbs = 0.005f;
        internal const float DeadEps = 0.02f;
        internal const float ThrottleSlack = 0.02f;
        internal const float SmallEps = 1e-4f;
        internal const float EscalationEps = 0.01f;
        internal const float FundsEps = 0.5f;
        internal const float ObjPctEps = 0.01f;

        private static int _gen;
        private static float _armSim;
        private static readonly Stopwatch _sw = new Stopwatch();
        private static readonly StringBuilder _sb = new StringBuilder(16384);
        private static readonly StringBuilder _tmp = new StringBuilder(256);
        private static readonly Dictionary<string, float> _fuel0 = new Dictionary<string, float>(64, StringComparer.Ordinal);
        private static float _fuel0At;
        private static int _ok, _mis, _skip;
        private static string _unit; private static bool _logAll; private static int _uOk, _uN, _uSkipQuiet;
        private static HashSet<string> _filter;
        private static readonly List<string> _misKeys = new List<string>(64);
        private static readonly HashSet<string> _misUnits = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<string> _reassertGroups = new List<string>(8);
        private static bool _thrLower, _holdActive;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static bool _f0Pending, _f0Valid;
        private static List<FactionEconomyDto> _f0Fac;
        private static SnapshotMission _f0Mis;
        private static readonly Dictionary<string, Dictionary<string, string>> _f0Ammo = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private static readonly List<string> _f0Notes = new List<string>(16);
        private struct Kin { public Vector3 p; public Quaternion q; public bool hasP, hasQ; }
        private static readonly Dictionary<string, Kin> _f0Kin = new Dictionary<string, Kin>(StringComparer.Ordinal);
        private static bool _f0KinValid;

        internal static void ResetFrame0() { _f0Pending = false; _f0Valid = false; _f0Fac = null; _f0Mis = null; _f0Ammo.Clear(); _f0Notes.Clear(); _f0Kin.Clear(); _f0KinValid = false; }

        internal static void CaptureFrame0(MidMissionSnapshot snap)
        {
            ResetFrame0();
            if (snap == null) return;
            var sb = new StringBuilder();
            try
            {
                var fac = MidMissionV03Helpers.CaptureFactions();
                int facMis = 0;
                if (snap.factions != null && fac != null)
                    foreach (var s in snap.factions)
                    {
                        if (s == null) continue;
                        FactionEconomyDto l = null; foreach (var x in fac) if (x != null && x.factionName == s.factionName) { l = x; break; }
                        if (l == null || Mathf.Abs(l.factionFunds - s.factionFunds) > FundsEps || Mathf.Abs(l.factionScore - s.factionScore) > FundsEps) facMis++;
                    }
                if (facMis > 0)
                {
                    int n = MidMissionV03Helpers.RestoreFactionEconomy(snap.factions, null);
                    _f0Notes.Add("economy mismatch at frame0 (" + facMis + " faction(s)) -> RestoreFactionEconomy applied=" + n);
                    fac = MidMissionV03Helpers.CaptureFactions();
                }
                _f0Fac = fac;
                var mis = NOSaveSystemPlugin.CaptureMissionInfo();
                if (snap.mission != null && mis != null &&
                    (Mathf.Abs(mis.currentEscalation - snap.mission.currentEscalation) > EscalationEps
                     || Mathf.Abs(mis.tacticalThreshold - snap.mission.tacticalThreshold) > EscalationEps
                     || Mathf.Abs(mis.strategicThreshold - snap.mission.strategicThreshold) > EscalationEps))
                {
                    int n = MidMissionV03Helpers.RestoreMissionBlackboard(snap.mission, true, false, null);
                    _f0Notes.Add("blackboard mismatch at frame0 -> RestoreMissionBlackboard(escalation, no missionTime) applied=" + n);
                    mis = NOSaveSystemPlugin.CaptureMissionInfo();
                }
                _f0Mis = mis;
                int ammoUnits = 0;
                foreach (var d in AllUnits(snap))
                {
                    if (string.IsNullOrEmpty(d.v096) || d.v096.IndexOf("wAmmo=", StringComparison.Ordinal) < 0) continue;
                    var u = FindUnit(d.private_uniqueName); if (u == null) continue;
                    string ex = null; try { ex = NoSaveV096.CaptureExtra(u); } catch { }
                    _f0Ammo[d.private_uniqueName] = ParseAmmo(ex); ammoUnits++;
                }
                int kinN = 0;
                foreach (var d in AllUnits(snap))
                {
                    if (string.IsNullOrEmpty(d.private_uniqueName)) continue;
                    var u = FindUnit(d.private_uniqueName); if (u == null) continue;
                    var k = new Kin();
                    try { var gp = GlobalPositionExtensions.GlobalPosition(u); k.p = new Vector3(gp.x, gp.y, gp.z); k.hasP = true; } catch { }
                    try { k.q = u.transform.rotation; k.hasQ = true; } catch { }
                    if (k.hasP || k.hasQ) { _f0Kin[d.private_uniqueName] = k; kinN++; }
                }
                _f0KinValid = kinN > 0;
                _f0Valid = _f0Fac != null && _f0Mis != null;
                _f0Pending = true;
                sb.Append("NOSAVE-VERIFY frame0 captured economy=").Append(_f0Fac != null ? _f0Fac.Count : -1).Append(" blackboard=").Append(_f0Mis != null)
                  .Append(" ammoUnits=").Append(ammoUnits).Append(" kinUnits=").Append(kinN);
                foreach (var n in _f0Notes) sb.Append(" | ").Append(n);
                LogI(sb.ToString());
            }
            catch (Exception ex) { _f0Valid = false; _f0Pending = true; LogW("NOSAVE-VERIFY frame0 capture ERROR " + ex.Message + " -> economy/blackboard/ammo log-only"); }
        }

        private static void LogI(string m) { NOSaveSystemPlugin.Log?.LogInfo(m); }
        private static void LogW(string m) { NOSaveSystemPlugin.Log?.LogWarning(m); }

        internal static void Arm(MidMissionSnapshot snap, string src)
        {
            if (snap == null || NOSaveSystemPlugin.Instance == null) return;
            int g = ++_gen;
            _armSim = Time.time;
            if (!_f0Pending) { _f0Valid = false; _f0Fac = null; _f0Mis = null; _f0Ammo.Clear(); _f0Kin.Clear(); _f0KinValid = false; }
            _f0Pending = false;
            LogI("NOSAVE-VERIFY armed src=" + src + " gen=" + g + " (runs once after unpause + engine hold + settle)");
            NOSaveSystemPlugin.Instance.StartCoroutine(NoSaveCoroutines.Safe(VerifyCo(snap, src, g), "VERIFY",
                ex => { Flush(); LogW("NOSAVE-VERIFY VERIFY ERROR " + (ex != null ? ex.GetType().Name + ": " + ex.Message : "?") + " -> aborted, game continues"); }));
        }

        private static bool Paused { get { return NoSaveWait.Paused || NoSaveV093.PausePending || NoSavePause0.Active; } }
        private static float SimDt { get { return Mathf.Max(0f, Time.time - _armSim); } }

        private static bool SceneStable()
        {
            try
            {
                if (MissionReload.IsRunning || NOSaveSystemPlugin.CoreRunning) return false;
                if (!MissionManager.IsRunning) return false;
                return true;
            }
            catch { return false; }
        }

        private sealed class Item { public int kind; public SavedUnitDto dto; }
        private static readonly List<Item> _items = new List<Item>(512);

        private static IEnumerator VerifyCo(MidMissionSnapshot snap, string src, int g)
        {
            float rt = 0f;
            while (Paused)
            {
                if (g != _gen) yield break;
                rt += Time.unscaledDeltaTime;
                if (rt > UnpauseWaitMax) { NoSaveWait.Timeout("verify-unpause", UnpauseWaitMax, "VERIFY aborted"); LogW("NOSAVE-VERIFY VERIFY TIMEOUT done=0/0 (still paused)"); yield break; }
                yield return null;
            }
            SampleFuel(snap);
            float et = 0f; _holdActive = false;
            while (NoSaveEngineGear.AnyActive)
            {
                if (g != _gen) yield break;
                if (!Paused) et += Time.unscaledDeltaTime;
                if (et > EngineWaitMax) { _holdActive = true; NoSaveWait.Timeout("verify-engine", EngineWaitMax, "verify now, engine fields SKIP(hold-active)"); break; }
                yield return null;
            }
            float st = 0f;
            while (_busy > 0 || !SceneStable())
            {
                if (g != _gen) yield break;
                if (!Paused) st += Time.unscaledDeltaTime;
                if (st > SettleWaitMax)
                {
                    NoSaveWait.Timeout("verify-settle", SettleWaitMax, "VERIFY aborted (busy=" + _busy + " stable=" + SceneStable() + ")");
                    LogW("NOSAVE-VERIFY VERIFY TIMEOUT done=0/" + CountItems(snap) + " (settle)");
                    yield break;
                }
                yield return null;
            }
            float total = st;
            _extLive = null;
            BuildItems(snap);
            int n = _items.Count, done = 0;
            _ok = _mis = _skip = 0; _misKeys.Clear(); _misUnits.Clear(); _reassertGroups.Clear(); _filter = null; _sb.Length = 0;
            ComputeThrottleLower(snap);
            LogI("NOSAVE-VERIFY start src=" + src + " items=" + n + " simDt=" + F(SimDt) + "s passEps=" + F(NoSaveEngineGear.LastPassEps) + " thrLower=" + _thrLower + " holdActive=" + _holdActive);
            CheckCoverage();
            _sw.Reset(); _sw.Start();
            while (done < n)
            {
                if (g != _gen) { Flush(); yield break; }
                if (_sw.Elapsed.TotalMilliseconds >= FrameBudgetMs)
                {
                    Flush();
                    _sw.Reset();
                    yield return null;
                    if (!Paused) total += Time.unscaledDeltaTime;
                    if (total > TotalMax) { LogW("NOSAVE-VERIFY VERIFY TIMEOUT done=" + done + "/" + n + " after " + F(total) + "s (total_max " + F(TotalMax) + "s)"); yield break; }
                    _sw.Start();
                }
                var cur = _items[done];
                if (cur.kind == 6) { if (ExtStep(snap)) done++; }
                else { RunItem(snap, cur); done++; }
            }
            _sw.Stop();
            Flush();
            int compared = _ok + _mis;
            if (_mis == 0) LogI("NOSAVE-VERIFY VERIFY PASS " + compared + "/" + compared + " skipped=" + _skip + " items=" + n);
            else LogW("NOSAVE-VERIFY VERIFY FAIL " + _mis + "/" + compared + " skipped=" + _skip + " items=" + n + " groups=" + string.Join(",", _reassertGroups.ToArray()));

            if (_mis == 0) yield break;
            int fixedN = Reassert(snap);
            yield return null;
            _filter = new HashSet<string>(_misKeys, StringComparer.Ordinal);
            int okBefore = _ok, misBefore = _mis;
            _ok = _mis = _skip = 0;
            var units = new List<string>(_misUnits);
            _sw.Reset(); _sw.Start();
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                string key = ItemKey(it);
                if (!units.Contains(key)) continue;
                if (_sw.Elapsed.TotalMilliseconds >= FrameBudgetMs)
                {
                    Flush(); _sw.Reset(); yield return null;
                    if (!Paused) total += Time.unscaledDeltaTime;
                    if (total > TotalMax + 1f) { LogW("NOSAVE-VERIFY VERIFY TIMEOUT done=" + i + "/" + _items.Count + " (re-verify)"); yield break; }
                    _sw.Start();
                }
                if (it.kind == 6)
                {
                    while (!ExtStep(snap))
                    {
                        if (g != _gen) { Flush(); yield break; }
                        if (_sw.Elapsed.TotalMilliseconds >= FrameBudgetMs)
                        {
                            Flush(); _sw.Reset(); yield return null;
                            if (!Paused) total += Time.unscaledDeltaTime;
                            if (total > TotalMax + 1f) { LogW("NOSAVE-VERIFY VERIFY TIMEOUT (re-verify ext)"); yield break; }
                            _sw.Start();
                        }
                    }
                }
                else RunItem(snap, it);
            }
            _sw.Stop(); Flush();
            int re = _ok + _mis;
            LogI("NOSAVE-VERIFY REVERIFY reassert_retry=" + ReassertRetry + " reasserted=" + fixedN + " ok=" + _ok + "/" + re + " stillMismatch=" + _mis + " (first pass " + misBefore + " mismatch, " + okBefore + " ok)");
            _filter = null;
        }

        private static int CountItems(MidMissionSnapshot s)
        {
            int c = 1;
            if (s.aircraft != null) c += s.aircraft.Count; if (s.vehicles != null) c += s.vehicles.Count;
            if (s.ships != null) c += s.ships.Count; if (s.otherUnits != null) c += s.otherUnits.Count;
            return c + 6;
        }

        private static void BuildItems(MidMissionSnapshot s)
        {
            _items.Clear();
            _items.Add(new Item { kind = 0 });
            if (s.aircraft != null)
            {
                for (int i = 0; i < s.aircraft.Count; i++) if (s.aircraft[i] != null && s.aircraft[i].isPlayerAircraft) _items.Add(new Item { kind = 1, dto = s.aircraft[i] });
                for (int i = 0; i < s.aircraft.Count; i++) if (s.aircraft[i] != null && !s.aircraft[i].isPlayerAircraft) _items.Add(new Item { kind = 1, dto = s.aircraft[i] });
            }
            if (s.vehicles != null) for (int i = 0; i < s.vehicles.Count; i++) if (s.vehicles[i] != null) _items.Add(new Item { kind = 1, dto = s.vehicles[i] });
            if (s.ships != null) for (int i = 0; i < s.ships.Count; i++) if (s.ships[i] != null) _items.Add(new Item { kind = 1, dto = s.ships[i] });
            if (s.otherUnits != null) for (int i = 0; i < s.otherUnits.Count; i++) if (s.otherUnits[i] != null) _items.Add(new Item { kind = 1, dto = s.otherUnits[i] });
            for (int k = 2; k <= 7; k++) _items.Add(new Item { kind = k });
        }

        private static string ItemKey(Item it)
        {
            switch (it.kind)
            {
                case 0: return "mission";
                case 1: return it.dto.private_uniqueName ?? "?";
                case 2: return "faction";
                case 3: return "objective";
                case 4: return "statics";
                case 5: return "datalink";
                case 6: return "ext";
                default: return "player";
            }
        }

        private static void RunItem(MidMissionSnapshot s, Item it)
        {
            try
            {
                switch (it.kind)
                {
                    case 0: BeginUnit("mission", true); CmpMission(s.mission); break;
                    case 1: CmpUnit(it.dto); break;
                    case 2: BeginUnit("faction", true); CmpFactions(s.factions); break;
                    case 3: BeginUnit("objective", false); CmpObjectives(s.activeObjectives); break;
                    case 4: BeginUnit("statics", false); CmpStatics(s.destroyedStatics); break;
                    case 5: BeginUnit("datalink", false); CmpDatalink(s.datalink); break;
                    case 6: BeginUnit("ext", false); { List<StateEntryDto> lv = null; try { lv = NoSaveExtState.Capture(null); } catch { } CmpExt(s.extState, lv); } break;
                    case 7: BeginUnit("player", true); CmpPlayerState(s.playerState); break;
                }
            }
            catch (Exception ex) { Res("item", "-", "-", 2, "error:" + ex.GetType().Name + " " + ex.Message); }
            EndUnit();
        }

        private static void BeginUnit(string name, bool logAll) { _unit = name; _logAll = logAll; _uOk = _uN = _uSkipQuiet = 0; }

        private static void EndUnit()
        {
            if (_filter != null || _logAll || _unit == null) return;
            if (_uN > 0 || _uSkipQuiet > 0) Line("NOSAVE-VERIFY unit=" + _unit + " ok=" + _uOk + "/" + _uN + (_uSkipQuiet > 0 ? " quietSkips=" + _uSkipQuiet : ""));
        }

        private static void Res(string field, string saved, string live, int status, string reason, bool quiet = false)
        {
            string key = _unit + "." + field;
            if (_filter != null && !_filter.Contains(key)) return;
            if (status == 0) { _ok++; _uOk++; _uN++; }
            else if (status == 1) { _mis++; _uN++; if (_filter == null) { _misKeys.Add(key); _misUnits.Add(_unit); } }
            else { _skip++; if (quiet && !_logAll && _filter == null) { _uSkipQuiet++; return; } }
            if (status == 0 && !_logAll && _filter == null) return;
            _tmp.Length = 0;
            _tmp.Append("NOSAVE-VERIFY field=").Append(key).Append(" saved=").Append(saved).Append(" live=").Append(live).Append(' ');
            if (status == 0) _tmp.Append("ok");
            else if (status == 1) { _tmp.Append("MISMATCH"); if (!string.IsNullOrEmpty(reason)) _tmp.Append(" (").Append(reason).Append(')'); }
            else _tmp.Append("SKIP(").Append(reason).Append(')');
            if (_filter != null) _tmp.Append(" reassert_retry=").Append(ReassertRetry);
            Line(_tmp.ToString());
        }

        private static void OkQuiet(string field)
        {
            if (_filter != null && !_filter.Contains(_unit + "." + field)) return;
            _ok++; _uOk++; _uN++;
        }

        private static void Line(string s)
        {
            if (_sb.Length > 0) _sb.Append('\n');
            _sb.Append(s);
            if (_sb.Length > 12000) Flush();
        }

        private static void Flush()
        {
            if (_sb.Length == 0) return;
            LogI(_sb.ToString());
            _sb.Length = 0;
        }

        private static string F(float v) { return float.IsNaN(v) ? "NaN" : v.ToString("0.####", Inv); }
        private static string B(bool b) { return b ? "1" : "0"; }
        private static string S(string s) { return s == null ? "null" : NoSaveUi.Ascii(s.Length > 80 ? s.Substring(0, 80) + "..." : s); }

        private static void Exact(string f, string a, string b) { Res(f, S(a), S(b), string.Equals(a ?? "", b ?? "", StringComparison.Ordinal) ? 0 : 1, null); }
        private static void Exact(string f, bool a, bool b) { Res(f, B(a), B(b), a == b ? 0 : 1, null); }
        private static void Exact(string f, int a, int b) { Res(f, a.ToString(Inv), b.ToString(Inv), a == b ? 0 : 1, null); }
        private static void Near(string f, float a, float b, float eps) { Res(f, F(a), F(b), Mathf.Abs(a - b) <= eps ? 0 : 1, Mathf.Abs(a - b) <= eps ? null : "eps=" + F(eps)); }
        private static void SkipF(string f, string a, string b, string why, bool quiet = false) { Res(f, S(a), S(b), 2, why, quiet); }
        private static void Input(string f, float a, float b) { bool eq = Mathf.Abs(a - b) <= 0.005f; Res(f, F(a), F(b), eq ? 0 : 2, eq ? null : "input-driven", !_logAll); }

        private static readonly Dictionary<Type, string[]> Covered = new Dictionary<Type, string[]>
        {
            { typeof(MidMissionSnapshot), new[] { "manifest", "mission", "aircraft", "vehicles", "ships", "otherUnits", "factions", "activeObjectives", "formatVersion", "extState", "datalink", "playerState", "destroyedStatics", "metaSavedLocal", "metaSavedUtc", "metaMission", "metaAircraft", "metaAircraftType" } },
            { typeof(SnapshotManifest), new[] { "plugin", "version", "phase", "utc", "unity", "scene", "gameState", "note" } },
            { typeof(SnapshotMission), new[] { "name", "loadKeyName", "loadKey", "loadKeyGroup", "mapKeyType", "mapKeyPath", "jsonVersion", "isRunning", "missionTime", "currentEscalation", "wrecksMaxNumber", "wrecksDecayTime", "tacticalThreshold", "strategicThreshold", "aircraftDefined", "vehiclesDefined", "shipsDefined", "error" } },
            { typeof(GlobalPosDto), new[] { "x", "y", "z" } },
            { typeof(QuatDto), new[] { "x", "y", "z", "w" } },
            { typeof(SavedUnitDto), new[] { "kind", "type", "definitionName", "faction", "private_uniqueName", "unitName", "globalPosition", "rotation", "persistentId", "disabled", "unitState", "velocity", "angularVelocity", "velocityCaptured", "parts", "v096" } },
            { typeof(SavedAircraftDto), new[] { "playerControlled", "isPlayerAircraft", "fuel", "fuelCaptured", "skill", "bravery", "startingSpeed", "ignition", "loadoutMountKeys", "gearDeployed", "radarAlt", "throttle", "brake", "controlsCaptured", "engineRpmRatios", "customAxis1", "pilotState", "pilotStateName", "hasTakenOff", "simThrottle", "pilotCaptured", "simThrottleCaptured", "engineState", "gearState", "animState", "savedThrust", "savedPower" } },
            { typeof(SavedVehicleDto), new[] { "holdPosition", "skill" } },
            { typeof(SavedShipDto), new[] { "holdPosition", "skill", "throttle", "steering", "controlsCaptured" } },
            { typeof(FactionEconomyDto), new[] { "factionName", "factionFunds", "factionScore" } },
            { typeof(ObjectiveDto), new[] { "uniqueName", "displayName", "faction", "status", "completePercent", "objectiveType", "hidden", "outcomesCount" } },
            { typeof(PartHpDto), new[] { "id", "hitPoints", "detached", "critical", "name" } },
            { typeof(StateEntryDto), new[] { "scope", "target", "key", "vtype", "value" } },
            { typeof(DatalinkDto), new[] { "faction", "uniqueName", "x", "y", "z", "attackers", "missileAttacks" } },
        };
        private static bool _coverageLogged;

        private static void CheckCoverage()
        {
            if (_coverageLogged) return;
            _coverageLogged = true;
            int fields = 0, missing = 0;
            foreach (var kv in Covered)
            {
                var have = new HashSet<string>(kv.Value, StringComparer.Ordinal);
                foreach (var fi in kv.Key.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    fields++;
                    if (have.Contains(fi.Name)) continue;
                    missing++;
                    Line("NOSAVE-VERIFY field=" + kv.Key.Name + "." + fi.Name + " saved=- live=- SKIP(" + fi.Name + ",no-comparator)");
                }
            }
            Line("NOSAVE-VERIFY coverage dtoFields=" + fields + " noComparator=" + missing);
        }

        private static void CmpMission(SnapshotMission s)
        {
            if (s == null) { SkipF("all", "null", "-", "pre-save-format"); return; }
            var l = NOSaveSystemPlugin.CaptureMissionInfo();
            if (l == null) { Res("all", "-", "null", 1, "capture failed"); return; }
            float dt = SimDt;
            Exact("name", s.name, l.name); Exact("loadKeyName", s.loadKeyName, l.loadKeyName); Exact("loadKey", s.loadKey, l.loadKey);
            Exact("loadKeyGroup", s.loadKeyGroup, l.loadKeyGroup); Exact("mapKeyType", s.mapKeyType, l.mapKeyType); Exact("mapKeyPath", s.mapKeyPath, l.mapKeyPath);
            Exact("jsonVersion", s.jsonVersion, l.jsonVersion); Exact("isRunning", s.isRunning, l.isRunning);
            float expT = s.missionTime + dt, epsT = TimeFloor + TimeRel * dt;
            Res("missionTime", F(s.missionTime) + "+" + F(dt), F(l.missionTime), Mathf.Abs(l.missionTime - expT) <= epsT ? 0 : 1, "eps=" + F(epsT));
            Board("currentEscalation", s.currentEscalation, _f0Valid ? _f0Mis.currentEscalation : float.NaN, l.currentEscalation, EscalationEps);
            Exact("wrecksMaxNumber", s.wrecksMaxNumber, l.wrecksMaxNumber); Near("wrecksDecayTime", s.wrecksDecayTime, l.wrecksDecayTime, SmallEps);
            Board("tacticalThreshold", s.tacticalThreshold, _f0Valid ? _f0Mis.tacticalThreshold : float.NaN, l.tacticalThreshold, EscalationEps);
            Board("strategicThreshold", s.strategicThreshold, _f0Valid ? _f0Mis.strategicThreshold : float.NaN, l.strategicThreshold, EscalationEps);
            Exact("aircraftDefined", s.aircraftDefined, l.aircraftDefined); Exact("vehiclesDefined", s.vehiclesDefined, l.vehiclesDefined); Exact("shipsDefined", s.shipsDefined, l.shipsDefined);
            SkipF("error", s.error, l.error, "diagnostic");
        }

        private static void Board(string f, float saved, float frame0, float live, float eps)
        {
            if (!float.IsNaN(frame0))
            {
                bool ok0 = Mathf.Abs(frame0 - saved) <= eps;
                Res(f, F(saved), F(frame0) + "(frame0)", ok0 ? 0 : 1, ok0 ? null : "frame0 differs after the frame-0 reassert");
                if (Mathf.Abs(live - saved) > eps) SkipF(f + ".live", F(saved), F(live), "sim-elapsed");
            }
            else
            {
                if (Mathf.Abs(live - saved) <= eps) Res(f, F(saved), F(live), 0, null);
                else SkipF(f, F(saved), F(live), "sim-elapsed,no-frame0");
            }
        }

        private static void CmpFactions(List<FactionEconomyDto> saved)
        {
            if (saved == null) { SkipF("all", "null", "-", "not-in-save"); return; }
            var live = MidMissionV03Helpers.CaptureFactions();
            int before = _mis;
            for (int i = 0; i < saved.Count; i++)
            {
                var s = saved[i]; if (s == null) continue;
                FactionEconomyDto l = null;
                if (live != null) for (int j = 0; j < live.Count; j++) if (live[j] != null && live[j].factionName == s.factionName) { l = live[j]; break; }
                string n = s.factionName ?? "?";
                if (l == null) { Res(n + ".present", "1", "0", 1, null); continue; }
                FactionEconomyDto f0 = null;
                if (_f0Valid) foreach (var x in _f0Fac) if (x != null && x.factionName == s.factionName) { f0 = x; break; }
                if (_f0Valid && f0 == null) { Res(n + ".present", "1", "0(frame0)", 1, null); continue; }
                Board(n + ".factionFunds", s.factionFunds, f0 != null ? f0.factionFunds : float.NaN, l.factionFunds, FundsEps);
                Board(n + ".factionScore", s.factionScore, f0 != null ? f0.factionScore : float.NaN, l.factionScore, FundsEps);
            }
        }

        private static void CmpObjectives(List<ObjectiveDto> saved)
        {
            if (saved == null) { SkipF("all", "null", "-", "not-in-save"); return; }
            var live = MidMissionV03Helpers.CaptureActiveObjectives();
            for (int i = 0; i < saved.Count; i++)
            {
                var s = saved[i]; if (s == null) continue;
                ObjectiveDto l = null;
                if (live != null) for (int j = 0; j < live.Count; j++) if (live[j] != null && live[j].uniqueName == s.uniqueName) { l = live[j]; break; }
                string n = s.uniqueName ?? "?";
                if (l == null) { Res(n + ".active", "1", "0", 1, "log-only"); continue; }
                Exact(n + ".status", s.status, l.status); Near(n + ".completePercent", s.completePercent, l.completePercent, ObjPctEps);
                Exact(n + ".hidden", s.hidden, l.hidden); Exact(n + ".outcomesCount", s.outcomesCount, l.outcomesCount);
                Exact(n + ".displayName", s.displayName, l.displayName); Exact(n + ".faction", s.faction, l.faction); Exact(n + ".objectiveType", s.objectiveType, l.objectiveType);
            }
        }

        private static void CmpStatics(string saved)
        {
            if (string.IsNullOrEmpty(saved)) { Res("destroyed", "0", "-", 0, null); return; }
            int before = _mis;
            var parts = saved.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                var name = parts[i]; if (string.IsNullOrEmpty(name)) continue;
                Unit u = null;
                try { UnitRegistry.customIDLookup.TryGetValue(name, out u); } catch { }
                bool dead = u == null;
                if (!dead) { try { dead = u.disabled || u.unitState == Unit.UnitState.Destroyed; } catch { } }
                Res(name + ".destroyed", "1", dead ? "1" : "0", dead ? 0 : 1, null);
            }
            if (_mis > before) AddGroup("statics");
        }

        private static void CmpDatalink(List<DatalinkDto> saved)
        {
            if (saved == null) { SkipF("all", "null", "-", "not-in-save"); return; }
            List<DatalinkDto> live = null;
            try { live = NoSaveV093.CaptureDatalink(); } catch { }
            var idx = new Dictionary<string, DatalinkDto>(StringComparer.Ordinal);
            if (live != null) for (int i = 0; i < live.Count; i++) if (live[i] != null) idx[live[i].faction + "/" + live[i].uniqueName] = live[i];
            int before = _mis;
            for (int i = 0; i < saved.Count; i++)
            {
                var s = saved[i]; if (s == null) continue;
                string k = s.faction + "/" + s.uniqueName;
                DatalinkDto l;
                if (!idx.TryGetValue(k, out l))
                {
                    Unit u = null; try { UnitRegistry.customIDLookup.TryGetValue(s.uniqueName ?? "", out u); } catch { }
                    if (u == null) { SkipF(k + ".tracked", "1", "0", "unit-gone", true); continue; }
                    Res(k + ".tracked", "1", "0", 1, null); continue;
                }
                OkQuiet(k + ".tracked");
                if (l.attackers != s.attackers || l.missileAttacks != s.missileAttacks)
                    SkipF(k + ".counters", s.attackers + "/" + s.missileAttacks, l.attackers + "/" + l.missileAttacks, "transient", true);
                if (_filter == null) { _skip++; _uSkipQuiet++; }
            }
            if (_mis > before) AddGroup("datalink");
        }

        private static List<StateEntryDto> _extLive;
        private static List<Unit> _extUnits;
        private static int _extSec, _extPos, _extSteps;
        private static double _extMs, _extMaxMs;
        private static readonly Stopwatch _extSw = new Stopwatch();
        internal const int ExtUnitChunk = 48;

        private static bool ExtStep(MidMissionSnapshot s)
        {
            if (s.extState == null || s.extState.Count == 0) { BeginUnit("ext", false); CmpExt(s.extState, null); EndUnit(); return true; }
            if (_extLive == null) { _extLive = new List<StateEntryDto>(256); _extSec = 0; _extPos = 0; _extUnits = null; _extSteps = 0; _extMs = 0; _extMaxMs = 0; }
            if (_extSec < NoSaveExtState.SectionCount)
            {
                _extSw.Reset(); _extSw.Start();
                try
                {
                    if (_extSec == NoSaveExtState.UnitsSection)
                    {
                        if (_extUnits == null) { _extUnits = NoSaveExtState.UnitList(); _extPos = 0; }
                        NoSaveExtState.CaptureUnitsRange(_extLive, _extUnits, _extPos, ExtUnitChunk);
                        _extPos += ExtUnitChunk;
                        if (_extPos >= _extUnits.Count) _extSec++;
                    }
                    else { NoSaveExtState.CaptureSection(_extSec, _extLive); _extSec++; }
                }
                catch (Exception ex) { LogW("NOSAVE-VERIFY ext section " + _extSec + " ERROR " + ex.Message); _extSec++; }
                _extSw.Stop();
                double ms = _extSw.Elapsed.TotalMilliseconds; _extMs += ms; if (ms > _extMaxMs) _extMaxMs = ms; _extSteps++;
                return false;
            }
            BeginUnit("ext", false);
            Line("NOSAVE-VERIFY ext capture steps=" + _extSteps + " totalMs=" + _extMs.ToString("0.##", Inv) + " maxStepMs=" + _extMaxMs.ToString("0.##", Inv)
                + " units=" + (_extUnits != null ? _extUnits.Count : 0) + " entries=" + _extLive.Count + " (chunk=" + ExtUnitChunk + " units)");
            CmpExt(s.extState, _extLive);
            EndUnit();
            _extLive = null; _extUnits = null;
            return true;
        }

        private static void CmpExt(List<StateEntryDto> saved, List<StateEntryDto> live)
        {
            if (saved == null || saved.Count == 0) { SkipF("all", "0", "-", "not-in-save"); return; }
            if (live == null) { Res("all", saved.Count.ToString(Inv), "null", 1, "capture failed"); return; }
            var idx = new Dictionary<string, StateEntryDto>(live.Count, StringComparer.Ordinal);
            for (int i = 0; i < live.Count; i++) { var e = live[i]; if (e != null) idx[e.scope + "|" + e.target + "|" + e.key] = e; }
            for (int i = 0; i < saved.Count; i++)
            {
                var s = saved[i]; if (s == null) continue;
                string k = s.scope + "|" + s.target + "|" + s.key;
                StateEntryDto l;
                if (!idx.TryGetValue(k, out l)) { SkipF(k, s.value, "absent", "absent-live", true); continue; }
                if (s.scope == "missionTime") { SkipF(k, s.value, l.value, "checked-in-mission.missionTime", true); continue; }
                if (string.Equals(s.value, l.value, StringComparison.Ordinal)) { OkQuiet(k); continue; }
                float a, b;
                if (s.vtype == "f" && float.TryParse(s.value, NumberStyles.Float, Inv, out a) && float.TryParse(l.value, NumberStyles.Float, Inv, out b)
                    && Mathf.Abs(a - b) <= 1e-3f * Mathf.Max(1f, Mathf.Abs(a))) { OkQuiet(k); continue; }
                Res(k, S(s.value), S(l.value), 1, "log-only");
            }
        }

        private static void CmpPlayerState(string saved)
        {
            if (string.IsNullOrEmpty(saved)) { SkipF("state", "null", "-", "pre-0.9.10-save"); return; }
            Aircraft ac = null; try { ac = MidMissionV03Helpers.FindLocalPlayerAircraft(); } catch { }
            string fac = null;
            try { var lp = MidMissionV03Helpers.FindLocalPlayer(); if (lp != null && lp.HQ != null && lp.HQ.faction != null) fac = lp.HQ.faction.factionName; } catch { }
            string live = (ac == null ? "noAircraft" : "aircraft") + "|" + (fac ?? "");
            Exact("state", saved, live);
        }

        private static Unit FindUnit(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try { Unit u; if (UnitRegistry.customIDLookup.TryGetValue(name, out u) && u != null) return u; } catch { }
            return null;
        }

        private static void CmpUnit(SavedUnitDto s)
        {
            var sa = s as SavedAircraftDto;
            bool player = sa != null && sa.isPlayerAircraft;
            BeginUnit(s.private_uniqueName ?? "?", player);
            Unit u = FindUnit(s.private_uniqueName);
            bool missile = s.kind != null && s.kind.IndexOf("Missile", StringComparison.Ordinal) >= 0;
            if (u == null)
            {
                if (s.disabled) { Res("present", "0(disabled)", "0", 0, null); return; }
                if (missile) { SkipF("present", "1", "0", "missile-expired"); return; }
                Res("present", "1", "0", 1, "log-only (unit not found by UniqueName)");
                return;
            }
            SavedUnitDto l = null;
            if (sa != null && u is Aircraft ac)
            {
                bool isP = false;
                try { isP = ReferenceEquals(ac, MidMissionV03Helpers.FindLocalPlayerAircraft()) || ac.Player != null; } catch { }
                l = NOSaveSystemPlugin.CaptureAircraft(ac, isP);
            }
            else if (s is SavedVehicleDto && u is GroundVehicle gv) l = NOSaveSystemPlugin.CaptureVehicle(gv);
            else if (s is SavedShipDto && u is Ship sh) l = NOSaveSystemPlugin.CaptureShip(sh);
            else l = NOSaveSystemPlugin.CaptureUnitBase(u, u.GetType().Name);
            if (l == null) { Res("capture", "-", "null", 1, "capture failed"); return; }

            float dt = SimDt;
            float A = sa != null ? AccAircraft : (s is SavedVehicleDto || s is SavedShipDto ? AccSurface : AccOther);
            float Al = sa != null ? AlphaAircraft : AlphaOther;
            Exact("kind", s.kind, l.kind); Exact("type", s.type, l.type); Exact("definitionName", s.definitionName, l.definitionName);
            Exact("faction", s.faction, l.faction); Exact("unitName", s.unitName, l.unitName);
            SkipF("persistentId", s.persistentId.ToString(Inv), l.persistentId.ToString(Inv), "reassigned-on-spawn", !player);
            Exact("disabled", s.disabled, l.disabled); Exact("unitState", s.unitState, l.unitState);

            Vector3 v = s.velocityCaptured && s.velocity != null ? new Vector3(s.velocity.x, s.velocity.y, s.velocity.z) : Vector3.zero;
            Vector3 w = s.velocityCaptured && s.angularVelocity != null ? new Vector3(s.angularVelocity.x, s.angularVelocity.y, s.angularVelocity.z) : Vector3.zero;
            Kin k0 = default(Kin); bool hasK0 = _f0KinValid && _f0Kin.TryGetValue(s.private_uniqueName ?? "", out k0);
            if (s.globalPosition != null && l.globalPosition != null)
            {
                var sp = new Vector3(s.globalPosition.x, s.globalPosition.y, s.globalPosition.z);
                var lp = new Vector3(l.globalPosition.x, l.globalPosition.y, l.globalPosition.z);
                if (hasK0 && k0.hasP)
                {
                    float d0 = (k0.p - sp).magnitude;
                    Res("globalPosition", V3(sp), V3(k0.p) + "(frame0)", d0 <= PosFloor ? 0 : 1, "d=" + F(d0) + " eps=" + F(PosFloor) + " (anchored at frame0)");
                    SkipF("globalPosition.live", V3(k0.p), V3(lp), "sim-elapsed d_from_frame0=" + F((lp - k0.p).magnitude) + " dt=" + F(dt), !player);
                }
                else
                {
                    float eps = PosFloor + PosVelK * v.magnitude * dt + 0.5f * A * dt * dt;
                    float d = (lp - (sp + v * dt)).magnitude;
                    Res("globalPosition", V3(sp) + "+v*" + F(dt), V3(lp), d <= eps ? 0 : 1, "d=" + F(d) + " eps=" + F(eps) + " (no frame0)");
                }
            }
            else SkipF("globalPosition", "-", "-", "not-captured");
            if (s.rotation != null && l.rotation != null)
            {
                var qs = new Quaternion(s.rotation.x, s.rotation.y, s.rotation.z, s.rotation.w);
                var ql = new Quaternion(l.rotation.x, l.rotation.y, l.rotation.z, l.rotation.w);
                if (hasK0 && k0.hasQ)
                {
                    float a0 = Quaternion.Angle(qs, k0.q);
                    Res("rotation", "q", "q(frame0)", a0 <= RotFloorDeg ? 0 : 1, "angle=" + F(a0) + " eps=" + F(RotFloorDeg) + " (anchored at frame0)");
                    SkipF("rotation.live", "q(frame0)", "q", "sim-elapsed angle_from_frame0=" + F(Quaternion.Angle(k0.q, ql)) + " dt=" + F(dt), !player);
                }
                else
                {
                    float ang = Quaternion.Angle(qs, ql);
                    float eps = Mathf.Min(180f, RotFloorDeg + RotWK * w.magnitude * dt * Mathf.Rad2Deg + 0.5f * Al * dt * dt * Mathf.Rad2Deg);
                    Res("rotation", "q", "q", ang <= eps ? 0 : 1, "angle=" + F(ang) + " eps=" + F(eps) + " (no frame0)");
                }
            }
            if (s.velocityCaptured && l.velocityCaptured && l.velocity != null)
            {
                var lv = new Vector3(l.velocity.x, l.velocity.y, l.velocity.z);
                float eps = VelFloor + VelRel * v.magnitude + A * dt;
                Res("velocity", V3(v), V3(lv), (lv - v).magnitude <= eps ? 0 : 1, "eps=" + F(eps));
                if (l.angularVelocity != null)
                {
                    var lw = new Vector3(l.angularVelocity.x, l.angularVelocity.y, l.angularVelocity.z);
                    float e2 = AngFloor + AngRel * w.magnitude + Al * dt;
                    Res("angularVelocity", V3(w), V3(lw), (lw - w).magnitude <= e2 ? 0 : 1, "eps=" + F(e2));
                }
            }
            else SkipF("velocity", B(s.velocityCaptured), B(l.velocityCaptured), "not-captured", !player);

            CmpParts(s, l, u);
            CmpV096(s, l, u);

            if (sa != null) CmpAircraft(sa, (SavedAircraftDto)l, (Aircraft)u, player, dt);
            else if (s is SavedVehicleDto svd)
            {
                var lvd = (SavedVehicleDto)l;
                SkipF("holdPosition", B(svd.holdPosition), B(lvd.holdPosition), "not-capturable(private)", true);
                Near("skill", svd.skill, lvd.skill, SmallEps);
            }
            else if (s is SavedShipDto ssd)
            {
                var lsd = (SavedShipDto)l;
                Exact("holdPosition", ssd.holdPosition, lsd.holdPosition); Near("skill", ssd.skill, lsd.skill, SmallEps);
                Input("throttle", ssd.throttle, lsd.throttle); Input("steering", ssd.steering, lsd.steering);
            }
        }

        private static string V3(Vector3 v) { return "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")"; }

        private static void CmpParts(SavedUnitDto s, SavedUnitDto l, Unit u)
        {
            if (s.parts == null || s.parts.Count == 0) return;
            int before = _mis;
            for (int i = 0; i < s.parts.Count; i++)
            {
                var p = s.parts[i]; if (p == null) continue;
                PartHpDto q = null;
                if (l.parts != null) for (int j = 0; j < l.parts.Count; j++) if (l.parts[j] != null && l.parts[j].id == p.id) { q = l.parts[j]; break; }
                string f = "part" + p.id;
                if (q == null) { Res(f + ".present", "1", "0", p.detached ? 0 : 1, null); continue; }
                Exact(f + ".detached", p.detached, q.detached);
                if (p.detached) continue;
                float d = q.hitPoints - p.hitPoints;
                if (Mathf.Abs(d) <= PartHpEps) Res(f + ".hp", F(p.hitPoints), F(q.hitPoints), 0, null);
                else if (d < 0f) SkipF(f + ".hp", F(p.hitPoints), F(q.hitPoints), "damaged-after-load");
                else Res(f + ".hp", F(p.hitPoints), F(q.hitPoints), 1, "healthier than saved");
            }
            if (_mis > before) AddGroup("parts");
        }

        private static Dictionary<string, string> ParseAmmo(string v096)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(v096)) return d;
            Dictionary<string, string> kv = null;
            try { kv = NoSaveV096.Parse(v096); } catch { }
            string raw;
            if (kv == null || !kv.TryGetValue("wAmmo", out raw) || raw == null) return d;
            raw = raw.Replace("%7C", "|").Replace("%3D", "=").Replace("%3B", ";").Replace("%25", "%");
            foreach (var item in raw.Split('|'))
            {
                var p = item.Split(','); if (p.Length < 3) continue;
                d[p[0] + "," + p[1]] = p[2] + (p.Length >= 4 ? "," + p[3] : "");
            }
            return d;
        }

        private static void CmpV096(SavedUnitDto s, SavedUnitDto l, Unit u)
        {
            if (string.IsNullOrEmpty(s.v096)) return;
            var sa = ParseAmmo(s.v096); var la = ParseAmmo(l.v096);
            int before = _mis;
            foreach (var kv in sa)
            {
                string f = "ammo[st" + kv.Key.Replace(",", ".w") + "]";
                string lv;
                if (!la.TryGetValue(kv.Key, out lv)) { Res(f, kv.Value, "absent", 1, null); continue; }
                int a = AmmoOf(kv.Value), b = AmmoOf(lv);
                string ta = TypeOf(kv.Value), tb = TypeOf(lv);
                if (ta != tb) { Res(f, kv.Value, lv, 1, "weapon type"); continue; }
                if (a == b) Res(f, a.ToString(Inv), b.ToString(Inv), 0, null);
                else if (b < a) SkipF(f, a.ToString(Inv), b.ToString(Inv), "consumed-after-load");
                else if (!AmmoReassertOk(s.private_uniqueName, kv.Key, b)) SkipF(f, a.ToString(Inv), b.ToString(Inv), _f0Valid ? "rearmed-after-load" : "no-frame0(log-only)");
                else Res(f, a.ToString(Inv), b.ToString(Inv), 1, "more than saved at frame0");
            }
            if (_mis > before) AddGroup("ammo");
            Dictionary<string, string> ks = null, kl = null;
            try { ks = NoSaveV096.Parse(s.v096); kl = NoSaveV096.Parse(l.v096 ?? ""); } catch { }
            if (ks == null) return;
            foreach (var kv in ks)
            {
                if (kv.Key == "wAmmo") continue;
                string lv = null; if (kl != null) kl.TryGetValue(kv.Key, out lv);
                if (string.Equals(kv.Value, lv, StringComparison.Ordinal)) Res("v096." + kv.Key, S(kv.Value), S(lv), 0, null);
                else SkipF("v096." + kv.Key, kv.Value, lv, "transient", true);
            }
        }

        private static bool AmmoReassertOk(string unit, string key, int live)
        {
            if (!_f0Valid || unit == null) return false;
            Dictionary<string, string> d; string v;
            if (!_f0Ammo.TryGetValue(unit, out d) || !d.TryGetValue(key, out v)) return false;
            return live <= AmmoOf(v);
        }

        private static int AmmoOf(string v) { int c = v.IndexOf(','); int a; int.TryParse(c < 0 ? v : v.Substring(0, c), NumberStyles.Integer, Inv, out a); return a; }
        private static string TypeOf(string v) { int c = v.IndexOf(','); return c < 0 ? "" : v.Substring(c + 1); }

        private static void ComputeThrottleLower(MidMissionSnapshot s)
        {
            _thrLower = false;
            try
            {
                var ac = MidMissionV03Helpers.FindLocalPlayerAircraft();
                if (ac == null || s.aircraft == null) return;
                SavedAircraftDto pd = null;
                for (int i = 0; i < s.aircraft.Count; i++) if (s.aircraft[i] != null && s.aircraft[i].isPlayerAircraft) { pd = s.aircraft[i]; break; }
                if (pd == null) return;
                float thr, acc; bool on;
                if (NoSavePilotFix.TryPlayerInput(ac, out thr, out acc, out on)) _thrLower = thr < pd.throttle - ThrottleSlack;
            }
            catch { }
        }

        private static void SampleFuel(MidMissionSnapshot s)
        {
            _fuel0.Clear(); _fuel0At = Time.time;
            if (s.aircraft == null) return;
            for (int i = 0; i < s.aircraft.Count; i++)
            {
                var d = s.aircraft[i]; if (d == null || !d.fuelCaptured) continue;
                var ac = FindUnit(d.private_uniqueName) as Aircraft; if (ac == null) continue;
                try { _fuel0[d.private_uniqueName] = ac.GetFuelLevel(); } catch { try { _fuel0[d.private_uniqueName] = ac.fuelLevel; } catch { } }
            }
        }

        internal const float FuelSpanMin = 0.5f;

        private static bool FuelExpected(SavedAircraftDto s, float live, out float exp, out float rate)
        {
            rate = 0f; exp = s.fuel;
            float f0; float span = Time.time - _fuel0At;
            if (!_fuel0.TryGetValue(s.private_uniqueName ?? "", out f0) || span < FuelSpanMin) return false;
            rate = Mathf.Max(0f, (f0 - live) / span);
            exp = Mathf.Max(0f, s.fuel - rate * SimDt);
            return true;
        }

        private static bool FuelRefueled(SavedAircraftDto s, float live, out float f0)
        {
            f0 = 0f;
            return s != null && _fuel0.TryGetValue(s.private_uniqueName ?? "", out f0) && live > f0 + FuelFloor;
        }

        private static void CmpAircraft(SavedAircraftDto s, SavedAircraftDto l, Aircraft ac, bool player, float dt)
        {
            Exact("playerControlled", s.playerControlled, l.playerControlled); Exact("isPlayerAircraft", s.isPlayerAircraft, l.isPlayerAircraft);
            if (s.fuelCaptured)
            {
                float rate, exp, f0r;
                if (FuelRefueled(s, l.fuel, out f0r)) SkipF("fuel", F(s.fuel), F(l.fuel), "refueled-after-load(resume=" + F(f0r) + ")");
                else if (!FuelExpected(s, l.fuel, out exp, out rate)) SkipF("fuel", F(s.fuel), F(l.fuel), "rate-unknown");
                else
                {
                    float eps = FuelRel * Mathf.Abs(s.fuel) + FuelFloor + FuelRateK * rate * dt;
                    bool ok = Mathf.Abs(l.fuel - exp) <= eps;
                    Res("fuel", F(s.fuel) + "-" + F(rate) + "*" + F(dt), F(l.fuel), ok ? 0 : 1, "expected=" + F(exp) + " eps=" + F(eps));
                    if (!ok && exp < l.fuel) AddGroup("fuel");
                }
            }
            else SkipF("fuel", "-", F(l.fuel), "not-captured");
            Near("skill", s.skill, l.skill, SmallEps); Near("bravery", s.bravery, l.bravery, SmallEps);
            SkipF("startingSpeed", F(s.startingSpeed), F(l.startingSpeed), "derived(velocity)", !player);
            Exact("ignition", s.ignition, l.ignition);
            Exact("loadoutMountKeys", s.loadoutMountKeys != null ? string.Join(",", s.loadoutMountKeys.ToArray()) : "", l.loadoutMountKeys != null ? string.Join(",", l.loadoutMountKeys.ToArray()) : "");
            Exact("gearDeployed", s.gearDeployed, l.gearDeployed);
            {
                float vy = s.velocityCaptured && s.velocity != null ? Mathf.Abs(s.velocity.y) : 0f;
                float eps = PosFloor + PosVelK * vy * dt + 0.5f * AccAircraft * dt * dt + (s.velocityCaptured && s.velocity != null ? Mathf.Abs(s.velocity.y) * dt : 0f);
                Near("radarAlt", s.radarAlt, l.radarAlt, eps);
            }
            if (player)
            {
                float thr, acc; bool on;
                if (NoSavePilotFix.TryPlayerInput(ac, out thr, out acc, out on))
                {
                    bool follow = Mathf.Abs(l.throttle - thr) <= 0.01f || !on;
                    if (Mathf.Abs(l.throttle - s.throttle) <= 0.005f) Res("throttle", F(s.throttle), F(l.throttle), 0, null);
                    else if (l.throttle < s.throttle - ThrottleSlack) SkipF("throttle", F(s.throttle), F(l.throttle) + " acc=" + F(acc), "throttle-lower");
                    else Res("throttle", F(s.throttle), F(l.throttle) + " acc->" + F(thr), follow ? 2 : 1, follow ? "input-driven(follows accumulator)" : "ci does not follow the accumulator (log-only)");
                    Input("simThrottle", s.simThrottle, l.simThrottle);
                }
                else Input("throttle", s.throttle, l.throttle);
            }
            else { Input("throttle", s.throttle, l.throttle); Input("simThrottle", s.simThrottle, l.simThrottle); }
            Input("brake", s.brake, l.brake); Input("customAxis1", s.customAxis1, l.customAxis1);
            SkipF("controlsCaptured", B(s.controlsCaptured), B(l.controlsCaptured), "capture-flag", true);
            SkipF("pilotCaptured", B(s.pilotCaptured), B(l.pilotCaptured), "capture-flag", true);
            SkipF("simThrottleCaptured", B(s.simThrottleCaptured), B(l.simThrottleCaptured), "capture-flag", true);
            if (s.pilotState == l.pilotState) Res("pilotState", S(s.pilotState), S(l.pilotState), 0, null); else SkipF("pilotState", s.pilotState, l.pilotState, "ai-state-advanced", !player);
            if (s.pilotStateName == l.pilotStateName) Res("pilotStateName", S(s.pilotStateName), S(l.pilotStateName), 0, null); else SkipF("pilotStateName", s.pilotStateName, l.pilotStateName, "ai-state-advanced", true);
            Exact("hasTakenOff", s.hasTakenOff, l.hasTakenOff);
            SkipF("savedThrust", F(s.savedThrust), F(l.savedThrust), "airspeed-dependent", !player);
            CmpEngines(s, l, player);
            CmpGear(s, l, dt);
        }

        private static float EngTol(float saved) { return Mathf.Max(NoSaveEngineGear.LastPassEps, EngineRelFloor) * Mathf.Max(Mathf.Abs(saved), 0.1f) + EngineAbs; }

        private static void EngVal(string f, float saved, float live, bool dead)
        {
            if (_holdActive) { SkipF(f, F(saved), F(live), "hold-active"); return; }
            if (dead) { bool z = Mathf.Abs(live) <= DeadEps * Mathf.Max(1f, Mathf.Abs(saved)); Res(f, F(saved), F(live), z ? 0 : 1, z ? null : "destroyed engine not dead (log-only)"); return; }
            float tol = EngTol(saved);
            if (live >= saved - tol) Res(f, F(saved), F(live), 0, null);
            else if (_thrLower) SkipF(f, F(saved), F(live), "throttle-lower");
            else Res(f, F(saved), F(live), 1, "below saved by " + F(saved - live) + " tol=" + F(tol) + " (log-only, reassert=skip(no-dip-rule))");
        }

        private static List<KeyValuePair<string, Dictionary<string, string>>> ParseEngines(string es)
        {
            var list = new List<KeyValuePair<string, Dictionary<string, string>>>();
            if (string.IsNullOrEmpty(es)) return list;
            foreach (var part in es.Split('#'))
            {
                int bar = part.IndexOf('|');
                string type = bar < 0 ? part : part.Substring(0, bar);
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                if (bar >= 0)
                    foreach (var kv in part.Substring(bar + 1).Split(';'))
                    {
                        int eq = kv.IndexOf('='); if (eq <= 0) continue;
                        d[kv.Substring(0, eq)] = kv.Substring(eq + 1);
                    }
                list.Add(new KeyValuePair<string, Dictionary<string, string>>(type, d));
            }
            return list;
        }

        private static readonly string[] EngPowerFields = { "RPMRatio", "rpmRatio", "currentRPM", "RPM", "rpm", "currentPower", "powerRatio" };

        private static bool IsDead(Dictionary<string, string> d)
        {
            string v;
            if (d.TryGetValue("operable", out v) && v == "0") return true;
            if (d.TryGetValue("inoperable", out v) && v == "1") return true;
            return false;
        }

        private static void CmpEngines(SavedAircraftDto s, SavedAircraftDto l, bool player)
        {
            var se = ParseEngines(s.engineState); var le = ParseEngines(l.engineState);
            if (se.Count == 0) { SkipF("engineState", "-", "-", "pre-0.9.5-save"); }
            int deadN = 0;
            for (int i = 0; i < se.Count; i++)
            {
                string f = "engine#" + i + "(" + se[i].Key + ")";
                if (i >= le.Count) { Res(f + ".present", "1", "0", 1, null); continue; }
                if (se[i].Key != le[i].Key) { Res(f + ".type", se[i].Key, le[i].Key, 1, null); continue; }
                var sd = se[i].Value; var ld = le[i].Value;
                bool dead = IsDead(sd); if (dead) deadN++;
                string a, b;
                if (sd.TryGetValue("operable", out a)) { ld.TryGetValue("operable", out b); Res(f + ".operable", a, b ?? "?", a == b ? 0 : 1, a == b ? null : "per-engine damage state (log-only)"); }
                if (sd.TryGetValue("inoperable", out a)) { ld.TryGetValue("inoperable", out b); Res(f + ".inoperable", a, b ?? "?", a == b ? 0 : 1, a == b ? null : "per-engine damage state (log-only)"); }
                int transient = 0;
                foreach (var kv in sd)
                {
                    if (kv.Key == "operable" || kv.Key == "inoperable") continue;
                    bool power = Array.IndexOf(EngPowerFields, kv.Key) >= 0;
                    if (!power) { transient++; continue; }
                    float sv, lv;
                    if (!float.TryParse(kv.Value, NumberStyles.Float, Inv, out sv)) continue;
                    if (!ld.TryGetValue(kv.Key, out b) || !float.TryParse(b, NumberStyles.Float, Inv, out lv)) { SkipF(f + "." + kv.Key, kv.Value, "absent", "absent-live"); continue; }
                    EngVal(f + "." + kv.Key, sv, lv, dead);
                }
                if (transient > 0) { _skip += transient; if (player) Line("NOSAVE-VERIFY field=" + _unit + "." + f + ".other saved=" + transient + "fields live=- SKIP(transient)"); else _uSkipQuiet += transient; }
            }
            if (s.engineRpmRatios != null && l.engineRpmRatios != null)
                for (int i = 0; i < s.engineRpmRatios.Count; i++)
                {
                    if (i >= l.engineRpmRatios.Count) { Res("engineRpmRatios[" + i + "]", F(s.engineRpmRatios[i]), "absent", 1, null); continue; }
                    bool dead = s.engineRpmRatios[i] <= 0f && deadN > 0;
                    EngVal("engineRpmRatios[" + i + "]", s.engineRpmRatios[i], l.engineRpmRatios[i], dead);
                }
            if (s.savedPower >= 0f) EngVal("savedPower", s.savedPower, l.savedPower, deadN > 0 && deadN >= se.Count && s.savedPower <= 0f);
        }

        private static void CmpGear(SavedAircraftDto s, SavedAircraftDto l, float dt)
        {
            bool transitional = false;
            if (s.gearState >= 0)
            {
                string sn = ((LandingGear.GearState)s.gearState).ToString(), ln = l.gearState >= 0 ? ((LandingGear.GearState)l.gearState).ToString() : "unknown";
                transitional = sn == "Retracting" || sn == "Extending";
                string target = sn == "Retracting" ? "LockedRetracted" : (sn == "Extending" ? "LockedExtended" : sn);
                bool ok = ln == sn || ln == target;
                Res("gearState", sn, ln, ok ? 0 : 1, ok ? null : (transitional ? "did not progress to " + target : "gear changed (log-only)"));
            }
            else SkipF("gearState", "-1", l.gearState.ToString(Inv), "pre-0.9.5-save");
            if (string.IsNullOrEmpty(s.animState)) return;
            var sa = s.animState.Split(';'); var la = (l.animState ?? "").Split(';');
            for (int i = 0; i < sa.Length; i++)
            {
                var p = sa[i].Split(','); if (p.Length < 3) continue;
                string[] q = null;
                for (int j = 0; j < la.Length; j++) { var t = la[j].Split(','); if (t.Length >= 3 && t[0] == p[0] && t[1] == p[1]) { q = t; break; } }
                string f = (p[0] == "G" ? "gear" : "flap") + "#" + p[1];
                if (q == null) { SkipF(f, sa[i], "absent", "absent-live"); continue; }
                float a, b;
                float.TryParse(p[2], NumberStyles.Float, Inv, out a); float.TryParse(q[2], NumberStyles.Float, Inv, out b);
                if (p[0] == "G")
                {
                    if (transitional) { SkipF(f + ".hinge", F(a), F(b), "transitional(progress checked by gearState)"); continue; }
                    Near(f + ".hinge", a, b, GearHingeDeg);
                    float da, db;
                    if (p.Length >= 4 && q.Length >= 4 && float.TryParse(p[3], NumberStyles.Float, Inv, out da) && float.TryParse(q[3], NumberStyles.Float, Inv, out db) && da >= 0f && db >= 0f)
                        Near(f + ".door", da, db, GearDoorEps);
                }
                else Near(f + ".pitch", a, b, FlapFloorDeg + FlapServo * dt);
            }
        }

        private static void AddGroup(string g) { if (_filter == null && !_reassertGroups.Contains(g)) _reassertGroups.Add(g); }

        private static bool UnitHasMismatch(string unit, string prefix)
        {
            string k = unit + "." + prefix;
            for (int i = 0; i < _misKeys.Count; i++) if (_misKeys[i].StartsWith(k, StringComparison.Ordinal)) return true;
            return false;
        }

        private static int Reassert(MidMissionSnapshot s)
        {
            int n = 0;
            var sb = new StringBuilder();
            try
            {
                foreach (var g in _reassertGroups) sb.Append(g).Append(' ');
                if (_reassertGroups.Contains("fuel") && s.aircraft != null)
                    foreach (var d in s.aircraft)
                    {
                        if (d == null || !d.fuelCaptured || !UnitHasMismatch(d.private_uniqueName, "fuel")) continue;
                        var ac = FindUnit(d.private_uniqueName) as Aircraft; if (ac == null) continue;
                        float live; try { live = ac.GetFuelLevel(); } catch { live = ac.fuelLevel; } float rate, exp, f0r;
                        if (FuelRefueled(d, live, out f0r)) { Line("NOSAVE-VERIFY reassert fuel " + d.private_uniqueName + " skip(refueled-after-load: live " + F(live) + " > resume " + F(f0r) + ")"); continue; }
                        if (!FuelExpected(d, live, out exp, out rate)) { Line("NOSAVE-VERIFY reassert fuel " + d.private_uniqueName + " skip(rate-unknown)"); continue; }
                        if (exp >= live) { Line("NOSAVE-VERIFY reassert fuel " + d.private_uniqueName + " skip(lower-only: expected " + F(exp) + " >= live " + F(live) + ")"); continue; }
                        if (live > 0f && exp <= 0f) { Line("NOSAVE-VERIFY reassert fuel " + d.private_uniqueName + " skip(would empty a tank)"); continue; }
                        ac.fuelLevel = exp; ac.NetworkfuelLevel = exp; n++;
                        Line("NOSAVE-VERIFY reassert fuel " + d.private_uniqueName + " " + F(live) + " -> " + F(exp));
                    }
                if (_reassertGroups.Contains("ammo"))
                    foreach (var d in AllUnits(s))
                    {
                        if (!UnitHasMismatch(d.private_uniqueName, "ammo")) continue;
                        var u = FindUnit(d.private_uniqueName); if (u == null || u.weaponStations == null) continue;
                        var sa = ParseAmmo(d.v096);
                        foreach (var st in u.weaponStations)
                        {
                            if (st == null || st.Weapons == null) continue;
                            bool touched = false;
                            for (int j = 0; j < st.Weapons.Count; j++)
                            {
                                var w = st.Weapons[j]; string v;
                                if (w == null || !sa.TryGetValue(st.Number.ToString(Inv) + "," + j.ToString(Inv), out v)) continue;
                                int saved = AmmoOf(v);
                                if (w.ammo > saved && TypeOf(v) == w.GetType().Name && AmmoReassertOk(d.private_uniqueName, st.Number.ToString(Inv) + "," + j.ToString(Inv), w.ammo)) { w.ammo = saved; touched = true; n++; }
                            }
                            if (touched) { try { st.AccountAmmo(); } catch { } }
                        }
                        Line("NOSAVE-VERIFY reassert ammo " + d.private_uniqueName + " (lowered to saved where live > saved)");
                    }
                if (_reassertGroups.Contains("parts"))
                    foreach (var d in AllUnits(s))
                    {
                        if (d.parts == null || !UnitHasMismatch(d.private_uniqueName, "part")) continue;
                        var u = FindUnit(d.private_uniqueName); if (u == null) continue;
                        n += LowerParts(u, d.parts, d.private_uniqueName);
                    }
                if (_reassertGroups.Contains("statics")) { NoSaveV0910.RestoreDestroyedStatics(s, null); n++; Line("NOSAVE-VERIFY reassert destroyedStatics"); }
                if (_reassertGroups.Contains("datalink")) { NoSaveV093.RestoreDatalink(s, null); n++; Line("NOSAVE-VERIFY reassert datalink (missing entries)"); }
                Line("NOSAVE-VERIFY reassert groups=" + sb + "applied=" + n + " (kinematics/engine/throttle/objectives/ext/gear/economy/blackboard: reassert=skip(log-only))");
            }
            catch (Exception ex) { Line("NOSAVE-VERIFY reassert ERROR " + ex.Message); }
            Flush();
            return n;
        }

        private static IEnumerable<SavedUnitDto> AllUnits(MidMissionSnapshot s)
        {
            if (s.aircraft != null) foreach (var d in s.aircraft) if (d != null) yield return d;
            if (s.vehicles != null) foreach (var d in s.vehicles) if (d != null) yield return d;
            if (s.ships != null) foreach (var d in s.ships) if (d != null) yield return d;
            if (s.otherUnits != null) foreach (var d in s.otherUnits) if (d != null) yield return d;
        }

        private static int LowerParts(Unit u, List<PartHpDto> saved, string name)
        {
            int n = 0;
            List<UnitPart> parts = null;
            try { parts = u.GetAllParts(); } catch { }
            if (parts == null) return 0;
            foreach (var p in parts)
            {
                if (p == null) continue;
                PartHpDto d = null;
                for (int i = 0; i < saved.Count; i++) if (saved[i] != null && saved[i].id == p.id) { d = saved[i]; break; }
                if (d == null || d.detached) continue;
                try
                {
                    if (p.IsDetached() || p.hitPoints <= d.hitPoints + PartHpEps) continue;
                    bool engine = false;
                    foreach (var mb in p.GetComponentsInChildren<MonoBehaviour>(true))
                        if (mb != null && NoSaveEngineGear.EngineTypes.Contains(mb.GetType().Name)) { engine = true; break; }
                    if (engine) { Line("NOSAVE-VERIFY reassert part" + p.id + " " + name + " skip(engine part, no-dip rule)"); continue; }
                    if (d.hitPoints <= 0f) { Line("NOSAVE-VERIFY reassert part" + p.id + " " + name + " SKIP(zero-hp)"); continue; }
                    p.hitPoints = Mathf.Clamp(d.hitPoints, PartHpEps, 100f); n++;
                }
                catch { }
            }
            if (n > 0) Line("NOSAVE-VERIFY reassert parts " + name + " lowered=" + n);
            return n;
        }
    }

    internal static class NoSaveThrottleFollow
    {
        private static int _gen;
        internal const float Window = 1f;
        internal const float ErrEps = 0.01f;
        internal const int StuckFrames = 10;
        internal const int MaxFrames = 600;

        private static readonly ThrottleMath.OnceGate _once = new ThrottleMath.OnceGate();
        private static int _restoreSeq;
        internal static int RestoreSeq { get { return _restoreSeq; } }
        internal static void BeginRestore() { _restoreSeq++; }
        internal static void StartOnce(Aircraft ac, string label, int lockGen, string path)
        {
            if (ac == null) return;
            int id; try { id = ac.GetInstanceID(); } catch { return; }
            if (!_once.TryClaim(id, lockGen, _restoreSeq)) return;
            NOSaveSystemPlugin.Log?.LogInfo("NOSAVE-THR " + label + " FOLLOW start path=" + path + " seq=" + _restoreSeq + " window=" + F(Window) + "s");
            Start(ac, label, lockGen);
        }

        internal static void Start(Aircraft ac, string label, int lockGen)
        {
            if (ac == null || NOSaveSystemPlugin.Instance == null) return;
            int g = ++_gen;
            NOSaveSystemPlugin.Instance.StartCoroutine(NoSaveCoroutines.Safe(NoSaveVerify.Track(Co(ac, label, lockGen, g)), "THR-FOLLOW"));
        }

        private static IEnumerator Co(Aircraft ac, string label, int lockGen, int g)
        {
            float t = 0f; int frames = 0, checkedN = 0, bad = 0, fixes = 0, budget = MaxFrames;
            float maxErr = 0f, lastAcc = float.NaN, lastCi = float.NaN; bool stuckLogged = false;
            var cls = new ThrottleMath.SrcClassifier();
            while (t < Window && budget-- > 0)
            {
                yield return null;
                frames++;
                if (g != _gen || lockGen != NoSaveEngineGear.Gen) yield break;
                bool alive = false; try { alive = ac != null && !ac.Equals(null) && !ac.disabled; } catch { }
                if (!alive) yield break;
                if (NoSaveWait.Paused) { budget++; if (frames > MaxFrames * 100) break; continue; }
                t += Time.unscaledDeltaTime;
                float thr, acc; bool on;
                if (!NoSavePilotFix.TryPlayerInput(ac, out thr, out acc, out on) || !on) continue;
                cls.Add(NoSavePilotFix.RawThrottleAxis(ac));
                ControlInputs ci = null; try { ci = ac.GetInputs(); } catch { }
                if (ci == null) continue;
                float err;
                bad = ThrottleMath.FollowStep(ci.throttle, thr, ErrEps, bad, out err);
                checkedN++; lastAcc = acc; lastCi = ci.throttle;
                if (err > maxErr) maxErr = err;
                if (bad >= StuckFrames && fixes == 0)
                {
                    fixes = 1; bad = 0;
                    bool raise = ThrottleMath.FollowFixAllowed(ci.throttle, thr);
                    NOSaveSystemPlugin.Log?.LogWarning("NOSAVE-THR " + label + " STUCK src=" + cls.Result(NoSavePilotFix.LastActiveControllerType(ac)) + " acc=" + F(acc) + " expected=" + F(thr) + " ci=" + F(ci.throttle)
                        + " for " + StuckFrames + " frames -> " + (raise ? "single capped fix: ci.throttle=" + F(thr) : "fix=skip(would lower power; the game rewrites ci from the accumulator)"));
                    if (raise) { try { ci.throttle = thr; } catch { } }
                    stuckLogged = true;
                }
            }
            if (checkedN == 0) { NOSaveSystemPlugin.Log?.LogInfo("NOSAVE-THR " + label + " FOLLOW n/a (flight controls off / no player state in window)"); yield break; }
            string tail = " src=" + cls.Result(NoSavePilotFix.LastActiveControllerType(ac)) + " acc=" + F(lastAcc) + " ci=" + F(lastCi) + " maxErr=" + F(maxErr) + " frames=" + checkedN
                + " keyFrames=" + cls.KeyFrames + " fracFrames=" + cls.FracFrames + " negative=" + PlayerSettings.throttleUseNegative + " relative=" + PlayerSettings.throttleUseRelative;
            if (stuckLogged) NOSaveSystemPlugin.Log?.LogInfo("NOSAVE-THR " + label + " FOLLOW after-fix" + tail + (bad >= StuckFrames ? " STILL-STUCK" : ""));
            else if (maxErr <= ErrEps) NOSaveSystemPlugin.Log?.LogInfo("NOSAVE-THR " + label + " FOLLOW ok" + tail);
            else NOSaveSystemPlugin.Log?.LogInfo("NOSAVE-THR " + label + " FOLLOW ok (transient err, never " + StuckFrames + " frames)" + tail);
        }

        private static string F(float v) { return float.IsNaN(v) ? "NaN" : v.ToString("0.###", CultureInfo.InvariantCulture); }
    }
}
