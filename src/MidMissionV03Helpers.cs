using System;
using System.Globalization;
using System.Collections.Generic;
using System.Text;
using Mirage;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace NOSaveSystem
{
    internal static class MidMissionV03Helpers
    {
        public static Aircraft FindLocalPlayerAircraft()
        {
            try
            {
                if (GameManager.GetLocalPlayer(out Player local) && local != null && local.Aircraft != null)
                    return local.Aircraft;
            }
            catch { }

            try
            {
                foreach (var kv in UnitRegistry.playerLookup)
                {
                    var p = kv.Value;
                    if (p == null || p.Aircraft == null) continue;
                    if (p.IsLocalPlayer) return p.Aircraft;
                }
            }
            catch { }

            try
            {
                foreach (var kv in UnitRegistry.playerLookup)
                {
                    if (kv.Value != null && kv.Value.Aircraft != null)
                        return kv.Value.Aircraft;
                }
            }
            catch { }
            return null;
        }

        public static Player FindLocalPlayer()
        {
            try
            {
                if (GameManager.GetLocalPlayer(out Player local) && local != null)
                    return local;
            }
            catch { }
            try
            {
                foreach (var kv in UnitRegistry.playerLookup)
                {
                    if (kv.Value != null && kv.Value.IsLocalPlayer) return kv.Value;
                }
            }
            catch { }
            try
            {
                foreach (var kv in UnitRegistry.playerLookup)
                {
                    if (kv.Value != null) return kv.Value;
                }
            }
            catch { }
            return null;
        }

        public static List<string> CaptureLoadoutKeys(Aircraft ac)
        {
            var keys = new List<string>();
            if (ac == null) return keys;
            try
            {
                var wm = ac.weaponManager;
                if (wm != null && wm.hardpointSets != null)
                {
                    foreach (var set in wm.hardpointSets)
                    {
                        if (set == null) { keys.Add(""); continue; }
                        string key = "";
                        try
                        {
                            if (set.weaponMount != null && set.weaponMount.jsonKey != null)
                                key = set.weaponMount.jsonKey;
                        }
                        catch { }
                        keys.Add(key);
                    }
                    if (keys.Count > 0) return keys;
                }
            }
            catch { }

            try
            {
                var loadout = ac.loadout;
                if (loadout != null && loadout.weapons != null)
                {
                    foreach (var mount in loadout.weapons)
                        keys.Add(mount != null && mount.jsonKey != null ? mount.jsonKey : "");
                }
            }
            catch { }
            return keys;
        }

        public static List<FactionEconomyDto> CaptureFactions()
        {
            var list = new List<FactionEconomyDto>();
            try
            {
                foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
                {
                    if (hq == null) continue;
                    var dto = new FactionEconomyDto();
                    try { dto.factionName = hq.faction != null ? hq.faction.factionName : null; } catch { }
                    try { dto.factionFunds = hq.factionFunds; } catch { }
                    try { dto.factionScore = hq.factionScore; } catch { }
                    list.Add(dto);
                }
            }
            catch (Exception ex)
            {
                NOSaveSystemPlugin.Log?.LogWarning("CaptureFactions: " + ex.Message);
            }
            return list;
        }

        public static List<ObjectiveDto> CaptureActiveObjectives()
        {
            var list = new List<ObjectiveDto>();
            try
            {
                var runner = MissionManager.Runner;
                if (runner == null || runner.ActiveObjectives == null) return list;
                foreach (var obj in runner.ActiveObjectives)
                {
                    if (obj == null) continue;
                    var dto = new ObjectiveDto();
                    try
                    {
                        if (obj.SavedObjective != null)
                        {
                            dto.uniqueName = obj.SavedObjective.UniqueName;
                            dto.displayName = obj.SavedObjective.DisplayName;
                            dto.faction = obj.SavedObjective.Faction;
                            try { dto.hidden = obj.SavedObjective.Hidden; } catch { }
                            try { dto.objectiveType = obj.SavedObjective.ObjectiveTypeEnum.ToString(); } catch { }
                            try
                            {
                                dto.outcomesCount = obj.SavedObjective.Outcomes != null
                                    ? obj.SavedObjective.Outcomes.Count
                                    : 0;
                            }
                            catch { }
                        }
                    }
                    catch { }
                    try
                    {
                        if (obj.Outcomes != null && obj.Outcomes.Count > dto.outcomesCount)
                            dto.outcomesCount = obj.Outcomes.Count;
                    }
                    catch { }
                    try { dto.status = obj.Status.ToString(); } catch { }
                    try { dto.completePercent = obj.CompletePercent; } catch { }
                    list.Add(dto);
                }
            }
            catch (Exception ex)
            {
                NOSaveSystemPlugin.Log?.LogWarning("CaptureActiveObjectives: " + ex.Message);
            }
            return list;
        }

        public static void LogObjectiveSnapshotDelta(List<ObjectiveDto> saved, StringBuilder plan)
        {
            if (saved == null || saved.Count == 0) return;
            try
            {
                var live = CaptureActiveObjectives();
                int matched = 0, statusMismatch = 0, missingLive = 0, extraLive = 0;
                var liveBy = new Dictionary<string, ObjectiveDto>(StringComparer.OrdinalIgnoreCase);
                foreach (var o in live)
                {
                    if (o == null) continue;
                    string k = !string.IsNullOrEmpty(o.uniqueName) ? o.uniqueName : (o.displayName ?? "");
                    if (string.IsNullOrEmpty(k)) continue;
                    if (!liveBy.ContainsKey(k)) liveBy[k] = o;
                }
                var savedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in saved)
                {
                    if (s == null) continue;
                    string k = !string.IsNullOrEmpty(s.uniqueName) ? s.uniqueName : (s.displayName ?? "");
                    if (string.IsNullOrEmpty(k)) continue;
                    savedKeys.Add(k);
                    ObjectiveDto l;
                    if (!liveBy.TryGetValue(k, out l))
                    {
                        missingLive++;
                        plan?.AppendLine("    OBJ missing-live " + k + " savedStatus=" + (s.status ?? "?"));
                        continue;
                    }
                    matched++;
                    string ls = l.status ?? "";
                    string ss = s.status ?? "";
                    if (!string.Equals(ls, ss, StringComparison.OrdinalIgnoreCase))
                    {
                        statusMismatch++;
                        plan?.AppendLine("    OBJ status-delta " + k + " saved=" + ss + " live=" + ls);
                    }
                }
                foreach (var kv in liveBy)
                {
                    if (!savedKeys.Contains(kv.Key))
                    {
                        extraLive++;
                        plan?.AppendLine("    OBJ extra-live " + kv.Key + " status=" + (kv.Value.status ?? "?"));
                    }
                }
                plan?.AppendLine("  OBJECTIVES delta matched=" + matched + " statusMismatch=" + statusMismatch +
                    " missingLive=" + missingLive + " extraLive=" + extraLive + " (read-only; no Complete())");
            }
            catch (Exception ex)
            {
                plan?.AppendLine("  OBJECTIVES delta FAIL: " + ex.Message);
            }
        }

        public static int TryApplyObjectiveStatuses(List<ObjectiveDto> saved, StringBuilder plan)
        {
            int applied = 0, skipped = 0, missing = 0, parseFail = 0;
            if (saved == null || saved.Count == 0) return 0;
            try
            {
                var runner = MissionManager.Runner;
                if (runner == null || runner.ActiveObjectives == null)
                {
                    plan?.AppendLine("  OBJECTIVES status-apply SKIP: no MissionManager.Runner/ActiveObjectives");
                    return 0;
                }

                var liveBy = new Dictionary<string, Objective>(StringComparer.OrdinalIgnoreCase);
                foreach (var obj in runner.ActiveObjectives)
                {
                    if (obj == null || obj.SavedObjective == null) continue;
                    string k = obj.SavedObjective.UniqueName;
                    if (string.IsNullOrEmpty(k)) k = obj.SavedObjective.DisplayName;
                    if (string.IsNullOrEmpty(k)) continue;
                    if (!liveBy.ContainsKey(k)) liveBy[k] = obj;
                }

                foreach (var s in saved)
                {
                    if (s == null) continue;
                    string k = !string.IsNullOrEmpty(s.uniqueName) ? s.uniqueName : (s.displayName ?? "");
                    if (string.IsNullOrEmpty(k)) { skipped++; continue; }
                    Objective live;
                    if (!liveBy.TryGetValue(k, out live) || live == null)
                    {
                        missing++;
                        plan?.AppendLine("    OBJ status-apply missing-live " + k);
                        continue;
                    }
                    if (string.IsNullOrEmpty(s.status)) { skipped++; continue; }

                    ObjectiveStatus parsed;
                    if (!Enum.TryParse(s.status, true, out parsed))
                    {
                        parseFail++;
                        plan?.AppendLine("    OBJ status-apply parseFail " + k + " raw=" + s.status);
                        continue;
                    }

                    try
                    {
                        var before = live.Status;
                        if (before.Equals(parsed)) { skipped++; continue; }
                        live.Status = parsed;
                        applied++;
                        plan?.AppendLine("    OBJ status-apply " + k + " " + before + " -> " + parsed);
                    }
                    catch (Exception ex)
                    {
                        plan?.AppendLine("    OBJ status-apply FAIL " + k + ": " + ex.Message);
                    }
                }

                plan?.AppendLine("  OBJECTIVES status-apply applied=" + applied + " skipped=" + skipped +
                    " missing=" + missing + " parseFail=" + parseFail + " (Status field only; Complete() not called)");
            }
            catch (Exception ex)
            {
                plan?.AppendLine("  OBJECTIVES status-apply FAIL: " + ex.Message);
            }
            return applied;
        }

        public static List<PartHpDto> CapturePartHealth(Unit unit)
        {
            var list = new List<PartHpDto>();
            if (unit == null) return list;
            try
            {
                List<UnitPart> parts = null;
                try { parts = unit.GetAllParts(); } catch { }
                if (parts == null)
                {
                    try { parts = unit.partLookup; } catch { }
                }
                if (parts == null) return list;
                foreach (var part in parts)
                {
                    if (part == null) continue;
                    var dto = new PartHpDto();
                    try { dto.id = part.id; } catch { }
                    try { dto.hitPoints = part.hitPoints; } catch { }
                    try { dto.detached = part.IsDetached(); } catch { }
                    dto.critical = false;
                    try { dto.name = part.gameObject != null ? part.gameObject.name : part.name; } catch
                    {
                        try { dto.name = part.name; } catch { }
                    }
                    list.Add(dto);
                }
            }
            catch (Exception ex)
            {
                NOSaveSystemPlugin.Log?.LogWarning("CapturePartHealth: " + ex.Message);
            }
            return list;
        }

        public static int RestorePartHealthExact(Unit unit, List<PartHpDto> saved, StringBuilder plan)
        {
            if (unit == null || saved == null || saved.Count == 0) return 0;
            int applied = 0;
            int failed = 0;
            try
            {
                List<UnitPart> parts = null;
                try { parts = unit.GetAllParts(); } catch { }
                if (parts == null)
                {
                    try { parts = unit.partLookup; } catch { }
                }
                if (parts == null) return 0;

                var byId = new Dictionary<byte, UnitPart>();
                foreach (var p in parts)
                {
                    if (p == null) continue;
                    try { byId[p.id] = p; } catch { }
                }

                foreach (var dto in saved)
                {
                    if (dto == null) continue;
                    if (dto.detached) continue;
                    if (!byId.TryGetValue(dto.id, out var live) || live == null) continue;
                    try { if (live.IsDetached()) continue; } catch { continue; }

                    float liveHp;
                    try { liveHp = live.hitPoints; }
                    catch { continue; }

                    float target = dto.hitPoints;
                    if (target > 100f) target = 100f;
                    if (target < 0f) target = 0f;
                    if (Math.Abs(target - liveHp) < 0.05f) continue;
                    try
                    {
                        live.hitPoints = target;
                        applied++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        plan?.AppendLine("  PARTHP FAIL id=" + dto.id + " target=" + target + ": " + ex.Message);
                    }
                }

                if (applied > 0 || failed > 0)
                    plan?.AppendLine("  PARTHP exact applied=" + applied + " failed=" + failed +
                                     " on " + (unit.UniqueName ?? unit.name));
            }
            catch (Exception ex)
            {
                plan?.AppendLine("  PARTHP FAIL: " + ex.Message);
            }
            return applied;
        }

        public static void CaptureAircraftControls(Aircraft ac, SavedAircraftDto dto)
        {
            if (ac == null || dto == null) return;
            try
            {
                var inputs = ac.GetInputs();
                if (inputs != null)
                {
                    dto.throttle = inputs.throttle;
                    dto.brake = inputs.brake;
                    dto.customAxis1 = inputs.customAxis1;
                    dto.controlsCaptured = true;
                }
            }
            catch { }
            try { NoSavePilotFix.CapturePilot(ac, dto); } catch { }
            try { NoSaveEngineGear.Capture(ac, dto); } catch { }
            try { dto.v096 = NoSaveV096.CaptureExtra(ac); } catch { }

            try
            {
                dto.engineRpmRatios = new List<float>();
                if (ac.engines != null)
                {
                    foreach (var eng in ac.engines)
                    {
                        if (eng == null) { dto.engineRpmRatios.Add(0f); continue; }
                        float ratio = 0f;
                        try { ratio = eng.GetRPMRatio(); } catch
                        {
                            try
                            {
                                float rpm = eng.GetRPM();
                                ratio = rpm;
                            }
                            catch { }
                        }
                        dto.engineRpmRatios.Add(ratio);
                    }
                }
            }
            catch { }
        }

        public static void RestoreAircraftControls(Aircraft ac, SavedAircraftDto dto, StringBuilder plan)
        {
            if (ac == null || dto == null || !dto.controlsCaptured) return;
            try
            {
                var inputs = ac.GetInputs();
                if (inputs != null)
                {
                    inputs.throttle = dto.throttle;
                    inputs.brake = dto.brake;
                    if (dto.pilotCaptured) inputs.customAxis1 = dto.customAxis1;
                    plan?.AppendLine("  CONTROLS throttle=" + dto.throttle.ToString("0.###") +
                                     " brake=" + dto.brake.ToString("0.###") +
                                     " -> " + (ac.UniqueName ?? ac.name));
                }
            }
            catch (Exception ex)
            {
                plan?.AppendLine("  CONTROLS FAIL: " + ex.Message);
            }
        }

        public static void ShowGameFeedback(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            try
            {
                if (MessageUI.i != null)
                {
                    MessageUI.i.GameMessage(message);
                    return;
                }
            }
            catch { }

            try
            {
                MissionMessages.ShowMessage(message, false, null, false);
                return;
            }
            catch { }

            try
            {
                if (AircraftActionsReport.i != null)
                    AircraftActionsReport.i.ReportText(message, 3.5f);
            }
            catch { }
        }

        public static int RestoreFactionEconomy(List<FactionEconomyDto> factions, StringBuilder plan)
        {
            if (factions == null || factions.Count == 0) return 0;
            int n = 0;
            foreach (var dto in factions)
            {
                if (dto == null || string.IsNullOrEmpty(dto.factionName)) continue;
                try
                {
                    var hq = FactionRegistry.HqFromName(dto.factionName);
                    if (hq == null)
                    {
                        plan.AppendLine("  ECONOMY skip missing HQ " + dto.factionName);
                        continue;
                    }
                    hq.SetFunds(dto.factionFunds);
                    hq.SetScore(dto.factionScore);
                    n++;
                    plan.AppendLine("  ECONOMY " + dto.factionName + " funds=" + dto.factionFunds + " score=" + dto.factionScore);
                }
                catch (Exception ex)
                {
                    plan.AppendLine("  ECONOMY FAIL " + dto.factionName + ": " + ex.Message);
                }
            }
            return n;
        }

        public static bool RebindPlayerToAircraft(Player player, Aircraft craft, StringBuilder plan)
        {
            if (player == null || craft == null) return false;
            try
            {
                bool alreadyBound = false;
                try { alreadyBound = ReferenceEquals(player.Aircraft, craft); } catch { }

                if (!alreadyBound)
                {
                    player.SetAircraft(craft);
                    plan.AppendLine("  REBIND SetAircraft -> " + craft.UniqueName);
                }

                player.AttachToAircraft(craft);
                plan.AppendLine("  REBIND AttachToAircraft -> " + craft.UniqueName +
                    (alreadyBound ? " (same craft)" : " (after SetAircraft)"));
                return true;
            }
            catch (Exception ex)
            {
                plan.AppendLine("  REBIND FAIL: " + ex.Message);
                return false;
            }
        }

        public static SavedLoadout BuildSavedLoadout(List<string> keys)
        {
            var sl = new SavedLoadout();
            if (keys == null) return sl;
            foreach (var key in keys)
                sl.Selected.Add(new SavedLoadout.SelectedMount { Key = key ?? "" });
            return sl;
        }

        public static bool ValidateTypeExists(string type)
        {
            if (string.IsNullOrEmpty(type)) return false;
            try { return Encyclopedia.Lookup != null && Encyclopedia.Lookup.ContainsKey(type); }
            catch { return true; }
        }

        public static string EnsureSpawnUniqueName(SavedUnitDto dto)
        {
            if (dto == null) return string.Empty;
            if (!string.IsNullOrEmpty(dto.private_uniqueName))
                return dto.private_uniqueName;

            bool isPlayer = false;
            try
            {
                if (dto is SavedAircraftDto acDto)
                    isPlayer = acDto.isPlayerAircraft || acDto.playerControlled;
            }
            catch { }

            string assigned;
            if (dto.persistentId != 0)
                assigned = "nosave_pid_" + dto.persistentId.ToString(CultureInfo.InvariantCulture);
            else if (isPlayer)
                assigned = "nosave_player_" + (string.IsNullOrEmpty(dto.type) ? "aircraft" : dto.type);
            else
                assigned = "nosave_" + (string.IsNullOrEmpty(dto.kind) ? "unit" : dto.kind) + "_" +
                           Guid.NewGuid().ToString("N").Substring(0, 8);

            string candidate = assigned;
            int n = 2;
            try
            {
                while (UnitRegistry.customIDLookup != null && UnitRegistry.customIDLookup.ContainsKey(candidate))
                {
                    candidate = assigned + "_" + n.ToString(CultureInfo.InvariantCulture);
                    n++;
                    if (n > 1000) break;
                }
            }
            catch { }

            dto.private_uniqueName = candidate;
            NOSaveSystemPlugin.Log?.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "Assigned UniqueName for spawn: {0} (type={1} kind={2} pid={3} player={4})",
                candidate, dto.type, dto.kind, dto.persistentId, isPlayer));
            return candidate;
        }

        public static void TryPersistUniqueNameOnLiveUnit(Unit unit, string uniqueName)
        {
            if (unit == null || string.IsNullOrEmpty(uniqueName)) return;
            try
            {
                string cur = null;
                try { cur = unit.UniqueName; } catch { }
                if (!string.IsNullOrEmpty(cur)) return;

                try { unit.NetworkUniqueName = uniqueName; } catch { }
                try
                {
                    if (UnitRegistry.customIDLookup != null && !UnitRegistry.customIDLookup.ContainsKey(uniqueName))
                        UnitRegistry.RegisterCustomID(uniqueName, unit);
                }
                catch { }

                string after = null;
                try { after = unit.UniqueName; } catch { }
                if (string.IsNullOrEmpty(after))
                {
                    NOSaveSystemPlugin.Log?.LogWarning(string.Format(CultureInfo.InvariantCulture,
                        "F5 Persist UniqueName '{0}' DID NOT STICK (UniqueName still empty, type={1}) - JSON has name; live may stay blank until respawn",
                        uniqueName, unit.GetType().Name));
                }
                else
                {
                    NOSaveSystemPlugin.Log?.LogInfo(string.Format(CultureInfo.InvariantCulture,
                        "F5 Persist UniqueName '{0}' on live unit (now='{1}' type={2})",
                        uniqueName, after, unit.GetType().Name));
                }
            }
            catch (Exception ex)
            {
                NOSaveSystemPlugin.Log?.LogWarning("TryPersistUniqueNameOnLiveUnit: " + ex.Message);
            }
        }

        public static Aircraft FindAircraftByUniqueName(string uniqueName)
        {
            if (string.IsNullOrEmpty(uniqueName)) return null;
            try
            {
                if (UnitRegistry.customIDLookup != null &&
                    UnitRegistry.customIDLookup.TryGetValue(uniqueName, out var u) &&
                    u is Aircraft ac)
                    return ac;
            }
            catch { }
            try
            {
                if (UnitRegistry.allAircraft != null)
                {
                    foreach (var ac in UnitRegistry.allAircraft)
                    {
                        if (ac == null) continue;
                        try { if (ac.UniqueName == uniqueName) return ac; } catch { }
                    }
                }
            }
            catch { }
            return null;
        }

        public static Aircraft SpawnAircraftFromDto(Spawner spawner, SavedAircraftDto dto)
        {
            if (spawner == null || dto == null) return null;
            if (!ValidateTypeExists(dto.type))
            {
                NOSaveSystemPlugin.Log?.LogWarning("SpawnSavedUnit skip: type not in Encyclopedia: " + dto.type);
                return null;
            }

            EnsureSpawnUniqueName(dto);

            if (!string.IsNullOrEmpty(dto.private_uniqueName) &&
                UnitRegistry.customIDLookup != null &&
                UnitRegistry.customIDLookup.ContainsKey(dto.private_uniqueName))
            {
                ForceClearUniqueNameReservation(dto.private_uniqueName, null);
            }

            bool wantPlayer = dto.isPlayerAircraft || dto.playerControlled;
            dto.isPlayerAircraft = wantPlayer;
            dto.playerControlled = wantPlayer;

            var saved = new SavedAircraft(dto.private_uniqueName ?? string.Empty)
            {
                type = dto.type,
                faction = dto.faction,
                globalPosition = dto.globalPosition != null
                    ? new GlobalPosition(dto.globalPosition.x, dto.globalPosition.y, dto.globalPosition.z)
                    : new GlobalPosition(0, 0, 0),
                rotation = dto.rotation != null
                    ? new Quaternion(dto.rotation.x, dto.rotation.y, dto.rotation.z, dto.rotation.w)
                    : Quaternion.identity,
                playerControlled = wantPlayer,
                fuel = dto.fuel > 0f ? dto.fuel : 1f,
                skill = dto.skill,
                bravery = dto.bravery,
                startingSpeed = dto.startingSpeed,
                savedLoadout = BuildSavedLoadout(dto.loadoutMountKeys)
            };

            try
            {
                spawner.SpawnSavedUnit(saved);
            }
            catch (Exception ex)
            {
                NOSaveSystemPlugin.Log?.LogError("SpawnSavedUnit exception aircraft type=" + dto.type +
                    " name=" + dto.private_uniqueName + ": " + ex);
                return null;
            }

            Aircraft ac = FindAircraftByUniqueName(dto.private_uniqueName);
            if (ac != null)
                return ac;

            if (wantPlayer)
            {
                var player = FindLocalPlayer();
                if (player != null)
                {
                    try
                    {
                        Aircraft spawnedPlayer;
                        if (spawner.TrySpawnPlayerControlled(player, out spawnedPlayer) && spawnedPlayer != null)
                        {
                            NOSaveSystemPlugin.Log?.LogInfo("TrySpawnPlayerControlled ok name=" +
                                (dto.private_uniqueName ?? "?") + " type=" + dto.type);
                            return spawnedPlayer;
                        }
                        NOSaveSystemPlugin.Log?.LogWarning(
                            "TrySpawnPlayerControlled returned false/null for name=" +
                            (dto.private_uniqueName ?? "?") + " type=" + dto.type +
                            " - falling back to non-pending spawn");
                    }
                    catch (Exception ex)
                    {
                        NOSaveSystemPlugin.Log?.LogWarning("TrySpawnPlayerControlled exception: " + ex);
                    }
                }
                else
                {
                    NOSaveSystemPlugin.Log?.LogWarning("SpawnAircraftFromDto: no local Player for player craft");
                }

                try
                {
                    try
                    {
                        if (UnitRegistry.customIDLookup != null &&
                            !string.IsNullOrEmpty(dto.private_uniqueName) &&
                            UnitRegistry.customIDLookup.ContainsKey(dto.private_uniqueName))
                        {
                            string prev = dto.private_uniqueName;
                            dto.private_uniqueName = null;
                            EnsureSpawnUniqueName(dto);
                            NOSaveSystemPlugin.Log?.LogInfo("Player fallback renamed " + prev + " -> " + dto.private_uniqueName);
                        }
                    }
                    catch { }

                    var savedImmediate = new SavedAircraft(dto.private_uniqueName ?? string.Empty)
                    {
                        type = dto.type,
                        faction = dto.faction,
                        globalPosition = saved.globalPosition,
                        rotation = saved.rotation,
                        playerControlled = false,
                        fuel = saved.fuel,
                        skill = saved.skill,
                        bravery = saved.bravery,
                        startingSpeed = saved.startingSpeed,
                        savedLoadout = saved.savedLoadout
                    };
                    spawner.SpawnSavedUnit(savedImmediate);
                    ac = FindAircraftByUniqueName(dto.private_uniqueName);
                    if (ac != null) return ac;
                }
                catch (Exception ex)
                {
                    NOSaveSystemPlugin.Log?.LogError("Player fallback SpawnSavedUnit exception: " + ex);
                }
            }

            NOSaveSystemPlugin.Log?.LogWarning(string.Format(CultureInfo.InvariantCulture,
                "SpawnAircraftFromDto: no unit after SpawnSavedUnit (type={0} name={1} player={2}). Spawner returned no registry entry.",
                dto.type, dto.private_uniqueName, wantPlayer));
            return null;
        }

        public static int RestoreMissionBlackboard(SnapshotMission mission, bool restoreEscalation, bool restoreMissionTimeMp, StringBuilder plan)
        {
            if (mission == null) return 0;
            int n = 0;
            try
            {
                var mm = MissionManager.i;
                if (mm == null)
                {
                    plan.AppendLine("  BLACKBOARD skip: MissionManager.i null");
                    return 0;
                }

                if (restoreEscalation)
                {
                    mm.NetworkcurrentEscalation = mission.currentEscalation;
                    plan.AppendLine("  ESCALATION -> " + mission.currentEscalation);
                    n++;
                }

                try
                {
                    mm.NetworkwrecksMaxNumber = mission.wrecksMaxNumber;
                    mm.NetworkwrecksDecayTime = mission.wrecksDecayTime;
                    mm.NetworktacticalThreshold = mission.tacticalThreshold;
                    mm.NetworkstrategicThreshold = mission.strategicThreshold;
                    plan.AppendLine("  BLACKBOARD wrecks/thresholds applied");
                    n++;
                }
                catch (Exception ex)
                {
                    plan.AppendLine("  BLACKBOARD thresholds FAIL: " + ex.Message);
                }

                if (restoreMissionTimeMp)
                    plan.AppendLine("  MISSIONTIME_SP skip (Time.timeSinceLevelLoad has no setter); saved=" + mission.missionTime);
            }
            catch (Exception ex)
            {
                plan.AppendLine("  BLACKBOARD FAIL: " + ex.Message);
            }
            return n;
        }

        public static bool ApplyLiveLoadout(Aircraft ac, List<string> mountKeys, StringBuilder plan)
        {
            if (ac == null) return false;
            if (mountKeys == null || mountKeys.Count == 0)
            {
                plan.AppendLine("  LOADOUT skip empty keys for " + (ac.UniqueName ?? ac.name));
                return false;
            }
            try
            {
                var wm = ac.weaponManager;
                if (wm == null)
                {
                    plan.AppendLine("  LOADOUT FAIL no WeaponManager on " + (ac.UniqueName ?? "?"));
                    return false;
                }

                try
                {
                    if (ac.networked && !ac.IsServer)
                    {
                        plan.AppendLine("  LOADOUT skip non-server networked " + (ac.UniqueName ?? "?"));
                        return false;
                    }
                }
                catch { }

                var saved = BuildSavedLoadout(mountKeys);
                try
                {
                    if (wm.hardpointSets != null && saved.Selected.Count != wm.hardpointSets.Length)
                    {
                        plan.AppendLine("  LOADOUT warn keys=" + saved.Selected.Count + " hardpoints=" + wm.hardpointSets.Length);
                    }
                }
                catch { }

                var loadout = saved.CreateLoadout(wm);
                if (loadout == null)
                {
                    plan.AppendLine("  LOADOUT FAIL CreateLoadout null");
                    return false;
                }

                ac.Networkloadout = loadout;
                wm.SpawnWeapons();
                plan.AppendLine("  LOADOUT applied keys=" + mountKeys.Count + " -> " + (ac.UniqueName ?? "?"));
                return true;
            }
            catch (Exception ex)
            {
                plan.AppendLine("  LOADOUT FAIL: " + ex.Message);
                return false;
            }
        }

        public static void CaptureShipControls(Ship ship, SavedShipDto dto)
        {
            if (ship == null || dto == null) return;
            try
            {
                var inputs = ship.GetInputs();
                if (inputs != null)
                {
                    dto.throttle = inputs.throttle;
                    dto.steering = inputs.steering;
                    dto.controlsCaptured = true;
                }
            }
            catch { }
        }

        public static void RestoreShipControls(Ship ship, SavedShipDto dto, StringBuilder plan)
        {
            if (ship == null || dto == null || !dto.controlsCaptured) return;
            try
            {
                var inputs = ship.GetInputs();
                if (inputs != null)
                {
                    inputs.throttle = dto.throttle;
                    inputs.steering = dto.steering;
                    plan?.AppendLine("  SHIPCONTROLS throttle=" + dto.throttle.ToString("0.###") +
                                     " steering=" + dto.steering.ToString("0.###") +
                                     " -> " + (ship.UniqueName ?? ship.name));
                }
            }
            catch (Exception ex)
            {
                plan?.AppendLine("  SHIPCONTROLS FAIL: " + ex.Message);
            }
        }

        public static bool IsProtectedStaticName(string uname)
        {
            if (string.IsNullOrEmpty(uname)) return false;
            return uname.StartsWith("<MAP_UNIT>", StringComparison.Ordinal)
                || uname.IndexOf("hangar", StringComparison.OrdinalIgnoreCase) >= 0
                || uname.IndexOf("shelter", StringComparison.OrdinalIgnoreCase) >= 0
                || uname.IndexOf("airbase", StringComparison.OrdinalIgnoreCase) >= 0
                || uname.IndexOf("runway", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsStaticUnitKind(string kind, Unit live = null)
        {
            if (live is Building || live is Scenery) return true;
            if (string.IsNullOrEmpty(kind)) return false;
            string k = kind;
            return k.IndexOf("Building", StringComparison.OrdinalIgnoreCase) >= 0
                || k.IndexOf("Scenery", StringComparison.OrdinalIgnoreCase) >= 0
                || k.IndexOf("Static", StringComparison.OrdinalIgnoreCase) >= 0
                || k.IndexOf("Factory", StringComparison.OrdinalIgnoreCase) >= 0
                || k.IndexOf("Depot", StringComparison.OrdinalIgnoreCase) >= 0
                || k.IndexOf("Pillbox", StringComparison.OrdinalIgnoreCase) >= 0
                || k.IndexOf("Gabion", StringComparison.OrdinalIgnoreCase) >= 0
                || k.IndexOf("Emplacement", StringComparison.OrdinalIgnoreCase) >= 0
                || k.IndexOf("Structure", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsSwapSafeMobileOther(string kind, Unit live = null)
        {
            if (IsStaticUnitKind(kind, live)) return false;
            if (live is PilotDismounted || live is Missile) return true;
            if (string.IsNullOrEmpty(kind)) return false;
            return kind.IndexOf("Pilot", StringComparison.OrdinalIgnoreCase) >= 0
                || kind.IndexOf("Missile", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool DespawnUnitForSwap(Unit unit, Player localPlayer, StringBuilder plan)
        {
            if (unit == null) return false;
            try { if (unit.Equals(null)) return false; } catch { return false; }

            string label = "?";
            try { label = unit.UniqueName ?? unit.name ?? unit.GetType().Name; } catch { try { label = unit.GetType().Name; } catch { } }

            try
            {
                if (IsStaticUnitKind(unit.GetType().Name, unit) || IsProtectedStaticName(label))
                {
                    plan?.AppendLine("    DESPAWN REFUSED static/protected " + label + " kind=" + unit.GetType().Name);
                    NOSaveSystemPlugin.Log?.LogInfo("DespawnUnitForSwap: refused static/protected " + label);
                    return false;
                }
            }
            catch { }

            try
            {
                if (unit is Aircraft ac && localPlayer != null)
                {
                    try
                    {
                        if (ReferenceEquals(localPlayer.Aircraft, ac))
                        {
                            localPlayer.RemoveAircraft(ac);
                            plan?.AppendLine("    UNBIND RemoveAircraft " + label);
                        }
                    }
                    catch (Exception ex)
                    {
                        plan?.AppendLine("    UNBIND FAIL: " + ex.Message);
                    }
                }

                GameObject go = null;
                try { go = unit.gameObject; } catch { }

                try
                {
                    unit.DisableUnit();
                    plan?.AppendLine("    DESPAWN DisableUnit " + label);
                }
                catch (Exception ex)
                {
                    plan?.AppendLine("    DESPAWN DisableUnit FAIL: " + ex.Message);
                    NOSaveSystemPlugin.Log?.LogWarning("Despawn DisableUnit: " + ex.Message);
                }

                try { if (go != null) go.SetActive(false); } catch { }

                string uname = null;
                try { uname = unit.UniqueName; } catch { }
                if (!string.IsNullOrEmpty(uname) && UnitRegistry.customIDLookup != null)
                {
                    try
                    {
                        if (UnitRegistry.customIDLookup.TryGetValue(uname, out var reg) && (reg == null || ReferenceEquals(reg, unit)))
                            UnitRegistry.customIDLookup.Remove(uname);
                    }
                    catch { }
                }

                try { UnitRegistry.UnregisterUnit(unit); } catch { }

                bool somOk = false;
                try
                {
                    var nm = NetworkManagerNuclearOption.i;
                    if (nm != null && nm.ServerObjectManager != null)
                    {
                        NetworkIdentity identity = null;
                        try { identity = unit.Identity; } catch { }
                        if (identity != null)
                        {
                            nm.ServerObjectManager.Destroy(identity, false);
                            somOk = true;
                            plan?.AppendLine("    DESPAWN ServerObjectManager.Destroy(Identity) " + label);
                        }
                        else if (go != null)
                        {
                            nm.ServerObjectManager.Destroy(go, false);
                            somOk = true;
                            plan?.AppendLine("    DESPAWN ServerObjectManager.Destroy(GO) " + label);
                        }
                    }
                }
                catch (Exception ex)
                {
                    plan?.AppendLine("    DESPAWN SOM FAIL: " + ex.Message);
                }

                try
                {
                    if (go != null)
                    {
                        UnityEngine.Object.Destroy(go);
                        plan?.AppendLine("    DESPAWN Object.Destroy " + label + (somOk ? " (after SOM)" : ""));
                    }
                }
                catch (Exception ex)
                {
                    plan?.AppendLine("    DESPAWN Object.Destroy FAIL: " + ex.Message);
                }

                return true;
            }
            catch (Exception ex)
            {
                plan?.AppendLine("    DESPAWN FAIL " + label + ": " + ex.Message);
                NOSaveSystemPlugin.Log?.LogWarning("DespawnUnitForSwap: " + ex.Message);
                return false;
            }
        }

        public static bool IsUnitObjectPresent(Unit unit)
        {
            if (unit == null) return false;
            try { if (unit.Equals(null)) return false; } catch { return false; }
            try
            {
                var go = unit.gameObject;
                if (go == null || go.Equals(null)) return false;
            }
            catch { return false; }
            return true;
        }

        public static bool IsLiveActiveUnit(Unit unit)
        {
            if (!IsUnitObjectPresent(unit)) return false;
            try { if (unit.disabled) return false; } catch { }
            try
            {
                var go = unit.gameObject;
                if (go == null || !go.activeInHierarchy) return false;
            }
            catch { return false; }
            return true;
        }

        public static bool VerifyDespawnOrForceDestroy(Unit unit, string label, StringBuilder plan)
        {
            if (string.IsNullOrEmpty(label)) label = "?";
            if (!IsUnitObjectPresent(unit))
            {
                plan?.AppendLine("    DESPAWN VERIFY ok " + label);
                return true;
            }

            plan?.AppendLine("    DESPAWN STILL ALIVE " + label + " - forcing DestroyImmediate");
            NOSaveSystemPlugin.Log?.LogWarning("DESPAWN STILL ALIVE " + label);

            try
            {
                try
                {
                    var nm = NetworkManagerNuclearOption.i;
                    if (nm != null && nm.ServerObjectManager != null)
                    {
                        NetworkIdentity identity = null;
                        try { identity = unit.Identity; } catch { }
                        if (identity != null)
                            nm.ServerObjectManager.Destroy(identity, false);
                        else if (unit.gameObject != null)
                            nm.ServerObjectManager.Destroy(unit.gameObject, false);
                    }
                }
                catch { }

                try { unit.DisableUnit(); } catch { }
                try
                {
                    string uname = null;
                    try { uname = unit.UniqueName; } catch { }
                    if (!string.IsNullOrEmpty(uname) && UnitRegistry.customIDLookup != null &&
                        UnitRegistry.customIDLookup.TryGetValue(uname, out var regU) && (regU == null || ReferenceEquals(regU, unit)))
                        UnitRegistry.customIDLookup.Remove(uname);
                }
                catch { }
                try { UnitRegistry.UnregisterUnit(unit); } catch { }

                GameObject go = null;
                try { go = unit.gameObject; } catch { }
                if (go != null)
                {
                    try { go.SetActive(false); } catch { }
                    try { UnityEngine.Object.DestroyImmediate(go); }
                    catch
                    {
                        try { UnityEngine.Object.Destroy(go); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                plan?.AppendLine("    DESPAWN FORCE FAIL " + label + ": " + ex.Message);
            }

            if (!IsUnitObjectPresent(unit))
            {
                plan?.AppendLine("    DESPAWN VERIFY ok (after force) " + label);
                return true;
            }

            plan?.AppendLine("    DESPAWN STILL ALIVE after force " + label);
            NOSaveSystemPlugin.Log?.LogError("DESPAWN STILL ALIVE after force " + label);
            return false;
        }

        public static int ClearSavedUniqueNameReservations(MidMissionSnapshot snap, StringBuilder plan)
        {
            int n = 0;
            if (snap == null) return 0;
            try
            {
                void ClearOne(string uname)
                {
                    if (string.IsNullOrEmpty(uname)) return;
                    try
                    {
                        if (UnitRegistry.customIDLookup != null && UnitRegistry.customIDLookup.ContainsKey(uname))
                        {
                            ForceClearUniqueNameReservation(uname, plan);
                            n++;
                        }
                    }
                    catch { }
                }
                if (snap.aircraft != null) foreach (var d in snap.aircraft) if (d != null) ClearOne(d.private_uniqueName);
                if (snap.vehicles != null) foreach (var d in snap.vehicles) if (d != null) ClearOne(d.private_uniqueName);
                if (snap.ships != null) foreach (var d in snap.ships) if (d != null) ClearOne(d.private_uniqueName);
                if (snap.otherUnits != null) foreach (var d in snap.otherUnits) if (d != null && !IsProtectedStaticName(d.private_uniqueName) && IsSwapSafeMobileOther(d.kind)) ClearOne(d.private_uniqueName);
                if (n > 0)
                {
                    plan?.AppendLine("  MR clear saved UniqueName reservations count=" + n);
                    NOSaveSystemPlugin.Log?.LogInfo("MissionReload: cleared " + n + " saved UniqueName reservations");
                }
            }
            catch (Exception ex)
            {
                plan?.AppendLine("  MR clear UniqueName reservations FAIL: " + ex.Message);
            }
            return n;
        }

        public static void ForceClearUniqueNameReservation(string uniqueName, StringBuilder plan)
        {
            if (string.IsNullOrEmpty(uniqueName) || UnitRegistry.customIDLookup == null) return;
            try
            {
                if (!UnitRegistry.customIDLookup.ContainsKey(uniqueName)) return;
                Unit u = null;
                try { UnitRegistry.customIDLookup.TryGetValue(uniqueName, out u); } catch { }
                bool present = IsUnitObjectPresent(u);
                UnitRegistry.customIDLookup.Remove(uniqueName);
                plan?.AppendLine("    FORCE-CLEAR UniqueName '" + uniqueName + "'" +
                    (present ? " (was still bound to present unit)" : " (stale/ghost entry)"));
                NOSaveSystemPlugin.Log?.LogInfo("ForceClear UniqueName " + uniqueName);
            }
            catch (Exception ex)
            {
                plan?.AppendLine("    FORCE-CLEAR UniqueName FAIL: " + ex.Message);
            }
        }

        public static bool TrySpawnOtherUnit(Spawner spawner, SavedUnitDto dto, StringBuilder plan)
        {
            if (spawner == null || dto == null) return false;
            if (!ValidateTypeExists(dto.type))
            {
                plan?.AppendLine("    SPAWN other skip: type not in Encyclopedia: " + dto.type);
                return false;
            }

            EnsureSpawnUniqueName(dto);

            if (!string.IsNullOrEmpty(dto.private_uniqueName) &&
                UnitRegistry.customIDLookup != null &&
                UnitRegistry.customIDLookup.ContainsKey(dto.private_uniqueName))
            {
                ForceClearUniqueNameReservation(dto.private_uniqueName, plan);
            }

            try
            {
                string kind = dto.kind ?? "";
                GlobalPosition gp = dto.globalPosition != null
                    ? new GlobalPosition(dto.globalPosition.x, dto.globalPosition.y, dto.globalPosition.z)
                    : new GlobalPosition(0, 0, 0);
                Quaternion rot = dto.rotation != null
                    ? new Quaternion(dto.rotation.x, dto.rotation.y, dto.rotation.z, dto.rotation.w)
                    : Quaternion.identity;
                string uname = dto.private_uniqueName ?? string.Empty;

                SavedUnit saved = null;
                if (kind.IndexOf("Building", StringComparison.OrdinalIgnoreCase) >= 0)
                    saved = new SavedBuilding(uname) { type = dto.type, faction = dto.faction, globalPosition = gp, rotation = rot };
                else if (kind.IndexOf("Scenery", StringComparison.OrdinalIgnoreCase) >= 0)
                    saved = new SavedScenery(uname) { type = dto.type, faction = dto.faction, globalPosition = gp, rotation = rot };
                else if (kind.IndexOf("Pilot", StringComparison.OrdinalIgnoreCase) >= 0)
                    saved = new SavedPilot(uname) { type = dto.type, faction = dto.faction, globalPosition = gp, rotation = rot };
                else if (kind.IndexOf("Container", StringComparison.OrdinalIgnoreCase) >= 0)
                    saved = new SavedContainer(uname) { type = dto.type, faction = dto.faction, globalPosition = gp, rotation = rot };
                else if (kind.IndexOf("Missile", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    float speed = 0f;
                    if (dto.velocityCaptured && dto.velocity != null)
                    {
                        var v = new Vector3(dto.velocity.x, dto.velocity.y, dto.velocity.z);
                        speed = v.magnitude;
                    }
                    if (NoSaveV096.TrySpawnMissile(spawner, dto, gp, rot, plan)) return true;
                    saved = new SavedMissile(uname) { type = dto.type, faction = dto.faction, globalPosition = gp, rotation = rot, startingSpeed = speed };
                }
                else
                {
                    plan?.AppendLine("    SPAWN other warn: unknown kind=" + kind + " trying SavedScenery");
                    saved = new SavedScenery(uname) { type = dto.type, faction = dto.faction, globalPosition = gp, rotation = rot };
                }

                if (saved == null) return false;
                spawner.SpawnSavedUnit(saved);
                plan?.AppendLine("    SPAWNED other kind=" + kind + " " + (dto.private_uniqueName ?? dto.type));
                return true;
            }
            catch (Exception ex)
            {
                plan?.AppendLine("    SPAWN other FAIL: " + ex.Message);
                NOSaveSystemPlugin.Log?.LogWarning("TrySpawnOtherUnit: " + ex.Message);
                return false;
            }
        }
    }

    [Serializable]
    public class FactionEconomyDto
    {
        public string factionName;
        public float factionFunds;
        public float factionScore;
    }

    [Serializable]
    public class ObjectiveDto
    {
        public string uniqueName;
        public string displayName;
        public string faction;
        public string status;
        public float completePercent;
        public string objectiveType;
        public bool hidden;
        public int outcomesCount;
    }

    [Serializable]
    public class PartHpDto
    {
        public byte id;
        public float hitPoints;
        public bool detached;
        public bool critical;
        public string name;
    }
}
