using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace NOSaveSystem
{
    /// <summary>
    /// MissionReload (v0.8.5): true mission re-entry with vanilla loading flow.
    /// Order (Ovalicus + Foggy / Mirage, not FishNet):
    /// 1) LoadingScreen.Show (optional UX) + block via busy
    /// 2) Tear down: Stop(false) + wait !stopping / Menu (vanilla leave; no DestroyImmediate of scene NOs)
    /// 3) Wait NetworkManager ready -> MissionSaveLoad.TryLoad -> SetMission -> StartHost(Offline, SinglePlayer, MapKey)
    /// 4) Gate: Host_SceneLoaded + StartMission + OnStartLocalPlayer + !IsLoadingScene + IsRunning (soft-bypass only after scene signal)
    /// 5) NetworkPause -> despawn mission-default mobiles -> spawn/apply snapshot (Swap core) -> settle -> unpause
    /// 6) HideLoadingScreen + MessageUI "Restored"
    /// Swap remains LoadMode=Swap fallback when MissionReload fails or is disabled.
    /// </summary>
    internal static class MissionReload
    {
        private static bool _startMissionSeen;
        private static bool _localPlayerReady;
        private static float _localPlayerReadyAt;
        private static bool _hostSceneLoaded; // NetworkManagerNuclearOption.Host_SceneLoaded postfix
        private static bool _sawLoadingScene; // saw IsLoadingScene=true after StartHost
        private static FieldInfo _stoppingField; // cached NetworkManagerNuclearOption.stopping
        private static bool _stoppingFieldLooked; // v0.9.1: cache the MISS too (was re-reflected every frame in wait loops)

        /// <summary>Last RunMissionReloadCo outcome for Continue/F6 fallback decisions.</summary>
        internal static bool LastOk { get; private set; }
        internal static bool LastHostStarted { get; private set; }
        internal static bool LastMissionRunning { get; private set; }
        internal static string LastFailReason { get; private set; }
        internal static bool LastRefusedReentrant { get; private set; }
        /// <summary>v0.10.1 (H1): true when the run was rejected during preflight (before any tear-down); nothing changed.</summary>
        internal static bool LastPreflightFail { get; private set; }
        private static bool _running;
        internal static bool IsRunning { get { return _running; } }

        private const float TearDownTimeoutSec = 45f;
        private const float NetworkReadyTimeoutSec = 30f;
        private const float HostReadyTimeoutSec = 60f; // v0.9.9: was 180s
        private const float PostReadySettleSec = 0.5f; // v0.9.9: was 1.5s
        // Soft bypass only after these floors if postfix missed (Foggy timing).
        private const float StartMissionBypassSec = 5f;
        private const float LocalPlayerBypassSec = 12f;

        internal static void NotifyStartMission()
        {
            _startMissionSeen = true;
            try { NoSaveDeepState.ResetClock(); } catch { }
            try { NoSaveV0910.OnMissionStart(); } catch { }
            NOSaveSystemPlugin.Log?.LogInfo("MissionReload gate: MissionManager.StartMission seen");
        }

        internal static void NotifyLocalPlayerReady()
        {
            _localPlayerReady = true;
            try { _localPlayerReadyAt = Time.unscaledTime; } catch { _localPlayerReadyAt = 0f; }
            NOSaveSystemPlugin.Log?.LogInfo("MissionReload gate: Player.OnStartLocalPlayer seen");
        }

        internal static void NotifyHostSceneLoaded()
        {
            _hostSceneLoaded = true;
            NOSaveSystemPlugin.Log?.LogInfo("MissionReload gate: NetworkManagerNuclearOption.Host_SceneLoaded seen");
        }

        internal static void ResetGates()
        {
            _startMissionSeen = false;
            _localPlayerReady = false;
            _localPlayerReadyAt = 0f;
            _hostSceneLoaded = false;
            _sawLoadingScene = false;
        }

        internal static void TryShowLoadingScreen()
        {
            try
            {
                // Instance API (Cecil): GetLoadingScreen() then ShowLoadingScreen(Sprite).
                // Ready-gate remains StartMission + OnStartLocalPlayer - LoadingScreen is UX only.
                var ls = LoadingScreen.GetLoadingScreen();
                if (ls != null)
                {
                    ls.ShowLoadingScreen(null);
                    NOSaveSystemPlugin.Log?.LogInfo("MissionReload: LoadingScreen.Show");
                }
                else
                    NOSaveSystemPlugin.Log?.LogInfo("MissionReload: LoadingScreen.GetLoadingScreen() null (UX skip)");
            }
            catch (Exception ex)
            {
                NOSaveSystemPlugin.Log?.LogInfo("MissionReload LoadingScreen.Show skipped: " + ex.Message);
            }
        }

        internal static void TryHideLoadingScreen()
        {
            try
            {
                var ls = LoadingScreen.GetLoadingScreen();
                if (ls != null)
                {
                    ls.HideLoadingScreen();
                    NOSaveSystemPlugin.Log?.LogInfo("MissionReload: LoadingScreen.Hide");
                }
            }
            catch (Exception ex)
            {
                NOSaveSystemPlugin.Log?.LogInfo("MissionReload LoadingScreen.Hide skipped: " + ex.Message);
            }
        }

        internal static void TrySetPaused(bool paused)
        {
            try
            {
                var np = NetworkPause.i;
                if (np != null)
                {
                    np.NetworkserverPaused = paused;
                    NOSaveSystemPlugin.Log?.LogInfo("MissionReload: NetworkPause=" + paused);
                }
                else
                    NOSaveSystemPlugin.Log?.LogInfo("MissionReload: NetworkPause.i null (skip set " + paused + ")");
            }
            catch (Exception ex)
            {
                NOSaveSystemPlugin.Log?.LogInfo("MissionReload NetworkPause set " + paused + " skipped: " + ex.Message);
            }
        }

        /// <summary>
        /// SP hard leave mirroring QuitMissionButton.QuitGame host path without disconnect reason spam:
        /// Stop(false), wait stopping clear / Menu. Does not use EndGameOutcome (soft end / leaderboard).
        /// </summary>
        internal static IEnumerator TearDownToMenu(StringBuilder plan, float timeoutSec = TearDownTimeoutSec)
        {
            plan?.AppendLine("  MR tear-down: Stop(false) + wait Menu");
            NOSaveSystemPlugin.Log?.LogInfo("MissionReload: tear-down begin");
            bool needStop = false;
            try
            {
                var gs = GameManager.gameState;
                needStop = gs == GameState.SinglePlayer || gs == GameState.Multiplayer || MissionManager.IsRunning;
            }
            catch { needStop = true; }

            if (!needStop)
            {
                plan?.AppendLine("  MR tear-down: already not in mission (skip Stop)");
                NOSaveSystemPlugin.Log?.LogInfo("MissionReload: tear-down skip (not in mission)");
                yield break;
            }

            try
            {
                // Resume pause UI if open so Stop is not fought by pause overlay (QuitGame does this).
                try
                {
                    if (GameplayUI.i != null) GameplayUI.i.ResumeGame();
                }
                catch { }

                var nm = NetworkManagerNuclearOption.i;
                if (nm != null)
                {
                    // Foggy: hard leave Stop(false). HostEndedMessage is for kicking remotes; SP offline skip.
                    nm.Stop(false);
                    plan?.AppendLine("  MR Stop(false) issued");
                    NOSaveSystemPlugin.Log?.LogInfo("MissionReload: Stop(false) issued");
                }
                else
                {
                    plan?.AppendLine("  MR WARN no NetworkManagerNuclearOption - SetGameState(Menu) only");
                    NOSaveSystemPlugin.Log?.LogWarning("MissionReload: NM.i null during tear-down; SetGameState(Menu)");
                    try { GameManager.SetGameState(GameState.Menu); } catch { }
                }
            }
            catch (Exception ex)
            {
                plan?.AppendLine("  MR Stop FAIL: " + ex.Message);
                NOSaveSystemPlugin.Log?.LogWarning("MissionReload Stop: " + ex);
                try { GameManager.SetGameState(GameState.Menu); } catch { }
            }

            float waited = 0f;
            while (waited < timeoutSec)
            {
                bool stopping = IsNetworkStopping();
                bool loading = false;
                bool running = false;
                GameState gs = GameState.Uninitialized;
                try { loading = NetworkManagerNuclearOption.IsLoadingScene; } catch { }
                try { running = MissionManager.IsRunning; } catch { }
                try { gs = GameManager.gameState; } catch { }

                if (!stopping && !loading && !running && gs == GameState.Menu)
                {
                    plan?.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  MR tear-down settled Menu after {0:F1}s", waited));
                    NOSaveSystemPlugin.Log?.LogInfo(string.Format(CultureInfo.InvariantCulture,
                        "MissionReload: tear-down settled Menu after {0:F1}s", waited));
                    yield break;
                }

                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            plan?.AppendLine("  MR tear-down TIMEOUT - forcing SetGameState(Menu); stopping=" + IsNetworkStopping());
            NOSaveSystemPlugin.Log?.LogWarning("MissionReload: tear-down TIMEOUT after " + timeoutSec + "s - forcing Menu");
            NoSaveWait.Timeout("tear-down", timeoutSec, "GameManager.SetGameState(Menu)");
            try { GameManager.SetGameState(GameState.Menu); } catch { }
            // Extra frames so Menu transition can start before NetworkReady wait.
            yield return null;
            yield return null;
            yield return null;
        }

        /// <summary>
        /// After Stop/Menu, wait until NetworkManagerNuclearOption.i is usable and not stopping.
        /// Prevents StartHost NRE / start-while-stopping races.
        /// </summary>
        private static IEnumerator WaitNetworkManagerReady(StringBuilder plan, float timeoutSec = NetworkReadyTimeoutSec)
        {
            plan?.AppendLine("  MR wait NetworkManager ready");
            NOSaveSystemPlugin.Log?.LogInfo("MissionReload: wait NetworkManager ready");
            float waited = 0f;
            while (waited < timeoutSec)
            {
                bool stopping = IsNetworkStopping();
                bool loading = false;
                NetworkManagerNuclearOption nm = null;
                GameState gs = GameState.Uninitialized;
                try { nm = NetworkManagerNuclearOption.i; } catch { }
                try { loading = NetworkManagerNuclearOption.IsLoadingScene; } catch { }
                try { gs = GameManager.gameState; } catch { }

                if (nm != null && !stopping && !loading &&
                    (gs == GameState.Menu || gs == GameState.Uninitialized))
                {
                    plan?.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  MR NetworkManager ready after {0:F1}s gs={1}", waited, gs));
                    NOSaveSystemPlugin.Log?.LogInfo(string.Format(CultureInfo.InvariantCulture,
                        "MissionReload: NetworkManager ready after {0:F1}s gs={1}", waited, gs));
                    yield break;
                }

                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            NoSaveWait.Timeout("network-ready", timeoutSec, "continue; StartHost is refused below if NM is null or still stopping");
            var nmFinal = NetworkManagerNuclearOption.i;
            plan?.AppendLine("  MR WARN NetworkManager ready TIMEOUT nmNull=" + (nmFinal == null) +
                             " stopping=" + IsNetworkStopping());
            NOSaveSystemPlugin.Log?.LogWarning("MissionReload: NetworkManager ready TIMEOUT (nmNull=" +
                (nmFinal == null) + " stopping=" + IsNetworkStopping() + ")");
        }

        private static bool IsNetworkStopping()
        {
            try
            {
                var nm = NetworkManagerNuclearOption.i;
                if (nm == null) return false;
                if (!_stoppingFieldLooked)
                {
                    _stoppingFieldLooked = true;
                    _stoppingField = typeof(NetworkManagerNuclearOption).GetField("stopping",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                }
                if (_stoppingField != null) return (bool)_stoppingField.GetValue(nm);
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Full MissionReload coroutine. Caller holds long-busy. Falls back to false so caller can Swap.
        /// </summary>
        internal static IEnumerator RunMissionReloadCo(bool alreadyAtMenu, System.Action<bool> onDone)
        {
            if (_running)
            {
                LastRefusedReentrant = true;
                NOSaveSystemPlugin.Log?.LogWarning("MissionReload: REFUSED - already running (double invocation)");
                // Do not touch LastOk/LastFailReason of the active run.
                try { onDone?.Invoke(false); } catch { }
                yield break;
            }
            _running = true;
            LastRefusedReentrant = false;
            LastPreflightFail = false;
            // Safe: a throw inside the body no longer strands _running / pause / busy (body finally still runs).
            try { yield return NoSaveCoroutines.Safe(RunMissionReloadCoBody(alreadyAtMenu, onDone), "MissionReload"); }
            finally { _running = false; }
        }

        private static IEnumerator RunMissionReloadCoBody(bool alreadyAtMenu, System.Action<bool> onDone)
        {
            var plan = new StringBuilder();
            plan.AppendLine("MissionReload v" + NOSaveSystemPlugin.PLUGIN_VERSION + " alreadyAtMenu=" + alreadyAtMenu);
            NOSaveSystemPlugin.Log?.LogInfo("MissionReload: BEGIN alreadyAtMenu=" + alreadyAtMenu);
            NoSaveV093.BeginSuppressJoinUi();
            bool ok = false;
            bool pausedSet = false;
            bool hostStarted = false;
            LastOk = false;
            LastHostStarted = false;
            LastMissionRunning = false;
            LastFailReason = null;
            LastPreflightFail = false;
            NoSaveV093.LoadPauseArmed = false; // v0.10.1 (M5): a new load supersedes any pending post-load reassert
            ResetGates();
            TryShowLoadingScreen();

            try
            {
                var plug = NOSaveSystemPlugin.Instance;
                if (plug == null)
                {
                    plan.AppendLine("  FAIL plugin null"); LastFailReason = "plugin_null"; LastPreflightFail = true;
                    NOSaveSystemPlugin.Log?.LogError("MissionReload: plugin Instance null");
                    yield break;
                }

                string savePath = plug.ResolveLoadPath(); // v0.10.0: save-menu slot or autosave
                if (!File.Exists(savePath))
                {
                    plan.AppendLine("  FAIL no save at " + savePath); LastFailReason = "no_save"; LastPreflightFail = true;
                    NOSaveSystemPlugin.Log?.LogWarning("MissionReload: no save at " + savePath);
                    NoSaveUi.SetStatus("NOSave MissionReload: no midmission_autosave.json");
                    yield break;
                }

                MidMissionSnapshot snap;
                try
                {
                    snap = SnapshotJson.Deserialize(File.ReadAllText(savePath, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    plan.AppendLine("  FAIL parse: " + ex.Message); LastFailReason = "parse"; LastPreflightFail = true;
                    NOSaveSystemPlugin.Log?.LogWarning("MissionReload: parse FAIL " + ex.Message);
                    yield break;
                }
                if (snap == null || snap.mission == null)
                {
                    plan.AppendLine("  FAIL empty snapshot/mission"); LastFailReason = "empty_snap"; LastPreflightFail = true;
                    NOSaveSystemPlugin.Log?.LogWarning("MissionReload: empty snapshot/mission");
                    yield break;
                }

                // --- 1b) v0.10.1 (H1): PREFLIGHT - validate everything that can be checked while the current mission
                // still runs. Any failure aborts here with a status message; the mission is NOT torn down.
                {
                    string pfName = snap.mission.loadKeyName ?? snap.mission.name;
                    string pfKey = snap.mission.loadKey ?? pfName;
                    string pfFail = null, pfWhy = null;
                    if (string.IsNullOrEmpty(pfName)) { pfFail = "missing_key"; pfWhy = "save has no mission key"; }
                    else if (snap.manifest == null || string.IsNullOrEmpty(snap.manifest.utc)) { pfFail = "no_manifest"; pfWhy = "save has no manifest timestamp (damaged or foreign file)"; }
                    else
                    {
                        Mission pfMission = null; string pfErr = null;
                        try { pfMission = NoSaveUi.TryLoadMissionByKeyPublic(pfName, pfKey, snap.mission.loadKeyGroup, out pfErr); }
                        catch (Exception ex) { pfErr = ex.Message; }
                        if (pfMission == null) { pfFail = "tryload"; pfWhy = "mission '" + pfName + "' not found (" + (pfErr ?? "?") + ")"; }
                    }
                    if (pfFail != null)
                    {
                        plan.AppendLine("  PREFLIGHT FAIL " + pfFail + ": " + pfWhy + " - nothing torn down");
                        LastFailReason = pfFail; LastPreflightFail = true;
                        NOSaveSystemPlugin.Log?.LogWarning("MissionReload: preflight FAIL " + pfFail + ": " + pfWhy + " (current mission left running)");
                        NoSaveUi.SetStatus("NOSave: cannot load this save - " + pfWhy, 8f);
                        yield break;
                    }
                    plan.AppendLine("  PREFLIGHT ok mission='" + pfName + "' manifest.utc=" + snap.manifest.utc);
                }
                // v0.10.2e (N4): new engine-hold generation only once the preflight passed (a failed preflight leaves the
                // running mission, and its holds, untouched)
                try { NoSaveEngineGear.NewLoad("MissionReload"); } catch { }

                // --- 2) Tear down if in-mission ---
                if (!alreadyAtMenu)
                {
                    NoSaveLoadTime.Begin();
                    yield return TearDownToMenu(plan);
                }
                else
                {
                    plan.AppendLine("  MR skip tear-down (already menu / Continue)");
                    NOSaveSystemPlugin.Log?.LogInfo("MissionReload: skip tear-down (alreadyAtMenu)");
                    // Continue: confirm Menu + not stopping before host (avoid half-state StartHost).
                    float menuWait = 0f;
                    const float menuVerifySec = 8f;
                    while (menuWait < menuVerifySec)
                    {
                        bool stopping = IsNetworkStopping();
                        bool loading = false;
                        bool running = false;
                        GameState gs = GameState.Uninitialized;
                        try { loading = NetworkManagerNuclearOption.IsLoadingScene; } catch { }
                        try { running = MissionManager.IsRunning; } catch { }
                        try { gs = GameManager.gameState; } catch { }
                        if (!stopping && !loading && !running && gs == GameState.Menu)
                        {
                            plan.AppendLine(string.Format(CultureInfo.InvariantCulture,
                                "  MR Continue menu verified after {0:F1}s", menuWait));
                            break;
                        }
                        menuWait += Time.unscaledDeltaTime;
                        yield return null;
                    }
                    if (menuWait >= menuVerifySec) NoSaveWait.Timeout("continue-menu-verify", menuVerifySec, "continue to NetworkManager-ready wait");
                }

                // --- 2b) Ensure NetworkManager usable before TryLoad/StartHost ---
                NoSaveLoadTime.Lap("teardown");
                yield return WaitNetworkManagerReady(plan);
                NoSaveLoadTime.Lap("netReady");

                // --- 3) TryLoad -> SetMission -> StartHost ---
                string loadName = snap.mission.loadKeyName ?? snap.mission.name;
                string loadKey = snap.mission.loadKey ?? loadName;
                if (string.IsNullOrEmpty(loadName))
                {
                    plan.AppendLine("  FAIL save missing loadKey/name"); LastFailReason = "missing_key";
                    NoSaveUi.SetStatus("NOSave MissionReload: save missing mission key");
                    yield break;
                }

                Mission mission = null;
                string lastErr = null;
                try
                {
                    mission = NoSaveUi.TryLoadMissionByKeyPublic(loadName, loadKey, snap.mission.loadKeyGroup, out lastErr);
                }
                catch (Exception ex) { lastErr = ex.Message; }

                if (mission == null)
                {
                    plan.AppendLine("  FAIL TryLoad '" + loadName + "': " + lastErr); LastFailReason = "tryload";
                    NOSaveSystemPlugin.Log?.LogWarning("MissionReload: TryLoad FAIL '" + loadName + "': " + lastErr);
                    NoSaveUi.SetStatus("NOSave MissionReload: could not load mission '" + loadName + "'");
                    yield break;
                }

                var nmHost = NetworkManagerNuclearOption.i;
                if (nmHost == null)
                {
                    plan.AppendLine("  FAIL NetworkManagerNuclearOption.i null before StartHost"); LastFailReason = "nm_null";
                    NOSaveSystemPlugin.Log?.LogError("MissionReload: NM.i null before StartHost - cannot host");
                    NoSaveUi.SetStatus("NOSave MissionReload: NetworkManager missing - try Swap/manual");
                    yield break;
                }
                if (IsNetworkStopping())
                {
                    plan.AppendLine("  FAIL NetworkManager still stopping before StartHost"); LastFailReason = "nm_stopping";
                    NOSaveSystemPlugin.Log?.LogError("MissionReload: NM still stopping before StartHost");
                    NoSaveUi.SetStatus("NOSave MissionReload: network still stopping - retry later");
                    yield break;
                }

                ResetGates();
                try
                {
                    MissionManager.SetMission(mission, false);
                    var map = mission.MapKey;
                    var opts = new HostOptions(SocketType.Offline, GameState.SinglePlayer, map);
                    nmHost.StartHost(opts);
                    hostStarted = true;
                    LastHostStarted = true;
                    plan.AppendLine("  MR SetMission+StartHost '" + (mission.Name ?? loadName) + "' map=" + map);
                    NOSaveSystemPlugin.Log?.LogInfo("MissionReload: SetMission+StartHost '" +
                        (mission.Name ?? loadName) + "' map=" + map);
                    NoSaveUi.SetStatus("NOSave MissionReload: hosting '" + (mission.Name ?? loadName) + "'...", 12f);
                }
                catch (Exception ex)
                {
                    plan.AppendLine("  FAIL StartHost: " + ex.Message); LastFailReason = "starthost";
                    NOSaveSystemPlugin.Log?.LogError("MissionReload: StartHost FAIL " + ex);
                    NoSaveUi.SetStatus("NOSave MissionReload: StartHost failed - try Swap/manual");
                    yield break;
                }

                // --- 4) Hard wait: StartMission + local player + IsRunning + units ---
                float waited = 0f;
                bool gateOk = false;
                bool bypassedStartMission = false;
                bool bypassedLocalPlayer = false;
                while (waited < HostReadyTimeoutSec)
                {
                    bool running = false;
                    bool hasMission = false;
                    bool loading = false;
                    int acCount = 0;
                    try { running = MissionManager.IsRunning; } catch { }
                    try { hasMission = MissionManager.CurrentMission != null; } catch { }
                    try { loading = NetworkManagerNuclearOption.IsLoadingScene; } catch { }
                    try { if (UnitRegistry.allAircraft != null) acCount = UnitRegistry.allAircraft.Count; } catch { }
                    if (loading) _sawLoadingScene = true;

                    // Also accept GameManager.GetLocalPlayer as local-ready if OnStartLocalPlayer postfix missed.
                    bool localPresent = false;
                    try { localPresent = MidMissionV03Helpers.FindLocalPlayer() != null; } catch { }
                    if (localPresent && !_localPlayerReady)
                    {
                        _localPlayerReady = true;
                        try { _localPlayerReadyAt = Time.unscaledTime; } catch { }
                        plan.AppendLine("  MR local player detected via FindLocalPlayer (postfix miss recovery)");
                    }

                    // Cecil: Host_SceneLoaded sets SceneIsReady + SpawnCharacter - prefer over soft timeouts (R1/R2).
                    bool sceneOk = _hostSceneLoaded || (_sawLoadingScene && !loading) || (waited > LocalPlayerBypassSec && !loading);
                    bool startOk = _startMissionSeen || (sceneOk && waited > StartMissionBypassSec);
                    // Do not soft-bypass localPlayer until scene-load signal (Host_SceneLoaded / !IsLoadingScene).
                    bool localOk = _localPlayerReady || (sceneOk && waited > LocalPlayerBypassSec);
                    // v0.9.9 HANG FIX: Free Flight / empty missions have NO pre-placed aircraft, so "acCount > 0" never became
                    // true and the load sat in this loop for the full 180s timeout. Aircraft are now optional after 2s.
                    bool acOk = acCount > 0 || (_startMissionSeen && sceneOk && waited > 2f);
                    bool gate = running && hasMission && !loading && acOk && startOk && localOk && sceneOk;

                    if (gate)
                    {
                        bypassedStartMission = !_startMissionSeen;
                        bypassedLocalPlayer = !_localPlayerReady;
                        bool bypassedHostScene = !_hostSceneLoaded;
                        plan.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "  MR ready after {0:F1}s startMission={1} localPlayer={2} hostScene={3} sawLoading={4} aircraft={5} bypassSM={6} bypassLP={7} bypassHS={8}",
                            waited, _startMissionSeen, _localPlayerReady, _hostSceneLoaded, _sawLoadingScene, acCount,
                            bypassedStartMission, bypassedLocalPlayer, bypassedHostScene));
                        NOSaveSystemPlugin.Log?.LogInfo(string.Format(CultureInfo.InvariantCulture,
                            "MissionReload: ready after {0:F1}s startMission={1} localPlayer={2} hostScene={3} aircraft={4} bypassSM={5} bypassLP={6} bypassHS={7}",
                            waited, _startMissionSeen, _localPlayerReady, _hostSceneLoaded, acCount,
                            bypassedStartMission, bypassedLocalPlayer, bypassedHostScene));
                        if (bypassedStartMission)
                            NOSaveSystemPlugin.Log?.LogWarning("MissionReload: StartMission postfix missed - soft-bypassed after " + StartMissionBypassSec + "s");
                        if (bypassedLocalPlayer)
                            NOSaveSystemPlugin.Log?.LogWarning("MissionReload: OnStartLocalPlayer missed - soft-bypassed after " + LocalPlayerBypassSec + "s (sceneOk)");
                        if (bypassedHostScene)
                            NOSaveSystemPlugin.Log?.LogWarning("MissionReload: Host_SceneLoaded postfix missed - used !IsLoadingScene / timeout");
                        gateOk = true;
                        NoSaveLoadTime.Lap("hostGate");
                        break;
                    }

                    float prevW = waited;
                    waited += Time.unscaledDeltaTime;
                    if ((int)(waited / 5f) != (int)(prevW / 5f))
                        NOSaveSystemPlugin.Log?.LogInfo(string.Format(CultureInfo.InvariantCulture,
                            "MissionReload: gate waiting {0:F0}s running={1} mission={2} loading={3} aircraft={4} start={5} local={6} scene={7}",
                            waited, running, hasMission, loading, acCount, _startMissionSeen, _localPlayerReady, _hostSceneLoaded));
                    yield return null;
                }

                if (!gateOk)
                {
                    NoSaveWait.Timeout("host-gate", HostReadyTimeoutSec, "proceed only if MissionManager.IsRunning, else abort");
                    plan.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  MR gate TIMEOUT after {0:F1}s startMission={1} localPlayer={2} IsRunning={3}",
                        waited, _startMissionSeen, _localPlayerReady, SafeIsRunning()));
                    NOSaveSystemPlugin.Log?.LogWarning(string.Format(CultureInfo.InvariantCulture,
                        "MissionReload: gate TIMEOUT after {0:F1}s startMission={1} localPlayer={2} IsRunning={3}",
                        waited, _startMissionSeen, _localPlayerReady, SafeIsRunning()));
                }

                if (!SafeIsRunning())
                {
                    plan.AppendLine("  FAIL mission not running after StartHost wait"); LastFailReason = "not_running";
                    NOSaveSystemPlugin.Log?.LogError("MissionReload: mission not running after StartHost wait - abort (caller may Swap-fallback)");
                    NoSaveUi.SetStatus("NOSave MissionReload: mission did not start - fallback Swap/manual");
                    yield break;
                }

                if (!gateOk)
                {
                    // IsRunning but gates incomplete - proceed cautiously (better than abort mid-host).
                    plan.AppendLine("  MR WARN proceeding with incomplete gates (IsRunning=true)");
                    NOSaveSystemPlugin.Log?.LogWarning("MissionReload: proceeding with incomplete gates (IsRunning=true)");
                }

                // Extra settle if local-player gate was soft/recovered - give Attach/spawn a beat.
                float settleTarget = PostReadySettleSec;
                if (bypassedLocalPlayer || !_localPlayerReady)
                    settleTarget = PostReadySettleSec + 1.5f;

                // Brief settle so default spawns finish before we despawn them
                float settle = 0f;
                bool settleEarly = false;
                while (settle < settleTarget)
                {
                    if (!bypassedLocalPlayer && _localPlayerReady && MidMissionV03Helpers.FindLocalPlayer() != null && settle > 0.25f) { settleEarly = true; break; } // v0.9.9 condition-based
                    settle += Time.unscaledDeltaTime;
                    yield return null;
                }
                if (!settleEarly) NoSaveWait.Timeout("post-ready-settle", settleTarget, "continue (local player " + (_localPlayerReady ? "ready" : "not confirmed") + ")");

                // --- 5) Pause -> apply snapshot (Swap core despawns defaults that match save, spawns from JSON) ---
                TrySetPaused(true);
                pausedSet = true;
                NoSavePause0.MarkApplyStart();
                plan.AppendLine("  MR NetworkPause=true; applying snapshot via Swap core");
                NoSaveUi.SetStatus("NOSave MissionReload: applying snapshot...", 8f);

                // Despawn mission-default mobiles; wait saved UniqueNames leave registry (R3 double-despawn race).
                NoSaveV0910.NoAircraftLoad = NoSaveV0910.IsNoAircraft(snap);
                plan.AppendLine("  PLAYER saved state=" + (snap.playerState ?? "(pre-0.9.10)") + " noAircraftLoad=" + NoSaveV0910.NoAircraftLoad);
                try { NoSaveDeepState.CompleteObjectivesEarly(snap, plan); } catch (Exception oex) { plan.AppendLine("  OBJECTIVES early FAIL: " + oex.Message); }
                NoSaveLoadTime.Lap("settle");
                yield return DespawnAllDefaultMobiles(plan, snap);
                NoSaveLoadTime.Lap("despawn");
                yield return WaitSavedUniqueNamesCleared(snap, plan);
                NoSaveLoadTime.Lap("nameSettle");

                // v0.10.1 (H2): VERIFIED purge - wait until no old mobile GameObject survives (late default spawns are
                // despawned too); never spawn the snapshot on top of survivors (ghost COIN_2-style duplicates).
                {
                    var pr = new NoSaveReconcile.PurgeResult();
                    Player pLocal = null;
                    try { pLocal = MidMissionV03Helpers.FindLocalPlayer(); } catch { }
                    yield return NoSaveReconcile.PurgeMobilesVerified("MR-defaults", pLocal, null, plan, pr, 10f);
                    NoSaveLoadTime.Lap("purgeVerified");
                    if (!pr.ok)
                    {
                        plan.AppendLine("  FAIL verified purge: " + pr.after); LastFailReason = "purge_unverified";
                        NOSaveSystemPlugin.Log?.LogError("MissionReload: verified purge FAILED (" + pr.after + ") - snapshot NOT applied");
                        NoSaveUi.SetStatus("NOSave MissionReload: old units did not despawn - restore aborted (see log)", 8f);
                        yield break;
                    }
                }

                // Free UniqueNames from JSON so Swap spawn reuses saved ids after despawn.
                try { MidMissionV03Helpers.ClearSavedUniqueNameReservations(snap, plan); } catch (Exception ex) { plan.AppendLine("  MR clear UniqueNames FAIL: " + ex.Message); }
                try { NoSaveV093.EnsurePlayerFaction(snap, plan); } catch (Exception fex) { plan.AppendLine("  FACTION ensure FAIL: " + fex.Message); }
                yield return plug.RestoreMidMissionSnapshotCoreCo();
                NoSaveLoadTime.Lap("spawnCore");
                if (!NOSaveSystemPlugin.LastCoreOk)
                {
                    // v0.9.1: core aborted (purge not verified), threw (swallowed by its Safe wrapper) or was refused.
                    // Previously MissionReload still reported "Restored" with ok=true.
                    plan.AppendLine("  FAIL snapshot core did not complete (see last_restore_plan.txt)"); LastFailReason = "core_failed";
                    NOSaveSystemPlugin.Log?.LogError("MissionReload: snapshot core did not complete - NOT reporting Restored");
                    NoSaveUi.SetStatus("NOSave MissionReload: snapshot apply failed (see log)", 8f);
                    yield break;
                }

                NoSavePause0.MarkSceneReady(); // v0.11.0 PAUSE0: scene ready, frame-0 state applied, still NetworkPaused
                yield return null; // Foggy/Ovalicus: one frame settle before unpause/hide (still paused: no sim tick)
                yield return null;

                // v0.11.0 PAUSE0: the remaining restore passes run BEFORE the unpause, so every saved state is in place
                // when the first simulated tick happens. They are time-independent (late references retry on their
                // own pause-aware coroutines).
                try { AuditHangars(plan); } catch (Exception hex) { plan.AppendLine("  HANGAR audit FAIL: " + hex.Message); }
                try { NoSaveV0910.RestoreDestroyedStatics(snap, plan); } catch (Exception sex) { plan.AppendLine("  STATICS restore FAIL: " + sex.Message); }
                NoSaveLoadTime.Lap("hangarAudit+statics");
                try { NoSaveV093.RestoreDatalink(snap, plan); } catch (Exception dex) { plan.AppendLine("  DATALINK restore FAIL: " + dex.Message); }
                NoSaveLoadTime.Lap("datalink");
                try { NoSaveDeepState.Restore(savePath, snap.manifest != null ? snap.manifest.utc : null, plan); } catch (Exception gex) { plan.AppendLine("  DEEP restore FAIL: " + gex.Message); }
                NoSaveLoadTime.Lap("deep");
                try { NoSaveV096.RestoreTargetsAndMissiles(snap, plan); } catch (Exception tex) { plan.AppendLine("  TARGETS restore FAIL: " + tex.Message); }
                NoSaveLoadTime.Lap("targetsAmmo");
                try { NoSaveV093.EnsurePlayerFaction(snap, plan); } catch { }

                // v0.11.0 PAUSE0: unpause NetworkPause and open the game's pause menu in the same frame (zero sim ticks).
                if (NoSaveV0910.NoAircraftLoad)
                {
                    TrySetPaused(false); pausedSet = false;
                    plan.AppendLine("  PAUSE skipped: no player aircraft in save -> native Join/respawn menu instead");
                }
                else if (!NOSaveSystemPlugin.PauseAfterLoad)
                {
                    TrySetPaused(false); pausedSet = false;
                    plan.AppendLine("  PAUSE after load disabled by config (PAUSE0 off)");
                }
                else
                {
                    bool p0 = NoSavePause0.HandOff(plan, snap); // always clears NetworkPause; captures frame-0 economy/blackboard/ammo
                    pausedSet = false;
                    if (!p0) NoSaveV093.SchedulePauseAfterLoad(plan); // fallback: legacy delayed pause
                }

                try
                {
                    MidMissionV03Helpers.ShowGameFeedback("NOSave MissionReload: Restored");
                }
                catch { }
                NoSaveUi.SetStatus("NOSave MissionReload: Restored", 5f);
                try { NoSaveVerify.Arm(snap, "MissionReload"); } catch (Exception vex) { plan.AppendLine("  VERIFY arm FAIL: " + vex.Message); }
                plan.AppendLine("  MR DONE Restored");
                NoSaveLoadTime.Finish(plan);
                NOSaveSystemPlugin.Log?.LogInfo("MissionReload: DONE Restored");
                ok = true;
            }
            finally
            {
                // Always clear pause if we set it (exception / early yield-break after pause).
                if (pausedSet)
                {
                    TrySetPaused(false);
                    plan.AppendLine("  MR finally: forced NetworkPause=false");
                }
                TryHideLoadingScreen();
                NoSaveV093.EndSuppressJoinUiSoon(ok);
                LastOk = ok;
                LastHostStarted = hostStarted;
                LastMissionRunning = SafeIsRunning();
                if (!ok && string.IsNullOrEmpty(LastFailReason))
                    LastFailReason = LastMissionRunning ? "incomplete_gates" : "failed";
                plan.AppendLine("  MR outcome ok=" + ok + " hostStarted=" + hostStarted +
                    " running=" + LastMissionRunning + " fail=" + (LastFailReason ?? "-"));
                try
                {
                    var planPath = Path.Combine(
                        NOSaveSystemPlugin.Instance != null
                            ? NOSaveSystemPlugin.Instance.SaveDirectory
                            : Path.Combine(BepInEx.Paths.BepInExRootPath, "NOSaveSystem", "saves"),
                        "last_missionreload_plan.txt");
                    File.WriteAllText(planPath, plan.ToString(), Encoding.UTF8);
                }
                catch { }
                NOSaveSystemPlugin.Log?.LogInfo(plan.ToString());
                // v0.9.1: a throwing callback inside finally would abort the rest of the unwind.
                try { onDone?.Invoke(ok); }
                catch (Exception cbEx) { NOSaveSystemPlugin.Log?.LogWarning("MissionReload onDone callback: " + cbEx.Message); }
            }
        }

        private static bool SafeIsRunning()
        {
            try { return MissionManager.IsRunning; } catch { return false; }
        }

        /// <summary>
        /// After StartHost, mission definition spawns its default unit set. Despawn all mobiles
        /// so snapshot apply does not double with leftovers (Ovalicus trap). Statics skipped.
        /// Logs how many live UniqueNames overlap the save set (match vs extras).
        /// </summary>
        private static IEnumerator DespawnAllDefaultMobiles(StringBuilder plan, MidMissionSnapshot snap)
        {
            var savedNames = BuildSavedUniqueNameSet(snap);
            plan?.AppendLine("  MR despawn-defaults (pre-snapshot) savedUniqueNames=" + savedNames.Count);
            NOSaveSystemPlugin.Log?.LogInfo("MissionReload: despawn-defaults begin savedUniqueNames=" + savedNames.Count);
            Player localPlayer = null;
            try { localPlayer = MidMissionV03Helpers.FindLocalPlayer(); } catch { }
            int n = 0;

            try
            {
                if (UnitRegistry.allAircraft != null)
                {
                    var list = new List<Aircraft>();
                    foreach (var ac in UnitRegistry.allAircraft)
                        if (ac != null) list.Add(ac);
                    foreach (var ac in list)
                    {
                        try
                        {
                            if (MidMissionV03Helpers.DespawnUnitForSwap(ac, localPlayer, plan)) n++;
                        }
                        catch (Exception ex)
                        {
                            plan?.AppendLine("  MR despawn one aircraft FAIL: " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex) { plan?.AppendLine("  MR despawn aircraft FAIL: " + ex.Message); }

            try
            {
                // Ground vehicles / ships via allUnits if available
                if (UnitRegistry.allUnits != null)
                {
                    var list = new List<Unit>();
                    foreach (var u in UnitRegistry.allUnits)
                    {
                        if (u == null) continue;
                        if (u is Aircraft) continue; // already done
                        string kind = null;
                        try { kind = u.GetType().Name; } catch { kind = "?"; }
                        if (MidMissionV03Helpers.IsStaticUnitKind(kind, u)) continue;
                        if (!MidMissionV03Helpers.IsSwapSafeMobileOther(kind, u) &&
                            !(u is GroundVehicle) && !(u is Ship))
                            continue;
                        list.Add(u);
                    }
                    foreach (var u in list)
                    {
                        try
                        {
                            if (MidMissionV03Helpers.DespawnUnitForSwap(u, localPlayer, plan)) n++;
                        }
                        catch (Exception ex)
                        {
                            plan?.AppendLine("  MR despawn one unit FAIL: " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex) { plan?.AppendLine("  MR despawn units FAIL: " + ex.Message); }

            int matched = 0;
            try
            {
                if (savedNames.Count > 0 && UnitRegistry.customIDLookup != null)
                {
                    foreach (var name in savedNames)
                    {
                        if (string.IsNullOrEmpty(name)) continue;
                        if (UnitRegistry.customIDLookup.ContainsKey(name)) matched++;
                    }
                }
            }
            catch { }
            plan?.AppendLine("  MR despawn-defaults count=" + n + " saveNameStillInRegistry=" + matched);
            NOSaveSystemPlugin.Log?.LogInfo("MissionReload: despawn-defaults count=" + n + " saveNameStillInRegistry=" + matched);
            yield return null;
            yield return null;
        }

        /// <summary>v0.9.2: log per-airbase hangar availability after restore (diagnoses "no hangars").</summary>
        private static void AuditHangars(StringBuilder plan)
        {
            var bases = UnityEngine.Object.FindObjectsOfType<Airbase>();
            int total = 0, usable = 0;
            foreach (var ab in bases)
            {
                if (ab == null) continue;
                int h = 0, dis = 0;
                try
                {
                    if (ab.hangars != null)
                        foreach (var hg in ab.hangars) { if (hg == null) continue; h++; try { if (hg.Disabled) dis++; } catch { } }
                }
                catch { }
                string owner = "none";
                try { owner = ab.CurrentHQ != null && ab.CurrentHQ.faction != null ? ab.CurrentHQ.faction.factionName : "none"; } catch { }
                total += h; usable += h - dis;
                plan?.AppendLine("  HANGAR airbase=" + ab.name + " owner=" + owner + " hangars=" + h + " disabled=" + dis);
            }
            var all = UnityEngine.Object.FindObjectsOfType<Hangar>();
            string msg = "airbases=" + bases.Length + " hangarsInLists=" + total + " usable=" + usable + " hangarObjectsInScene=" + all.Length;
            plan?.AppendLine("  HANGAR audit " + msg);
            NOSaveSystemPlugin.Log?.LogInfo("MissionReload: hangar audit " + msg);
            if (usable == 0) NOSaveSystemPlugin.Log?.LogWarning("MissionReload: NO usable hangars after restore!");
        }

        private static HashSet<string> BuildSavedUniqueNameSet(MidMissionSnapshot snap)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (snap == null) return set;
            try
            {
                if (snap.aircraft != null)
                    foreach (var a in snap.aircraft)
                        if (a != null && !string.IsNullOrEmpty(a.private_uniqueName)) set.Add(a.private_uniqueName);
                if (snap.vehicles != null)
                    foreach (var g in snap.vehicles)
                        if (g != null && !string.IsNullOrEmpty(g.private_uniqueName)) set.Add(g.private_uniqueName);
                if (snap.ships != null)
                    foreach (var s in snap.ships)
                        if (s != null && !string.IsNullOrEmpty(s.private_uniqueName)) set.Add(s.private_uniqueName);
                // v0.9.2: only swap-safe mobile others (pilots/missiles). Static map units
                // (hangars, shelters, buildings, '<MAP_UNIT>++*') are never despawned/cleared:
                // v0.9.1 included them here, so the settle TIMEOUT path destroyed all hangars
                // ("no hangars" on respawn after load).
                if (snap.otherUnits != null)
                    foreach (var u in snap.otherUnits)
                        if (u != null && !string.IsNullOrEmpty(u.private_uniqueName)
                            && !MidMissionV03Helpers.IsProtectedStaticName(u.private_uniqueName)
                            && MidMissionV03Helpers.IsSwapSafeMobileOther(u.kind))
                            set.Add(u.private_uniqueName);
            }
            catch { }
            return set;
        }

        /// <summary>
        /// Wait until saved UniqueNames leave UnitRegistry.customIDLookup (deferred Destroy settle).
        /// Mitigates R3 double-despawn / UniqueName reservation races before Swap core.
        /// </summary>
        private static IEnumerator WaitSavedUniqueNamesCleared(MidMissionSnapshot snap, StringBuilder plan)
        {
            var saved = BuildSavedUniqueNameSet(snap);
            if (saved.Count == 0)
            {
                plan?.AppendLine("  MR UniqueName settle skip (no saved names)");
                yield return null;
                yield break;
            }
            // v0.8.5: 5s soft wait, then redeespawn holders + force-clear leftovers (R3).
            const float timeoutSec = 5.0f;
            float waited = 0f;
            int lastRemain = -1;
            while (waited < timeoutSec)
            {
                int remain = CountSavedNamesStillInRegistry(saved);
                if (remain == 0)
                {
                    plan?.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  MR UniqueName settle OK after {0:F2}s (0/{1} remain)", waited, saved.Count));
                    NOSaveSystemPlugin.Log?.LogInfo(string.Format(CultureInfo.InvariantCulture,
                        "MissionReload: UniqueName settle OK after {0:F2}s", waited));
                    yield return null;
                    yield break;
                }
                lastRemain = remain;
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            var stuck = ListSavedNamesStillInRegistry(saved);
            plan?.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  MR WARN UniqueName settle TIMEOUT {0:F1}s remain={1}/{2} - redeespawn+force-clear",
                timeoutSec, stuck.Count, saved.Count));
            NOSaveSystemPlugin.Log?.LogWarning("MissionReload: UniqueName settle TIMEOUT remain=" + stuck.Count);
            NoSaveWait.Timeout("uniquename-settle", timeoutSec, "re-despawn holders + force-clear reservations (remain=" + stuck.Count + ")");
            foreach (var name in stuck)
                plan?.AppendLine("    stuck UniqueName '" + name + "'");

            // Second despawn pass: destroy units still holding saved UniqueNames.
            int redeespawned = 0;
            Player localPlayer = null;
            try { localPlayer = MidMissionV03Helpers.FindLocalPlayer(); } catch { }
            foreach (var name in stuck)
            {
                if (string.IsNullOrEmpty(name)) continue;
                Unit holder = null;
                try
                {
                    if (UnitRegistry.customIDLookup != null)
                        UnitRegistry.customIDLookup.TryGetValue(name, out holder);
                }
                catch { holder = null; }
                if (holder == null) continue;
                try
                {
                    if (MidMissionV03Helpers.DespawnUnitForSwap(holder, localPlayer, plan))
                        redeespawned++;
                }
                catch (Exception ex)
                {
                    plan?.AppendLine("    MR redeespawn FAIL '" + name + "': " + ex.Message);
                }
            }
            plan?.AppendLine("  MR UniqueName redeespawn count=" + redeespawned);
            yield return null;
            yield return null;

            // Brief extra settle after redeespawn.
            float extra = 0f;
            const float extraSec = 0.75f; // v0.9.9 (condition-based early exit)
            while (extra < extraSec)
            {
                if (CountSavedNamesStillInRegistry(saved) == 0) break;
                extra += Time.unscaledDeltaTime;
                yield return null;
            }

            int still = CountSavedNamesStillInRegistry(saved);
            if (still > 0)
            {
                NoSaveWait.Timeout("uniquename-extra-settle", extraSec, "force-clear reservations (remain=" + still + ")");
                plan?.AppendLine("  MR UniqueName force-clear after redeespawn remain=" + still);
                try { MidMissionV03Helpers.ClearSavedUniqueNameReservations(snap, plan); }
                catch (Exception ex) { plan?.AppendLine("  MR force-clear UniqueNames FAIL: " + ex.Message); }
                still = CountSavedNamesStillInRegistry(saved);
            }
            plan?.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  MR UniqueName settle final remain={0}/{1} (0=ready for Swap)", still, saved.Count));
            NOSaveSystemPlugin.Log?.LogInfo("MissionReload: UniqueName settle final remain=" + still);
            yield return null;
            yield return null;
        }

        private static int CountSavedNamesStillInRegistry(HashSet<string> saved)
        {
            int remain = 0;
            try
            {
                if (saved == null || UnitRegistry.customIDLookup == null) return 0;
                foreach (var name in saved)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    if (UnitRegistry.customIDLookup.ContainsKey(name)) remain++;
                }
            }
            catch { return -1; }
            return remain;
        }

        private static List<string> ListSavedNamesStillInRegistry(HashSet<string> saved)
        {
            var list = new List<string>();
            try
            {
                if (saved == null || UnitRegistry.customIDLookup == null) return list;
                foreach (var name in saved)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    if (UnitRegistry.customIDLookup.ContainsKey(name)) list.Add(name);
                }
            }
            catch { }
            return list;
        }
    }

    [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.StartMission))]
    internal static class Patch_MissionManager_StartMission
    {
        static void Postfix()
        {
            try { MissionReload.NotifyStartMission(); }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Player), "OnStartLocalPlayer")]
    internal static class Patch_Player_OnStartLocalPlayer
    {
        static void Postfix(Player __instance)
        {
            try
            {
                // OnStartLocalPlayer is the local-player Mirage hook; still null-guard.
                if (__instance != null)
                    MissionReload.NotifyLocalPlayerReady();
            }
            catch { }
        }
    }

    /// <summary>
    /// Cecil: Host_SceneLoaded logs "Server finished loading", sets SceneIsReady, SpawnCharacter.
    /// Prefer this over soft gate timeouts after StartHost (StartHost -> StartHostAsync.Forget).
    /// </summary>
    [HarmonyPatch(typeof(NetworkManagerNuclearOption), nameof(NetworkManagerNuclearOption.Host_SceneLoaded))]
    internal static class Patch_NM_Host_SceneLoaded
    {
        static void Postfix()
        {
            try { MissionReload.NotifyHostSceneLoaded(); }
            catch { }
        }
    }
}
