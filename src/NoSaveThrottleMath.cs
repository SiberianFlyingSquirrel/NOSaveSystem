using System;

namespace NOSaveSystem
{
    public enum ThrottlePhase { Hold = 0, Probe = 1, Blend = 2, Released = 3 }

    public enum EngineReArm { Proceed = 0, BlockExpired = 1, BlockReleased = 2, KeepExisting = 3, RetireAndProceed = 4, ClearExpiryAndProceed = 5 }

    public static class ThrottleMath
    {
        public const float BlendTime = 0.75f;
        public const float MadToSigma = 1.4826f;

        public static float Clamp01(float x) { return x < 0f ? 0f : (x > 1f ? 1f : x); }

        public static float Clamp(float x, float a, float b) { return x < a ? a : (x > b ? b : x); }

        public static float MapAccToThrottle(float acc, bool useNegative, bool inverted)
        {
            float x = acc;
            if (useNegative) x = 0.5f * (x + 1f);
            if (inverted) x = 1f - x;
            return Clamp01(x);
        }

        public static float ThrottleToAcc(float thr, bool useNegative, bool inverted)
        {
            float x = Clamp01(thr);
            if (inverted) x = 1f - x;
            return useNegative ? x * 2f - 1f : x;
        }

        public static float SeedAcc(float savedThrottle, bool savedAccCaptured, float savedAcc, bool useNegative, bool inverted, out bool usedSavedAcc)
        {
            usedSavedAcc = savedAccCaptured && Math.Abs(MapAccToThrottle(savedAcc, useNegative, inverted) - savedThrottle) <= 0.005f;
            return usedSavedAcc ? savedAcc : ThrottleToAcc(savedThrottle, useNegative, inverted);
        }

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

        public static float BlendWeight(float t01)
        {
            float p = Clamp01(t01);
            p = -2f * p * p * p + 3f * p * p;
            return 1f - p;
        }

        public static float BlendProgress(float elapsed, float length)
        {
            return length <= 0f ? 1f : Clamp01(elapsed / length);
        }

        public static float Blend(float saved, float live, float t01)
        {
            if (t01 <= 0f) return saved;
            if (t01 >= 1f) return live;
            float w = BlendWeight(t01);
            return live + (saved - live) * w;
        }

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

        public static bool IsThrottleLower(float live, float saved, float slack)
        {
            return live < saved - slack;
        }

        public static float FollowError(float engineThrottle, float expectedThrottle)
        {
            return Math.Abs(engineThrottle - expectedThrottle);
        }

        public static int FollowStep(float engineThrottle, float expectedThrottle, float eps, int badRun, out float error)
        {
            error = FollowError(engineThrottle, expectedThrottle);
            return error > eps ? badRun + 1 : 0;
        }

        public static bool FollowFixAllowed(float engineThrottle, float expectedThrottle)
        {
            return expectedThrottle > engineThrottle;
        }

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

        public static float Median(float[] sorted, int n)
        {
            return n % 2 == 1 ? sorted[n / 2] : 0.5f * (sorted[n / 2 - 1] + sorted[n / 2]);
        }

        public static float StdSigma(double sum, double sumSq, int n)
        {
            if (n < 2) return 0f;
            double m = sum / n, v = sumSq / n - m * m;
            return (float)Math.Sqrt(Math.Max(v, 0.0));
        }

        public static float EpsStep(float sigma, float floor, float k)
        {
            return Math.Max(floor, k * sigma);
        }

        public static float PassEps(float sigma, float minEps)
        {
            return Math.Max(minEps, 3f * sigma);
        }

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

        public static EngineReArm EngineReArmDecision(bool expired, int expiredSeq, bool hasLock, bool lockReleased, int lockSeq, int seq)
        {
            if (expired && expiredSeq == seq) return EngineReArm.BlockExpired;
            if (hasLock)
            {
                if (lockSeq != seq) return EngineReArm.RetireAndProceed;
                return lockReleased ? EngineReArm.BlockReleased : EngineReArm.KeepExisting;
            }
            return expired ? EngineReArm.ClearExpiryAndProceed : EngineReArm.Proceed;
        }

        public sealed class OnceGate
        {
            private bool _any;
            private int _ac, _gen, _seq;
            public bool TryClaim(int ac, int gen, int seq)
            {
                if (_any && ac == _ac && gen == _gen && seq == _seq) return false;
                _any = true; _ac = ac; _gen = gen; _seq = seq;
                return true;
            }
        }

        public sealed class SrcClassifier
        {
            public int Frames, KeyFrames, FracFrames, CentreAfterFrac, RestFrac;
            private bool _sawFrac; private float _last = float.NaN; private int _stable;

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

            public string Result(string lastActiveControllerType)
            {
                if (FracFrames > 0) return RestFrac > 0 ? "axis" : (CentreAfterFrac > 0 ? "pad" : "axis");
                if (KeyFrames > 0) return "kb";
                return lastActiveControllerType == "Joystick" ? "axis(idle)" : "kb(idle)";
            }
        }
    }
}
