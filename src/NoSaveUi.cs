using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using NuclearOption.SceneLoading;
using UnityEngine;

namespace NOSaveSystem
{
    internal static class NoSaveUi
    {
        
        private static GameObject _pauseOverlay;
        private static bool _continueInProgress;
        private static bool _autoContinueFired;
        private static float _menuEnterRealtime = -1f;

        internal static string Ascii(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            bool clean = true;
            foreach (char c in s) if (c > 126 || (c < 32 && c != '\n')) { clean = false; break; }
            if (clean) return s;
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                switch (ch)
                {
                    case '\u2014': case '\u2013': case '\u2012': case '\u2212': sb.Append('-'); continue;
                    case '\u2026': sb.Append("..."); continue;
                    case '\u2018': case '\u2019': case '\u00b4': sb.Append('\''); continue;
                    case '\u201c': case '\u201d': sb.Append('"'); continue;
                    case '\u00a0': case '\u2009': case '\u202f': sb.Append(' '); continue;
                    case '\u00b7': case '\u2022': sb.Append('*'); continue;
                    case '\n': sb.Append('\n'); continue;
                }
                if (ch < 32) continue;
                sb.Append(ch <= 126 ? ch : '?');
            }
            return sb.ToString();
        }

        internal static void SetStatus(string msg, float seconds = 4f)
        {
            msg = Ascii(msg);
            try { MidMissionV03Helpers.ShowGameFeedback(msg); } catch { }
            NOSaveSystemPlugin.Log?.LogInfo("[UI] " + msg);
        }

        private static bool IsPauseOverlayActive()
        {
            try
            {
                var np = NetworkPause.i;
                if (np == null) return false;
                if (np.NetworkserverPaused) return true;
                if (np.overlay != null && np.overlay.activeInHierarchy) return true;
            }
            catch { }

            if (_pauseOverlay != null)
            {
                try { if (_pauseOverlay.activeInHierarchy) return true; } catch { }
            }

            try
            {
                if (Time.timeScale < 0.01f && GameManager.gameState == GameState.SinglePlayer)
                    return true;
            }
            catch { }
            return false;
        }

        private static bool IsMainMenu()
        {
            try { return GameManager.gameState == GameState.Menu; }
            catch { return false; }
        }

        private static string _metaPath, _metaName;
        private static DateTime _metaStamp;
        private static bool _metaOk;
        private static float _metaCheckedAt = -100f;
        private const float MetaRefreshSec = 1f;

        private static bool TryReadSaveMeta(out string missionName, out DateTime stamp, out string path)
        {
            float now = Time.realtimeSinceStartup;
            if (now - _metaCheckedAt < MetaRefreshSec && now >= _metaCheckedAt)
            {
                missionName = _metaName; stamp = _metaStamp; path = _metaPath;
                return _metaOk;
            }
            _metaCheckedAt = now;
            try
            {
                var plug = NOSaveSystemPlugin.Instance;
                string p = plug != null ? Path.Combine(plug.SaveDirectory, NOSaveSystemPlugin.AutosaveFileName) : null;
                if (p != null && _metaOk && p == _metaPath && File.Exists(p) && File.GetLastWriteTime(p) == _metaStamp)
                {
                    missionName = _metaName; stamp = _metaStamp; path = _metaPath;
                    return true;
                }
            }
            catch { }
            _metaOk = TryReadSaveMetaUncached(out _metaName, out _metaStamp, out _metaPath);
            missionName = _metaName; stamp = _metaStamp; path = _metaPath;
            return _metaOk;
        }

        private static bool TryReadSaveMetaUncached(out string missionName, out DateTime stamp, out string path)
        {
            missionName = null;
            stamp = default;
            path = null;
            try
            {
                var plug = NOSaveSystemPlugin.Instance;
                if (plug == null) return false;
                path = Path.Combine(plug.SaveDirectory, NOSaveSystemPlugin.AutosaveFileName);
                if (!File.Exists(path)) return false;
                stamp = File.GetLastWriteTime(path);
                var json = File.ReadAllText(path, Encoding.UTF8);
                missionName = ExtractJsonString(json, "name");
                if (string.IsNullOrEmpty(missionName))
                    missionName = ExtractJsonString(json, "loadKeyName");
                if (string.IsNullOrEmpty(missionName))
                    missionName = "(unknown mission)";
                return true;
            }
            catch { return false; }
        }

        private static string ExtractJsonString(string json, string key)
        {
            var marker = "\"" + key + "\"";
            int idx = json.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx = json.IndexOf(':', idx);
            if (idx < 0) return null;
            idx++;
            while (idx < json.Length && char.IsWhiteSpace(json[idx])) idx++;
            if (idx >= json.Length || json[idx] != '"') return null;
            idx++;
            var sb = new StringBuilder();
            while (idx < json.Length)
            {
                char c = json[idx++];
                if (c == '\\' && idx < json.Length) { sb.Append(json[idx++]); continue; }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        internal static bool ContinueInProgress => _continueInProgress;

        internal static IEnumerator ContinueFromFileCo(string path)
        {
            if (!string.IsNullOrEmpty(path)) NOSaveSystemPlugin.LoadPathOverride = path;
            try { yield return ContinueAfterRebootCoroutine(); }
            finally
            {
                if (!string.IsNullOrEmpty(path) && NOSaveSystemPlugin.LoadPathOverride == path)
                {
                    NOSaveSystemPlugin.LoadPathOverride = null;
                    var plug = NOSaveSystemPlugin.Instance;
                    try { NOSaveSystemPlugin.LastLoadedPath = plug != null && string.Equals(path, plug.AutosavePath, StringComparison.OrdinalIgnoreCase) ? null : path; } catch { }
                }
            }
        }

        internal static IEnumerator ContinueAfterRebootCoroutine()
        {
            if (_continueInProgress) yield break;
            if (!NOSaveSystemPlugin.GateSp("Continue", true)) yield break;
            var host0 = NOSaveSystemPlugin.Instance;
            if (host0 == null) { SetStatus("NOSave plugin not ready"); yield break; }
            if (!host0.TryBeginLongBusy("Continue")) { SetStatus("NOSave busy"); yield break; }
            _continueInProgress = true;
            try
            {
            SetStatus("NOSave Continue: loading mission...", 8f);

            MidMissionSnapshot snap = null;
            string savePath = null;
            try
            {
                var plug = NOSaveSystemPlugin.Instance;
                savePath = plug.ResolveLoadPath();
                if (!File.Exists(savePath))
                {
                    SetStatus("NOSave Continue: no midmission_autosave.json");
                    _continueInProgress = false;
                    yield break;
                }
                var json = File.ReadAllText(savePath, Encoding.UTF8);
                snap = SnapshotJson.Deserialize(json);
            }
            catch (Exception ex)
            {
                SetStatus("NOSave Continue: parse failed: " + ex.Message);
                _continueInProgress = false;
                yield break;
            }

            if (snap == null || snap.mission == null)
            {
                SetStatus("NOSave Continue: empty snapshot");
                _continueInProgress = false;
                yield break;
            }

            string loadName = snap.mission.loadKeyName ?? snap.mission.name;
            string loadKey = snap.mission.loadKey ?? loadName;
            if (string.IsNullOrEmpty(loadName))
            {
                SetStatus("NOSave Continue: save missing loadKey/name - start mission manually, then Load");
                _continueInProgress = false;
                yield break;
            }

            var plugInst = NOSaveSystemPlugin.Instance;
            bool continueViaLegacyHost = false;
            if (plugInst != null && plugInst.IsMissionReloadLoadMode())
            {
                SetStatus("NOSave Continue: MissionReload...", 10f);
                bool mrOk = false;
                yield return MissionReload.RunMissionReloadCo(true, ok => { mrOk = ok; });
                if (mrOk)
                {
                    SetStatus("NOSave Continue: Restored (MissionReload).", 5f);
                }
                else
                {
                    bool running = false;
                    bool atMenu = false;
                    try { running = MissionManager.IsRunning; } catch { }
                    try { atMenu = GameManager.gameState == GameState.Menu; } catch { }
                    string fail = MissionReload.LastFailReason ?? "unknown";
                    bool hostStarted = MissionReload.LastHostStarted;
                    NOSaveSystemPlugin.Log?.LogWarning(
                        "Continue: MissionReload FAILED fail=" + fail +
                        " hostStarted=" + hostStarted + " running=" + running + " atMenu=" + atMenu);

                    if (fail == "core_failed" || MissionReload.LastRefusedReentrant)
                    {
                        NOSaveSystemPlugin.Log?.LogError("Continue: MissionReload " + fail + " - NOT running Swap fallback (see last_restore_plan.txt)");
                        SetStatus("NOSave Continue: restore aborted (" + fail + ") - see last_restore_plan.txt", 10f);
                    }
                    else if (running || MissionReload.LastMissionRunning)
                    {
                        NOSaveSystemPlugin.Log?.LogWarning("Continue: MR fail with mission running -> Swap-core fallback");
                        SetStatus("NOSave Continue: MissionReload failed (" + fail + ") -> Swap fallback...", 10f);
                        MidMissionV03Helpers.ShowGameFeedback("NOSave Continue: Swap fallback");
                        yield return plugInst.RestoreMidMissionSnapshotCoreCo();
                        SetStatus("NOSave Continue: Restored (Swap fallback after MissionReload fail).", 5f);
                    }
                    else if (atMenu && !hostStarted)
                    {
                        NOSaveSystemPlugin.Log?.LogWarning("Continue: MR fail at menu (no host) -> legacy Continue host+Swap");
                        SetStatus("NOSave Continue: MissionReload failed (" + fail + ") -> legacy host...", 10f);
                        continueViaLegacyHost = true;
                    }
                    else
                    {
                        NOSaveSystemPlugin.Log?.LogWarning("Continue: MR fail ambiguous state - NOT auto-hosting. Retry Continue or F6 Swap in-mission.");
                        SetStatus("NOSave Continue: MissionReload failed () - retry Continue, or start mission + F6 Swap.", 12f);
                        MidMissionV03Helpers.ShowGameFeedback("NOSave Continue: MissionReload failed - retry");
                    }
                }
            }
            else
            {
                continueViaLegacyHost = true;
            }

            if (continueViaLegacyHost)
            {
                Mission mission = null;
                string lastErr = null;
                try
                {
                    mission = TryLoadMissionByKey(loadName, loadKey, snap.mission.loadKeyGroup, out lastErr);
                }
                catch (Exception ex)
                {
                    lastErr = ex.Message;
                }

                if (mission == null)
                {
                    SetStatus("NOSave Continue: could not load '" + loadName + "' (" + lastErr + "). Manual: start that mission then Load.");
                    _continueInProgress = false;
                    yield break;
                }

                try
                {
                    MissionManager.SetMission(mission, false);
                    var map = mission.MapKey;
                    var opts = new HostOptions(SocketType.Offline, GameState.SinglePlayer, map);
                    var nm = NetworkManagerNuclearOption.i;
                    if (nm == null) throw new InvalidOperationException("NetworkManagerNuclearOption.i is null");
                    nm.StartHost(opts);
                    SetStatus("NOSave Continue: hosting '" + (mission.Name ?? loadName) + "'...", 10f);
                }
                catch (Exception ex)
                {
                    SetStatus("NOSave Continue: StartHost failed: " + ex.Message + " - start mission manually then Load.");
                    _continueInProgress = false;
                    yield break;
                }

                float waited = 0f;
                const float timeout = 180f;
                while (waited < timeout)
                {
                    bool running = false;
                    int acCount = 0;
                    try
                    {
                        running = MissionManager.IsRunning;
                        if (UnitRegistry.allAircraft != null) acCount = UnitRegistry.allAircraft.Count;
                    }
                    catch { }

                    if (running && acCount > 0)
                        break;

                    waited += Time.unscaledDeltaTime;
                    yield return null;
                }
                if (waited >= timeout) NoSaveWait.Timeout("continue-host", timeout, "abort Continue unless the mission is running");

                float settle = 0f;
                while (settle < 2.5f)
                {
                    settle += Time.unscaledDeltaTime;
                    yield return null;
                }

                bool runningNow = false;
                try { runningNow = MissionManager.IsRunning; } catch { }
                if (!runningNow)
                {
                    SetStatus("NOSave Continue: mission did not start in time - open same mission manually, pause, Load.");
                    _continueInProgress = false;
                    yield break;
                }

                SetStatus("NOSave Continue: applying snapshot (Swap)...", 6f);
                int loadToken = NoSaveReconcile.TryAcquireLoad("Continue");
                if (loadToken == 0) { SetStatus("NOSave busy (load in progress)"); yield break; }
                try { yield return NOSaveSystemPlugin.Instance.RestoreMidMissionSnapshotCoreCo(); }
                finally { NoSaveReconcile.ReleaseLoad(loadToken); }
                SetStatus(NOSaveSystemPlugin.LastCoreOk ? "NOSave Continue: done." : "NOSave Continue: restore aborted (see last_restore_plan.txt)", 5f);
            }
            }
            finally
            {
                _continueInProgress = false;
                try { NOSaveSystemPlugin.Instance?.EndLongBusy(); } catch { }
            }
        }

        internal static Mission TryLoadMissionByKeyPublic(string name, string key, string groupHint, out string error)
        {
            return TryLoadMissionByKey(name, key, groupHint, out error);
        }

        private static Mission TryLoadMissionByKey(string name, string key, string groupHint, out string error)
        {
            error = null;
            Mission mission = null;
            string err = null;

            var groups = new System.Collections.Generic.List<MissionGroup>();
            try
            {
                if (!string.IsNullOrEmpty(groupHint))
                {
                    if (groupHint.IndexOf("User", StringComparison.OrdinalIgnoreCase) >= 0) groups.Add(MissionGroup.User);
                    if (groupHint.IndexOf("Workshop", StringComparison.OrdinalIgnoreCase) >= 0) groups.Add(MissionGroup.Workshop);
                    if (groupHint.IndexOf("Editor", StringComparison.OrdinalIgnoreCase) >= 0) groups.Add(MissionGroup.EditorMissions);
                    if (groupHint.IndexOf("Temp", StringComparison.OrdinalIgnoreCase) >= 0) groups.Add(MissionGroup.Temp);
                    if (groupHint.IndexOf("Resource", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        groupHint.IndexOf("BuiltIn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        groupHint.IndexOf("Default", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        groupHint.IndexOf("Tutorial", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        groups.Add(MissionGroup.BuiltIn);
                        groups.Add(MissionGroup.Default);
                        groups.Add(MissionGroup.Tutorial);
                    }
                }
            }
            catch { }

            void Add(MissionGroup g) { if (g != null && !groups.Contains(g)) groups.Add(g); }
            try
            {
                Add(MissionGroup.BuiltIn);
                Add(MissionGroup.Default);
                Add(MissionGroup.Tutorial);
                Add(MissionGroup.User);
                Add(MissionGroup.Workshop);
                Add(MissionGroup.Temp);
                Add(MissionGroup.EditorMissions);
            }
            catch { }

            foreach (var g in groups)
            {
                try
                {
                    var mk = new MissionKey(name, g);
                    if (MissionSaveLoad.TryLoad(mk, out mission, out err) && mission != null)
                    {
                        error = null;
                        return mission;
                    }
                    if (!string.IsNullOrEmpty(err)) error = err;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
            }

            try
            {
                if (MissionGroup.AllGroups != null)
                {
                    foreach (var g in MissionGroup.AllGroups)
                    {
                        if (g == null) continue;
                        try
                        {
                            var mk = new MissionKey(name, g);
                            if (MissionSaveLoad.TryLoad(mk, out mission, out err) && mission != null)
                            {
                                error = null;
                                return mission;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            if (string.IsNullOrEmpty(error)) error = "mission not found in any MissionGroup";
            return null;
        }

        internal static void AutoContinueTick()
        {
            try
            {
                if (_continueInProgress || _autoContinueFired) return;
                var plug = NOSaveSystemPlugin.Instance;
                if (plug == null) return;
                float sec = plug.AutoContinueSeconds;
                if (sec <= 0f) return;
                if (!IsMainMenu()) { _menuEnterRealtime = -1f; return; }
                if (!TryReadSaveMeta(out _, out _, out _)) return;
                if (_menuEnterRealtime < 0f) _menuEnterRealtime = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - _menuEnterRealtime < sec) return;
                if (plug.IsBusyOrCoolingDown) return;
                if (!NOSaveSystemPlugin.GateSp("AutoContinue", true)) return;
                _autoContinueFired = true;
                NOSaveSystemPlugin.Log?.LogInfo("AutoContinue: firing Continue after " + sec + "s on menu");
                plug.StartCoroutine(ContinueAfterRebootCoroutine());
            }
            catch (Exception ex)
            {
                NOSaveSystemPlugin.Log?.LogWarning("AutoContinueTick: " + ex.Message);
            }
        }

        internal static void RememberPauseOverlay(GameObject overlay)
        {
            if (overlay == null) return;
            _pauseOverlay = overlay;
        }
    }

    internal class NoSaveUiBehaviour : MonoBehaviour
    {
        private void Update()
        {
            NoSaveUi.AutoContinueTick();
        }
    }

    [HarmonyPatch(typeof(NetworkPause), "Awake")]
    internal static class Patch_NetworkPause_Awake
    {
        static void Postfix(NetworkPause __instance)
        {
            try
            {
                if (__instance != null && __instance.overlay != null)
                    NoSaveUi.RememberPauseOverlay(__instance.overlay);
                NOSaveSystemPlugin.Log?.LogInfo("NetworkPause Awake: pause overlay hooked for NOSave UI");
            }
            catch (Exception ex)
            {
                NOSaveSystemPlugin.Log?.LogWarning("NetworkPause Awake postfix: " + ex.Message);
            }
        }
    }

    [HarmonyPatch(typeof(NetworkPause), "HookPausedChanged")]
    internal static class Patch_NetworkPause_HookPausedChanged
    {
        static void Postfix(NetworkPause __instance)
        {
            try
            {
                if (__instance != null && __instance.overlay != null)
                    NoSaveUi.RememberPauseOverlay(__instance.overlay);
            }
            catch { }
        }
    }
}

