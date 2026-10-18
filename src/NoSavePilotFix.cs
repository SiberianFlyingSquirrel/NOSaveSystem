using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace NOSaveSystem
{
    /// <summary>
    /// v0.9.2: pilot / throttle restore.
    /// Root causes (Assembly-CSharp):
    ///  * Pilot.SetStartingAiState picks AITaxiState for any plane with radarAlt <= spawnOffset.y+1, so an AI plane
    ///    restored mid-takeoff-roll (30-40 m/s on the runway) re-plans a taxi route back to the runway start and veers off.
    ///  * PilotPlayerState.EnterState resets private simulatedThrottle (0.4 airborne / -1 ground) and writes it to
    ///    ControlInputs.throttle every frame, wiping the restored throttle.
    /// Fix: after the game's own Pilot init, use game-native transitions (SwitchState(new AIPilotTakeoffState()))
    /// and set PilotPlayerState.simulatedThrottle.
    /// </summary>
    internal static class NoSavePilotFix
    {
        private static readonly FieldInfo SimThrottleField =
            typeof(PilotPlayerState).GetField("simulatedThrottle", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        // v0.9.4: fixes recorded per load, re-asserted once on the first ResumeGame after a load-pause.
        private static readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Aircraft, SavedAircraftDto>> _pending =
            new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Aircraft, SavedAircraftDto>>();
        private static float _pendingSetAt = -1000f;

        internal static void ClearPending() { _pending.Clear(); }

        // ---------------- v0.11.0: player throttle accumulator (keyboard primary) ----------------
        // PilotPlayerState.PlayerThrottleAxis1Controls (Update, or FixedUpdate when auto-hover is on):
        //   num = Rewired GetAxisRaw("Throttle")  - keyboard throttle keys give a digital -1/0/+1 on this axis
        //   axis moved a little (0 < |num-prev| < 0.5)  -> simulatedThrottle = num            (analog axis / HOTAS)
        //   else if |num| > 0.5 (key held)              -> simulatedThrottle += clamp(num - sim, -dt, +dt)  (keyboard ramp)
        //   controlInputs.throttle = clamp01( throttleUseNegative ? 0.5*(sim+1) : sim , then 1-x for an inverted collective )
        // So the incremental KB throttle lives in the PRIVATE float PilotPlayerState.simulatedThrottle (range -1..1 with
        // throttleUseNegative); controlInputs.throttle is rewritten from it every call and is never read back.
        // PlayerControls() skips this whole block while !GameManager.flightControlsEnabled or pilotStrength < 0.2 (G-LOC).
        private static readonly FieldInfo CollectiveField =
            typeof(PilotPlayerState).GetField("collective", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo PilotStrengthField =
            typeof(PilotPlayerState).GetField("pilotStrength", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo RewiredPlayerField =
            typeof(PilotPlayerState).GetField("player", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static MethodInfo _getAxisRaw; private static bool _getAxisRawLooked;
        private static readonly object[] _axisArgs = { "Throttle" };

        private static bool InvertedCollective(PilotPlayerState ps)
        {
            if (!PlayerSettings.invertCollective || ps == null || CollectiveField == null) return false;
            try { return (bool)CollectiveField.GetValue(ps); } catch { return false; }
        }

        /// <summary>Exact copy of the game's accumulator -> controlInputs.throttle mapping.</summary>
        internal static float AccToThrottle(PilotPlayerState ps, float sim)
        {
            return ThrottleMath.MapAccToThrottle(sim, PlayerSettings.throttleUseNegative, InvertedCollective(ps)); // v0.11.1: pure math
        }

        /// <summary>Inverse of AccToThrottle (for seeding the accumulator from a saved controlInputs.throttle).</summary>
        internal static float ThrottleToAcc(PilotPlayerState ps, float thr)
        {
            return ThrottleMath.ThrottleToAcc(thr, PlayerSettings.throttleUseNegative, InvertedCollective(ps)); // v0.11.1: pure math
        }

        /// <summary>v0.11.0: the accumulator value that reproduces the saved throttle. The saved simulatedThrottle is used
        /// only if it maps to the saved controlInputs.throttle (within 0.005); otherwise the inverse of the saved throttle,
        /// so the keyboard throttle continues exactly from the saved power (no jump on release).</summary>
        internal static float SeedValue(PilotPlayerState ps, SavedAircraftDto dto, out string how)
        {
            bool usedSaved;
            float seed = ThrottleMath.SeedAcc(dto.throttle, dto.simThrottleCaptured, dto.simThrottle, PlayerSettings.throttleUseNegative, InvertedCollective(ps), out usedSaved);
            if (usedSaved) { how = "saved-acc"; return seed; }
            how = dto.simThrottleCaptured ? "inverse(saved throttle) - saved acc " + dto.simThrottle.ToString("0.###", CultureInfo.InvariantCulture) + " maps to "
                + AccToThrottle(ps, dto.simThrottle).ToString("0.###", CultureInfo.InvariantCulture) : "inverse(saved throttle)";
            return seed;
        }

        /// <summary>Player throttle as the game will produce it from the accumulator. controlsOn = the game is
        /// currently writing controlInputs.throttle from it (flight controls enabled, no G-LOC).</summary>
        internal static bool TryPlayerInput(Aircraft ac, out float thr, out float acc, out bool controlsOn)
        {
            thr = float.NaN; acc = float.NaN; controlsOn = false;
            try
            {
                var p = GetPilot(ac);
                if (p == null || !(p.currentState is PilotPlayerState ps)) return false;
                acc = ReadSimThrottle(ps);
                if (float.IsNaN(acc)) return false;
                thr = AccToThrottle(ps, acc);
                float strength = 1f;
                try { if (PilotStrengthField != null) strength = (float)PilotStrengthField.GetValue(ps); } catch { }
                controlsOn = GameManager.flightControlsEnabled && strength >= 0.2f;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Raw Rewired "Throttle" axis (keyboard: -1/0/+1 while a throttle key is held). NaN if unreadable.</summary>
        internal static float RawThrottleAxis(Aircraft ac)
        {
            try
            {
                var p = GetPilot(ac);
                if (p == null || !(p.currentState is PilotPlayerState ps) || RewiredPlayerField == null) return float.NaN;
                var rp = RewiredPlayerField.GetValue(ps); if (rp == null) return float.NaN;
                if (!_getAxisRawLooked) { _getAxisRawLooked = true; _getAxisRaw = rp.GetType().GetMethod("GetAxisRaw", new[] { typeof(string) }); }
                if (_getAxisRaw == null) return float.NaN;
                return (float)_getAxisRaw.Invoke(rp, _axisArgs);
            }
            catch { return float.NaN; }
        }

        private static System.Reflection.PropertyInfo _ctrlProp; private static System.Reflection.MethodInfo _lastActive; private static bool _ctrlLooked;
        /// <summary>v0.11.1: Rewired type name of the player's last active controller ("Keyboard"/"Mouse"/"Joystick"/"Custom"),
        /// read through the game's Rewired player (no device names). null if unreadable.</summary>
        internal static string LastActiveControllerType(Aircraft ac)
        {
            try
            {
                var p = GetPilot(ac);
                if (p == null || !(p.currentState is PilotPlayerState ps) || RewiredPlayerField == null) return null;
                var rp = RewiredPlayerField.GetValue(ps); if (rp == null) return null;
                if (!_ctrlLooked)
                {
                    _ctrlLooked = true;
                    _ctrlProp = rp.GetType().GetProperty("controllers");
                    var cm = _ctrlProp != null ? _ctrlProp.PropertyType : null;
                    _lastActive = cm != null ? cm.GetMethod("GetLastActiveController", Type.EmptyTypes) : null;
                }
                if (_ctrlProp == null || _lastActive == null) return null;
                var cm2 = _ctrlProp.GetValue(rp, null); if (cm2 == null) return null;
                var c = _lastActive.Invoke(cm2, null); if (c == null) return null;
                var tp = c.GetType().GetProperty("type");
                var v = tp != null ? tp.GetValue(c, null) : null;
                return v != null ? v.ToString() : null;
            }
            catch { return null; }
        }

        private static readonly System.Collections.Generic.HashSet<int> _seeded = new System.Collections.Generic.HashSet<int>();
        internal static void ClearSeeded() { _seeded.Clear(); }
        /// <summary>v0.11.0: the accumulator of this aircraft's PilotPlayerState was seeded from the save in this load.</summary>
        internal static bool IsSeeded(Aircraft ac) { try { return ac != null && _seeded.Contains(ac.GetInstanceID()); } catch { return false; } }

        private static float SeedAccumulator(Aircraft ac, SavedAircraftDto dto, PilotPlayerState ps, string label, string where)
        {
            try { _seeded.Add(ac.GetInstanceID()); } catch { }
            string how; float sim = SeedValue(ps, dto, out how);
            try { if (SimThrottleField != null) SimThrottleField.SetValue(ps, sim); else Log(label + " REFLECT MISS PilotPlayerState.simulatedThrottle"); } catch { }
            try { var inp = ac.GetInputs(); if (inp != null) inp.throttle = dto.throttle; } catch { }
            NOSaveSystemPlugin.Log?.LogInfo("NOSAVE-THR " + label + " SEED " + where + " acc(simulatedThrottle)=" + sim.ToString("0.###", CultureInfo.InvariantCulture)
                + " -> throttle=" + AccToThrottle(ps, sim).ToString("0.###", CultureInfo.InvariantCulture) + " saved=" + dto.throttle.ToString("0.###", CultureInfo.InvariantCulture)
                + " src=" + how + " negative=" + PlayerSettings.throttleUseNegative + " relative=" + PlayerSettings.throttleUseRelative + " invertedCollective=" + InvertedCollective(ps));
            return sim;
        }

        internal static float ReadSimThrottle(PilotPlayerState ps)
        {
            try { return SimThrottleField != null ? (float)SimThrottleField.GetValue(ps) : float.NaN; } catch { return float.NaN; }
        }

        /// <summary>v0.10.2: give a (re-)entered PilotPlayerState the saved throttle input once; the player's own input drives it afterwards.</summary>
        internal static void ApplyPlayerThrottle(Aircraft ac, SavedAircraftDto dto, PilotPlayerState ps, string label)
        {
            if (ac == null || dto == null || ps == null) return;
            SeedAccumulator(ac, dto, ps, label, "player-state-entered");
        }

        internal static void ReassertOnResume()
        {
            if (_pending.Count == 0) return;
            if (Time.unscaledTime - _pendingSetAt > 600f) { _pending.Clear(); return; }
            Log("resume: re-asserting " + _pending.Count + " aircraft");
            foreach (var kv in _pending)
            {
                var ac = kv.Key; var dto = kv.Value;
                bool alive = false;
                try { alive = ac != null && !ac.Equals(null) && !ac.disabled; } catch { }
                if (!alive || dto == null) continue;
                string label = "?"; try { label = ac.UniqueName ?? ac.name; } catch { }
                // v0.10.2: no engine/gear writes on resume - the post-load prefix lock owns the window, later writes are blocked.
                try { NoSaveEngineGear.OnResume(ac, "PilotFix.resume"); } catch { }
                var p = GetPilot(ac);
                if (p == null || p.currentState == null) continue;
                try
                {
                    if (p.currentState is PilotPlayerState ps)
                    {
                        // v0.11.0: the hold starts on this unpause; the accumulator is re-seeded so keyboard ramps start from saved
                        SeedAccumulator(ac, dto, ps, label, "resume");
                    }
                    else if (dto.pilotCaptured && dto.pilotState == "AIPilotTakeoffState" && p.currentState is AIPilotTaxiState
                             && p.pilotType == Pilot.PilotType.Plane)
                    {
                        p.AITakeoffState = new AIPilotTakeoffState();
                        p.SwitchState(p.AITakeoffState);
                        Log(label + " resume: re-entered AIPilotTakeoffState");
                    }
                }
                catch (Exception ex) { Log(label + " resume reassert FAIL " + ex.Message); }
            }
            _pending.Clear();
        }

        private static void Log(string m) { NOSaveSystemPlugin.Log?.LogInfo("PilotFix: " + m); }

        internal static Pilot GetPilot(Aircraft ac)
        {
            try
            {
                if (ac == null || ac.pilots == null || ac.pilots.Length == 0) return null;
                return ac.pilots[0];
            }
            catch { return null; }
        }

        internal static void CapturePilot(Aircraft ac, SavedAircraftDto dto)
        {
            var p = GetPilot(ac);
            if (p == null || dto == null) return;
            try { dto.pilotState = p.currentState != null ? p.currentState.GetType().Name : ""; } catch { }
            try { dto.pilotStateName = p.GetCurrentState() ?? ""; } catch { }
            try { dto.hasTakenOff = p.flightInfo != null && p.flightInfo.HasTakenOff; } catch { }
            dto.pilotCaptured = true;
            try
            {
                if (p.currentState is PilotPlayerState ps && SimThrottleField != null)
                {
                    dto.simThrottle = (float)SimThrottleField.GetValue(ps);
                    dto.simThrottleCaptured = true;
                }
            }
            catch { }
        }

        internal static IEnumerator Run(Aircraft ac, SavedAircraftDto dto)
        {
            string label = "?";
            try { label = ac.UniqueName ?? ac.name; } catch { }
            try
            {
                if (Time.unscaledTime - _pendingSetAt > 30f) _pending.Clear();
                _pendingSetAt = Time.unscaledTime;
                _pending.Add(new System.Collections.Generic.KeyValuePair<Aircraft, SavedAircraftDto>(ac, dto));
            }
            catch { }
            if (dto == null || !dto.pilotCaptured)
            {
                Log(label + " skip (save has no pilot state; pre-0.9.2 save) -> controls only");
                // still re-apply throttle a few frames later for the player
            }
            // Wait for Pilot_OnInitialize to pick its starting state (max ~5s realtime).
            float waited = 0f;
            Pilot p = null;
            while (waited < 5f)
            {
                bool alive = false;
                try { alive = ac != null && !ac.Equals(null); } catch { }
                if (!alive) { Log(label + " aircraft gone before init"); yield break; }
                p = GetPilot(ac);
                if (p != null && p.currentState != null) break;
                waited += NoSaveWait.Dt(); // v0.11.0 PAUSE0: pause-aware (max 120x while the pause menu is up)
                yield return null;
            }
            if (p == null || p.currentState == null) { Log(label + " pilot/state not ready after 5s - skipped"); NoSaveWait.Timeout("pilot-init " + label, 5f, "pilot fix skipped (engine hold armed from the spawn path only)"); yield break; }
            yield return null; // one more frame so EnterState side-effects settled
            // v0.9.5: engine spool + instant gear after the game's own init (Aircraft init calls SetGear(true) on ground)
            try { NoSaveEngineGear.ApplyEngines(ac, dto, label, "PilotFix.init"); } catch (Exception ex) { Log(label + " engines FAIL " + ex.Message); }
            try { NoSaveEngineGear.ApplyGear(ac, dto, label); } catch (Exception ex) { Log(label + " gear FAIL " + ex.Message); }

            string cur = "?";
            try { cur = p.currentState?.GetType().Name ?? "null"; } catch { }
            Log(label + " live state=" + cur + " saved=" + (dto?.pilotState ?? "?") + " ('" + (dto?.pilotStateName ?? "") + "')");

            if (dto != null && dto.pilotCaptured)
            {
                try { if (p.flightInfo != null) p.flightInfo.HasTakenOff = dto.hasTakenOff; } catch (Exception ex) { Log(label + " HasTakenOff set FAIL " + ex.Message); }
            }

            ControlInputs inputs = null;
            try { inputs = ac.GetInputs(); } catch { }

            // ---- Player ----
            if (p.currentState is PilotPlayerState ps)
            {
                // v0.11.0: seed the keyboard/axis accumulator so the throttle continues from the saved value
                if (dto != null) { try { SeedAccumulator(ac, dto, ps, label, "pilot-init"); } catch (Exception ex) { Log(label + " player throttle FAIL " + ex.Message); } }
                try { if (inputs != null && dto != null) { inputs.throttle = dto.throttle; if (dto.pilotCaptured) inputs.customAxis1 = dto.customAxis1; } } catch { }
                yield break;
            }

            if (dto == null || !dto.pilotCaptured) yield break;
            string saved = dto.pilotState ?? "";

            // ---- AI plane that was on its takeoff roll: resume native takeoff instead of re-taxi ----
            if (saved == "AIPilotTakeoffState" && p.pilotType == Pilot.PilotType.Plane)
            {
                if (p.currentState is AIPilotTakeoffState) { Log(label + " already in takeoff state"); }
                else
                {
                    try
                    {
                        // same transition the game uses in AIPilotTaxiState.WaitingForTakeoffClearance
                        p.AITakeoffState = new AIPilotTakeoffState();
                        p.SwitchState(p.AITakeoffState);
                        if (p.AICombatState == null) p.AICombatState = new AIPilotCombatModes(ac);
                        if (p.AILandingState == null) p.AILandingState = new AIPilotLandingState();
                        Log(label + " resumed AIPilotTakeoffState (was " + cur + ") -> " + p.GetCurrentState());
                    }
                    catch (Exception ex)
                    {
                        Log(label + " takeoff resume FAIL " + ex.Message + " -> fallback taxi re-plan from standstill");
                        StopOnGround(ac, label);
                        try { p.AITaxiState = new AIPilotTaxiState(); p.SwitchState(p.AITaxiState); } catch (Exception ex2) { Log(label + " taxi fallback FAIL " + ex2.Message); }
                    }
                }
                try { if (inputs != null) { inputs.throttle = dto.throttle; inputs.brake = 0f; } } catch { }
                yield break;
            }

            // ---- AI on the ground in taxi (or anything grounded that the game put into taxi) ----
            if (p.currentState is AIPilotTaxiState)
            {
                // Taxi re-plans from the current position (native SearchForAirbase). Fast ground movers (not a takeoff
                // roll) can't follow the new taxi path at speed -> stop them so the re-plan starts consistent.
                float speed = 0f;
                try { speed = ac.rb != null ? ac.rb.velocity.magnitude : 0f; } catch { }
                if (speed > 12f) { Log(label + " grounded taxi at " + speed.ToString("0.0") + " m/s -> stopping for clean re-plan"); StopOnGround(ac, label); }
                Log(label + " native taxi re-plan (saved '" + dto.pilotStateName + "')");
                yield break;
            }

            // Airborne AI: keep game's combat/helo state, velocity is already restored. Inputs are AI-driven.
            Log(label + " keep state " + cur + " (airborne/other); throttle=" + dto.throttle.ToString("0.###"));
        }

        private static void StopOnGround(Aircraft ac, string label)
        {
            try
            {
                if (ac?.rb == null) return;
                ac.rb.velocity = Vector3.zero;
                ac.rb.angularVelocity = Vector3.zero;
                var inp = ac.GetInputs();
                if (inp != null) { inp.throttle = 0f; inp.brake = 1f; }
                Log(label + " zeroed velocity");
            }
            catch (Exception ex) { Log(label + " StopOnGround FAIL " + ex.Message); }
        }
    }
}
