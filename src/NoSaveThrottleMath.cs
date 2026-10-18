using System;

namespace NOSaveSystem
{
    /// <summary>Phase of the post-load engine/throttle hold (input to <see cref="ThrottleMath.HoldOutput"/>).</summary>
    public enum ThrottlePhase { Hold = 0, Probe = 1, Blend = 2, Released = 3 }

    /// <summary>
    /// v0.11.1: Unity-free throttle / power math used by the plugin (and testable outside the game). Only System / Math.
    ///
    /// Game input path (decompiled PilotPlayerState.PlayerThrottleAxis1Controls). Every device goes through the Rewired
    /// "Throttle" action, so rebinding, axis inversion, deadzone and calibration are already applied by Rewired:
    ///   num = clamp(GetAxisRaw("Throttle"), -1, 1), prev = clamp(GetAxisRawPrev("Throttle"), -1, 1)
    ///   throttleUseRelative: num/prev quantised to -1/0/+1 (|x| &gt; 0.1)
    ///   "Axis Modifier" held: throttle num = 0 (the value goes to Custom Axis 1)
    ///   0 &lt; |num-prev| &lt; 0.5 -&gt; acc = num                          (absolute axis: HOTAS lever, stick/trigger moving)
    ///   else |num| &gt; 0.5      -&gt; acc += clamp(num - acc, -dt, +dt)  (keyboard key, held stick, lever parked beyond 0.5)
    ///   throttle = clamp01(throttleUseNegative ? 0.5*(acc+1) : acc), then 1-x for an inverted collective
    /// acc is the private PilotPlayerState.simulatedThrottle.
    /// </summary>
    public static class ThrottleMath
    {
        /// <summary>Player blend length after the hold (seconds).</summary>
        public const float BlendTime = 0.75f;
        /// <summary>MAD -&gt; sigma factor for normally distributed noise.</summary>
        public const float MadToSigma = 1.4826f;

        /// <summary>Clamp to [0, 1].</summary>
        public static float Clamp01(float x) { return x < 0f ? 0f : (x > 1f ? 1f : x); }

        /// <summary>Clamp to [a, b].</summary>
        public static float Clamp(float x, float a, float b) { return x < a ? a : (x > b ? b : x); }

        /// <summary>The game's accumulator -&gt; controlInputs.throttle mapping (exact copy):
        /// clamp01(useNegative ? 0.5*(acc+1) : acc), then 1-x when <paramref name="inverted"/> (inverted collective).</summary>
        public static float MapAccToThrottle(float acc, bool useNegative, bool inverted)
        {
            float x = acc;
            if (useNegative) x = 0.5f * (x + 1f);
            if (inverted) x = 1f - x;
            return Clamp01(x);
        }

        /// <summary>Inverse of <see cref="MapAccToThrottle"/> on its range: the accumulator value that the game maps to
        /// <paramref name="thr"/> (thr clamped to [0,1]; result in [-1,1] with useNegative, else [0,1]).</summary>
        public static float ThrottleToAcc(float thr, bool useNegative, bool inverted)
        {
            float x = Clamp01(thr);
            if (inverted) x = 1f - x;
            return useNegative ? x * 2f - 1f : x;
        }

        /// <summary>Seed for the accumulator after a load: the saved accumulator if it maps to the saved throttle within
        /// 0.005, else the inverse of the saved throttle (the throttle continues exactly from the saved value).</summary>
        public static float SeedAcc(float savedThrottle, bool savedAccCaptured, float savedAcc, bool useNegative, bool inverted, out bool usedSavedAcc)
        {
            usedSavedAcc = savedAccCaptured && Math.Abs(MapAccToThrottle(savedAcc, useNegative, inverted) - savedThrottle) <= 0.005f;
            return usedSavedAcc ? savedAcc : ThrottleToAcc(savedThrottle, useNegative, inverted);
        }

        /// <summary>Replica of the game's per-frame accumulator update for the Rewired "Throttle" action
        /// (raw/rawPrev = GetAxisRaw / GetAxisRawPrev). Documentation and synthetic tests only; the plugin never drives it.</summary>
        public static float GameAccStep(float acc, float raw, float rawPrev, bool relative, bool axisModifier, float dt)
        {
            float num = Clamp(raw, -1f, 1f), prev = Clamp(rawPrev, -1f, 1f);
            if (relative)
            {
                num = Math.Abs(num) > 0.1f ? Math.Sign(num) * 1f : 0f;
                prev = Math.Abs(prev) > 0.1f ? Math.Sign(prev) * 1f : 0f;
            }
            if (axisModifier) num = 0f;
            float d = Math.Abs(num - prev);
            if (d > 0f && d < 0.5f) acc = num;
            else if (Math.Abs(num) > 0.5f) acc += Clamp(num - acc, -dt, dt);
            return acc;
        }

        /// <summary>Weight of the SAVED throttle during the blend: Unity Mathf.SmoothStep(1, 0, t01), t01 = blend
        /// progress clamped to [0,1] (1 at the start, 0 at the end).</summary>
        public static float BlendWeight(float t01)
        {
            float p = Clamp01(t01);
            p = -2f * p * p * p + 3f * p * p;
            return 1f - p;
        }

        /// <summary>Blend progress in [0,1] from elapsed blend time and blend length (length &lt;= 0 -&gt; 1).</summary>
        public static float BlendProgress(float elapsed, float length)
        {
            return length <= 0f ? 1f : Clamp01(elapsed / length);
        }

        /// <summary>Smooth blend from saved (t01 = 0) to live (t01 = 1): live + (saved - live) * BlendWeight(t01).
        /// Exactly saved at t01 &lt;= 0 and exactly live at t01 &gt;= 1.</summary>
        public static float Blend(float saved, float live, float t01)
        {
            if (t01 <= 0f) return saved;
            if (t01 >= 1f) return live;
            float w = BlendWeight(t01);
            return live + (saved - live) * w;
        }

        /// <summary>Throttle input the hold writes into controlInputs.throttle (the value the engine sim reads):
        /// Hold / Probe -&gt; exactly saved, whatever the axis / keys / pad do (no power-input dip during the hold);
        /// Blend -&gt; <see cref="Blend"/>(saved, live, blendT01); Released -&gt; live (the value the game writes itself, so
        /// the end of the override is jump-free).</summary>
        public static float HoldOutput(float saved, float live, ThrottlePhase phase, float blendT01 = 0f)
        {
            switch (phase)
            {
                case ThrottlePhase.Hold:
                case ThrottlePhase.Probe: return saved;
                case ThrottlePhase.Blend: return Blend(saved, live, blendT01);
                default: return live;
            }
        }

        /// <summary>The player's live throttle is below the saved one by more than the slack (lower power is the
        /// player's choice: no reassert, no dip FAIL).</summary>
        public static bool IsThrottleLower(float live, float saved, float slack)
        {
            return live < saved - slack;
        }

        /// <summary>FOLLOW error: |throttle the engine reads - throttle the game maps from the player's accumulator|.</summary>
        public static float FollowError(float engineThrottle, float expectedThrottle)
        {
            return Math.Abs(engineThrottle - expectedThrottle);
        }

        /// <summary>FOLLOW stuck counter: returns badRun+1 when the error exceeds eps, else 0
        /// (STUCK when the result reaches the stuck-frame count).</summary>
        public static int FollowStep(float engineThrottle, float expectedThrottle, float eps, int badRun, out float error)
        {
            error = FollowError(engineThrottle, expectedThrottle);
            return error > eps ? badRun + 1 : 0;
        }

        /// <summary>The single STUCK fix may only RAISE the engine's throttle input (never lowers power).</summary>
        public static bool FollowFixAllowed(float engineThrottle, float expectedThrottle)
        {
            return expectedThrottle > engineThrottle;
        }

        /// <summary>Robust noise of n samples: max(1.4826 * MAD, 0.5 * mean absolute deviation from the median) (the floor
        /// keeps a quantised signal from collapsing MAD to 0). scratch must hold n floats. NaN if n &lt; 1.</summary>
        public static float MadSigma(float[] samples, int n, float[] scratch)
        {
            if (samples == null || scratch == null || n < 1 || n > samples.Length || n > scratch.Length) return float.NaN;
            Array.Copy(samples, scratch, n);
            Array.Sort(scratch, 0, n);
            float med = Median(scratch, n);
            for (int i = 0; i < n; i++) scratch[i] = Math.Abs(samples[i] - med);
            Array.Sort(scratch, 0, n);
            float mad = Median(scratch, n);
            float meanAbs = 0f; for (int i = 0; i < n; i++) meanAbs += scratch[i]; meanAbs /= n;
            return Math.Max(MadToSigma * mad, 0.5f * meanAbs);
        }

        /// <summary>Median of the first n values of an already sorted array.</summary>
        public static float Median(float[] sorted, int n)
        {
            return n % 2 == 1 ? sorted[n / 2] : 0.5f * (sorted[n / 2 - 1] + sorted[n / 2]);
        }

        /// <summary>Population standard deviation from running sum / sum of squares (0 if n &lt; 2).</summary>
        public static float StdSigma(double sum, double sumSq, int n)
        {
            if (n < 2) return 0f;
            double m = sum / n, v = sumSq / n - m * m;
            return (float)Math.Sqrt(Math.Max(v, 0.0));
        }

        /// <summary>Per-step drift limit of the probe: max(floor, k * sigma) (floor 2e-4, k 4 in the plugin).</summary>
        public static float EpsStep(float sigma, float floor, float k)
        {
            return Math.Max(floor, k * sigma);
        }

        /// <summary>Monitor pass eps: max(minEps, 3 * sigma) (minEps 0.003 = 0.3 % in the plugin).</summary>
        public static float PassEps(float sigma, float minEps)
        {
            return Math.Max(minEps, 3f * sigma);
        }

        /// <summary>One monitor dip step: ratio (live/saved power or RPM) below 1 - passEps extends the dip run, otherwise
        /// resets it. failed becomes true once the run reaches failSteps. Returns true when this step is a dip.</summary>
        public static bool DipStep(float ratio, float passEps, ref int dipRun, int failSteps, ref bool failed)
        {
            if (ratio < 1f - passEps)
            {
                dipRun++;
                if (dipRun >= failSteps) failed = true;
                return true;
            }
            dipRun = 0;
            return false;
        }

        /// <summary>
        /// Input source of the player's throttle from the game's own Rewired "Throttle" action values only (no device
        /// names): kb = only -1/0/+1 with key frames; pad = fractional values that spring back to the centre;
        /// axis = fractional values resting away from the centre (lever). Idle windows use the Rewired type of the last
        /// active controller ("Keyboard"/"Mouse" -&gt; kb, "Joystick" -&gt; axis) and are marked "(idle)".
        /// </summary>
        public sealed class SrcClassifier
        {
            /// <summary>Counters (frames seen, frames at +-1, fractional frames, centre returns after fractional, resting fractional).</summary>
            public int Frames, KeyFrames, FracFrames, CentreAfterFrac, RestFrac;
            private bool _sawFrac; private float _last = float.NaN; private int _stable;

            /// <summary>Feed one frame of GetAxisRaw("Throttle") (NaN ignored).</summary>
            public void Add(float raw)
            {
                if (float.IsNaN(raw)) return;
                Frames++;
                float a = Math.Abs(raw);
                bool keyVal = raw == 0f || raw == 1f || raw == -1f;
                if (raw == 1f || raw == -1f) KeyFrames++;
                if (!keyVal) { FracFrames++; _sawFrac = true; }
                if (_sawFrac && a < 0.05f) CentreAfterFrac++;
                if (!float.IsNaN(_last) && Math.Abs(raw - _last) < 1e-4f) _stable++; else _stable = 0;
                if (!keyVal && a >= 0.05f && _stable >= 5) RestFrac++;
                _last = raw;
            }

            /// <summary>"kb" | "axis" | "pad", or "kb(idle)" / "axis(idle)" when no input moved in the window.</summary>
            public string Result(string lastActiveControllerType)
            {
                if (FracFrames > 0) return RestFrac > 0 ? "axis" : (CentreAfterFrac > 0 ? "pad" : "axis");
                if (KeyFrames > 0) return "kb";
                return lastActiveControllerType == "Joystick" ? "axis(idle)" : "kb(idle)";
            }
        }
    }
}
