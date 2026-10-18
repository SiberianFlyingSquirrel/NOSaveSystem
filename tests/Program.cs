using System;
using System.Collections.Generic;
using System.Globalization;
using NOSaveSystem;

namespace NOSaveSystem.Tests
{
    internal static class Program
    {
        private static int _pass, _fail, _skip;
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
        private static readonly int[] Fps = { 60, 144 };
        private static readonly (bool neg, bool inv)[] Modes = { (false, false), (true, false), (false, true), (true, true) };

        private static void Report(string name, string status, string detail)
        {
            if (status == "PASS") _pass++; else if (status == "FAIL") _fail++; else _skip++;
            Console.WriteLine("{0,-4}  {1,-58} {2}", status, name, detail);
        }
        private static void Check(string name, bool ok, string detail = "") => Report(name, ok ? "PASS" : "FAIL", detail);
        private static string F(float v) => v.ToString("0.######", Ic);
        private static string M((bool neg, bool inv) m) => "neg=" + (m.neg ? 1 : 0) + " inv=" + (m.inv ? 1 : 0);

        private static int Main()
        {
            Console.WriteLine("NOSaveSystem.Tests - ThrottleMath linked from plugin source");
            Console.WriteLine("--- invariants");
            HoldOutputIsSaved(); RoundTrip(); BlendShape(); BlendFloor(); ThrottleLower(); KeyboardRamp(); Seed();
            MadSigmaSafe(); ProbeHelpers(); Classifier();
            Console.WriteLine("--- synthetic GameAccStep sequences (hold 2 s, probe 0.5 s, blend 0.75 s, released 0.5 s)");
            foreach (int fps in Fps) foreach (var sc in Scenarios()) Run(sc, fps);
            Console.WriteLine("--- total: {0} PASS, {1} FAIL, {2} SKIP", _pass, _fail, _skip);
            return _fail == 0 ? 0 : 1;
        }

        private static void HoldOutputIsSaved()
        {
            int bad = 0, deleg = 0;
            for (int si = 0; si <= 20; si++)
                for (int li = -100; li <= 100; li++)
                {
                    float s = si / 20f, l = li / 100f;
                    if (TM.Hold(s, l, ThrottlePhase.Hold) != s || TM.Hold(s, l, ThrottlePhase.Probe) != s) bad++;
                    if (TM.Hold(s, l, ThrottlePhase.Released) != l || TM.Hold(s, l, ThrottlePhase.Blend, 0.3f) != TM.Blend(s, l, 0.3f)) deleg++;
                }
            Check("U1 HoldOutput(Hold|Probe)==saved, live -1..1", bad == 0, bad + " mismatches");
            Check("U1 HoldOutput(Blend)==Blend, (Released)==live", deleg == 0, deleg + " mismatches");
        }

        private static void RoundTrip()
        {
            foreach (var m in Modes)
            {
                float worst = 0f;
                for (int i = 0; i <= 1000; i++) { float t = i / 1000f; worst = Math.Max(worst, Math.Abs(TM.Map(TM.Inv(t, m.neg, m.inv), m.neg, m.inv) - t)); }
                Check("U2 round-trip " + M(m), worst <= 1e-5f, "maxErr=" + F(worst));
            }
        }

        private static IEnumerable<(float s, float l)> Pairs()
        {
            for (int a = 0; a <= 10; a++) for (int b = 0; b <= 10; b++) yield return (a / 10f, b / 10f);
            yield return (0.8f, 0.1f); yield return (0.4f, 1f); yield return (0.5f, 0.49f);
        }

        private static void BlendShape()
        {
            int endBad = 0;
            foreach (var (s, l) in Pairs())
                if (TM.Blend(s, l, 0f) != s || TM.Blend(s, l, -1f) != s || TM.Blend(s, l, 1f) != l || TM.Blend(s, l, 2f) != l) endBad++;
            Check("U3 Blend(s,l,<=0)==s and Blend(s,l,>=1)==l exactly", endBad == 0, endBad + " bad");
            foreach (int fps in Fps)
            {
                float dt = 1f / fps; int monoBad = 0; float peak = 0f;
                foreach (var (s, l) in Pairs())
                {
                    float prev = TM.Blend(s, l, 0f);
                    for (int k = 1; ; k++)
                    {
                        float t = TM.Progress(k * dt, TM.BlendTime);
                        float cur = TM.Blend(s, l, t);
                        if ((cur - prev) * Math.Sign(l - s) < -1e-7f) monoBad++;
                        if (Math.Abs(l - s) > 1e-6f) peak = Math.Max(peak, Math.Abs(cur - prev) / (Math.Abs(l - s) * dt / TM.BlendTime));
                        prev = cur;
                        if (t >= 1f) break;
                    }
                }
                Check("U3 Blend monotone in t @" + fps + "fps", monoBad == 0, monoBad + " reversals");
                Check("U3 max step <= 1.5*|l-s|*dt/0.75 @" + fps + "fps", peak <= 1.5f + 2e-3f,
                    "peak = " + F(peak) + " x |l-s|*dt/0.75 (linear bound k=1 " + (peak <= 1f + 1e-3f ? "met" : "exceeded, as expected for SmoothStep") + ")");
            }
        }

        private static void BlendFloor()
        {
            int bad = 0;
            foreach (var (s, l) in Pairs()) for (int k = 0; k <= 100; k++) if (TM.Blend(s, l, k / 100f) < Math.Min(s, l) - 1e-6f) bad++;
            Check("U4 Blend >= min(saved, live)", bad == 0, bad + " below");
        }

        private static void ThrottleLower()
        {
            int bad = 0;
            for (int si = 0; si <= 100; si++) for (int li = 0; li <= 100; li++)
                { float s = si / 100f, l = li / 100f; if (TM.Lower(l, s, TM.Slack) != (l < s - TM.Slack)) bad++; }
            bool edge = TM.Lower(0.47f, 0.5f, 0.02f) && !TM.Lower(0.5f, 0.5f, 0f) && !TM.Lower(0.49f, 0.5f, 0.02f);
            Check("U5 IsThrottleLower iff live < saved - slack", bad == 0 && edge, bad + " mismatches, edges " + (edge ? "ok" : "bad"));
        }

        private static void KeyboardRamp()
        {
            foreach (int fps in Fps)
                foreach (var m in Modes)
                {
                    float dt = 1f / fps, worstSeed = 0f, worstStep = 0f; bool clampOk = true;
                    for (int si = 0; si <= 20; si++)
                    {
                        float saved = si / 20f;
                        float acc = TM.Inv(saved, m.neg, m.inv), prevRaw = 0f;
                        acc = TM.Step(acc, 0f, prevRaw, false, false, dt);
                        worstSeed = Math.Max(worstSeed, Math.Abs(TM.Map(acc, m.neg, m.inv) - saved));
                        for (int i = 0; i < 3 * fps; i++)
                        {
                            float raw = i < fps ? 1f : i < fps + 10 ? 0f : -1f;
                            float a0 = acc; acc = TM.Step(acc, raw, prevRaw, false, false, dt); prevRaw = raw;
                            worstStep = Math.Max(worstStep, Math.Abs(acc - a0));
                            float thr = TM.Map(acc, m.neg, m.inv);
                            if (acc > 1f + 1e-6f || acc < -1f - 1e-6f || thr < 0f || thr > 1f) clampOk = false;
                        }
                    }
                    Check("U6 KB ramp first frame==saved " + M(m) + " @" + fps, worstSeed <= 1e-5f, "maxErr=" + F(worstSeed));
                    Check("U6 KB ramp |dacc|<=dt & clamped " + M(m) + " @" + fps, worstStep <= dt + 1e-6f && clampOk, "maxStep=" + F(worstStep) + " dt=" + F(dt) + (clampOk ? "" : " CLAMP VIOLATION"));
                }
        }

        private static void Seed()
        {
            foreach (var m in Modes)
            {
                float worst = 0f; int usedWrong = 0;
                for (int si = 0; si <= 100; si++)
                {
                    float s = si / 100f; bool used;
                    float a = TM.Seed(s, false, 0f, m.neg, m.inv, out used); worst = Math.Max(worst, Math.Abs(TM.Map(a, m.neg, m.inv) - s)); if (used) usedWrong++;
                    float good = TM.Inv(s, m.neg, m.inv) + 0.002f;
                    a = TM.Seed(s, true, good, m.neg, m.inv, out used); worst = Math.Max(worst, Math.Abs(TM.Map(a, m.neg, m.inv) - s));
                    a = TM.Seed(s, true, 5f, m.neg, m.inv, out used); worst = Math.Max(worst, Math.Abs(TM.Map(a, m.neg, m.inv) - s)); if (used && Math.Abs(TM.Map(5f, m.neg, m.inv) - s) > 0.005f) usedWrong++;
                }
                Check("U7 SeedAcc -> Map == saved (<=0.005) " + M(m), worst <= 0.005f && usedWrong == 0, "maxErr=" + F(worst) + " wrongSource=" + usedWrong);
            }
        }

        private static void MadSigmaSafe()
        {
            var sc = new float[16];
            bool nanNull = float.IsNaN(TM.MadSigma(null, 3, sc)) && float.IsNaN(TM.MadSigma(new float[3], 3, null));
            bool nanN0 = float.IsNaN(TM.MadSigma(new float[3], 0, sc)) && float.IsNaN(TM.MadSigma(new float[3], -1, sc));
            bool nanBig = float.IsNaN(TM.MadSigma(new float[3], 4, sc)) && float.IsNaN(TM.MadSigma(new float[8], 8, new float[4]));
            Check("U8 MadSigma NaN on null / n<1 / n>len / small scratch", nanNull && nanN0 && nanBig, "");
            float n1 = TM.MadSigma(new[] { 0.3f }, 1, sc);
            float n2 = TM.MadSigma(new[] { 0.3f, 0.5f }, 2, sc);
            Check("U8 MadSigma n=1 / n=2 finite, >= 0", !float.IsNaN(n1) && n1 >= 0f && !float.IsNaN(n2) && n2 >= 0f, "n1=" + F(n1) + " n2=" + F(n2));
            string withNan; try { withNan = F(TM.MadSigma(new[] { 0f, float.NaN, 1f, 2f }, 4, sc)); } catch (Exception e) { withNan = "THREW " + e.GetType().Name; }
            Check("U8 MadSigma with a NaN sample does not throw", !withNan.StartsWith("THREW"), "result=" + withNan);
            float g = TM.MadSigma(new[] { -2f, -1f, 0f, 1f, 2f }, 5, sc);
            Check("U8 MadSigma {-2..2} == 1.4826", Math.Abs(g - 1.4826f) < 1e-4f, "sigma=" + F(g));
            float q = TM.MadSigma(new[] { 0f, 0f, 0f, 0f, 0f, 1f, -1f }, 7, sc);
            Check("U8 MadSigma quantised (MAD=0) floored > 0", q > 0f, "sigma=" + F(q));
        }

        private static void ProbeHelpers()
        {
            Check("U9 EpsStep floor / k*sigma", TM.EpsStep(0f) == TM.ProbeEps && Math.Abs(TM.EpsStep(1e-3f) - 4e-3f) < 1e-7f, F(TM.EpsStep(0f)) + " / " + F(TM.EpsStep(1e-3f)));
            Check("U9 PassEps floor / 3*sigma", TM.PassEps(0f) == TM.PassMinEps && Math.Abs(TM.PassEps(0.01f) - 0.03f) < 1e-7f, F(TM.PassEps(0f)) + " / " + F(TM.PassEps(0.01f)));
            int run = 0; bool failed = false; float eps = 0.003f;
            bool d1 = TM.DipStep(0.99f, eps, ref run, 3, ref failed), d2 = TM.DipStep(0.99f, eps, ref run, 3, ref failed);
            bool ok1 = d1 && d2 && run == 2 && !failed;
            bool d3 = TM.DipStep(1.0f, eps, ref run, 3, ref failed); bool ok2 = !d3 && run == 0 && !failed;
            for (int i = 0; i < 3; i++) TM.DipStep(0.5f, eps, ref run, 3, ref failed);
            Check("U9 DipStep run / reset / fail at failSteps", ok1 && ok2 && failed && run == 3, "run=" + run + " failed=" + failed);
            float err; int r = TM.FollowStep(0.5f, 0.52f, 0.01f, 4, out err); int r0 = TM.FollowStep(0.5f, 0.505f, 0.01f, 4, out _);
            Check("U9 FollowStep counts / resets, error=|eng-exp|", r == 5 && r0 == 0 && Math.Abs(err - 0.02f) < 1e-6f, "run=" + r + "/" + r0 + " err=" + F(err));
            Check("U9 FollowFixAllowed raise-only", TM.FixAllowed(0.4f, 0.6f) && !TM.FixAllowed(0.6f, 0.4f) && !TM.FixAllowed(0.5f, 0.5f), "");
        }

        private static void Classifier()
        {
            var kb = new[] { 0f, 1f, 1f, 1f, 0f, -1f, -1f, 0f, 0f, 1f };
            var pad = new List<float>(); for (int i = 0; i <= 8; i++) pad.Add(i * 0.1f); for (int i = 8; i >= 0; i--) pad.Add(i * 0.1f);
            var axis = new List<float>(); for (int i = 0; i <= 6; i++) axis.Add(i * 0.1f); for (int i = 0; i < 10; i++) axis.Add(0.6f);
            var idle = new[] { 0f, 0f, 0f, 0f };
            string a = TM.Classify(kb, "Keyboard"), b = TM.Classify(pad.ToArray(), "Joystick"), c = TM.Classify(axis.ToArray(), "Keyboard");
            string d = TM.Classify(idle, "Joystick"), e = TM.Classify(idle, "Keyboard"), f = TM.Classify(idle, "Mouse");
            Check("U10 SrcClassifier {-1,0,1} -> kb", a == "kb", a);
            Check("U10 SrcClassifier fractional springing to 0 -> pad", b == "pad", b);
            Check("U10 SrcClassifier fractional resting off-centre -> axis", c == "axis", c);
            Check("U10 SrcClassifier idle -> axis(idle)/kb(idle)", d == "axis(idle)" && e == "kb(idle)" && f == "kb(idle)", d + " / " + e + " / " + f);
        }

        private sealed class Sc
        {
            public string Name; public float Saved; public bool Neg, Inv, Relative;
            public Func<float, float> Raw;
            public Func<float, bool> AxisMod = _ => false;
        }

        private static float Jit(float t, float amp) => amp * (float)Math.Sin(t * 97.0) * (float)Math.Cos(t * 18.6);
        private static float Ramp(float t, float t0, float rate) => Math.Max(0f, t - t0) * rate;

        private static List<Sc> Scenarios()
        {
            var L = new List<Sc>();
            Sc Axis(string n, float s, bool neg, bool inv, float target, float rate)
            {
                var sc = new Sc { Name = n, Saved = s, Neg = neg, Inv = inv };
                sc.Raw = t => TM.Inv(target < s ? Math.Max(target, s - Ramp(t, 0.15f, rate)) : Math.Min(target, s + Ramp(t, 0.15f, rate)), neg, inv);
                return sc;
            }
            L.Add(Axis("lever far below (throttle lower)", 0.8f, false, false, 0.1f, 0.6f));
            L.Add(Axis("lever far above", 0.4f, false, false, 1f, 0.6f));
            L.Add(Axis("lever far above neg", 0.4f, true, false, 1f, 0.6f));
            L.Add(new Sc { Name = "lever parked beyond 0.5 (ramp)", Saved = 0.4f, Raw = t => 0.9f });
            L.Add(new Sc { Name = "keys up held during hold", Saved = 0.5f, Raw = t => t >= 0.1f && t < 1f ? 1f : 0f });
            L.Add(new Sc { Name = "keys up held into blend", Saved = 0.3f, Neg = true, Raw = t => t >= 2.3f && t < 3.0f ? 1f : 0f });
            L.Add(new Sc { Name = "keys down held (throttle lower)", Saved = 0.7f, Raw = t => t >= 0.1f && t < 1f ? -1f : 0f });
            L.Add(new Sc { Name = "keys relative mode up", Saved = 0.5f, Neg = true, Relative = true, Raw = t => t >= 0.2f && t < 0.8f ? 0.4f : 0f });
            L.Add(new Sc { Name = "no input", Saved = 0.65f, Neg = true, Raw = t => 0f });
            L.Add(new Sc { Name = "axis modifier held", Saved = 0.6f, Neg = true, Raw = t => 0.3f + Jit(t, 0.01f), AxisMod = t => true });
            { float s = 0.6f; L.Add(new Sc { Name = "pad trigger up noisy", Saved = s, Neg = true,
                Raw = t => TM.Inv(Math.Min(0.9f, s + Ramp(t, 0.3f, 0.25f)), true, false) + Jit(t, 0.003f) }); }
            L.Add(new Sc { Name = "pad spring (stick out and back)", Saved = 0.5f, Neg = true,
                Raw = t => t < 0.2f ? 0f : t < 0.6f ? Math.Min(0.8f, (t - 0.2f) * 4f) : Math.Max(0f, 0.8f - (t - 0.6f) * 4f) });
            L.Add(new Sc { Name = "pad spring during blend", Saved = 0.5f, Neg = true,
                Raw = t => t < 2.6f ? 0f : t < 2.9f ? Math.Min(0.8f, (t - 2.6f) * 4f) : Math.Max(0f, 0.8f - (t - 2.9f) * 4f) });
            L.Add(Axis("inverted lever far above", 0.5f, false, true, 1f, 0.6f));
            L.Add(Axis("inverted lever far below neg (throttle lower)", 0.5f, true, true, 0f, 0.6f));
            L.Add(new Sc { Name = "inverted keys down (= throttle up)", Saved = 0.4f, Neg = true, Inv = true, Raw = t => t >= 0.1f && t < 1.5f ? -1f : 0f });
            { float s = 0.5f; float a0 = TM.Inv(s, true, false);
              L.Add(new Sc { Name = "mixed axis + keys", Saved = s, Neg = true,
                Raw = t => t < 0.15f ? a0 : t < 0.5f ? a0 + (t - 0.15f) * 0.25f : t < 0.7f ? a0 + 0.0875f : t < 1.3f ? 1f : t < 1.8f ? 0f : t < 2.1f ? -1f : 0f }); }
            { float s = 0.5f; float a0 = TM.Inv(s, true, false);
              L.Add(new Sc { Name = "deadzone axis jitter +-0.004", Saved = s, Neg = true, Raw = t => a0 + Jit(t, 0.004f) }); }
            L.Add(new Sc { Name = "deadzone pad stick at 0", Saved = 0.55f, Raw = t => 0f });
            return L;
        }

        private static void Run(Sc sc, int fps)
        {
            float dt = 1f / fps, s = sc.Saved;
            int nHold = 2 * fps, nProbe = fps / 2, nBlend = (int)Math.Ceiling(TM.BlendTime * fps) + 1, nRel = fps / 2;
            int total = nHold + nProbe + nBlend + nRel;
            var outp = new float[total]; var live = new float[total]; var acc = new float[total];
            bool used; float a = TM.Seed(s, false, 0f, sc.Neg, sc.Inv, out used);
            float seedErr = Math.Abs(TM.Map(a, sc.Neg, sc.Inv) - s);
            float prevRaw = sc.Raw(0f);
            int blendStart = nHold + nProbe, relStart = blendStart + nBlend;
            for (int i = 0; i < total; i++)
            {
                float t = i * dt, raw = sc.Raw(t);
                a = TM.Step(a, raw, prevRaw, sc.Relative, sc.AxisMod(t), dt); prevRaw = raw;
                acc[i] = a; live[i] = TM.Map(a, sc.Neg, sc.Inv);
                ThrottlePhase ph = i < nHold ? ThrottlePhase.Hold : i < blendStart ? ThrottlePhase.Probe : i < relStart ? ThrottlePhase.Blend : ThrottlePhase.Released;
                outp[i] = TM.Hold(s, live[i], ph, TM.Progress((i - blendStart) * dt, TM.BlendTime));
            }
            string n = "S " + sc.Name + " @" + fps;
            Check(n + ": seed==saved", seedErr <= 1e-5f, "err=" + F(seedErr));
            float hMin = float.MaxValue, hMax = float.MinValue;
            for (int i = 0; i < blendStart; i++) { hMin = Math.Min(hMin, outp[i]); hMax = Math.Max(hMax, outp[i]); }
            Check(n + ": hold+probe out==saved", hMin == s && hMax == s, "out " + F(hMin) + ".." + F(hMax));
            float liveMin = float.MaxValue; int dip = 0;
            for (int i = blendStart; i < relStart; i++) { liveMin = Math.Min(liveMin, live[i]); if (outp[i] < Math.Min(s, live[i]) - 1e-6f) dip++; }
            if (TM.Lower(liveMin, s, TM.Slack))
                Report(n + ": blend no dip", "SKIP", "throttle-lower (player choice): live min " + F(liveMin) + (dip == 0 ? ", out>=min(saved,live) held" : ", ALSO " + dip + " frames < min(saved,live)"));
            else Check(n + ": blend no dip", dip == 0, dip == 0 ? "live min " + F(liveMin) : dip + " frames below min(saved,live)");
            float worstExcess = float.MinValue;
            for (int i = blendStart; i <= relStart; i++)
            {
                float step = Math.Abs(outp[i] - outp[i - 1]);
                float allow = Math.Abs(live[i] - live[i - 1]) + 1.5f * Math.Max(Math.Abs(s - live[i]), Math.Abs(s - live[i - 1])) * dt / TM.BlendTime + 1e-5f;
                worstExcess = Math.Max(worstExcess, step - allow);
            }
            bool endOk = true;
            for (int i = relStart - 1; i < total; i++) if (outp[i] != live[i] || live[i] != TM.Map(acc[i], sc.Neg, sc.Inv)) endOk = false;
            Check(n + ": no jump (blend + release)", worstExcess <= 0f && outp[blendStart] == s, "entry=" + F(outp[blendStart] - s) + " release step=" + F(Math.Abs(outp[relStart] - outp[relStart - 1])) + (worstExcess > 0f ? " excess " + F(worstExcess) : ""));
            Check(n + ": end == live == Map(acc)", endOk, "end=" + F(outp[total - 1]));
        }
    }
}