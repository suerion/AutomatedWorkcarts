using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Rust;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static TrainEngine;
using static TrainTrackSpline;

namespace Oxide.Plugins
{
    [Info("Automated Workcarts", "WhiteThunder", "0.14.2")]
    [Description("Spawns conductor NPCs that drive workcarts between stations.")]
    internal class AutomatedWorkcarts : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private Plugin CargoTrainEvent;

        private static AutomatedWorkcarts _pluginInstance;
        private static Configuration _pluginConfig;
        private static StoredPluginData _pluginData;
        private static StoredMapData _mapData;
        private static StoredTunnelData _tunnelData;

        private const string PermissionToggle = "automatedworkcarts.toggle";
        private const string PermissionManageTriggers = "automatedworkcarts.managetriggers";
        private const string PermissionViewMarkers = "automatedworkcarts.viewmarkers";

        private const string PlayerPrefab = "assets/prefabs/player/player.prefab";
        private const string DeliveryDroneMarkerPrefab = "assets/prefabs/misc/marketplace/deliverydronemarker.prefab";

        private WorkcartTriggerManager _triggerManager = new WorkcartTriggerManager();
        private AutomatedWorkcartManager _workcartManager = new AutomatedWorkcartManager();

        #endregion

        #region Hooks

        private void Init()
        {
            _pluginInstance = this;
            _pluginData = StoredPluginData.Load();
            _tunnelData = StoredTunnelData.Load();

            permission.RegisterPermission(PermissionToggle, this);
            permission.RegisterPermission(PermissionManageTriggers, this);
            permission.RegisterPermission(PermissionViewMarkers, this);
        }

        private void Unload()
        {
            _pluginData.Save();
            _triggerManager.DestroyAll();
            TrainController.DestroyAll();

            _mapData = null;
            _pluginData = null;
            _tunnelData = null;
            _pluginConfig = null;
            _pluginInstance = null;
        }

        private void OnServerInitialized()
        {
            _mapData = StoredMapData.Load();
            _triggerManager.CreateAll();

            var foundWorkcarts = new List<uint>();

            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var workcart = entity as TrainEngine;
                if (workcart == null)
                    continue;

                if (_pluginData.HasWorkcartId(workcart.net.ID))
                {
                    foundWorkcarts.Add(workcart.net.ID);
                    timer.Once(UnityEngine.Random.Range(0, 1f), () =>
                    {
                        if (workcart != null && !IsWorkcartOwned(workcart))
                            TryAddTrainController(workcart);
                    });
                }
            }

            _pluginData.ReplaceWorkcartIds(foundWorkcarts);
        }

        private void OnServerSave()
        {
            _pluginData.Save();
        }

        private void OnNewSave()
        {
            _pluginData = StoredPluginData.Clear();
        }

        private void OnEntityKill(TrainEngine workcart)
        {
            if (workcart == null || workcart.net == null)
                return;

            _workcartManager.Unregister(workcart);
        }

        private bool? OnEntityTakeDamage(TrainEngine workcart)
        {
            if (workcart.GetComponent<TrainController>() != null)
            {
                // Return true (standard) to cancel default behavior (prevent damage).
                return true;
            }

            return null;
        }

        private bool? OnEntityTakeDamage(BasePlayer player)
        {
            var workcart = player.GetMountedVehicle() as TrainEngine;
            if (workcart == null)
                return null;

            var trainController = workcart.GetComponent<TrainController>();
            if (trainController == null)
                return null;

            if (player == trainController.Conductor)
            {
                // Return true (standard) to cancel default behavior (prevent damage).
                return true;
            }

            return null;
        }

        private bool? CanLootEntity(BasePlayer player, StorageContainer container)
        {
            if (container.ShortPrefabName == "workcart_fuel_storage"
                && container.GetComponentInParent<TrainController>() != null)
                return false;

            return null;
        }

        private void OnEntityEnter(WorkcartTrigger trigger, TrainEngine workcart)
        {
            var trainController = workcart.GetComponent<TrainController>();
            if (trainController == null)
            {
                if (trigger.TriggerInfo.AddConductor
                    && !IsWorkcartOwned(workcart)
                    && CanHaveMoreConductors())
                {
                    TryAddTrainController(workcart, trigger.TriggerInfo);
                    _pluginData.AddWorkcartId(workcart.net.ID);
                }

                return;
            }

            if (trigger.entityContents?.Contains(workcart) ?? false)
                return;

            trainController.HandleWorkcartTrigger(trigger.TriggerInfo);
        }

        private void OnEntityEnter(TriggerTrainCollisions trigger, TrainEngine otherWorkcart)
        {
            if (trigger.entityContents?.Contains(otherWorkcart) ?? false)
                return;

            var workcart = trigger.GetComponentInParent<TrainEngine>();
            if (workcart == null)
                return;

            var dot = Vector3.Dot(GetWorkcartForward(workcart), GetWorkcartForward(otherWorkcart));
            if (dot >= 0.01f)
            {
                // Going same direction.
                TrainEngine forwardWorkcart, backwardWorkcart;
                DetermineWorkcartOrientations(workcart, otherWorkcart, out forwardWorkcart, out backwardWorkcart);

                var forwardController = forwardWorkcart.GetComponent<TrainController>();
                var backController = backwardWorkcart.GetComponent<TrainController>();

                // Do nothing if neither workcart is automated.
                if (forwardController == null && backController == null)
                    return;

                if (forwardController != null)
                {
                    forwardController.DepartEarlyIfStoppedOrStopping();
                }
                else if (Math.Abs(EngineSpeedToNumber(forwardWorkcart.CurThrottleSetting)) < Math.Abs(EngineSpeedToNumber(backwardWorkcart.CurThrottleSetting)))
                {
                    // Destroy the forward workcart if it's not automated and going too slow.
                    _pluginInstance.LogWarning($"Destroying non-automated workcart due to insufficient speed.");
                    ScheduleDestroyWorkcart(forwardWorkcart);
                    return;
                }

                if (backController != null)
                    backController.StartChilling();
            }
            else
            {
                // Going opposite directions or perpendicular.
                var controller = workcart.GetComponent<TrainController>();
                var otherController = otherWorkcart.GetComponent<TrainController>();

                // Do nothing if neither workcart is automated.
                if (controller == null && otherController == null)
                    return;

                if (GetForwardWorkcart(workcart, otherWorkcart) == workcart)
                {
                    // Not a head on collision. One is braking while hitting the other.
                    if (controller != null)
                        controller.DepartEarlyIfStoppedOrStopping();

                    return;
                }

                // If one of the workcarts is not automated, destroy that one.
                if (controller == null)
                {
                    if (_pluginConfig.BulldozeOffendingWorkcarts)
                    {
                        _pluginInstance.LogWarning($"Destroying non-automated workcart due to head-on collision with an automated workcart.");
                        ScheduleDestroyWorkcart(workcart);
                    }
                    else
                        otherController.StartChilling();

                    return;
                }

                if (otherController == null)
                {
                    if (_pluginConfig.BulldozeOffendingWorkcarts)
                    {
                        _pluginInstance.LogWarning($"Destroying non-automated workcart due to head-on collision with an automated workcart.");
                        ScheduleDestroyWorkcart(otherWorkcart);
                    }
                    else
                        controller.StartChilling();

                    return;
                }

                // Don't destroy both, since the collison event can happen for both workcarts in the same frame.
                if (controller.IsDestroying)
                    return;

                // If both are automated, destroy the slower one (if same speed, will be random).
                _pluginInstance.LogWarning($"Destroying automated workcart due to head-on collision with another.");
                if (GetFasterWorkcart(workcart, otherWorkcart) == workcart)
                    otherController.ScheduleDestruction();
                else
                    controller.ScheduleDestruction();
            }
        }

        #endregion

        #region Commands

        [Command("aw.showmarkers")]
        private void CommandShowMarkers(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer
                || !VerifyPermission(player, PermissionViewMarkers))
                return;

            if (_workcartManager.NumWorkcarts == 0)
            {
                ReplyToPlayer(player, Lang.ErrorNoAutomatedWorkcarts);
                return;
            }

            float duration;
            if (args.Length == 0 || !float.TryParse(args[0], out duration))
                duration = 60;

            var basePlayer = player.Object as BasePlayer;
            _workcartManager.ShowMarkersToPlayer(basePlayer.userID, duration);
            ReplyToPlayer(player, Lang.ShowMarkersSuccess, _workcartManager.NumWorkcarts, FormatTime(duration));
        }

        [Command("aw.toggle")]
        private void CommandAutomateWorkcart(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer
                || !VerifyPermission(player, PermissionToggle))
                return;

            var basePlayer = player.Object as BasePlayer;

            var workcart = GetWorkcartWhereAiming(basePlayer);
            if (workcart == null)
            {
                ReplyToPlayer(player, Lang.ErrorNoWorkcartFound);
                return;
            }

            var trainController = workcart.GetComponent<TrainController>();
            if (trainController == null)
            {
                if (IsWorkcartOwned(workcart))
                {
                    ReplyToPlayer(player, Lang.ErrorWorkcartOwned);
                    return;
                }

                if (!CanHaveMoreConductors())
                {
                    ReplyToPlayer(player, Lang.ErrorMaxConductors, _workcartManager.NumWorkcarts, _pluginConfig.MaxConductors);
                    return;
                }

                if (TryAddTrainController(workcart))
                {
                    _pluginData.AddWorkcartId(workcart.net.ID);
                    player.Reply(GetMessage(player, Lang.ToggleOnSuccess, _workcartManager.NumWorkcarts) + " " + GetConductorCountMessage(player));
                }
                else
                    ReplyToPlayer(player, Lang.ErrorAutomateBlocked);
            }
            else
            {
                UnityEngine.Object.Destroy(trainController);
                _workcartManager.Unregister(workcart);
                player.Reply(GetMessage(player, Lang.ToggleOffSuccess) + " " + GetConductorCountMessage(player));
            }
        }

        [Command("aw.addtrigger", "awt.add")]
        private void CommandAddTrigger(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer
                || !VerifyPermission(player, PermissionManageTriggers))
                return;

            if (!_pluginConfig.EnableMapTriggers)
            {
                ReplyToPlayer(player, Lang.ErrorMapTriggersDisabled);
                return;
            }

            Vector3 trackPosition;
            if (!TryGetTrackPosition(player.Object as BasePlayer, out trackPosition))
            {
                ReplyToPlayer(player, Lang.ErrorNoTrackFound);
                return;
            }

            var triggerInfo = new WorkcartTriggerInfo() { Position = trackPosition };

            AddTriggerShared(player, cmd, args, triggerInfo);
        }

        [Command("aw.addtunneltrigger", "awt.addt")]
        private void CommandAddTunnelTrigger(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer
                || !VerifyPermission(player, PermissionManageTriggers))
                return;

            Vector3 trackPosition;
            if (!TryGetTrackPosition(player.Object as BasePlayer, out trackPosition))
            {
                ReplyToPlayer(player, Lang.ErrorNoTrackFound);
                return;
            }

            DungeonCellWrapper dungeonCellWrapper;
            if (!VerifySupportedNearbyTrainTunnel(player, trackPosition, out dungeonCellWrapper))
                return;

            if (!_pluginConfig.IsTunnelTypeEnabled(dungeonCellWrapper.TunnelType))
            {
                ReplyToPlayer(player, Lang.ErrorTunnelTypeDisabled, dungeonCellWrapper.TunnelType);
                return;
            }

            var triggerInfo = new WorkcartTriggerInfo()
            {
                TunnelType = dungeonCellWrapper.TunnelType.ToString(),
                Position = dungeonCellWrapper.InverseTransformPoint(trackPosition),
            };

            AddTriggerShared(player, cmd, args, triggerInfo);
        }

        private void AddTriggerShared(IPlayer player, string cmd, string[] args, WorkcartTriggerInfo triggerInfo)
        {
            if (args.Length == 0)
            {
                triggerInfo.Speed = EngineSpeeds.Zero.ToString();
            }
            else
            {
                foreach (var arg in args)
                {
                    if (!VerifyValidArgAndModifyTrigger(player, cmd, arg, triggerInfo, Lang.AddTriggerSyntax))
                        return;
                }
            }

            _triggerManager.AddTrigger(triggerInfo);
            _triggerManager.ShowAllRepeatedly(player.Object as BasePlayer);
            ReplyToPlayer(player, Lang.AddTriggerSuccess, GetTriggerPrefix(player, triggerInfo), triggerInfo.Id);
        }

        [Command("aw.updatetrigger", "awt.update")]
        private void CommandUpdateTrigger(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer
                || !VerifyPermission(player, PermissionManageTriggers)
                || !VerifyAnyTriggers(player))
                return;

            var basePlayer = player.Object as BasePlayer;
            WorkcartTriggerInfo triggerInfo;
            string[] optionArgs;

            if (!VerifyValidTrigger(player, cmd, args, Lang.UpdateTriggerSyntax, out triggerInfo, out optionArgs))
                return;

            if (optionArgs.Length == 0)
            {
                ReplyToPlayer(player, Lang.UpdateTriggerSyntax, cmd, GetTriggerOptions(player));
                return;
            }

            foreach (var arg in optionArgs)
            {
                if (!VerifyValidArgAndModifyTrigger(player, cmd, arg, triggerInfo, Lang.UpdateTriggerSyntax))
                    return;
            }

            _triggerManager.UpdateTrigger(triggerInfo);
            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.UpdateTriggerSuccess, GetTriggerPrefix(player, triggerInfo), triggerInfo.Id);
        }

        [Command("aw.replacetrigger", "awt.replace")]
        private void CommandReplaceTrigger(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer
                || !VerifyPermission(player, PermissionManageTriggers)
                || !VerifyAnyTriggers(player))
                return;

            var basePlayer = player.Object as BasePlayer;
            WorkcartTriggerInfo triggerInfo;
            string[] optionArgs;

            if (!VerifyValidTrigger(player, cmd, args, Lang.UpdateTriggerSyntax, out triggerInfo, out optionArgs))
                return;

            if (optionArgs.Length == 0)
            {
                ReplyToPlayer(player, Lang.UpdateTriggerSyntax, cmd, GetTriggerOptions(player));
                return;
            }

            var newTriggerInfo = new WorkcartTriggerInfo();
            foreach (var arg in optionArgs)
            {
                if (!VerifyValidArgAndModifyTrigger(player, cmd, arg, newTriggerInfo, Lang.UpdateTriggerSyntax))
                    return;
            }

            triggerInfo.CopyFrom(newTriggerInfo);
            _triggerManager.UpdateTrigger(triggerInfo);
            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.UpdateTriggerSuccess, GetTriggerPrefix(player, triggerInfo), triggerInfo.Id);
        }

        [Command("aw.movetrigger", "awt.move")]
        private void CommandMoveTrigger(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer
                || !VerifyPermission(player, PermissionManageTriggers)
                || !VerifyAnyTriggers(player))
                return;

            var basePlayer = player.Object as BasePlayer;
            WorkcartTriggerInfo triggerInfo;

            string[] optionArgs;
            if (!VerifyValidTrigger(player, cmd, args, Lang.RemoveTriggerSyntax, out triggerInfo, out optionArgs))
                return;

            Vector3 trackPosition;
            if (!VerifyTrackPosition(player, out trackPosition))
                return;

            if (triggerInfo.TriggerType == WorkcartTriggerType.Tunnel)
            {
                DungeonCellWrapper dungeonCellWrapper;
                if (!VerifySupportedNearbyTrainTunnel(player, trackPosition, out dungeonCellWrapper))
                    return;

                if (dungeonCellWrapper.TunnelType != triggerInfo.GetTunnelType())
                {
                    ReplyToPlayer(player, Lang.ErrorUnsupportedTunnel);
                    return;
                }

                trackPosition = dungeonCellWrapper.InverseTransformPoint(trackPosition);
            }

            _triggerManager.MoveTrigger(triggerInfo, trackPosition);
            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.MoveTriggerSuccess, GetTriggerPrefix(player, triggerInfo), triggerInfo.Id);
        }

        [Command("aw.removetrigger", "awt.remove")]
        private void CommandRemoveTrigger(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer
                || !VerifyPermission(player, PermissionManageTriggers)
                || !VerifyAnyTriggers(player))
                return;

            var basePlayer = player.Object as BasePlayer;
            WorkcartTriggerInfo triggerInfo;
            string[] optionArgs;

            if (!VerifyValidTrigger(player, cmd, args, Lang.RemoveTriggerSyntax, out triggerInfo, out optionArgs))
                return;

            _triggerManager.RemoveTrigger(triggerInfo);
            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.RemoveTriggerSuccess, GetTriggerPrefix(player, triggerInfo), triggerInfo.Id);
        }

        [Command("aw.showtriggers")]
        private void CommandShowStops(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer
                || !VerifyPermission(player, PermissionManageTriggers)
                || !VerifyAnyTriggers(player))
                return;

            _triggerManager.ShowAllRepeatedly(player.Object as BasePlayer);
        }

        #endregion

        #region Helper Methods - Command Checks

        private bool VerifyPermission(IPlayer player, string permissionName)
        {
            if (player.HasPermission(permissionName))
                return true;

            ReplyToPlayer(player, Lang.ErrorNoPermission);
            return false;
        }

        private bool VerifyAnyTriggers(IPlayer player)
        {
            if (_mapData.MapTriggers.Count > 0
                || _tunnelData.TunnelTriggers.Count > 0)
                return true;

            ReplyToPlayer(player, Lang.ErrorNoTriggers);
            return false;
        }

        private bool VerifyTriggerExists(IPlayer player, int triggerId, WorkcartTriggerType triggerType, out WorkcartTriggerInfo triggerInfo)
        {
            triggerInfo = _triggerManager.FindTrigger(triggerId, triggerType);
            if (triggerInfo != null)
                return true;

            _triggerManager.ShowAllRepeatedly(player.Object as BasePlayer);
            ReplyToPlayer(player, Lang.ErrorTriggerNotFound, GetTriggerPrefix(player, triggerType), triggerId);
            return false;
        }

        private bool VerifyTrackPosition(IPlayer player, out Vector3 trackPosition)
        {
            if (TryGetTrackPosition(player.Object as BasePlayer, out trackPosition))
                return true;

            ReplyToPlayer(player, Lang.ErrorNoTrackFound);
            return false;
        }

        private bool IsTriggerArg(IPlayer player, string arg, out int triggerId, out WorkcartTriggerType triggerType)
        {
            triggerType = WorkcartTriggerType.Map;
            triggerId = 0;

            if (arg.Length <= 1)
                return false;

            var triggerPrefix = arg.Substring(0, 1).ToLower();
            var triggerIdString = arg.Substring(1).ToLower();

            if (!int.TryParse(triggerIdString, out triggerId))
                return false;

            if (triggerPrefix == GetTriggerPrefix(player, WorkcartTriggerType.Tunnel).ToLower())
            {
                triggerType = WorkcartTriggerType.Tunnel;
                return true;
            }
            else if (triggerPrefix == GetTriggerPrefix(player, WorkcartTriggerType.Map).ToLower())
            {
                triggerType = WorkcartTriggerType.Map;
                return true;
            }

            return false;
        }

        private bool VerifyValidTrigger(IPlayer player, string cmd, string[] args, string errorMessageName, out WorkcartTriggerInfo triggerInfo, out string[] optionArgs)
        {
            var basePlayer = player.Object as BasePlayer;
            optionArgs = args;
            triggerInfo = null;

            int triggerId;
            WorkcartTriggerType triggerType;
            if (args.Length > 0 && IsTriggerArg(player, args[0], out triggerId, out triggerType))
            {
                optionArgs = args.Skip(1).ToArray();
                return VerifyTriggerExists(player, triggerId, triggerType, out triggerInfo);
            }

            triggerInfo = _triggerManager.FindNearestTriggerWhereAiming(basePlayer);
            if (triggerInfo != null)
                return true;

            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, errorMessageName, cmd, GetTriggerOptions(player));
            return false;
        }

        private bool VerifySupportedNearbyTrainTunnel(IPlayer player, Vector3 trackPosition, out DungeonCellWrapper dungeonCellWrapper)
        {
            dungeonCellWrapper = FindNearestDungeonCell(trackPosition);
            if (dungeonCellWrapper == null || dungeonCellWrapper.TunnelType == TunnelType.Unsupported)
            {
                ReplyToPlayer(player, Lang.ErrorUnsupportedTunnel);
                return false;
            }

            return true;
        }

        private bool VerifyValidArgAndModifyTrigger(IPlayer player, string cmd, string arg, WorkcartTriggerInfo triggerInfo, string errorMessageName)
        {
            var argLower = arg.ToLower();
            if (argLower == "start" || argLower == "conductor")
            {
                triggerInfo.AddConductor = true;
                return true;
            }

            if (argLower == "brake")
            {
                triggerInfo.Brake = true;
                return true;
            }

            float stopDuration;
            if (float.TryParse(arg, out stopDuration))
            {
                triggerInfo.StopDuration = stopDuration;
                return true;
            }

            WorkcartSpeed speed;
            if (Enum.TryParse<WorkcartSpeed>(arg, true, out speed))
            {
                var speedString = speed.ToString();

                // If zero speed is already set, assume this is the departure speed.
                if (triggerInfo.Speed == WorkcartSpeed.Zero.ToString())
                    triggerInfo.DepartureSpeed = speedString;
                else
                    triggerInfo.Speed = speedString;

                return true;
            }

            WorkcartDirection direction;
            if (Enum.TryParse<WorkcartDirection>(arg, true, out direction))
            {
                triggerInfo.Direction = direction.ToString();
                return true;
            }

            WorkcartTrackSelection trackSelection;
            if (Enum.TryParse<WorkcartTrackSelection>(arg, true, out trackSelection))
            {
                triggerInfo.TrackSelection = trackSelection.ToString();
                return true;
            }

            ReplyToPlayer(player, errorMessageName, cmd, GetTriggerOptions(player));
            return false;
        }

        #endregion

        #region Helper Methods

        private static bool AutomationWasBlocked(TrainEngine workcart)
        {
            object hookResult = Interface.CallHook("OnWorkcartAutomate", workcart);
            if (hookResult is bool && (bool)hookResult == false)
                return true;

            if (_pluginInstance.CargoTrainEvent?.Call("IsTrainSpecial", workcart.net.ID)?.Equals(true) ?? false)
                return true;

            return false;
        }

        private static bool CanHaveMoreConductors() =>
            _pluginConfig.MaxConductors < 0
            || _pluginInstance._workcartManager.NumWorkcarts < _pluginConfig.MaxConductors;

        private static bool IsWorkcartOwned(TrainEngine workcart) => workcart.OwnerID != 0;

        private static bool TryAddTrainController(TrainEngine workcart, WorkcartTriggerInfo triggerInfo = null)
        {
            if (AutomationWasBlocked(workcart))
                return false;

            var trainController = workcart.gameObject.AddComponent<TrainController>();
            if (triggerInfo != null)
                trainController.StartImmediately(triggerInfo);

            workcart.SetHealth(workcart.MaxHealth());
            _pluginInstance._workcartManager.Register(workcart);
            Interface.CallHook("OnWorkcartAutomated", workcart);

            return true;
        }

        private static string GetShortName(string prefabName)
        {
            var slashIndex = prefabName.LastIndexOf("/");
            var baseName = (slashIndex == -1) ? prefabName : prefabName.Substring(slashIndex + 1);
            return baseName.Replace(".prefab", "");
        }

        private static bool TryParseEngineSpeed(string speedName, out EngineSpeeds engineSpeed)
        {
            if (Enum.TryParse<EngineSpeeds>(speedName, true, out engineSpeed))
                return true;

            engineSpeed = EngineSpeeds.Zero;
            _pluginInstance.LogError($"Unrecognized engine speed: {speedName}");
            return false;
        }

        private static bool TryParseTrackSelection(string selectionName, out TrackSelection trackSelection)
        {
            if (Enum.TryParse<TrackSelection>(selectionName, true, out trackSelection))
                return true;

            _pluginInstance.LogError($"Unrecognized track selection: {selectionName}");
            trackSelection = TrackSelection.Default;
            return false;
        }

        private static string GetEnumOptions<T>()
        {
            var names = Enum.GetNames(typeof(T));

            for (var i = 0; i < names.Length; i++)
                names[i] = $"<color=#fd4>{names[i]}</color>";

            return string.Join(" | ", names);
        }

        private static bool TryGetHitPosition(BasePlayer player, out Vector3 position, float maxDistance)
        {
            RaycastHit hit;
            if (Physics.Raycast(player.eyes.HeadRay(), out hit, maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                position = hit.point;
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        private static bool TryGetTrackPosition(BasePlayer player, out Vector3 trackPosition, float maxDistance = 30)
        {
            Vector3 hitPosition;
            if (!TryGetHitPosition(player, out hitPosition, maxDistance))
            {
                trackPosition = Vector3.zero;
                return false;
            }

            TrainTrackSpline spline;
            float distanceResult;
            if (!TrainTrackSpline.TryFindTrackNearby(hitPosition, 5, out spline, out distanceResult))
            {
                trackPosition = Vector3.zero;
                return false;
            }

            trackPosition = spline.GetPosition(distanceResult);
            return true;
        }

        private static DungeonCellWrapper FindNearestDungeonCell(Vector3 position)
        {
            DungeonCell closestDungeon = null;
            var shortestDistance = float.MaxValue;

            foreach (var dungeon in TerrainMeta.Path.DungeonCells)
            {
                var dungeonCellWrapper = new DungeonCellWrapper(dungeon);
                if (dungeonCellWrapper.TunnelType == TunnelType.Unsupported)
                    continue;

                if (!dungeonCellWrapper.IsInBounds(position))
                    continue;

                var distance = Vector3.Distance(dungeon.transform.position, position);
                if (distance < shortestDistance)
                {
                    shortestDistance = distance;
                    closestDungeon = dungeon;
                }
            }

            return closestDungeon == null ? null : new DungeonCellWrapper(closestDungeon);
        }

        private static List<DungeonCellWrapper> FindAllTunnelsOfType(TunnelType tunnelType)
        {
            var dungeonCellList = new List<DungeonCellWrapper>();

            foreach (var dungeonCell in TerrainMeta.Path.DungeonCells)
            {
                if (DungeonCellWrapper.GetTunnelType(dungeonCell) == tunnelType)
                    dungeonCellList.Add(new DungeonCellWrapper(dungeonCell));
            }

            return dungeonCellList;
        }

        private static BaseEntity GetLookEntity(BasePlayer player, int layerMask = Physics.DefaultRaycastLayers, float maxDistance = 20)
        {
            RaycastHit hit;
            return Physics.Raycast(player.eyes.HeadRay(), out hit, maxDistance, layerMask, QueryTriggerInteraction.Ignore)
                ? hit.GetEntity()
                : null;
        }

        private static TrainEngine GetWorkcartWhereAiming(BasePlayer player) =>
            GetLookEntity(player, Rust.Layers.Mask.Vehicle_Detailed) as TrainEngine;

        private static Vector3 GetWorkcartForward(TrainEngine workcart)
        {
            var trainController = workcart.GetComponent<TrainController>();
            var speed = trainController != null
                ? trainController.CurrentIntendedVelocity
                : workcart.CurThrottleSetting;

            return EngineSpeedToNumber(speed) >= 0 ?
                workcart.transform.forward
                : -workcart.transform.forward;
        }

        private static TrainEngine GetFasterWorkcart(TrainEngine workcart, TrainEngine otherWorkcart)
        {
            return Math.Abs(workcart.TrackSpeed) >= Math.Abs(otherWorkcart.TrackSpeed)
                ? workcart
                : otherWorkcart;
        }

        // Given two workcarts going the same direction, return the one that is closer to the destination direction.
        private static TrainEngine GetForwardWorkcart(TrainEngine workcart, TrainEngine otherWorkcart)
        {
            var forward = GetWorkcartForward(workcart);
            var otherForward = GetWorkcartForward(otherWorkcart);

            var position = workcart.transform.position;
            var otherPosition = otherWorkcart.transform.position;
            var forwardPosition = position + forward * 100f;

            var workcartDistance = (forwardPosition - position).magnitude;
            var otherWorkcartDistance = (forwardPosition - otherPosition).magnitude;

            return workcartDistance <= otherWorkcartDistance
                ? workcart
                : otherWorkcart;
        }

        private static void DetermineWorkcartOrientations(TrainEngine workcart, TrainEngine otherWorkcart, out TrainEngine forwardWorkcart, out TrainEngine backwardWorkcart)
        {
            forwardWorkcart = GetForwardWorkcart(workcart, otherWorkcart);
            backwardWorkcart = forwardWorkcart == workcart
                ? otherWorkcart
                : workcart;
        }

        private static void DestroyWorkcart(TrainEngine workcart)
        {
            if (workcart.IsDestroyed)
                return;

            var hitInfo = new HitInfo(null, workcart, Rust.DamageType.Explosion, float.MaxValue, workcart.transform.position);
            hitInfo.UseProtection = false;
            workcart.Die(hitInfo);
        }

        private static void ScheduleDestroyWorkcart(TrainEngine workcart)
        {
            workcart.Invoke(() => DestroyWorkcart(workcart), 0);
        }

        private static string FormatTime(double seconds) =>
            TimeSpan.FromSeconds(seconds).ToString("g");

        private static void EnableGlobalBroadcastFixed(BaseEntity entity, bool wants)
        {
            entity.globalBroadcast = wants;

            if (wants)
            {
                entity.UpdateNetworkGroup();
            }
            else if (entity.net?.group?.ID == 0)
            {
                // Fix vanilla bug that prevents leaving the global network group.
                var group = entity.net.sv.visibility.GetGroup(entity.transform.position);
                entity.net.SwitchGroup(group);
            }
        }

        #endregion

        #region Dungeon Cells

        private class DungeonCellWrapper
        {
            public static TunnelType GetTunnelType(DungeonCell dungeonCell) =>
                GetTunnelType(GetShortName(dungeonCell.name));

            private static TunnelType GetTunnelType(string shortName)
            {
                AutomatedWorkcarts.TunnelType tunnelType;
                return DungeonCellTypes.TryGetValue(shortName, out tunnelType)
                    ? tunnelType
                    : AutomatedWorkcarts.TunnelType.Unsupported;
            }

            public static Quaternion GetRotation(string shortName)
            {
                Quaternion rotation;
                return DungeonRotations.TryGetValue(shortName, out rotation)
                    ? rotation
                    : Quaternion.identity;
            }

            public string ShortName { get; private set; }
            public TunnelType TunnelType { get; private set; }
            public Vector3 Position { get; private set; }
            public Quaternion Rotation { get; private set; }

            private OBB _boundingBox;

            public DungeonCellWrapper(DungeonCell dungeonCell)
            {
                ShortName = GetShortName(dungeonCell.name);
                TunnelType = GetTunnelType(ShortName);
                Position = dungeonCell.transform.position;
                Rotation = GetRotation(ShortName);

                Vector3 dimensions;
                if (DungeonCellDimensions.TryGetValue(TunnelType, out dimensions))
                    _boundingBox = new OBB(Position + new Vector3(0, dimensions.y / 2, 0), dimensions, Rotation);
            }

            // World position to local position.
            public Vector3 InverseTransformPoint(Vector3 worldPosition) =>
                Quaternion.Inverse(Rotation) * (worldPosition - Position);

            // Local position to world position.
            public Vector3 TransformPoint(Vector3 localPosition) =>
                Position + Rotation * localPosition;

            public bool IsInBounds(Vector3 position) => _boundingBox.Contains(position);
        }

        #endregion

        #region Workcart Triggers

        private static readonly Dictionary<string, Quaternion> DungeonRotations = new Dictionary<string, Quaternion>()
        {
            ["station-sn-0"] = Quaternion.Euler(0, 180, 0),
            ["station-sn-1"] = Quaternion.identity,
            ["station-sn-2"] = Quaternion.Euler(0, 180, 0),
            ["station-sn-3"] = Quaternion.identity,
            ["station-we-0"] = Quaternion.Euler(0, 90, 0),
            ["station-we-1"] = Quaternion.Euler(0, -90, 0),
            ["station-we-2"] = Quaternion.Euler(0, 90, 0),
            ["station-we-3"] = Quaternion.Euler(0, -90, 0),

            ["straight-sn-0"] = Quaternion.Euler(0, 0, 0),
            ["straight-sn-1"] = Quaternion.Euler(0, 0, 0),
            ["straight-we-0"] = Quaternion.Euler(0, -90, 0),
            ["straight-we-1"] = Quaternion.Euler(0, 90, 0),

            ["straight-sn-4"] = Quaternion.Euler(0, 0, 0),
            ["straight-sn-5"] = Quaternion.Euler(0, 180, 0),
            ["straight-we-4"] = Quaternion.Euler(0, -90, 0),
            ["straight-we-5"] = Quaternion.Euler(0, 90, 0),
        };

        private static readonly Dictionary<string, TunnelType> DungeonCellTypes = new Dictionary<string, TunnelType>()
        {
            ["station-sn-0"] = TunnelType.TrainStation,
            ["station-sn-1"] = TunnelType.TrainStation,
            ["station-sn-2"] = TunnelType.TrainStation,
            ["station-sn-3"] = TunnelType.TrainStation,
            ["station-we-0"] = TunnelType.TrainStation,
            ["station-we-1"] = TunnelType.TrainStation,
            ["station-we-2"] = TunnelType.TrainStation,
            ["station-we-3"] = TunnelType.TrainStation,

            ["straight-sn-4"] = TunnelType.BarricadeTunnel,
            ["straight-sn-5"] = TunnelType.BarricadeTunnel,
            ["straight-we-4"] = TunnelType.BarricadeTunnel,
            ["straight-we-5"] = TunnelType.BarricadeTunnel,

            ["straight-sn-0"] = TunnelType.LootTunnel,
            ["straight-sn-1"] = TunnelType.LootTunnel,
            ["straight-we-0"] = TunnelType.LootTunnel,
            ["straight-we-1"] = TunnelType.LootTunnel,
        };

        private static readonly Dictionary<TunnelType, Vector3> DungeonCellDimensions = new Dictionary<TunnelType, Vector3>()
        {
            [TunnelType.TrainStation] = new Vector3(16.5f, 8.5f, 216),
            [TunnelType.BarricadeTunnel] = new Vector3(16.5f, 8.5f, 216),
            [TunnelType.LootTunnel] = new Vector3(16.5f, 8.5f, 216),
        };

        // Don't rename these since the names are persisted in data files.
        private enum TunnelType
        {
            TrainStation,
            BarricadeTunnel,
            LootTunnel,
            Unsupported
        }

        // Don't rename these since the names are persisted in data files.
        private enum WorkcartSpeed
        {
            Zero = 0,
            Lo = 1,
            Med = 2,
            Hi = 3
        }

        // Don't rename these since the names are persisted in data files.
        private enum WorkcartDirection
        {
            Fwd,
            Rev,
            Invert
        }

        // Don't rename these since the names are persisted in data files.
        private enum WorkcartTrackSelection
        {
            Default,
            Left,
            Right,
            Swap
        }

        private static int EngineSpeedToNumber(EngineSpeeds engineSpeed)
        {
            switch (engineSpeed)
            {
                case EngineSpeeds.Fwd_Hi: return 3;
                case EngineSpeeds.Fwd_Med: return 2;
                case EngineSpeeds.Fwd_Lo: return 1;
                case EngineSpeeds.Rev_Lo: return -1;
                case EngineSpeeds.Rev_Med: return -2;
                case EngineSpeeds.Rev_Hi: return -3;
                default: return 0;
            }
        }

        private static EngineSpeeds EngineSpeedFromNumber(int speedNumber)
        {
            switch (speedNumber)
            {
                case 3: return EngineSpeeds.Fwd_Hi;
                case 2: return EngineSpeeds.Fwd_Med;
                case 1: return EngineSpeeds.Fwd_Lo;
                case -1: return EngineSpeeds.Rev_Lo;
                case -2: return EngineSpeeds.Rev_Med;
                case -3: return EngineSpeeds.Rev_Hi;
                default: return EngineSpeeds.Zero;
            }
        }

        private static EngineSpeeds GetNextVelocity(EngineSpeeds throttleSpeed, WorkcartSpeed? desiredSpeed, WorkcartDirection? desiredDirection)
        {
            // -3 to 3
            var signedSpeed = EngineSpeedToNumber(throttleSpeed);

            // 0, 1, 2, 3
            var unsignedSpeed = Math.Abs(signedSpeed);

            // 1 or -1
            var sign = signedSpeed == 0 ? 1 : signedSpeed / unsignedSpeed;

            if (desiredDirection == WorkcartDirection.Fwd)
                sign = 1;
            else if (desiredDirection == WorkcartDirection.Rev)
                sign = -1;
            else if (desiredDirection == WorkcartDirection.Invert)
                sign *= -1;

            if (desiredSpeed == WorkcartSpeed.Hi)
                unsignedSpeed = 3;
            else if (desiredSpeed == WorkcartSpeed.Med)
                unsignedSpeed = 2;
            else if (desiredSpeed == WorkcartSpeed.Lo)
                unsignedSpeed = 1;
            else if (desiredSpeed == WorkcartSpeed.Zero)
                unsignedSpeed = 0;

            return EngineSpeedFromNumber(sign * unsignedSpeed);
        }

        private static TrackSelection GetNextTrackSelection(TrackSelection trackSelection, WorkcartTrackSelection? desiredTrackSelection)
        {
            switch (desiredTrackSelection)
            {
                case WorkcartTrackSelection.Default:
                    return TrackSelection.Default;

                case WorkcartTrackSelection.Left:
                    return TrackSelection.Left;

                case WorkcartTrackSelection.Right:
                    return TrackSelection.Right;

                case WorkcartTrackSelection.Swap:
                    return trackSelection == TrackSelection.Left
                        ? TrackSelection.Right
                        : trackSelection == TrackSelection.Right
                        ? TrackSelection.Left
                        : trackSelection;

                default:
                    return trackSelection;
            }
        }

        private enum WorkcartTriggerType { Map, Tunnel }

        private class WorkcartTrigger : TriggerBase
        {
            public WorkcartTriggerInfo TriggerInfo;
        }

        private class WorkcartTriggerInfo
        {
            [JsonProperty("Id")]
            public int Id;

            [JsonProperty("Position")]
            public Vector3 Position;

            [JsonProperty("TunnelType", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string TunnelType;

            [JsonProperty("AddConductor", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool AddConductor = false;

            [JsonProperty("Brake", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool Brake = false;

            [JsonProperty("Direction", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Direction;

            [JsonProperty("Speed", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Speed;

            [JsonProperty("TrackSelection", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string TrackSelection;

            [JsonProperty("StopDuration", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float StopDuration;

            [JsonProperty("DepartureSpeed", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string DepartureSpeed;

            [JsonIgnore]
            public WorkcartTriggerType TriggerType => TunnelType != null ? WorkcartTriggerType.Tunnel : WorkcartTriggerType.Map;

            public float GetStopDuration()
            {
                return StopDuration > 0
                    ? StopDuration
                    : 30;
            }

            private TunnelType? _tunnelType;
            public TunnelType GetTunnelType()
            {
                if (_tunnelType != null)
                    return (TunnelType)_tunnelType;

                _tunnelType = AutomatedWorkcarts.TunnelType.Unsupported;

                if (!string.IsNullOrEmpty(TunnelType))
                {
                    TunnelType tunnelType;
                    if (Enum.TryParse<TunnelType>(TunnelType, out tunnelType))
                        _tunnelType = tunnelType;
                }

                return (TunnelType)_tunnelType;
            }

            private WorkcartSpeed? _speed;
            public WorkcartSpeed? GetSpeed()
            {
                if (_speed == null && !string.IsNullOrWhiteSpace(Speed))
                {
                    WorkcartSpeed speed;
                    if (Enum.TryParse<WorkcartSpeed>(Speed, out speed))
                        _speed = speed;
                }

                // Ensure there is a target speed when braking.
                return Brake ? _speed ?? WorkcartSpeed.Zero : _speed;
            }

            private WorkcartDirection? _direction;
            public WorkcartDirection? GetDirection()
            {
                if (_direction == null && !string.IsNullOrWhiteSpace(Direction))
                {
                    WorkcartDirection direction;
                    if (Enum.TryParse<WorkcartDirection>(Direction, out direction))
                        _direction = direction;
                }

                return _direction;
            }

            private WorkcartTrackSelection? _trackSelection;
            public WorkcartTrackSelection? GetTrackSelection()
            {
                if (_trackSelection == null && !string.IsNullOrWhiteSpace(TrackSelection))
                {
                    WorkcartTrackSelection trackSelection;
                    if (Enum.TryParse<WorkcartTrackSelection>(TrackSelection, out trackSelection))
                        _trackSelection = trackSelection;
                }

                return _trackSelection;
            }

            private WorkcartSpeed? _departureSpeed;
            public WorkcartSpeed? GetDepartureSpeed()
            {
                if (_departureSpeed == null && !string.IsNullOrWhiteSpace(Speed))
                {
                    WorkcartSpeed speed;
                    if (Enum.TryParse<WorkcartSpeed>(DepartureSpeed, out speed))
                        _departureSpeed = speed;
                }

                return _departureSpeed ?? WorkcartSpeed.Med;
            }

            public void InvalidateCache()
            {
                _speed = null;
                _direction = null;
                _trackSelection = null;
                _departureSpeed = null;
            }

            public void CopyFrom(WorkcartTriggerInfo triggerInfo)
            {
                AddConductor = triggerInfo.AddConductor;
                Brake = triggerInfo.Brake;
                Speed = triggerInfo.Speed;
                DepartureSpeed = triggerInfo.DepartureSpeed;
                Direction = triggerInfo.Direction;
                TrackSelection = triggerInfo.TrackSelection;
            }

            public Color GetColor()
            {
                if (AddConductor)
                    return Color.cyan;

                var speed = GetSpeed();
                var direction = GetDirection();
                var trackSelection = GetTrackSelection();

                float hue, saturation;

                if (Brake)
                {
                    // Orange
                    hue = 0.5f/6f;
                    saturation = speed == WorkcartSpeed.Zero ? 1
                        : speed == WorkcartSpeed.Lo ? 0.8f
                        : 0.6f;
                    return Color.HSVToRGB(0.5f/6f, saturation, 1);
                }

                if (speed == WorkcartSpeed.Zero)
                    return Color.white;

                if (speed == null && direction == null && trackSelection != null)
                    return Color.magenta;

                hue = direction == WorkcartDirection.Fwd
                    ? 1/3f // Green
                    : direction == WorkcartDirection.Rev
                    ? 0 // Red
                    : direction == WorkcartDirection.Invert
                    ? 0.5f/6f // Orange
                    : 1/6f; // Yellow

                saturation = speed == WorkcartSpeed.Hi
                    ? 1
                    : speed == WorkcartSpeed.Med
                    ? 0.8f
                    : speed == WorkcartSpeed.Lo
                    ? 0.6f
                    : 1;

                return Color.HSVToRGB(hue, saturation, 1);
            }
        }

        private abstract class BaseTriggerWrapper
        {
            public WorkcartTriggerInfo TriggerInfo { get; protected set; }
            public virtual Vector3 Position => TriggerInfo.Position;

            protected GameObject _gameObject;

            protected BaseTriggerWrapper(WorkcartTriggerInfo triggerInfo)
            {
                TriggerInfo = triggerInfo;
            }

            protected void CreateTrigger()
            {
                _gameObject = new GameObject();
                UpdatePosition();

                var sphereCollider = _gameObject.AddComponent<SphereCollider>();
                sphereCollider.isTrigger = true;
                sphereCollider.radius = 1;
                sphereCollider.gameObject.layer = 6;

                var trigger = _gameObject.AddComponent<WorkcartTrigger>();
                trigger.TriggerInfo = TriggerInfo;
                trigger.interestLayers = Layers.Mask.Vehicle_World;
            }

            public virtual void UpdatePosition()
            {
                _gameObject.transform.position = Position;
            }

            public void Destroy()
            {
                UnityEngine.Object.Destroy(_gameObject);
            }
        }

        private class MapTriggerWrapper : BaseTriggerWrapper
        {
            public static MapTriggerWrapper CreateWorldTrigger(WorkcartTriggerInfo triggerInfo)
            {
                var triggerWrapper = new MapTriggerWrapper(triggerInfo);
                triggerWrapper.CreateTrigger();
                return triggerWrapper;
            }

            public MapTriggerWrapper(WorkcartTriggerInfo triggerInfo) : base(triggerInfo) {}
        }

        private class TunnelTriggerWrapper : BaseTriggerWrapper
        {
            public static TunnelTriggerWrapper[] CreateTunnelTriggers(WorkcartTriggerInfo triggerInfo)
            {
                var matchingDungeonCells = FindAllTunnelsOfType(triggerInfo.GetTunnelType());
                var triggerWrapperList = new TunnelTriggerWrapper[matchingDungeonCells.Count];

                for (var i = 0; i < matchingDungeonCells.Count; i++)
                {
                    var triggerWrapper = new TunnelTriggerWrapper(triggerInfo, matchingDungeonCells[i]);
                    triggerWrapper.CreateTrigger();
                    triggerWrapperList[i] = triggerWrapper;
                }

                return triggerWrapperList;
            }

            private DungeonCellWrapper _dungeonCellWrapper;

            public override Vector3 Position => _dungeonCellWrapper.TransformPoint(TriggerInfo.Position);

            public TunnelTriggerWrapper(WorkcartTriggerInfo triggerInfo, DungeonCellWrapper dungeonCellWrapper) : base(triggerInfo)
            {
                _dungeonCellWrapper = dungeonCellWrapper;
            }
        }

        #endregion

        #region Trigger Manager

        private class WorkcartTriggerManager
        {
            private const float TriggerDisplayDuration = 1f;
            private const float TriggerDisplayRadius = 1f;
            private const float TriggerDrawDistance = 150;

            private Dictionary<WorkcartTriggerInfo, MapTriggerWrapper> _mapTriggers = new Dictionary<WorkcartTriggerInfo, MapTriggerWrapper>();
            private Dictionary<WorkcartTriggerInfo, TunnelTriggerWrapper[]> _tunnelTriggers = new Dictionary<WorkcartTriggerInfo, TunnelTriggerWrapper[]>();
            private Dictionary<ulong, Timer> _drawTimers = new Dictionary<ulong, Timer>();

            private int GetHighestTriggerId(IEnumerable<WorkcartTriggerInfo> triggerList)
            {
                var highestTriggerId = 0;

                foreach (var triggerInfo in triggerList)
                    highestTriggerId = Math.Max(highestTriggerId, triggerInfo.Id);

                return highestTriggerId;
            }

            public WorkcartTriggerInfo FindTrigger(int triggerId, WorkcartTriggerType triggerType)
            {
                IEnumerable<WorkcartTriggerInfo> triggerList = _mapTriggers.Keys;
                if (triggerType == WorkcartTriggerType.Tunnel)
                    triggerList = _tunnelTriggers.Keys;

                foreach (var triggerInfo in triggerList)
                {
                    if (triggerInfo.Id == triggerId)
                        return triggerInfo;
                }

                return null;
            }

            public void AddTrigger(WorkcartTriggerInfo triggerInfo)
            {
                if (triggerInfo.TriggerType == WorkcartTriggerType.Tunnel)
                {
                    if (triggerInfo.Id == 0)
                        triggerInfo.Id = GetHighestTriggerId(_tunnelTriggers.Keys) + 1;

                    _tunnelTriggers[triggerInfo] = TunnelTriggerWrapper.CreateTunnelTriggers(triggerInfo);
                    _tunnelData.AddTrigger(triggerInfo);
                }
                else
                {
                    if (triggerInfo.Id == 0)
                        triggerInfo.Id = GetHighestTriggerId(_mapTriggers.Keys) + 1;

                    _mapTriggers[triggerInfo] = MapTriggerWrapper.CreateWorldTrigger(triggerInfo);
                    _mapData.AddTrigger(triggerInfo);
                }
            }

            public void UpdateTrigger(WorkcartTriggerInfo triggerInfo)
            {
                triggerInfo.InvalidateCache();

                if (triggerInfo.TriggerType == WorkcartTriggerType.Tunnel)
                    _tunnelData.Save();
                else
                    _mapData.Save();
            }

            public void MoveTrigger(WorkcartTriggerInfo triggerInfo, Vector3 position)
            {
                triggerInfo.Position = position;

                if (triggerInfo.TriggerType == WorkcartTriggerType.Tunnel)
                {
                    _tunnelData.Save();

                    TunnelTriggerWrapper[] triggerWrapperList;
                    if (_tunnelTriggers.TryGetValue(triggerInfo, out triggerWrapperList))
                    {
                        foreach (var triggerWrapper in triggerWrapperList)
                            triggerWrapper.UpdatePosition();
                    }
                }
                else
                {
                    _mapData.Save();

                    MapTriggerWrapper triggerWrapper;
                    if (_mapTriggers.TryGetValue(triggerInfo, out triggerWrapper))
                        triggerWrapper.UpdatePosition();
                }
            }

            public void RemoveTrigger(WorkcartTriggerInfo triggerInfo)
            {
                if (triggerInfo.TriggerType == WorkcartTriggerType.Tunnel)
                {
                    TunnelTriggerWrapper[] triggerWrapperList;
                    if (_tunnelTriggers.TryGetValue(triggerInfo, out triggerWrapperList))
                    {
                        foreach (var triggerWrapper in triggerWrapperList)
                            triggerWrapper.Destroy();

                        _tunnelTriggers.Remove(triggerInfo);
                    }

                    _tunnelData.RemoveTrigger(triggerInfo);
                }
                else
                {
                    MapTriggerWrapper triggerWrapper;
                    if (_mapTriggers.TryGetValue(triggerInfo, out triggerWrapper))
                    {
                        triggerWrapper.Destroy();
                        _mapTriggers.Remove(triggerInfo);
                    }

                    _mapData.RemoveTrigger(triggerInfo);
                }
            }

            public void CreateAll()
            {
                if (_pluginConfig.EnableMapTriggers)
                {
                    foreach (var triggerInfo in _mapData.MapTriggers)
                        _mapTriggers[triggerInfo] = MapTriggerWrapper.CreateWorldTrigger(triggerInfo);
                }

                foreach (var triggerInfo in _tunnelData.TunnelTriggers)
                {
                    var tunnelType = triggerInfo.GetTunnelType();
                    if (tunnelType == TunnelType.Unsupported || !_pluginConfig.IsTunnelTypeEnabled(tunnelType))
                        continue;

                    _tunnelTriggers[triggerInfo] = TunnelTriggerWrapper.CreateTunnelTriggers(triggerInfo);
                }
            }

            public void DestroyAll()
            {
                foreach (var triggerWrapper in _mapTriggers.Values)
                    triggerWrapper.Destroy();

                foreach (var triggerWrapperList in _tunnelTriggers.Values)
                    foreach (var triggerWrapper in triggerWrapperList)
                        triggerWrapper.Destroy();
            }

            public void ShowAllRepeatedly(BasePlayer player)
            {
                ShowNearbyTriggers(player, player.transform.position);

                Timer existingTimer;
                if (_drawTimers.TryGetValue(player.userID, out existingTimer))
                    existingTimer.Destroy();

                _drawTimers[player.userID] = _pluginInstance.timer.Repeat(TriggerDisplayDuration - 0.1f, 60, () =>
                {
                    ShowNearbyTriggers(player, player.transform.position);
                });
            }

            private void ShowNearbyTriggers(BasePlayer player, Vector3 playerPosition)
            {
                var isAdmin = player.IsAdmin;
                if (!isAdmin)
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                    player.SendNetworkUpdateImmediate();
                }

                foreach (var trigger in _mapTriggers.Values)
                {
                    if (Vector3.Distance(playerPosition, trigger.Position) <= TriggerDrawDistance)
                        ShowTrigger(player, trigger);
                }

                foreach (var triggerList in _tunnelTriggers.Values)
                {
                    foreach (var trigger in triggerList)
                    {
                        if (Vector3.Distance(playerPosition, trigger.Position) <= TriggerDrawDistance)
                            ShowTrigger(player, trigger, triggerList.Length);
                    }
                }

                if (!isAdmin)
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
                    player.SendNetworkUpdateImmediate();
                }
            }

            private static void ShowTrigger(BasePlayer player, BaseTriggerWrapper trigger, int count = 1)
            {
                var triggerInfo = trigger.TriggerInfo;
                var color = triggerInfo.GetColor();

                var spherePosition = trigger.Position;
                player.SendConsoleCommand("ddraw.sphere", TriggerDisplayDuration, color, spherePosition, TriggerDisplayRadius);

                var triggerPrefix = _pluginInstance.GetTriggerPrefix(player, triggerInfo);
                var infoLines = new List<string>()
                {
                    _pluginInstance.GetMessage(player, Lang.InfoTrigger, triggerPrefix, triggerInfo.Id)
                };

                if (triggerInfo.TriggerType == WorkcartTriggerType.Tunnel)
                    infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerTunnel, triggerInfo.TunnelType, count));
                else
                    infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerMap, triggerInfo.Id));

                if (triggerInfo.AddConductor)
                    infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerAddConductor));

                var direction = triggerInfo.GetDirection();
                if (direction != null && !triggerInfo.Brake)
                    infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerDirection, direction));

                var speed = triggerInfo.GetSpeed();
                if (speed != null)
                    infoLines.Add(_pluginInstance.GetMessage(player, triggerInfo.Brake ? Lang.InfoTriggerBrakeToSpeed : Lang.InfoTriggerSpeed, speed));

                if (speed == WorkcartSpeed.Zero)
                    infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerStopDuration, triggerInfo.GetStopDuration()));

                var trackSelection = triggerInfo.GetTrackSelection();
                if (trackSelection != null)
                    infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerTrackSelection, trackSelection));

                if (speed == WorkcartSpeed.Zero && direction != null)
                    infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerDepartureDirection, direction));

                var departureSpeed = triggerInfo.GetDepartureSpeed();
                if (speed == WorkcartSpeed.Zero)
                    infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerDepartureSpeed, departureSpeed));

                var textPosition = trigger.Position + new Vector3(0, 1.5f + infoLines.Count * 0.1f, 0);
                player.SendConsoleCommand("ddraw.text", TriggerDisplayDuration, color, textPosition, string.Join("\n", infoLines));
            }

            public BaseTriggerWrapper FindNearestTrigger(Vector3 position, float maxDistance = 10)
            {
                BaseTriggerWrapper closestTriggerWrapper = null;
                float shortestDistance = float.MaxValue;

                foreach (var trigger in _mapTriggers.Values)
                {
                    var distance = Vector3.Distance(position, trigger.Position);
                    if (distance >= shortestDistance || distance >= maxDistance)
                        continue;

                    shortestDistance = distance;
                    closestTriggerWrapper = trigger;
                }

                foreach (var triggerList in _tunnelTriggers.Values)
                {
                    foreach (var trigger in triggerList)
                    {
                        var distance = Vector3.Distance(position, trigger.Position);
                        if (distance >= shortestDistance || distance >= maxDistance)
                            continue;

                        shortestDistance = distance;
                        closestTriggerWrapper = trigger;
                    }
                }

                return closestTriggerWrapper;
            }

            public WorkcartTriggerInfo FindNearestTriggerWhereAiming(BasePlayer player, float maxDistance = 10)
            {
                Vector3 trackPosition;
                if (!TryGetTrackPosition(player, out trackPosition))
                    return null;

                return FindNearestTrigger(trackPosition, maxDistance)?.TriggerInfo;
            }
        }

        #endregion

        #region Workcart Manager

        private class AutomatedWorkcartManager
        {
            private List<TrainEngine> _automatedWorkcarts = new List<TrainEngine>();

            public int NumWorkcarts => _automatedWorkcarts.Count;

            public void Register(TrainEngine workcart)
            {
                _automatedWorkcarts.Add(workcart);
                _pluginData.AddWorkcartId(workcart.net.ID);
            }

            public void Unregister(TrainEngine workcart)
            {
                _automatedWorkcarts.Remove(workcart);
                _pluginData.RemoveWorkcartId(workcart.net.ID);
            }

            public void ShowMarkersToPlayer(ulong userId, float duration)
            {
                foreach (var workcart in _automatedWorkcarts)
                {
                    if (workcart == null)
                        continue;

                    var controller = workcart.GetComponent<TrainController>();
                    if (controller == null)
                        continue;

                    controller.AddMakerForPlayer(userId, duration);
                }
            }
        }

        #endregion

        #region Train Controller

        private class TrainController : FacepunchBehaviour
        {
            private const float ChillDuration = 3f;

            public static void DestroyAll()
            {
                foreach (var entity in BaseNetworkable.serverEntities)
                {
                    var workcart = entity as TrainEngine;
                    if (workcart == null)
                        continue;

                    var component = workcart.GetComponent<TrainController>();
                    if (component == null)
                        continue;

                    DestroyImmediate(component);
                }
            }

            public BasePlayer Conductor { get; private set; }
            private TrainEngine _workcart;
            private EngineSpeeds _targetVelocity;
            private EngineSpeeds _departureVelocity;
            private float _stopDuration;

            private Dictionary<ulong, Action> _mapMarkerCleanupFunctions = new Dictionary<ulong, Action>();

            private void Awake()
            {
                _workcart = GetComponent<TrainEngine>();
                if (_workcart == null)
                    return;

                AddConductor();
                EnableUnlimitedFuel();
                StartTrain(_pluginConfig.GetDefaultSpeed());
            }

            public void StartImmediately(WorkcartTriggerInfo triggerInfo)
            {
                var initialSpeed = GetNextVelocity(EngineSpeeds.Zero, triggerInfo.GetSpeed(), triggerInfo.GetDirection());

                Invoke(() =>
                {
                    StartTrain(initialSpeed);
                    HandleWorkcartTrigger(triggerInfo);
                }, UnityEngine.Random.Range(0, 1f));
            }

            public void HandleWorkcartTrigger(WorkcartTriggerInfo triggerInfo)
            {
                CancelWaitingAtStop();
                CancelChilling();

                var currentIntendedVelocity = CurrentIntendedVelocity;

                var triggerSpeed = triggerInfo.GetSpeed();
                var triggerDirection = triggerInfo.GetDirection();

                _workcart.SetTrackSelection(GetNextTrackSelection(_workcart.curTrackSelection, triggerInfo.GetTrackSelection()));
                _departureVelocity = GetNextVelocity(currentIntendedVelocity, triggerInfo.GetDepartureSpeed(), triggerDirection);
                _stopDuration = triggerInfo.GetStopDuration();

                var nextVelocity = GetNextVelocity(currentIntendedVelocity, triggerSpeed, triggerDirection);

                // Only cancel braking if the trigger specifies speed.
                // Must do this after computing the next velocity.
                if (triggerSpeed != null && IsBraking)
                    CancelBraking();

                if (triggerInfo.Brake)
                {
                    StartBrakingUntilVelocity(currentIntendedVelocity, nextVelocity);
                    return;
                }

                SetThrottle(nextVelocity);

                if (nextVelocity == EngineSpeeds.Zero)
                    BeginWaitingAtStop(triggerInfo.GetStopDuration());
            }

            private void BeginWaitingAtStop(float stopDuration) => Invoke(DepartFromStop, stopDuration);

            public void DepartEarlyIfStoppedOrStopping()
            {
                if (IsWaitingAtStop || IsStopping)
                {
                    CancelWaitingAtStop();
                    CancelBraking();
                    DepartFromStop();
                }
            }

            public void DepartFromStop() => SetThrottle(_departureVelocity);
            private bool IsWaitingAtStop => IsInvoking(DepartFromStop);
            public void CancelWaitingAtStop() => CancelInvoke(DepartFromStop);

            public void SetThrottle(EngineSpeeds engineSpeed)
            {
                _workcart.SetThrottle(engineSpeed);
            }

            public void ScheduleDestruction() => Invoke(DestroyCinematically, 0);
            public bool IsDestroying => IsInvoking(DestroyCinematically);
            private void DestroyCinematically() => DestroyWorkcart(_workcart);

            private void StartBrakingUntilVelocity(EngineSpeeds currentVelocity, EngineSpeeds desiredVelocity)
            {
                SetThrottle(GetNextVelocity(currentVelocity, WorkcartSpeed.Lo, WorkcartDirection.Invert));
                CancelBraking();
                _targetVelocity = desiredVelocity;
                InvokeRepeating(BrakeUpdate, 0, 0);
            }
            private bool IsBraking => IsInvoking(BrakeUpdate);
            private void CancelBraking() => CancelInvoke(BrakeUpdate);

            private bool IsStopping => IsBraking && _targetVelocity == EngineSpeeds.Zero;

            private void BrakeUpdate()
            {
                if (IsNearSpeed(_targetVelocity))
                {
                    SetThrottle(_targetVelocity);
                    if (_targetVelocity == EngineSpeeds.Zero)
                        BeginWaitingAtStop(_stopDuration);

                    CancelBraking();
                }
            }

            public void StartChilling()
            {
                if (IsBraking)
                    return;

                if (!IsChilling)
                {
                    _targetVelocity = CurrentIntendedVelocity;
                    if (!IsBraking)
                        _targetVelocity = _workcart.CurThrottleSetting;
                    else if (_targetVelocity == EngineSpeeds.Zero)
                        _targetVelocity = _departureVelocity;

                    SetThrottle(EngineSpeeds.Zero);
                }

                CancelChilling();
                Invoke(EndChilling, ChillDuration);
            }

            private bool IsChilling => IsInvoking(EndChilling);
            private void EndChilling() => SetThrottle(_targetVelocity);
            private void CancelChilling() => CancelInvoke(EndChilling);

            public EngineSpeeds CurrentIntendedVelocity => IsChilling || IsBraking
                ? _targetVelocity
                : _workcart.CurThrottleSetting == EngineSpeeds.Zero
                ? _departureVelocity
                : _workcart.CurThrottleSetting;

            private float GetThrottleFraction(EngineSpeeds throttle)
            {
                switch (throttle)
                {
                    case EngineSpeeds.Rev_Hi: return -1;
                    case EngineSpeeds.Rev_Med: return -0.5f;
                    case EngineSpeeds.Rev_Lo: return -0.2f;
                    case EngineSpeeds.Fwd_Lo: return 0.2f;
                    case EngineSpeeds.Fwd_Med: return 0.5f;
                    case EngineSpeeds.Fwd_Hi: return 1;
                    default: return 0;
                }
            }

            private bool IsNearSpeed(EngineSpeeds desiredThrottle, float leeway = 0.1f)
            {
                var currentSpeed = Vector3.Dot(_workcart.transform.forward, _workcart.GetLocalVelocity());
                var desiredSpeed = _workcart.maxSpeed * GetThrottleFraction(desiredThrottle);

                // If desiring a negative speed, current speed is expected to increase (e.g., -10 to -5).
                // If desiring positive speed, current speed is expected to decrease (e.g., 10 to 5).
                // If desiring zero speed, the direction depends on the throttle being applied (e.g., if positive, -10 to -5).
                return desiredSpeed < 0 || (desiredSpeed == 0 && GetThrottleFraction(_workcart.CurThrottleSetting) > 0)
                    ? currentSpeed + leeway >= desiredSpeed
                    : currentSpeed - leeway <= desiredSpeed;
            }

            private void AddConductor()
            {
                _workcart.DismountAllPlayers();

                Conductor = GameManager.server.CreateEntity(PlayerPrefab, _workcart.transform.position) as BasePlayer;
                if (Conductor == null)
                    return;

                Conductor.enableSaving = false;
                Conductor.Spawn();

                // Disabling the collider is a simple and performant way to prevent NPCs from targeting the conductor.
                Conductor.DisablePlayerCollider();

                AddOutfit();

                _workcart.platformParentTrigger.OnTriggerEnter(Conductor.playerCollider);

                Conductor.displayName = "Conductor";
                _workcart.AttemptMount(Conductor, false);
            }

            private void StartTrain(EngineSpeeds initialSpeed)
            {
                _workcart.engineController.TryStartEngine(Conductor);
                SetThrottle(initialSpeed);
                _workcart.SetTrackSelection(_pluginConfig.GetDefaultTrackSelection());
            }

            private void AddOutfit()
            {
                Conductor.inventory.Strip();

                foreach (var itemInfo in _pluginConfig.ConductorOutfit)
                {
                    var itemDefinition = itemInfo.GetItemDefinition();
                    if (itemDefinition != null)
                        Conductor.inventory.containerWear.AddItem(itemDefinition, 1, itemInfo.SkinId);
                }

                Conductor.SendNetworkUpdate();
            }

            private void EnableUnlimitedFuel()
            {
                _workcart.fuelSystem.cachedHasFuel = true;
                _workcart.fuelSystem.nextFuelCheckTime = float.MaxValue;
            }

            private void DisableUnlimitedFuel()
            {
                _workcart.fuelSystem.nextFuelCheckTime = 0;
            }

            private void OnDestroy()
            {
                if (Conductor != null)
                    Conductor.Kill();

                DisableUnlimitedFuel();

                foreach (var cleanupFunction in _mapMarkerCleanupFunctions.Values.ToArray())
                    cleanupFunction();

                if (_workcart.globalBroadcast)
                    EnableGlobalBroadcastFixed(_workcart, false);
            }

            public bool HasMarkers() => !_mapMarkerCleanupFunctions.IsEmpty();

            private MapMarkerDeliveryDrone AddDroneMapMarker(ulong userId)
            {
                var mapMarker = GameManager.server.CreateEntity(DeliveryDroneMarkerPrefab) as MapMarkerDeliveryDrone;
                if (mapMarker == null)
                    return null;

                mapMarker.EnableSaving(false);
                mapMarker.OwnerID = userId;
                mapMarker.SetParent(_workcart);
                mapMarker.Spawn();

                return mapMarker;
            }

            public void AddMakerForPlayer(ulong userId, float duration)
            {
                Action cleanupFunction;
                if (_mapMarkerCleanupFunctions.TryGetValue(userId, out cleanupFunction))
                {
                    CancelInvoke(cleanupFunction);
                    Invoke(cleanupFunction, duration);
                    return;
                }

                var marker = AddDroneMapMarker(userId);
                cleanupFunction = () =>
                {
                    if (!marker.IsDestroyed)
                        marker.Kill();

                    _mapMarkerCleanupFunctions.Remove(userId);

                    if (_workcart.globalBroadcast && _mapMarkerCleanupFunctions.IsEmpty())
                        EnableGlobalBroadcastFixed(_workcart, false);
                };

                _mapMarkerCleanupFunctions.Add(userId, cleanupFunction);
                Invoke(cleanupFunction, duration);

                if (!_workcart.globalBroadcast)
                    _workcart.EnableGlobalBroadcast(true);
            }
        }

        #endregion

        #region Data

        private class StoredPluginData
        {
            [JsonProperty("AutomatedWorkcardIds")]
            public HashSet<uint> AutomatedWorkcardIds = new HashSet<uint>();

            public static string Filename => _pluginInstance.Name;

            public static StoredPluginData Load() =>
                Interface.Oxide.DataFileSystem.ReadObject<StoredPluginData>(Filename) ?? new StoredPluginData();

            public static StoredPluginData Clear() => new StoredPluginData().Save();

            public StoredPluginData Save()
            {
                Interface.Oxide.DataFileSystem.WriteObject<StoredPluginData>(Filename, this);
                return this;
            }

            public bool HasWorkcartId(uint workcartId)
            {
                return AutomatedWorkcardIds.Contains(workcartId);
            }

            public void AddWorkcartId(uint workcartId)
            {
                AutomatedWorkcardIds.Add(workcartId);
            }

            public void RemoveWorkcartId(uint workcartId)
            {
                AutomatedWorkcardIds.Remove(workcartId);
            }

            public void ReplaceWorkcartIds(List<uint> workcardIds)
            {
                AutomatedWorkcardIds = new HashSet<uint>(workcardIds);
                Save();
            }
        }

        private class StoredMapData
        {
            [JsonProperty("MapTriggers")]
            public List<WorkcartTriggerInfo> MapTriggers = new List<WorkcartTriggerInfo>();

            private static string GetMapName() =>
                World.SaveFileName.Substring(0, World.SaveFileName.LastIndexOf("."));

            public static string Filename => $"{_pluginInstance.Name}/{GetMapName()}";

            public static StoredMapData Load()
            {
                if (Interface.Oxide.DataFileSystem.ExistsDatafile(Filename))
                    return Interface.Oxide.DataFileSystem.ReadObject<StoredMapData>(Filename) ?? new StoredMapData();

                return new StoredMapData();
            }

            public StoredMapData Save()
            {
                Interface.Oxide.DataFileSystem.WriteObject(Filename, this);
                return this;
            }

            public void AddTrigger(WorkcartTriggerInfo customTrigger)
            {
                MapTriggers.Add(customTrigger);
                Save();
            }

            public void RemoveTrigger(WorkcartTriggerInfo triggerInfo)
            {
                MapTriggers.Remove(triggerInfo);
                Save();
            }
        }

        private class StoredTunnelData
        {
            [JsonProperty("TunnelTriggers")]
            public List<WorkcartTriggerInfo> TunnelTriggers = new List<WorkcartTriggerInfo>();

            public static string Filename => $"{_pluginInstance.Name}/TunnelTriggers";

            public static StoredTunnelData Load()
            {
                if (Interface.Oxide.DataFileSystem.ExistsDatafile(Filename))
                    return Interface.Oxide.DataFileSystem.ReadObject<StoredTunnelData>(Filename) ?? GetDefaultData();

                return GetDefaultData();
            }

            public StoredTunnelData Save()
            {
                Interface.Oxide.DataFileSystem.WriteObject(Filename, this);
                return this;
            }

            public void AddTrigger(WorkcartTriggerInfo triggerInfo)
            {
                TunnelTriggers.Add(triggerInfo);
                Save();
            }

            public void RemoveTrigger(WorkcartTriggerInfo triggerInfo)
            {
                TunnelTriggers.Remove(triggerInfo);
                Save();
            }

            public static StoredTunnelData GetDefaultData()
            {
                var stationStopDuration = 15f;
                var quickStopDuration = 5f;
                var triggerHeight = 0.29f;

                return new StoredTunnelData()
                {
                    TunnelTriggers =
                    {
                        new WorkcartTriggerInfo
                        {
                            Id = 1,
                            Position = new Vector3(4.5f, triggerHeight, 53),
                            TunnelType = TunnelType.TrainStation.ToString(),
                            Brake = true,
                            Speed = WorkcartSpeed.Zero.ToString(),
                            StopDuration = stationStopDuration,
                            DepartureSpeed = WorkcartSpeed.Hi.ToString(),
                        },
                        new WorkcartTriggerInfo
                        {
                            Id = 2,
                            Position = new Vector3(0, triggerHeight, -84),
                            TunnelType = TunnelType.TrainStation.ToString(),
                            AddConductor = true,
                            Direction = WorkcartDirection.Fwd.ToString(),
                            Speed = WorkcartSpeed.Hi.ToString(),
                            TrackSelection = WorkcartTrackSelection.Left.ToString(),
                        },
                        new WorkcartTriggerInfo
                        {
                            Id = 3,
                            Position = new Vector3(-4.5f, triggerHeight, -13),
                            TunnelType = TunnelType.TrainStation.ToString(),
                            Brake = true,
                            Speed = WorkcartSpeed.Zero.ToString(),
                            StopDuration = stationStopDuration,
                            DepartureSpeed = WorkcartSpeed.Hi.ToString(),
                        },
                        new WorkcartTriggerInfo
                        {
                            Id = 4,
                            Position = new Vector3(0, triggerHeight, 84),
                            TunnelType = TunnelType.TrainStation.ToString(),
                            AddConductor = true,
                            Direction = WorkcartDirection.Fwd.ToString(),
                            Speed = WorkcartSpeed.Hi.ToString(),
                            TrackSelection = WorkcartTrackSelection.Left.ToString(),
                        },

                        new WorkcartTriggerInfo
                        {
                            Id = 5,
                            Position = new Vector3(-4.45f, triggerHeight, -31),
                            TunnelType = TunnelType.BarricadeTunnel.ToString(),
                            Brake = true,
                            Speed = WorkcartSpeed.Med.ToString(),
                        },
                        new WorkcartTriggerInfo
                        {
                            Id = 6,
                            Position = new Vector3(-4.5f, triggerHeight, -1f),
                            TunnelType = TunnelType.BarricadeTunnel.ToString(),
                            Brake = true,
                            Speed = WorkcartSpeed.Zero.ToString(),
                            StopDuration = 5,
                            DepartureSpeed = WorkcartSpeed.Hi.ToString(),
                        },
                        new WorkcartTriggerInfo
                        {
                            Id = 7,
                            Position = new Vector3(4.45f, triggerHeight, 39),
                            TunnelType = TunnelType.BarricadeTunnel.ToString(),
                            Brake = true,
                            Speed = WorkcartSpeed.Med.ToString(),
                        },
                        new WorkcartTriggerInfo
                        {
                            Id = 8,
                            Position = new Vector3(4.5f, triggerHeight, 9f),
                            TunnelType = TunnelType.BarricadeTunnel.ToString(),
                            Brake = true,
                            Speed = WorkcartSpeed.Zero.ToString(),
                            StopDuration = 5,
                            DepartureSpeed = WorkcartSpeed.Hi.ToString(),
                        },

                        new WorkcartTriggerInfo
                        {
                            Id = 9,
                            Position = new Vector3(3, triggerHeight, 35f),
                            TunnelType = TunnelType.LootTunnel.ToString(),
                            Brake = true,
                            Speed = WorkcartSpeed.Zero.ToString(),
                            StopDuration = quickStopDuration,
                            DepartureSpeed = WorkcartSpeed.Hi.ToString(),
                        },
                        new WorkcartTriggerInfo
                        {
                            Id = 10,
                            Position = new Vector3(-3, triggerHeight, -35f),
                            TunnelType = TunnelType.LootTunnel.ToString(),
                            Brake = true,
                            Speed = WorkcartSpeed.Zero.ToString(),
                            StopDuration = quickStopDuration,
                            DepartureSpeed = WorkcartSpeed.Hi.ToString(),
                        },
                    }
                };
            }
        }

        #endregion

        #region Configuration

        private class ItemInfo
        {
            [JsonProperty("ShortName")]
            public string ShortName;

            [JsonProperty("Skin")]
            public ulong SkinId;

            private bool _isValidated = false;
            private ItemDefinition _itemDefinition;
            public ItemDefinition GetItemDefinition()
            {
                if (!_isValidated)
                {
                    var itemDefinition = ItemManager.FindItemDefinition(ShortName);
                    if (itemDefinition != null)
                        _itemDefinition = itemDefinition;
                    else
                        _pluginInstance.LogError($"Invalid item short name in config: '{ShortName}'");

                    _isValidated = true;
                }

                return _itemDefinition;
            }
        }

        private class Configuration : SerializableConfiguration
        {
            [JsonProperty("DefaultSpeed")]
            public string DefaultSpeed = EngineSpeeds.Fwd_Hi.ToString();

            [JsonProperty("DefaultTrackSelection")]
            public string DefaultTrackSelection = TrackSelection.Left.ToString();

            [JsonProperty("BulldozeOffendingWorkcarts")]
            public bool BulldozeOffendingWorkcarts = false;

            [JsonProperty("EnableMapTriggers")]
            public bool EnableMapTriggers = true;

            [JsonProperty("EnableTunnelTriggers")]
            public Dictionary<string, bool> EnableTunnelTriggers = new Dictionary<string, bool>
            {
                [TunnelType.TrainStation.ToString()] = false,
                [TunnelType.BarricadeTunnel.ToString()] = false,
                [TunnelType.LootTunnel.ToString()] = false,
            };

            [JsonProperty("MaxConductors")]
            public int MaxConductors = -1;

            [JsonProperty("ConductorOutfit")]
            public ItemInfo[] ConductorOutfit = new ItemInfo[]
            {
                new ItemInfo { ShortName = "jumpsuit.suit" },
                new ItemInfo { ShortName = "sunglasses03chrome" },
                new ItemInfo { ShortName = "hat.boonie" },
            };

            public bool IsTunnelTypeEnabled(TunnelType tunnelType)
            {
                bool enabled;
                return EnableTunnelTriggers.TryGetValue(tunnelType.ToString(), out enabled)
                    ? enabled
                    : false;
            }

            private EngineSpeeds? _defaultSpeed;
            public EngineSpeeds GetDefaultSpeed()
            {
                if (_defaultSpeed != null)
                    return (EngineSpeeds)_defaultSpeed;

                EngineSpeeds engineSpeed;
                if (TryParseEngineSpeed(DefaultSpeed, out engineSpeed))
                {
                    _defaultSpeed = engineSpeed;
                    return engineSpeed;
                }

                return EngineSpeeds.Fwd_Hi;
            }

            private TrackSelection? _defaultTrackSelection;
            public TrackSelection GetDefaultTrackSelection()
            {
                if (_defaultTrackSelection != null)
                    return (TrackSelection)_defaultTrackSelection;

                TrackSelection trackSelection;
                if (TryParseTrackSelection(DefaultTrackSelection, out trackSelection))
                {
                    _defaultTrackSelection = trackSelection;
                    return trackSelection;
                }

                return TrackSelection.Left;
            }
        }

        private Configuration GetDefaultConfig() => new Configuration();

        #endregion

        #region Configuration Boilerplate

        private class SerializableConfiguration
        {
            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonHelper.Deserialize(ToJson()) as Dictionary<string, object>;
        }

        private static class JsonHelper
        {
            public static object Deserialize(string json) => ToObject(JToken.Parse(json));

            private static object ToObject(JToken token)
            {
                switch (token.Type)
                {
                    case JTokenType.Object:
                        return token.Children<JProperty>()
                                    .ToDictionary(prop => prop.Name,
                                                  prop => ToObject(prop.Value));

                    case JTokenType.Array:
                        return token.Select(ToObject).ToList();

                    default:
                        return ((JValue)token).Value;
                }
            }
        }

        private bool MaybeUpdateConfig(SerializableConfiguration config)
        {
            var currentWithDefaults = config.ToDictionary();
            var currentRaw = Config.ToDictionary(x => x.Key, x => x.Value);
            return MaybeUpdateConfigDict(currentWithDefaults, currentRaw);
        }

        private bool MaybeUpdateConfigDict(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
        {
            bool changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                object currentRawValue;
                if (currentRaw.TryGetValue(key, out currentRawValue))
                {
                    var defaultDictValue = currentWithDefaults[key] as Dictionary<string, object>;
                    var currentDictValue = currentRawValue as Dictionary<string, object>;

                    if (defaultDictValue != null)
                    {
                        if (currentDictValue == null)
                        {
                            currentRaw[key] = currentWithDefaults[key];
                            changed = true;
                        }
                        else if (MaybeUpdateConfigDict(defaultDictValue, currentDictValue))
                            changed = true;
                    }
                }
                else
                {
                    currentRaw[key] = currentWithDefaults[key];
                    changed = true;
                }
            }

            return changed;
        }

        protected override void LoadDefaultConfig() => _pluginConfig = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _pluginConfig = Config.ReadObject<Configuration>();
                if (_pluginConfig == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(_pluginConfig))
                {
                    LogWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch
            {
                LogWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Log($"Configuration changes saved to {Name}.json");
            Config.WriteObject(_pluginConfig, true);
        }

        #endregion

        #region Localization

        private void ReplyToPlayer(IPlayer player, string messageName, params object[] args) =>
            player.Reply(string.Format(GetMessage(player, messageName), args));

        private void ChatMessage(BasePlayer player, string messageName, params object[] args) =>
            player.ChatMessage(string.Format(GetMessage(player.IPlayer, messageName), args));

        private string GetMessage(BasePlayer player, string messageName, params object[] args) =>
            GetMessage(player.UserIDString, messageName, args);

        private string GetMessage(IPlayer player, string messageName, params object[] args) =>
            GetMessage(player.Id, messageName, args);

        private string GetMessage(string playerId, string messageName, params object[] args)
        {
            var message = lang.GetMessage(messageName, this, playerId);
            return args.Length > 0 ? string.Format(message, args) : message;
        }

        private string GetTriggerOptions(IPlayer player)
        {
            var speedOptions = GetMessage(player, Lang.HelpSpeedOptions, GetEnumOptions<WorkcartSpeed>());
            var directionOptions = GetMessage(player, Lang.HelpDirectionOptions, GetEnumOptions<WorkcartDirection>());
            var trackSelectionOptions = GetMessage(player, Lang.HelpTrackSelectionOptions, GetEnumOptions<WorkcartTrackSelection>());
            var otherOptions = GetMessage(player, Lang.HelpOtherOptions);

            return $"{speedOptions}\n{directionOptions}\n{trackSelectionOptions}\n{otherOptions}";
        }

        private string GetTriggerPrefix(IPlayer player, WorkcartTriggerType triggerType) =>
            GetMessage(player, triggerType == WorkcartTriggerType.Tunnel ? Lang.InfoTriggerTunnelPrefix : Lang.InfoTriggerMapPrefix);

        private string GetTriggerPrefix(IPlayer player, WorkcartTriggerInfo triggerInfo) =>
            GetTriggerPrefix(player, triggerInfo.TriggerType);

        private string GetTriggerPrefix(BasePlayer player, WorkcartTriggerType triggerType) =>
            GetTriggerPrefix(player.IPlayer, triggerType);

        private string GetTriggerPrefix(BasePlayer player, WorkcartTriggerInfo triggerInfo) =>
            GetTriggerPrefix(player.IPlayer, triggerInfo.TriggerType);

        private string GetConductorCountMessage(IPlayer player) =>
             _pluginConfig.MaxConductors >= 0
             ? GetMessage(player, Lang.InfoConductorCountLimited, _workcartManager.NumWorkcarts, _pluginConfig.MaxConductors)
             : GetMessage(player, Lang.InfoConductorCountUnlimited, _workcartManager.NumWorkcarts);

        private class Lang
        {
            public const string ErrorNoPermission = "Error.NoPermission";
            public const string ErrorNoTriggers = "Error.NoTriggers";
            public const string ErrorTriggerNotFound = "Error.TriggerNotFound";
            public const string ErrorNoTrackFound = "Error.ErrorNoTrackFound";
            public const string ErrorNoWorkcartFound = "Error.NoWorkcartFound";
            public const string ErrorAutomateBlocked = "Error.AutomateBlocked";
            public const string ErrorUnsupportedTunnel = "Error.UnsupportedTunnel";
            public const string ErrorTunnelTypeDisabled = "Error.TunnelTypeDisabled";
            public const string ErrorMapTriggersDisabled = "Error.MapTriggersDisabled";
            public const string ErrorMaxConductors = "Error.MaxConductors";
            public const string ErrorWorkcartOwned = "Error.WorkcartOwned";
            public const string ErrorNoAutomatedWorkcarts = "Error.NoAutomatedWorkcarts";

            public const string ToggleOnSuccess = "Toggle.Success.On";
            public const string ToggleOffSuccess = "Toggle.Success.Off";
            public const string ShowMarkersSuccess = "ShowMarkers.Success";

            public const string AddTriggerSyntax = "AddTrigger.Syntax";
            public const string AddTriggerSuccess = "AddTrigger.Success";
            public const string MoveTriggerSuccess = "MoveTrigger.Success";
            public const string UpdateTriggerSyntax = "UpdateTrigger.Syntax";
            public const string UpdateTriggerSuccess = "UpdateTrigger.Success";
            public const string RemoveTriggerSyntax = "RemoveTrigger.Syntax";
            public const string RemoveTriggerSuccess = "RemoveTrigger.Success";

            public const string InfoConductorCountLimited = "Info.ConductorCount.Limited";
            public const string InfoConductorCountUnlimited = "Info.ConductorCount.Unlimited";

            public const string HelpSpeedOptions = "Help.SpeedOptions";
            public const string HelpDirectionOptions = "Help.DirectionOptions";
            public const string HelpTrackSelectionOptions = "Help.TrackSelectionOptions";
            public const string HelpOtherOptions = "Help.OtherOptions";

            public const string InfoTrigger = "Info.Trigger";
            public const string InfoTriggerMapPrefix = "Info.Trigger.Prefix.Map";
            public const string InfoTriggerTunnelPrefix = "Info.Trigger.Prefix.Tunnel";

            public const string InfoTriggerMap = "Info.Trigger.Map";
            public const string InfoTriggerTunnel = "Info.Trigger.Tunnel";
            public const string InfoTriggerAddConductor = "Info.Trigger.Conductor";
            public const string InfoTriggerStopDuration = "Info.Trigger.StopDuration";

            public const string InfoTriggerSpeed = "Info.Trigger.Speed";
            public const string InfoTriggerBrakeToSpeed = "Info.Trigger.BrakeToSpeed";
            public const string InfoTriggerDepartureSpeed = "Info.Trigger.DepartureSpeed";
            public const string InfoTriggerDirection = "Info.Trigger.Direction";
            public const string InfoTriggerDepartureDirection = "Info.Trigger.DepartureDirection";
            public const string InfoTriggerTrackSelection = "Info.Trigger.TrackSelection";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.ErrorNoPermission] = "You don't have permission to do that.",
                [Lang.ErrorNoTriggers] = "There are no workcart triggers on this map.",
                [Lang.ErrorTriggerNotFound] = "Error: Trigger id #<color=#fd4>{0}{1}</color> not found.",
                [Lang.ErrorNoTrackFound] = "Error: No track found nearby.",
                [Lang.ErrorNoWorkcartFound] = "Error: No workcart found.",
                [Lang.ErrorAutomateBlocked] = "Error: Another plugin blocked automating that workcart.",
                [Lang.ErrorUnsupportedTunnel] = "Error: Not a supported train tunnel.",
                [Lang.ErrorTunnelTypeDisabled] = "Error: Tunnel type <color=#fd4>{0}</color> is currently disabled.",
                [Lang.ErrorMapTriggersDisabled] = "Error: Map triggers are disabled.",
                [Lang.ErrorMaxConductors] = "Error: There are already <color=#fd4>{0}</color> out of <color=#fd4>{1}</color> conductors.",
                [Lang.ErrorWorkcartOwned] = "Error: That workcart has an owner.",
                [Lang.ErrorNoAutomatedWorkcarts] = "Error: There are no automated workcarts.",

                [Lang.ToggleOnSuccess] = "That workcart is now automated.",
                [Lang.ToggleOffSuccess] = "That workcart is no longer automated.",
                [Lang.ShowMarkersSuccess] = "Showing map markers of all <color=#fd4>{0}</color> automated workcarts for <color=#fd4>{1}</color>. Only you can see them.",

                [Lang.AddTriggerSyntax] = "Syntax: <color=#fd4>{0} <option1> <option2> ...</color>\n{1}",
                [Lang.AddTriggerSuccess] = "Successfully added trigger #<color=#fd4>{0}{1}</color>.",
                [Lang.UpdateTriggerSyntax] = "Syntax: <color=#fd4>{0} <id> <option1> <option2> ...</color>\n{1}",
                [Lang.UpdateTriggerSuccess] = "Successfully updated trigger #<color=#fd4>{0}{1}</color>",
                [Lang.MoveTriggerSuccess] = "Successfully moved trigger #<color=#fd4>{0}{1}</color>",
                [Lang.RemoveTriggerSyntax] = "Syntax: <color=#fd4>{0} <id></color>",
                [Lang.RemoveTriggerSuccess] = "Trigger #<color=#fd4>{0}{1}</color> successfully removed.",

                [Lang.InfoConductorCountLimited] = "Total conductors: <color=#fd4>{0}/{1}</color>.",
                [Lang.InfoConductorCountUnlimited] = "Total conductors: <color=#fd4>{0}</color>.",

                [Lang.HelpSpeedOptions] = "Speeds: {0}",
                [Lang.HelpDirectionOptions] = "Directions: {0}",
                [Lang.HelpTrackSelectionOptions] = "Track selection: {0}",
                [Lang.HelpOtherOptions] = "Other options: <color=#fd4>Conductor</color> | <color=#fd4>Brake</color>",

                [Lang.InfoTrigger] = "Workcart Trigger #{0}{1}",
                [Lang.InfoTriggerMapPrefix] = "M",
                [Lang.InfoTriggerTunnelPrefix] = "T",

                [Lang.InfoTriggerMap] = "Map-specific",
                [Lang.InfoTriggerTunnel] = "Tunnel type: {0} (x{1})",
                [Lang.InfoTriggerAddConductor] = "Adds Conductor",
                [Lang.InfoTriggerStopDuration] = "Stop duration: {0}s",

                [Lang.InfoTriggerSpeed] = "Speed: {0}",
                [Lang.InfoTriggerBrakeToSpeed] = "Brake to speed: {0}",
                [Lang.InfoTriggerDepartureSpeed] = "Departure speed: {0}",
                [Lang.InfoTriggerDirection] = "Direction: {0}",
                [Lang.InfoTriggerDepartureDirection] = "Departure direction: {0}",
                [Lang.InfoTriggerTrackSelection] = "Track selection: {0}",
            }, this, "en");
        }

        #endregion
    }
}
