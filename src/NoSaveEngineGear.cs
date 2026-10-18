using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using NuclearOption.Jobs;
using UnityEngine;

namespace NOSaveSystem
{
    internal static class NoSaveEngineGear
    {
        internal static readonly HashSet<string> EngineTypes = new HashSet<string>(StringComparer.Ordinal)
        { "Turbojet", "Turbofan", "TurbineEngine", "PropFan", "DuctedFan", "ConstantSpeedProp", "RotorShaft", "Transmission" };

        private static readonly string[] FloatFields =
        { "thrust", "rpm", "thrustRatio", "currentThrust", "currentRPM", "spoolRatio", "dynamicThrustFactor",
          "dynamicThrustFactorSmoothed", "currentPower", "powerRatio", "RPMRatio", "startedAmount", "throttle",
          "rpmRatio", "availablePower", "availableThrust", "angularVelocity", "RPM", "trimThrust",
          "throttlePosition", "throttleSmoothingVel", "PropPitch", "powerAvailable", "engineTorque", "averageAoA",
          "totalPowerRequested",
          "currentThrustSmoothVel", "dynamicThrustFactorSmoothingVel" };
        private static readonly string[] VerifyFields = { "DuctedFan.currentThrustSmoothVel", "Turbofan.dynamicThrustFactorSmoothingVel" };
        private static readonly string[] NestedFloatFields = { "propPitchPID.i", "propPitchPID.p", "propPitchPID.d", "propPitchPID.previousError" };
        private static readonly string[] BoolFields = { "afterburnerOn", "hasFuel", "operable", "inoperable" };
        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal const float HoldTimeoutPlayer = 3.0f;
        internal const float HoldTimeoutAi = 1.5f;
        internal const float BlendTime = 0.75f;
        internal const float BlendTimeTimeout = 1.5f;
        internal const float MonitorWindow = 3.0f;
        internal const int InitialForceSteps = 10;
        internal const int ProbeSteps = 8;
        internal const int ConvergeSteps = 30;
        internal const int ReforceSteps = 10;
        private const float ProbeEps = 2e-4f;
        private const float ProbeSigmaK = 4f;
        private const int ProbeMaxOutliers = 2;
        private const float CumAbortMax = 0.0025f;
        private const float CumReleaseMax = 0.0015f;
        private const int SigmaSteps = 8;
        private const float SpikeK = 3f;
        private static readonly HashSet<string> ProbeFields = new HashSet<string>(StringComparer.Ordinal)
        { "currentRPM", "RPM", "rpm", "startedAmount", "currentPower" };
        private static readonly HashSet<string> RpmFields = new HashSet<string>(StringComparer.Ordinal) { "currentRPM", "RPM", "rpm" };
        private const float ThrottleMoveMax = 0.02f;
        private const int ThrottleMoveSteps = 3;
        private const int ThrottleGraceSteps = 10;
        private const int AiThrottleGraceSteps = 5;
        private const float ThrottleSlack = 0.005f;
        private const float DeadHoldMin = 1.0f;
        private const float DeadHardCapExtra = 2.0f;
        private const float RiseEps = 0.002f;
        private const int RiseSkipSteps = 15;
        private const float PassEpsMin = 0.003f;
        private const int DipFailSteps = 3;
        private const int NoFuelSteps = 2;
        private const int SoftDamageSteps = 2;
        private const float SampleEvery = 0.25f;
        private static readonly string[] PartFieldNames = { "part", "unitPart" };

        private static void Log(string m) { NOSaveSystemPlugin.Log?.LogInfo("EngineGear: " + m); }
        private static void Eng(string m) { NOSaveSystemPlugin.Log?.LogInfo("NOSAVE-ENG " + m); }
        private static string N(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
        private static void EngArm(bool player, string m)
        {
            if (player) NOSaveSystemPlugin.Log?.LogInfo("NOSAVE-ENG " + m);
            else NOSaveSystemPlugin.Log?.LogDebug("NOSAVE-ENG " + m);
        }
        private static string E(float v) { return v.ToString("0.###E+0", CultureInfo.InvariantCulture); }

        private static readonly Dictionary<string, FieldInfo> _fi = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        private static FieldInfo Fi(Type t, string name)
        {
            string k = t.FullName + "." + name;
            if (_fi.TryGetValue(k, out var f)) return f;
            for (var cur = t; cur != null && f == null; cur = cur.BaseType) f = cur.GetField(name, BF | BindingFlags.DeclaredOnly);
            _fi[k] = f;
            return f;
        }
        private static readonly Dictionary<string, FieldInfo[]> _nfi = new Dictionary<string, FieldInfo[]>(StringComparer.Ordinal);
        private static FieldInfo[] NFi(Type t, string path)
        {
            string k = t.FullName + "." + path;
            if (_nfi.TryGetValue(k, out var r)) return r;
            r = null;
            int dot = path.IndexOf('.');
            if (dot > 0)
            {
                var o = Fi(t, path.Substring(0, dot));
                if (o != null)
                {
                    var i = Fi(o.FieldType, path.Substring(dot + 1));
                    if (i != null && i.FieldType == typeof(float)) r = new[] { o, i };
                }
            }
            _nfi[k] = r;
            return r;
        }

        private static T Get<T>(object o, string name, T def)
        {
            try { var f = o == null ? null : Fi(o.GetType(), name); if (f != null) { var v = f.GetValue(o); if (v is T tv) return tv; } } catch { }
            return def;
        }

        private static List<MonoBehaviour> Engines(Aircraft ac)
        {
            var list = new List<MonoBehaviour>();
            foreach (var mb in ac.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && EngineTypes.Contains(mb.GetType().Name)) list.Add(mb);
            try
            {
                var extra = new List<object>();
                if (ac.engines != null) foreach (var e in ac.engines) extra.Add(e);
                if (ac.engineStates != null) foreach (var e in ac.engineStates) extra.Add(e);
                foreach (var o in extra)
                {
                    var mb = o as MonoBehaviour; if (mb == null || list.Contains(mb)) continue;
                    list.Add(mb);
                    foreach (var tr in mb.GetComponentsInParent<Transmission>(true)) if (tr != null && !list.Contains(tr)) list.Add(tr);
                    foreach (var tr in mb.GetComponents<Transmission>()) if (tr != null && !list.Contains(tr)) list.Add(tr);
                }
            }
            catch { }
            return list;
        }

        private static float SumThrust(IList<MonoBehaviour> engines)
        {
            float s = 0f;
            for (int i = 0; i < engines.Count; i++) { try { if (engines[i] is IThrustSource ts && engines[i] != null) s += ts.GetThrust(); } catch { } }
            return s;
        }
        private static float SumPower(IList<MonoBehaviour> engines)
        {
            float s = 0f;
            for (int i = 0; i < engines.Count; i++) { try { if (engines[i] is IPowerSource ps && !(engines[i] is Transmission) && engines[i] != null) s += ps.GetPower(); } catch { } }
            return s;
        }

        internal static void Capture(Aircraft ac, SavedAircraftDto dto)
        {
            if (ac == null || dto == null) return;
            try { dto.gearState = (int)ac.gearState; } catch { dto.gearState = -1; }
            var sb = new StringBuilder();
            var engines = Engines(ac);
            foreach (var mb in engines)
            {
                var t = mb.GetType();
                if (sb.Length > 0) sb.Append('#');
                sb.Append(t.Name).Append('|');
                bool first = true;
                foreach (var fn in FloatFields)
                {
                    var f = Fi(t, fn);
                    if (f == null || f.FieldType != typeof(float)) continue;
                    try
                    {
                        float v = (float)f.GetValue(mb);
                        if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                        if (!first) sb.Append(';'); first = false;
                        sb.Append(fn).Append('=').Append(v.ToString("R", CultureInfo.InvariantCulture));
                    }
                    catch { }
                }
                foreach (var fn in NestedFloatFields)
                {
                    var nf = NFi(t, fn); if (nf == null) continue;
                    try
                    {
                        var o = nf[0].GetValue(mb); if (o == null) continue;
                        float v = (float)nf[1].GetValue(o);
                        if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                        if (!first) sb.Append(';'); first = false;
                        sb.Append(fn).Append('=').Append(v.ToString("R", CultureInfo.InvariantCulture));
                    }
                    catch { }
                }
                foreach (var fn in BoolFields)
                {
                    var f = Fi(t, fn);
                    if (f == null || f.FieldType != typeof(bool)) continue;
                    try { if (!first) sb.Append(';'); first = false; sb.Append(fn).Append('=').Append((bool)f.GetValue(mb) ? "1" : "0"); } catch { }
                }
            }
            dto.engineState = sb.ToString();
            dto.savedThrust = SumThrust(engines);
            dto.savedPower = SumPower(engines);
            try { dto.animState = CaptureAnim(ac); } catch (Exception ex) { dto.animState = ""; Log("anim capture FAIL " + ex.Message); }
        }

        private struct FieldVal { public FieldInfo f; public object v; public bool isFloat, isThrottle; }
        private struct NestedVal { public FieldInfo outer, inner; public float v; }

        private sealed class CompVals
        {
            public MonoBehaviour mb; public Lock L; public bool isTransmission;
            public readonly List<FieldVal> vals = new List<FieldVal>();
            public readonly List<NestedVal> nested = new List<NestedVal>();
            public bool hasPitch; public float pitch;
            public readonly List<FieldInfo> probeF = new List<FieldInfo>();
            public readonly List<float> probeSaved = new List<float>();
            public float[] probeDen, prev; public bool prevValid, everTicked;
            public float maxRate; public int maxRateIdx = -1;
            public float[][] hist;
            public double[] fS, fQ; public int fN;
            public float[][] sig; public int sigN;
            public bool savedDamaged, savedNoFuel; public int softDmgRun;
            public bool dead, excluded; public float savedPow, savedRpmC; public int killTries; public string exclReason;
        }

        private static List<CompVals> Parse(Aircraft ac, SavedAircraftDto dto, out int mismatch, out float savedRpm)
        {
            mismatch = 0; savedRpm = 0f;
            var res = new List<CompVals>();
            if (string.IsNullOrEmpty(dto.engineState)) return res;
            var live = Engines(ac);
            var parts = dto.engineState.Split('#');
            for (int i = 0; i < parts.Length && i < live.Count; i++)
            {
                var seg = parts[i]; int bar = seg.IndexOf('|'); if (bar < 0) continue;
                var mb = live[i]; var t = mb.GetType();
                if (seg.Substring(0, bar) != t.Name) { mismatch++; continue; }
                var cv = new CompVals { mb = mb, isTransmission = mb is Transmission };
                bool coherentPitch = Fi(t, "blades") != null;
                foreach (var kv in seg.Substring(bar + 1).Split(';'))
                {
                    int eq = kv.IndexOf('='); if (eq <= 0) continue;
                    string name = kv.Substring(0, eq), val = kv.Substring(eq + 1);
                    try
                    {
                        if (name.IndexOf('.') > 0)
                        {
                            var nf = NFi(t, name); if (nf == null) continue;
                            cv.nested.Add(new NestedVal { outer = nf[0], inner = nf[1], v = float.Parse(val, CultureInfo.InvariantCulture) });
                            continue;
                        }
                        var f = Fi(t, name); if (f == null) continue;
                        if (f.FieldType == typeof(float))
                        {
                            float v = float.Parse(val, CultureInfo.InvariantCulture);
                            if (name == "PropPitch")
                            {
                                if (coherentPitch) { cv.hasPitch = true; cv.pitch = v; }
                                continue;
                            }
                            cv.vals.Add(new FieldVal { f = f, v = v, isFloat = true, isThrottle = name.StartsWith("throttle", StringComparison.Ordinal) });
                            if (ProbeFields.Contains(name))
                            {
                                cv.probeF.Add(f); cv.probeSaved.Add(v);
                            }
                            if (!cv.isTransmission && RpmFields.Contains(name)) { savedRpm += Mathf.Abs(v); cv.savedRpmC += Mathf.Abs(v); }
                            if (name == "currentPower") cv.savedPow = v;
                        }
                        else if (f.FieldType == typeof(bool))
                        {
                            bool b = val == "1";
                            if (name == "operable") { if (!b) cv.savedDamaged = true; continue; }
                            if (name == "inoperable") { if (b) cv.savedDamaged = true; continue; }
                            if (name == "hasFuel") { if (!b) cv.savedNoFuel = true; b = true; }
                            cv.vals.Add(new FieldVal { f = f, v = b });
                        }
                    }
                    catch { }
                }
                int n = cv.probeF.Count;
                cv.probeDen = new float[n]; cv.prev = new float[n];
                cv.hist = new float[n][]; cv.fS = new double[n]; cv.fQ = new double[n]; cv.sig = new float[n][];
                for (int k = 0; k < n; k++) { cv.hist[k] = new float[ConvergeSteps]; cv.sig[k] = new float[SigmaSteps]; }
                for (int k = 0; k < n; k++)
                {
                    float floor = cv.probeF[k].Name == "startedAmount" ? 0.01f : 1f;
                    cv.probeDen[k] = Mathf.Max(Mathf.Abs(cv.probeSaved[k]), floor);
                    cv.prev[k] = cv.probeSaved[k];
                }
                res.Add(cv);
            }
            return res;
        }

        private static readonly Dictionary<Type, MethodInfo> _setPitch = new Dictionary<Type, MethodInfo>();
        private static readonly object[] _arg1 = new object[1];
        private static bool SetPropPitchCoherent(MonoBehaviour prop, float target)
        {
            var t = prop.GetType();
            var fPitch = Fi(t, "PropPitch"); var fBlades = Fi(t, "blades");
            if (fPitch == null || fBlades == null) return false;
            float cur = (float)fPitch.GetValue(prop);
            float d = target - cur;
            if (Mathf.Abs(d) < 1e-5f) return true;
            int turn = 1, pdir = 1;
            try { turn = Convert.ToInt32(Fi(t, "turnDirection").GetValue(prop)); } catch { }
            try { pdir = Convert.ToInt32(Fi(t, "pitchDirection").GetValue(prop)); } catch { }
            var blades = fBlades.GetValue(prop) as IList;
            if (blades == null) return false;
            for (int i = 0; i < blades.Count; i++)
            {
                var b = blades[i]; if (b == null) continue;
                var bt = b.GetType();
                if (!_setPitch.TryGetValue(bt, out var m)) { m = bt.GetMethod("SetPitch", BF); _setPitch[bt] = m; }
                if (m == null) return false;
                _arg1[0] = d * (0f - turn) * (0f - pdir);
                m.Invoke(b, _arg1);
            }
            fPitch.SetValue(prop, target);
            return true;
        }

        private static int Write(CompVals cv, bool skipThrottle)
        {
            int set = 0;
            for (int i = 0; i < cv.vals.Count; i++)
            {
                var fv = cv.vals[i];
                if (skipThrottle && fv.isThrottle) continue;
                try { fv.f.SetValue(cv.mb, fv.v); set++; } catch { }
            }
            for (int i = 0; i < cv.nested.Count; i++)
            {
                var nv = cv.nested[i];
                try { if (SetNested(cv.mb, nv, nv.v)) set++; } catch { }
            }
            if (cv.hasPitch) { try { if (SetPropPitchCoherent(cv.mb, cv.pitch)) set++; } catch { } }
            return set;
        }

        private static bool SetNested(MonoBehaviour mb, NestedVal nv, float v)
        {
            var o = nv.outer.GetValue(mb);
            if (o == null) return false;
            nv.inner.SetValue(o, v);
            if (nv.outer.FieldType.IsValueType) nv.outer.SetValue(mb, o);
            return true;
        }

        private static bool _pidReported;
        private static void ReportPidFields()
        {
            if (_pidReported) return;
            _pidReported = true;
            var sb = new StringBuilder();
            foreach (var n in EngineTypes)
            {
                Type t = null; try { t = AccessTools.TypeByName(n); } catch { }
                if (t == null) continue;
                foreach (var path in NestedFloatFields)
                {
                    int dot = path.IndexOf('.');
                    var outer = Fi(t, path.Substring(0, dot));
                    if (outer == null) continue;
                    var r = NFi(t, path);
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(n).Append('.').Append(path).Append(r != null ? "=found(" : "=MISSING(")
                      .Append(outer.FieldType.Name).Append(outer.FieldType.IsValueType ? " struct" : " class").Append(')');
                }
            }
            Eng("PID reflection: " + (sb.Length > 0 ? sb.ToString() : "no engine type declares " + string.Join("/", NestedFloatFields)));
            var sb2 = new StringBuilder();
            foreach (var p in VerifyFields)
            {
                int dot = p.IndexOf('.');
                Type t = null; try { t = AccessTools.TypeByName(p.Substring(0, dot)); } catch { }
                var f = t != null ? Fi(t, p.Substring(dot + 1)) : null;
                if (sb2.Length > 0) sb2.Append(", ");
                sb2.Append(p).Append(t == null ? "=MISSING(type)" : f == null ? "=MISSING(skipped)" : f.FieldType != typeof(float) ? "=MISSING(not float, skipped)" : "=found");
            }
            Eng("field reflection: " + sb2);
        }

        private static void WriteBlend(CompVals cv, float w, bool skipThrottle)
        {
            for (int i = 0; i < cv.vals.Count; i++)
            {
                var fv = cv.vals[i];
                if (!fv.isFloat || (skipThrottle && fv.isThrottle)) continue;
                try { float sv = (float)fv.v, live = (float)fv.f.GetValue(cv.mb); fv.f.SetValue(cv.mb, live + (sv - live) * w); } catch { }
            }
            for (int i = 0; i < cv.nested.Count; i++)
            {
                var nv = cv.nested[i];
                try { var o = nv.outer.GetValue(cv.mb); if (o != null) { float live = (float)nv.inner.GetValue(o); SetNested(cv.mb, nv, live + (nv.v - live) * w); } } catch { }
            }
            if (cv.hasPitch)
            {
                try { var f = Fi(cv.mb.GetType(), "PropPitch"); float live = (float)f.GetValue(cv.mb); SetPropPitchCoherent(cv.mb, live + (cv.pitch - live) * w); } catch { }
            }
        }

        private enum Phase { Forcing, Probe, Blending, Monitor }

        private sealed class Lock
        {
            public Aircraft ac; public SavedAircraftDto dto; public string label; public bool player; public int gen;
            public List<CompVals> comps; public MonoBehaviour[] engines;
            public float t; public bool released, done; public Phase phase = Phase.Forcing;
            public int holdSteps, reforceLeft = InitialForceSteps, probeRun, cleanRun, bestClean, probes, probeFails, stepCount;
            public float epsStep = ProbeEps, sigmaDelta, sigmaProbe, lastSlope = float.NaN, lastCum = float.NaN, lastProj = float.NaN;
            public bool sigmaReady; public string sigmaSrc = "-";
            public float liveThrottle, lastWrittenThr; public bool liveSeen, thrWritten;
            public string liveSrc = "saved"; public float lastAcc = float.NaN;
            public float prevMonThr = float.NaN; public int riseSkip, riseSkipped; public bool followStarted;
            public readonly bool[] outl = new bool[ConvergeSteps]; public int winPos, winN, outlCount;
            public string lastProbeFail = "-";
            public float holdEnd = -1f, blendStart = -1f, blendLen, releasedAt = -1f; public bool timedOut;
            public string endReason, skip;
            public float savedThrust, savedPower, savedRpm; public bool legacy;
            public bool deadOnly;
            public int playerSteps, throttleOffSteps; public bool baselineSet, throttleFree; public float baseline;
            public double noiseSum, noiseSq; public int noiseN; public float passEps = PassEpsMin, sigma;
            public int samplesChecked, monitorSteps, dipRun, maxDipRun; public bool dipFailed;
            public float minRatio = 99f, minAt = -1f, dipMax, dipArea; public string measured = "power";
            public int forcedWrites, blendWrites, ignitionForced, noFuelRun;
            public float nextSample, armGameTime; public bool sampleWriteDue;
            public readonly HashSet<MonoBehaviour> sampled = new HashSet<MonoBehaviour>();
            public object psRef; public bool simReapplied;
        }

        private static readonly Dictionary<MonoBehaviour, CompVals> _byComp = new Dictionary<MonoBehaviour, CompVals>();
        private static readonly Dictionary<Aircraft, Lock> _byAc = new Dictionary<Aircraft, Lock>();
        private static readonly HashSet<Aircraft> _expired = new HashSet<Aircraft>();
        private static int _gen;
        internal static int Gen { get { return _gen; } }
        internal static bool AnyActive;

        internal static void ClearAll(string why)
        {
            if (_byAc.Count > 0) Eng("locks cleared (" + why + ") count=" + _byAc.Count + " gen=" + _gen);
            foreach (var L in _byAc.Values) { L.done = true; L.released = true; }
            _byComp.Clear(); _byAc.Clear(); _expired.Clear(); _gearResumed.Clear(); AnyActive = false;
        }

        internal static void NewLoad(string why) { _gen++; ClearAll(why); try { NoSavePilotFix.ClearSeeded(); } catch { } }

        private static readonly List<MonoBehaviour> _pruneComp = new List<MonoBehaviour>();
        private static readonly List<Aircraft> _pruneAc = new List<Aircraft>();
        internal static void Prune()
        {
            _pruneComp.Clear(); _pruneAc.Clear();
            foreach (var k in _byComp.Keys) if (k == null) _pruneComp.Add(k);
            foreach (var k in _pruneComp) _byComp.Remove(k);
            foreach (var k in _expired) if (k == null) _pruneAc.Add(k);
            foreach (var k in _pruneAc) _expired.Remove(k);
            _pruneAc.Clear();
            foreach (var kv in _byAc) if (kv.Key == null) _pruneAc.Add(kv.Key);
            foreach (var k in _pruneAc) { if (_byAc.TryGetValue(k, out var L)) { L.done = true; L.released = true; } _byAc.Remove(k); }
            _pruneAc.Clear();
            foreach (var k in _gearResumed.Keys) if (k == null) _pruneAc.Add(k);
            foreach (var k in _pruneAc) _gearResumed.Remove(k);
            AnyActive = _byAc.Count > 0;
        }

        private static bool Alive(Aircraft ac) { try { return ac != null && !ac.Equals(null) && !ac.disabled; } catch { return false; } }
        private static float Timeout(Lock L) { return L.player ? HoldTimeoutPlayer : HoldTimeoutAi; }

        private static bool IsPlayer(Aircraft ac)
        {
            try { var pl = ac.Player; if (pl != null && pl.IsLocalPlayer) return true; } catch { }
            try { var p = NoSavePilotFix.GetPilot(ac); return p != null && p.currentState is PilotPlayerState; } catch { }
            return false;
        }

        private static void ReevalPlayer(Lock L, string source)
        {
            if (L.player || L.released) return;
            if (!IsPlayer(L.ac)) return;
            L.player = true;
            Eng(L.label + " ARM player=true (re-evaluated, source=" + source + ", t=" + N(L.t) + ") timeout now " + N(Timeout(L)) + "s");
        }

        internal static void ApplyEngines(Aircraft ac, SavedAircraftDto dto, string label, string source = "apply")
        {
            if (ac == null || dto == null) return;
            if (string.IsNullOrEmpty(dto.engineState))
            {
                if (IsPlayer(ac)) { try { NoSaveThrottleFollow.StartOnce(ac, label ?? SafeName(ac), _gen, "no-lock(no engineState) source=" + source); } catch { } }
                return;
            }
            Prune();
            string lbl = label ?? SafeName(ac);
            if (_expired.Contains(ac)) { Eng("LATE-WRITE blocked source=" + source + " aircraft=" + lbl + " (post-load window already ended - engine state left to the sim)"); return; }
            if (_byAc.TryGetValue(ac, out var existing))
            {
                if (existing.released) { Eng("LATE-WRITE blocked source=" + source + " aircraft=" + lbl + " t=" + N(existing.t) + " (hold already released)"); return; }
                if (label != null) existing.label = label;
                ReevalPlayer(existing, source);
                return;
            }
            int mismatch; float savedRpm;
            var comps = Parse(ac, dto, out mismatch, out savedRpm);
            var L = new Lock { ac = ac, dto = dto, label = lbl, player = IsPlayer(ac), comps = comps, gen = _gen };
            var eng = new List<MonoBehaviour>();
            foreach (var cv in comps) { cv.L = L; if (!cv.isTransmission) eng.Add(cv.mb); }
            L.engines = eng.ToArray();
            L.savedRpm = savedRpm;
            L.legacy = dto.savedThrust < 0f || dto.savedPower < 0f;
            L.savedThrust = dto.savedThrust >= 0f ? dto.savedThrust : LegacySum(dto, "currentThrust", "thrust");
            L.savedPower = dto.savedPower >= 0f ? dto.savedPower : LegacySum(dto, "currentPower", null);
            L.measured = L.savedPower > 1f ? "power" : L.savedRpm > 1f ? "rpm" : "none";
            L.armGameTime = Time.time;
            int probeN = 0, nestedN = 0; foreach (var cv in comps) { probeN += cv.probeF.Count; nestedN += cv.nested.Count; }
            ReportPidFields();
            if (comps.Count == 0 || NOSaveSystemPlugin.Instance == null)
            {
                Log(lbl + " engines: no hold (" + (comps.Count == 0 ? "no matching engine components, typeMismatch=" + mismatch : "plugin instance missing") + ") source=" + source);
                if (L.player) { try { NoSaveThrottleFollow.StartOnce(ac, lbl, _gen, "no-lock(no engine comps) source=" + source); } catch { } }
                return;
            }
            int deadN = ArmDead(L, source);
            int engN = 0; foreach (var cv in comps) if (!cv.isTransmission) engN++;
            L.deadOnly = engN > 0 && deadN >= engN;
            string issue = L.deadOnly ? null : HealthIssue(L, true);
            EngArm(L.player, lbl + " ARM source=" + source + " player=" + L.player + " comps=" + comps.Count + " probeFields=" + probeN + " nested=" + nestedN + " typeMismatch=" + mismatch
                + " savedPower=" + N(L.savedPower) + " savedRpm=" + N(L.savedRpm) + " savedThrust=" + N(L.savedThrust) + " savedThrottle=" + N(dto.throttle)
                + " ignition=" + dto.ignition + " legacy=" + L.legacy + " timeout=" + N(Timeout(L)) + "s probe window=" + ConvergeSteps + " steps eps=max(" + E(ProbeEps) + ",4sigma) outliers<=" + ProbeMaxOutliers
                + " cumRelease<=" + N(CumReleaseMax * 100f) + "%" + " deadAtSave=" + deadN + "/" + engN + (L.deadOnly ? " -> all engines destroyed: no hold, dead state forced " + N(DeadHoldMin) + "s" : "") + (issue != null ? " -> " + issue : ""));
            if (issue != null)
            {
                _byAc[ac] = L;
                L.endReason = issue; L.skip = issue; L.holdEnd = 0f; L.blendStart = 0f; L.releasedAt = 0f;
                if (L.player) Eng(lbl + " HOLD END reason=" + issue + " at arm (source=" + source + ") - no forced write, no blend");
                Result(L); Finish(L);
                return;
            }
            int set = 0;
            foreach (var cv in comps)
            {
                if (cv.dead) { DeadWrite(cv); _byComp[cv.mb] = cv; continue; }
                if (L.deadOnly) { cv.excluded = true; cv.exclReason = "all-engines-destroyed"; continue; }
                set += Write(cv, false); _byComp[cv.mb] = cv;
            }
            if (!L.deadOnly) ForceRunInputs(L, "arm");
            _byAc[ac] = L; AnyActive = true;
            Log(lbl + " engines live=" + comps.Count + " fieldsSet=" + set + " typeMismatch=" + mismatch + " source=" + source);
            NOSaveSystemPlugin.Instance.StartCoroutine(NoSaveCoroutines.Safe(MonitorCo(L), "EngineMonitor"));
        }

        private static float LegacySum(SavedAircraftDto dto, string a, string b)
        {
            float s = 0f;
            foreach (var seg in dto.engineState.Split('#'))
            {
                int bar = seg.IndexOf('|'); if (bar < 0) continue;
                foreach (var kv in seg.Substring(bar + 1).Split(';'))
                {
                    int eq = kv.IndexOf('='); if (eq <= 0) continue;
                    string n = kv.Substring(0, eq);
                    if ((n == a || (b != null && n == b)) && float.TryParse(kv.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) s += v;
                }
            }
            return s;
        }

        private static void ForceRunInputs(Lock L, string why)
        {
            if (!L.player && why != "arm") return;
            try
            {
                if (L.dto.ignition && !L.ac.Ignition)
                {
                    L.ac.NetworkIgnition = true;
                    L.ignitionForced++;
                    if (L.player && L.ignitionForced <= 3) Eng(L.label + " ignition was OFF -> forced ON (" + why + ", t=" + N(L.t) + ", count=" + L.ignitionForced + ")");
                }
            }
            catch { }
        }

        private static float BlendT01(Lock L) { return ThrottleMath.BlendProgress(L.t - L.blendStart, L.blendLen); }

        private static float BlendWeight(Lock L)
        {
            return ThrottleMath.BlendWeight(BlendT01(L));
        }

        internal static void OnTick(MonoBehaviour mb)
        {
            if (!_byComp.TryGetValue(mb, out var cv)) return;
            var L = cv.L;
            if (cv.dead)
            {
                if (L != null && !L.done && L.gen == _gen && Alive(L.ac)) { if (DeadCap(L, "tick")) return; DeadWrite(cv); }
                return;
            }
            if (L == null || L.released || L.done || L.gen != _gen) return;
            if (!Alive(L.ac)) return;
            float wdAge = Time.time - L.armGameTime;
            float wdBound = Timeout(L) + (L.player ? BlendTime : BlendTimeTimeout) + 1f;
            if (wdAge > wdBound)
            {
                L.released = true; L.endReason = "watchdog"; L.skip = "watchdog";
                bool restored = RestoreLiveThrottle(L);
                if (L.player) NoSaveWait.Timeout("engine-hold-watchdog", wdBound, "monitor coroutine stalled -> released without blend, live throttle restored");
                OnReleased(L);
                if (L.player) Eng(L.label + " HOLD END reason=watchdog phase=" + L.phase + " gameTime=" + N(wdAge) + "s bound=" + N(wdBound) + "s lockoutMs=" + Mathf.RoundToInt(wdAge * 1000f)
                    + " throttleLive=" + N(L.liveSeen ? L.liveThrottle : ReadThrottleIn(L)) + " saved=" + N(L.dto.throttle)
                    + " throttleRestored=" + (restored ? "yes" : "no") + " - released without blend");
                Result(L); Finish(L);
                return;
            }
            if (L.phase == Phase.Probe)
            {
                if (L.player) { OverrideThrottle(L, ThrottlePhase.Probe, 0f); WriteThrottleOnly(cv); }
                return;
            }
            bool logIt = L.player && L.sampleWriteDue && !L.sampled.Contains(mb);
            string pre = logIt ? Snapshot(mb) : null;
            string tag;
            bool freeThr = !L.player && L.throttleFree;
            if (L.phase == Phase.Forcing)
            {
                ForceRunInputs(L, "tick");
                if (L.player) OverrideThrottle(L, ThrottlePhase.Hold, 0f);
                Write(cv, freeThr); L.forcedWrites++; tag = "FORCED-WRITE";
            }
            else
            {
                float w = BlendWeight(L);
                if (L.player) OverrideThrottle(L, ThrottlePhase.Blend, BlendT01(L));
                WriteBlend(cv, w, freeThr); L.blendWrites++; tag = "BLEND-WRITE w=" + N(w);
            }
            if (logIt)
            {
                L.sampled.Add(mb);
                Eng(L.label + " t=" + N(L.t) + " " + tag + (L.throttleFree ? " (throttle fields free)" : "") + " " + mb.GetType().Name + " pre[" + pre + "] post[" + Snapshot(mb) + "]");
            }
        }

        private static readonly Dictionary<Type, bool> _hasFixed = new Dictionary<Type, bool>();
        internal static bool HasFixedUpdate(Type t)
        {
            if (_hasFixed.TryGetValue(t, out var b)) return b;
            b = false;
            for (var cur = t; cur != null && cur != typeof(MonoBehaviour); cur = cur.BaseType)
                if (AccessTools.DeclaredMethod(cur, "FixedUpdate") != null) { b = true; break; }
            _hasFixed[t] = b; return b;
        }

        internal static void OnTickPost(MonoBehaviour mb, string method)
        {
            if (!_byComp.TryGetValue(mb, out var cv)) return;
            if (cv.excluded) return;
            var L = cv.L;
            if (L == null || L.released || L.done || L.gen != _gen) return;
            bool fixedTick = method == "FixedUpdate";
            if (fixedTick != HasFixedUpdate(mb.GetType())) return;
            if (cv.probeF.Count == 0) return;
            float scale = 1f;
            if (!fixedTick) { float dt = Time.deltaTime; scale = dt > 1e-6f ? Time.fixedDeltaTime / dt : 1f; }
            bool probing = L.phase == Phase.Probe, forcing = L.phase == Phase.Forcing;
            bool sigNaN = false;
            for (int k = 0; k < cv.probeF.Count; k++)
            {
                try
                {
                    float live = (float)cv.probeF[k].GetValue(mb);
                    if (forcing)
                    {
                        double dlt = (live - cv.probeSaved[k]) / cv.probeDen[k] * scale;
                        if (!double.IsNaN(dlt)) { cv.fS[k] += dlt; cv.fQ[k] += dlt * dlt; if (k == 0) cv.fN++; }
                    }
                    if (probing && cv.prevValid)
                    {
                        float rs = (live - cv.prev[k]) / cv.probeDen[k] * scale;
                        if (cv.sigN < SigmaSteps) { if (float.IsNaN(rs)) sigNaN = true; else cv.sig[k][cv.sigN] = rs; }
                        float rate = Mathf.Abs(rs);
                        if (float.IsNaN(rate)) rate = 1f;
                        if (rate > cv.maxRate) { cv.maxRate = rate; cv.maxRateIdx = k; }
                    }
                    cv.prev[k] = live;
                }
                catch { }
            }
            if (probing && cv.prevValid && cv.sigN < SigmaSteps && !sigNaN) cv.sigN++;
            cv.prevValid = true; cv.everTicked = true;
        }

        private static readonly string[] SnapFields = { "throttle", "throttlePosition", "currentRPM", "RPM", "startedAmount", "currentPower", "powerRatio", "currentThrust", "thrust", "PropPitch", "hasFuel", "operable" };
        private static string Snapshot(MonoBehaviour mb)
        {
            var sb = new StringBuilder(); var t = mb.GetType();
            foreach (var n in SnapFields)
            {
                var f = Fi(t, n); if (f == null) continue;
                try
                {
                    var v = f.GetValue(mb);
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(n).Append('=').Append(v is float fv ? N(fv) : v is bool bv ? (bv ? "1" : "0") : Convert.ToString(v, CultureInfo.InvariantCulture));
                }
                catch { }
            }
            try { var nf = NFi(t, "propPitchPID.i"); if (nf != null) { var o = nf[0].GetValue(mb); if (o != null) sb.Append(" pid.i=").Append(N((float)nf[1].GetValue(o))); } } catch { }
            return sb.ToString();
        }

        private static string SafeName(Aircraft ac) { try { return ac.UniqueName ?? ac.name; } catch { return "?"; } }

        private static float LiveThrottle(Lock L)
        {
            if (L.player && !L.released) return PlayerLive(L);
            return ReadThrottleIn(L);
        }

        private static float PlayerLive(Lock L)
        {
            float thr, acc; bool on;
            if (NoSavePilotFix.TryPlayerInput(L.ac, out thr, out acc, out on))
            {
                L.lastAcc = acc;
                if (!NoSavePilotFix.IsSeeded(L.ac)) { L.liveSrc = "unseeded"; return L.dto.throttle; }
                if (on) { L.liveSrc = "acc"; return thr; }
                L.liveSrc = "acc(controls-off)"; return thr;
            }
            if (L.liveSeen) { L.liveSrc = "ci"; return L.liveThrottle; }
            L.liveSrc = "saved"; return L.dto.throttle;
        }

        private static void OverrideThrottle(Lock L, ThrottlePhase phase, float t01)
        {
            try
            {
                var ci = L.ac.GetInputs(); if (ci == null) return;
                float v = ci.throttle;
                if (!(L.thrWritten && Mathf.Abs(v - L.lastWrittenThr) < 1e-6f)) { L.liveThrottle = v; L.liveSeen = true; }
                float live = PlayerLive(L);
                float value = ThrottleMath.HoldOutput(L.dto.throttle, live, phase, t01);
                ci.throttle = value; L.lastWrittenThr = value; L.thrWritten = true;
            }
            catch { }
        }

        private static bool RestoreLiveThrottle(Lock L)
        {
            if (!L.player || !L.thrWritten) return false;
            try
            {
                var ci = L.ac.GetInputs(); if (ci == null) return false;
                if (Mathf.Abs(ci.throttle - L.lastWrittenThr) < 1e-6f) ci.throttle = L.liveSeen ? L.liveThrottle : ci.throttle;
                L.thrWritten = false;
                return true;
            }
            catch { return false; }
        }

        private static void WriteThrottleOnly(CompVals cv)
        {
            for (int i = 0; i < cv.vals.Count; i++)
            {
                var fv = cv.vals[i];
                if (!fv.isThrottle) continue;
                try { fv.f.SetValue(cv.mb, fv.v); } catch { }
            }
        }

        private static float ReadThrottleIn(Lock L) { try { var ci = L.ac.GetInputs(); return ci != null ? ci.throttle : 0f; } catch { return 0f; } }

        private static float Ratio(Lock L)
        {
            if (L.measured == "power") return SumPower(L.engines) / L.savedPower;
            if (L.measured == "rpm")
            {
                float s = 0f;
                foreach (var cv in L.comps)
                {
                    if (cv.isTransmission || cv.excluded) continue;
                    for (int i = 0; i < cv.vals.Count; i++)
                        if (cv.vals[i].isFloat && RpmFields.Contains(cv.vals[i].f.Name)) { try { s += Mathf.Abs((float)cv.vals[i].f.GetValue(cv.mb)); } catch { } }
                }
                return s / L.savedRpm;
            }
            return 1f;
        }

        private static void StartBlend(Lock L, string reason, float len)
        {
            L.endReason = reason; L.holdEnd = L.t;
            L.phase = Phase.Blending; L.blendStart = L.t; L.blendLen = len;
        }

        private static void LogHoldEnd(Lock L, string extra)
        {
            if (!L.player) return;
            Eng(L.label + " HOLD END t=" + N(L.t) + " reason=" + L.endReason + " timeout=" + (L.timedOut ? "yes" : "no")
                + " holdDuration=" + N(L.holdEnd) + "s holdSteps=" + L.holdSteps + " probes=" + L.probes + " probeFails=" + L.probeFails
                + " probeSteps=" + L.cleanRun + " window=" + L.winN + "/" + ConvergeSteps + " outliers=" + L.outlCount + " bestRun=" + L.bestClean + " lastProbeFail=" + L.lastProbeFail
                + " epsStep=" + E(L.epsStep) + " sigmaSrc=" + L.sigmaSrc + " sigmaProbe=" + E(L.sigmaProbe) + " sigmaForced=" + E(L.sigmaDelta)
                + " minSlope=" + (float.IsNaN(L.lastSlope) ? "n/a" : E(L.lastSlope)) + " dipMin=" + (float.IsNaN(L.lastCum) ? "n/a" : N(L.lastCum * 100f) + "%") + " throttleLive=" + N(LiveThrottle(L)) + " baseline=" + (L.baselineSet ? N(L.baseline) : "n/a") + " saved=" + N(L.dto.throttle)
                + " lockoutMs=" + Mathf.RoundToInt((Mathf.Max(L.holdEnd, 0f) + (L.phase == Phase.Blending ? L.blendLen : 0f)) * 1000f)
                + " forcedWrites=" + L.forcedWrites + " " + extra);
        }

        private static IEnumerator MonitorCo(Lock L)
        {
            var wait = new WaitForFixedUpdate();
            float lastLogged = -1f;
            while (true)
            {
                yield return wait;
                if (L.done) yield break;
                if (L.gen != _gen) { L.done = true; yield break; }
                if (!Alive(L.ac)) { if (L.player) Eng(L.label + " RESULT SKIP(aircraft-gone) t=" + N(L.t)); Finish(L); yield break; }
                if (Time.timeScale <= 0f) continue;
                L.t += Time.fixedDeltaTime;
                L.stepCount++;
                if (!L.released && (L.stepCount % 5) == 0) ReevalPlayer(L, "step");
                float inThr = LiveThrottle(L);

                if (L.deadOnly)
                {
                    if (DeadCap(L, "monitor")) yield break;
                    if (L.t >= DeadHoldMin)
                    {
                        L.endReason = "destroyed"; L.skip = "destroyed"; L.holdEnd = L.t; L.blendStart = L.t; L.released = true; L.releasedAt = L.t;
                        if (L.player) Eng(L.label + " HOLD END reason=destroyed t=" + N(L.t) + " (all engines destroyed at save: kept dead " + N(DeadHoldMin) + "s, no hold, no spool)");
                        OnReleased(L); Result(L); Finish(L); yield break;
                    }
                    continue;
                }

                {
                    string issue = HealthIssue(L, false);
                    if (issue != null)
                    {
                        L.endReason = issue; L.skip = issue;
                        if (L.holdEnd < 0f) L.holdEnd = L.t;
                        if (L.blendStart < 0f) L.blendStart = L.t;
                        L.released = true; L.releasedAt = L.t; OnReleased(L);
                        if (L.player) Eng(L.label + " HOLD END reason=" + issue + " t=" + N(L.t) + " holdSteps=" + L.holdSteps + " - released immediately, no blend, fuel/ignition no longer forced");
                        Result(L); Finish(L); yield break;
                    }
                }

                bool throttleRelease = false;
                if (L.phase == Phase.Forcing || L.phase == Phase.Probe)
                {
                    if (L.player)
                    {
                        bool ps = false; try { var p = NoSavePilotFix.GetPilot(L.ac); ps = p != null && p.currentState is PilotPlayerState; } catch { }
                        if (ps)
                        {
                            L.playerSteps++;
                            if (!L.baselineSet)
                            {
                                if (L.playerSteps >= ThrottleGraceSteps)
                                {
                                    L.baselineSet = true; L.baseline = inThr;
                                    Eng(L.label + " THROTTLE baseline=" + N(L.baseline) + " saved=" + N(L.dto.throttle) + " (live input after " + ThrottleGraceSteps
                                        + " unpaused PilotPlayerState steps; held at saved until converged, then blended to live)");
                                }
                            }
                        }
                    }
                    else if (L.holdSteps >= AiThrottleGraceSteps)
                    {
                        if (Mathf.Abs(inThr - L.dto.throttle) > ThrottleMoveMax) L.throttleOffSteps++; else L.throttleOffSteps = 0;
                        if (L.throttleOffSteps >= ThrottleMoveSteps) { throttleRelease = true; L.throttleFree = true; }
                    }
                }

                if (L.phase == Phase.Forcing || L.phase == Phase.Probe)
                {
                    L.holdSteps++;
                    if (throttleRelease)
                    {
                        StartBlend(L, "ai-throttle", BlendTime);
                        LogHoldEnd(L, "-> BLEND " + N(L.blendLen) + "s (ai input moved > " + N(ThrottleMoveMax * 100f) + "% from saved)");
                    }
                    else if (L.t >= Timeout(L))
                    {
                        L.timedOut = true; L.skip = L.skip ?? "timeout";
                        if (L.player) NoSaveWait.Timeout("engine-hold " + L.label, Timeout(L), "blend " + N(BlendTime) + "s saved -> live, RESULT SKIP(timeout)");
                        StartBlend(L, "TIMEOUT", L.player ? BlendTime : BlendTimeTimeout);
                        LogHoldEnd(L, "-> BLEND " + N(L.blendLen) + "s");
                    }
                    else if (L.phase == Phase.Forcing)
                    {
                        if (--L.reforceLeft <= 0)
                        {
                            float sig = 0f;
                            foreach (var cv in L.comps)
                                for (int k = 0; k < cv.probeF.Count; k++)
                                {
                                    if (cv.excluded || cv.fN < 2) continue;
                                    float s = ThrottleMath.StdSigma(cv.fS[k], cv.fQ[k], cv.fN);
                                    if (s > sig) sig = s;
                                }
                            L.sigmaDelta = sig; L.epsStep = ThrottleMath.EpsStep(sig, ProbeEps, ProbeSigmaK);
                            L.sigmaReady = false; L.sigmaSrc = "forced(provisional)";
                            L.phase = Phase.Probe; L.probeRun = 0; L.cleanRun = 0; L.probes++;
                            L.winPos = 0; L.winN = 0; L.outlCount = 0; for (int i = 0; i < L.outl.Length; i++) L.outl[i] = false;
                            foreach (var cv in L.comps) { cv.maxRate = 0f; cv.maxRateIdx = -1; cv.sigN = 0; }
                        }
                    }
                    else
                    {
                        L.probeRun++;
                        bool any = false; float worst = 0f, dipMin = float.MaxValue; CompVals worstCv = null; string dipField = "-";
                        foreach (var cv in L.comps)
                        {
                            if (cv.excluded || cv.probeF.Count == 0 || !cv.everTicked) continue;
                            any = true;
                            if (cv.maxRate > worst) { worst = cv.maxRate; worstCv = cv; }
                            cv.maxRate = 0f;
                            for (int k = 0; k < cv.probeF.Count; k++)
                            {
                                float c = (cv.prev[k] - cv.probeSaved[k]) / cv.probeDen[k];
                                cv.hist[k][L.winPos] = c;
                                if (c < dipMin) { dipMin = c; dipField = cv.mb.GetType().Name + "." + cv.probeF[k].Name; }
                            }
                        }
                        if (!any)
                        {
                            if (L.probeRun >= ProbeSteps)
                            {
                                L.skip = "unmeasured";
                                StartBlend(L, "unmeasured", BlendTime);
                                LogHoldEnd(L, "-> no measurable engine part ticked; BLEND " + N(BlendTime) + "s");
                            }
                        }
                        else
                        {
                            if (!L.sigmaReady && L.probeRun >= SigmaSteps)
                            {
                                float sp = ProbeSigma(L, out bool found);
                                if (found) { L.sigmaProbe = sp; L.sigmaSrc = "probe"; L.epsStep = ThrottleMath.EpsStep(sp, ProbeEps, ProbeSigmaK); }
                                else { L.sigmaSrc = "forced"; L.epsStep = ThrottleMath.EpsStep(L.sigmaDelta, ProbeEps, ProbeSigmaK); }
                                L.sigmaReady = true;
                                if (L.player) Eng(L.label + " PROBE " + L.probes + " noise sigmaSrc=" + L.sigmaSrc + " sigma=" + E(found ? sp : L.sigmaDelta)
                                    + " (forced=" + E(L.sigmaDelta) + ") -> epsStep=" + E(L.epsStep) + " spikeAbort=" + E(SpikeK * L.epsStep));
                            }
                            bool outlier = L.sigmaReady && worst > L.epsStep;
                            if (L.winN == ConvergeSteps && L.outl[L.winPos]) L.outlCount--;
                            L.outl[L.winPos] = outlier; if (outlier) L.outlCount++;
                            L.winPos = (L.winPos + 1) % ConvergeSteps; if (L.winN < ConvergeSteps) L.winN++;
                            L.lastCum = dipMin;
                            ComputePassEps(L);
                            float cumAbort = Mathf.Min(CumAbortMax, 0.5f * L.passEps);
                            string fld = worstCv != null && worstCv.maxRateIdx >= 0 && worstCv.maxRateIdx < worstCv.probeF.Count
                                ? worstCv.mb.GetType().Name + "." + worstCv.probeF[worstCv.maxRateIdx].Name : "?";
                            string fail = null;
                            if (dipMin < -cumAbort) fail = "dip " + dipField + "=" + N(dipMin * 100f) + "% < -" + N(cumAbort * 100f) + "%";
                            else if (L.sigmaReady && worst > SpikeK * L.epsStep) fail = "spike " + fld + "@" + E(worst) + "/step > " + N(SpikeK) + "*eps " + E(L.epsStep);
                            else if (L.sigmaReady && L.outlCount > ProbeMaxOutliers) fail = "outliers " + L.outlCount + " > " + ProbeMaxOutliers + " (last " + fld + "@" + E(worst) + "/step > eps " + E(L.epsStep) + ")";
                            if (fail != null)
                            {
                                L.probeFails++; L.lastProbeFail = fail;
                                if (L.player) Eng(L.label + " PROBE " + L.probes + " abort after " + L.probeRun + " unforced steps: " + fail + " -> forcing again for " + ReforceSteps + " steps");
                                L.phase = Phase.Forcing; L.reforceLeft = ReforceSteps; L.cleanRun = 0;
                            }
                            else
                            {
                                L.cleanRun = L.probeRun; if (L.cleanRun > L.bestClean) L.bestClean = L.cleanRun;
                                if (L.measured != "none") { double r = Ratio(L); L.noiseSum += r; L.noiseSq += r * r; L.noiseN++; }
                                if (L.sigmaReady && L.winN >= ConvergeSteps)
                                {
                                    string sf; float minSlope, maxResid, winMin, minProj;
                                    WindowStats(L, out minSlope, out sf, out maxResid, out winMin, out minProj);
                                    float residMax = Mathf.Max(CumReleaseMax, SpikeK * L.epsStep);
                                    L.lastSlope = minSlope; L.lastProj = minProj;
                                    ComputePassEps(L);
                                    bool trendOk = minSlope >= -0.25f * L.epsStep || minProj >= -L.passEps;
                                    if (winMin >= -CumReleaseMax && trendOk && maxResid <= residMax && L.player)
                                    {
                                        ComputePassEps(L);
                                        StartBlend(L, "converged", BlendTime);
                                        LogHoldEnd(L, "-> BLEND " + N(BlendTime) + "s saved -> live throttle (src=" + L.liveSrc + " acc=" + N(L.lastAcc) + "); minSlope=" + E(minSlope) + "/step(" + sf + ") proj3s=" + N(minProj * 100f) + "% windowMin=" + N(winMin * 100f)
                                            + "% resid=" + E(maxResid) + "<=" + E(residMax) + " outliers=" + L.outlCount + " passEps=" + N(L.passEps * 100f) + "% sigma=" + E(L.sigma) + " n=" + L.noiseN);
                                    }
                                    else if (winMin >= -CumReleaseMax && trendOk && maxResid <= residMax)
                                    {
                                        L.endReason = "converged"; L.holdEnd = L.t; L.blendStart = L.t; L.blendLen = 0f;
                                        L.released = true; L.releasedAt = L.t; OnReleased(L);
                                        ComputePassEps(L);
                                        LogHoldEnd(L, "-> released (already unforced: blend=0) minSlope=" + E(minSlope) + "/step(" + sf + ") windowMin=" + N(winMin * 100f)
                                            + "% resid=" + E(maxResid) + "<=" + E(residMax) + " outliers=" + L.outlCount + " passEps=" + N(L.passEps * 100f) + "% sigma=" + E(L.sigma) + " n=" + L.noiseN);
                                        if (!L.player) { Finish(L); yield break; }
                                        if (L.legacy) L.skip = "legacy";
                                        else if (L.measured == "none") L.skip = "no-reference";
                                        if (L.skip != null) { Result(L); Finish(L); yield break; }
                                        L.phase = Phase.Monitor;
                                        Eng(L.label + " RELEASED t=" + N(L.t) + " -> sim owns the engine; monitoring " + N(MonitorWindow) + "s on " + L.measured
                                            + " ratio, FAIL if < " + N(1f - L.passEps) + " for >= " + DipFailSteps + " steps");
                                    }
                                }
                            }
                        }
                    }
                }
                else if (L.phase == Phase.Blending)
                {
                    if (L.t - L.blendStart >= L.blendLen)
                    {
                        L.released = true; L.releasedAt = L.t; OnReleased(L);
                        ComputePassEps(L);
                        if (L.player)
                            Eng(L.label + " RELEASED t=" + N(L.t) + " blend=" + N(L.t - L.blendStart) + "s blendWrites=" + L.blendWrites
                                + (L.skip != null ? " -> sim owns the engine; PASS/FAIL skipped (" + L.skip + ")" : ""));
                        if (!L.player) { Finish(L); yield break; }
                        if (L.skip == null && ThrottleMath.IsThrottleLower(LiveThrottle(L), L.dto.throttle, ThrottleSlack)) L.skip = "throttle-lower";
                        if (L.skip == null && L.legacy) L.skip = "legacy";
                        if (L.skip == null && L.measured == "none") L.skip = "no-reference";
                        if (L.skip != null) { Result(L); Finish(L); yield break; }
                        L.phase = Phase.Monitor;
                    }
                }
                else
                {
                    L.monitorSteps++;
                    float reff = L.dto.throttle;
                    float rt = L.t - L.releasedAt;
                    if (ThrottleMath.IsThrottleLower(inThr, reff, ThrottleSlack))
                    {
                        L.skip = "throttle-lower";
                        Eng(L.label + " MONITOR t=+" + N(rt) + "s live throttle " + N(inThr) + " < saved " + N(reff) + " - " + N(ThrottleSlack) + " (src=" + L.liveSrc + ") -> SKIP(throttle-lower)");
                        Result(L); Finish(L); yield break;
                    }
                    if (!float.IsNaN(L.prevMonThr) && inThr - L.prevMonThr > RiseEps) L.riseSkip = RiseSkipSteps;
                    L.prevMonThr = inThr;
                    if (L.riseSkip > 0) { L.riseSkip--; L.riseSkipped++; }
                    else
                    {
                        float r = Ratio(L);
                        L.samplesChecked++;
                        if (r < L.minRatio) { L.minRatio = r; L.minAt = rt; }
                        float lim = 1f - L.passEps;
                        if (1f - r > L.dipMax) L.dipMax = 1f - r;
                        if (ThrottleMath.DipStep(r, L.passEps, ref L.dipRun, DipFailSteps, ref L.dipFailed))
                        {
                            L.dipArea += (lim - r) * Time.fixedDeltaTime;
                            if (L.dipRun > L.maxDipRun) L.maxDipRun = L.dipRun;
                        }
                    }
                    if (rt >= MonitorWindow) { Result(L); Finish(L); yield break; }
                }

                if (L.player)
                {
                    WatchPlayerState(L);
                    if (L.t >= L.nextSample)
                    {
                        L.nextSample += SampleEvery; L.sampled.Clear(); L.sampleWriteDue = !L.released && L.phase != Phase.Probe;
                        if (L.t - lastLogged > 0.2f || lastLogged < 0f)
                        {
                            lastLogged = L.t;
                            string ph = L.phase == Phase.Forcing ? "HOLD reforce=" + L.reforceLeft
                                : L.phase == Phase.Probe ? "PROBE#" + L.probes + " window=" + L.winN + "/" + ConvergeSteps + " outliers=" + L.outlCount + " dipMin=" + (float.IsNaN(L.lastCum) ? "n/a" : N(L.lastCum * 100f) + "%") + " sigmaSrc=" + L.sigmaSrc
                                : L.phase == Phase.Blending ? "BLEND w=" + N(BlendWeight(L)) : "SIM +" + N(L.t - L.releasedAt) + "s";
                            Eng(L.label + " t=" + N(L.t) + " phase=" + ph + " " + PlayerLine(L, inThr));
                        }
                    }
                }
            }
        }

        private static float ProbeSigma(Lock L, out bool found)
        {
            found = false; float worst = 0f;
            foreach (var cv in L.comps)
            {
                if (cv.excluded || cv.probeF.Count == 0 || cv.sigN < 5) continue;
                int n = Math.Min(cv.sigN, SigmaSteps);
                for (int k = 0; k < cv.probeF.Count; k++)
                {
                    float s = ThrottleMath.MadSigma(cv.sig[k], n, _sigBuf);
                    if (float.IsNaN(s)) continue;
                    found = true;
                    if (s > worst) worst = s;
                }
            }
            return worst;
        }
        private static readonly float[] _sigBuf = new float[SigmaSteps];

        private static void WindowStats(Lock L, out float minSlope, out string field, out float maxResid, out float winMin, out float minProj)
        {
            minSlope = float.MaxValue; field = "-"; maxResid = 0f; winMin = float.MaxValue; minProj = float.MaxValue;
            float steps3 = 3f / Mathf.Max(Time.fixedDeltaTime, 1e-3f);
            int n = ConvergeSteps; float xm = (n - 1) * 0.5f, sxx = 0f;
            for (int i = 0; i < n; i++) sxx += (i - xm) * (i - xm);
            foreach (var cv in L.comps)
            {
                if (cv.excluded || cv.probeF.Count == 0 || !cv.everTicked) continue;
                for (int k = 0; k < cv.probeF.Count; k++)
                {
                    var h = cv.hist[k];
                    float ym = 0f;
                    for (int i = 0; i < n; i++) ym += h[(L.winPos + i) % n];
                    ym /= n;
                    float sxy = 0f;
                    for (int i = 0; i < n; i++) sxy += (i - xm) * (h[(L.winPos + i) % n] - ym);
                    float s = sxy / sxx;
                    if (float.IsNaN(s)) s = -1f;
                    if (s < minSlope) { minSlope = s; field = cv.mb.GetType().Name + "." + cv.probeF[k].Name; }
                    float proj = (ym + s * ((n - 1) - xm)) + s * steps3;
                    if (proj < minProj) minProj = proj;
                    for (int i = 0; i < n; i++)
                    {
                        float y = h[(L.winPos + i) % n];
                        float r = Mathf.Abs(y - (ym + s * (i - xm)));
                        if (r > maxResid) maxResid = r;
                        if (y < winMin) winMin = y;
                    }
                }
            }
            if (minSlope == float.MaxValue) minSlope = 0f;
            if (winMin == float.MaxValue) winMin = 0f;
            if (minProj == float.MaxValue) minProj = 0f;
        }

        internal static float LastPassEps = PassEpsMin;

        private static bool DeadCap(Lock L, string where)
        {
            if (L == null || !L.deadOnly || L.done) return false;
            float age = Time.time - L.armGameTime;
            if (age < DeadHoldMin + DeadHardCapExtra) return false;
            L.endReason = "destroyed-cap"; L.skip = "destroyed";
            if (L.holdEnd < 0f) L.holdEnd = L.t;
            if (L.blendStart < 0f) L.blendStart = L.t;
            L.released = true; L.releasedAt = L.t;
            if (L.player) Eng(L.label + " HOLD END reason=destroyed-cap where=" + where + " gameTime=" + N(age) + "s cap=" + N(DeadHoldMin + DeadHardCapExtra) + "s t=" + N(L.t) + " (dead-only lock force-finished)");
            OnReleased(L); Result(L); Finish(L);
            return true;
        }

        private static void OnReleased(Lock L)
        {
            if (!L.player || L.followStarted) return;
            L.followStarted = true;
            try { NoSaveThrottleFollow.StartOnce(L.ac, L.label, L.gen, "release(" + (L.endReason ?? "?") + ")"); } catch (Exception ex) { Log("follow start FAIL " + ex.Message); }
        }

        private static void ComputePassEps(Lock L)
        {
            if (L.noiseN >= 2) L.sigma = ThrottleMath.StdSigma(L.noiseSum, L.noiseSq, L.noiseN);
            L.passEps = ThrottleMath.PassEps(L.sigma, PassEpsMin);
            if (L.player) LastPassEps = L.passEps;
        }

        private static int CompDamaged(CompVals cv)
        {
            var mb = cv.mb;
            if (mb == null) return 2;
            var t = mb.GetType();
            try { var f = Fi(t, "inoperable"); if (f != null && f.FieldType == typeof(bool) && (bool)f.GetValue(mb)) return 2; } catch { }
            for (int i = 0; i < PartFieldNames.Length; i++)
            {
                try
                {
                    var f = Fi(t, PartFieldNames[i]);
                    if (f != null && f.GetValue(mb) is UnitPart up && up != null && (up.IsDetached() || up.hitPoints <= 0f)) return 2;
                }
                catch { }
            }
            try { var f = Fi(t, "operable"); if (f != null && f.FieldType == typeof(bool) && !(bool)f.GetValue(mb)) return 1; } catch { }
            return 0;
        }

        private static readonly string[] DeadZeroFields = { "startedAmount", "currentRPM", "RPM", "rpm", "RPMRatio", "currentPower", "powerRatio", "currentThrust", "thrust", "condition" };

        private static string CompLabel(Lock L, CompVals cv)
        {
            int i = L.comps.IndexOf(cv);
            return cv.mb != null ? cv.mb.GetType().Name + "#" + i : "engine#" + i;
        }

        private static bool OperableNow(CompVals cv)
        {
            var mb = cv.mb; if (mb == null) return false; var t = mb.GetType();
            try { var f = Fi(t, "inoperable"); if (f != null && f.FieldType == typeof(bool)) return !(bool)f.GetValue(mb); } catch { }
            try { var f = Fi(t, "operable"); if (f != null && f.FieldType == typeof(bool)) return (bool)f.GetValue(mb); } catch { }
            return true;
        }

        private static string KillDead(CompVals cv)
        {
            var mb = cv.mb; if (mb == null) return "gone";
            if (!OperableNow(cv)) return "already-dead";
            var t = mb.GetType(); string how = null;
            try
            {
                var m = AccessTools.Method(t, "KillEngine", Type.EmptyTypes);
                if (m != null) { m.Invoke(mb, null); how = "KillEngine"; }
                else if ((m = AccessTools.Method(t, "DuctedFan_OnDamage")) != null)
                {
                    bool det = SavedPartDetached(cv) == true;
                    var pt = m.GetParameters()[0].ParameterType; object e = Activator.CreateInstance(pt);
                    try { var fd = AccessTools.Field(pt, "detached"); if (fd != null) fd.SetValue(e, det); } catch { }
                    try { var fh = AccessTools.Field(pt, "hitPoints"); if (fh != null) fh.SetValue(e, 0f); } catch { }
                    m.Invoke(mb, new[] { e }); how = "DuctedFan_OnDamage(detached=" + (det ? "1" : "0") + ",hp=0)";
                }
                else if ((m = AccessTools.Method(t, "Prop_OnAttachmentBreak")) != null)
                {
                    bool? det = SavedPartDetached(cv);
                    if (det == true)
                    {
                        object up = null; try { up = Fi(t, "unitPart")?.GetValue(mb); } catch { }
                        m.Invoke(mb, new[] { up }); how = "Prop_OnAttachmentBreak(saved part detached)";
                    }
                    else how = "Prop_OnAttachmentBreak skipped (saved part " + (det == false ? "attached" : "unknown") + ")";
                }
            }
            catch (Exception ex) { how = (how ?? "kill") + " threw " + (ex.InnerException ?? ex).GetType().Name; }
            if (OperableNow(cv))
            {
                try { var f = Fi(t, "operable"); if (f != null && f.FieldType == typeof(bool)) f.SetValue(mb, false); } catch { }
                try { var f = Fi(t, "inoperable"); if (f != null && f.FieldType == typeof(bool)) f.SetValue(mb, true); } catch { }
                how = (how ?? "no game kill path") + " -> flag set";
            }
            return how ?? "flag";
        }

        private static bool? SavedPartDetached(CompVals cv)
        {
            try
            {
                var mb = cv.mb; var parts = cv.L != null && cv.L.dto != null ? cv.L.dto.parts : null;
                if (mb == null || parts == null) return null;
                UnitPart up = null;
                try { up = Fi(mb.GetType(), "unitPart")?.GetValue(mb) as UnitPart; } catch { }
                if (up == null) up = mb.GetComponentInParent<UnitPart>();
                if (up == null) return null;
                byte id = up.id;
                for (int i = 0; i < parts.Count; i++) if (parts[i] != null && parts[i].id == id) return parts[i].detached;
            }
            catch { }
            return null;
        }

        private static void DeadWrite(CompVals cv)
        {
            var mb = cv.mb; if (mb == null) return; var t = mb.GetType();
            if (cv.killTries < 5 && OperableNow(cv)) { cv.killTries++; KillDead(cv); }
            for (int i = 0; i < DeadZeroFields.Length; i++)
            {
                var f = Fi(t, DeadZeroFields[i]);
                if (f == null || f.FieldType != typeof(float)) continue;
                try { f.SetValue(mb, 0f); } catch { }
            }
        }

        private static int ArmDead(Lock L, string source)
        {
            int n = 0;
            foreach (var cv in L.comps)
            {
                if (cv.isTransmission || !cv.savedDamaged) continue;
                n++; cv.dead = true; cv.excluded = true; cv.exclReason = "destroyed";
                cv.killTries = 1;
                string how = KillDead(cv);
                DeadWrite(cv);
                L.savedPower = Mathf.Max(0f, L.savedPower - Mathf.Max(cv.savedPow, 0f));
                L.savedRpm = Mathf.Max(0f, L.savedRpm - cv.savedRpmC);
                EngArm(L.player, L.label + " ENGINE " + CompLabel(L, cv) + " destroyed at save -> restored dead from frame 0 (" + how + "; startedAmount/RPM/power/thrust=0, forced dead for the lock) RESULT SKIP(destroyed) [this engine only] source=" + source);
            }
            if (n > 0)
            {
                var eng = new List<MonoBehaviour>();
                foreach (var cv in L.comps) if (!cv.isTransmission && !cv.excluded) eng.Add(cv.mb);
                L.engines = eng.ToArray();
                if (L.measured == "power" && L.savedPower <= 1f) L.measured = L.savedRpm > 1f ? "rpm" : "none";
            }
            return n;
        }

        private static void ExcludeComp(Lock L, CompVals cv, string reason)
        {
            if (cv.excluded) return;
            cv.excluded = true; cv.exclReason = reason;
            if (cv.mb != null) _byComp.Remove(cv.mb);
            var eng = new List<MonoBehaviour>();
            foreach (var c in L.comps) if (!c.isTransmission && !c.excluded) eng.Add(c.mb);
            L.engines = eng.ToArray();
            L.savedPower = Mathf.Max(0f, L.savedPower - Mathf.Max(cv.savedPow, 0f));
            L.savedRpm = Mathf.Max(0f, L.savedRpm - cv.savedRpmC);
            if (L.measured == "power" && L.savedPower <= 1f) L.measured = L.savedRpm > 1f ? "rpm" : "none";
            L.noiseSum = 0; L.noiseSq = 0; L.noiseN = 0;
            if (L.player) Eng(L.label + " ENGINE " + CompLabel(L, cv) + " HOLD END reason=damaged t=" + N(L.t) + " phase=" + L.phase
                + " (this engine only, released immediately, no blend; " + L.engines.Length + " healthy engine(s) keep the hold) RESULT SKIP(damaged) [this engine only]");
        }

        private static string HealthIssue(Lock L, bool atArm)
        {
            int engines = 0, healthy = 0;
            foreach (var cv in L.comps)
            {
                if (cv.isTransmission) continue;
                engines++;
                if (cv.excluded) continue;
                if (!atArm)
                {
                    int d = CompDamaged(cv);
                    cv.softDmgRun = d == 1 ? cv.softDmgRun + 1 : 0;
                    if (d == 2 || cv.softDmgRun >= SoftDamageSteps) { ExcludeComp(L, cv, "damaged-during-hold"); continue; }
                }
                healthy++;
            }
            if (!atArm && engines > 0 && healthy == 0) return "damaged";
            if (atArm)
            {
                try { if (L.dto.fuelCaptured && L.dto.fuel <= 0f) return "nofuel"; } catch { }
                if (L.dto.ignition) foreach (var cv in L.comps) if (cv.savedNoFuel && !cv.isTransmission && !cv.dead) return "nofuel";
                return null;
            }
            bool empty = false;
            try { empty = L.ac.GetFuelLevel() <= 0f && L.ac.fuelLevel <= 0f; } catch { }
            L.noFuelRun = empty ? L.noFuelRun + 1 : 0;
            return L.noFuelRun >= NoFuelSteps ? "nofuel" : null;
        }

        private static void WatchPlayerState(Lock L)
        {
            try
            {
                var p = NoSavePilotFix.GetPilot(L.ac);
                var st = p != null ? p.currentState : null;
                if (st is PilotPlayerState ps)
                {
                    if (L.psRef == null) { L.psRef = ps; return; }
                    if (!ReferenceEquals(L.psRef, ps) && !L.simReapplied && !L.released)
                    {
                        L.psRef = ps; L.simReapplied = true;
                        NoSavePilotFix.ApplyPlayerThrottle(L.ac, L.dto, ps, L.label + " (player state re-entered at t=" + N(L.t) + ")");
                    }
                }
            }
            catch { }
        }

        private static string PlayerLine(Lock L, float inThr)
        {
            var sb = new StringBuilder();
            sb.Append("throttleIn=").Append(N(inThr)).Append(" src=").Append(L.liveSrc).Append(" acc=").Append(N(L.lastAcc));
            try
            {
                var p = NoSavePilotFix.GetPilot(L.ac);
                if (p?.currentState is PilotPlayerState ps) sb.Append(" simThrottle=").Append(N(NoSavePilotFix.ReadSimThrottle(ps))).Append(" state=PilotPlayerState");
                else sb.Append(" state=").Append(p?.currentState?.GetType().Name ?? "null");
            }
            catch { }
            sb.Append(" power=").Append(N(SumPower(L.engines))).Append("/").Append(N(L.savedPower));
            sb.Append(" thrust=").Append(N(SumThrust(L.engines))).Append("/").Append(N(L.savedThrust));
            if (L.measured != "none") sb.Append(" ratio(").Append(L.measured).Append(")=").Append(N(Ratio(L)));
            try { sb.Append(" ign=").Append(L.ac.Ignition ? 1 : 0).Append(" fuel=").Append(N(L.ac.fuelLevel)); } catch { }
            foreach (var cv in L.comps) sb.Append(" | ").Append(cv.mb.GetType().Name).Append(": ").Append(Snapshot(cv.mb));
            return sb.ToString();
        }

        private static void Result(Lock L)
        {
            if (!L.player) return;
            string hold = " | endReason=" + (L.endReason ?? "?") + " holdDuration=" + N(Mathf.Max(L.holdEnd, 0f)) + "s holdSteps=" + L.holdSteps
                + " probes=" + L.probes + " probeFails=" + L.probeFails + " window=" + L.winN + "/" + ConvergeSteps + " outliers=" + L.outlCount
                + " epsStep=" + E(L.epsStep) + " sigmaSrc=" + L.sigmaSrc + " minSlope=" + (float.IsNaN(L.lastSlope) ? "n/a" : E(L.lastSlope))
                + " dipMin=" + (float.IsNaN(L.lastCum) ? "n/a" : N(L.lastCum * 100f) + "%") + " lastProbeFail=" + L.lastProbeFail
                + " timeout=" + (L.timedOut ? "yes" : "no") + " blend=" + N(Mathf.Max(L.releasedAt - L.blendStart, 0f)) + "s"
                + " forcedWrites=" + L.forcedWrites + " blendWrites=" + L.blendWrites
                + " throttle baseline=" + (L.baselineSet ? N(L.baseline) : "n/a") + " saved=" + N(L.dto.throttle);
            if (L.skip != null)
            {
                Eng(L.label + " RESULT SKIP(" + L.skip + ")" + hold);
                return;
            }
            string verdict = L.samplesChecked == 0 ? "PASS(no-check: no dip-checkable step in the window)" : (L.dipFailed ? "FAIL" : "PASS");
            Eng(L.label + " RESULT " + verdict + " measured=" + L.measured + " ratio window=release+" + N(MonitorWindow) + "s"
                + " minRatio=" + N(L.minRatio) + "@+" + N(L.minAt) + "s limit=" + N(1f - L.passEps) + " passEps=" + N(L.passEps * 100f) + "% sigma=" + E(L.sigma) + " n=" + L.noiseN
                + " dipMax=" + N(L.dipMax * 100f) + "% dipArea=" + E(L.dipArea) + " maxDipRun=" + L.maxDipRun + " failRun=" + DipFailSteps
                + " checkedSteps=" + L.samplesChecked + "/" + L.monitorSteps + " riseSkipped=" + L.riseSkipped + " savedPower=" + N(L.savedPower) + " savedRpm=" + N(L.savedRpm) + hold);
        }

        private static void Finish(Lock L)
        {
            if (L.player && !L.followStarted && Alive(L.ac)) OnReleased(L);
            L.done = true; L.released = true;
            foreach (var cv in L.comps) if (cv.mb != null) _byComp.Remove(cv.mb);
            _byAc.Remove(L.ac);
            if (L.ac != null) _expired.Add(L.ac);
            Prune();
        }

        internal static void OnResume(Aircraft ac, string source)
        {
            string lbl = SafeName(ac);
            if (_byAc.TryGetValue(ac, out var L) && !L.released) { if (L.player) Eng(lbl + " resume (" + source + ") inside hold phase=" + L.phase + " t=" + N(L.t) + " - no extra write needed"); }
            else if (_byAc.ContainsKey(ac) || _expired.Contains(ac)) Eng("LATE-WRITE blocked source=" + source + " aircraft=" + lbl + " (resume outside the post-load window: no engine write)");
        }

        internal static void Hold(Aircraft ac, SavedAircraftDto dto, string label, float holdSeconds) { ApplyEngines(ac, dto, label, "hold"); }

        private static readonly Dictionary<Aircraft, LandingGear.GearState> _gearResumed = new Dictionary<Aircraft, LandingGear.GearState>();

        private static string CaptureAnim(Aircraft ac)
        {
            var sb = new StringBuilder();
            var gears = ac.GetComponentsInChildren<LandingGear>(true);
            for (int i = 0; i < gears.Length; i++)
            {
                var lg = gears[i]; var hinge = Get<Transform>(lg, "gearHinge", null);
                if (hinge == null || hinge.parent == null) continue;
                float angle = Vector3.Angle(hinge.forward, hinge.parent.forward);
                if (sb.Length > 0) sb.Append(';');
                sb.Append("G,").Append(i).Append(',').Append(angle.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(DoorAmount(lg).ToString("R", CultureInfo.InvariantCulture));
            }
            var cs = ac.GetComponentsInChildren<ControlSurface>(true);
            for (int i = 0; i < cs.Length; i++)
            {
                if (!Get(cs[i], "flap", false)) continue;
                float p;
                if (!TryFlap(cs[i], false, 0f, out p)) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append("F,").Append(i).Append(',').Append(p.ToString("R", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static float DoorAmount(LandingGear lg)
        {
            var doors = Get<IList>(lg, "gearDoors", null);
            if (doors == null) return -1f;
            foreach (var d in doors)
            {
                if (!(d is LandingGear.GearDoor gd) || gd.transform == null) continue;
                var c = Quaternion.Euler(gd.closedAngle); var o = Quaternion.Euler(gd.openAngle);
                float total = Quaternion.Angle(c, o);
                if (total < 0.01f) continue;
                return Mathf.Clamp01(Quaternion.Angle(c, gd.transform.localRotation) / total);
            }
            return -1f;
        }

        private static bool TryFlap(ControlSurface cs, bool write, float value, out float read)
        {
            read = 0f;
            var f = Fi(typeof(ControlSurface), "JobFields"); if (f == null) return false;
            var boxed = f.GetValue(cs);
            if (!(boxed is PtrAllocation<ControlSurfaceFields> pa) || !pa.IsCreated) return false;
            if (Ptr<ControlSurfaceFields>.JobRunningSafety) return false;
            ref ControlSurfaceFields r = ref pa.Ref();
            if (write) r.currentPitch = value;
            read = r.currentPitch;
            return true;
        }

        private static Dictionary<string, float[]> ParseAnim(string s)
        {
            var d = new Dictionary<string, float[]>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(s)) return d;
            foreach (var e in s.Split(';'))
            {
                var p = e.Split(','); if (p.Length < 3) continue;
                var vals = new float[p.Length - 2];
                bool ok = true;
                for (int i = 2; i < p.Length; i++) ok &= float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out vals[i - 2]);
                if (ok) d[p[0] + "," + p[1]] = vals;
            }
            return d;
        }

        internal static void ApplyGear(Aircraft ac, SavedAircraftDto dto, string label)
        {
            if (ac == null || dto == null) return;
            string lbl = label ?? SafeName(ac);
            var before = ac.gearState;
            if (_gearResumed.TryGetValue(ac, out var est))
            {
                bool same = before == est;
                if (!same && (est == LandingGear.GearState.Retracting && before == LandingGear.GearState.LockedRetracted)) same = true;
                if (!same && (est == LandingGear.GearState.Extending && before == LandingGear.GearState.LockedExtended)) same = true;
                if (same) { if (label != null) Log(lbl + " gear check: still " + before + " as restored - not touched"); return; }
                Log(lbl + " gear check: game changed " + est + " -> " + before + " after restore - re-applying saved state");
                _gearResumed.Remove(ac);
            }
            var anim = ParseAnim(dto.animState);
            bool transitional = dto.gearState == (int)LandingGear.GearState.Retracting || dto.gearState == (int)LandingGear.GearState.Extending;
            if (transitional)
            {
                bool extending = dto.gearState == (int)LandingGear.GearState.Extending;
                bool haveAnim = false; foreach (var k in anim.Keys) if (k.StartsWith("G,", StringComparison.Ordinal)) { haveAnim = true; break; }
                var start = extending ? LandingGear.GearState.LockedRetracted : LandingGear.GearState.LockedExtended;
                if (ac.gearState != start) ac.SetGear(start);
                if (!haveAnim)
                {
                    try { ac.NetworkgearDeployed = extending; } catch { }
                    ac.SetGear((LandingGear.GearState)dto.gearState);
                    Log(lbl + " gear " + before + " -> " + ac.gearState + " (saved=" + dto.gearState + ", old save without progress: native transition restarted from " + start + ")");
                }
                else
                {
                    ac.gearState = (LandingGear.GearState)dto.gearState;
                    try { ac.NetworkgearDeployed = extending; } catch { }
                    var gears = ac.GetComponentsInChildren<LandingGear>(true);
                    int started = 0;
                    for (int i = 0; i < gears.Length; i++)
                    {
                        try { if (gears[i] != null) gears[i].enabled = true; } catch { }
                        if (!anim.TryGetValue("G," + i, out var v)) continue;
                        float door = v.Length > 1 ? v[1] : -1f;
                        if (NOSaveSystemPlugin.Instance != null)
                        {
                            NOSaveSystemPlugin.Instance.StartCoroutine(NoSaveCoroutines.Safe(GearMoveCo(ac, gears[i], v[0], door, extending, lbl + " gear#" + i), "GearContinue"));
                            started++;
                        }
                    }
                    Log(lbl + " gear " + before + " -> " + ac.gearState + " (saved=" + dto.gearState + ") continuing from saved progress on " + started + " leg(s)");
                }
            }
            else
            {
                bool down;
                if (dto.gearState == (int)LandingGear.GearState.LockedRetracted) down = false;
                else if (dto.gearState == (int)LandingGear.GearState.LockedExtended) down = true;
                else down = dto.gearDeployed;
                var target = down ? LandingGear.GearState.LockedExtended : LandingGear.GearState.LockedRetracted;
                if (before != target) ac.SetGear(target);
                try { ac.NetworkgearDeployed = down; } catch { }
                if (label != null) Log(lbl + " gear " + before + " -> " + ac.gearState + " (saved=" + dto.gearState + " locked state, deployed=" + down + ")");
            }
            _gearResumed[ac] = ac.gearState;
            ApplyFlaps(ac, anim, lbl);
        }

        private static void ApplyFlaps(Aircraft ac, Dictionary<string, float[]> anim, string lbl)
        {
            bool any = false; foreach (var k in anim.Keys) if (k.StartsWith("F,", StringComparison.Ordinal)) { any = true; break; }
            if (!any) return;
            if (NOSaveSystemPlugin.Instance != null)
                NOSaveSystemPlugin.Instance.StartCoroutine(NoSaveCoroutines.Safe(FlapCo(ac, anim, lbl), "FlapRestore"));
        }

        private static IEnumerator FlapCo(Aircraft ac, Dictionary<string, float[]> anim, string lbl)
        {
            NoSaveVerify.BusyEnter();
            try { yield return FlapCoBody(ac, anim, lbl); }
            finally { NoSaveVerify.BusyExit(); }
        }

        private static IEnumerator FlapCoBody(Aircraft ac, Dictionary<string, float[]> anim, string lbl)
        {
            float waited = 0f;
            while (true)
            {
                if (!Alive(ac)) yield break;
                var cs = ac.GetComponentsInChildren<ControlSurface>(true);
                int ok = 0, want = 0; var sb = new StringBuilder();
                for (int i = 0; i < cs.Length; i++)
                {
                    if (!anim.TryGetValue("F," + i, out var v)) continue;
                    want++;
                    if (TryFlap(cs[i], true, v[0], out var now)) { ok++; sb.Append(" #").Append(i).Append('=').Append(N(now)); }
                }
                if (ok == want || waited > 2f)
                {
                    if (ok < want) NoSaveWait.Timeout("flap-restore " + lbl, 2f, "flaps left at the game's own position on " + (want - ok) + " surface(s)");
                    Log(lbl + " flaps restored " + ok + "/" + want + sb + " (servo continues toward the gear-driven target from here)");
                    yield break;
                }
                waited += NoSaveWait.Dt();
                yield return null;
            }
        }

        private static IEnumerator GearMoveCo(Aircraft ac, LandingGear lg, float startAngle, float door, bool extending, string lbl)
        {
            NoSaveVerify.BusyEnter();
            try { yield return GearMoveCoBody(ac, lg, startAngle, door, extending, lbl); }
            finally { NoSaveVerify.BusyExit(); }
        }

        private static IEnumerator GearMoveCoBody(Aircraft ac, LandingGear lg, float startAngle, float door, bool extending, string lbl)
        {
            var hinge = Get<Transform>(lg, "gearHinge", null);
            var strut = Get<Transform>(lg, "strutRotationTransform", null);
            var part = Get<AeroPart>(lg, "attachedPart", null);
            var doors = Get<IList>(lg, "gearDoors", null);
            Vector3 baseAng = Get(lg, "hingeBaseAngles", Vector3.zero), basePos = Get(lg, "hingeBasePos", Vector3.zero), foldMotion = Get(lg, "hingeFoldMotion", Vector3.zero);
            float foldDeg = Get(lg, "foldDegrees", 90f), foldSpeed = Get(lg, "foldSpeed", 45f), strutRot = Get(lg, "strutRotation", 0f);
            float extDrag = Get(lg, "extendedDrag", 0f), retDrag = Get(lg, "retractedDrag", 0f);
            var foldField = Fi(typeof(LandingGear), "foldAmount");
            var ump = typeof(LandingGear).GetMethod("UpdateMovingParts", BF);
            if (hinge == null || Mathf.Abs(foldDeg) < 0.001f) { Log(lbl + " gear continue skipped (no hinge)"); yield break; }
            float absFold = Mathf.Abs(foldDeg);
            var want = extending ? LandingGear.GearState.Extending : LandingGear.GearState.Retracting;
            float gearAngle = Mathf.Clamp(startAngle, 0f, absFold);
            bool doorsOpen; float moveTime;
            if (extending) { doorsOpen = gearAngle < absFold - 0.5f || door < 0f || door >= 0.999f; moveTime = doorsOpen ? 1f : Mathf.Max(door, 0f); }
            else { doorsOpen = true; moveTime = gearAngle >= absFold - 0.01f && door >= 0f ? 1f - door : 0f; }

            void Doors(float amt) { if (doors == null) return; foreach (var d in doors) (d as LandingGear.GearDoor)?.Animate(amt); }
            void Pose()
            {
                hinge.localEulerAngles = baseAng + new Vector3(gearAngle * Mathf.Sign(foldDeg), 0f, 0f);
                if (strut != null) strut.localEulerAngles = new Vector3(0f, Mathf.Abs(gearAngle / foldDeg) * strutRot, 0f);
                if (part != null) part.dragArea = Mathf.Lerp(extDrag, retDrag, gearAngle / absFold);
                float fa = Mathf.Abs(gearAngle / foldDeg);
                try { foldField?.SetValue(lg, fa); } catch { }
                hinge.localPosition = Vector3.Lerp(basePos, basePos + foldMotion, fa);
                try { ump?.Invoke(lg, null); } catch { }
            }

            Pose();
            Doors(extending ? (doorsOpen ? 1f : moveTime) : (moveTime > 0f ? 1f - moveTime : 1f));
            lg.enabled = true;
            Log(lbl + " continue " + want + " from angle=" + N(gearAngle) + "/" + N(absFold) + " door=" + N(door) + " doorsOpen=" + doorsOpen);
            float gearCap = 2f + absFold / Mathf.Max(Mathf.Abs(foldSpeed), 1f) + 3f, gearT = 0f;
            while (true)
            {
                yield return null;
                if (!Alive(ac) || lg == null || hinge == null) yield break;
                if (ac.gearState != want) yield break;
                float dt = Time.deltaTime;
                gearT += dt;
                if (gearT > gearCap)
                {
                    var fin = extending ? LandingGear.GearState.LockedExtended : LandingGear.GearState.LockedRetracted;
                    NoSaveWait.Timeout("gear-move " + lbl, gearCap, "SetGear(" + fin + ") native snap");
                    try { ac.SetGear(fin); } catch { }
                    yield break;
                }
                if (extending)
                {
                    if (!doorsOpen) { moveTime += dt; Doors(moveTime); if (moveTime > 1f) doorsOpen = true; }
                    else
                    {
                        gearAngle -= foldSpeed * dt;
                        if (gearAngle < 0f)
                        {
                            gearAngle = 0f; Pose();
                            if (part != null) part.dragArea = extDrag;
                            ac.SetGear(LandingGear.GearState.LockedExtended);
                            Log(lbl + " extend complete (LockedExtended)");
                            yield break;
                        }
                    }
                }
                else
                {
                    gearAngle += foldSpeed * dt;
                    if (gearAngle > absFold)
                    {
                        gearAngle = absFold;
                        hinge.localEulerAngles = baseAng + new Vector3(foldDeg, 0f, 0f);
                        if (strut != null) strut.localEulerAngles = new Vector3(0f, strutRot, 0f);
                        moveTime += dt;
                        bool latched = doors == null || doors.Count == 0;
                        if (doors != null)
                            foreach (var d in doors)
                            {
                                var gd = d as LandingGear.GearDoor; if (gd == null) continue;
                                gd.Animate(1f - moveTime);
                                if (moveTime > 1f) { latched = true; if (gd.transform != null) gd.transform.localEulerAngles = Vector3.zero; }
                            }
                        if (latched)
                        {
                            ac.SetGear(LandingGear.GearState.LockedRetracted);
                            if (part != null) part.dragArea = retDrag;
                            Log(lbl + " retract complete (LockedRetracted)");
                            yield break;
                        }
                        continue;
                    }
                }
                Pose();
            }
        }
    }

    [HarmonyPatch]
    internal static class Patch_EngineTick_V0102
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var seen = new HashSet<MethodBase>();
            var names = new List<string>();
            foreach (var n in NoSaveEngineGear.EngineTypes)
            {
                var t = AccessTools.TypeByName(n);
                if (t == null) { names.Add(n + ":missing"); continue; }
                foreach (var m in new[] { "Update", "FixedUpdate" })
                {
                    for (var cur = t; cur != null && cur != typeof(MonoBehaviour); cur = cur.BaseType)
                    {
                        var mi = AccessTools.DeclaredMethod(cur, m);
                        if (mi == null || mi.IsAbstract) continue;
                        if (seen.Add(mi)) names.Add(n + "." + m + (cur != t ? "(inherited from " + cur.Name + ")" : ""));
                        break;
                    }
                }
            }
            NOSaveSystemPlugin.Log?.LogInfo("NOSAVE-ENG tick patch: " + seen.Count + " methods [" + string.Join(", ", names.ToArray()) + "]");
            return seen;
        }

        static void Prefix(MonoBehaviour __instance)
        {
            if (!NoSaveEngineGear.AnyActive) return;
            try { NoSaveEngineGear.OnTick(__instance); } catch { }
        }

        static void Postfix(MonoBehaviour __instance, MethodBase __originalMethod)
        {
            if (!NoSaveEngineGear.AnyActive) return;
            try { NoSaveEngineGear.OnTickPost(__instance, __originalMethod != null ? __originalMethod.Name : "Update"); } catch { }
        }
    }
}
