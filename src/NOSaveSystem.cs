using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using NuclearOption.SceneLoading;
using UnityEngine;

namespace NOSaveSystem
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class NOSaveSystemPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.lorenzo.nosavesystem";
        public const string PLUGIN_NAME = "NOSaveSystem";
        public const string PLUGIN_VERSION = "1.0.1";
        public const int BUILD_ID = 1;

        internal static ManualLogSource Log;
        internal static NOSaveSystemPlugin Instance;
        private ConfigEntry<KeyCode> _saveKey;
        private ConfigEntry<KeyCode> _loadKey;
        private ConfigEntry<KeyCode> _continueKey;
        private ConfigEntry<float> _autoContinueSeconds;
        private ConfigEntry<string> _saveDir;
        private ConfigEntry<bool> _allowRespawnMissing;
        private ConfigEntry<bool> _restoreFactionEconomy;
        private ConfigEntry<bool> _restoreEscalation;
        private ConfigEntry<bool> _restoreMissionTimeMp;
        private ConfigEntry<bool> _applyLiveLoadout;
        private ConfigEntry<bool> _alsoWriteReflectDump;
        private ConfigEntry<bool> _restorePartHealth;
        private ConfigEntry<bool> _restoreAircraftControls;
        private static ConfigEntry<bool> _pauseAfterLoad;
        private static ConfigEntry<float> _pauseAfterLoadDelay;
        private static ConfigEntry<bool> _deepState;
        private static ConfigEntry<bool> _runObjectiveOutcomes;
        internal static bool DeepStateEnabled { get { try { return _deepState == null || _deepState.Value; } catch { return true; } } }
        internal static bool RunObjectiveOutcomes { get { try { return _runObjectiveOutcomes == null || _runObjectiveOutcomes.Value; } catch { return true; } } }
        internal static float PauseAfterLoadDelay { get { try { return _pauseAfterLoadDelay == null ? 0.0001f : Mathf.Max(0f, _pauseAfterLoadDelay.Value); } catch { return 0.0001f; } } }
        internal static bool PauseAfterLoad { get { try { return _pauseAfterLoad == null || _pauseAfterLoad.Value; } catch { return true; } } }
        private ConfigEntry<bool> _showHudFeedback;
        private ConfigEntry<string> _restoreMode;
        private ConfigEntry<string> _loadMode;
        private ConfigEntry<bool> _restoreFullState;
        private ConfigEntry<bool> _restoreAllObjectiveStatus;
        private Harmony _harmony;

        private bool _opBusy;
        private float _busyCooldownUntil;
        private float _busySince;
        private const float BusyStaleSec = 600f;

        private const string SaveFileName = "midmission_autosave.json";
        internal string SaveDirectory => _saveDir.Value;
        internal static string AutosaveFileName => SaveFileName;
        internal static string LoadPathOverride;
        internal static string PendingArchiveStamp;
        internal string AutosavePath => Path.Combine(_saveDir.Value, SaveFileName);
        internal string ResolveLoadPath()
        {
            var o = LoadPathOverride;
            try { if (!string.IsNullOrEmpty(o) && File.Exists(o)) return o; } catch { }
            return AutosavePath;
        }

        internal static bool SpHost()
        {
            try
            {
                if (GameManager.gameState != GameState.SinglePlayer) return false;
                if (PlayingFromEditor()) return false;
                var nm = NetworkManagerNuclearOption.i;
                var srv = nm != null ? nm.Server : null;
                if (srv == null || !srv.Active) return false;
                if (!srv.IsHost || srv.LocalPlayer == null) return false;
                if (srv.AllPlayers == null) return false;
                int n = srv.AllPlayers.Count;
                if (n > 1) { NoteMp(n, "gate"); return false; }
                if (_mpLatched) return false;
                return true;
            }
            catch { return false; }
        }

        private static bool _mpLatched;
        private static float _gatePollAt;
        internal static bool MpLatched { get { return _mpLatched; } }

        internal static void NoteMp(int players, string where)
        {
            if (_mpLatched) return;
            _mpLatched = true;
            Log?.LogWarning("GATE MP detected players=" + players + " where=" + where + " -> NOSave UI hidden, save/load/delete blocked until the next scene load (no config bypass)");
            try { NoSaveV0100.OnMpDetected(); } catch { }
        }

        private static void GatePoll()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _gatePollAt) return;
            _gatePollAt = now + 1f;
            try
            {
                if (GameManager.gameState == GameState.Menu) return;
                var nm = NetworkManagerNuclearOption.i;
                var srv = nm != null ? nm.Server : null;
                if (srv != null && srv.Active && srv.AllPlayers != null && srv.AllPlayers.Count > 1) NoteMp(srv.AllPlayers.Count, "poll");
            }
            catch { }
        }

        private static FieldInfoCache _editorFlag;
        private sealed class FieldInfoCache { public System.Reflection.FieldInfo F; public System.Reflection.PropertyInfo P; }
        internal static bool PlayingFromEditor()
        {
            try
            {
                if (_editorFlag == null)
                {
                    var t = typeof(GameManager);
                    const System.Reflection.BindingFlags bf = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    _editorFlag = new FieldInfoCache { F = t.GetField("IsPlayingFromEditor", bf), P = t.GetProperty("IsPlayingFromEditor", bf) };
                }
                if (_editorFlag.F != null) return (bool)_editorFlag.F.GetValue(null);
                if (_editorFlag.P != null) return (bool)_editorFlag.P.GetValue(null, null);
            }
            catch { }
            string marker = EditorMarker();
            if (marker == null) return false;
            if (!_editorWarned)
            {
                _editorWarned = true;
                Log?.LogWarning("NOSaveSystem: editor-play flag not readable and editor marker found (" + marker + ") - NOSave disabled while it is present");
            }
            return true;
        }
        private static bool _editorWarned;

        private static Type _missionEditorType; private static bool _missionEditorLooked;
        private static float _markerAt = -100f; private static string _markerCache;
        internal static string EditorMarker()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _markerAt < 1f) return _markerCache;
            _markerAt = now; _markerCache = EditorMarkerNow();
            return _markerCache;
        }
        private static string EditorMarkerNow()
        {
            try { if (GameManager.gameState == GameState.Editor) return "gameState=Editor"; } catch { }
            try
            {
                if (!_missionEditorLooked) { _missionEditorLooked = true; _missionEditorType = AccessTools.TypeByName("NuclearOption.MissionEditorScripts.MissionEditor"); }
                if (_missionEditorType != null)
                {
                    var o = UnityEngine.Object.FindObjectOfType(_missionEditorType) as Component;
                    if (o != null && o.gameObject.activeInHierarchy) return "active MissionEditor component";
                }
            }
            catch { }
            return null;
        }

        internal static string EditorFlagSource()
        {
            try { PlayingFromEditor(); } catch { }
            if (_editorFlag == null) return "unreadable(fail-closed)";
            if (_editorFlag.F != null) return "GameManager.IsPlayingFromEditor(field)=" + PlayingFromEditor();
            if (_editorFlag.P != null) return "GameManager.IsPlayingFromEditor(property)=" + PlayingFromEditor();
            string m = EditorMarker();
            return m == null ? "WARN MISSING(no editor markers -> allowed)" : "WARN MISSING(editor marker " + m + " -> NOSave off)";
        }

        internal static string LastLoadedPath;
        internal static int SaveCounter;

        internal void LoadLatestOrLast()
        {
            string last = LastLoadedPath;
            bool exists = false;
            try { exists = !string.IsNullOrEmpty(last) && File.Exists(last); } catch { }
            if (exists) { Log.LogInfo("F6: reloading last menu-loaded save " + last); RestoreFromFile(last, false); }
            else { LastLoadedPath = null; RestoreMidMissionSnapshot(); }
        }

        internal static bool ContinueAllowed()
        {
            try
            {
                if (GameManager.gameState != GameState.Menu) return false;
                if (PlayingFromEditor()) return false;
                var nm = NetworkManagerNuclearOption.i;
                if (nm != null && nm.Server != null && nm.Server.Active) return false;
                return true;
            }
            catch { return false; }
        }

        private static float _gateWarnAt = -100f; private static string _gateWarnOp;

        internal static bool GateSp(string op, bool menuContinue = false, bool quiet = false)
        {
            bool ok = menuContinue ? ContinueAllowed() : SpHost();
            if (!ok && !quiet && (op != _gateWarnOp || Time.realtimeSinceStartup - _gateWarnAt > 2f))
            {
                _gateWarnOp = op; _gateWarnAt = Time.realtimeSinceStartup;
                Log?.LogWarning("NOSaveSystem: disabled outside singleplayer (" + op + ") - no-op");
            }
            return ok;
        }

        private void LogFingerprint()
        {
            string sha = "?", cfg, kind = "?", path = "?";
#if DEBUG
            cfg = "Debug";
#else
            cfg = "Release";
#endif
            try
            {
                path = typeof(NOSaveSystemPlugin).Assembly.Location;
                using (var s = System.Security.Cryptography.SHA256.Create())
                using (var fs = File.OpenRead(path))
                {
                    var h = s.ComputeHash(fs); var sb = new StringBuilder(64);
                    foreach (var b in h) sb.Append(b.ToString("x2"));
                    sha = sb.ToString();
                }
            }
            catch (Exception ex) { sha = "unavailable(" + ex.GetType().Name + ")"; }
            try
            {
                string def = _saveDir != null ? _saveDir.DefaultValue as string : null;
                string portable = Path.Combine(BepInEx.Paths.BepInExRootPath, "NOSaveSystem", "saves");
                kind = def != null && string.Equals(Path.GetFullPath(def), Path.GetFullPath(portable), StringComparison.OrdinalIgnoreCase)
                    ? "release (portable default SaveDirectory)" : "dev (non-portable default SaveDirectory)";
            }
            catch { }
            Log.LogInfo("FINGERPRINT name=" + PLUGIN_NAME + " version=" + PLUGIN_VERSION + " build=" + BUILD_ID + " config=" + cfg + " sha256=" + sha + " kind=" + kind
                + " file=" + System.IO.Path.GetFileName(path) + " spLock=SinglePlayer+localServer+IsHost+LocalPlayer+AllPlayers<=1+!editor+mpLatch (no config bypass) editorFlag=" + EditorFlagSource());
        }
        internal bool HudFeedbackEnabled => _showHudFeedback != null && _showHudFeedback.Value;
        internal float AutoContinueSeconds => _autoContinueSeconds != null ? _autoContinueSeconds.Value : 0f;
        private const string DebugFileName = "autosave_debug.json";

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            _saveKey = Config.Bind("Hotkeys", "SaveKey", KeyCode.F5, "Write mid-mission snapshot (SavedMission-shaped JSON)");
            _loadKey = Config.Bind("Hotkeys", "LoadKey", KeyCode.F6, "Swap-out restore (default): despawn live mobiles then spawn from save. (v0.10.0: singleplayer only.)");
            _continueKey = Config.Bind("Hotkeys", "ContinueKey", KeyCode.F8, "Main-menu Continue mid-mission: loads the latest save (same as SAVE MENU > CONTINUE (LATEST)). No UI.");
            _autoContinueSeconds = Config.Bind("UI", "AutoContinueSeconds", 0f, "If >0 and midmission save exists, auto-fire Continue once after this many seconds on main menu. Playtest/debug; leave 0 normally.");
            _saveDir = Config.Bind(
                "Paths",
                "SaveDirectory",
                Path.Combine(BepInEx.Paths.BepInExRootPath, "NOSaveSystem", "saves"),
                "Directory for midmission_autosave.json");
            _allowRespawnMissing = Config.Bind(
                "Load",
                "AllowRespawnMissing",
                true,
                "If true AND Spawner.IsServer, respawn ANY missing saved unit via Spawner.SpawnSavedUnit / TrySpawnVehicle / TrySpawnShip so nothing poofs. Default ON for singleplayer fidelity. Singleplayer-only: the mod is disabled outside singleplayer.");
            _restoreFactionEconomy = Config.Bind(
                "Load",
                "RestoreFactionEconomy",
                true,
                "On F6, apply saved FactionHQ funds/score via SetFunds/SetScore when HQs exist.");
            _restoreEscalation = Config.Bind(
                "Load",
                "RestoreEscalation",
                true,
                "On F6, write MissionManager.NetworkcurrentEscalation from save.");
            _restoreMissionTimeMp = Config.Bind(
                "Load",
                "RestoreMissionTimeMp",
                true,
                "On F6 in multiplayer, adjust NetworkmultiplayerStartTime so MissionTime ~= saved. SP uses Time.timeSinceLevelLoad (not writable).");
            _applyLiveLoadout = Config.Bind(
                "Load",
                "ApplyLiveLoadout",
                true,
                "After teleport apply loadoutMountKeys to LIVE aircraft via Networkloadout+SpawnWeapons. Default ON for exact SP restore; still server-only for networked craft.");
            _alsoWriteReflectDump = Config.Bind(
                "Save",
                "AlsoWriteReflectDump",
                false,
                "If true, F5 also writes legacy autosave_debug.json reflection dump");
            _restorePartHealth = Config.Bind(
                "Load",
                "RestorePartHealth",
                true,
                "On F6, restore EXACT UnitPart.hitPoints for still-attached parts (raise or lower). Never detaches/reattaches. Logs failures if unsafe. Default on.");
            _restoreAircraftControls = Config.Bind(
                "Load",
                "RestoreAircraftControls",
                true,
                "On F6, restore Aircraft.GetInputs() throttle/brake for teleported aircraft. Engine RPM ratios are snapshot-only. Default on.");
            _pauseAfterLoad = Config.Bind(
                "Load",
                "PauseAfterLoad",
                true,
                "v0.9.3: after a MissionReload restore completes, open the game's pause (ESC) menu so nothing runs until you resume. v0.11.0: PAUSE0 = paused on the first rendered frame, zero sim ticks.");
            _deepState = Config.Bind("Load", "DeepStateGeneric", true,
                "v0.9.7: generic exhaustive per-object field save/restore (sidecar <save>.deep). Disable if it causes problems; explicit handlers still run.");
            _runObjectiveOutcomes = Config.Bind("Load", "RunObjectiveOutcomesOnLoad", true,
                "v0.9.7: objectives that were Complete at save get native Objective.Complete() (outcomes) before the unit restore.");
            _pauseAfterLoadDelay = Config.Bind(
                "Load",
                "PauseAfterLoadDelaySeconds",
                0.0001f,
                "v0.9.4: realtime delay before the post-load pause (min one frame). v0.11.0: only used by the legacy fallback when PAUSE0 fails.");
            _showHudFeedback = Config.Bind(
                "UI",
                "ShowHudFeedback",
                true,
                "After F5/F6, show a short MessageUI.GameMessage (fallback MissionMessages / AircraftActionsReport). Default on.");
            _restoreMode = Config.Bind(
                "Load",
                "RestoreMode",
                "Swap",
                "Within-mission unit apply strategy when not using MissionReload (or as MissionReload fallback). Swap: despawn+spawn. (v0.10.0: legacy Teleport mode removed; any value now uses Swap.)");
            _loadMode = Config.Bind(
                "Load",
                "LoadMode",
                "MissionReload",
                "Continue/F6 top-level strategy. MissionReload (0.8): Stop/Menu -> TryLoad -> SetMission -> StartHost -> wait StartMission+local player -> despawn defaults -> apply snapshot. Swap: in-place despawn+spawn fallback (no full re-entry).");

            _restoreFullState = Config.Bind(
                "Load",
                "RestoreFullState",
                true,
                "v0.9.0: after units are restored, apply saved extState (SP mission clock if an offset field is found, faction extras, objective progress, unit ammo, unit/airbase ownership, player score). Unconfirmed members go through logged reflection lookups; misses are skipped.");
            _restoreAllObjectiveStatus = Config.Bind(
                "Load",
                "RestoreAllObjectiveStatus",
                true,
                "v0.9.0: write Objective.Status (field only) for ALL saved objectives incl. completed ones. NEVER calls Complete()/CompleteObjective, so Outcomes (score/endgame/next objectives) do not re-fire.");

            try { Directory.CreateDirectory(_saveDir.Value); }
            catch (Exception ex) { Log.LogWarning("Could not create save directory: " + ex.Message); }

            try
            {
                try { _mainSceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle; } catch { }
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += (s, m) =>
                {
                    _editorWarned = false; _markerAt = -100f;
                    if (m == UnityEngine.SceneManagement.LoadSceneMode.Single)
                    {
                        _prevSceneHandle = _mainSceneHandle; _mainSceneHandle = s.handle; _mainSceneName = s.name;
                        if (_mpLatched) { _mpLatched = false; Log?.LogInfo("GATE MP latch cleared (scene load '" + s.name + "')"); }
                        try { NoSavePause0.ResetState("sceneLoaded"); } catch { }
                    }
                };
                UnityEngine.SceneManagement.SceneManager.sceneUnloaded += _ =>
                {
                    try { NoSaveV093.LoadPauseArmed = false; NoSaveV096.ClearAmmoReassert(); NoSavePilotFix.ClearPending(); } catch { }
                    try
                    {
                        bool running = false; try { running = GameManager.gameState != GameState.Menu; } catch { }
                        bool isMain = _.handle == _mainSceneHandle || _.handle == _prevSceneHandle;
                        if (!running || isMain) NoSavePause0.ResetState("sceneUnloaded");
                        else Log?.LogInfo("PAUSE0 reset ignored sceneUnloaded '" + _.name + "' (additive/loading scene; mission scene '" + (_mainSceneName ?? "?") + "' still loaded)");
                    }
                    catch { }
                    try { NoSaveEngineGear.Prune(); } catch { }
                };
            }
            catch (Exception ex) { Log.LogWarning("sceneUnloaded hook: " + ex.Message); }

            _harmony = new Harmony(PLUGIN_GUID);
            PatchEachClass();
            try { gameObject.AddComponent<NoSaveV0100Behaviour>(); }
            catch (Exception v100Ex) { Log.LogWarning("v0100: behaviour: " + v100Ex.Message); }
            try { gameObject.AddComponent<NoSaveUiBehaviour>(); }
            catch (Exception uiEx) { Log.LogWarning("NoSaveUiBehaviour: " + uiEx.Message); }
            LogFingerprint();
            Log.LogInfo(PLUGIN_NAME + " v" + PLUGIN_VERSION + " loaded. F5=snapshot, F6/Continue LoadMode=" + (_loadMode != null ? _loadMode.Value : "MissionReload") + " RestoreMode=" + (_restoreMode != null ? _restoreMode.Value : "Swap") + " (Swap=fallback).");
        }

        private static int _mainSceneHandle = -1, _prevSceneHandle = -1;
        private static string _mainSceneName;

        private void PatchEachClass()
        {
            int ok = 0, fail = 0, methods = 0;
            Type[] types;
            try { types = AccessTools.GetTypesFromAssembly(typeof(NOSaveSystemPlugin).Assembly); }
            catch (Exception ex) { Log.LogWarning("patch FAIL <type scan> " + ex); types = new Type[0]; }
            foreach (var t in types)
            {
                bool isPatch = false;
                try { isPatch = t != null && t.IsDefined(typeof(HarmonyAttribute), false); } catch { }
                if (!isPatch) continue;
                try
                {
                    var r = _harmony.CreateClassProcessor(t).Patch();
                    int n = r != null ? r.Count : 0; methods += n; ok++;
                    Log.LogInfo("patch ok " + t.FullName + " methods=" + n);
                }
                catch (Exception ex) { fail++; Log.LogWarning("patch FAIL " + t.FullName + " " + ex); }
            }
            if (fail == 0) Log.LogInfo("patch summary ok=" + ok + " fail=0 methods=" + methods);
            else Log.LogWarning("patch summary ok=" + ok + " fail=" + fail + " methods=" + methods + " (features of the failed classes are inactive)");
        }

        private void FixedUpdate() { NoSavePause0.OnFixedUpdate(); }

        private void Update()
        {
            GatePoll();
            if (Input.GetKeyDown(_saveKey.Value))
            {
                if (!GateSp("F5 save hotkey")) return;
                try { CaptureMidMissionSnapshot(); }
                catch (Exception ex) { Log.LogError("Save failed: " + ex); }
            }

            if (Input.GetKeyDown(_loadKey.Value))
            {
                if (!GateSp("F6 load hotkey")) return;
                try { LoadLatestOrLast(); }
                catch (Exception ex) { Log.LogError("Load failed: " + ex); }
            }

            if (_continueKey != null && Input.GetKeyDown(_continueKey.Value))
            {
                try
                {
                    if (!GateSp("F8 Continue hotkey (main menu only)", true)) return;
                    if (GameManager.gameState == GameState.Menu && !IsBusyOrCoolingDown)
                        StartCoroutine(NoSaveUi.ContinueAfterRebootCoroutine());
                }
                catch (Exception ex) { Log.LogError("Continue hotkey failed: " + ex); }
            }
        }

        internal bool IsBusyOrCoolingDown
        {
            get
            {
                try
                {
                    if (_opBusy && Time.unscaledTime - _busySince > BusyStaleSec && !MissionReload.IsRunning && !_coreRunning)
                    {
                        Log.LogWarning("NOSave busy flag STALE (>" + BusyStaleSec + "s) - clearing");
                        _opBusy = false;
                    }
                    return _opBusy || Time.unscaledTime < _busyCooldownUntil;
                }
                catch { return _opBusy; }
            }
        }

        internal bool TryBeginLongBusy(string op)
        {
            if (IsBusyOrCoolingDown)
            {
                RejectBusy(op);
                return false;
            }
            _opBusy = true;
            try { _busySince = Time.unscaledTime; } catch { _busySince = 0f; }
            Log.LogInfo("NOSave busy BEGIN (long) op=" + op);
            return true;
        }

        internal void EndLongBusy()
        {
            _opBusy = false;
            try { _busyCooldownUntil = Time.unscaledTime + 1f; } catch { _busyCooldownUntil = 0f; }
            Log.LogInfo("NOSave busy END (long) +1s cooldown");
        }

        private void RejectBusy(string op)
        {
            Log.LogInfo("NOSave busy - ignored " + op);
            if (HudFeedbackEnabled)
                MidMissionV03Helpers.ShowGameFeedback("NOSave busy");
        }

        private bool TryEnterBusy(string op)
        {
            if (IsBusyOrCoolingDown)
            {
                RejectBusy(op);
                return false;
            }
            _opBusy = true;
            try { _busySince = Time.unscaledTime; } catch { _busySince = 0f; }
            return true;
        }

        private void ExitBusy()
        {
            _opBusy = false;
            try { _busyCooldownUntil = Time.unscaledTime + 1f; } catch { _busyCooldownUntil = 0f; }
        }

        internal void CaptureMidMissionSnapshot()
        {
            if (!GateSp("Save")) return;
            if (!TryEnterBusy("F5/Save")) return;
            try { CaptureMidMissionSnapshotCore(); }
            finally { ExitBusy(); }
        }

        internal bool CanStartOp => GateSp("op", false, true) && !IsBusyOrCoolingDown && !MissionReload.IsRunning && !_coreRunning && !NoSaveReconcile.LoadInProgress;

        internal void CaptureMidMissionSnapshotCore()
        {
            Directory.CreateDirectory(_saveDir.Value);
            var path = Path.Combine(_saveDir.Value, SaveFileName);

            bool running = false; int acN0 = 0;
            try { running = MissionManager.IsRunning; } catch { }
            try { if (UnitRegistry.allAircraft != null) acN0 = UnitRegistry.allAircraft.Count; } catch { }
            if (!running || NoSaveReconcile.LoadInProgress)
            {
                Log.LogWarning("F5 refused: mission running=" + running + " aircraft=" + acN0 + " loadInProgress=" + NoSaveReconcile.LoadInProgress + " (existing save kept)");
                if (HudFeedbackEnabled) MidMissionV03Helpers.ShowGameFeedback("NOSave F5 refused (no running mission)");
                return;
            }

            var snap = BuildSnapshot();
            var json = SnapshotJson.Serialize(snap);
            WriteAtomic(path, json);
            SaveCounter++;
            LastLoadedPath = null;
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            try { Directory.CreateDirectory(Path.Combine(_saveDir.Value, "archive")); } catch { }
            PendingArchiveStamp = stamp;
            try { NoSaveDeepState.Save(path, snap.manifest != null ? snap.manifest.utc : null); } catch (Exception dex) { Log.LogWarning("deep-state save FAIL: " + dex.Message); }
            finally { PendingArchiveStamp = null; }
            try
            {
                var archiveDir = Path.Combine(_saveDir.Value, "archive");
                Directory.CreateDirectory(archiveDir);
                var archivePath = Path.Combine(archiveDir, "midmission_" + stamp + ".json");
                File.Copy(path, archivePath, true);
                Log.LogInfo("Also archived snapshot to " + archivePath);
            }
            catch (Exception archEx)
            {
                Log.LogWarning("Archive copy failed: " + archEx.Message);
            }

            Log.LogInfo(string.Format(
                CultureInfo.InvariantCulture,
                "Wrote mid-mission snapshot to {0} (aircraft={1}, vehicles={2}, ships={3}, factions={4}, objectives={5}, mission={6})",
                path,
                snap.aircraft != null ? snap.aircraft.Count : 0,
                snap.vehicles != null ? snap.vehicles.Count : 0,
                snap.ships != null ? snap.ships.Count : 0,
                snap.factions != null ? snap.factions.Count : 0,
                snap.activeObjectives != null ? snap.activeObjectives.Count : 0,
                snap.mission != null ? snap.mission.name : "?"));

            if (_showHudFeedback.Value)
            {
                int acN = snap.aircraft != null ? snap.aircraft.Count : 0;
                int objN = snap.activeObjectives != null ? snap.activeObjectives.Count : 0;
                MidMissionV03Helpers.ShowGameFeedback(
                    string.Format(CultureInfo.InvariantCulture, "NOSave F5: {0} aircraft, {1} objectives saved", acN, objN));
            }

            if (_alsoWriteReflectDump.Value)
            {
                try { WriteLegacyReflectDump(); }
                catch (Exception ex) { Log.LogWarning("Reflect dump failed: " + ex.Message); }
            }
        }

        internal static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            string bak = path + ".bak";
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            var bytes = new UTF8Encoding(false).GetBytes(text);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, bak); return; }
                catch (Exception ex) { Log?.LogInfo("File.Replace unavailable (" + ex.Message + ") - copy fallback"); }
                try { File.Copy(path, bak, true); } catch (Exception bex) { Log?.LogWarning("autosave .bak copy failed: " + bex.Message); }
                File.Copy(tmp, path, true);
                try { File.Delete(tmp); } catch { }
            }
            else
            {
                try { File.Move(tmp, path); }
                catch (IOException) { File.Copy(tmp, path, true); try { File.Delete(tmp); } catch { } }
            }
        }

        private MidMissionSnapshot BuildSnapshot()
        {
            var snap = new MidMissionSnapshot
            {
                manifest = new SnapshotManifest
                {
                    plugin = PLUGIN_NAME,
                    version = PLUGIN_VERSION,
                    phase = "v0.9.1-fullstate",
                    utc = DateTime.UtcNow.ToString("o"),
                    unity = Application.unityVersion,
                    scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                    gameState = GameManager.gameState.ToString(),
                    note = "v0.9.1 review fixes; v0.9.0 full-state extState (formatVersion 2); v0.8.9 verified mobile purge before spawn + single load lock; v0.8.3 Host_SceneLoaded gate + UniqueName settle after despawn; Continue Swap/legacy failpath; objectives Status public field but Outcomes unsafe - snapshot-only"
                },
                mission = CaptureMissionInfo(),
                aircraft = new List<SavedAircraftDto>(),
                vehicles = new List<SavedVehicleDto>(),
                ships = new List<SavedShipDto>(),
                otherUnits = new List<SavedUnitDto>(),
                factions = MidMissionV03Helpers.CaptureFactions(),
                activeObjectives = MidMissionV03Helpers.CaptureActiveObjectives(),
                formatVersion = NoSaveExtState.FormatVersion
            };

            Aircraft playerAc = MidMissionV03Helpers.FindLocalPlayerAircraft();

            var seen = new HashSet<int>();

            if (UnitRegistry.allAircraft != null)
            {
                foreach (var ac in UnitRegistry.allAircraft)
                {
                    if (ac == null) continue;
                    seen.Add(ac.GetInstanceID());
                    bool isPlayer = playerAc != null && ReferenceEquals(ac, playerAc);
                    if (!isPlayer)
                    {
                        try { isPlayer = ac.Player != null; } catch { }
                    }
                    snap.aircraft.Add(CaptureAircraft(ac, isPlayer));
                }
            }

            if (UnitRegistry.allUnits != null)
            {
                foreach (var unit in UnitRegistry.allUnits)
                {
                    if (unit == null) continue;
                    if (seen.Contains(unit.GetInstanceID())) continue;
                    if (unit is Aircraft) continue;

                    if (unit is GroundVehicle gv)
                    {
                        snap.vehicles.Add(CaptureVehicle(gv));
                        seen.Add(unit.GetInstanceID());
                    }
                    else if (unit is Ship ship)
                    {
                        snap.ships.Add(CaptureShip(ship));
                        seen.Add(unit.GetInstanceID());
                    }
                    else
                    {
                        snap.otherUnits.Add(CaptureUnitBase(unit, unit.GetType().Name));
                        seen.Add(unit.GetInstanceID());
                    }
                }
            }

            try { snap.playerState = NoSaveV0910.CapturePlayerState(playerAc); } catch { }
            try { snap.destroyedStatics = NoSaveV0910.CaptureDestroyedStatics(); } catch (Exception ex) { Log.LogWarning("destroyed statics capture FAIL: " + ex.Message); }
            try { snap.datalink = NoSaveV093.CaptureDatalink(); }
            catch (Exception ex) { Log.LogWarning("datalink capture FAIL: " + ex.Message); snap.datalink = new List<DatalinkDto>(); }
            try { snap.extState = NoSaveExtState.Capture(snap); }
            catch (Exception ex) { Log.LogWarning("ext-state capture FAIL: " + ex.Message); snap.extState = new List<StateEntryDto>(); }

            try { NoSaveV0100.FillMeta(snap); } catch (Exception ex) { Log.LogWarning("v0100: meta capture FAIL: " + ex.Message); }
            return snap;
        }

        internal static SnapshotMission CaptureMissionInfo()
        {
            var info = new SnapshotMission();
            try
            {
                info.isRunning = MissionManager.IsRunning;
                info.missionTime = MissionManager.i != null ? MissionManager.i.MissionTime : 0f;
                try
                {
                    if (MissionManager.i != null)
                    {
                        var mm = MissionManager.i;
                        info.currentEscalation = mm.currentEscalation;
                        info.wrecksMaxNumber = mm.wrecksMaxNumber;
                        info.wrecksDecayTime = mm.wrecksDecayTime;
                        info.tacticalThreshold = mm.tacticalThreshold;
                        info.strategicThreshold = mm.strategicThreshold;
                    }
                }
                catch { }
            }
            catch { }

            try
            {
                var m = MissionManager.CurrentMission;
                if (m != null)
                {
                    info.name = m.Name;
                    info.jsonVersion = m.JsonVersion;
                    if (m.LoadKey.HasValue)
                    {
                        info.loadKeyName = m.LoadKey.Value.Name;
                        info.loadKey = m.LoadKey.Value.Key;
                        try
                        {
                            var g = m.LoadKey.Value.Group;
                            info.loadKeyGroup = g != null ? g.GetType().Name : null;
                        }
                        catch { }
                    }
                    try
                    {
                        info.mapKeyType = m.MapKey.Type.ToString();
                        info.mapKeyPath = m.MapKey.Path;
                    }
                    catch { }
                    info.aircraftDefined = m.aircraft != null ? m.aircraft.Count : 0;
                    info.vehiclesDefined = m.vehicles != null ? m.vehicles.Count : 0;
                    info.shipsDefined = m.ships != null ? m.ships.Count : 0;
                }
            }
            catch (Exception ex)
            {
                info.error = ex.Message;
            }

            return info;
        }

        internal static SavedAircraftDto CaptureAircraft(Aircraft ac, bool isPlayer)
        {
            var dto = new SavedAircraftDto();
            dto.kind = "aircraft";
            dto.playerControlled = isPlayer;
            dto.isPlayerAircraft = isPlayer;
            FillUnitFields(ac, dto);
            try { dto.fuel = ac.GetFuelLevel(); dto.fuelCaptured = true; } catch { try { dto.fuel = ac.fuelLevel; dto.fuelCaptured = true; } catch { } }
            try { dto.skill = ac.skill; } catch { }
            try { dto.bravery = ac.bravery; } catch { }
            if (dto.velocityCaptured && dto.velocity != null)
            {
                var v = new Vector3(dto.velocity.x, dto.velocity.y, dto.velocity.z);
                dto.startingSpeed = v.magnitude;
            }
            else
            {
                try { dto.startingSpeed = ac.speed; } catch { }
            }
            if (!dto.velocityCaptured)
            {
                try
                {
                    var sv = ac.startingVelocity;
                    if (sv.sqrMagnitude > 1e-6f)
                    {
                        dto.velocity = new GlobalPosDto { x = sv.x, y = sv.y, z = sv.z };
                        if (dto.startingSpeed < 0.01f) dto.startingSpeed = sv.magnitude;
                        dto.velocityCaptured = true;
                    }
                }
                catch { }
            }
            try { dto.ignition = ac.Ignition; } catch { }
            try { dto.gearDeployed = ac.gearDeployed; } catch { }
            try { dto.radarAlt = ac.radarAlt; } catch { }
            dto.loadoutMountKeys = MidMissionV03Helpers.CaptureLoadoutKeys(ac);
            MidMissionV03Helpers.CaptureAircraftControls(ac, dto);
            return dto;
        }

        internal static SavedVehicleDto CaptureVehicle(GroundVehicle gv)
        {
            var dto = new SavedVehicleDto();
            FillUnitFields(gv, dto);
            dto.kind = "vehicle";
            dto.holdPosition = false;
            try { dto.skill = gv.skill; } catch { dto.skill = 0.5f; }
            return dto;
        }

        internal static SavedShipDto CaptureShip(Ship ship)
        {
            var dto = new SavedShipDto();
            FillUnitFields(ship, dto);
            dto.kind = "ship";
            try { dto.holdPosition = ship.holdPosition; } catch { dto.holdPosition = false; }
            try { dto.skill = ship.skill; } catch { dto.skill = 0.5f; }
            MidMissionV03Helpers.CaptureShipControls(ship, dto);
            return dto;
        }

        internal static SavedUnitDto CaptureUnitBase(Unit unit, string kind)
        {
            var dto = new SavedUnitDto();
            FillUnitFields(unit, dto);
            try { dto.v096 = NoSaveV096.CaptureExtra(unit); } catch { }
            dto.kind = kind;
            return dto;
        }

        private static void FillUnitFields(Unit unit, SavedUnitDto dto)
        {
            try { dto.v096 = NoSaveV096.CaptureExtra(unit); } catch { }
            try
            {
                var def = unit.definition;
                dto.type = def != null ? (string.IsNullOrEmpty(def.jsonKey) ? def.unitName : def.jsonKey) : null;
                dto.definitionName = def != null ? def.unitName : null;
            }
            catch { }

            try
            {
                var hq = unit.NetworkHQ;
                if (hq != null && hq.faction != null)
                    dto.faction = hq.faction.factionName;
            }
            catch { }

            try { dto.unitName = unit.unitName; } catch { }
            try { dto.persistentId = unit.persistentID.Id; } catch { }
            try { dto.private_uniqueName = unit.UniqueName; } catch { }
            if (string.IsNullOrEmpty(dto.private_uniqueName))
            {
                try
                {
                    MidMissionV03Helpers.EnsureSpawnUniqueName(dto);
                    MidMissionV03Helpers.TryPersistUniqueNameOnLiveUnit(unit, dto.private_uniqueName);
                }
                catch (Exception ex)
                {
                    Log?.LogWarning("F5 UniqueName ensure/persist: " + ex.Message);
                }
            }
            try { dto.disabled = unit.disabled; } catch { }
            try { dto.unitState = unit.unitState.ToString(); } catch { }

            try
            {
                var gp = GlobalPositionExtensions.GlobalPosition(unit);
                dto.globalPosition = new GlobalPosDto { x = gp.x, y = gp.y, z = gp.z };
            }
            catch
            {
                try
                {
                    var sp = unit.NetworkstartPosition;
                    dto.globalPosition = new GlobalPosDto { x = sp.x, y = sp.y, z = sp.z };
                }
                catch { }
            }

            try
            {
                var q = unit.transform.rotation;
                dto.rotation = new QuatDto { x = q.x, y = q.y, z = q.z, w = q.w };
            }
            catch
            {
                try
                {
                    var q = unit.NetworkstartRotation;
                    dto.rotation = new QuatDto { x = q.x, y = q.y, z = q.z, w = q.w };
                }
                catch { }
            }

            CaptureUnitVelocity(unit, dto);
            dto.parts = MidMissionV03Helpers.CapturePartHealth(unit);
        }

        private static void CaptureUnitVelocity(Unit unit, SavedUnitDto dto)
        {
            if (unit == null || dto == null) return;
            try
            {
                var rb = unit.rb;
                if (rb != null)
                {
                    var v = rb.velocity;
                    var w = rb.angularVelocity;
                    dto.velocity = new GlobalPosDto { x = v.x, y = v.y, z = v.z };
                    dto.angularVelocity = new GlobalPosDto { x = w.x, y = w.y, z = w.z };
                    dto.velocityCaptured = true;
                }
            }
            catch { }
        }

        private static bool _teleportWarned;
        private bool IsSwapRestoreMode()
        {
            try
            {
                string m = _restoreMode != null ? _restoreMode.Value : "Swap";
                if (!string.IsNullOrEmpty(m) && string.Equals(m.Trim(), "Teleport", StringComparison.OrdinalIgnoreCase) && !_teleportWarned)
                {
                    _teleportWarned = true;
                    Log.LogWarning("v0100: Load.RestoreMode=Teleport is no longer supported - using Swap");
                }
                return true;
            }
            catch { return true; }
        }

        internal void RestoreMidMissionSnapshot() { TryStartRestore(); }

        internal bool TryStartRestore()
        {
            if (!GateSp("Load")) return false;
            if (!TryEnterBusy("F6/Load")) return false;
            int token = NoSaveReconcile.TryAcquireLoad("F6/Load");
            if (token == 0) { ExitBusy(); RejectBusy("F6/Load (lock)"); return false; }
            NoSaveThrottleFollow.BeginRestore();
            StartCoroutine(NoSaveCoroutines.Safe(RestoreMidMissionSnapshotCo(token), "F6/Load"));
            return true;
        }

        internal bool RestoreFromFile(string path) { return RestoreFromFile(path, true); }

        internal bool RestoreFromFile(string path, bool remember)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Log.LogWarning("v0100: load refused, file missing: " + path); return false; }
            LoadPathOverride = path;
            bool started = false;
            try { started = TryStartRestore(); }
            finally { if (!started) LoadPathOverride = null; }
            if (started && remember)
            {
                LastLoadedPath = string.Equals(path, AutosavePath, StringComparison.OrdinalIgnoreCase) ? null : path;
            }
            return started;
        }

        internal bool IsMissionReloadLoadMode()
        {
            try
            {
                string m = _loadMode != null ? _loadMode.Value : "MissionReload";
                if (string.IsNullOrEmpty(m)) return true;
                return string.Equals(m.Trim(), "MissionReload", StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        private IEnumerator RestoreMidMissionSnapshotCo(int loadToken)
        {
            try
            {
                if (IsMissionReloadLoadMode())
                {
                    bool mrOk = false;
                    bool alreadyMenu = false;
                    try { alreadyMenu = GameManager.gameState == GameState.Menu; } catch { }
                    Log.LogInfo("F6/Load LoadMode=MissionReload alreadyMenu=" + alreadyMenu);
                    yield return MissionReload.RunMissionReloadCo(alreadyMenu, ok => { mrOk = ok; });
                    if (mrOk) yield break;
                    if (MissionReload.LastRefusedReentrant)
                    {
                        Log.LogWarning("MissionReload refused (already running) - NOT starting Swap fallback");
                        yield break;
                    }
                    if (MissionReload.LastPreflightFail)
                    {
                        Log.LogWarning("MissionReload preflight rejected the save (" + (MissionReload.LastFailReason ?? "-") + ") - nothing changed, no Swap fallback");
                        yield break;
                    }
                    if (MissionReload.LastFailReason == "purge_unverified")
                    {
                        Log.LogError("MissionReload: mobile purge not verified - NOT spawning (no Swap fallback)");
                        NoSaveUi.SetStatus("NOSave: restore aborted - old units did not despawn (see log)", 8f);
                        yield break;
                    }
                    if (MissionReload.LastFailReason == "core_failed")
                    {
                        Log.LogError("MissionReload: snapshot core failed/aborted inside reload - NOT running Swap fallback (see last_restore_plan.txt)");
                        NoSaveUi.SetStatus("NOSave: restore aborted (see last_restore_plan.txt)", 8f);
                        yield break;
                    }
                    if (!MissionReload.LastMissionRunning)
                    {
                        Log.LogError("MissionReload FAILED and no mission running (fail=" + (MissionReload.LastFailReason ?? "-") + ") - Swap fallback skipped");
                        NoSaveUi.SetStatus("NOSave: MissionReload failed - start the mission, then Load", 8f);
                        yield break;
                    }
                    Log.LogWarning("MissionReload FAILED/incomplete - falling back to in-place Swap apply (LoadMode stays MissionReload; auto-fallback only)");
                    MidMissionV03Helpers.ShowGameFeedback("NOSave MissionReload failed - Swap fallback");
                    NoSaveUi.SetStatus("NOSave: MissionReload failed - Swap fallback...", 8f);
                }
                else
                {
                    Log.LogInfo("F6/Load LoadMode=Swap - in-place Swap apply (MissionReload skipped by config)");
                    NoSaveUi.SetStatus("NOSave: Swap apply (LoadMode=Swap)...", 6f);
                }
                yield return RestoreMidMissionSnapshotCoreCo();
            }
            finally
            {
                NoSaveReconcile.ReleaseLoad(loadToken);
                ExitBusy();
                LoadPathOverride = null;
            }
        }

        internal void RestoreMidMissionSnapshotCore()
        {
            if (_coreRunning || MissionReload.IsRunning)
            {
                Log.LogWarning("RestoreMidMissionSnapshotCore refused: restore already running");
                return;
            }
            int token = NoSaveReconcile.TryAcquireLoad("CoreSync");
            if (token == 0) return;
            StartCoroutine(NoSaveCoroutines.Safe(RestoreCoreWithLockCo(token), "CoreSync"));
        }

        private IEnumerator RestoreCoreWithLockCo(int token)
        {
            try { yield return RestoreMidMissionSnapshotCoreCo(); }
            finally { NoSaveReconcile.ReleaseLoad(token); }
        }

        private static bool _coreRunning;
        internal static bool CoreRunning { get { return _coreRunning; } }
        internal static bool LastCoreOk { get; private set; }

        internal IEnumerator RestoreMidMissionSnapshotCoreCo()
        {
            if (_coreRunning)
            {
                LastCoreOk = false;
                Log.LogWarning("F6 core refused: another core restore is already running (double invocation)");
                yield break;
            }
            _coreRunning = true;
            LastCoreOk = false;
            NoSaveThrottleFollow.BeginRestore();
            try { yield return NoSaveCoroutines.Safe(RestoreMidMissionSnapshotCoreCoBody(), "F6 core"); }
            finally { _coreRunning = false; _pidIndex = null; }
        }

        private static Dictionary<uint, Unit> _pidIndex;

        private static void BuildPidIndex()
        {
            var d = new Dictionary<uint, Unit>();
            try
            {
                if (UnitRegistry.allAircraft != null)
                    foreach (var a in UnitRegistry.allAircraft) { if (a == null) continue; try { uint id = a.persistentID.Id; if (id != 0 && !d.ContainsKey(id)) d[id] = a; } catch { } }
                if (UnitRegistry.allUnits != null)
                    foreach (var u in UnitRegistry.allUnits) { if (u == null) continue; try { uint id = u.persistentID.Id; if (id != 0 && !d.ContainsKey(id)) d[id] = u; } catch { } }
            }
            catch { }
            _pidIndex = d;
        }

        private IEnumerator RestoreMidMissionSnapshotCoreCoBody()
        {
            LastCoreOk = false;
            var path = ResolveLoadPath();
            if (!File.Exists(path))
            {
                Log.LogWarning("No save file at " + path + " - press F5 in a mission first.");
                yield break;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            var snap = SnapshotJson.Deserialize(json);
            if (snap == null)
            {
                Log.LogError("Failed to parse " + path);
                yield break;
            }

            bool swapMode = IsSwapRestoreMode();
            int teleported = 0, missing = 0, spawned = 0, failed = 0, rebound = 0, economy = 0;
            int playerSpawnedOnContinue = 0, despawned = 0, swapped = 0, skippedStatic = 0;
            var plan = new StringBuilder();
            plan.AppendLine("F6 restore plan v0.8.5 mode=" + (swapMode ? "Swap" : "Teleport") + " from " + path);

            Spawner spawner = null;
            try { spawner = Spawner.i; } catch { }
            bool canSpawn = spawner != null && spawner.IsServer && (swapMode || _allowRespawnMissing.Value);
            if (swapMode && (spawner == null || !spawner.IsServer))
                plan.AppendLine("  WARN Swap needs Spawner.IsServer; host/offline only. canSpawn=" + canSpawn);

            if (_restoreFactionEconomy.Value)
                economy = MidMissionV03Helpers.RestoreFactionEconomy(snap.factions, plan);

            int blackboard = MidMissionV03Helpers.RestoreMissionBlackboard(
                snap.mission, _restoreEscalation.Value, _restoreMissionTimeMp.Value, plan);

            var localPlayer = MidMissionV03Helpers.FindLocalPlayer();
            Aircraft playerCraftAfter = null;

            var pass1Despawned = new List<KeyValuePair<Unit, string>>();
            if (swapMode)
            {
                plan.AppendLine("  SWAP pass1 DESPAWN all mobiles before settle");
                if (snap.aircraft != null)
                {
                    foreach (var dto in snap.aircraft)
                    {
                        var live = FindLiveAircraft(dto);
                        if (live == null) continue;
                        if (MidMissionV03Helpers.DespawnUnitForSwap(live, localPlayer, plan))
                        {
                            despawned++;
                            pass1Despawned.Add(new KeyValuePair<Unit, string>(live, DtoLabel(dto)));
                            plan.AppendLine("  SWAP despawn aircraft " + DtoLabel(dto));
                        }
                        else
                        {
                            failed++;
                            plan.AppendLine("  FAIL despawn aircraft " + DtoLabel(dto));
                        }
                    }
                }
                if (snap.vehicles != null)
                {
                    foreach (var dto in snap.vehicles)
                    {
                        var live = FindLiveUnit(dto) as GroundVehicle;
                        if (live == null) continue;
                        if (MidMissionV03Helpers.DespawnUnitForSwap(live, localPlayer, plan))
                        {
                            despawned++;
                            pass1Despawned.Add(new KeyValuePair<Unit, string>(live, DtoLabel(dto)));
                            plan.AppendLine("  SWAP despawn vehicle " + DtoLabel(dto));
                        }
                        else { failed++; plan.AppendLine("  FAIL despawn vehicle " + DtoLabel(dto)); }
                    }
                }
                if (snap.ships != null)
                {
                    foreach (var dto in snap.ships)
                    {
                        var live = FindLiveUnit(dto) as Ship;
                        if (live == null) continue;
                        if (MidMissionV03Helpers.DespawnUnitForSwap(live, localPlayer, plan))
                        {
                            despawned++;
                            pass1Despawned.Add(new KeyValuePair<Unit, string>(live, DtoLabel(dto)));
                            plan.AppendLine("  SWAP despawn ship " + DtoLabel(dto));
                        }
                        else { failed++; plan.AppendLine("  FAIL despawn ship " + DtoLabel(dto)); }
                    }
                }
                if (snap.otherUnits != null)
                {
                    foreach (var dto in snap.otherUnits)
                    {
                        var live = FindLiveUnit(dto);
                        bool isStatic = MidMissionV03Helpers.IsStaticUnitKind(dto.kind, live);
                        bool mobileOk = MidMissionV03Helpers.IsSwapSafeMobileOther(dto.kind, live);
                        if (isStatic || !mobileOk) continue;
                        if (live == null) continue;
                        if (MidMissionV03Helpers.DespawnUnitForSwap(live, localPlayer, plan))
                        {
                            despawned++;
                            pass1Despawned.Add(new KeyValuePair<Unit, string>(live, DtoLabel(dto)));
                            plan.AppendLine("  SWAP despawn other " + DtoLabel(dto) + " kind=" + (dto.kind ?? "?"));
                        }
                        else { failed++; plan.AppendLine("  FAIL despawn other " + DtoLabel(dto)); }
                    }
                }
                plan.AppendLine("  SETTLE 2 frames after pass1 (Destroy deferred; DisableUnit+SetActive already applied)");
                yield return null;
                yield return null;
                int stillAlive = 0;
                foreach (var kv in pass1Despawned)
                {
                    try { if (!MidMissionV03Helpers.VerifyDespawnOrForceDestroy(kv.Key, kv.Value, plan)) stillAlive++; }
                    catch (Exception vex) { plan.AppendLine("    DESPAWN VERIFY EX " + kv.Value + ": " + vex.Message); }
                }
                plan.AppendLine("  SWAP pass1 verify: despawned=" + pass1Despawned.Count + " forcedOrStillAlive=" + stillAlive);
                yield return null;
            }

            BuildPidIndex();

            if (snap.aircraft != null)
            {
                var aircraftOrder = new List<SavedAircraftDto>(snap.aircraft);
                aircraftOrder.Sort((a, b) =>
                {
                    int ap = (a != null && (a.isPlayerAircraft || a.playerControlled)) ? 0 : 1;
                    int bp = (b != null && (b.isPlayerAircraft || b.playerControlled)) ? 0 : 1;
                    return ap.CompareTo(bp);
                });

                foreach (var dto in aircraftOrder)
                {
                    if (dto == null) continue;
                    bool isPlayerDto = dto.isPlayerAircraft || dto.playerControlled;
                    var live = FindLiveAircraft(dto);

                    if (swapMode)
                    {
                        if (live != null)
                        {
                            if (MidMissionV03Helpers.DespawnUnitForSwap(live, localPlayer, plan))
                            {
                                despawned++;
                                plan.AppendLine("  SWAP despawn aircraft " + DtoLabel(dto));
                            }
                            else
                            {
                                failed++;
                                plan.AppendLine("  FAIL despawn aircraft " + DtoLabel(dto));
                            }
                            live = null;
                        }

                        bool spawnPlayer = isPlayerDto && spawner != null && spawner.IsServer;
                        bool doSpawn = canSpawn || spawnPlayer;
                        if (!doSpawn)
                        {
                            missing++;
                            plan.AppendLine("  MISSING aircraft " + DtoLabel(dto) + " -> need Spawner.IsServer for Swap");
                            continue;
                        }

                        var spawnedAc = MidMissionV03Helpers.SpawnAircraftFromDto(spawner, dto);
                        if (spawnedAc != null)
                        {
                            spawned++;
                            swapped++;
                            if (isPlayerDto)
                            {
                                playerSpawnedOnContinue++;
                                playerCraftAfter = spawnedAc;
                            }
                            plan.AppendLine("  SWAP spawn aircraft " + DtoLabel(dto) + (isPlayerDto ? " (player)" : ""));
                            ApplyAircraftStateAfterRestore(spawnedAc, dto, plan, ref teleported);
                        }
                        else
                        {
                            failed++;
                            missing++;
                            plan.AppendLine("  SWAP SPAWN FAIL aircraft " + DtoLabel(dto));
                        }
                    }
                }
            }

            if (snap.vehicles != null)
            {
                foreach (var dto in snap.vehicles)
                {
                    if (dto == null) continue;
                    var live = FindLiveUnit(dto) as GroundVehicle;
                    if (swapMode)
                    {
                        if (live != null)
                        {
                            if (MidMissionV03Helpers.DespawnUnitForSwap(live, localPlayer, plan))
                            {
                                despawned++;
                                plan.AppendLine("  SWAP despawn vehicle " + DtoLabel(dto));
                            }
                            else { failed++; plan.AppendLine("  FAIL despawn vehicle " + DtoLabel(dto)); }
                            live = null;
                        }
                        if (!canSpawn)
                        {
                            missing++;
                            plan.AppendLine("  MISSING vehicle " + DtoLabel(dto) + " -> need Spawner.IsServer");
                            continue;
                        }
                        if (TryRespawnVehicle(spawner, dto))
                        {
                            spawned++;
                            swapped++;
                            plan.AppendLine("  SWAP spawn vehicle " + DtoLabel(dto));
                            var spawnedLive = FindSpawnedUnit(dto) as GroundVehicle;
                            if (spawnedLive != null && ApplyVelocityOnlyAfterSpawn(spawnedLive, dto, 0f, null, plan))
                            {
                                teleported++;
                                if (_restorePartHealth.Value && dto.parts != null && dto.parts.Count > 0)
                                    MidMissionV03Helpers.RestorePartHealthExact(spawnedLive, dto.parts, plan);
                            }
                        }
                        else { failed++; missing++; plan.AppendLine("  SWAP SPAWN FAIL vehicle " + DtoLabel(dto)); }
                    }
                    else
                    {
                        if (live != null)
                        {
                            if (TryTeleportUnit(live, dto, 0f))
                            {
                                teleported++;
                                plan.AppendLine("  TELEPORT vehicle " + DtoLabel(dto));
                                if (_restorePartHealth.Value && dto.parts != null && dto.parts.Count > 0)
                                    MidMissionV03Helpers.RestorePartHealthExact(live, dto.parts, plan);
                            }
                            else { failed++; plan.AppendLine("  FAIL teleport vehicle " + DtoLabel(dto)); }
                        }
                        else
                        {
                            missing++;
                            plan.AppendLine("  MISSING vehicle " + DtoLabel(dto) +
                                            (canSpawn ? " -> TrySpawnVehicle" : " -> need IsServer + AllowRespawnMissing"));
                            if (canSpawn && TryRespawnVehicle(spawner, dto))
                            {
                                spawned++;
                                plan.AppendLine("    SPAWNED vehicle " + DtoLabel(dto));
                                var spawnedLive = FindSpawnedUnit(dto) as GroundVehicle;
                                if (spawnedLive != null && TryTeleportUnit(spawnedLive, dto, 0f))
                                {
                                    teleported++;
                                    if (_restorePartHealth.Value && dto.parts != null && dto.parts.Count > 0)
                                        MidMissionV03Helpers.RestorePartHealthExact(spawnedLive, dto.parts, plan);
                                }
                            }
                            else if (canSpawn) { failed++; plan.AppendLine("    SPAWN FAIL vehicle " + DtoLabel(dto)); }
                        }
                    }
                }
            }

            if (snap.ships != null)
            {
                foreach (var dto in snap.ships)
                {
                    if (dto == null) continue;
                    var live = FindLiveUnit(dto) as Ship;
                    if (swapMode)
                    {
                        if (live != null)
                        {
                            if (MidMissionV03Helpers.DespawnUnitForSwap(live, localPlayer, plan))
                            {
                                despawned++;
                                plan.AppendLine("  SWAP despawn ship " + DtoLabel(dto));
                            }
                            else { failed++; plan.AppendLine("  FAIL despawn ship " + DtoLabel(dto)); }
                            live = null;
                        }
                        if (!canSpawn)
                        {
                            missing++;
                            plan.AppendLine("  MISSING ship " + DtoLabel(dto) + " -> need Spawner.IsServer");
                            continue;
                        }
                        if (TryRespawnShip(spawner, dto))
                        {
                            spawned++;
                            swapped++;
                            plan.AppendLine("  SWAP spawn ship " + DtoLabel(dto));
                            var spawnedLive = FindSpawnedUnit(dto) as Ship;
                            if (spawnedLive != null && ApplyVelocityOnlyAfterSpawn(spawnedLive, dto, 0f, null, plan))
                            {
                                teleported++;
                                if (_restorePartHealth.Value && dto.parts != null && dto.parts.Count > 0)
                                    MidMissionV03Helpers.RestorePartHealthExact(spawnedLive, dto.parts, plan);
                                MidMissionV03Helpers.RestoreShipControls(spawnedLive, dto, plan);
                            }
                        }
                        else { failed++; missing++; plan.AppendLine("  SWAP SPAWN FAIL ship " + DtoLabel(dto)); }
                    }
                    else
                    {
                        if (live != null)
                        {
                            if (TryTeleportUnit(live, dto, 0f))
                            {
                                teleported++;
                                plan.AppendLine("  TELEPORT ship " + DtoLabel(dto));
                                if (_restorePartHealth.Value && dto.parts != null && dto.parts.Count > 0)
                                    MidMissionV03Helpers.RestorePartHealthExact(live, dto.parts, plan);
                                MidMissionV03Helpers.RestoreShipControls(live, dto, plan);
                            }
                            else { failed++; plan.AppendLine("  FAIL teleport ship " + DtoLabel(dto)); }
                        }
                        else
                        {
                            missing++;
                            plan.AppendLine("  MISSING ship " + DtoLabel(dto) +
                                            (canSpawn ? " -> TrySpawnShip" : " -> need IsServer + AllowRespawnMissing"));
                            if (canSpawn && TryRespawnShip(spawner, dto))
                            {
                                spawned++;
                                plan.AppendLine("    SPAWNED ship " + DtoLabel(dto));
                                var spawnedLive = FindSpawnedUnit(dto) as Ship;
                                if (spawnedLive != null && TryTeleportUnit(spawnedLive, dto, 0f))
                                {
                                    teleported++;
                                    if (_restorePartHealth.Value && dto.parts != null && dto.parts.Count > 0)
                                        MidMissionV03Helpers.RestorePartHealthExact(spawnedLive, dto.parts, plan);
                                    MidMissionV03Helpers.RestoreShipControls(spawnedLive, dto, plan);
                                }
                            }
                            else if (canSpawn) { failed++; plan.AppendLine("    SPAWN FAIL ship " + DtoLabel(dto)); }
                        }
                    }
                }
            }

            if (snap.otherUnits != null)
            {
                foreach (var dto in snap.otherUnits)
                {
                    if (dto == null) continue;
                    var live = FindLiveUnit(dto);
                    bool isStatic = MidMissionV03Helpers.IsStaticUnitKind(dto.kind, live);
                    bool mobileOk = MidMissionV03Helpers.IsSwapSafeMobileOther(dto.kind, live);

                    if (isStatic || (!mobileOk && swapMode))
                    {
                        skippedStatic++;
                        plan.AppendLine("  SKIP static/non-mobile other " + DtoLabel(dto) + " kind=" + (dto.kind ?? "?"));
                        continue;
                    }

                    if (swapMode)
                    {
                        if (!mobileOk)
                        {
                            skippedStatic++;
                            plan.AppendLine("  SKIP other (not swap-safe) " + DtoLabel(dto));
                            continue;
                        }
                        if (live != null)
                        {
                            if (MidMissionV03Helpers.DespawnUnitForSwap(live, localPlayer, plan))
                            {
                                despawned++;
                                plan.AppendLine("  SWAP despawn other " + DtoLabel(dto));
                            }
                            else { failed++; plan.AppendLine("  FAIL despawn other " + DtoLabel(dto)); }
                            live = null;
                        }
                        if (!canSpawn)
                        {
                            missing++;
                            plan.AppendLine("  MISSING other " + DtoLabel(dto) + " -> need Spawner.IsServer");
                            continue;
                        }
                        if (MidMissionV03Helpers.TrySpawnOtherUnit(spawner, dto, plan))
                        {
                            spawned++;
                            swapped++;
                            var spawnedLive = FindSpawnedUnit(dto);
                            if (spawnedLive != null && TryTeleportUnit(spawnedLive, dto, 0f))
                            {
                                teleported++;
                                if (_restorePartHealth.Value && dto.parts != null && dto.parts.Count > 0)
                                    MidMissionV03Helpers.RestorePartHealthExact(spawnedLive, dto.parts, plan);
                            }
                        }
                        else { failed++; missing++; plan.AppendLine("  SWAP SPAWN FAIL other " + DtoLabel(dto)); }
                    }
                    else
                    {
                        if (isStatic)
                        {
                            skippedStatic++;
                            plan.AppendLine("  SKIP static other (Teleport mode never TPs buildings) " + DtoLabel(dto));
                            continue;
                        }
                        if (live != null)
                        {
                            if (TryTeleportUnit(live, dto, 0f))
                            {
                                teleported++;
                                plan.AppendLine("  TELEPORT other " + DtoLabel(dto));
                                if (_restorePartHealth.Value && dto.parts != null && dto.parts.Count > 0)
                                    MidMissionV03Helpers.RestorePartHealthExact(live, dto.parts, plan);
                            }
                            else { failed++; plan.AppendLine("  FAIL teleport other " + DtoLabel(dto)); }
                        }
                        else
                        {
                            missing++;
                            plan.AppendLine("  MISSING other " + DtoLabel(dto) +
                                            (canSpawn ? " -> SpawnSavedUnit(kind)" : " -> need IsServer + AllowRespawnMissing"));
                            if (canSpawn && MidMissionV03Helpers.TrySpawnOtherUnit(spawner, dto, plan))
                            {
                                spawned++;
                                var spawnedLive = FindSpawnedUnit(dto);
                                if (spawnedLive != null && TryTeleportUnit(spawnedLive, dto, 0f))
                                {
                                    teleported++;
                                    if (_restorePartHealth.Value && dto.parts != null && dto.parts.Count > 0)
                                        MidMissionV03Helpers.RestorePartHealthExact(spawnedLive, dto.parts, plan);
                                }
                            }
                            else if (canSpawn) { failed++; plan.AppendLine("    SPAWN FAIL other " + DtoLabel(dto)); }
                        }
                    }
                }
            }

            if (localPlayer != null && playerCraftAfter != null)
            {
                if (MidMissionV03Helpers.RebindPlayerToAircraft(localPlayer, playerCraftAfter, plan))
                    rebound++;
                else
                    failed++;
            }

            if (_restoreFullState != null && _restoreFullState.Value)
            {
                yield return null;
                try
                {
                    NoSaveExtState.Restore(snap, plan, _restoreAllObjectiveStatus != null && _restoreAllObjectiveStatus.Value);
                    if (NoSaveReflect.Report.Length > 0) plan.AppendLine("  REFLECT lookups:\n" + NoSaveReflect.Report);
                }
                catch (Exception ex) { plan.AppendLine("  EXT-STATE FAIL: " + ex.Message); }
            }
            else plan.AppendLine("  EXT-STATE skipped (Load.RestoreFullState=false)");

            if (snap.activeObjectives != null && snap.activeObjectives.Count > 0)
            {
                plan.AppendLine("  OBJECTIVES count=" + snap.activeObjectives.Count + " (Status field apply; Complete()/Outcomes NOT called)");
                try { MidMissionV03Helpers.LogObjectiveSnapshotDelta(snap.activeObjectives, plan); } catch { }
                try { MidMissionV03Helpers.TryApplyObjectiveStatuses(snap.activeObjectives, plan); } catch (Exception ex) { plan.AppendLine("  OBJECTIVES status-apply outer FAIL: " + ex.Message); }
            }

            try { NoSaveV0911.FixObjectiveRegistrations(snap, plan); } catch (Exception ex) { plan.AppendLine("  V0911 FAIL: " + ex.Message); }

            Log.LogInfo(string.Format(
                CultureInfo.InvariantCulture,
                "F6 done: mode={0} swapped={1} despawned={2} spawned={3} teleported/post={4} missing={5} skippedStatic={6} rebound={7} economy={8} blackboard={9} failed={10} canSpawn={11} playerSpawned={12}",
                swapMode ? "Swap" : "Teleport", swapped, despawned, spawned, teleported, missing, skippedStatic,
                rebound, economy, blackboard, failed, canSpawn, playerSpawnedOnContinue));
            Log.LogInfo(plan.ToString());

            if (_showHudFeedback.Value)
            {
                MidMissionV03Helpers.ShowGameFeedback(string.Format(CultureInfo.InvariantCulture,
                    swapMode
                        ? "NOSave F6 Swap: swapped={0} spawned={1} skippedStatic={2} failed={3}"
                        : "NOSave F6 Teleport: teleported={0} spawned={1} missing={2} failed={3}",
                    swapMode ? swapped : teleported,
                    spawned,
                    swapMode ? skippedStatic : missing,
                    failed));
            }

            try
            {
                var planPath = Path.Combine(_saveDir.Value, "last_restore_plan.txt");
                File.WriteAllText(planPath, plan.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Log.LogWarning("last_restore_plan.txt write failed: " + ex.Message); }
            LastCoreOk = true;
            if (!MissionReload.IsRunning) { try { NoSaveVerify.Arm(snap, "Swap"); } catch (Exception vex) { Log.LogWarning("NOSAVE-VERIFY arm FAIL " + vex.Message); } }
        }

        private void ApplyAircraftStateFields(Aircraft ac, SavedAircraftDto dto, StringBuilder plan)
        {
            if (ac == null || dto == null) return;
            try
            {
                if (dto.fuel > 0f || dto.fuelCaptured)
                {
                    ac.fuelLevel = dto.fuel;
                    ac.NetworkfuelLevel = dto.fuel;
                }
            }
            catch { }
            try
            {
                ac.Ignition = dto.ignition;
                ac.NetworkIgnition = dto.ignition;
            }
            catch { }
            try { NoSaveEngineGear.ApplyGear(ac, dto, null); }
            catch { try { ac.NetworkgearDeployed = dto.gearDeployed; } catch { } }
            if (_restorePartHealth.Value && dto.parts != null && dto.parts.Count > 0)
                MidMissionV03Helpers.RestorePartHealthExact(ac, dto.parts, plan);
            try { NoSaveEngineGear.ApplyEngines(ac, dto, null, "spawn"); } catch { }
            if (_restoreAircraftControls.Value)
            {
                MidMissionV03Helpers.RestoreAircraftControls(ac, dto, plan);
                try
                {
                    if (Instance != null)
                    {
                        plan?.AppendLine("  PILOTFIX scheduled -> " + (ac.UniqueName ?? ac.name) +
                                         " savedState=" + (dto.pilotState ?? "?") + " ('" + (dto.pilotStateName ?? "") + "')" +
                                         " hasTakenOff=" + dto.hasTakenOff + " simThrottle=" + (dto.simThrottleCaptured ? dto.simThrottle.ToString("0.###") : "n/a"));
                        Instance.StartCoroutine(NoSaveCoroutines.Safe(NoSaveVerify.Track(NoSavePilotFix.Run(ac, dto)), "PilotFix"));
                    }
                }
                catch (Exception ex) { plan?.AppendLine("  PILOTFIX schedule FAIL: " + ex.Message); }
            }
            if (_applyLiveLoadout.Value && dto.loadoutMountKeys != null && dto.loadoutMountKeys.Count > 0)
                MidMissionV03Helpers.ApplyLiveLoadout(ac, dto.loadoutMountKeys, plan);
        }

        private bool ApplyAircraftStateAfterRestore(Aircraft ac, SavedAircraftDto dto, StringBuilder plan, ref int teleported)
        {
            if (ac == null) return false;
            bool ok = ApplyVelocityOnlyAfterSpawn(ac, dto, dto.startingSpeed, dto, plan);
            if (ok)
            {
                teleported++;
                plan.AppendLine("    POST-SPAWN velocity (pose from spawn) " + DtoLabel(dto));
            }
            ApplyAircraftStateFields(ac, dto, plan);
            return ok;
        }

        private static bool ApplyVelocityOnlyAfterSpawn(Unit unit, SavedUnitDto dto, float speedHint, SavedAircraftDto acDto, StringBuilder plan)
        {
            if (unit == null || dto == null) return false;
            try
            {
                var rot = dto.rotation != null
                    ? new Quaternion(dto.rotation.x, dto.rotation.y, dto.rotation.z, dto.rotation.w)
                    : unit.transform.rotation;
                ResolveUnitVelocity(dto, acDto ?? (dto as SavedAircraftDto), rot, speedHint, out var desiredVel, out var desiredAng, out var velSource);
                var rb = unit.rb;
                if (rb != null)
                {
                    rb.velocity = desiredVel;
                    rb.angularVelocity = desiredAng;
                }
                if (unit is Aircraft ac)
                {
                    try { ac.startingVelocity = desiredVel; ac.NetworkstartingVelocity = desiredVel; } catch { }
                    try { ac.velocityPrev = desiredVel; } catch { }
                    try { ac.accel = Vector3.zero; } catch { }
                }
                plan?.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "    VEL-ONLY {0} velSource={1} speed={2:F2}", DtoLabel(dto), velSource, desiredVel.magnitude));
                return true;
            }
            catch (Exception ex)
            {
                plan?.AppendLine("    VEL-ONLY FAIL " + DtoLabel(dto) + ": " + ex.Message);
                return false;
            }
        }

        private static string DtoLabel(SavedUnitDto dto)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "type={0} faction={1} name={2} pid={3}",
                dto.type, dto.faction, dto.private_uniqueName, dto.persistentId);
        }

        private static Aircraft FindLiveAircraft(SavedAircraftDto dto)
        {
            if (UnitRegistry.allAircraft == null) return null;
            if (!string.IsNullOrEmpty(dto.private_uniqueName) &&
                UnitRegistry.customIDLookup != null &&
                UnitRegistry.customIDLookup.TryGetValue(dto.private_uniqueName, out var byId) &&
                byId is Aircraft acById &&
                MidMissionV03Helpers.IsLiveActiveUnit(acById))
                return acById;

            if (_pidIndex != null)
            {
                Unit byPid;
                if (dto.persistentId != 0 && _pidIndex.TryGetValue(dto.persistentId, out byPid) && byPid is Aircraft &&
                    MidMissionV03Helpers.IsLiveActiveUnit(byPid))
                    return (Aircraft)byPid;
            }
            else
            foreach (var ac in UnitRegistry.allAircraft)
            {
                if (!MidMissionV03Helpers.IsLiveActiveUnit(ac)) continue;
                if (!string.IsNullOrEmpty(dto.private_uniqueName) && ac.UniqueName == dto.private_uniqueName)
                    return ac;
                if (dto.persistentId != 0 && ac.persistentID.Id == dto.persistentId)
                    return ac;
            }

            if (dto.isPlayerAircraft || dto.playerControlled)
            {
                var localAc = MidMissionV03Helpers.FindLocalPlayerAircraft();
                if (MidMissionV03Helpers.IsLiveActiveUnit(localAc)) return localAc;
            }
            return null;
        }

        private static Unit FindLiveUnit(SavedUnitDto dto)
        {
            if (!string.IsNullOrEmpty(dto.private_uniqueName) &&
                UnitRegistry.customIDLookup != null &&
                UnitRegistry.customIDLookup.TryGetValue(dto.private_uniqueName, out var byId) &&
                MidMissionV03Helpers.IsLiveActiveUnit(byId))
                return byId;

            if (_pidIndex != null)
            {
                Unit byPid;
                if (dto.persistentId != 0 && _pidIndex.TryGetValue(dto.persistentId, out byPid) && MidMissionV03Helpers.IsLiveActiveUnit(byPid))
                    return byPid;
                return null;
            }
            if (UnitRegistry.allUnits == null) return null;
            foreach (var u in UnitRegistry.allUnits)
            {
                if (!MidMissionV03Helpers.IsLiveActiveUnit(u)) continue;
                if (!string.IsNullOrEmpty(dto.private_uniqueName) && u.UniqueName == dto.private_uniqueName)
                    return u;
                if (dto.persistentId != 0 && u.persistentID.Id == dto.persistentId)
                    return u;
            }
            return null;
        }

        private static Unit FindSpawnedUnit(SavedUnitDto dto)
        {
            if (dto == null || string.IsNullOrEmpty(dto.private_uniqueName)) return null;
            try
            {
                if (UnitRegistry.customIDLookup != null &&
                    UnitRegistry.customIDLookup.TryGetValue(dto.private_uniqueName, out var byId) &&
                    MidMissionV03Helpers.IsLiveActiveUnit(byId))
                    return byId;
                if (UnitRegistry.allUnits != null)
                    foreach (var u in UnitRegistry.allUnits)
                        if (MidMissionV03Helpers.IsLiveActiveUnit(u) && u.UniqueName == dto.private_uniqueName)
                            return u;
            }
            catch { }
            return null;
        }

        private static bool TryTeleportUnit(Unit unit, SavedUnitDto dto, float speedHint)
        {
            return TryTeleportUnit(unit, dto, speedHint, null);
        }

        private static bool TryTeleportUnit(Unit unit, SavedUnitDto dto, float speedHint, SavedAircraftDto acDto)
        {
            if (unit == null || dto.globalPosition == null || dto.rotation == null) return false;

            Collider[] colliders = null;
            bool[] prevCol = null;
            bool deferColliderRestore = false;
            Vector3 desiredVel = Vector3.zero;
            Vector3 desiredAng = Vector3.zero;
            string velSource = "zero";
            try
            {
                var gp = new GlobalPosition(dto.globalPosition.x, dto.globalPosition.y, dto.globalPosition.z);
                var local = GlobalPositionExtensions.ToLocalPosition(gp);
                var rot = new Quaternion(dto.rotation.x, dto.rotation.y, dto.rotation.z, dto.rotation.w);

                bool isAircraft = unit is Aircraft || acDto != null;
                ResolveUnitVelocity(dto, acDto ?? (dto as SavedAircraftDto), rot, speedHint, out desiredVel, out desiredAng, out velSource);

                if (isAircraft)
                {
                    colliders = unit.GetComponentsInChildren<Collider>();
                    prevCol = new bool[colliders != null ? colliders.Length : 0];
                    if (colliders != null)
                    {
                        for (int i = 0; i < colliders.Length; i++)
                        {
                            if (colliders[i] == null) continue;
                            prevCol[i] = colliders[i].enabled;
                            colliders[i].enabled = false;
                        }
                    }
                    deferColliderRestore = true;
                }

                unit.transform.SetPositionAndRotation(local, rot);
                try { unit.NetworkstartPosition = gp; } catch { }
                try { unit.NetworkstartRotation = rot; } catch { }

                var rb = unit.rb;
                if (rb != null)
                {
                    bool wasKinematic = false;
                    try { wasKinematic = rb.isKinematic; rb.isKinematic = true; } catch { }

                    rb.position = local;
                    rb.rotation = rot;
                    try { Physics.SyncTransforms(); } catch { }

                    try { rb.isKinematic = wasKinematic; } catch { }

                    rb.velocity = desiredVel;
                    rb.angularVelocity = desiredAng;
                }

                if (unit is Aircraft ac)
                {
                    try
                    {
                        ac.startingVelocity = desiredVel;
                        ac.NetworkstartingVelocity = desiredVel;
                    }
                    catch { }
                    try { ac.velocityPrev = desiredVel; } catch { }
                    try { ac.accel = Vector3.zero; } catch { }
                }

                try { Physics.SyncTransforms(); } catch { }

                if (rb != null)
                {
                    try
                    {
                        rb.velocity = desiredVel;
                        rb.angularVelocity = desiredAng;
                    }
                    catch { }
                }

                Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "Teleport LITERAL {0} kind={1} pos=({2:F2},{3:F2},{4:F2}) velSource={5} speed={6:F2} ang={7:F3}",
                    DtoLabel(dto), dto.kind ?? (isAircraft ? "aircraft" : "?"), local.x, local.y, local.z, velSource, desiredVel.magnitude, desiredAng.magnitude));

                return true;
            }
            catch (Exception ex)
            {
                Log.LogWarning("Teleport failed for " + DtoLabel(dto) + ": " + ex.Message);
                deferColliderRestore = false;
                return false;
            }
            finally
            {
                if (colliders != null && prevCol != null)
                {
                    if (deferColliderRestore && Instance != null)
                    {
                        Instance.StartCoroutine(NoSaveCoroutines.Safe(NoSaveVerify.Track(RestoreCollidersAfterDelay(colliders, prevCol, 2.0f, unit, desiredVel, desiredAng)), "Colliders"));
                        Log.LogInfo("invuln colliders deferred 2.0s for " + DtoLabel(dto));
                    }
                    else
                    {
                        for (int i = 0; i < colliders.Length; i++)
                        {
                            if (colliders[i] == null) continue;
                            colliders[i].enabled = prevCol[i];
                        }
                    }
                }
            }
        }

        private static IEnumerator RestoreCollidersAfterDelay(Collider[] colliders, bool[] prevCol, float seconds, Unit unit, Vector3 desiredVel, Vector3 desiredAng)
        {
            if (seconds > 0f) yield return new WaitForSecondsRealtime(seconds);
            if (colliders == null || prevCol == null) yield break;
            for (int i = 0; i < colliders.Length && i < prevCol.Length; i++)
            {
                if (colliders[i] == null) continue;
                try { colliders[i].enabled = prevCol[i]; } catch { }
            }
            if (unit != null)
            {
                try
                {
                    var rb = unit.rb;
                    if (rb != null)
                    {
                        rb.velocity = desiredVel;
                        rb.angularVelocity = desiredAng;
                    }
                }
                catch { }
                try
                {
                    if (unit is Aircraft ac)
                    {
                        ac.startingVelocity = desiredVel;
                        ac.NetworkstartingVelocity = desiredVel;
                        ac.velocityPrev = desiredVel;
                    }
                }
                catch { }
            }
        }

        private static void ResolveUnitVelocity(SavedUnitDto dto, SavedAircraftDto acDto, Quaternion rot, float speedHint,
            out Vector3 desiredVel, out Vector3 desiredAng, out string velSource)
        {
            desiredVel = Vector3.zero;
            desiredAng = Vector3.zero;
            velSource = "zero";

            SavedUnitDto src = (SavedUnitDto)acDto ?? dto;
            float hint = speedHint;
            if (acDto != null && acDto.startingSpeed > hint) hint = acDto.startingSpeed;

            if (src != null && src.velocityCaptured && src.velocity != null)
            {
                desiredVel = new Vector3(src.velocity.x, src.velocity.y, src.velocity.z);
                velSource = "saved.velocity";
            }
            else if (acDto != null && hint > 0.05f)
            {
                desiredVel = rot * Vector3.forward * hint;
                velSource = "startingSpeed*forward";
            }
            else
            {
                desiredVel = Vector3.zero;
                velSource = "zero(savedOrGrounded)";
            }

            if (src != null && src.angularVelocity != null && src.velocityCaptured)
            {
                desiredAng = new Vector3(src.angularVelocity.x, src.angularVelocity.y, src.angularVelocity.z);
            }
            else
            {
                desiredAng = Vector3.zero;
            }
        }

        private static void ResolveAircraftVelocity(SavedAircraftDto acDto, Quaternion rot, float speedHint,
            out Vector3 desiredVel, out Vector3 desiredAng, out string velSource)
        {
            ResolveUnitVelocity(acDto, acDto, rot, speedHint, out desiredVel, out desiredAng, out velSource);
        }
        private static bool TryRespawnAircraft(Spawner spawner, SavedAircraftDto dto)
        {
            return MidMissionV03Helpers.SpawnAircraftFromDto(spawner, dto) != null;
        }

        private static bool TryRespawnVehicle(Spawner spawner, SavedVehicleDto dto)
        {
            try
            {
                if (!MidMissionV03Helpers.ValidateTypeExists(dto.type)) return false;
                MidMissionV03Helpers.EnsureSpawnUniqueName(dto);
                if (!string.IsNullOrEmpty(dto.private_uniqueName) && UnitRegistry.customIDLookup != null && UnitRegistry.customIDLookup.ContainsKey(dto.private_uniqueName)) return false;
                var saved = new SavedVehicle(dto.private_uniqueName ?? string.Empty)
                {
                    type = dto.type,
                    faction = dto.faction,
                    globalPosition = ToGlobal(dto.globalPosition),
                    rotation = ToQuat(dto.rotation),
                    skill = dto.skill,
                    holdPosition = dto.holdPosition
                };
                GroundVehicle spawned;
                if (!(spawner.TrySpawnVehicle(saved, out spawned) && spawned != null)) return false;
                NoSaveV0911.RegisterRespawned(saved, spawned);
                return true;
            }
            catch (Exception ex)
            {
                Log.LogWarning("TrySpawnVehicle failed: " + ex.Message);
                return false;
            }
        }

        private static bool TryRespawnShip(Spawner spawner, SavedShipDto dto)
        {
            try
            {
                if (!MidMissionV03Helpers.ValidateTypeExists(dto.type)) return false;
                MidMissionV03Helpers.EnsureSpawnUniqueName(dto);
                if (!string.IsNullOrEmpty(dto.private_uniqueName) && UnitRegistry.customIDLookup != null && UnitRegistry.customIDLookup.ContainsKey(dto.private_uniqueName)) return false;
                var saved = new SavedShip(dto.private_uniqueName ?? string.Empty)
                {
                    type = dto.type,
                    faction = dto.faction,
                    globalPosition = ToGlobal(dto.globalPosition),
                    rotation = ToQuat(dto.rotation),
                    skill = dto.skill,
                    holdPosition = dto.holdPosition
                };
                Ship spawned;
                if (!(spawner.TrySpawnShip(saved, out spawned) && spawned != null)) return false;
                NoSaveV0911.RegisterRespawned(saved, spawned);
                return true;
            }
            catch (Exception ex)
            {
                Log.LogWarning("TrySpawnShip failed: " + ex.Message);
                return false;
            }
        }

        private static GlobalPosition ToGlobal(GlobalPosDto p)
        {
            if (p == null) return new GlobalPosition(0, 0, 0);
            return new GlobalPosition(p.x, p.y, p.z);
        }

        private static Quaternion ToQuat(QuatDto q)
        {
            if (q == null) return Quaternion.identity;
            return new Quaternion(q.x, q.y, q.z, q.w);
        }

        private void WriteLegacyReflectDump()
        {
            var path = Path.Combine(_saveDir.Value, DebugFileName);
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"manifest\": { \"phase\": \"legacy-reflect\", \"utc\": \"" + DateTime.UtcNow.ToString("o") + "\" },");
            sb.AppendLine("  \"note\": \"Optional debug dump; primary save is midmission_autosave.json\"");
            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Log.LogInfo("Also wrote " + path);
        }

        private void OnDestroy()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
        }
    }

    internal static class PluginInfo
    {
        public const string PLUGIN_GUID = NOSaveSystemPlugin.PLUGIN_GUID;
        public const string PLUGIN_NAME = NOSaveSystemPlugin.PLUGIN_NAME;
        public const string PLUGIN_VERSION = NOSaveSystemPlugin.PLUGIN_VERSION;
    }

    [Serializable]
    public class MidMissionSnapshot
    {
        public SnapshotManifest manifest;
        public SnapshotMission mission;
        public List<SavedAircraftDto> aircraft;
        public List<SavedVehicleDto> vehicles;
        public List<SavedShipDto> ships;
        public List<SavedUnitDto> otherUnits;
        public List<FactionEconomyDto> factions;
        public List<ObjectiveDto> activeObjectives;
        public int formatVersion;
        public List<StateEntryDto> extState;
        public List<DatalinkDto> datalink;
        public string playerState;
        public string destroyedStatics;
        public string metaSavedLocal, metaSavedUtc, metaMission, metaAircraft, metaAircraftType;
    }

    [Serializable]
    public class SnapshotManifest
    {
        public string plugin;
        public string version;
        public string phase;
        public string utc;
        public string unity;
        public string scene;
        public string gameState;
        public string note;
    }

    [Serializable]
    public class SnapshotMission
    {
        public string name;
        public string loadKeyName;
        public string loadKey;
        public string loadKeyGroup;
        public string mapKeyType;
        public string mapKeyPath;
        public int jsonVersion;
        public bool isRunning;
        public float missionTime;
        public float currentEscalation;
        public int wrecksMaxNumber;
        public float wrecksDecayTime;
        public float tacticalThreshold;
        public float strategicThreshold;
        public int aircraftDefined;
        public int vehiclesDefined;
        public int shipsDefined;
        public string error;
    }

    [Serializable]
    public class GlobalPosDto
    {
        public float x, y, z;
    }

    [Serializable]
    public class QuatDto
    {
        public float x, y, z, w;
    }

    [Serializable]
    public class SavedUnitDto
    {
        public string kind;
        public string type;
        public string definitionName;
        public string faction;
        public string private_uniqueName;
        public string unitName;
        public GlobalPosDto globalPosition;
        public QuatDto rotation;
        public uint persistentId;
        public bool disabled;
        public string unitState;
        public GlobalPosDto velocity;
        public GlobalPosDto angularVelocity;
        public bool velocityCaptured;
        public List<PartHpDto> parts;
        public string v096;
    }

    [Serializable]
    public class SavedAircraftDto : SavedUnitDto
    {
        public bool playerControlled;
        public bool isPlayerAircraft;
        public float fuel;
        public bool fuelCaptured;
        public float skill;
        public float bravery;
        public float startingSpeed;
        public bool ignition;
        public List<string> loadoutMountKeys;
        public bool gearDeployed;
        public float radarAlt;
        public float throttle;
        public float brake;
        public bool controlsCaptured;
        public List<float> engineRpmRatios;
        public float customAxis1;
        public string pilotState;
        public string pilotStateName;
        public bool hasTakenOff;
        public float simThrottle;
        public bool pilotCaptured;
        public bool simThrottleCaptured;
        public string engineState;
        public int gearState = -1;
        public string animState;
        public float savedThrust = -1f;
        public float savedPower = -1f;
    }

    [Serializable]
    public class SavedVehicleDto : SavedUnitDto
    {
        public bool holdPosition;
        public float skill;
    }

    [Serializable]
    public class SavedShipDto : SavedUnitDto
    {
        public bool holdPosition;
        public float skill;
        public float throttle;
        public float steering;
        public bool controlsCaptured;
    }

    internal static class SnapshotJson
    {
        public static string Serialize(MidMissionSnapshot snap)
        {
            var sb = new StringBuilder(64 * 1024);
            sb.AppendLine("{");
            AppendManifest(sb, snap.manifest);
            sb.AppendLine(",");
            AppendMission(sb, snap.mission);
            sb.AppendLine(",");
            sb.Append("  \"aircraft\": ");
            AppendAircraftArray(sb, snap.aircraft);
            sb.AppendLine(",");
            sb.Append("  \"vehicles\": ");
            AppendVehicleArray(sb, snap.vehicles);
            sb.AppendLine(",");
            sb.Append("  \"ships\": ");
            AppendShipArray(sb, snap.ships);
            sb.AppendLine(",");
            sb.Append("  \"otherUnits\": ");
            AppendUnitArray(sb, snap.otherUnits);
            sb.AppendLine(",");
            sb.Append("  \"factions\": ");
            AppendFactions(sb, snap.factions);
            sb.AppendLine(",");
            sb.Append("  \"activeObjectives\": ");
            AppendObjectives(sb, snap.activeObjectives);
            sb.AppendLine(",");
            sb.Append("  \"formatVersion\": ").Append(snap.formatVersion > 0 ? snap.formatVersion : 1);
            sb.AppendLine(",");
            sb.Append("  \"extState\": ");
            AppendExtState(sb, snap.extState);
            sb.AppendLine(",");
            sb.Append("  \"datalink\": [");
            if (snap.datalink != null)
                for (int i = 0; i < snap.datalink.Count; i++)
                {
                    var d = snap.datalink[i]; if (d == null) continue;
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"faction\":\"").Append(E(d.faction ?? "")).Append("\",\"uniqueName\":\"").Append(E(d.uniqueName ?? ""))
                      .Append("\",\"x\":").Append(F(d.x)).Append(",\"y\":").Append(F(d.y)).Append(",\"z\":").Append(F(d.z))
                      .Append(",\"a\":").Append(d.attackers).Append(",\"m\":").Append(d.missileAttacks).Append("}");
                }
            sb.Append("],");
            sb.AppendLine();
            sb.Append("  \"nosavePlayerState\": \"").Append(E(snap.playerState ?? "")).AppendLine("\",");
            sb.Append("  \"nosaveDestroyedStatics\": \"").Append(E(snap.destroyedStatics ?? "")).Append("\"");
            sb.AppendLine(",");
            sb.Append("  \"nosaveMeta\": {\"savedLocal\":\"").Append(E(snap.metaSavedLocal ?? ""))
              .Append("\",\"savedUtc\":\"").Append(E(snap.metaSavedUtc ?? ""))
              .Append("\",\"missionName\":\"").Append(E(snap.metaMission ?? ""))
              .Append("\",\"aircraftName\":\"").Append(E(snap.metaAircraft ?? ""))
              .Append("\",\"aircraftType\":\"").Append(E(snap.metaAircraftType ?? ""))
              .Append("\",\"pluginVersion\":\"").Append(E(NOSaveSystemPlugin.PLUGIN_VERSION)).Append("\"}");
            sb.AppendLine();
            sb.Append("}");
            return sb.ToString();
        }

        public static MidMissionSnapshot Deserialize(string json)
        {
            var snap = new MidMissionSnapshot
            {
                aircraft = new List<SavedAircraftDto>(),
                vehicles = new List<SavedVehicleDto>(),
                ships = new List<SavedShipDto>(),
                otherUnits = new List<SavedUnitDto>(),
                factions = new List<FactionEconomyDto>(),
                activeObjectives = new List<ObjectiveDto>(),
                mission = new SnapshotMission(),
                manifest = new SnapshotManifest()
            };

            foreach (var obj in ExtractObjectArray(json, "aircraft"))
            {
                var dto = new SavedAircraftDto();
                FillUnitFromObject(obj, dto);
                dto.playerControlled = GetBool(obj, "playerControlled");
                dto.isPlayerAircraft = GetBool(obj, "isPlayerAircraft");
                dto.fuel = GetFloat(obj, "fuel");
                dto.fuelCaptured = HasKey(obj, "fuelCaptured") ? GetBool(obj, "fuelCaptured") : false;
                dto.skill = GetFloat(obj, "skill");
                dto.bravery = GetFloat(obj, "bravery");
                dto.startingSpeed = GetFloat(obj, "startingSpeed");
                dto.ignition = GetBool(obj, "ignition");
                dto.gearDeployed = GetBool(obj, "gearDeployed");
                dto.radarAlt = GetFloat(obj, "radarAlt");
                dto.velocityCaptured = GetBool(obj, "velocityCaptured");
                dto.velocity = GetVec3(obj, "velocity");
                if (dto.velocity != null && !dto.velocityCaptured)
                    dto.velocityCaptured = true;
                dto.angularVelocity = GetVec3(obj, "angularVelocity");
                dto.loadoutMountKeys = GetStringArray(obj, "loadoutMountKeys");
                dto.throttle = GetFloat(obj, "throttle");
                dto.brake = GetFloat(obj, "brake");
                dto.controlsCaptured = GetBool(obj, "controlsCaptured");
                dto.engineRpmRatios = GetFloatArray(obj, "engineRpmRatios");
                dto.customAxis1 = GetFloat(obj, "customAxis1");
                dto.pilotState = GetString(obj, "pilotState");
                dto.pilotStateName = GetString(obj, "pilotStateName");
                dto.hasTakenOff = GetBool(obj, "hasTakenOff");
                dto.simThrottle = GetFloat(obj, "simThrottle");
                dto.pilotCaptured = GetBool(obj, "pilotCaptured");
                dto.simThrottleCaptured = GetBool(obj, "simThrottleCaptured");
                dto.engineState = GetString(obj, "engineState");
                dto.gearState = HasKey(obj, "gearState") ? (int)GetFloat(obj, "gearState") : -1;
                dto.animState = GetString(obj, "animState");
                dto.savedThrust = HasKey(obj, "savedThrust") ? GetFloat(obj, "savedThrust") : -1f;
                dto.savedPower = HasKey(obj, "savedPower") ? GetFloat(obj, "savedPower") : -1f;
                dto.parts = GetPartHpArray(obj, "parts");
                snap.aircraft.Add(dto);
            }
            foreach (var obj in ExtractObjectArray(json, "vehicles"))
            {
                var dto = new SavedVehicleDto();
                FillUnitFromObject(obj, dto);
                dto.holdPosition = GetBool(obj, "holdPosition");
                dto.skill = GetFloat(obj, "skill");
                snap.vehicles.Add(dto);
            }
            foreach (var obj in ExtractObjectArray(json, "ships"))
            {
                var dto = new SavedShipDto();
                FillUnitFromObject(obj, dto);
                dto.holdPosition = GetBool(obj, "holdPosition");
                dto.skill = GetFloat(obj, "skill");
                dto.throttle = GetFloat(obj, "throttle");
                dto.steering = GetFloat(obj, "steering");
                dto.controlsCaptured = GetBool(obj, "controlsCaptured");
                snap.ships.Add(dto);
            }
            foreach (var obj in ExtractObjectArray(json, "otherUnits"))
            {
                var dto = new SavedUnitDto();
                FillUnitFromObject(obj, dto);
                snap.otherUnits.Add(dto);
            }

            try { snap.playerState = GetString(json, "nosavePlayerState"); } catch { }
            try { snap.destroyedStatics = GetString(json, "nosaveDestroyedStatics"); } catch { }
            try
            {
                var metaObj = ExtractObject(json, "nosaveMeta");
                if (metaObj != null)
                {
                    snap.metaSavedLocal = GetString(metaObj, "savedLocal");
                    snap.metaSavedUtc = GetString(metaObj, "savedUtc");
                    snap.metaMission = GetString(metaObj, "missionName");
                    snap.metaAircraft = GetString(metaObj, "aircraftName");
                    snap.metaAircraftType = GetString(metaObj, "aircraftType");
                }
            }
            catch { }
            snap.datalink = new List<DatalinkDto>();
            try
            {
                foreach (var obj in ExtractObjectArray(json, "datalink"))
                    snap.datalink.Add(new DatalinkDto
                    {
                        faction = GetString(obj, "faction"),
                        uniqueName = GetString(obj, "uniqueName"),
                        x = GetFloat(obj, "x"), y = GetFloat(obj, "y"), z = GetFloat(obj, "z"),
                        attackers = (int)GetFloat(obj, "a"), missileAttacks = (int)GetFloat(obj, "m")
                    });
            }
            catch { }
            foreach (var obj in ExtractObjectArray(json, "factions"))
            {
                snap.factions.Add(new FactionEconomyDto
                {
                    factionName = GetString(obj, "factionName"),
                    factionFunds = GetFloat(obj, "factionFunds"),
                    factionScore = GetFloat(obj, "factionScore")
                });
            }
            foreach (var obj in ExtractObjectArray(json, "activeObjectives"))
            {
                snap.activeObjectives.Add(new ObjectiveDto
                {
                    uniqueName = GetString(obj, "uniqueName"),
                    displayName = GetString(obj, "displayName"),
                    faction = GetString(obj, "faction"),
                    status = GetString(obj, "status"),
                    completePercent = GetFloat(obj, "completePercent"),
                    objectiveType = GetString(obj, "objectiveType"),
                    hidden = GetBool(obj, "hidden"),
                    outcomesCount = (int)GetFloat(obj, "outcomesCount")
                });
            }

            var missionObj = ExtractObject(json, "mission");
            if (missionObj != null)
            {
                snap.mission.name = GetString(missionObj, "name");
                snap.mission.loadKeyName = GetString(missionObj, "loadKeyName");
                snap.mission.loadKey = GetString(missionObj, "loadKey");
                snap.mission.loadKeyGroup = GetString(missionObj, "loadKeyGroup");
                snap.mission.mapKeyType = GetString(missionObj, "mapKeyType");
                snap.mission.mapKeyPath = GetString(missionObj, "mapKeyPath");
                snap.mission.missionTime = GetFloat(missionObj, "missionTime");
                snap.mission.currentEscalation = GetFloat(missionObj, "currentEscalation");
                snap.mission.wrecksMaxNumber = (int)GetFloat(missionObj, "wrecksMaxNumber");
                snap.mission.wrecksDecayTime = GetFloat(missionObj, "wrecksDecayTime");
                snap.mission.tacticalThreshold = GetFloat(missionObj, "tacticalThreshold");
                snap.mission.strategicThreshold = GetFloat(missionObj, "strategicThreshold");
                snap.mission.jsonVersion = (int)GetFloat(missionObj, "jsonVersion");
                snap.mission.isRunning = GetBool(missionObj, "isRunning");
            }

            var manifestObj = ExtractObject(json, "manifest");
            if (manifestObj != null)
            {
                snap.manifest.plugin = GetString(manifestObj, "plugin");
                snap.manifest.version = GetString(manifestObj, "version");
                snap.manifest.phase = GetString(manifestObj, "phase");
                snap.manifest.utc = GetString(manifestObj, "utc");
            }

            snap.formatVersion = HasKey(json, "formatVersion") ? (int)GetFloat(json, "formatVersion") : 1;
            snap.extState = new List<StateEntryDto>();
            if (snap.formatVersion >= 2)
            {
                foreach (var obj in ExtractObjectArray(json, "extState"))
                {
                    var d = ParseFlatObject(obj);
                    string sc, tg, k, vt, v;
                    d.TryGetValue("scope", out sc); d.TryGetValue("target", out tg); d.TryGetValue("key", out k);
                    d.TryGetValue("vtype", out vt); d.TryGetValue("value", out v);
                    if (string.IsNullOrEmpty(sc) || string.IsNullOrEmpty(k)) continue;
                    snap.extState.Add(new StateEntryDto { scope = sc, target = tg ?? "", key = k, vtype = vt, value = v });
                }
            }

            return snap;
        }

        private static void FillUnitFromObject(string obj, SavedUnitDto dto)
        {
            dto.kind = GetString(obj, "kind");
            dto.type = GetString(obj, "type");
            dto.definitionName = GetString(obj, "definitionName");
            dto.faction = GetString(obj, "faction");
            dto.private_uniqueName = GetString(obj, "private_uniqueName");
            dto.unitName = GetString(obj, "unitName");
            dto.unitState = GetString(obj, "unitState");
            dto.persistentId = GetUInt(obj, "persistentId");
            dto.disabled = GetBool(obj, "disabled");
            var gp = ExtractObject(obj, "globalPosition");
            if (gp != null)
                dto.globalPosition = new GlobalPosDto { x = GetFloat(gp, "x"), y = GetFloat(gp, "y"), z = GetFloat(gp, "z") };
            var rot = ExtractObject(obj, "rotation");
            if (rot != null)
                dto.rotation = new QuatDto { x = GetFloat(rot, "x"), y = GetFloat(rot, "y"), z = GetFloat(rot, "z"), w = GetFloat(rot, "w") };
            dto.velocityCaptured = GetBool(obj, "velocityCaptured");
            dto.velocity = GetVec3(obj, "velocity");
            if (dto.velocity != null && !dto.velocityCaptured)
                dto.velocityCaptured = true;
            dto.angularVelocity = GetVec3(obj, "angularVelocity");
            dto.parts = GetPartHpArray(obj, "parts");
            dto.v096 = GetString(obj, "v096");
        }

        private static void AppendManifest(StringBuilder sb, SnapshotManifest m)
        {
            sb.Append("  \"manifest\": {");
            sb.Append("\"plugin\":\"").Append(E(m.plugin)).Append("\",");
            sb.Append("\"version\":\"").Append(E(m.version)).Append("\",");
            sb.Append("\"phase\":\"").Append(E(m.phase)).Append("\",");
            sb.Append("\"utc\":\"").Append(E(m.utc)).Append("\",");
            sb.Append("\"unity\":\"").Append(E(m.unity)).Append("\",");
            sb.Append("\"scene\":\"").Append(E(m.scene)).Append("\",");
            sb.Append("\"gameState\":\"").Append(E(m.gameState)).Append("\",");
            sb.Append("\"note\":\"").Append(E(m.note)).Append("\"");
            sb.Append("}");
        }

        private static void AppendMission(StringBuilder sb, SnapshotMission m)
        {
            sb.Append("  \"mission\": {");
            sb.Append("\"name\":\"").Append(E(m.name)).Append("\",");
            sb.Append("\"loadKeyName\":\"").Append(E(m.loadKeyName)).Append("\",");
            sb.Append("\"loadKey\":\"").Append(E(m.loadKey)).Append("\",");
            sb.Append("\"loadKeyGroup\":\"").Append(E(m.loadKeyGroup)).Append("\",");
            sb.Append("\"mapKeyType\":\"").Append(E(m.mapKeyType)).Append("\",");
            sb.Append("\"mapKeyPath\":\"").Append(E(m.mapKeyPath)).Append("\",");
            sb.Append("\"jsonVersion\":").Append(m.jsonVersion).Append(",");
            sb.Append("\"isRunning\":").Append(m.isRunning ? "true" : "false").Append(",");
            sb.Append("\"missionTime\":").Append(F(m.missionTime)).Append(","); sb.Append("\"currentEscalation\":").Append(F(m.currentEscalation)).Append(","); sb.Append("\"wrecksMaxNumber\":").Append(m.wrecksMaxNumber).Append(","); sb.Append("\"wrecksDecayTime\":").Append(F(m.wrecksDecayTime)).Append(","); sb.Append("\"tacticalThreshold\":").Append(F(m.tacticalThreshold)).Append(","); sb.Append("\"strategicThreshold\":").Append(F(m.strategicThreshold)).Append(",");
            sb.Append("\"aircraftDefined\":").Append(m.aircraftDefined).Append(",");
            sb.Append("\"vehiclesDefined\":").Append(m.vehiclesDefined).Append(",");
            sb.Append("\"shipsDefined\":").Append(m.shipsDefined);
            if (!string.IsNullOrEmpty(m.error))
                sb.Append(",\"error\":\"").Append(E(m.error)).Append("\"");
            sb.Append("}");
        }

        private static void AppendAircraftArray(StringBuilder sb, List<SavedAircraftDto> list)
        {
            sb.Append("[");
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var a = list[i];
                    sb.Append("{");
                    AppendUnitFields(sb, a);
                    sb.Append(",\"playerControlled\":").Append(a.playerControlled ? "true" : "false");
                    sb.Append(",\"isPlayerAircraft\":").Append(a.isPlayerAircraft ? "true" : "false");
                    sb.Append(",\"fuel\":").Append(F(a.fuel));
                    sb.Append(",\"fuelCaptured\":").Append(a.fuelCaptured ? "true" : "false");
                    sb.Append(",\"skill\":").Append(F(a.skill));
                    sb.Append(",\"bravery\":").Append(F(a.bravery));
                    sb.Append(",\"startingSpeed\":").Append(F(a.startingSpeed));
                    sb.Append(",\"ignition\":").Append(a.ignition ? "true" : "false");
                    sb.Append(",\"gearDeployed\":").Append(a.gearDeployed ? "true" : "false");
                    sb.Append(",\"radarAlt\":").Append(F(a.radarAlt));
                    sb.Append(",\"loadoutMountKeys\":");
                    AppendStringArray(sb, a.loadoutMountKeys);
                    sb.Append(",\"throttle\":").Append(F(a.throttle));
                    sb.Append(",\"brake\":").Append(F(a.brake));
                    sb.Append(",\"controlsCaptured\":").Append(a.controlsCaptured ? "true" : "false");
                    sb.Append(",\"engineRpmRatios\":");
                    AppendFloatArray(sb, a.engineRpmRatios);
                    sb.Append(",\"customAxis1\":").Append(F(a.customAxis1));
                    sb.Append(",\"pilotState\":\"").Append(E(a.pilotState ?? "")).Append("\"");
                    sb.Append(",\"pilotStateName\":\"").Append(E(a.pilotStateName ?? "")).Append("\"");
                    sb.Append(",\"hasTakenOff\":").Append(a.hasTakenOff ? "true" : "false");
                    sb.Append(",\"simThrottle\":").Append(F(a.simThrottle));
                    sb.Append(",\"pilotCaptured\":").Append(a.pilotCaptured ? "true" : "false");
                    sb.Append(",\"simThrottleCaptured\":").Append(a.simThrottleCaptured ? "true" : "false");
                    sb.Append(",\"engineState\":\"").Append(E(a.engineState ?? "")).Append("\"");
                    sb.Append(",\"gearState\":").Append(a.gearState.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(",\"animState\":\"").Append(E(a.animState ?? "")).Append("\"");
                    sb.Append(",\"savedThrust\":").Append(F(a.savedThrust));
                    sb.Append(",\"savedPower\":").Append(F(a.savedPower));
                    sb.Append("}");
                }
            }
            sb.Append("]");
        }

        private static void AppendVehicleArray(StringBuilder sb, List<SavedVehicleDto> list)
        {
            sb.Append("[");
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var a = list[i];
                    sb.Append("{");
                    AppendUnitFields(sb, a);
                    sb.Append(",\"holdPosition\":").Append(a.holdPosition ? "true" : "false");
                    sb.Append(",\"skill\":").Append(F(a.skill));
                    sb.Append("}");
                }
            }
            sb.Append("]");
        }

        private static void AppendShipArray(StringBuilder sb, List<SavedShipDto> list)
        {
            sb.Append("[");
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var a = list[i];
                    sb.Append("{");
                    AppendUnitFields(sb, a);
                    sb.Append(",\"holdPosition\":").Append(a.holdPosition ? "true" : "false");
                    sb.Append(",\"skill\":").Append(F(a.skill));
                    sb.Append(",\"throttle\":").Append(F(a.throttle));
                    sb.Append(",\"steering\":").Append(F(a.steering));
                    sb.Append(",\"controlsCaptured\":").Append(a.controlsCaptured ? "true" : "false");
                    sb.Append("}");
                }
            }
            sb.Append("]");
        }

        private static void AppendUnitArray(StringBuilder sb, List<SavedUnitDto> list)
        {
            sb.Append("[");
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("{");
                    AppendUnitFields(sb, list[i]);
                    sb.Append("}");
                }
            }
            sb.Append("]");
        }

        private static void AppendFactions(StringBuilder sb, List<FactionEconomyDto> list)
        {
            sb.Append("[");
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var f = list[i];
                    sb.Append("{\"factionName\":\"").Append(E(f.factionName)).Append("\",");
                    sb.Append("\"factionFunds\":").Append(F(f.factionFunds)).Append(",");
                    sb.Append("\"factionScore\":").Append(F(f.factionScore)).Append("}");
                }
            }
            sb.Append("]");
        }

        private static void AppendObjectives(StringBuilder sb, List<ObjectiveDto> list)
        {
            sb.Append("[");
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var o = list[i];
                    sb.Append("{\"uniqueName\":\"").Append(E(o.uniqueName)).Append("\",");
                    sb.Append("\"displayName\":\"").Append(E(o.displayName)).Append("\",");
                    sb.Append("\"faction\":\"").Append(E(o.faction)).Append("\",");
                    sb.Append("\"status\":\"").Append(E(o.status)).Append("\",");
                    sb.Append("\"completePercent\":").Append(F(o.completePercent)).Append(",");
                    sb.Append("\"objectiveType\":\"").Append(E(o.objectiveType)).Append("\",");
                    sb.Append("\"hidden\":").Append(o.hidden ? "true" : "false").Append(",");
                    sb.Append("\"outcomesCount\":").Append(o.outcomesCount).Append("}");
                }
            }
            sb.Append("]");
        }

        private static void AppendExtState(StringBuilder sb, List<StateEntryDto> list)
        {
            sb.Append("[");
            if (list != null)
            {
                bool first = true;
                foreach (var e in list)
                {
                    if (e == null) continue;
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("\n    {\"scope\":\"").Append(E(e.scope))
                      .Append("\",\"target\":\"").Append(E(e.target))
                      .Append("\",\"key\":\"").Append(E(e.key))
                      .Append("\",\"vtype\":\"").Append(E(e.vtype))
                      .Append("\",\"value\":\"").Append(E(e.value)).Append("\"}");
                }
            }
            sb.Append("]");
        }

        private static Dictionary<string, string> ParseFlatObject(string obj)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            int i = 0, n = obj.Length;
            string pendingKey = null;
            while (i < n)
            {
                char c = obj[i];
                if (c == '"')
                {
                    var sb = new StringBuilder();
                    i++;
                    while (i < n)
                    {
                        char ch = obj[i++];
                        if (ch == '\\' && i < n)
                        {
                            char esc = obj[i++];
                            if (esc == 'n') sb.Append('\n'); else if (esc == 'r') sb.Append('\r'); else if (esc == 't') sb.Append('\t'); else sb.Append(esc);
                            continue;
                        }
                        if (ch == '"') break;
                        sb.Append(ch);
                    }
                    if (pendingKey == null) pendingKey = sb.ToString();
                    else { d[pendingKey] = sb.ToString(); pendingKey = null; }
                    continue;
                }
                if (pendingKey != null && c != ':' && c != ',' && c != '{' && c != '}' && !char.IsWhiteSpace(c))
                {
                    int st = i;
                    while (i < n && obj[i] != ',' && obj[i] != '}') i++;
                    d[pendingKey] = obj.Substring(st, i - st).Trim();
                    pendingKey = null;
                    continue;
                }
                i++;
            }
            return d;
        }

        private static uint GetUInt(string obj, string key)
        {
            var marker = "\"" + key + "\"";
            int idx = obj.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return 0;
            idx = obj.IndexOf(':', idx);
            if (idx < 0) return 0;
            idx++;
            while (idx < obj.Length && char.IsWhiteSpace(obj[idx])) idx++;
            int start = idx;
            while (idx < obj.Length && (char.IsDigit(obj[idx]) || obj[idx] == '.' || obj[idx] == 'e' || obj[idx] == 'E' || obj[idx] == '+' || obj[idx] == '-')) idx++;
            var s = obj.Substring(start, idx - start);
            uint u;
            if (uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out u)) return u;
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 0 && d <= uint.MaxValue) return (uint)d;
            return 0;
        }

        private static bool HasKey(string obj, string key)
        {
            return obj.IndexOf("\"" + key + "\"", StringComparison.Ordinal) >= 0;
        }

        private static void AppendStringArray(StringBuilder sb, List<string> keys)
        {
            sb.Append("[");
            if (keys != null)
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("\"").Append(E(keys[i])).Append("\"");
                }
            }
            sb.Append("]");
        }

        private static void AppendUnitFields(StringBuilder sb, SavedUnitDto u)
        {
            sb.Append("\"kind\":\"").Append(E(u.kind)).Append("\",");
            sb.Append("\"type\":\"").Append(E(u.type)).Append("\",");
            sb.Append("\"definitionName\":\"").Append(E(u.definitionName)).Append("\",");
            sb.Append("\"faction\":\"").Append(E(u.faction)).Append("\",");
            sb.Append("\"private_uniqueName\":\"").Append(E(u.private_uniqueName)).Append("\",");
            sb.Append("\"unitName\":\"").Append(E(u.unitName)).Append("\",");
            sb.Append("\"persistentId\":").Append(u.persistentId).Append(",");
            sb.Append("\"disabled\":").Append(u.disabled ? "true" : "false").Append(",");
            sb.Append("\"unitState\":\"").Append(E(u.unitState)).Append("\",");
            if (u.globalPosition != null)
                sb.Append("\"globalPosition\":{\"x\":").Append(F(u.globalPosition.x))
                    .Append(",\"y\":").Append(F(u.globalPosition.y))
                    .Append(",\"z\":").Append(F(u.globalPosition.z)).Append("},");
            else
                sb.Append("\"globalPosition\":null,");
            if (u.rotation != null)
                sb.Append("\"rotation\":{\"x\":").Append(F(u.rotation.x))
                    .Append(",\"y\":").Append(F(u.rotation.y))
                    .Append(",\"z\":").Append(F(u.rotation.z))
                    .Append(",\"w\":").Append(F(u.rotation.w)).Append("}");
            else
                sb.Append("\"rotation\":null");
            sb.Append(",\"velocityCaptured\":").Append(u.velocityCaptured ? "true" : "false");
            if (u.velocity != null)
                sb.Append(",\"velocity\":{\"x\":").Append(F(u.velocity.x)).Append(",\"y\":").Append(F(u.velocity.y)).Append(",\"z\":").Append(F(u.velocity.z)).Append("}");
            else
                sb.Append(",\"velocity\":null");
            if (u.angularVelocity != null)
                sb.Append(",\"angularVelocity\":{\"x\":").Append(F(u.angularVelocity.x)).Append(",\"y\":").Append(F(u.angularVelocity.y)).Append(",\"z\":").Append(F(u.angularVelocity.z)).Append("}");
            else
                sb.Append(",\"angularVelocity\":null");
            sb.Append(",\"v096\":\"").Append(E(u.v096 ?? "")).Append("\"");
            sb.Append(",\"parts\":");
            AppendPartsArray(sb, u.parts);
        }
        private static string E(string s)
        {
            if (s == null) return "";
            StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                string rep = null;
                if (c == '\\') rep = "\\\\";
                else if (c == '"') rep = "\\\"";
                else if (c == '\r') rep = "\\r";
                else if (c == '\n') rep = "\\n";
                else if (c == '\t') rep = "\\t";
                else if (c < ' ') rep = "";
                if (rep == null) { if (sb != null) sb.Append(c); continue; }
                if (sb == null) { sb = new StringBuilder(s.Length + 8); sb.Append(s, 0, i); }
                sb.Append(rep);
            }
            return sb != null ? sb.ToString() : s;
        }

        private static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "0";
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        private static List<string> ExtractObjectArray(string json, string key)
        {
            var result = new List<string>();
            var marker = "\"" + key + "\"";
            int idx = json.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return result;
            idx = json.IndexOf('[', idx);
            if (idx < 0) return result;
            int i = idx + 1;
            while (i < json.Length)
            {
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length || json[i] == ']') break;
                if (json[i] != '{') { i++; continue; }
                int start = i;
                int depth = 0;
                for (; i < json.Length; i++)
                {
                    char c = json[i];
                    if (c == '"')
                    {
                        i++;
                        while (i < json.Length)
                        {
                            if (json[i] == '\\') { i += 2; continue; }
                            if (json[i] == '"') break;
                            i++;
                        }
                        continue;
                    }
                    if (c == '{') depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            result.Add(json.Substring(start, i - start + 1));
                            i++;
                            break;
                        }
                    }
                }
                while (i < json.Length && (char.IsWhiteSpace(json[i]) || json[i] == ',')) i++;
            }
            return result;
        }

        internal static string ExtractObject(string json, string key)
        {
            var marker = "\"" + key + "\"";
            int idx = json.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx = json.IndexOf('{', idx);
            if (idx < 0) return null;
            int start = idx;
            int depth = 0;
            for (int i = idx; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"')
                {
                    i++;
                    while (i < json.Length)
                    {
                        if (json[i] == '\\') { i += 2; continue; }
                        if (json[i] == '"') break;
                        i++;
                    }
                    continue;
                }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return json.Substring(start, i - start + 1);
                }
            }
            return null;
        }

        internal static string GetString(string obj, string key)
        {
            var marker = "\"" + key + "\"";
            int idx = obj.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx = obj.IndexOf(':', idx);
            if (idx < 0) return null;
            idx++;
            while (idx < obj.Length && char.IsWhiteSpace(obj[idx])) idx++;
            if (idx >= obj.Length) return null;
            if (obj[idx] == 'n') return null;
            if (obj[idx] != '"') return null;
            idx++;
            var sb = new StringBuilder();
            while (idx < obj.Length)
            {
                char c = obj[idx++];
                if (c == '\\' && idx < obj.Length)
                {
                    char n = obj[idx++];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 'r') sb.Append('\r');
                    else if (n == 't') sb.Append('\t');
                    else sb.Append(n);
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static List<string> GetStringArray(string obj, string key)
        {
            var list = new List<string>();
            var marker = "\"" + key + "\"";
            int idx = obj.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return list;
            idx = obj.IndexOf('[', idx);
            if (idx < 0) return list;
            int i = idx + 1;
            while (i < obj.Length)
            {
                while (i < obj.Length && (char.IsWhiteSpace(obj[i]) || obj[i] == ',')) i++;
                if (i >= obj.Length || obj[i] == ']') break;
                if (obj[i] != '"') { i++; continue; }
                i++;
                var sb = new StringBuilder();
                while (i < obj.Length)
                {
                    char ch = obj[i++];
                    if (ch == '\\' && i < obj.Length) { sb.Append(obj[i++]); continue; }
                    if (ch == '"') break;
                    sb.Append(ch);
                }
                list.Add(sb.ToString());
            }
            return list;
        }
        private static GlobalPosDto GetVec3(string obj, string key)
        {
            var marker = "\"" + key + "\"";
            int idx = obj.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx = obj.IndexOf(':', idx);
            if (idx < 0) return null;
            idx++;
            while (idx < obj.Length && char.IsWhiteSpace(obj[idx])) idx++;
            if (idx >= obj.Length) return null;
            if (obj[idx] == 'n') return null;
            if (obj[idx] != '{') return null;
            int start = idx;
            int depth = 0;
            for (int i = idx; i < obj.Length; i++)
            {
                char c = obj[i];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        var blob = obj.Substring(start, i - start + 1);
                        return new GlobalPosDto
                        {
                            x = GetFloat(blob, "x"),
                            y = GetFloat(blob, "y"),
                            z = GetFloat(blob, "z")
                        };
                    }
                }
            }
            return null;
        }

        private static float GetFloat(string obj, string key)
        {
            var marker = "\"" + key + "\"";
            int idx = obj.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return 0f;
            idx = obj.IndexOf(':', idx);
            if (idx < 0) return 0f;
            idx++;
            while (idx < obj.Length && char.IsWhiteSpace(obj[idx])) idx++;
            int start = idx;
            while (idx < obj.Length && (char.IsDigit(obj[idx]) || obj[idx] == '-' || obj[idx] == '+' || obj[idx] == '.' || obj[idx] == 'e' || obj[idx] == 'E'))
                idx++;
            var s = obj.Substring(start, idx - start);
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v);
            return v;
        }

        private static void AppendFloatArray(StringBuilder sb, List<float> vals)
        {
            sb.Append("[");
            if (vals != null)
            {
                for (int i = 0; i < vals.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append(F(vals[i]));
                }
            }
            sb.Append("]");
        }

        private static void AppendPartsArray(StringBuilder sb, List<PartHpDto> parts)
        {
            sb.Append("[");
            if (parts != null)
            {
                for (int i = 0; i < parts.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var p = parts[i];
                    if (p == null) { sb.Append("null"); continue; }
                    sb.Append("{\"id\":").Append(p.id);
                    sb.Append(",\"hitPoints\":").Append(F(p.hitPoints));
                    sb.Append(",\"detached\":").Append(p.detached ? "true" : "false");
                    sb.Append(",\"critical\":").Append(p.critical ? "true" : "false");
                    sb.Append(",\"name\":\"").Append(E(p.name)).Append("\"}");
                }
            }
            sb.Append("]");
        }

        private static List<float> GetFloatArray(string obj, string key)
        {
            var list = new List<float>();
            var marker = "\"" + key + "\"";
            int idx = obj.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return list;
            idx = obj.IndexOf('[', idx);
            if (idx < 0) return list;
            int i = idx + 1;
            while (i < obj.Length)
            {
                while (i < obj.Length && (char.IsWhiteSpace(obj[i]) || obj[i] == ',')) i++;
                if (i >= obj.Length || obj[i] == ']') break;
                int start = i;
                while (i < obj.Length && (char.IsDigit(obj[i]) || obj[i] == '-' || obj[i] == '+' || obj[i] == '.' || obj[i] == 'e' || obj[i] == 'E'))
                    i++;
                if (i == start)
                {
                    while (i < obj.Length && obj[i] != ',' && obj[i] != ']') i++;
                    continue;
                }
                var s = obj.Substring(start, i - start);
                if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    list.Add(v);
            }
            return list;
        }

        private static List<PartHpDto> GetPartHpArray(string obj, string key)
        {
            var list = new List<PartHpDto>();
            foreach (var partObj in ExtractObjectArray(obj, key))
            {
                list.Add(new PartHpDto
                {
                    id = (byte)GetFloat(partObj, "id"),
                    hitPoints = GetFloat(partObj, "hitPoints"),
                    detached = GetBool(partObj, "detached"),
                    critical = GetBool(partObj, "critical"),
                    name = GetString(partObj, "name")
                });
            }
            return list;
        }

        private static bool GetBool(string obj, string key)
        {
            var marker = "\"" + key + "\"";
            int idx = obj.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return false;
            idx = obj.IndexOf(':', idx);
            if (idx < 0) return false;
            idx++;
            while (idx < obj.Length && char.IsWhiteSpace(obj[idx])) idx++;
            return idx + 4 <= obj.Length && string.Compare(obj, idx, "true", 0, 4, StringComparison.OrdinalIgnoreCase) == 0;
        }
    }
}

