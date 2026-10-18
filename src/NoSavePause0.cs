using System;
using System.Collections;
using System.Text;
using NuclearOption.Networking;
using NuclearOption.UI;
using UnityEngine;

namespace NOSaveSystem
{
    /// <summary>
    /// v0.11.0 FEAT PAUSE0: the loaded mission is paused on its FIRST rendered frame with zero simulation ticks.
    ///
    /// Flow (MissionReload): the snapshot core runs under NetworkPause (HookPausedChanged -> timeScale 0, so no
    /// FixedUpdate runs). Every saved state (transforms, velocities, engine force incl. dead engines, gear/flap pose,
    /// damage, statics, datalink, deep state, targets) is applied while still paused. Then, inside ONE frame with no
    /// yield in between: NetworkPause=false (timeScale 1, applied synchronously on the host) and immediately
    /// GameplayUI.PauseGame() - the game's own ESC path (LeaderboardMenu.Open: SP timeScale 0 + AudioListener.pause).
    /// Unity runs FixedUpdate only inside the player loop, so no physics step can happen between the two calls.
    /// The loading screen is hidden afterwards, so the first frame the player sees is the pause menu.
    ///
    /// simTicks = FixedUpdate steps counted (plugin FixedUpdate) between scene-ready (core done) and the pause.
    /// </summary>
    /// <summary>v0.11.0 build 2 (P1): while PAUSE0 owns the pause, NetworkPause's unpause hook only hides its overlay;
    /// its timeScale=1 write is skipped (the pause menu owns time; LeaderboardMenu.Close restores it on resume).</summary>
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
        /// <summary>Incremented from NOSaveSystemPlugin.FixedUpdate (runs only while timeScale &gt; 0).</summary>
        internal static int FixedTicks;
        private static int _ticksApply = -1, _ticksReady = -1, _frameReady = -1;
        private static int _gen;
        /// <summary>True from a successful PAUSE0 until the first resume.</summary>
        internal static bool Active;

        internal static void Log(string m) { NOSaveSystemPlugin.Log?.LogInfo("PAUSE0 " + m); }

        /// <summary>Called when MissionReload sets NetworkPause=true before the snapshot apply.</summary>
        internal static void MarkApplyStart() { _ticksApply = FixedTicks; _ticksReady = -1; _frameReady = -1; ResetState("applyStart", false); }

        /// <summary>v0.11.0 build 3 (G1): clears every PAUSE0 flag so a quit-to-menu from the PAUSE0 menu (no ResumeGame)
        /// cannot leave Active=true and make the NetworkPause prefix freeze the next mission at timeScale 0.
        /// Active=false, _handoff=false, guard coroutine stopped (_gen++), frame0 capture cleared.
        /// Called from sceneUnloaded, sceneLoaded(Single) and MarkApplyStart.</summary>
        internal static void ResetState(string reason, bool log = true)
        {
            bool was = Active || _handoff;
            Active = false; _handoff = false; GuardHits = 0; _gen++;
            try { NoSaveVerify.ResetFrame0(); } catch { }
            if (log) Log("reset reason=" + reason + (was ? " (was active)" : ""));
        }

        /// <summary>v0.11.0 build 2 (P1): while true the NetworkPause timescale hook may not raise timeScale
        /// (Harmony prefix Patch_NetworkPause_HookPausedChanged_Pause0).</summary>
        internal static bool SuppressTimeScale { get { return _handoff || Active; } }
        private static bool _handoff;
        internal static int GuardHits;

        internal static void GuardHit(string where)
        {
            GuardHits++;
            NOSaveSystemPlugin.Log?.LogWarning("PAUSE0 GUARD hit#" + GuardHits + " where=" + where + " timeScale=" + Time.timeScale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " -> 0");
            TimeScaleManager.Scale = 0f;
        }

        /// <summary>Called from the plugin FixedUpdate: a FixedUpdate while the PAUSE0 menu is up means a sim tick ran.</summary>
        internal static void OnFixedUpdate()
        {
            FixedTicks++;
            if (Active)
            {
                bool menu = false; try { menu = GameplayUI.GameIsPaused; } catch { }
                if (menu && Time.timeScale > 0f) GuardHit("FixedUpdate(sim tick ran while paused)");
            }
        }

        /// <summary>Called when the snapshot core finished (scene ready, all frame-0 state applied).</summary>
        internal static void MarkSceneReady() { _ticksReady = FixedTicks; _frameReady = Time.frameCount; }

        /// <summary>
        /// Unpause the NetworkPause and open the game's pause menu in the same frame.
        /// Returns false (after logging PAUSE0 FAIL) when the pause could not be established; the caller falls back
        /// to the legacy delayed pause. Never throws, never waits.
        /// </summary>
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
                // v0.11.0 build 2 (P1): the NetworkPause unpause hook is told not to raise timeScale, so time stays 0
                // through the hand-off (it never reaches 1, not even inside this frame).
                _handoff = true;
                try
                {
                    MissionReload.TrySetPaused(false); netUnpaused = true; // overlay off; timeScale write suppressed by prefix
                    if (!already) ui.PauseGame();                          // same frame: ESC path (SP: timeScale 0 + audio pause)
                }
                finally { _handoff = false; }
                bool paused = GameplayUI.GameIsPaused;
                float ts = Time.timeScale;
                if (!paused)
                {
                    TimeScaleManager.Scale = 1f; // suppressed unpause above: give the game its time back
                    Fail("PauseGame did not open the menu (timeScale was " + ts.ToString("0.###") + ", now 1)", plan); return false;
                }
                TimeScaleManager.Scale = 0f; // always (P1)
                if (ts > 0f)
                {
                    try { var al = Type.GetType("UnityEngine.AudioListener, UnityEngine.AudioModule"); var pp = al != null ? al.GetProperty("pause") : null; if (pp != null) pp.SetValue(null, true, null); } catch { } // no AudioModule reference
                    Log("menu open but timeScale was " + ts.ToString("0.###") + " -> forced 0");
                }
                NoSaveV093.LoadPauseArmed = true; // first ResumeGame reasserts ammo/pilot (v0.10.1 M5)
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

        /// <summary>
        /// Watches the first 10 rendered frames after the hand-off: the pause must hold (no FixedUpdate while the menu
        /// is up). If something sets timeScale back while the menu is open, stop it again once and log.
        /// Bounded: 10 frames, ends at once if the player resumes.
        /// </summary>
        private static IEnumerator GuardCo(int g)
        {
            int t0 = FixedTicks;
            for (int i = 0; i < 10; i++)
            {
                yield return null;
                if (g != _gen) yield break;
                if (!GameplayUI.GameIsPaused) { Active = false; Log("guard: resumed after " + (i + 1) + " frame(s) simTicksWhilePaused=" + (FixedTicks - t0) + " guardHits=" + GuardHits); yield break; }
                if (Time.timeScale > 0f) GuardHit("frame+" + (i + 1)); // every frame (P1)
            }
            Log("guard: pause held 10 frames simTicksWhilePaused=" + (FixedTicks - t0) + " guardHits=" + GuardHits);
        }

        /// <summary>Called from the ResumeGame postfix.</summary>
        internal static void OnResume()
        {
            if (!Active) return;
            Active = false;
            Log("resume: sim time starts now (hold/probe/blend, watchdog, monitor, VERIFY run on sim time from here)");
        }
    }
}
