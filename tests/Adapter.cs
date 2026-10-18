using NOSaveSystem;

namespace NOSaveSystem.Tests
{
    internal static class TM
    {
        public const float BlendTime = ThrottleMath.BlendTime;
        public const float Slack = 0.02f;
        public const float ProbeEps = 2e-4f, ProbeSigmaK = 4f, PassMinEps = 0.003f;

        public static float Map(float acc, bool neg, bool inv) => ThrottleMath.MapAccToThrottle(acc, neg, inv);
        public static float Inv(float thr, bool neg, bool inv) => ThrottleMath.ThrottleToAcc(thr, neg, inv);
        public static float Seed(float saved, bool cap, float savedAcc, bool neg, bool inv, out bool used) => ThrottleMath.SeedAcc(saved, cap, savedAcc, neg, inv, out used);
        public static float Step(float acc, float raw, float rawPrev, bool relative, bool axisMod, float dt) => ThrottleMath.GameAccStep(acc, raw, rawPrev, relative, axisMod, dt);
        public static float Blend(float saved, float live, float t01) => ThrottleMath.Blend(saved, live, t01);
        public static float Progress(float elapsed, float len) => ThrottleMath.BlendProgress(elapsed, len);
        public static float Hold(float saved, float live, ThrottlePhase ph, float t01 = 0f) => ThrottleMath.HoldOutput(saved, live, ph, t01);
        public static bool Lower(float live, float saved, float slack) => ThrottleMath.IsThrottleLower(live, saved, slack);
        public static int FollowStep(float eng, float exp, float eps, int run, out float err) => ThrottleMath.FollowStep(eng, exp, eps, run, out err);
        public static bool FixAllowed(float eng, float exp) => ThrottleMath.FollowFixAllowed(eng, exp);
        public static float MadSigma(float[] s, int n, float[] scratch) => ThrottleMath.MadSigma(s, n, scratch);
        public static float EpsStep(float sigma) => ThrottleMath.EpsStep(sigma, ProbeEps, ProbeSigmaK);
        public static float PassEps(float sigma) => ThrottleMath.PassEps(sigma, PassMinEps);
        public static bool DipStep(float ratio, float passEps, ref int run, int failSteps, ref bool failed) => ThrottleMath.DipStep(ratio, passEps, ref run, failSteps, ref failed);
        public static string Classify(float[] raws, string lastType)
        {
            var c = new ThrottleMath.SrcClassifier();
            foreach (var r in raws) c.Add(r);
            return c.Result(lastType);
        }
    }
}