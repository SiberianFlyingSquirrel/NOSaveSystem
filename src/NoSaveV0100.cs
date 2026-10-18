using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HarmonyLib;
using NuclearOption.UI;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace NOSaveSystem
{
    /// <summary>
    /// v0.10.0: native "SAVE MENU" button in the pause menu (LeaderboardMenu, MenuMode.Leaderboard) placed directly
    /// under QUIT MISSION, opening a uGUI save menu (list newest first, Load / Delete(confirm) / New Save / Close).
    /// Scene hierarchy (GameWorld level1): Canvas/MenuCanvas/Leaderboard[LeaderboardMenu]/mainOrganizer[VLG]/ButtonsPanel[VLG]
    ///   {SPACER, ResumeButton, JoinFactionButton, OptionsButton, HideUIButton, RestartMission_Button, QuitMission_Button[QuitMissionButton]}
    /// SP-host only (compliance gate).
    /// </summary>
    internal static class NoSaveV0100
    {
        internal const string ButtonName = "NOSave_SaveGameButton";
        internal const string PanelName = "NOSave_SaveMenuPanel";
        internal const string ButtonLabel = "SAVE MENU"; // v0.10.2: was "SAVE GAME"

        internal static void Log(string m) { NOSaveSystemPlugin.Log?.LogInfo("v0100: " + m); }
        internal static void Warn(string m) { NOSaveSystemPlugin.Log?.LogWarning("v0100: " + m); }

        private static Button _saveButton;
        private static LeaderboardMenu _menuFor;
        private static SaveMenuPanel _panel;
        private static string _lastFail;
        private static float _lastFailAt = -100f;
        private static string _lastHiddenReason;

        internal static bool PanelOpen
        {
            get { try { return _panel != null && _panel.gameObject.activeInHierarchy; } catch { return false; } }
        }

        internal static bool ButtonPresent(LeaderboardMenu menu)
        {
            try { return _saveButton != null && _menuFor == menu; } catch { return false; }
        }

        private static void Fail(string msg)
        {
            float now = Time.realtimeSinceStartup;
            if (msg == _lastFail && now - _lastFailAt < 10f) return;
            _lastFail = msg; _lastFailAt = now;
            Warn("button injection FAILED: " + msg);
        }

        // ------------------------------------------------------------------ metadata

        /// <summary>Fills v0.10.0 save metadata (timestamp, mission, player aircraft) on a fresh snapshot.</summary>
        internal static void FillMeta(MidMissionSnapshot snap)
        {
            if (snap == null) return;
            snap.metaSavedLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            snap.metaSavedUtc = snap.manifest != null ? snap.manifest.utc : DateTime.UtcNow.ToString("o");
            string mission = snap.mission != null ? snap.mission.name : null;
            if (string.IsNullOrEmpty(mission) && snap.mission != null) mission = snap.mission.loadKeyName;
            snap.metaMission = string.IsNullOrEmpty(mission) ? "Unknown" : mission;
            SavedAircraftDto pa = null;
            if (snap.aircraft != null) foreach (var a in snap.aircraft) if (a != null && a.isPlayerAircraft) { pa = a; break; }
            if (pa != null)
            {
                snap.metaAircraft = !string.IsNullOrEmpty(pa.definitionName) ? pa.definitionName : (!string.IsNullOrEmpty(pa.unitName) ? pa.unitName : (pa.type ?? "Unknown"));
                snap.metaAircraftType = pa.type ?? "";
            }
            else
            {
                bool noAc = false; try { noAc = NoSaveV0910.IsNoAircraft(snap); } catch { }
                snap.metaAircraft = noAc ? "No aircraft" : "Unknown";
                snap.metaAircraftType = "";
            }
        }

        // ------------------------------------------------------------------ injection

        internal static void OnMenuOpened(LeaderboardMenu menu, MenuMode mode, string src)
        {
            if (menu == null) return;
            try { if (_panel != null && _panel.gameObject.activeSelf) _panel.Close("pause menu (re)opened", false); } catch { }
            bool sp = NOSaveSystemPlugin.GateSp("SaveMenu.inject", false, true);
            if (mode != MenuMode.Leaderboard || !sp)
            {
                // compliance: never inject outside an SP host session; hide an existing clone.
                try { if (_saveButton != null) _saveButton.gameObject.SetActive(false); } catch { }
                string why = "mode=" + mode + " spHost=" + sp;
                if (why != _lastHiddenReason) { _lastHiddenReason = why; Log("SAVE MENU button not shown (" + why + ") src=" + src); }
                return;
            }
            _lastHiddenReason = null;
            if (!EnsureButton(menu, src)) return;
            try
            {
                if (!_saveButton.gameObject.activeSelf) _saveButton.gameObject.SetActive(true);
                var prt = _saveButton.transform.parent as RectTransform;
                if (prt != null) LayoutRebuilder.MarkLayoutForRebuild(prt);
                var mrt = menu.transform as RectTransform;
                if (mrt != null) LayoutRebuilder.MarkLayoutForRebuild(mrt);
            }
            catch (Exception ex) { Warn("show button: " + ex.Message); }
        }

        private static bool EnsureButton(LeaderboardMenu menu, string src)
        {
            try
            {
                if (_saveButton != null && _menuFor == menu && _saveButton.transform.IsChildOf(menu.transform)) return true;
                _saveButton = null; _menuFor = null;

                // never duplicate: re-bind an existing clone (e.g. static state lost)
                var existing = FindDeep(menu.transform, ButtonName);
                if (existing != null)
                {
                    var eb = existing.GetComponent<Button>();
                    if (eb != null)
                    {
                        eb.onClick = new Button.ButtonClickedEvent();
                        eb.onClick.AddListener(OnSaveButtonClicked);
                        _saveButton = eb; _menuFor = menu;
                        Log("existing SAVE MENU button re-bound (no duplicate) src=" + src);
                        return true;
                    }
                }

                var exit = FindExitButton(menu, out string how);
                if (exit == null) { Fail("QUIT/EXIT button not found under LeaderboardMenu '" + menu.name + "' src=" + src); return false; }
                var parent = exit.transform.parent;
                Button style = GetMenuButton(menu, "settingsButton");
                if (style == null || style.transform.parent != parent) style = exit;

                var go = CloneClean(style.gameObject, parent, ButtonName);
                if (go == null) { Fail("clone of '" + style.name + "' failed (see CloneClean FAIL) - no SAVE MENU button"); return false; }
                var btn = go.GetComponent<Button>();
                if (btn == null) { UnityEngine.Object.Destroy(go); Fail("clone has no Button"); return false; }
                btn.onClick = new Button.ButtonClickedEvent();
                btn.onClick.AddListener(OnSaveButtonClicked);
                btn.interactable = true;
                SetLabel(go, ButtonLabel);

                var ert = exit.transform as RectTransform;
                var crt = go.transform as RectTransform;
                int idx = exit.transform.GetSiblingIndex() + 1;
                go.transform.SetSiblingIndex(idx);
                var lg = parent.GetComponent<LayoutGroup>();
                if (ert != null && crt != null)
                {
                    crt.sizeDelta = ert.sizeDelta; // same footprint as QUIT MISSION
                    if (lg == null)
                    {
                        crt.anchorMin = ert.anchorMin; crt.anchorMax = ert.anchorMax; crt.pivot = ert.pivot;
                        crt.anchoredPosition = ert.anchoredPosition - new Vector2(0f, ert.rect.height + 2f);
                    }
                }
                go.SetActive(true);
                _saveButton = btn; _menuFor = menu;
                Log("SAVE MENU button injected OK: under '" + exit.name + "' (found by " + how + "), style cloned from '" + style.name +
                    "', parent '" + parent.name + "' layout=" + (lg != null ? lg.GetType().Name : "none->manual offset") +
                    " siblingIndex=" + go.transform.GetSiblingIndex() + " src=" + src);
                return true;
            }
            catch (Exception ex) { Fail("exception " + ex); return false; }
        }

        internal static Button GetMenuButton(LeaderboardMenu menu, string field)
        {
            try { return Traverse.Create(menu).Field(field).GetValue<Button>(); } catch { return null; }
        }

        private static Button FindExitButton(LeaderboardMenu menu, out string how)
        {
            how = null;
            try
            {
                var q = menu.GetComponentInChildren<QuitMissionButton>(true);
                if (q != null)
                {
                    var b = q.GetComponent<Button>();
                    if (b != null) { how = "QuitMissionButton component"; return b; }
                }
            }
            catch { }
            Button best = null;
            try
            {
                foreach (var b in menu.GetComponentsInChildren<Button>(true))
                {
                    if (b == null || b.name.StartsWith("NOSave_", StringComparison.Ordinal)) continue;
                    if (UnderNamed(b.transform, menu.transform)) continue;
                    string n = b.name.ToUpperInvariant();
                    string l = (GetLabel(b.gameObject) ?? "").ToUpperInvariant();
                    bool nameHit = n.Contains("QUIT") || n.Contains("EXIT");
                    bool textHit = l.Contains("QUIT") || l.Contains("EXIT");
                    if (!nameHit && !textHit) continue;
                    best = b; how = "fallback " + (nameHit ? "name" : "text") + " '" + (nameHit ? b.name : l) + "'";
                    if (nameHit && textHit) break;
                }
            }
            catch { }
            return best;
        }

        private static bool UnderNamed(Transform t, Transform stop)
        {
            for (var p = t.parent; p != null && p != stop; p = p.parent)
            {
                string n = p.name.ToUpperInvariant();
                if (n.Contains("CONFORM") || n.Contains("CONFIRM") || n.StartsWith("NOSAVE_", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        internal static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindDeep(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>Instantiate inside an inactive holder (no Awake/OnEnable of game scripts), strip game-logic
        /// MonoBehaviours (ButtonController/QuitMissionButton etc.), reset every Button.onClick (persistent calls too).</summary>
        /// v0.11.1 (L2): returns null (half-built object destroyed, logged) when the template or clone has no Button.
        internal static GameObject CloneClean(GameObject template, Transform parent, string name)
        {
            if (template == null || template.GetComponent<Button>() == null) { Warn("CloneClean FAIL '" + (template != null ? template.name : "null") + "': template has no Button"); return null; }
            var holder = new GameObject("NOSave_cloneHolder");
            holder.SetActive(false);
            holder.transform.SetParent(parent, false);
            GameObject go = null;
            try
            {
                go = UnityEngine.Object.Instantiate(template, holder.transform, false);
                if (go == null || go.GetComponent<Button>() == null)
                {
                    if (go != null) UnityEngine.Object.DestroyImmediate(go);
                    Warn("CloneClean FAIL '" + template.name + "': clone has no Button -> half-built object destroyed");
                    return null;
                }
                go.name = name;
                var gameAsm = typeof(LeaderboardMenu).Assembly;
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    if (mb.GetType().Assembly == gameAsm) UnityEngine.Object.DestroyImmediate(mb);
                }
                foreach (var b in go.GetComponentsInChildren<Button>(true))
                    b.onClick = new Button.ButtonClickedEvent();
                go.transform.SetParent(parent, false);
            }
            catch (Exception ex)
            {
                Warn("CloneClean FAIL '" + template.name + "': " + ex.GetType().Name + " " + ex.Message + " -> half-built object destroyed");
                if (go != null) { try { UnityEngine.Object.DestroyImmediate(go); } catch { } }
                return null;
            }
            finally { UnityEngine.Object.Destroy(holder); }
            return go;
        }

        internal static string GetLabel(GameObject go)
        {
            try
            {
                var t = go.GetComponentInChildren<TMP_Text>(true);
                if (t != null) return t.text;
                var u = go.GetComponentInChildren<Text>(true);
                if (u != null) return u.text;
            }
            catch { }
            return null;
        }

        internal static void SetLabel(GameObject go, string text)
        {
            var t = go.GetComponentInChildren<TMP_Text>(true);
            if (t != null) { t.text = text; return; }
            var u = go.GetComponentInChildren<Text>(true);
            if (u != null) u.text = text;
        }

        private static void OnSaveButtonClicked()
        {
            Log("SAVE MENU button clicked");
            try
            {
                if (!NOSaveSystemPlugin.GateSp("SaveMenu.open")) return;
                var menu = _menuFor != null ? _menuFor : SceneSingleton<LeaderboardMenu>.i;
                if (menu == null) { Warn("menu open refused: LeaderboardMenu gone"); return; }
                if (_panel == null || !_panel.transform.IsChildOf(menu.transform))
                {
                    if (_panel != null) { try { UnityEngine.Object.Destroy(_panel.gameObject); } catch { } }
                    _panel = SaveMenuPanel.Create(menu);
                }
                if (_panel != null) _panel.Open();
                else Warn("save menu panel could not be built");
            }
            catch (Exception ex) { Warn("open save menu FAIL: " + ex); }
        }

        internal static void HandleEsc()
        {
            try { if (_panel != null) _panel.HandleEsc(); } catch { }
        }

        // ------------------------------------------------------------------ v0.11.0: main-menu SAVE MENU button
        internal const string MainButtonName = "NOSave_MainMenuSaveButton";
        private static Button _mmButton;
        private static MainMenu _mmFor;
        private static SaveMenuPanel _mmPanel;
        private static MainMenu _mmCached;
        private static float _mmFindAt;
        private static string _mmLastHidden;
        private static float _mmFailAt = -100f;
        private static int _mmFails;

        internal static bool MainPanelOpen
        {
            get { try { return _mmPanel != null && _mmPanel.gameObject.activeInHierarchy; } catch { return false; } }
        }

        internal static void OnMainMenuStart(MainMenu mm, string src) { _mmCached = mm; _mmFails = 0; _mmFailAt = -100f; PollMain(src); }

        private static Button MainTemplate(MainMenu mm)
        {
            try { return Traverse.Create(mm).Field("missionsButton").GetValue<Button>(); } catch { return null; }
        }

        /// <summary>Visibility rule: only on a main menu that can launch an SP mission (GateSp menuContinue: main
        /// menu, nothing hosted, not editor play) and while no NOSave load is running.</summary>
        internal static void PollMain(string src)
        {
            try
            {
                bool atMenu = false;
                try { atMenu = GameManager.gameState == GameState.Menu; } catch { }
                if (!atMenu) return;
                var mm = _mmCached;
                if (mm == null)
                {
                    float now = Time.realtimeSinceStartup;
                    if (now < _mmFindAt) return;
                    _mmFindAt = now + 2f;
                    mm = UnityEngine.Object.FindObjectOfType<MainMenu>();
                    _mmCached = mm;
                    if (mm == null) return;
                }
                bool can = NOSaveSystemPlugin.GateSp("SaveMenu.main", true, true) && !NoSaveUi.ContinueInProgress && !MissionReload.IsRunning;
                if (!can)
                {
                    ShowMain(false); // v0.11.1 (L3): siblings moved back while hidden
                    if (MainPanelOpen) _mmPanel.Close("main menu cannot launch SP now", true);
                    string why = "spMenu=" + NOSaveSystemPlugin.GateSp("SaveMenu.main", true, true) + " loading=" + (NoSaveUi.ContinueInProgress || MissionReload.IsRunning);
                    if (why != _mmLastHidden) { _mmLastHidden = why; Log("main-menu SAVE MENU button not shown (" + why + ") src=" + src); }
                    return;
                }
                _mmLastHidden = null;
                if (_mmButton == null || _mmFor != mm)
                {
                    if (_mmFails > 0 && Time.realtimeSinceStartup - _mmFailAt < Mathf.Min(30f, 0.5f * Mathf.Pow(2f, Mathf.Min(_mmFails, 6)))) return;
                    if (!InjectMain(mm, src)) { _mmFails++; _mmFailAt = Time.realtimeSinceStartup; return; }
                    _mmFails = 0;
                }
                ShowMain(true);
            }
            catch { }
        }

        private static bool InjectMain(MainMenu mm, string src)
        {
            try
            {
                var tpl = MainTemplate(mm);
                if (tpl == null) { Warn("main-menu SAVE MENU: MainMenu.missionsButton not found src=" + src); return false; }
                var parent = tpl.transform.parent;
                var existing = parent != null ? parent.Find(MainButtonName) : null;
                GameObject go;
                if (existing != null) { go = existing.gameObject; Log("main-menu SAVE MENU existing clone re-bound (placement unchanged, mode=" + _mmPlaceMode + ")"); }
                else
                {
                    if (!CloneNative(tpl.gameObject, parent, MainButtonName, out go) || go == null)
                    {
                        Warn("main-menu SAVE MENU: clone failed (see CloneNative FAIL) -> no button, retry with back-off src=" + src);
                        return false;
                    }
                    string mode = PlaceMain(go, tpl, parent); // v0.11.1 (L3): directly under MISSIONS, no overlap
                    Log("main-menu SAVE MENU placement mode=" + mode + " parent='" + (parent != null ? parent.name : "-") + "' siblingIndex=" + go.transform.GetSiblingIndex());
                    Log("main-menu SAVE MENU look vs '" + tpl.name + "': " + LookDiff(tpl.gameObject, go));
                }
                go.SetActive(true);
                SetLabel(go, ButtonLabel);
                var b = go.GetComponent<Button>();
                if (b == null) { UnityEngine.Object.Destroy(go); Warn("main-menu SAVE MENU: clone has no Button -> destroyed, no button"); return false; }
                b.onClick.RemoveListener(OnMainButtonClicked);
                b.onClick.AddListener(OnMainButtonClicked); // keeps the re-wired native click sound listeners
                b.interactable = true;
                _mmButton = b; _mmFor = mm;
                try { if (parent is RectTransform prt) LayoutRebuilder.MarkLayoutForRebuild(prt); } catch { }
                Log("main-menu SAVE MENU button injected OK under '" + (parent != null ? parent.name : "-") + "' cloned from '" + tpl.name + "' label='" + GetLabel(tpl.gameObject) + "' src=" + src);
                return true;
            }
            catch (Exception ex) { Warn("main-menu SAVE MENU injection FAILED: " + ex.Message); return false; }
        }

        /// <summary>
        /// v0.11.0: clone a native main-menu button keeping its look/feel: Image/TMP/Animator/AudioSource/EventSystem
        /// components and the game's look-only UIStyleSystem appliers stay; game-logic MonoBehaviours are removed and
        /// onClick is reset.
        /// v0.11.1 (L2): returns false (half-built object destroyed, logged) when the template or the clone has no Button.
        /// v0.11.1 (L3 sound): native hover/click sounds are kept: persistent AudioSource calls of onClick and of every
        /// EventTrigger entry are re-wired (game-target calls dropped, the trigger itself kept); game MonoBehaviours that
        /// only hold sound/look data (AudioClip + value fields) are kept; other game scripts with AudioClip fields are
        /// removed and their clips re-wired (hover/enter/highlight -> PointerEnter, click/press/select -> onClick) through
        /// SoundManager.PlayMenuOneShot.
        /// </summary>
        internal static bool CloneNative(GameObject template, Transform parent, string name, out GameObject result)
        {
            result = null;
            if (template == null || template.GetComponent<Button>() == null)
            {
                Warn("CloneNative FAIL '" + (template != null ? template.name : "null") + "': template has no Button -> no button");
                return false;
            }
            var holder = new GameObject("NOSave_cloneHolder");
            holder.SetActive(false);
            holder.transform.SetParent(parent, false);
            GameObject go = null;
            var kept = new StringBuilder();
            try
            {
                go = UnityEngine.Object.Instantiate(template, holder.transform, false);
                if (go == null) { Warn("CloneNative FAIL '" + template.name + "': Instantiate returned null -> no button"); return false; }
                go.name = name;
                var b0 = go.GetComponent<Button>();
                if (b0 == null)
                {
                    UnityEngine.Object.DestroyImmediate(go); go = null;
                    Warn("CloneNative FAIL '" + template.name + "': clone has no Button -> half-built object destroyed, no button");
                    return false;
                }
                var gameAsm = typeof(LeaderboardMenu).Assembly;
                var hoverClips = new List<UnityEngine.Object>(); var clickClips = new List<UnityEngine.Object>();
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    var t = mb.GetType();
                    if (t.Assembly != gameAsm) continue;
                    if (t.Namespace == "NuclearOption.UIStyleSystem") { kept.Append(t.Name).Append(' '); continue; }
                    bool hasClip, soundOnly = SoundOnly(t, out hasClip);
                    if (hasClip && soundOnly) { kept.Append("sound:").Append(t.Name).Append(' '); continue; }
                    if (hasClip) CollectClips(mb, hoverClips, clickClips);
                    UnityEngine.Object.DestroyImmediate(mb);
                }
                foreach (var et in go.GetComponentsInChildren<UnityEngine.EventSystems.EventTrigger>(true))
                {
                    int audioN = 0, dropped = 0;
                    try
                    {
                        var rebuilt = new List<UnityEngine.EventSystems.EventTrigger.Entry>();
                        foreach (var en in et.triggers)
                        {
                            if (en == null) continue;
                            var calls = AudioCalls(en.callback, gameAsm, ref dropped);
                            if (calls.Count == 0) continue;
                            var ne = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = en.eventID };
                            foreach (var c in calls) { var cc = c; ne.callback.AddListener(_ => cc()); audioN++; }
                            rebuilt.Add(ne);
                        }
                        et.triggers = rebuilt;
                    }
                    catch { et.triggers = new List<UnityEngine.EventSystems.EventTrigger.Entry>(); }
                    kept.Append("EventTrigger(audio=").Append(audioN).Append(",dropped=").Append(dropped).Append(") ");
                }
                foreach (var b in go.GetComponentsInChildren<Button>(true))
                {
                    int dropped = 0;
                    var calls = AudioCalls(b.onClick, gameAsm, ref dropped);
                    b.onClick = new Button.ButtonClickedEvent();
                    foreach (var c in calls) { var cc = c; b.onClick.AddListener(() => cc()); kept.Append("clickAudio "); }
                }
                // clips of removed game scripts -> SoundManager.PlayMenuOneShot (reflection: no AudioModule reference)
                if (hoverClips.Count > 0 || clickClips.Count > 0)
                {
                    if (hoverClips.Count > 0)
                    {
                        var et = go.GetComponent<UnityEngine.EventSystems.EventTrigger>() ?? go.AddComponent<UnityEngine.EventSystems.EventTrigger>();
                        var ne = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerEnter };
                        var clip = hoverClips[0]; ne.callback.AddListener(_ => PlayMenuClip(clip));
                        et.triggers.Add(ne); kept.Append("rewired-hover(").Append(clip.name).Append(") ");
                    }
                    if (clickClips.Count > 0) { var clip = clickClips[0]; b0.onClick.AddListener(() => PlayMenuClip(clip)); kept.Append("rewired-click(").Append(clip.name).Append(") "); }
                }
                go.transform.SetParent(parent, false);
                result = go;
            }
            catch (Exception ex)
            {
                Warn("CloneNative FAIL '" + template.name + "': " + ex.GetType().Name + " " + ex.Message + " -> half-built object destroyed, no button");
                if (go != null) { try { UnityEngine.Object.DestroyImmediate(go); } catch { } }
                result = null;
                return false;
            }
            finally { UnityEngine.Object.Destroy(holder); }
            Log("CloneNative '" + template.name + "' kept: " + (kept.Length > 0 ? kept.ToString() : "(look components only)") + DescribeSound(template));
            return true;
        }

        private static readonly Dictionary<Type, bool[]> _soundOnly = new Dictionary<Type, bool[]>();
        /// <summary>True when every instance field (whole hierarchy below MonoBehaviour) is a value type, string, enum,
        /// AudioClip/AudioSource/AudioMixerGroup or a look asset (Sprite/Color/Material/Font) and at least one is an AudioClip.</summary>
        private static bool SoundOnly(Type t, out bool hasClip)
        {
            bool[] r;
            if (!_soundOnly.TryGetValue(t, out r))
            {
                bool clip = false, only = true;
                const System.Reflection.BindingFlags bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
                for (var cur = t; cur != null && cur != typeof(MonoBehaviour); cur = cur.BaseType)
                    foreach (var f in cur.GetFields(bf))
                    {
                        var ft = f.FieldType; string fn = ft.FullName ?? ft.Name;
                        if (fn == "UnityEngine.AudioClip" || fn == "UnityEngine.AudioClip[]") { clip = true; continue; }
                        if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string) || ft == typeof(Color) || ft == typeof(Vector2) || ft == typeof(Vector3)) continue;
                        if (fn == "UnityEngine.AudioSource" || fn == "UnityEngine.Audio.AudioMixerGroup" || ft == typeof(Sprite) || ft == typeof(Material)) continue;
                        only = false;
                    }
                r = new[] { clip, only };
                _soundOnly[t] = r;
            }
            hasClip = r[0];
            return r[1];
        }

        private static void CollectClips(MonoBehaviour mb, List<UnityEngine.Object> hover, List<UnityEngine.Object> click)
        {
            const System.Reflection.BindingFlags bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            for (var cur = mb.GetType(); cur != null && cur != typeof(MonoBehaviour); cur = cur.BaseType)
                foreach (var f in cur.GetFields(bf))
                {
                    if ((f.FieldType.FullName ?? "") != "UnityEngine.AudioClip") continue;
                    UnityEngine.Object clip = null; try { clip = f.GetValue(mb) as UnityEngine.Object; } catch { }
                    if (clip == null) continue;
                    string n = f.Name.ToLowerInvariant();
                    if (n.Contains("hover") || n.Contains("enter") || n.Contains("highlight") || n.Contains("over")) hover.Add(clip);
                    else if (n.Contains("click") || n.Contains("press") || n.Contains("select") || n.Contains("submit")) click.Add(clip);
                }
        }

        private static System.Reflection.MethodInfo _playMenuOneShot;
        private static void PlayMenuClip(UnityEngine.Object clip)
        {
            try
            {
                if (clip == null) return;
                if (_playMenuOneShot == null) _playMenuOneShot = typeof(SoundManager).GetMethod("PlayMenuOneShot", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                if (_playMenuOneShot != null) _playMenuOneShot.Invoke(null, new object[] { clip });
            }
            catch { }
        }

        /// <summary>Persistent calls of a UnityEvent whose target is an AudioSource (Play/PlayOneShot/PlayDelayed),
        /// rebuilt as actions (argument read from the serialized PersistentCall). Game-target calls are counted as dropped.</summary>
        private static List<Action> AudioCalls(UnityEventBase ev, System.Reflection.Assembly gameAsm, ref int dropped)
        {
            var res = new List<Action>();
            if (ev == null) return res;
            int n = 0; try { n = ev.GetPersistentEventCount(); } catch { return res; }
            for (int i = 0; i < n; i++)
            {
                UnityEngine.Object tg = null; string m = null;
                try { tg = ev.GetPersistentTarget(i); m = ev.GetPersistentMethodName(i); } catch { }
                if (tg == null || string.IsNullOrEmpty(m)) continue;
                if ((tg.GetType().FullName ?? "") != "UnityEngine.AudioSource") { if (tg.GetType().Assembly == gameAsm) dropped++; continue; }
                object arg = PersistentArg(ev, i);
                var target = tg; System.Reflection.MethodInfo mi = null;
                try
                {
                    foreach (var cand in target.GetType().GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                    {
                        if (cand.Name != m) continue;
                        var ps = cand.GetParameters();
                        if (arg == null && ps.Length == 0) { mi = cand; break; }
                        if (arg != null && ps.Length == 1 && ps[0].ParameterType.IsInstanceOfType(arg)) { mi = cand; break; }
                    }
                }
                catch { }
                if (mi == null) continue;
                var call = mi; var a = arg;
                res.Add(() => { try { if (target != null) call.Invoke(target, a == null ? null : new[] { a }); } catch { } });
            }
            return res;
        }

        private static object PersistentArg(UnityEventBase ev, int i)
        {
            try
            {
                const System.Reflection.BindingFlags bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                var grp = typeof(UnityEventBase).GetField("m_PersistentCalls", bf).GetValue(ev);
                var calls = grp.GetType().GetField("m_Calls", bf).GetValue(grp) as System.Collections.IList;
                var pc = calls[i];
                int mode = Convert.ToInt32(pc.GetType().GetField("m_Mode", bf).GetValue(pc));
                var args = pc.GetType().GetField("m_Arguments", bf).GetValue(pc);
                switch (mode) // PersistentListenerMode: 0 EventDefined, 1 Void, 2 Object, 3 Int, 4 Float, 5 String, 6 Bool
                {
                    case 2: return args.GetType().GetField("m_ObjectArgument", bf).GetValue(args);
                    case 3: return args.GetType().GetField("m_IntArgument", bf).GetValue(args);
                    case 4: return args.GetType().GetField("m_FloatArgument", bf).GetValue(args);
                    case 5: return args.GetType().GetField("m_StringArgument", bf).GetValue(args);
                    case 6: return args.GetType().GetField("m_BoolArgument", bf).GetValue(args);
                    default: return null;
                }
            }
            catch { return null; }
        }

        /// <summary>One-line description of the template's sound wiring (logged next to the clone result).</summary>
        private static string DescribeSound(GameObject tpl)
        {
            try
            {
                int src = 0, trig = 0, clickAudio = 0;
                foreach (var c in tpl.GetComponentsInChildren<Component>(true)) if (c != null && (c.GetType().FullName ?? "") == "UnityEngine.AudioSource") src++;
                foreach (var et in tpl.GetComponentsInChildren<UnityEngine.EventSystems.EventTrigger>(true)) trig += et.triggers != null ? et.triggers.Count : 0;
                var b = tpl.GetComponent<Button>();
                if (b != null) for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++) { var t = b.onClick.GetPersistentTarget(i); if (t != null && (t.GetType().FullName ?? "") == "UnityEngine.AudioSource") clickAudio++; }
                return " | template sound: audioSources=" + src + " triggerEntries=" + trig + " onClickAudio=" + clickAudio + (src + trig + clickAudio == 0 ? " (no native sound wiring found on the template)" : "");
            }
            catch { return ""; }
        }

        /// <summary>v0.11.1 (L3): native look check (font, size, colors, transition) between template and clone.</summary>
        private static string LookDiff(GameObject tpl, GameObject go)
        {
            try
            {
                var sb = new StringBuilder();
                var a = tpl.GetComponentInChildren<TMP_Text>(true); var b = go.GetComponentInChildren<TMP_Text>(true);
                if (a != null && b != null)
                {
                    if (a.font != b.font) sb.Append(" font");
                    if (Mathf.Abs(a.fontSize - b.fontSize) > 0.01f) sb.Append(" fontSize");
                    if (a.color != b.color) sb.Append(" color");
                    if (a.fontStyle != b.fontStyle) sb.Append(" fontStyle");
                }
                var ba = tpl.GetComponent<Button>(); var bb = go.GetComponent<Button>();
                if (ba != null && bb != null)
                {
                    if (!ba.colors.Equals(bb.colors)) sb.Append(" colors(normal/highlighted/pressed/selected)");
                    if (ba.transition != bb.transition) sb.Append(" transition");
                    if (ba.spriteState.highlightedSprite != bb.spriteState.highlightedSprite || ba.spriteState.selectedSprite != bb.spriteState.selectedSprite) sb.Append(" spriteState");
                }
                var ia = tpl.GetComponent<Image>(); var ib = go.GetComponent<Image>();
                if (ia != null && ib != null && (ia.sprite != ib.sprite || ia.color != ib.color)) sb.Append(" image");
                return sb.Length == 0 ? "identical(font,size,color,colors,transition,spriteState,image)" : "DIFF:" + sb;
            }
            catch (Exception ex) { return "n/a(" + ex.GetType().Name + ")"; }
        }

        // ------------------------------------------------------------------ v0.11.1 (L3): placement without overlap
        private static readonly List<RectTransform> _mmShifted = new List<RectTransform>();
        private static float _mmShiftStep;
        private static bool _mmShiftOn;
        private static string _mmPlaceMode = "?";

        /// <summary>LayoutGroup on the parent (template not ignoring layout): sibling right below the template, the
        /// layout does the rest. Otherwise: step = distance template -> nearest native sibling below (else above, else
        /// height*1.1); the clone takes the slot below the template and every native sibling below is moved down by
        /// one step (moved back while the button is hidden).</summary>
        private static string PlaceMain(GameObject go, Button tpl, Transform parent)
        {
            var rt = go.transform as RectTransform; var trt = tpl.transform as RectTransform;
            go.transform.SetSiblingIndex(tpl.transform.GetSiblingIndex() + 1);
            _mmShifted.Clear(); _mmShiftOn = false; _mmShiftStep = 0f;
            if (parent == null || rt == null || trt == null) return _mmPlaceMode = "none(no RectTransform)";
            var lg = parent.GetComponent<LayoutGroup>();
            bool ignore = false; try { var le = tpl.GetComponent<LayoutElement>(); ignore = le != null && le.ignoreLayout; } catch { }
            if (lg != null && !ignore)
            {
                try { LayoutRebuilder.MarkLayoutForRebuild(parent as RectTransform); } catch { }
                return _mmPlaceMode = "layout(" + lg.GetType().Name + ")";
            }
            float ty = trt.localPosition.y, tx = trt.localPosition.x, h = trt.rect.height, w = Mathf.Max(1f, trt.rect.width);
            RectTransform below = null, above = null;
            var col = new List<RectTransform>();
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i) as RectTransform;
                if (c == null || c == trt || c == rt || !c.gameObject.activeSelf || c.name.StartsWith("NOSave_", StringComparison.Ordinal)) continue;
                if (Mathf.Abs(c.localPosition.x - tx) > w * 0.5f) continue; // not in the template's column
                float y = c.localPosition.y;
                if (y < ty - 0.5f) { col.Add(c); if (below == null || y > below.localPosition.y) below = c; }
                else if (y > ty + 0.5f) { if (above == null || y < above.localPosition.y) above = c; }
            }
            float step = below != null ? ty - below.localPosition.y : above != null ? above.localPosition.y - ty : h * 1.1f;
            string src = below != null ? "sibling-below '" + below.name + "'" : above != null ? "sibling-above '" + above.name + "'" : "height*1.1";
            if (step < 1f || step > Mathf.Max(h, 1f) * 4f) { step = h * 1.1f; src += "(out of range -> height*1.1)"; }
            rt.anchorMin = trt.anchorMin; rt.anchorMax = trt.anchorMax; rt.pivot = trt.pivot; rt.sizeDelta = trt.sizeDelta;
            rt.anchoredPosition = trt.anchoredPosition - new Vector2(0f, step);
            _mmShifted.AddRange(col); _mmShiftStep = step;
            SetMainShift(true);
            return _mmPlaceMode = "manual(step=" + step.ToString("0.#", CultureInfo.InvariantCulture) + " from " + src + ", shifted=" + col.Count + ")";
        }

        private static void SetMainShift(bool on)
        {
            if (on == _mmShiftOn || _mmShiftStep <= 0f) return;
            float d = on ? -_mmShiftStep : _mmShiftStep;
            foreach (var c in _mmShifted) { try { if (c != null) c.anchoredPosition += new Vector2(0f, d); } catch { } }
            _mmShiftOn = on;
        }

        private static void ShowMain(bool show)
        {
            try
            {
                if (_mmButton == null) return;
                if (_mmButton.gameObject.activeSelf != show) _mmButton.gameObject.SetActive(show);
                SetMainShift(show);
            }
            catch { }
        }

        /// <summary>v0.11.1: GATE MP detected -> hide every NOSave button and close every NOSave panel.</summary>
        internal static void OnMpDetected()
        {
            try { if (_saveButton != null) _saveButton.gameObject.SetActive(false); } catch { }
            try { if (PanelOpen) _panel.Close("GATE MP detected", true); } catch { }
            ShowMain(false);
            try { if (MainPanelOpen) _mmPanel.Close("GATE MP detected", true); } catch { }
        }

        private static void OnMainButtonClicked()
        {
            Log("main-menu SAVE MENU button clicked");
            try
            {
                if (!NOSaveSystemPlugin.GateSp("SaveMenu.main.open", true)) return;
                var mm = _mmFor;
                if (mm == null) { Warn("main-menu SAVE MENU open refused: MainMenu gone"); return; }
                var tpl = MainTemplate(mm);
                Canvas c = null;
                try { c = tpl != null ? tpl.GetComponentInParent<Canvas>() : null; if (c != null) c = c.rootCanvas; } catch { }
                Transform host = c != null ? c.transform : (tpl != null ? tpl.transform.root : null);
                if (_mmPanel == null || host == null || !_mmPanel.transform.IsChildOf(host))
                {
                    if (_mmPanel != null) { try { UnityEngine.Object.Destroy(_mmPanel.gameObject); } catch { } }
                    _mmPanel = SaveMenuPanel.CreateMain(host, tpl);
                }
                if (_mmPanel != null) _mmPanel.Open();
                else Warn("main-menu save menu panel could not be built");
            }
            catch (Exception ex) { Warn("main-menu open save menu FAIL: " + ex); }
        }

        internal static void HandleEscMain()
        {
            try { if (_mmPanel != null) _mmPanel.HandleEsc(); } catch { }
        }

        // v0.10.1 (M1): GameplayUI.Update opens/closes the pause menu on the Rewired "Pause" action too (gamepad etc.),
        // not only on Escape. Read it via reflection so no Rewired reference is needed.
        private static System.Reflection.MethodInfo _getButtonDown;
        private static bool _pauseReflectFailed;
        private static System.Reflection.FieldInfo _playerInputField;
        private static System.Reflection.PropertyInfo _playerInputProp;
        internal static bool PauseActionDown()
        {
            if (_pauseReflectFailed) return false;
            try
            {
                // GameManager.playerInput is a Rewired.Player; read it untyped so no Rewired reference is needed.
                if (_playerInputField == null && _playerInputProp == null)
                {
                    const System.Reflection.BindingFlags bf = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    _playerInputField = typeof(GameManager).GetField("playerInput", bf);
                    if (_playerInputField == null) _playerInputProp = typeof(GameManager).GetProperty("playerInput", bf);
                    if (_playerInputField == null && _playerInputProp == null) { _pauseReflectFailed = true; Warn("Pause action: GameManager.playerInput not found"); return false; }
                }
                object pi = _playerInputField != null ? _playerInputField.GetValue(null) : _playerInputProp.GetValue(null, null);
                if (pi == null) return false;
                if (_getButtonDown == null) _getButtonDown = pi.GetType().GetMethod("GetButtonDown", new[] { typeof(string) });
                if (_getButtonDown == null) { _pauseReflectFailed = true; Warn("Pause action: GetButtonDown(string) not found"); return false; }
                return (bool)_getButtonDown.Invoke(pi, new object[] { "Pause" });
            }
            catch (Exception ex) { _pauseReflectFailed = true; Warn("Pause action read FAIL (ESC still works): " + ex.Message); return false; }
        }

        private static int _pollFails;
        private static float _pollNextAt;
        private const int PollFailsWarn = 5;
        internal static void ResetPollBackoff() { _pollFails = 0; _pollNextAt = 0f; }

        internal static void Poll()
        {
            try
            {
                var m = SceneSingleton<LeaderboardMenu>.i;
                if (m == null || !m.gameObject.activeInHierarchy) return;
                bool sp = NOSaveSystemPlugin.GateSp("SaveMenu.poll", false, true);
                if (!sp)
                {
                    if (_saveButton != null && _saveButton.gameObject.activeSelf) { _saveButton.gameObject.SetActive(false); Log("SAVE MENU button hidden (left SP host)"); }
                    if (PanelOpen) _panel.Close("left SP host", true);
                    return;
                }
                if (!ButtonPresent(m))
                {
                    // v0.10.1 (L7): exponential back-off after repeated injection failures (0.5s -> max 30s);
                    // reset whenever the menu is opened again or the button is present.
                    float now = Time.realtimeSinceStartup;
                    if (now < _pollNextAt) return;
                    MenuMode mode = MenuMode.Leaderboard;
                    try { mode = Traverse.Create(m).Field("currentMode").GetValue<MenuMode>(); } catch { }
                    OnMenuOpened(m, mode, "poll-retry");
                    if (ButtonPresent(m)) { _pollFails = 0; _pollNextAt = 0f; }
                    else
                    {
                        _pollFails++;
                        float wait = Mathf.Min(30f, 0.5f * Mathf.Pow(2f, Mathf.Min(_pollFails, 6)));
                        _pollNextAt = now + wait;
                        if (_pollFails == PollFailsWarn) Warn("SAVE MENU button injection failed " + _pollFails + "x - retrying every " + wait + "s at most");
                    }
                }
                else { _pollFails = 0; _pollNextAt = 0f; }
            }
            catch { }
        }
    }

    // ====================================================================== save list / files

    internal class SaveEntry
    {
        public string Path;
        public DateTime Time;
        public string Mission;
        public string Aircraft;
        public bool IsLatest;      // same content as midmission_autosave.json (what F6 / Continue load)
        public bool IsAutosaveFile; // Path is the autosave itself (no archive copy)
        public long Length;
        public string Utc;
        public bool HasMeta;
    }

    internal static class NoSaveSaves
    {
        private class CacheItem { public DateTime Mtime; public long Len; public SaveEntry E; }
        private static readonly Dictionary<string, CacheItem> _cache = new Dictionary<string, CacheItem>(StringComparer.OrdinalIgnoreCase);

        internal static string ArchiveDir
        {
            get { var p = NOSaveSystemPlugin.Instance; return p == null ? null : System.IO.Path.Combine(p.SaveDirectory, "archive"); }
        }

        internal static List<SaveEntry> Scan()
        {
            var list = new List<SaveEntry>();
            var plug = NOSaveSystemPlugin.Instance;
            if (plug == null) return list;
            string arch = ArchiveDir;
            try
            {
                if (Directory.Exists(arch))
                    foreach (var f in Directory.GetFiles(arch, "*.json"))
                    {
                        if (!f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                        var e = Read(f);
                        if (e != null) list.Add(e);
                    }
            }
            catch (Exception ex) { NoSaveV0100.Warn("scan archive FAIL: " + ex.Message); }
            try
            {
                string auto = plug.AutosavePath;
                if (File.Exists(auto))
                {
                    var a = Read(auto);
                    SaveEntry match = null;
                    if (a != null)
                        foreach (var e in list)
                            if (e.Length == a.Length && !string.IsNullOrEmpty(a.Utc) && e.Utc == a.Utc) { match = e; break; }
                    if (match != null) match.IsLatest = true;
                    else if (a != null) { a.IsLatest = true; a.IsAutosaveFile = true; list.Add(a); }
                }
            }
            catch (Exception ex) { NoSaveV0100.Warn("scan autosave FAIL: " + ex.Message); }
            list.Sort((x, y) => y.Time.CompareTo(x.Time));
            return list;
        }

        private static SaveEntry Read(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (_cache.TryGetValue(path, out var c) && c.Mtime == fi.LastWriteTime && c.Len == fi.Length)
                {
                    c.E.IsLatest = false; c.E.IsAutosaveFile = false;
                    return c.E;
                }
                var e = Parse(path, fi);
                _cache[path] = new CacheItem { Mtime = fi.LastWriteTime, Len = fi.Length, E = e };
                return e;
            }
            catch (Exception ex) { NoSaveV0100.Warn("read save meta FAIL " + path + ": " + ex.Message); return null; }
        }

        /// <summary>Backward compatible: v0.10 "nosaveMeta" object; else manifest.utc / mission.name / player aircraft object /
        /// nosavePlayerState; else file-name stamp / mtime and "Unknown".</summary>
        private static SaveEntry Parse(string path, FileInfo fi)
        {
            var e = new SaveEntry { Path = path, Length = fi.Length, Time = fi.LastWriteTime, Mission = "Unknown", Aircraft = "Unknown" };
            string text;
            try { text = File.ReadAllText(path, Encoding.UTF8); }
            catch { return e; }
            bool timeSet = false;
            try
            {
                var man = SnapshotJson.ExtractObject(text, "manifest");
                if (man != null) e.Utc = SnapshotJson.GetString(man, "utc");
            }
            catch { }
            string meta = null;
            try { if (text.IndexOf("\"nosaveMeta\"", StringComparison.Ordinal) >= 0) meta = SnapshotJson.ExtractObject(text, "nosaveMeta"); } catch { }
            if (meta != null)
            {
                e.HasMeta = true;
                var sl = SnapshotJson.GetString(meta, "savedLocal");
                if (!string.IsNullOrEmpty(sl) && DateTime.TryParseExact(sl, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) { e.Time = t; timeSet = true; }
                var mn = SnapshotJson.GetString(meta, "missionName"); if (!string.IsNullOrEmpty(mn)) e.Mission = mn;
                var an = SnapshotJson.GetString(meta, "aircraftName"); if (!string.IsNullOrEmpty(an)) e.Aircraft = an;
            }
            if (!timeSet && !string.IsNullOrEmpty(e.Utc) && DateTime.TryParse(e.Utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var u))
            { e.Time = u.Kind == DateTimeKind.Utc ? u.ToLocalTime() : u; timeSet = true; }
            if (!timeSet)
            {
                var fn = System.IO.Path.GetFileNameWithoutExtension(path); // midmission_yyyyMMdd_HHmmss[_fff]
                if (fn.StartsWith("midmission_", StringComparison.Ordinal) &&
                    DateTime.TryParseExact(fn.Substring(11), new[] { "yyyyMMdd_HHmmss_fff", "yyyyMMdd_HHmmss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ft)) e.Time = ft;
            }
            if (e.Mission == "Unknown")
            {
                try
                {
                    var mis = SnapshotJson.ExtractObject(text, "mission");
                    if (mis != null)
                    {
                        var n = SnapshotJson.GetString(mis, "name");
                        if (string.IsNullOrEmpty(n)) n = SnapshotJson.GetString(mis, "loadKeyName");
                        if (!string.IsNullOrEmpty(n)) e.Mission = n;
                    }
                }
                catch { }
            }
            if (e.Aircraft == "Unknown")
            {
                try
                {
                    int i = text.IndexOf("\"isPlayerAircraft\":true", StringComparison.Ordinal);
                    if (i > 0)
                    {
                        int s = text.LastIndexOf("{\"kind\":\"aircraft\"", i, StringComparison.Ordinal);
                        if (s >= 0)
                        {
                            var obj = text.Substring(s, i - s);
                            var n = SnapshotJson.GetString(obj, "definitionName");
                            if (string.IsNullOrEmpty(n)) n = SnapshotJson.GetString(obj, "unitName");
                            if (string.IsNullOrEmpty(n)) n = SnapshotJson.GetString(obj, "type");
                            if (!string.IsNullOrEmpty(n)) e.Aircraft = n;
                        }
                    }
                    else
                    {
                        var ps = SnapshotJson.GetString(text, "nosavePlayerState");
                        if (!string.IsNullOrEmpty(ps) && ps.StartsWith("noAircraft", StringComparison.Ordinal)) e.Aircraft = "No aircraft";
                    }
                }
                catch { }
            }
            return e;
        }

        private static bool DeleteFile(string p)
        {
            try { if (File.Exists(p)) { File.Delete(p); NoSaveV0100.Log("deleted " + p); } return true; }
            catch (Exception ex) { NoSaveV0100.Warn("delete FAIL " + p + ": " + ex.Message); return false; }
        }

        private static void DeletePair(string json)
        {
            DeleteFile(json);
            DeleteFile(json + ".deep");
            _cache.Remove(json);
            try { if (string.Equals(NOSaveSystemPlugin.LastLoadedPath, json, StringComparison.OrdinalIgnoreCase)) NOSaveSystemPlugin.LastLoadedPath = null; } catch { }
        }

        /// <summary>v0.10.1 (M3): copy src over dst atomically (.tmp + File.Replace, move/copy fallback). Leaves no .tmp/.bak.</summary>
        private static void ReplaceAtomic(string src, string dst)
        {
            string tmp = dst + ".tmp", bak = dst + ".bak";
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            File.Copy(src, tmp, true);
            try { File.SetLastWriteTime(tmp, File.GetLastWriteTime(src)); } catch { }
            try
            {
                if (File.Exists(dst))
                {
                    bool replaced = false;
                    try { File.Replace(tmp, dst, bak); replaced = true; }
                    catch (Exception rex) { NoSaveV0100.Log("File.Replace unavailable (" + rex.Message + ") - copy fallback"); }
                    if (!replaced) File.Copy(tmp, dst, true);
                }
                else File.Move(tmp, dst);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                try { if (File.Exists(bak)) File.Delete(bak); } catch { }
            }
        }

        /// <summary>Deletes the save (+ .deep sidecar). If it was the latest (= autosave content), the newest remaining
        /// archived save is first promoted to midmission_autosave.json (atomic; v0.10.1 M3) and only then are the old
        /// files deleted, so F6/Continue never see a half-promoted pair. With nothing left the autosave is removed.</summary>
        internal static void Delete(SaveEntry e)
        {
            if (!NOSaveSystemPlugin.GateSp("delete")) return; // v0.10.2
            var plug = NOSaveSystemPlugin.Instance;
            if (e == null || plug == null) return;
            string auto = plug.AutosavePath;
            bool isAutoFile = string.Equals(e.Path, auto, StringComparison.OrdinalIgnoreCase);
            if (!e.IsLatest) { DeletePair(e.Path); return; }

            SaveEntry next = null;
            try
            {
                foreach (var r in Scan())
                {
                    if (r.IsAutosaveFile || r.IsLatest) continue;
                    if (string.Equals(r.Path, e.Path, StringComparison.OrdinalIgnoreCase)) continue;
                    next = r; break;
                }
            }
            catch (Exception ex) { NoSaveV0100.Warn("delete: scan FAIL " + ex.Message); }

            if (next != null)
            {
                try
                {
                    ReplaceAtomic(next.Path, auto);
                    if (File.Exists(next.Path + ".deep")) ReplaceAtomic(next.Path + ".deep", auto + ".deep");
                    else DeleteFile(auto + ".deep"); // never leave the old sidecar next to the promoted save
                    _cache.Remove(auto);
                    NoSaveV0100.Log("promoted " + next.Path + " -> autosave (F6/Continue now load it)");
                }
                catch (Exception ex)
                {
                    NoSaveV0100.Warn("promote FAIL - nothing deleted: " + ex.Message);
                    return; // delete only after a successful promote
                }
                if (!isAutoFile) DeletePair(e.Path);
            }
            else
            {
                DeletePair(e.Path);
                if (!isAutoFile) DeletePair(auto);
                NoSaveV0100.Log("no saves left; autosave removed");
            }
            foreach (var junk in new[] { auto + ".tmp", auto + ".bak", auto + ".deep.tmp", auto + ".deep.bak" })
                try { if (File.Exists(junk)) File.Delete(junk); } catch { }
        }
    }

    // ====================================================================== uGUI panel

    internal class SaveMenuPanel : MonoBehaviour
    {
        private LeaderboardMenu _menu;
        private Button _tplNeutral, _tplGreen, _tplRed;
        private TMP_Text _tplLabel, _tplTitle;
        private RectTransform _content;
        private TMP_Text _status, _empty;
        private GameObject _confirm;
        private TMP_Text _confirmText;
        private Button _confirmYes, _confirmNo;
        private Action _pending;

        private static Image FindImage(Transform root, string name)
        {
            var t = NoSaveV0100.FindDeep(root, name);
            return t != null ? t.GetComponent<Image>() : null;
        }

        internal static SaveMenuPanel Create(LeaderboardMenu menu)
        {
            try
            {
                var existing = NoSaveV0100.FindDeep(menu.transform, NoSaveV0100.PanelName);
                if (existing != null) Destroy(existing.gameObject);

                var quit = menu.GetComponentInChildren<QuitMissionButton>(true);
                Button exitBtn = quit != null ? quit.GetComponent<Button>() : null;
                Button neutral = NoSaveV0100.GetMenuButton(menu, "settingsButton");
                Button green = NoSaveV0100.GetMenuButton(menu, "primaryActionButton");
                if (neutral == null) neutral = exitBtn ?? green;
                if (green == null) green = neutral;
                Button red = exitBtn ?? neutral;
                if (neutral == null) { NoSaveV0100.Warn("panel: no button template found"); return null; }

                TMP_Text title = null;
                try { title = Traverse.Create(menu).Field("mainTitleText").GetValue<TextMeshProUGUI>(); } catch { }
                TMP_Text label = neutral.GetComponentInChildren<TMP_Text>(true);
                if (title == null) title = label;

                Image dimStyle = null;
                try { var cp = quit != null ? Traverse.Create(quit).Field("confirmLeavePanel").GetValue<GameObject>() : null; if (cp != null) dimStyle = cp.GetComponent<Image>(); } catch { }
                if (dimStyle == null) dimStyle = FindImage(menu.transform, "QuitMissionConformPanel");
                Image winStyle = FindImage(menu.transform, "mainOrganizer");
                if (winStyle == null && exitBtn != null && exitBtn.transform.parent != null && exitBtn.transform.parent.parent != null) winStyle = exitBtn.transform.parent.parent.GetComponent<Image>();
                Image headStyle = title != null && title.transform.parent != null ? title.transform.parent.GetComponent<Image>() : null;
                Image listStyle = FindImage(menu.transform, "Scroll View");
                Image rowStyle = FindImage(menu.transform, "Score Header") ?? FindImage(menu.transform, "ButtonsPanel");
                return Build(menu.transform, menu, neutral, green, red, title, label, dimStyle, winStyle, headStyle, listStyle, rowStyle, quit, false);
            }
            catch (Exception ex) { NoSaveV0100.Warn("panel build FAIL: " + ex); return null; }
        }

        /// <summary>v0.11.0: main-menu instance of the SAME save list panel. Styles come from the cloned native
        /// main-menu button (MainMenu.missionsButton); no pause-menu art exists in the menu scene.</summary>
        internal static SaveMenuPanel CreateMain(Transform host, Button tpl)
        {
            try
            {
                if (host == null || tpl == null) { NoSaveV0100.Warn("main-menu panel: no host/template"); return null; }
                var existing = NoSaveV0100.FindDeep(host, NoSaveV0100.PanelName);
                if (existing != null) Destroy(existing.gameObject);
                TMP_Text label = tpl.GetComponentInChildren<TMP_Text>(true);
                return Build(host, null, tpl, tpl, tpl, label, label, null, null, null, null, null, null, true);
            }
            catch (Exception ex) { NoSaveV0100.Warn("main-menu panel build FAIL: " + ex); return null; }
        }

        private bool _mainMenu;
        private Button _continueLatest;

        private static SaveMenuPanel Build(Transform host, LeaderboardMenu menu, Button neutral, Button green, Button red, TMP_Text title, TMP_Text label,
            Image dimStyle, Image winStyle, Image headStyle, Image listStyle, Image rowStyle, QuitMissionButton quit, bool mainMenu)
        {
            try
            {
                var root = NewRT(NoSaveV0100.PanelName, host);
                Stretch(root, 0, 0, 0, 0);
                AddImage(root.gameObject, dimStyle, new Color(0f, 0f, 0f, 0.6f)).raycastTarget = true;
                var p = root.gameObject.AddComponent<SaveMenuPanel>();
                p._menu = menu; p._mainMenu = mainMenu; p._tplNeutral = neutral; p._tplGreen = green; p._tplRed = red; p._tplLabel = label; p._tplTitle = title;

                var win = NewRT("Window", root);
                win.anchorMin = win.anchorMax = new Vector2(0.5f, 0.5f); win.pivot = new Vector2(0.5f, 0.5f);
                win.sizeDelta = new Vector2(980f, 600f); win.anchoredPosition = Vector2.zero;
                AddImage(win.gameObject, winStyle, new Color(0.13f, 0.15f, 0.17f, 0.97f));

                var head = NewRT("Header", win);
                head.anchorMin = new Vector2(0f, 1f); head.anchorMax = new Vector2(1f, 1f); head.pivot = new Vector2(0.5f, 1f);
                head.sizeDelta = new Vector2(0f, 40f); head.anchoredPosition = Vector2.zero;
                AddImage(head.gameObject, headStyle, new Color(0.34f, 0.36f, 0.38f, 0.8f));
                var ht = AddText(head, "Title", "SAVE MENU", title, title != null ? Mathf.Clamp(title.fontSize, 18f, 30f) : 24f, TextAlignmentOptions.Center);
                Stretch((RectTransform)ht.transform, 12, 0, 12, 0);

                var cols = NewRT("Columns", win);
                cols.anchorMin = new Vector2(0f, 1f); cols.anchorMax = new Vector2(1f, 1f); cols.pivot = new Vector2(0.5f, 1f);
                cols.sizeDelta = new Vector2(-24f, 24f); cols.anchoredPosition = new Vector2(0f, -46f);
                var chl = cols.gameObject.AddComponent<HorizontalLayoutGroup>();
                ConfigureRowLayout(chl);
                ColText(cols, "SAVED (LOCAL TIME)", label, 15f, 240f, 0f);
                ColText(cols, "MISSION", label, 15f, 0f, 1f);
                ColText(cols, "AIRCRAFT", label, 15f, 210f, 0f);
                ColText(cols, "", label, 15f, 212f, 0f);

                var list = NewRT("List", win);
                Stretch(list, 12, 64, 12, 74);
                var listImg = AddImage(list.gameObject, listStyle, new Color(0.40f, 0.45f, 0.51f, 0.5f));
                listImg.raycastTarget = true;
                var vp = NewRT("Viewport", list);
                Stretch(vp, 4, 4, 22, 4); // v0.10.2: right 22 px reserved for the scrollbar
                vp.gameObject.AddComponent<RectMask2D>();
                vp.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.001f); // raycast surface for wheel/drag
                var content = NewRT("Content", vp);
                content.anchorMin = new Vector2(0f, 1f); content.anchorMax = new Vector2(1f, 1f); content.pivot = new Vector2(0.5f, 1f);
                content.sizeDelta = new Vector2(0f, 0f); content.anchoredPosition = Vector2.zero;
                var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
                vlg.spacing = 4f; vlg.padding = new RectOffset(2, 2, 2, 2);
                vlg.childControlWidth = true; vlg.childControlHeight = true; vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
                vlg.childAlignment = TextAnchor.UpperCenter;
                var csf = content.gameObject.AddComponent<ContentSizeFitter>();
                csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                var sr = list.gameObject.AddComponent<ScrollRect>();
                sr.content = content; sr.viewport = vp; sr.horizontal = false; sr.vertical = true;
                sr.movementType = ScrollRect.MovementType.Clamped; sr.scrollSensitivity = 30f; sr.inertia = false;
                sr.verticalScrollbar = BuildScrollbar(list, listStyle);
                sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
                p._content = content;
                p._empty = AddText(list, "Empty", mainMenu ? "No saves yet. Save in a mission with SAVE MENU (or F5)." : "No saves yet. Use NEW SAVE (or F5).", label, 18f, TextAlignmentOptions.Center);
                Stretch((RectTransform)p._empty.transform, 0, 0, 0, 0);

                var foot = NewRT("Footer", win);
                foot.anchorMin = new Vector2(0f, 0f); foot.anchorMax = new Vector2(1f, 0f); foot.pivot = new Vector2(0.5f, 0f);
                foot.sizeDelta = new Vector2(-24f, 44f); foot.anchoredPosition = new Vector2(0f, 10f);
                p._status = AddText(foot, "Status", "", label, 16f, TextAlignmentOptions.MidlineLeft);
                var srt = (RectTransform)p._status.transform; Stretch(srt, 0, 0, 440, 0);
                var bClose = p.MakeButton(p._tplNeutral, foot, "NOSave_Close", "CLOSE", () => p.Close("Close button", true));
                PlaceRight((RectTransform)bClose.transform, 0f, 200f, 40f);
                if (!mainMenu)
                {
                    var bNew = p.MakeButton(p._tplGreen, foot, "NOSave_NewSave", "NEW SAVE", p.OnNewSave);
                    PlaceRight((RectTransform)bNew.transform, 210f, 200f, 40f);
                }
                else
                {
                    // v0.11.0: the old main-menu Continue box loaded the autosave (latest save) in one click; kept here.
                    p._continueLatest = p.MakeButton(p._tplGreen, foot, "NOSave_ContinueLatest", "CONTINUE (LATEST)", p.OnContinueLatest);
                    PlaceRight((RectTransform)p._continueLatest.transform, 210f, 220f, 40f);
                }

                p.BuildConfirm(root, quit);
                root.gameObject.SetActive(false);
                NoSaveV0100.Log("save menu panel built under '" + host.name + "' main=" + mainMenu + " (styles: window=" + (winStyle != null) + " header=" + (headStyle != null) +
                                " list=" + (listStyle != null) + " row=" + (rowStyle != null) + " dim=" + (dimStyle != null) + " confirmClone=" + (p._confirm != null && p._confirm.name == "NOSave_ConfirmCloned") + ")");
                p._rowStyle = rowStyle;
                return p;
            }
            catch (Exception ex) { NoSaveV0100.Warn("panel build FAIL: " + ex); return null; }
        }

        private Image _rowStyle;

        // ---------- helpers ----------
        private static RectTransform NewRT(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            return rt;
        }

        private static void Stretch(RectTransform rt, float l, float b, float r, float t)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(l, b); rt.offsetMax = new Vector2(-r, -t);
        }

        private static void PlaceRight(RectTransform rt, float rightOffset, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 0.5f); rt.pivot = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(w, h); rt.anchoredPosition = new Vector2(-rightOffset, 0f);
        }

        private static Image AddImage(GameObject go, Image style, Color fallback)
        {
            var img = go.AddComponent<Image>();
            if (style != null)
            {
                img.sprite = style.sprite; img.type = style.type; img.color = style.color;
                img.pixelsPerUnitMultiplier = style.pixelsPerUnitMultiplier;
                img.fillCenter = style.fillCenter;
            }
            else img.color = fallback;
            return img;
        }

        private static TextMeshProUGUI AddText(Transform parent, string name, string text, TMP_Text style, float size, TextAlignmentOptions align)
        {
            var rt = NewRT(name, parent);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            if (style != null)
            {
                if (style.font != null) t.font = style.font;
                if (style.fontSharedMaterial != null) t.fontSharedMaterial = style.fontSharedMaterial;
                t.color = style.color;
                t.fontStyle = style.fontStyle;
            }
            t.enableAutoSizing = false;
            t.fontSize = size;
            t.alignment = align;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.raycastTarget = false;
            t.richText = true;
            t.text = text;
            return t;
        }

        private static void ConfigureRowLayout(HorizontalLayoutGroup h)
        {
            h.padding = new RectOffset(10, 6, 3, 3); h.spacing = 8f;
            h.childControlWidth = true; h.childControlHeight = true;
            h.childForceExpandWidth = false; h.childForceExpandHeight = true;
            h.childAlignment = TextAnchor.MiddleLeft;
        }

        private static TextMeshProUGUI ColText(Transform parent, string text, TMP_Text style, float size, float prefW, float flex)
        {
            var t = AddText(parent, "Col", text, style, size, TextAlignmentOptions.MidlineLeft);
            var le = t.gameObject.AddComponent<LayoutElement>();
            if (prefW > 0f) { le.preferredWidth = prefW; le.minWidth = prefW; }
            else le.minWidth = 80f;
            le.flexibleWidth = flex;
            return t;
        }

        /// <summary>v0.11.1 (L2): minimal uGUI button used only when cloning the native template failed.</summary>
        private static GameObject PlainButton(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>(); img.color = new Color(0.12f, 0.14f, 0.16f, 0.9f);
            var tgo = new GameObject("Text", typeof(RectTransform));
            tgo.transform.SetParent(go.transform, false);
            var txt = tgo.AddComponent<TextMeshProUGUI>(); txt.alignment = TextAlignmentOptions.Center; txt.fontSize = 18f; txt.color = Color.white;
            var trt = tgo.transform as RectTransform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
            NoSaveV0100.Warn("SaveMenu: native button clone failed -> plain fallback button '" + name + "'");
            return go;
        }

        private Button MakeButton(Button tpl, Transform parent, string name, string label, UnityAction onClick)
        {
            var go = tpl != null ? NoSaveV0100.CloneClean(tpl.gameObject, parent, name) : null;
            if (go == null) go = PlainButton(parent, name); // v0.11.1 (L2): graceful fallback, panel still usable
            go.SetActive(true);
            var b = go.GetComponent<Button>();
            b.onClick = new Button.ButtonClickedEvent();
            b.onClick.AddListener(onClick);
            b.interactable = true;
            var t = go.GetComponentInChildren<TMP_Text>(true);
            if (t != null)
            {
                t.text = label;
                var lrt = t.transform as RectTransform;
                if (lrt != null && lrt.parent == go.transform) Stretch(lrt, 4, 0, 4, 0);
            }
            else NoSaveV0100.SetLabel(go, label);
            return b;
        }

        private void BuildConfirm(RectTransform root, QuitMissionButton quit)
        {
            GameObject src = null;
            try { if (quit != null) src = Traverse.Create(quit).Field("confirmLeavePanel").GetValue<GameObject>(); } catch { }
            if (src != null)
            {
                try
                {
                    var go = NoSaveV0100.CloneClean(src, root, "NOSave_ConfirmCloned");
                    var btns = go.GetComponentsInChildren<Button>(true);
                    TMP_Text txt = null;
                    foreach (var t in go.GetComponentsInChildren<TMP_Text>(true))
                        if (t.GetComponentInParent<Button>() == null) { txt = t; break; }
                    if (btns.Length >= 2 && txt != null)
                    {
                        _confirm = go; _confirmText = txt; _confirmYes = btns[0]; _confirmNo = btns[1];
                        var rt = go.transform as RectTransform; if (rt != null) Stretch(rt, 0, 0, 0, 0);
                    }
                    else Destroy(go);
                }
                catch (Exception ex) { NoSaveV0100.Warn("confirm clone FAIL: " + ex.Message); }
            }
            if (_confirm == null)
            {
                var c = NewRT("NOSave_Confirm", root); Stretch(c, 0, 0, 0, 0);
                AddImage(c.gameObject, null, new Color(0.6f, 0.6f, 0.6f, 0.8f)).raycastTarget = true;
                var box = NewRT("Panel", c);
                box.anchorMin = box.anchorMax = new Vector2(0.5f, 0.5f); box.sizeDelta = new Vector2(600f, 158f);
                AddImage(box.gameObject, null, new Color(0.3f, 0.3f, 0.3f, 1f));
                _confirmText = AddText(box, "Text", "", _tplLabel, 18f, TextAlignmentOptions.Center);
                var trt = (RectTransform)_confirmText.transform;
                trt.anchorMin = new Vector2(0f, 1f); trt.anchorMax = new Vector2(1f, 1f); trt.pivot = new Vector2(0.5f, 1f);
                trt.sizeDelta = new Vector2(-20f, 60f); trt.anchoredPosition = new Vector2(0f, -10f);
                _confirmYes = MakeButton(_tplRed, box, "Yes", "DELETE", () => { });
                var yrt = (RectTransform)_confirmYes.transform; yrt.anchorMin = yrt.anchorMax = new Vector2(0.5f, 0.5f); yrt.sizeDelta = new Vector2(296f, 30f); yrt.anchoredPosition = new Vector2(0f, -5f);
                _confirmNo = MakeButton(_tplNeutral, box, "No", "CANCEL", () => { });
                var nrt = (RectTransform)_confirmNo.transform; nrt.anchorMin = nrt.anchorMax = new Vector2(0.5f, 0.5f); nrt.sizeDelta = new Vector2(296f, 30f); nrt.anchoredPosition = new Vector2(0f, -48f);
                _confirm = c.gameObject;
            }
            _confirmYes.onClick = new Button.ButtonClickedEvent();
            _confirmYes.onClick.AddListener(OnConfirmYes);
            _confirmNo.onClick = new Button.ButtonClickedEvent();
            _confirmNo.onClick.AddListener(() => { NoSaveV0100.Log("menu Delete cancelled"); HideConfirm(); });
            NoSaveV0100.SetLabel(_confirmYes.gameObject, "Delete");
            NoSaveV0100.SetLabel(_confirmNo.gameObject, "Cancel");
            _confirm.SetActive(false);
        }

        // ---------- behaviour ----------
        internal void Open()
        {
            HideConfirm();
            Refresh();
            gameObject.SetActive(true);
            transform.SetAsLastSibling();
            NoSaveV0100.Log("save menu opened");
        }

        internal void Close(string why, bool log)
        {
            HideConfirm();
            gameObject.SetActive(false);
            if (log) NoSaveV0100.Log("save menu closed (" + why + ")");
        }

        internal void HandleEsc()
        {
            if (_confirm != null && _confirm.activeSelf) { NoSaveV0100.Log("menu Delete cancelled (ESC)"); HideConfirm(); }
            else Close("ESC", true);
        }

        private void HideConfirm() { _pending = null; if (_confirm != null) _confirm.SetActive(false); }

        private void OnConfirmYes()
        {
            var a = _pending; HideConfirm();
            try { a?.Invoke(); } catch (Exception ex) { NoSaveV0100.Warn("confirm action FAIL: " + ex.Message); }
        }

        private void SetStatus(string s) { if (_status != null) _status.text = NoSaveUi.Ascii(s); }

        private static string Fmt(DateTime t) { return t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }

        private List<SaveEntry> _entries = new List<SaveEntry>();

        internal void Refresh()
        {
            try
            {
                for (int i = _content.childCount - 1; i >= 0; i--)
                {
                    var c = _content.GetChild(i).gameObject;
                    c.SetActive(false);
                    Destroy(c);
                }
                _entries = NoSaveSaves.Scan();
                foreach (var e in _entries) BuildRow(e);
                if (_empty != null) _empty.gameObject.SetActive(_entries.Count == 0);
                if (_continueLatest != null)
                {
                    bool has = false;
                    try { var pl = NOSaveSystemPlugin.Instance; has = pl != null && File.Exists(pl.AutosavePath); } catch { }
                    _continueLatest.interactable = has;
                }
                var plug = NOSaveSystemPlugin.Instance;
                SetStatus(_entries.Count == 1 ? "1 save" : _entries.Count + " saves"); // v0.10.2: count only, no path
                LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
                try { var sr = _content.GetComponentInParent<ScrollRect>(); if (sr != null) sr.verticalNormalizedPosition = 1f; } catch { }
                NoSaveV0100.Log("save menu listed " + _entries.Count + " save(s)");
            }
            catch (Exception ex) { NoSaveV0100.Warn("refresh FAIL: " + ex); SetStatus("Error listing saves - see log"); }
        }

        private void BuildRow(SaveEntry e)
        {
            var row = NewRT("Row", _content);
            AddImage(row.gameObject, _rowStyle, new Color(0.74f, 0.74f, 0.74f, 0.35f)).raycastTarget = true;
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 40f; le.minHeight = 40f;
            ConfigureRowLayout(row.gameObject.AddComponent<HorizontalLayoutGroup>());
            ColText(row, Fmt(e.Time) + (e.IsLatest ? "  <color=#7FDCFF>LATEST</color>" : ""), _tplLabel, 17f, 240f, 0f);
            ColText(row, NoSaveUi.Ascii(e.Mission ?? "Unknown"), _tplLabel, 17f, 0f, 1f);
            ColText(row, NoSaveUi.Ascii(e.Aircraft ?? "Unknown"), _tplLabel, 17f, 210f, 0f);
            var entry = e;
            // v0.10.2: the row owns its LOAD/DELETE buttons as plain uGUI children (laid out by the row's
            // HorizontalLayoutGroup, clipped by the viewport RectMask2D). Cloned game buttons carried their own
            // layout/canvas setup and rendered outside the list with many saves.
            MakeRowButton(_tplGreen, row, "NOSave_Load", "LOAD", () => OnLoad(entry), 100f);
            MakeRowButton(_tplRed, row, "NOSave_Delete", "DELETE", () => OnDelete(entry), 100f);
        }

        private Button MakeRowButton(Button tpl, Transform parent, string name, string label, UnityAction onClick, float width)
        {
            var rt = NewRT(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            Image timg = null;
            try { timg = tpl != null ? (tpl.targetGraphic as Image ?? tpl.GetComponent<Image>()) : null; } catch { }
            if (timg != null) { img.sprite = timg.sprite; img.type = timg.type; img.color = timg.color; img.pixelsPerUnitMultiplier = timg.pixelsPerUnitMultiplier; }
            else img.color = new Color(0.35f, 0.38f, 0.42f, 1f);
            img.raycastTarget = true;
            var b = rt.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            if (tpl != null)
            {
                b.transition = tpl.transition == Selectable.Transition.Animation ? Selectable.Transition.ColorTint : tpl.transition;
                b.colors = tpl.colors; b.spriteState = tpl.spriteState;
            }
            b.navigation = new Navigation { mode = Navigation.Mode.None };
            b.onClick.AddListener(onClick);
            TMP_Text tl = null; try { tl = tpl != null ? tpl.GetComponentInChildren<TMP_Text>(true) : null; } catch { }
            var txt = AddText(rt, "Label", label, tl ?? _tplLabel, 16f, TextAlignmentOptions.Center);
            Stretch((RectTransform)txt.transform, 4, 0, 4, 0);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = width; le.minWidth = width; le.flexibleWidth = 0f;
            return b;
        }

        private static Scrollbar BuildScrollbar(RectTransform list, Image style)
        {
            var sbrt = NewRT("Scrollbar", list);
            sbrt.anchorMin = new Vector2(1f, 0f); sbrt.anchorMax = new Vector2(1f, 1f); sbrt.pivot = new Vector2(1f, 0.5f);
            sbrt.sizeDelta = new Vector2(14f, -8f); sbrt.anchoredPosition = new Vector2(-4f, 0f);
            AddImage(sbrt.gameObject, null, new Color(0f, 0f, 0f, 0.35f));
            var area = NewRT("Sliding Area", sbrt); Stretch(area, 1, 1, 1, 1);
            var handle = NewRT("Handle", area); Stretch(handle, 0, 0, 0, 0);
            var himg = AddImage(handle.gameObject, null, new Color(0.75f, 0.78f, 0.82f, 0.85f));
            var sb = sbrt.gameObject.AddComponent<Scrollbar>();
            sb.handleRect = handle; sb.targetGraphic = himg; sb.direction = Scrollbar.Direction.BottomToTop;
            sb.navigation = new Navigation { mode = Navigation.Mode.None };
            return sb;
        }

        private static void FitLabel(Button b)
        {
            var t = b.GetComponentInChildren<TMP_Text>(true);
            if (t != null && !t.enableAutoSizing && t.fontSize > 18f) t.fontSize = 18f;
        }

        private void OnNewSave()
        {
            var plug = NOSaveSystemPlugin.Instance;
            NoSaveV0100.Log("menu New Save clicked");
            if (plug == null) return;
            if (!NOSaveSystemPlugin.GateSp("SAVE MENU new save")) { Close("left SP host", true); return; }
            if (!plug.CanStartOp) { NoSaveV0100.Log("menu New Save refused: busy"); SetStatus("NOSave busy - try again in a moment"); return; }
            int counterBefore = NOSaveSystemPlugin.SaveCounter; // v0.10.1 (L5): explicit success signal, not timestamps
            try { plug.CaptureMidMissionSnapshot(); }
            catch (Exception ex) { NoSaveV0100.Warn("menu New Save FAIL: " + ex.Message); SetStatus("Save failed - see log"); return; }
            Refresh();
            // v0.10.2d: success = the write counter moved; an empty/late archive listing is not a refusal
            bool ok = NOSaveSystemPlugin.SaveCounter != counterBefore;
            NoSaveV0100.Log("menu New Save result=" + (ok ? "saved" : "not saved (refused, see log)") + " listed=" + _entries.Count);
            if (!ok) SetStatus("Save refused (no running mission?) - see log");
            else if (_entries.Count > 0) SetStatus("Saved: " + Fmt(_entries[0].Time) + "  -  " + _entries[0].Mission);
            else SetStatus("Saved (the list will show it after a refresh)");
        }

        private void OnLoad(SaveEntry e)
        {
            NoSaveV0100.Log("menu Load clicked: " + e.Path + " (" + Fmt(e.Time) + ", " + e.Mission + ", " + e.Aircraft + ")");
            var plug = NOSaveSystemPlugin.Instance;
            if (plug == null) return;
            if (_mainMenu) { StartMainMenuLoad(e.Path, Fmt(e.Time) + ", " + e.Mission); return; }
            if (!NOSaveSystemPlugin.GateSp("SAVE MENU load")) { Close("left SP host", true); return; }
            if (!plug.CanStartOp) { NoSaveV0100.Log("menu Load refused: busy"); SetStatus("NOSave busy - try again in a moment"); return; }
            Close("load started", true);
            bool started = plug.RestoreFromFile(e.Path);
            NoSaveV0100.Log("menu Load " + (started ? "started" : "refused") + ": " + e.Path);
            NoSaveUi.SetStatus(started ? "NOSave: loading " + e.Mission + " (" + Fmt(e.Time) + ")" : "NOSave: load refused (see log)", 6f);
        }

        /// <summary>v0.11.0: main-menu CONTINUE (LATEST) = the old Continue box: load the autosave.</summary>
        private void OnContinueLatest()
        {
            NoSaveV0100.Log("main-menu CONTINUE (LATEST) clicked");
            var plug = NOSaveSystemPlugin.Instance;
            if (plug == null) return;
            StartMainMenuLoad(null, "latest");
        }

        /// <summary>v0.11.0: main-menu load = the real Continue flow (offline SP host, mission load + loading screen,
        /// MissionReload restore, PAUSE0). path null = autosave. The SP gate (main menu, nothing hosted, not editor)
        /// is enforced here and again inside the Continue coroutine; the coroutine always hosts Offline/SinglePlayer.</summary>
        private void StartMainMenuLoad(string path, string what)
        {
            var plug = NOSaveSystemPlugin.Instance;
            if (plug == null) return;
            if (!NOSaveSystemPlugin.GateSp("SAVE MENU main-menu load", true)) { Close("not at a main menu that can launch SP", true); return; }
            if (plug.IsBusyOrCoolingDown || MissionReload.IsRunning || NOSaveSystemPlugin.CoreRunning || NoSaveReconcile.LoadInProgress || NoSaveUi.ContinueInProgress)
            { NoSaveV0100.Log("main-menu Load refused: busy"); SetStatus("NOSave busy - try again in a moment"); return; }
            if (path != null && !File.Exists(path)) { SetStatus("Save file missing - list refreshed"); Refresh(); return; }
            Close("main-menu load started", true);
            NoSaveV0100.Log("main-menu Load started (" + what + "): " + (path ?? plug.AutosavePath));
            plug.StartCoroutine(NoSaveCoroutines.Safe(NoSaveUi.ContinueFromFileCo(path), "SaveMenu.MainMenuLoad"));
        }

        private void OnDelete(SaveEntry e)
        {
            NoSaveV0100.Log("menu Delete clicked (awaiting confirm): " + e.Path);
            if (_confirm == null) return;
            _pending = () =>
            {
                var plug = NOSaveSystemPlugin.Instance;
                if (!NOSaveSystemPlugin.GateSp("SAVE MENU delete", _mainMenu)) { Close(_mainMenu ? "left main menu" : "left SP host", true); return; }
                if (_mainMenu && NoSaveUi.ContinueInProgress) { SetStatus("Load running - delete later"); return; }
                if (plug != null && (MissionReload.IsRunning || NOSaveSystemPlugin.CoreRunning)) { SetStatus("Restore running - delete later"); return; }
                NoSaveV0100.Log("menu Delete confirmed: " + e.Path + (e.IsLatest ? " (latest/autosave)" : ""));
                NoSaveSaves.Delete(e);
                Refresh();
                SetStatus("Deleted save from " + Fmt(e.Time));
            };
            if (_confirmText != null) _confirmText.text = NoSaveUi.Ascii("Delete this save?\n" + Fmt(e.Time) + "  -  " + e.Mission + "  -  " + e.Aircraft);
            _confirm.SetActive(true);
            _confirm.transform.SetAsLastSibling();
        }
    }

    /// <summary>Retry injection while the pause menu is open (e.g. if the Open postfix was missed).</summary>
    internal class NoSaveV0100Behaviour : MonoBehaviour
    {
        private float _next;
        private void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (NoSaveV0100.MainPanelOpen && Input.GetKeyDown(KeyCode.Escape)) NoSaveV0100.HandleEscMain(); // v0.11.0
            if (now < _next) return;
            _next = now + 0.5f;
            NoSaveV0100.Poll();
            NoSaveV0100.PollMain("poll"); // v0.11.0
        }
    }

    [HarmonyPatch(typeof(LeaderboardMenu), nameof(LeaderboardMenu.Open))]
    internal static class Patch_LeaderboardMenu_Open_V0100
    {
        static void Postfix(LeaderboardMenu __instance, MenuMode mode)
        {
            try { NoSaveV0100.ResetPollBackoff(); NoSaveV0100.OnMenuOpened(__instance, mode, "LeaderboardMenu.Open"); }
            catch (Exception ex) { NoSaveV0100.Warn("Open postfix: " + ex.Message); }
        }
    }

    /// <summary>v0.11.0: inject the main-menu SAVE MENU button when the main menu starts.</summary>
    [HarmonyPatch(typeof(MainMenu), "Start")]
    internal static class Patch_MainMenu_Start_V0110
    {
        static void Postfix(MainMenu __instance)
        {
            try { NoSaveV0100.OnMainMenuStart(__instance, "MainMenu.Start"); }
            catch (Exception ex) { NoSaveV0100.Warn("MainMenu.Start postfix: " + ex.Message); }
        }
    }

    /// <summary>ESC while the save menu is open closes the save menu (or its confirm) instead of resuming the game.</summary>
    [HarmonyPatch(typeof(GameplayUI), "Update")]
    internal static class Patch_GameplayUI_Update_V0100
    {
        static bool Prefix()
        {
            try
            {
                if (NoSaveV0100.PanelOpen && (Input.GetKeyDown(KeyCode.Escape) || NoSaveV0100.PauseActionDown()))
                {
                    NoSaveV0100.HandleEsc();
                    return false;
                }
            }
            catch { }
            return true;
        }
    }
}
