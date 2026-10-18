using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace NOSaveSystem
{
    /// <summary>
    /// v0.9.6: missiles in flight (target, guiding owner, seeker mode, motor/fuel, age, exact velocity) and
    /// aircraft targeting (WeaponManager target list, active station, Pilot.primaryTarget, AI currentTarget).
    /// Extra state lives in SavedUnitDto.v096 as "k=v;k=v" (escaped) so old saves stay compatible.
    /// </summary>
    internal static class NoSaveV096
    {
        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static void Log(string m) { NOSaveSystemPlugin.Log?.LogInfo("v096: " + m); }

        // ---------- kv helpers ----------
        private static string Esc(string s) { return (s ?? "").Replace("%", "%25").Replace(";", "%3B").Replace("=", "%3D").Replace("|", "%7C"); }
        private static string Unesc(string s) { return (s ?? "").Replace("%7C", "|").Replace("%3D", "=").Replace("%3B", ";").Replace("%25", "%"); }
        private static string Fs(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }
        internal static Dictionary<string, string> Parse(string s)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(s)) return d;
            foreach (var kv in s.Split(';'))
            {
                int i = kv.IndexOf('='); if (i <= 0) continue;
                d[kv.Substring(0, i)] = kv.Substring(i + 1);
            }
            return d;
        }
        private static float PF(Dictionary<string, string> d, string k, float def) { return d.TryGetValue(k, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : def; }
        private static string NameOf(PersistentID id)
        {
            try { if (UnitRegistry.TryGetUnit(id, out var u) && u != null) return u.UniqueName; } catch { }
            return null;
        }
        private static string NameOf(Unit u) { try { return u != null ? u.UniqueName : null; } catch { return null; } }

        private static Dictionary<string, Unit> _byName;
        private static Unit FindUnit(string name)
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

        // ---------- capture ----------
        internal static string CaptureExtra(Unit unit)
        {
            var sb = new StringBuilder();
            void Add(string k, string v) { if (v == null) return; if (sb.Length > 0) sb.Append(';'); sb.Append(k).Append('=').Append(Esc(v)); }

            if (unit is Missile m)
            {
                Add("mTarget", NameOf(m.targetID));
                Add("mOwner", NameOf(m.ownerID));
                try { Add("mSeeker", ((int)m.seekerMode).ToString(CultureInfo.InvariantCulture)); } catch { }
                try { Add("mAge", Fs(m.timeSinceSpawn)); } catch { }
                try { Add("mBooster", m.boosterIsAttached ? "1" : "0"); } catch { }
                try { var f = typeof(Missile).GetField("motorStage", BF); if (f != null) Add("mStage", ((int)f.GetValue(m)).ToString(CultureInfo.InvariantCulture)); } catch { }
                try { if (m.rb != null) Add("mMass", Fs(m.rb.mass)); } catch { }
                try
                {
                    var arr = typeof(Missile).GetField("motors", BF)?.GetValue(m) as Array;
                    if (arr != null)
                    {
                        var ms = new StringBuilder();
                        foreach (var mo in arr)
                        {
                            if (ms.Length > 0) ms.Append('|');
                            if (mo == null) { ms.Append("x"); continue; }
                            var t = mo.GetType();
                            float fuel = (float)(t.GetField("fuelMass", BF)?.GetValue(mo) ?? 0f);
                            float rate = (float)(t.GetField("burnRate", BF)?.GetValue(mo) ?? 0f);
                            float delay = (float)(t.GetField("delayTimer", BF)?.GetValue(mo) ?? 0f);
                            bool act = (bool)(t.GetField("activated", BF)?.GetValue(mo) ?? false);
                            ms.Append(Fs(fuel)).Append(',').Append(Fs(rate)).Append(',').Append(Fs(delay)).Append(',').Append(act ? "1" : "0");
                        }
                        Add("mMotors", ms.ToString());
                    }
                }
                catch { }
            }
            else if (unit is Aircraft ac)
            {
                try
                {
                    var wm = ac.weaponManager;
                    if (wm != null)
                    {
                        var names = new List<string>();
                        foreach (var t in wm.GetTargetList()) { var n = NameOf(t); if (!string.IsNullOrEmpty(n)) names.Add(n.Replace("|", "%7C")); }
                        Add("tList", string.Join("|", names));
                        if (wm.currentWeaponStation != null) Add("tStation", wm.currentWeaponStation.Number.ToString(CultureInfo.InvariantCulture));
                    }
                }
                catch { }
                try
                {
                    var p = NoSavePilotFix.GetPilot(ac);
                    if (p != null)
                    {
                        Add("tPrimary", NameOf(p.GetPrimaryTarget()));
                        var st = p.currentState;
                        var f = st?.GetType().GetField("currentTarget", BF);
                        if (f != null) Add("tAI", NameOf(f.GetValue(st) as Unit));
                    }
                }
                catch { }
            }
            // v0.9.8: per-station / per-weapon ammo for every armed unit (aircraft, vehicles, ships)
            try
            {
                if (!(unit is Missile) && unit.weaponStations != null && unit.weaponStations.Count > 0)
                {
                    var ws = new StringBuilder();
                    foreach (var st in unit.weaponStations)
                    {
                        if (st == null || st.Weapons == null) continue;
                        for (int j = 0; j < st.Weapons.Count; j++)
                        {
                            var w = st.Weapons[j]; if (w == null) continue;
                            if (ws.Length > 0) ws.Append('|');
                            ws.Append(st.Number.ToString(CultureInfo.InvariantCulture)).Append(',').Append(j.ToString(CultureInfo.InvariantCulture)).Append(',')
                              .Append(w.ammo.ToString(CultureInfo.InvariantCulture)).Append(',').Append(w.GetType().Name);
                        }
                    }
                    Add("wAmmo", ws.ToString());
                }
            }
            catch { }
            return sb.ToString();
        }

        // ---------- weapon ammo (v0.9.8) ----------
        private static MidMissionSnapshot _ammoSnap;
        private static float _ammoSnapAt = -1000f;

        internal static void ClearAmmoReassert() { _ammoSnap = null; }

        internal static void ReassertAmmoOnResume()
        {
            if (_ammoSnap == null) return;
            if (Time.unscaledTime - _ammoSnapAt > 600f) { _ammoSnap = null; return; }
            _byName = null;
            int n = ApplyAllAmmo(_ammoSnap, null, "resume");
            Log("resume ammo units=" + n);
            _ammoSnap = null;
        }

        private static IEnumerable<SavedUnitDto> AllDtos(MidMissionSnapshot snap)
        {
            if (snap == null) yield break;
            if (snap.aircraft != null) foreach (var d in snap.aircraft) yield return d;
            if (snap.vehicles != null) foreach (var d in snap.vehicles) yield return d;
            if (snap.ships != null) foreach (var d in snap.ships) yield return d;
            if (snap.otherUnits != null) foreach (var d in snap.otherUnits) yield return d;
        }

        private static int ApplyAllAmmo(MidMissionSnapshot snap, StringBuilder plan, string pass)
        {
            int units = 0;
            foreach (var dto in AllDtos(snap))
            {
                if (dto == null || string.IsNullOrEmpty(dto.v096) || dto.v096.IndexOf("wAmmo=", StringComparison.Ordinal) < 0) continue;
                var u = FindUnit(dto.private_uniqueName);
                if (u == null) { Log(pass + " AMMO unit not found " + dto.private_uniqueName); continue; }
                try { if (ApplyAmmo(u, Parse(dto.v096), pass, plan)) units++; }
                catch (Exception ex) { Log(pass + " AMMO FAIL " + dto.private_uniqueName + ": " + ex.Message); }
            }
            return units;
        }

        private static bool ApplyAmmo(Unit u, Dictionary<string, string> d, string pass, StringBuilder plan)
        {
            if (!d.TryGetValue("wAmmo", out var raw) || u.weaponStations == null) return false;
            raw = Unesc(raw);
            int set = 0, same = 0, miss = 0, typeMis = 0;
            var touched = new HashSet<WeaponStation>();
            var sbChg = new StringBuilder();
            foreach (var item in raw.Split('|'))
            {
                var p = item.Split(','); if (p.Length < 3) continue;
                int num, j, ammo;
                if (!int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out num) || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out j) || !int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out ammo)) continue;
                WeaponStation st = null;
                foreach (var s in u.weaponStations) if (s != null && s.Number == num) { st = s; break; }
                if (st == null || st.Weapons == null || j >= st.Weapons.Count || st.Weapons[j] == null) { miss++; continue; }
                var w = st.Weapons[j];
                if (p.Length >= 4 && p[3] != w.GetType().Name) { typeMis++; continue; }
                if (w.ammo == ammo) { same++; touched.Add(st); continue; }
                if (sbChg.Length < 300) sbChg.Append(" st").Append(num).Append('w').Append(j).Append(':').Append(w.ammo).Append("->").Append(ammo);
                w.ammo = ammo; set++; touched.Add(st);
            }
            int stationAmmo = 0;
            foreach (var st in touched) { try { st.AccountAmmo(); stationAmmo += st.Ammo; } catch { } }
            string label = "?"; try { label = u.UniqueName; } catch { }
            string msg = pass + " AMMO " + label + " set=" + set + " same=" + same + " missing=" + miss + " typeMismatch=" + typeMis + " stationsAccounted=" + touched.Count + " totalAmmo=" + stationAmmo + sbChg;
            if (set > 0 || miss > 0 || typeMis > 0 || pass == "pass1") { Log(msg); plan?.AppendLine("  " + msg); }
            return true;
        }

        // ---------- missile spawn ----------
        private static readonly List<KeyValuePair<Missile, SavedUnitDto>> _missiles = new List<KeyValuePair<Missile, SavedUnitDto>>();

        internal static bool TrySpawnMissile(Spawner spawner, SavedUnitDto dto, GlobalPosition gp, Quaternion rot, StringBuilder plan)
        {
            if (spawner == null || dto == null) return false;
            var d = Parse(dto.v096);
            if (!Encyclopedia.i.TryGetPrefab(dto.type, out var prefab) || prefab == null)
            {
                plan?.AppendLine("    MISSILE prefab not found type=" + dto.type + " -> legacy SavedMissile");
                return false;
            }
            string target = d.TryGetValue("mTarget", out var tn) ? Unesc(tn) : "";
            string owner = d.TryGetValue("mOwner", out var on) ? Unesc(on) : "";
            Vector3 vel = (dto.velocityCaptured && dto.velocity != null) ? new Vector3(dto.velocity.x, dto.velocity.y, dto.velocity.z) : Vector3.zero;
            FactionHQ hq = null;
            try { hq = FactionRegistry.HqFromName(dto.faction); } catch { }
            Missile m = null;
            try
            {
                m = spawner.SpawnSavedMissile(prefab, gp, rot, hq, target ?? "", owner ?? "", vel, dto.private_uniqueName ?? "");
            }
            catch (Exception ex)
            {
                plan?.AppendLine("    MISSILE SpawnSavedMissile FAIL " + dto.private_uniqueName + ": " + ex.Message);
                return false;
            }
            if (m == null) return false;
            ApplyMissileState(m, dto, d, plan);
            _missiles.Add(new KeyValuePair<Missile, SavedUnitDto>(m, dto));
            plan?.AppendLine("    MISSILE spawned " + dto.private_uniqueName + " type=" + dto.type + " target='" + target + "' owner='" + owner + "' v=" + vel.magnitude.ToString("0.0"));
            Log("missile spawned " + dto.private_uniqueName + " target='" + target + "' owner='" + owner + "'");
            return true;
        }

        private static void ApplyMissileState(Missile m, SavedUnitDto dto, Dictionary<string, string> d, StringBuilder plan)
        {
            try { if (d.TryGetValue("mSeeker", out var s)) m.NetworkseekerMode = (Missile.SeekerMode)(byte)int.Parse(s, CultureInfo.InvariantCulture); } catch (Exception ex) { plan?.AppendLine("    MISSILE seeker FAIL " + ex.Message); }
            try { var pr = typeof(Missile).GetProperty("timeSinceSpawn", BF); pr?.GetSetMethod(true)?.Invoke(m, new object[] { PF(d, "mAge", 0f) }); } catch { }
            try { if (d.TryGetValue("mBooster", out var b) && b == "0") m.boosterIsAttached = false; } catch { }
            try { var f = typeof(Missile).GetField("motorStage", BF); if (f != null && d.ContainsKey("mStage")) f.SetValue(m, (int)PF(d, "mStage", 0f)); } catch { }
            try { if (m.rb != null && d.ContainsKey("mMass")) m.rb.mass = PF(d, "mMass", m.rb.mass); } catch { }
            try
            {
                if (d.TryGetValue("mMotors", out var ms))
                {
                    var arr = typeof(Missile).GetField("motors", BF)?.GetValue(m) as Array;
                    var segs = Unesc(ms).Split('|');
                    for (int i = 0; arr != null && i < arr.Length && i < segs.Length; i++)
                    {
                        var mo = arr.GetValue(i); if (mo == null || segs[i] == "x") continue;
                        var p = segs[i].Split(','); if (p.Length < 4) continue;
                        float fuel = float.Parse(p[0], CultureInfo.InvariantCulture), rate = float.Parse(p[1], CultureInfo.InvariantCulture), delay = float.Parse(p[2], CultureInfo.InvariantCulture);
                        bool act = p[3] == "1";
                        var t = mo.GetType();
                        t.GetField("fuelMass", BF)?.SetValue(mo, fuel);
                        if (act)
                        {
                            // Activate() computes burnRate = fuelMass / burnTime -> set burnTime = remaining burn so the rate matches.
                            if (rate > 0f) t.GetField("burnTime", BF)?.SetValue(mo, Mathf.Max(0.0001f, fuel / rate));
                            t.GetField("delayTimer", BF)?.SetValue(mo, 0f);
                        }
                        else t.GetField("delayTimer", BF)?.SetValue(mo, delay);
                    }
                }
            }
            catch (Exception ex) { plan?.AppendLine("    MISSILE motors FAIL " + ex.Message); }
            try { if (m.rb != null && dto.velocity != null) m.rb.velocity = new Vector3(dto.velocity.x, dto.velocity.y, dto.velocity.z); } catch { }
        }

        // ---------- post pass ----------
        internal static void RestoreTargetsAndMissiles(MidMissionSnapshot snap, StringBuilder plan)
        {
            _ammoSnap = snap; _ammoSnapAt = Time.unscaledTime;
            int pending = Pass(snap, plan, "pass1");
            if (pending > 0 && NOSaveSystemPlugin.Instance != null)
                NOSaveSystemPlugin.Instance.StartCoroutine(NoSaveCoroutines.Safe(NoSaveVerify.Track(RetryCo(snap)), "TargetsRetry"));
        }

        private static IEnumerator RetryCo(MidMissionSnapshot snap)
        {
            foreach (var w in new[] { 0.5f, 1.5f, 3f })
            {
                float t = 0f; while (t < w) { t += NoSaveWait.Dt(); yield return null; } // v0.11.0: pause-aware
                if (Pass(snap, null, "retry+" + w + "s") == 0) yield break;
            }
            NoSaveWait.Timeout("targets-retry", 5f, "unresolved missile/target references left as spawned");
            _missiles.Clear();
        }

        /// <returns>number of unresolved references</returns>
        private static int Pass(MidMissionSnapshot snap, StringBuilder plan, string pass)
        {
            _byName = null;
            int unresolved = 0, mOk = 0, aOk = 0;
            // Missiles: re-target / re-owner if target spawned after the missile.
            foreach (var kv in _missiles.ToArray())
            {
                var m = kv.Key; if (m == null) continue;
                bool alive = false; try { alive = !m.Equals(null) && !m.disabled; } catch { }
                if (!alive) continue;
                var d = Parse(kv.Value.v096);
                string tn = d.TryGetValue("mTarget", out var a) ? Unesc(a) : null;
                string on = d.TryGetValue("mOwner", out var b) ? Unesc(b) : null;
                try
                {
                    if (!string.IsNullOrEmpty(tn))
                    {
                        var tu = FindUnit(tn);
                        if (tu == null) unresolved++;
                        else if (m.targetID != tu.persistentID) { m.SetTarget(tu); mOk++; Log(pass + " missile " + m.UniqueName + " SetTarget(" + tn + ")"); }
                        else mOk++;
                    }
                    if (!string.IsNullOrEmpty(on))
                    {
                        var ou = FindUnit(on);
                        if (ou == null) unresolved++;
                        else if (m.ownerID != ou.persistentID) { m.NetworkownerID = ou.persistentID; Log(pass + " missile " + m.UniqueName + " owner=" + on); }
                    }
                    try { if (d.TryGetValue("mSeeker", out var s)) m.NetworkseekerMode = (Missile.SeekerMode)(byte)int.Parse(s, CultureInfo.InvariantCulture); } catch { }
                }
                catch (Exception ex) { Log(pass + " missile retarget FAIL " + ex.Message); }
            }
            // Aircraft targets
            if (snap?.aircraft != null)
            {
                foreach (var dto in snap.aircraft)
                {
                    if (dto == null || string.IsNullOrEmpty(dto.v096)) continue;
                    var ac = FindUnit(dto.private_uniqueName) as Aircraft;
                    if (ac == null) continue;
                    var d = Parse(dto.v096);
                    try { unresolved += ApplyAircraftTargets(ac, d, pass, plan); aOk++; }
                    catch (Exception ex) { Log(pass + " targets FAIL " + dto.private_uniqueName + ": " + ex.Message); }
                }
            }
            try { ApplyAllAmmo(snap, plan, pass); } catch (Exception ex) { Log(pass + " ammo FAIL " + ex.Message); }
            string msg = pass + " missilesTargeted=" + mOk + " aircraftProcessed=" + aOk + " unresolved=" + unresolved;
            plan?.AppendLine("  TARGETS " + msg);
            Log(msg);
            return unresolved;
        }

        private static int ApplyAircraftTargets(Aircraft ac, Dictionary<string, string> d, string pass, StringBuilder plan)
        {
            int unresolved = 0;
            string label = ac.UniqueName ?? ac.name;
            var wm = ac.weaponManager;
            if (wm != null && d.TryGetValue("tStation", out var st))
            {
                try
                {
                    byte idx = (byte)int.Parse(st, CultureInfo.InvariantCulture);
                    if (wm.currentWeaponStation == null || wm.currentWeaponStation.Number != idx)
                    {
                        if (idx < ac.weaponStations.Count) { wm.SetActiveStation(idx); Log(pass + " " + label + " active station=" + idx); }
                    }
                }
                catch (Exception ex) { Log(pass + " " + label + " station FAIL " + ex.Message); }
            }
            if (wm != null && d.TryGetValue("tList", out var tl) && !string.IsNullOrEmpty(tl))
            {
                var names = Unesc(tl).Split('|');
                var want = new List<Unit>();
                foreach (var n in names) { var u = FindUnit(n.Replace("%7C", "|")); if (u == null || u.disabled) unresolved++; else want.Add(u); }
                var cur = wm.GetTargetList();
                bool same = cur.Count == want.Count;
                for (int i = 0; same && i < want.Count; i++) same = cur[i] == want[i];
                if (!same && want.Count > 0)
                {
                    bool isLocal = false;
                    try { isLocal = GameManager.IsLocalAircraft(ac); } catch { }
                    var hud = isLocal ? SceneSingleton<CombatHUD>.i : null;
                    wm.ClearTargetList();
                    for (int i = want.Count - 1; i >= 0; i--) // AddTargetList inserts at 0 -> add in reverse to keep order
                    {
                        if (hud != null && hud.aircraft == ac) hud.SelectUnit(want[i]); // native: marker + AddTargetList
                        if (!wm.CheckIsTarget(want[i])) wm.AddTargetList(want[i]);
                    }
                    Log(pass + " " + label + " targetList=" + want.Count + "/" + names.Length + (isLocal ? " (player HUD)" : ""));
                }
            }
            var p = NoSavePilotFix.GetPilot(ac);
            if (p != null)
            {
                if (d.TryGetValue("tPrimary", out var pn))
                {
                    var u = FindUnit(Unesc(pn));
                    if (u == null) unresolved++; else if (p.GetPrimaryTarget() != u) { p.SetPrimaryTarget(u); Log(pass + " " + label + " primaryTarget=" + u.UniqueName); }
                }
                if (d.TryGetValue("tAI", out var an))
                {
                    var u = FindUnit(Unesc(an));
                    var stt = p.currentState;
                    var f = stt?.GetType().GetField("currentTarget", BF);
                    if (u == null) unresolved++;
                    else if (f != null && (f.GetValue(stt) as Unit) != u)
                    {
                        f.SetValue(stt, u);
                        try
                        {
                            var ft = stt.GetType().GetField("currentTargetTracking", BF);
                            if (ft != null && ac.NetworkHQ != null) ft.SetValue(stt, ac.NetworkHQ.GetTrackingData(u.persistentID));
                        }
                        catch { }
                        if (wm != null && !wm.CheckIsTarget(u)) wm.AddTargetList(u);
                        Log(pass + " " + label + " AI currentTarget=" + u.UniqueName + " (" + stt.GetType().Name + ")");
                    }
                }
            }
            return unresolved;
        }
    }
}
