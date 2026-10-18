using System;
using System.Collections;
using System.Text;
using NuclearOption.Networking;
using NuclearOption.UI;
using UnityEngine;

namespace NOSaveSystem
{
    [HarmonyLib.HarmonyPatch(typeof(NetworkPause), "HookPausedChanged")]
    internal static class Patch_NetworkPause_HookPausedChanged_Pause0
    {
        private static System.Reflection.FieldInfo _localPaused;
        [HarmonyLib.HarmonyPriority(HarmonyLib.Priority.First)]
        static bool Prefix(NetworkPause __instance)
        {
            try
            {
                if (__instance == null || !NoSavePause0.SuppressTimeScale || __instance.NetworkserverPaused) return true;
                try { if (__instance.overlay != null) __instance.overlay.SetActive(false); } catch { }
                try
                {
                    if (_localPaused == null) _localPaused = HarmonyLib.AccessTools.Field(typeof(NetworkPause), "localPaused");
                    if (_localPaused != null) _localPaused.SetValue(__instance, false);
                }
                catch { }
                NOSaveSystemPlugin.Log?.LogInfo("PAUSE0 HOOK NetworkPause unpause: overlay hidden, timeScale write skipped (PAUSE0 owns the pause)");
                return false;
            }
            catch { return true; }
        }
    }

    internal static class NoSavePause0
    {
        internal static int FixedTicks;
        private static int _ticksApply = -1, _ticksReady = -1, _frameReady = -1;
        private static int _gen;
        internal static bool Active;

        internal static void Log(string m) { NOSaveSystemPlugin.Log?.LogInfo("PAUSE0 " + m); }

        internal static void MarkApplyStart() { _ticksApply = FixedTicks; _ticksReady = -1; _frameReady = -1; ResetState("applyStart", false); }

        internal static void ResetState(string reason, bool log = true)
        {
            bool was = Active || _handoff;
            Active = false; _handoff = false; GuardHits = 0; _gen++;
            try { NoSaveVerify.ResetFrame0(); } catch { }
            if (log) Log("reset reason=" + reason + (was ? " (was active)" : ""));
        }

        internal static bool SuppressTimeScale { get { return _handoff || Active; } }
        private static bool _handoff;
        internal static int GuardHits;

        internal static void GuardHit(string where)
        {
            GuardHits++;
            NOSaveSystemPlugin.Log?.LogWarning("PAUSE0 GUARD hit#" + GuardHits + " where=" + where + " timeScale=" + Time.timeScale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " -> 0");
            TimeScaleManager.Scale = 0f;
        }

        internal static void OnFixedUpdate()
        {
            FixedTicks++;
            if (Active)
            {
                bool menu = false; try { menu = GameplayUI.GameIsPaused; } catch { }
                if (menu && Time.timeScale > 0f) GuardHit("FixedUpdate(sim tick ran while paused)");
            }
        }

        internal static void MarkSceneReady() { _ticksReady = FixedTicks; _frameReady = Time.frameCount; }

        internal static bool HandOff(StringBuilder plan, MidMissionSnapshot snap = null)
        {
            bool netUnpaused = false;
            try
            {
                if (_ticksReady < 0) MarkSceneReady();
                int ticks = FixedTicks - _ticksReady;
                int ticksApply = _ticksApply >= 0 ? FixedTicks - _ticksApply : -1;
                int frames = Time.frameCount - _frameReady;
                GameplayUI ui = null;
                try { ui = SceneSingleton<GameplayUI>.i; } catch { }
                if (ui == null) { Fail("GameplayUI null", plan); MissionReload.TrySetPaused(false); return false; }
                bool already = GameplayUI.GameIsPaused;
                _handoff = true;
                try
                {
                    MissionReload.TrySetPaused(false); netUnpaused = true;
                    if (!already) ui.PauseGame();
                }
                finally { _handoff = false; }
                bool paused = GameplayUI.GameIsPaused;
                float ts = Time.timeScale;
                if (!paused)
                {
                    TimeScaleManager.Scale = 1f;
                    Fail("PauseGame did not open the menu (timeScale was " + ts.ToString("0.###") + ", now 1)", plan); return false;
                }
                TimeScaleManager.Scale = 0f;
                if (ts > 0f)
                {
                    try { var al = Type.GetType("UnityEngine.AudioListener, UnityEngine.AudioModule"); var pp = al != null ? al.GetProperty("pause") : null; if (pp != null) pp.SetValue(null, true, null); } catch { }
                    Log("menu open but timeScale was " + ts.ToString("0.###") + " -> forced 0");
                }
                NoSaveV093.LoadPauseArmed = true;
                Active = true;
                try { NoSaveVerify.CaptureFrame0(snap); } catch (Exception fex) { NOSaveSystemPlugin.Log?.LogWarning("PAUSE0 frame0 capture FAIL " + fex.Message); }
                int g = ++_gen;
                string line = "frame=" + frames + " simTicks=" + ticks + " applyTicks=" + (ticksApply < 0 ? "n/a" : ticksApply.ToString())
                    + " via=GameplayUI.PauseGame" + (already ? "(already-paused)" : "") + " timeScale=" + Time.timeScale.ToString("0.###");
                if (ticks != 0) NOSaveSystemPlugin.Log?.LogWarning("PAUSE0 " + line + " (expected simTicks=0)");
                else Log(line);
                plan?.AppendLine("  PAUSE0 " + line);
                try { NOSaveSystemPlugin.Instance.StartCoroutine(NoSaveCoroutines.Safe(GuardCo(g), "PAUSE0-guard")); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                _handoff = false;
                Fail(ex.GetType().Name + ": " + ex.Message, plan);
                if (!netUnpaused) { try { MissionReload.TrySetPaused(false); } catch { } }
                bool p = false; try { p = GameplayUI.GameIsPaused; } catch { }
                if (!p) { try { TimeScaleManager.Scale = 1f; } catch { } }
                Active = false;
                return false;
            }
        }

        private static void Fail(string reason, StringBuilder plan)
        {
            NOSaveSystemPlugin.Log?.LogWarning("PAUSE0 FAIL " + reason + " -> continue (legacy delayed pause if enabled)");
            plan?.AppendLine("  PAUSE0 FAIL " + reason);
        }

        private static IEnumerator GuardCo(int g)
        {
            int t0 = FixedTicks;
            for (int i = 0; i < 10; i++)
            {
                yield return null;
                if (g != _gen) yield break;
                if (!GameplayUI.GameIsPaused) { Active = false; Log("guard: resumed after " + (i + 1) + " frame(s) simTicksWhilePaused=" + (FixedTicks - t0) + " guardHits=" + GuardHits); yield break; }
                if (Time.timeScale > 0f) GuardHit("frame+" + (i + 1));
            }
            Log("guard: pause held 10 frames simTicksWhilePaused=" + (FixedTicks - t0) + " guardHits=" + GuardHits);
        }

        internal static void OnResume()
        {
            if (!Active) return;
            Active = false;
            Log("resume: sim time starts now (hold/probe/blend, watchdog, monitor, VERIFY run on sim time from here)");
        }
    }
}
