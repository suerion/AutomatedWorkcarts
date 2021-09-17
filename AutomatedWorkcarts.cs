using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Rust;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using UnityEngine;
using static TrainEngine;
using static TrainTrackSpline;

namespace Oxide.Plugins
{
    [Info("Automated Workcarts", "WhiteThunder", "0.23.0")]
    [Description("Automates workcarts with NPC conductors.")]
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

        private const string ShopkeeperPrefab = "assets/prefabs/npc/bandit/shopkeepers/bandit_shopkeeper.prefab";
        private const string GenericMapMarkerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string VendingMapMarkerPrefab = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";

        private WorkcartTriggerManager _triggerManager = new WorkcartTriggerManager();
        private AutomatedWorkcartManager _workcartManager = new AutomatedWorkcartManager();

        private ProtectionProperties _immortalProtection;
        private Coroutine _startupCoroutine;

        #endregion

        #region Hooks

        private void Init()
        {
            _pluginInstance = this;
            _pluginData = StoredPluginData.Load();
            _tunnelData = StoredTunnelData.Load();

            permission.RegisterPermission(PermissionToggle, this);
            permission.RegisterPermission(PermissionManageTriggers, this);

            if (!_pluginConfig.GenericMapMarker.Enabled)
                Unsubscribe(nameof(OnPlayerConnected));
        }

        private void OnServerInitialized()
        {
            _tunnelData.MigrateTriggers();
            _mapData = StoredMapData.Load();

            _immortalProtection = ScriptableObject.CreateInstance<ProtectionProperties>();
            _immortalProtection.name = "AutomatedWorkcartsProtection";
            _immortalProtection.Add(1);

            _startupCoroutine = ServerMgr.Instance.StartCoroutine(DoStartupRoutine());
        }

        private void Unload()
        {
            if (_startupCoroutine != null)
                ServerMgr.Instance.StopCoroutine(_startupCoroutine);

            OnServerSave();
            _triggerManager.DestroyAll();
            TrainController.DestroyAll();

            UnityEngine.Object.Destroy(_immortalProtection);

            _mapData = null;
            _pluginData = null;
            _tunnelData = null;
            _pluginConfig = null;
            _pluginInstance = null;
        }

        private void OnServerSave()
        {
            _workcartManager.UpdateWorkcartData();
            _pluginData.Save();
        }

        private void OnNewSave()
        {
            _pluginData = StoredPluginData.Clear();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player.IsReceivingSnapshot)
            {
                timer.Once(1f, () => OnPlayerConnected(player));
                return;
            }

            _workcartManager.ResendAllGenericMarkers();
        }

        private void OnEntityKill(TrainEngine workcart)
        {
            if (workcart == null || workcart.net == null)
                return;

            _workcartManager.Unregister(workcart);
        }

        private void OnEntityEnter(WorkcartTrigger trigger, TrainEngine workcart)
        {
            var trainController = TrainController.GetForWorkcart(workcart);
            if (trainController == null)
            {
                if (trigger.TriggerData.AddConductor
                    && !trigger.TriggerData.Destroy
                    && !IsWorkcartOwned(workcart)
                    && CanHaveMoreConductors())
                {
                    TryAddTrainController(workcart, trigger.TriggerData);
                }

                return;
            }

            if (trigger.entityContents?.Contains(workcart) ?? false)
                return;

            trainController.HandleWorkcartTrigger(trigger.TriggerData);
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

                var forwardController = TrainController.GetForWorkcart(forwardWorkcart);
                var backController = TrainController.GetForWorkcart(backwardWorkcart);

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
                    LogWarning($"Destroying non-automated workcart due to insufficient speed.");
                    ScheduleDestroyWorkcart(forwardWorkcart);
                    return;
                }

                if (backController != null)
                {
                    if ((backwardWorkcart.transform.position - forwardWorkcart.transform.position).sqrMagnitude < 9)
                    {
                        LogWarning($"Destroying automated workcart due to it somehow getting inside another workcart.");
                        backController.ScheduleDestruction();
                        return;
                    }

                    backController.StartChilling();
                }
            }
            else
            {
                // Going opposite directions or perpendicular.
                var controller = TrainController.GetForWorkcart(workcart);
                var otherController = TrainController.GetForWorkcart(otherWorkcart);

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
                        LogWarning($"Destroying non-automated workcart due to head-on collision with an automated workcart.");
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
                        LogWarning($"Destroying non-automated workcart due to head-on collision with an automated workcart.");
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
                LogWarning($"Destroying automated workcart due to head-on collision with another.");
                if (GetFasterWorkcart(workcart, otherWorkcart) == workcart)
                    otherController.ScheduleDestruction();
                else
                    controller.ScheduleDestruction();
            }
        }

        #endregion

        #region Commands

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

            var trainController = TrainController.GetForWorkcart(workcart);
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

                WorkcartData workcartData = null;

                if (args.Length > 0)
                {
                    var routeName = GetRouteNameFromArg(player, args[0], requirePrefix: false);
                    if (!string.IsNullOrWhiteSpace(routeName))
                        workcartData = new WorkcartData { Route = routeName };
                }

                if (TryAddTrainController(workcart, workcartData: workcartData))
                {
                    var baseMessage = workcartData != null
                        ? GetMessage(player, Lang.ToggleOnWithRouteSuccess, workcartData.Route)
                        : GetMessage(player, Lang.ToggleOnSuccess);

                    player.Reply(baseMessage + " " + GetConductorCountMessage(player));

                    if (player.HasPermission(PermissionManageTriggers))
                    {
                        if (workcartData?.Route != null)
                            _triggerManager.SetPlayerDisplayedRoute(basePlayer, workcartData.Route);

                        _triggerManager.ShowAllRepeatedly(basePlayer);
                    }
                }
                else
                {
                    ReplyToPlayer(player, Lang.ErrorAutomateBlocked);
                }
            }
            else
            {
                RemoveTrainController(workcart);
                player.Reply(GetMessage(player, Lang.ToggleOffSuccess) + " " + GetConductorCountMessage(player));
            }
        }

        [Command("aw.resetall")]
        private void CommandResetWorkcarts(IPlayer player, string cmd, string[] args)
        {
            if (!player.IsServer
                && !VerifyPermission(player, PermissionToggle))
                return;

            var numReset = _workcartManager.ResetAll();
            ReplyToPlayer(player, Lang.ResetAllSuccess, numReset);
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

            var triggerData = new TriggerData() { Position = trackPosition };
            AddTriggerShared(player, cmd, args, triggerData);
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

            var triggerData = new TriggerData()
            {
                TunnelType = dungeonCellWrapper.TunnelType.ToString(),
                Position = dungeonCellWrapper.InverseTransformPoint(trackPosition),
            };

            AddTriggerShared(player, cmd, args, triggerData);
        }

        private void AddTriggerShared(IPlayer player, string cmd, string[] args, TriggerData triggerData)
        {
            foreach (var arg in args)
            {
                if (!VerifyValidArgAndModifyTrigger(player, cmd, arg, triggerData, Lang.AddTriggerSyntax))
                    return;
            }

            if (!triggerData.AddConductor
                && !triggerData.Destroy
                && triggerData.GetTrackSelectionInstruction() == null
                && triggerData.GetSpeedInstruction() == null
                && triggerData.GetDirectionInstruction() == null)
            {
                triggerData.Speed = EngineSpeeds.Zero.ToString();
            }

            var basePlayer = player.Object as BasePlayer;

            _triggerManager.AddTrigger(triggerData);

            if (triggerData.Route != null)
                _triggerManager.SetPlayerDisplayedRoute(basePlayer, triggerData.Route);

            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.AddTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.updatetrigger", "awt.update")]
        private void CommandUpdateTrigger(IPlayer player, string cmd, string[] args)
        {
            TriggerData triggerData;
            string[] optionArgs;
            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.UpdateTriggerSyntax, out triggerData, out optionArgs))
                return;

            if (optionArgs.Length == 0)
            {
                ReplyToPlayer(player, Lang.UpdateTriggerSyntax, cmd, GetTriggerOptions(player));
                return;
            }

            var newTriggerData = triggerData.Clone();
            foreach (var arg in optionArgs)
            {
                if (!VerifyValidArgAndModifyTrigger(player, cmd, arg, newTriggerData, Lang.UpdateTriggerSyntax))
                    return;
            }

            _triggerManager.UpdateTrigger(triggerData, newTriggerData);

            var basePlayer = player.Object as BasePlayer;
            if (triggerData.Route != null)
                _triggerManager.SetPlayerDisplayedRoute(basePlayer, triggerData.Route);

            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.UpdateTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.replacetrigger", "awt.replace")]
        private void CommandReplaceTrigger(IPlayer player, string cmd, string[] args)
        {
            TriggerData triggerData;
            string[] optionArgs;
            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.UpdateTriggerSyntax, out triggerData, out optionArgs))
                return;

            if (optionArgs.Length == 0)
            {
                ReplyToPlayer(player, Lang.UpdateTriggerSyntax, cmd, GetTriggerOptions(player));
                return;
            }

            var newTriggerData = new TriggerData();
            foreach (var arg in optionArgs)
            {
                if (!VerifyValidArgAndModifyTrigger(player, cmd, arg, newTriggerData, Lang.UpdateTriggerSyntax))
                    return;
            }

            _triggerManager.UpdateTrigger(triggerData, newTriggerData);

            var basePlayer = player.Object as BasePlayer;
            if (triggerData.Route != null)
                _triggerManager.SetPlayerDisplayedRoute(basePlayer, triggerData.Route);

            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.UpdateTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.enabletrigger", "awt.enable")]
        private void CommandEnableTrigger(IPlayer player, string cmd, string[] args)
        {
            TriggerData triggerData;
            string[] optionArgs;
            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.SimpleTriggerSyntax, out triggerData, out optionArgs))
                return;

            var newTriggerData = triggerData.Clone();
            newTriggerData.Enabled = true;
            _triggerManager.UpdateTrigger(triggerData, newTriggerData);

            var basePlayer = player.Object as BasePlayer;
            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.UpdateTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.disabletrigger", "awt.disable")]
        private void CommandDisableTrigger(IPlayer player, string cmd, string[] args)
        {
            TriggerData triggerData;
            string[] optionArgs;
            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.SimpleTriggerSyntax, out triggerData, out optionArgs))
                return;

            var newTriggerData = triggerData.Clone();
            newTriggerData.Enabled = false;
            _triggerManager.UpdateTrigger(triggerData, newTriggerData);

            var basePlayer = player.Object as BasePlayer;
            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.UpdateTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.movetrigger", "awt.move")]
        private void CommandMoveTrigger(IPlayer player, string cmd, string[] args)
        {
            TriggerData triggerData;
            string[] optionArgs;

            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.SimpleTriggerSyntax, out triggerData, out optionArgs))
                return;

            Vector3 trackPosition;
            if (!VerifyTrackPosition(player, out trackPosition))
                return;

            if (triggerData.TriggerType == WorkcartTriggerType.Tunnel)
            {
                DungeonCellWrapper dungeonCellWrapper;
                if (!VerifySupportedNearbyTrainTunnel(player, trackPosition, out dungeonCellWrapper))
                    return;

                if (dungeonCellWrapper.TunnelType != triggerData.GetTunnelType())
                {
                    ReplyToPlayer(player, Lang.ErrorUnsupportedTunnel);
                    return;
                }

                trackPosition = dungeonCellWrapper.InverseTransformPoint(trackPosition);
            }

            _triggerManager.MoveTrigger(triggerData, trackPosition);

            var basePlayer = player.Object as BasePlayer;
            if (triggerData.Route != null)
                _triggerManager.SetPlayerDisplayedRoute(basePlayer, triggerData.Route);

            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.MoveTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.removetrigger", "awt.remove")]
        private void CommandRemoveTrigger(IPlayer player, string cmd, string[] args)
        {
            TriggerData triggerData;
            string[] optionArgs;

            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.SimpleTriggerSyntax, out triggerData, out optionArgs))
                return;

            _triggerManager.RemoveTrigger(triggerData);

            var basePlayer = player.Object as BasePlayer;
            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.RemoveTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.showtriggers", "awt.show")]
        private void CommandShowStops(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer
                || !VerifyPermission(player, PermissionManageTriggers)
                || !VerifyAnyTriggers(player))
                return;

            int duration = 60;
            string routeName = null;

            foreach (var arg in args)
            {
                if (duration == 60)
                {
                    int argIntValue;
                    if (int.TryParse(arg, out argIntValue))
                    {
                        duration = argIntValue;
                        continue;
                    }
                }

                if (routeName == null)
                {
                    var routeNameArg = GetRouteNameFromArg(player, arg, requirePrefix: false);
                    if (!string.IsNullOrWhiteSpace(routeNameArg))
                        routeName = routeNameArg;
                }
            }

            var basePlayer = player.Object as BasePlayer;

            _triggerManager.SetPlayerDisplayedRoute(basePlayer, routeName);
            _triggerManager.ShowAllRepeatedly(basePlayer, duration);

            if (routeName != null)
                ReplyToPlayer(player, Lang.ShowTriggersWithRouteSuccess, routeName, FormatTime(duration));
            else
                ReplyToPlayer(player, Lang.ShowTriggersSuccess, FormatTime(duration));
        }

        #endregion

        #region API

        private bool API_AutomateWorkcart(TrainEngine workcart)
        {
            return IsWorkcartAutomated(workcart)
                ? true
                : TryAddTrainController(workcart);
        }

        private void API_StopAutomatingWorkcart(TrainEngine workcart, bool immediate = false)
        {
            RemoveTrainController(workcart, immediate);
        }

        private bool API_IsWorkcartAutomated(TrainEngine workcart)
        {
            return IsWorkcartAutomated(workcart);
        }

        private TrainEngine[] API_GetAutomatedWorkcarts()
        {
            return _workcartManager.GetWorkcarts();
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

        private bool VerifyTriggerExists(IPlayer player, int triggerId, WorkcartTriggerType triggerType, out TriggerData triggerData)
        {
            triggerData = _triggerManager.FindTrigger(triggerId, triggerType);
            if (triggerData != null)
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

            if (arg.StartsWith("#"))
                arg = arg.Substring(1);

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

        private bool VerifyValidTrigger(IPlayer player, string cmd, string[] args, string errorMessageName, out TriggerData triggerData, out string[] optionArgs)
        {
            var basePlayer = player.Object as BasePlayer;
            optionArgs = args;
            triggerData = null;

            int triggerId;
            WorkcartTriggerType triggerType;
            if (args.Length > 0 && IsTriggerArg(player, args[0], out triggerId, out triggerType))
            {
                optionArgs = args.Skip(1).ToArray();
                return VerifyTriggerExists(player, triggerId, triggerType, out triggerData);
            }

            triggerData = _triggerManager.FindNearestTriggerWhereAiming(basePlayer);
            if (triggerData != null)
                return true;

            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, errorMessageName, cmd, GetTriggerOptions(player));
            return false;
        }

        private bool VerifyCanModifyTrigger(IPlayer player, string cmd, string[] args, string errorMessageName, out TriggerData triggerData, out string[] optionArgs)
        {
            triggerData = null;
            optionArgs = null;

            if (player.IsServer
                || !VerifyPermission(player, PermissionManageTriggers)
                || !VerifyAnyTriggers(player))
                return false;

            return VerifyValidTrigger(player, cmd, args, errorMessageName, out triggerData, out optionArgs);
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

        private string GetRouteNameFromArg(IPlayer player, string routeName, bool requirePrefix = true)
        {
            if (routeName.StartsWith("@"))
                return routeName.Substring(1);

            return requirePrefix ? null : routeName;
        }

        private bool VerifyValidArgAndModifyTrigger(IPlayer player, string cmd, string arg, TriggerData triggerData, string errorMessageName)
        {
            var argLower = arg.ToLower();
            if (argLower == "start" || argLower == "conductor")
            {
                triggerData.AddConductor = true;
                return true;
            }

            if (argLower == "brake")
            {
                triggerData.Brake = true;
                return true;
            }

            if (argLower == "destroy")
            {
                triggerData.Destroy = true;
                return true;
            }

            if (argLower == "enabled")
            {
                triggerData.Enabled = true;
                return true;
            }

            if (argLower == "disabled")
            {
                triggerData.Enabled = false;
                return true;
            }

            float stopDuration;
            if (float.TryParse(arg, out stopDuration))
            {
                triggerData.StopDuration = stopDuration;
                return true;
            }

            var routeName = GetRouteNameFromArg(player, arg, requirePrefix: true);
            if (!string.IsNullOrWhiteSpace(routeName))
            {
                triggerData.Route = routeName;
                return true;
            }

            SpeedInstruction speedInstruction;
            if (Enum.TryParse<SpeedInstruction>(arg, true, out speedInstruction))
            {
                var speedString = speedInstruction.ToString();

                // If zero speed is already set, assume this is the departure speed.
                if (triggerData.Speed == SpeedInstruction.Zero.ToString())
                    triggerData.DepartureSpeed = speedString;
                else
                    triggerData.Speed = speedString;

                return true;
            }

            DirectionInstruction directionInstruction;
            if (Enum.TryParse<DirectionInstruction>(arg, true, out directionInstruction))
            {
                triggerData.Direction = directionInstruction.ToString();
                return true;
            }

            TrackSelectionInstruction trackSelectionInstruction;
            if (Enum.TryParse<TrackSelectionInstruction>(arg, true, out trackSelectionInstruction))
            {
                triggerData.TrackSelection = trackSelectionInstruction.ToString();
                return true;
            }

            ReplyToPlayer(player, errorMessageName, cmd, GetTriggerOptions(player));
            return false;
        }

        #endregion

        #region Helper Methods

        private IEnumerator DoStartupRoutine()
        {
            _pluginInstance.TrackStart();
            yield return _triggerManager.CreateAll();

            var foundWorkcartIds = new HashSet<uint>();
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var workcart = entity as TrainEngine;
                if (workcart == null)
                    continue;

                var workcartData = _pluginData.GetWorkcartData(workcart.net.ID);
                if (workcartData != null)
                {
                    foundWorkcartIds.Add(workcart.net.ID);
                    timer.Once(UnityEngine.Random.Range(0, 1f), () =>
                    {
                        if (workcart != null
                            && !IsWorkcartOwned(workcart)
                            && CanHaveMoreConductors()
                            && !IsWorkcartAutomated(workcart))
                            TryAddTrainController(workcart, workcartData: workcartData);
                    });
                }
            }

            _pluginData.TrimToWorkcartIds(foundWorkcartIds);
            _pluginInstance.TrackEnd();
        }

        private static bool AutomationWasBlocked(TrainEngine workcart)
        {
            object hookResult = Interface.CallHook("OnWorkcartAutomationStart", workcart);
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

        private static bool IsWorkcartAutomated(TrainEngine workcart) =>
            TrainController.GetForWorkcart(workcart) != null;

        private static bool TryAddTrainController(TrainEngine workcart, TriggerData triggerData = null, WorkcartData workcartData = null)
        {
            if (AutomationWasBlocked(workcart))
                return false;

            if (workcartData == null)
            {
                workcartData = new WorkcartData
                {
                    Route = triggerData?.Route,
                };
            }

            var trainController = TrainController.AddToWorkcart(workcart, workcartData);
            if (triggerData != null)
                trainController.HandleConductorTrigger(triggerData);

            _pluginInstance._workcartManager.Register(workcart, workcartData);
            Interface.CallHook("OnWorkcartAutomationStarted", workcart);

            return true;
        }

        private static void RemoveTrainController(TrainEngine workcart, bool immediate = false)
        {
            var controller = TrainController.GetForWorkcart(workcart);
            if (controller == null)
                return;

            if (immediate)
                UnityEngine.Object.DestroyImmediate(controller);
            else
                UnityEngine.Object.Destroy(controller);

            _pluginInstance._workcartManager.Unregister(workcart);
            Interface.CallHook("OnWorkcartAutomationStopped", workcart);
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
            DungeonGridCell closestDungeon = null;
            var shortestSqrDistance = float.MaxValue;

            foreach (var dungeon in TerrainMeta.Path.DungeonGridCells)
            {
                var dungeonCellWrapper = new DungeonCellWrapper(dungeon);
                if (dungeonCellWrapper.TunnelType == TunnelType.Unsupported)
                    continue;

                if (!dungeonCellWrapper.IsInBounds(position))
                    continue;

                var sqrDistance = (dungeon.transform.position - position).sqrMagnitude;
                if (sqrDistance < shortestSqrDistance)
                {
                    shortestSqrDistance = sqrDistance;
                    closestDungeon = dungeon;
                }
            }

            return closestDungeon == null ? null : new DungeonCellWrapper(closestDungeon);
        }

        private static List<DungeonCellWrapper> FindAllTunnelsOfType(TunnelType tunnelType)
        {
            var dungeonCellList = new List<DungeonCellWrapper>();

            foreach (var dungeonCell in TerrainMeta.Path.DungeonGridCells)
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
            var trainController = TrainController.GetForWorkcart(workcart);
            var speed = trainController != null
                ? trainController.IntendedCurrentVelocity
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

        #endregion

        #region Dungeon Cells

        private enum TunnelType
        {
            // Don't rename these since the names are persisted in data files.
            TrainStation,
            BarricadeTunnel,
            LootTunnel,
            Intersection,
            LargeIntersection,
            Unsupported
        }

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

            ["straight-sn-0"] = Quaternion.identity,
            ["straight-sn-1"] = Quaternion.Euler(0, 180, 0),
            ["straight-we-0"] = Quaternion.Euler(0, -90, 0),
            ["straight-we-1"] = Quaternion.Euler(0, 90, 0),

            ["straight-sn-4"] = Quaternion.identity,
            ["straight-sn-5"] = Quaternion.Euler(0, 180, 0),
            ["straight-we-4"] = Quaternion.Euler(0, -90, 0),
            ["straight-we-5"] = Quaternion.Euler(0, 90, 0),

            ["intersection-n"] = Quaternion.identity,
            ["intersection-e"] = Quaternion.Euler(0, 90, 0),
            ["intersection-s"] = Quaternion.Euler(0, 180, 0),
            ["intersection-w"] = Quaternion.Euler(0, -90, 0),

            ["intersection"] = Quaternion.identity,
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

            ["straight-sn-0"] = TunnelType.LootTunnel,
            ["straight-sn-1"] = TunnelType.LootTunnel,
            ["straight-we-0"] = TunnelType.LootTunnel,
            ["straight-we-1"] = TunnelType.LootTunnel,

            ["straight-sn-4"] = TunnelType.BarricadeTunnel,
            ["straight-sn-5"] = TunnelType.BarricadeTunnel,
            ["straight-we-4"] = TunnelType.BarricadeTunnel,
            ["straight-we-5"] = TunnelType.BarricadeTunnel,

            ["intersection-n"] = TunnelType.Intersection,
            ["intersection-e"] = TunnelType.Intersection,
            ["intersection-s"] = TunnelType.Intersection,
            ["intersection-w"] = TunnelType.Intersection,

            ["intersection"] = TunnelType.LargeIntersection,
        };

        private static readonly Dictionary<TunnelType, Vector3> DungeonCellDimensions = new Dictionary<TunnelType, Vector3>()
        {
            [TunnelType.TrainStation] = new Vector3(108, 8.5f, 216),
            [TunnelType.BarricadeTunnel] = new Vector3(16.5f, 8.5f, 216),
            [TunnelType.LootTunnel] = new Vector3(16.5f, 8.5f, 216),
            [TunnelType.Intersection] = new Vector3(216, 8.5f, 216),
            [TunnelType.LargeIntersection] = new Vector3(216, 8.5f, 216),
        };

        private class DungeonCellWrapper
        {
            public static TunnelType GetTunnelType(DungeonGridCell dungeonCell) =>
                GetTunnelType(GetShortName(dungeonCell.name));

            private static TunnelType GetTunnelType(string shortName)
            {
                TunnelType tunnelType;
                return DungeonCellTypes.TryGetValue(shortName, out tunnelType)
                    ? tunnelType
                    : TunnelType.Unsupported;
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

            public DungeonCellWrapper(DungeonGridCell dungeonCell)
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

        private enum SpeedInstruction
        {
            // Don't rename these since the names are persisted in data files.
            Zero = 0,
            Lo = 1,
            Med = 2,
            Hi = 3
        }

        private enum DirectionInstruction
        {
            // Don't rename these since the names are persisted in data files.
            Fwd,
            Rev,
            Invert
        }

        private enum TrackSelectionInstruction
        {
            // Don't rename these since the names are persisted in data files.
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

        private static EngineSpeeds DetermineNextVelocity(EngineSpeeds throttleSpeed, SpeedInstruction? desiredSpeed, DirectionInstruction? directionInstruction)
        {
            // -3 to 3
            var signedSpeed = EngineSpeedToNumber(throttleSpeed);

            // 0, 1, 2, 3
            var unsignedSpeed = Math.Abs(signedSpeed);

            // 1 or -1
            var sign = signedSpeed == 0 ? 1 : signedSpeed / unsignedSpeed;

            if (directionInstruction == DirectionInstruction.Fwd)
                sign = 1;
            else if (directionInstruction == DirectionInstruction.Rev)
                sign = -1;
            else if (directionInstruction == DirectionInstruction.Invert)
                sign *= -1;

            if (desiredSpeed == SpeedInstruction.Hi)
                unsignedSpeed = 3;
            else if (desiredSpeed == SpeedInstruction.Med)
                unsignedSpeed = 2;
            else if (desiredSpeed == SpeedInstruction.Lo)
                unsignedSpeed = 1;
            else if (desiredSpeed == SpeedInstruction.Zero)
                unsignedSpeed = 0;

            return EngineSpeedFromNumber(sign * unsignedSpeed);
        }

        private static TrackSelection DetermineNextTrackSelection(TrackSelection trackSelection, TrackSelectionInstruction? trackSelectionInstruction)
        {
            switch (trackSelectionInstruction)
            {
                case TrackSelectionInstruction.Default:
                    return TrackSelection.Default;

                case TrackSelectionInstruction.Left:
                    return TrackSelection.Left;

                case TrackSelectionInstruction.Right:
                    return TrackSelection.Right;

                case TrackSelectionInstruction.Swap:
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
            public TriggerData TriggerData;
        }

        private class TriggerData
        {
            [JsonProperty("Id")]
            public int Id;

            [JsonProperty("Position")]
            public Vector3 Position;

            [JsonProperty("Enabled", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(true)]
            public bool Enabled = true;

            [JsonProperty("Route", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Route;

            [JsonProperty("TunnelType", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string TunnelType;

            [JsonProperty("AddConductor", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool AddConductor = false;

            [JsonProperty("Brake", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool Brake = false;

            [JsonProperty("Destroy", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool Destroy = false;

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

                if (!string.IsNullOrWhiteSpace(TunnelType))
                {
                    TunnelType tunnelType;
                    if (Enum.TryParse<TunnelType>(TunnelType, out tunnelType))
                        _tunnelType = tunnelType;
                }

                return (TunnelType)_tunnelType;
            }

            private SpeedInstruction? _speedInstruction;
            public SpeedInstruction? GetSpeedInstruction()
            {
                if (_speedInstruction == null && !string.IsNullOrWhiteSpace(Speed))
                {
                    SpeedInstruction speed;
                    if (Enum.TryParse<SpeedInstruction>(Speed, out speed))
                        _speedInstruction = speed;
                }

                // Ensure there is a target speed when braking.
                return Brake ? _speedInstruction ?? SpeedInstruction.Zero : _speedInstruction;
            }

            private DirectionInstruction? _directionInstruction;
            public DirectionInstruction? GetDirectionInstruction()
            {
                if (_directionInstruction == null && !string.IsNullOrWhiteSpace(Direction))
                {
                    DirectionInstruction direction;
                    if (Enum.TryParse<DirectionInstruction>(Direction, out direction))
                        _directionInstruction = direction;
                }

                return _directionInstruction;
            }

            private TrackSelectionInstruction? _trackSelectionInstruction;
            public TrackSelectionInstruction? GetTrackSelectionInstruction()
            {
                if (_trackSelectionInstruction == null && !string.IsNullOrWhiteSpace(TrackSelection))
                {
                    TrackSelectionInstruction trackSelection;
                    if (Enum.TryParse<TrackSelectionInstruction>(TrackSelection, out trackSelection))
                        _trackSelectionInstruction = trackSelection;
                }

                return _trackSelectionInstruction;
            }

            private SpeedInstruction? _departureSpeedInstruction;
            public SpeedInstruction? GetDepartureSpeedInstruction()
            {
                if (_departureSpeedInstruction == null && !string.IsNullOrWhiteSpace(Speed))
                {
                    SpeedInstruction speed;
                    if (Enum.TryParse<SpeedInstruction>(DepartureSpeed, out speed))
                        _departureSpeedInstruction = speed;
                }

                return _departureSpeedInstruction ?? SpeedInstruction.Med;
            }

            public bool MatchesRoute(string routeName)
            {
                if (string.IsNullOrWhiteSpace(Route))
                {
                    // Trigger has no specified route so it applies to all workcarts.
                    return true;
                }

                return routeName?.ToLower() == Route.ToLower();
            }

            public void InvalidateCache()
            {
                _speedInstruction = null;
                _directionInstruction = null;
                _trackSelectionInstruction = null;
                _departureSpeedInstruction = null;
            }

            public void CopyFrom(TriggerData triggerData)
            {
                Enabled = triggerData.Enabled;
                Route = triggerData.Route;
                AddConductor = triggerData.AddConductor;
                Brake = triggerData.Brake;
                Destroy = triggerData.Destroy;
                Speed = triggerData.Speed;
                DepartureSpeed = triggerData.DepartureSpeed;
                Direction = triggerData.Direction;
                TrackSelection = triggerData.TrackSelection;
                StopDuration = triggerData.StopDuration;
            }

            public TriggerData Clone()
            {
                var triggerData = new TriggerData();
                triggerData.CopyFrom(this);
                return triggerData;
            }

            public Color GetColor(string routeName)
            {
                if (!Enabled || !MatchesRoute(routeName))
                    return Color.grey;

                if (Destroy)
                    return Color.red;

                if (AddConductor)
                    return Color.cyan;

                var speedInstruction = GetSpeedInstruction();
                var directionInstruction = GetDirectionInstruction();
                var trackSelectionInstruction = GetTrackSelectionInstruction();

                float hue, saturation;

                if (Brake)
                {
                    // Orange
                    hue = 0.5f/6f;
                    saturation = speedInstruction == SpeedInstruction.Zero ? 1
                        : speedInstruction == SpeedInstruction.Lo ? 0.8f
                        : 0.6f;
                    return Color.HSVToRGB(0.5f/6f, saturation, 1);
                }

                if (speedInstruction == SpeedInstruction.Zero)
                    return Color.white;

                if (speedInstruction == null && directionInstruction == null && trackSelectionInstruction != null)
                    return Color.magenta;

                hue = directionInstruction == DirectionInstruction.Fwd
                    ? 1/3f // Green
                    : directionInstruction == DirectionInstruction.Rev
                    ? 0 // Red
                    : directionInstruction == DirectionInstruction.Invert
                    ? 0.5f/6f // Orange
                    : 1/6f; // Yellow

                saturation = speedInstruction == SpeedInstruction.Hi
                    ? 1
                    : speedInstruction == SpeedInstruction.Med
                    ? 0.8f
                    : speedInstruction == SpeedInstruction.Lo
                    ? 0.6f
                    : 1;

                return Color.HSVToRGB(hue, saturation, 1);
            }
        }

        #endregion

        #region Trigger Instances

        private abstract class BaseTriggerInstance
        {
            public TriggerData TriggerData { get; protected set; }
            public TrainTrackSpline Spline { get; private set; }
            public float DistanceOnSpline { get; private set; }

            public abstract Vector3 WorldPosition { get; }

            private GameObject _gameObject;

            protected BaseTriggerInstance(TriggerData triggerData)
            {
                TriggerData = triggerData;
            }

            public void CreateTrigger()
            {
                _gameObject = new GameObject();
                UpdatePosition();

                var sphereCollider = _gameObject.AddComponent<SphereCollider>();
                sphereCollider.isTrigger = true;
                sphereCollider.radius = 1;
                sphereCollider.gameObject.layer = 6;

                var trigger = _gameObject.AddComponent<WorkcartTrigger>();
                trigger.TriggerData = TriggerData;
                trigger.interestLayers = Layers.Mask.Vehicle_World;
            }

            public void Enable()
            {
                if (_gameObject != null)
                    _gameObject.SetActive(true);
                else
                    CreateTrigger();
            }

            public void Disable()
            {
                if (_gameObject != null)
                    _gameObject.SetActive(false);
            }

            public void UpdatePosition()
            {
                if (_gameObject == null)
                    return;

                _gameObject.transform.position = WorldPosition;

                TrainTrackSpline spline;
                float distanceOnSpline;
                if (TrainTrackSpline.TryFindTrackNearby(WorldPosition, 2, out spline, out distanceOnSpline))
                {
                    Spline = spline;
                    DistanceOnSpline = distanceOnSpline;
                }
                else
                {
                    Spline = null;
                    DistanceOnSpline = 0;
                }
            }

            public void Destroy()
            {
                UnityEngine.Object.Destroy(_gameObject);
            }
        }

        private class MapTriggerInstance : BaseTriggerInstance
        {
            public override Vector3 WorldPosition => TriggerData.Position;

            public MapTriggerInstance(TriggerData triggerData) : base(triggerData) {}
        }

        private class TunnelTriggerInstance : BaseTriggerInstance
        {
            public DungeonCellWrapper DungeonCellWrapper { get; private set; }

            public override Vector3 WorldPosition => DungeonCellWrapper.TransformPoint(TriggerData.Position);

            public TunnelTriggerInstance(TriggerData triggerData, DungeonCellWrapper dungeonCellWrapper) : base(triggerData)
            {
                DungeonCellWrapper = dungeonCellWrapper;
            }
        }

        #endregion

        #region Trigger Controllers

        private abstract class BaseTriggerController
        {
            public TriggerData TriggerData { get; protected set; }
            public BaseTriggerInstance[] TriggerInstanceList { get; protected set; }

            public BaseTriggerController(TriggerData triggerData)
            {
                TriggerData = triggerData;
            }

            public abstract void Create();

            public void OnMove()
            {
                foreach (var triggerInstance in TriggerInstanceList)
                    triggerInstance.UpdatePosition();
            }

            public void OnEnableDisable()
            {
                foreach (var triggerInstance in TriggerInstanceList)
                {
                    if (TriggerData.Enabled)
                        triggerInstance.Enable();
                    else
                        triggerInstance.Disable();
                }
            }

            public void Destroy()
            {
                if (TriggerInstanceList == null)
                    return;

                foreach (var triggerInstance in TriggerInstanceList)
                    triggerInstance.Destroy();
            }
        }

        private class MapTriggerController : BaseTriggerController
        {
            public MapTriggerController(TriggerData triggerData) : base(triggerData) {}

            public override void Create()
            {
                var triggerInstance = new MapTriggerInstance(TriggerData);
                TriggerInstanceList = new MapTriggerInstance[] { triggerInstance };

                if (TriggerData.Enabled)
                    triggerInstance.CreateTrigger();
            }
        }

        private class TunnelTriggerController : BaseTriggerController
        {
            public TunnelTriggerController(TriggerData triggerData) : base(triggerData) {}

            public override void Create()
            {
                var matchingDungeonCells = FindAllTunnelsOfType(TriggerData.GetTunnelType());
                TriggerInstanceList = new TunnelTriggerInstance[matchingDungeonCells.Count];

                for (var i = 0; i < matchingDungeonCells.Count; i++)
                {
                    var triggerInstance = new TunnelTriggerInstance(TriggerData, matchingDungeonCells[i]);
                    TriggerInstanceList[i] = triggerInstance;

                    if (TriggerData.Enabled)
                        triggerInstance.CreateTrigger();
                }
            }
        }

        #endregion

        #region Trigger Manager

        private class WorkcartTriggerManager
        {
            private class PlayerInfo
            {
                public Timer Timer;
                public string Route;
            }

            private const float TriggerDisplayDuration = 1f;
            private const float TriggerDisplayRadius = 1f;
            private float TriggerDisplayDistanceSquared => _pluginConfig.TriggerDisplayDistance * _pluginConfig.TriggerDisplayDistance;

            private Dictionary<TriggerData, BaseTriggerController> _triggerControllers = new Dictionary<TriggerData, BaseTriggerController>();
            private Dictionary<TrainTrackSpline, List<BaseTriggerInstance>> _splinesToTriggers = new Dictionary<TrainTrackSpline, List<BaseTriggerInstance>>();
            private Dictionary<ulong, PlayerInfo> _playerInfo = new Dictionary<ulong, PlayerInfo>();

            private int GetHighestTriggerId(IEnumerable<TriggerData> triggerList)
            {
                var highestTriggerId = 0;

                foreach (var triggerData in triggerList)
                    highestTriggerId = Math.Max(highestTriggerId, triggerData.Id);

                return highestTriggerId;
            }

            private void RegisterTriggerWithSpline(BaseTriggerInstance triggerInstance, TrainTrackSpline spline)
            {
                List<BaseTriggerInstance> triggerInstanceList;
                if (!_splinesToTriggers.TryGetValue(spline, out triggerInstanceList))
                {
                    triggerInstanceList = new List<BaseTriggerInstance>() { triggerInstance };
                    _splinesToTriggers[spline] = triggerInstanceList;
                }
                else
                {
                    triggerInstanceList.Add(triggerInstance);
                }
            }

            private void UnregisterTriggerFromSpline(BaseTriggerInstance triggerInstance, TrainTrackSpline spline)
            {
                List<BaseTriggerInstance> triggerInstanceList;
                if (_splinesToTriggers.TryGetValue(spline, out triggerInstanceList))
                {
                    triggerInstanceList.Remove(triggerInstance);
                    if (triggerInstanceList.Count == 0)
                        _splinesToTriggers.Remove(spline);
                }
            }

            private void RegisterTriggerWithSpline(BaseTriggerController triggerController)
            {
                foreach (var triggerInstance in triggerController.TriggerInstanceList)
                {
                    if (triggerInstance.Spline != null)
                        RegisterTriggerWithSpline(triggerInstance, triggerInstance.Spline);
                }
            }

            private void UnregisterTriggerFromSpline(BaseTriggerController triggerController)
            {
                foreach (var triggerInstance in triggerController.TriggerInstanceList)
                {
                    if (triggerInstance.Spline != null)
                        UnregisterTriggerFromSpline(triggerInstance, triggerInstance.Spline);
                }
            }

            public List<BaseTriggerInstance> GetTriggersForSpline(TrainTrackSpline spline)
            {
                List<BaseTriggerInstance> triggerInstanceList;
                return _splinesToTriggers.TryGetValue(spline, out triggerInstanceList)
                    ? triggerInstanceList
                    : null;
            }

            public TriggerData FindTrigger(int triggerId, WorkcartTriggerType triggerType)
            {
                foreach (var triggerData in _triggerControllers.Keys)
                {
                    if (triggerData.TriggerType == triggerType && triggerData.Id == triggerId)
                        return triggerData;
                }

                return null;
            }

            public void AddTrigger(TriggerData triggerData)
            {
                if (triggerData.TriggerType == WorkcartTriggerType.Tunnel)
                {
                    if (triggerData.Id == 0)
                        triggerData.Id = GetHighestTriggerId(_tunnelData.TunnelTriggers) + 1;

                    CreateTunnelTriggerController(triggerData);
                    _tunnelData.AddTrigger(triggerData);
                }
                else
                {
                    if (triggerData.Id == 0)
                        triggerData.Id = GetHighestTriggerId(_mapData.MapTriggers) + 1;

                    CreateMapTriggerController(triggerData);
                    _mapData.AddTrigger(triggerData);
                }
            }

            private void SaveTrigger(TriggerData triggerData)
            {
                if (triggerData.TriggerType == WorkcartTriggerType.Tunnel)
                    _tunnelData.Save();
                else
                    _mapData.Save();
            }

            private BaseTriggerController GetTriggerController(TriggerData triggerData)
            {
                BaseTriggerController triggerController;
                return _triggerControllers.TryGetValue(triggerData, out triggerController)
                    ? triggerController
                    : null;
            }

            public void UpdateTrigger(TriggerData triggerData, TriggerData newTriggerData)
            {
                var triggerController = GetTriggerController(triggerData);
                if (triggerController == null)
                    return;

                var enabledChanged = triggerData.Enabled != newTriggerData.Enabled;
                triggerData.CopyFrom(newTriggerData);
                triggerData.InvalidateCache();

                if (enabledChanged)
                {
                    triggerController.OnEnableDisable();

                    if (triggerData.Enabled)
                        RegisterTriggerWithSpline(triggerController);
                    else
                        UnregisterTriggerFromSpline(triggerController);
                }

                SaveTrigger(triggerData);
            }

            public void MoveTrigger(TriggerData triggerData, Vector3 position)
            {
                var triggerController = GetTriggerController(triggerData);
                if (triggerController == null)
                    return;

                UnregisterTriggerFromSpline(triggerController);
                triggerData.Position = position;
                triggerController.OnMove();
                RegisterTriggerWithSpline(triggerController);
                SaveTrigger(triggerData);
            }

            private void DestroyTriggerController(BaseTriggerController triggerController)
            {
                UnregisterTriggerFromSpline(triggerController);
                triggerController.Destroy();
            }

            public void RemoveTrigger(TriggerData triggerData)
            {
                var triggerController = GetTriggerController(triggerData);
                if (triggerController == null)
                    return;

                DestroyTriggerController(triggerController);
                _triggerControllers.Remove(triggerData);

                if (triggerData.TriggerType == WorkcartTriggerType.Tunnel)
                    _tunnelData.RemoveTrigger(triggerData);
                else
                    _mapData.RemoveTrigger(triggerData);
            }

            private void CreateMapTriggerController(TriggerData triggerData)
            {
                var triggerController = new MapTriggerController(triggerData);
                triggerController.Create();
                RegisterTriggerWithSpline(triggerController);
                _triggerControllers[triggerData] = triggerController;
            }

            private void CreateTunnelTriggerController(TriggerData triggerData)
            {
                var triggerController = new TunnelTriggerController(triggerData);
                triggerController.Create();
                RegisterTriggerWithSpline(triggerController);
                _triggerControllers[triggerData] = triggerController;
            }

            public IEnumerator CreateAll()
            {
                if (_pluginConfig.EnableMapTriggers)
                {
                    foreach (var triggerData in _mapData.MapTriggers)
                    {
                        CreateMapTriggerController(triggerData);
                        _pluginInstance.TrackEnd();
                        yield return CoroutineEx.waitForEndOfFrame;
                        _pluginInstance.TrackStart();
                    }
                }

                foreach (var triggerData in _tunnelData.TunnelTriggers)
                {
                    var tunnelType = triggerData.GetTunnelType();
                    if (tunnelType == TunnelType.Unsupported || !_pluginConfig.IsTunnelTypeEnabled(tunnelType))
                        continue;

                    CreateTunnelTriggerController(triggerData);
                    _pluginInstance.TrackEnd();
                    yield return CoroutineEx.waitForEndOfFrame;
                    _pluginInstance.TrackStart();
                }
            }

            public void DestroyAll()
            {
                foreach (var triggerController in _triggerControllers.Values)
                    DestroyTriggerController(triggerController);

                _triggerControllers.Clear();
                _splinesToTriggers.Clear();
            }

            private PlayerInfo GetOrCreatePlayerInfo(BasePlayer player)
            {
                PlayerInfo playerInfo;
                if (!_playerInfo.TryGetValue(player.userID, out playerInfo))
                {
                    playerInfo = new PlayerInfo();
                    _playerInfo[player.userID] = playerInfo;
                }

                return playerInfo;
            }

            public void SetPlayerDisplayedRoute(BasePlayer player, string routeName)
            {
                GetOrCreatePlayerInfo(player).Route = routeName;
            }

            public void ShowAllRepeatedly(BasePlayer player, int duration = -1)
            {
                var playerInfo = GetOrCreatePlayerInfo(player);

                ShowNearbyTriggers(player, player.transform.position, playerInfo.Route);

                if (!playerInfo.Timer?.Destroyed ?? false)
                {
                    var newDuration = duration >= 0 ? duration : Math.Max(playerInfo.Timer.Repetitions, 60);
                    playerInfo.Timer.Reset(delay: -1, repetitions: newDuration);
                    return;
                }

                playerInfo.Timer = _pluginInstance.timer.Repeat(TriggerDisplayDuration - 0.2f, duration, () =>
                {
                    ShowNearbyTriggers(player, player.transform.position, playerInfo.Route);
                });
            }

            private void ShowNearbyTriggers(BasePlayer player, Vector3 playerPosition, string routeName)
            {
                var isAdmin = player.IsAdmin;
                if (!isAdmin)
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                    player.SendNetworkUpdateImmediate();
                }

                foreach (var triggerController in _triggerControllers.Values)
                {
                    foreach (var triggerInstance in triggerController.TriggerInstanceList)
                    {
                        if ((playerPosition - triggerInstance.WorldPosition).sqrMagnitude <= TriggerDisplayDistanceSquared)
                            ShowTrigger(player, triggerInstance, routeName, triggerController.TriggerInstanceList.Length);
                    }
                }

                if (!isAdmin)
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
                    player.SendNetworkUpdateImmediate();
                }
            }

            private static void ShowTrigger(BasePlayer player, BaseTriggerInstance trigger, string routeName, int count = 1)
            {
                var triggerData = trigger.TriggerData;
                var color = triggerData.GetColor(routeName);

                var spherePosition = trigger.WorldPosition;
                player.SendConsoleCommand("ddraw.sphere", TriggerDisplayDuration, color, spherePosition, TriggerDisplayRadius);

                var triggerPrefix = _pluginInstance.GetTriggerPrefix(player, triggerData);
                var infoLines = new List<string>();

                if (!triggerData.Enabled)
                    infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerrDisabled));

                infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTrigger, triggerPrefix, triggerData.Id));

                if (triggerData.TriggerType == WorkcartTriggerType.Tunnel)
                    infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerTunnel, triggerData.TunnelType, count));
                else
                    infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerMap, triggerData.Id));

                if (!string.IsNullOrWhiteSpace(triggerData.Route))
                    infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerRoute, triggerData.Route));

                if (triggerData.Destroy)
                {
                    infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerDestroy));
                }
                else
                {
                    if (triggerData.AddConductor)
                        infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerAddConductor));

                    var directionInstruction = triggerData.GetDirectionInstruction();
                    var speedInstruction = triggerData.GetSpeedInstruction();

                    // When speed is zero, departure direction will be shown instead of direction.
                    if (directionInstruction != null && speedInstruction != SpeedInstruction.Zero)
                        infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerDirection, directionInstruction));

                    if (speedInstruction != null)
                        infoLines.Add(_pluginInstance.GetMessage(player, triggerData.Brake ? Lang.InfoTriggerBrakeToSpeed : Lang.InfoTriggerSpeed, speedInstruction));

                    if (speedInstruction == SpeedInstruction.Zero)
                        infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerStopDuration, triggerData.GetStopDuration()));

                    var trackSelectionInstruction = triggerData.GetTrackSelectionInstruction();
                    if (trackSelectionInstruction != null)
                        infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerTrackSelection, trackSelectionInstruction));

                    if (directionInstruction != null && speedInstruction == SpeedInstruction.Zero)
                        infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerDepartureDirection, directionInstruction));

                    var departureSpeedInstruction = triggerData.GetDepartureSpeedInstruction();
                    if (speedInstruction == SpeedInstruction.Zero)
                        infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerDepartureSpeed, departureSpeedInstruction));
                }

                var textPosition = trigger.WorldPosition + new Vector3(0, 1.5f + infoLines.Count * 0.1f, 0);
                player.SendConsoleCommand("ddraw.text", TriggerDisplayDuration, color, textPosition, string.Join("\n", infoLines));
            }

            public BaseTriggerInstance FindNearestTrigger(Vector3 position, float maxDistance = 10)
            {
                BaseTriggerInstance closestTriggerInstance = null;
                float shortestSqrDistance = float.MaxValue;

                foreach (var triggerController in _triggerControllers.Values)
                {
                    foreach (var triggerInstance in triggerController.TriggerInstanceList)
                    {
                        var sqrDistance = (position - triggerInstance.WorldPosition).sqrMagnitude;
                        if (sqrDistance >= shortestSqrDistance || sqrDistance >= maxDistance)
                            continue;

                        shortestSqrDistance = sqrDistance;
                        closestTriggerInstance = triggerInstance;
                    }
                }

                return closestTriggerInstance;
            }

            public TriggerData FindNearestTriggerWhereAiming(BasePlayer player, float maxDistance = 10)
            {
                Vector3 trackPosition;
                if (!TryGetTrackPosition(player, out trackPosition))
                    return null;

                return FindNearestTrigger(trackPosition, maxDistance)?.TriggerData;
            }
        }

        #endregion

        #region Workcart Manager

        private class AutomatedWorkcartManager
        {
            private List<TrainEngine> _automatedWorkcarts = new List<TrainEngine>();

            public int NumWorkcarts => _automatedWorkcarts.Count;
            public TrainEngine[] GetWorkcarts() => _automatedWorkcarts.ToArray();

            public void Register(TrainEngine workcart, WorkcartData workcartData)
            {
                _automatedWorkcarts.Add(workcart);
                _pluginData.AddWorkcartId(workcart.net.ID, workcartData);
            }

            public void Unregister(TrainEngine workcart)
            {
                _automatedWorkcarts.Remove(workcart);
                _pluginData.RemoveWorkcartId(workcart.net.ID);
            }

            public int ResetAll()
            {
                var numWorkcarts = NumWorkcarts;

                foreach (var workcart in _automatedWorkcarts.ToArray())
                    RemoveTrainController(workcart);

                return numWorkcarts;
            }

            public void ResendAllGenericMarkers()
            {
                foreach (var workcart in _automatedWorkcarts)
                {
                    if (workcart == null)
                        continue;

                    var controller = TrainController.GetForWorkcart(workcart);
                    if (controller == null)
                        continue;

                    controller.ResendGenericMarker();
                }
            }

            public void UpdateWorkcartData()
            {
                foreach (var workcart in _automatedWorkcarts)
                {
                    if (workcart == null)
                        continue;

                    var controller = TrainController.GetForWorkcart(workcart);
                    if (controller == null)
                        continue;

                    controller.UpdateWorkcartData();
                }
            }
        }

        #endregion

        #region Train Controller

        private class TrainController : FacepunchBehaviour
        {
            private const float ChillDuration = 3f;

            public static TrainController GetForWorkcart(TrainEngine workcart) =>
                workcart.GetComponent<TrainController>();

            public static TrainController AddToWorkcart(TrainEngine workcart, WorkcartData workcartData)
            {
                var controller = workcart.GetComponent<TrainController>();
                if (controller != null)
                    return controller;

                controller = workcart.gameObject.AddComponent<TrainController>();
                controller.Init(workcartData);
                return controller;
            }

            public static void DestroyAll()
            {
                foreach (var entity in BaseNetworkable.serverEntities)
                {
                    var workcart = entity as TrainEngine;
                    if (workcart == null)
                        continue;

                    RemoveTrainController(workcart, immediate: true);
                }
            }

            public NPCShopKeeper Conductor { get; private set; }
            private TrainEngine _workcart;
            private Transform _transform;
            private ProtectionProperties _originalProtection;

            private MapMarkerGenericRadius _genericMarker;
            private VendingMachineMapMarker _vendingMarker;

            private EngineSpeeds _targetVelocity;
            private EngineSpeeds _departureVelocity;
            private float _stopDuration;

            private WorkcartData _workcartData;

            public bool IsDestroying => IsInvoking(DestroyCinematically);
            private bool IsChilling => IsInvoking(EndChilling);
            private bool IsBraking => IsInvokingFixedTime(BrakeUpdate);
            private bool IsStopping => IsBraking && _targetVelocity == EngineSpeeds.Zero;
            private bool IsWaitingAtStop => IsInvoking(DepartFromStop);

            private void Awake()
            {
                _workcart = GetComponent<TrainEngine>();
                if (_workcart == null)
                    return;

                _transform = _workcart.transform;
                _originalProtection = _workcart.baseProtection;
                _workcart.baseProtection = _pluginInstance._immortalProtection;

                _workcart.SetHealth(_workcart.MaxHealth());

                AddConductor();
                MaybeAddMapMarkers();
                EnableUnlimitedFuel();
            }

            public void Init(WorkcartData workcartData)
            {
                _workcartData = workcartData;

                _workcart.engineController.TryStartEngine(Conductor);

                // Delay disabling hazard checks since starting the engine does not immediately update entity flags.
                Invoke(DisableHazardChecks, 1f);

                var throttle = workcartData.Throttle ?? EngineSpeeds.Zero;
                if (throttle == EngineSpeeds.Zero)
                    throttle = _pluginConfig.GetDefaultSpeed();

                SetThrottle(throttle);
                SetTrackSelection(workcartData.TrackSelection ?? _pluginConfig.GetDefaultTrackSelection());
            }

            public void UpdateWorkcartData()
            {
                _workcartData.Throttle = IntendedDepartureVelocity;
                _workcartData.TrackSelection = _workcart.curTrackSelection;
            }

            public void HandleConductorTrigger(TriggerData triggerData)
            {
                SetThrottle(EngineSpeeds.Zero);

                // Delay at least one second in case the workcart needs to reposition after spawning.
                // Not delaying may cause the workcart to get stuck for unknown reasons.
                // Delay a random interval to spread out load.
                Invoke(() =>
                {
                    HandleWorkcartTrigger(triggerData);
                }, UnityEngine.Random.Range(1, 2f));
            }

            public void HandleWorkcartTrigger(TriggerData triggerData)
            {
                if (!triggerData.MatchesRoute(_workcartData.Route))
                    return;

                if (triggerData.Destroy)
                {
                    Invoke(() => _workcart.Kill(BaseNetworkable.DestroyMode.Gib), 0);
                    return;
                }

                var intendedVelocity = IntendedCurrentVelocity;

                // These are canceled after determing next current intended velocity, since that computation logic takes these into account.
                CancelWaitingAtStop();
                CancelChilling();

                var triggerSpeed = triggerData.GetSpeedInstruction();
                var triggerDirection = triggerData.GetDirectionInstruction();

                _workcart.SetTrackSelection(DetermineNextTrackSelection(_workcart.curTrackSelection, triggerData.GetTrackSelectionInstruction()));
                _departureVelocity = DetermineNextVelocity(intendedVelocity, triggerData.GetDepartureSpeedInstruction(), triggerDirection);
                _stopDuration = triggerData.GetStopDuration();

                var nextVelocity = DetermineNextVelocity(intendedVelocity, triggerSpeed, triggerDirection);

                // Only cancel braking if the trigger specifies speed.
                // Must do this after computing the next velocity.
                if (triggerSpeed != null && IsBraking)
                    CancelBraking();

                if (triggerData.Brake)
                {
                    StartBrakingUntilVelocity(intendedVelocity, nextVelocity);
                    return;
                }

                SetThrottle(nextVelocity);

                if (nextVelocity == EngineSpeeds.Zero)
                    BeginWaitingAtStop(triggerData.GetStopDuration());
            }

            public void ResendGenericMarker()
            {
                if (_genericMarker != null)
                    _genericMarker.SendUpdate();
            }

            private void BeginWaitingAtStop(float stopDuration) => Invoke(DepartFromStop, stopDuration);

            public void DepartEarlyIfStoppedOrStopping()
            {
                if (IsStopping || IsWaitingAtStop)
                {
                    CancelWaitingAtStop();
                    CancelBraking();
                    DepartFromStop();
                }
            }

            private void DepartFromStop() => SetThrottle(_departureVelocity);
            private void CancelWaitingAtStop() => CancelInvoke(DepartFromStop);

            private void SetThrottle(EngineSpeeds engineSpeed)
            {
                _workcart.SetThrottle(engineSpeed);
            }

            private void SetTrackSelection(TrackSelection trackSelection)
            {
                _workcart.SetTrackSelection(trackSelection);
            }

            public void ScheduleDestruction() => Invoke(DestroyCinematically, 0);
            private void DestroyCinematically() => DestroyWorkcart(_workcart);

            private void StartBrakingUntilVelocity(EngineSpeeds currentVelocity, EngineSpeeds desiredVelocity)
            {
                SetThrottle(DetermineNextVelocity(currentVelocity, SpeedInstruction.Lo, DirectionInstruction.Invert));
                CancelBraking();
                _targetVelocity = desiredVelocity;
                InvokeRepeatingFixedTime(BrakeUpdate);
            }

            private void CancelBraking() => CancelInvokeFixedTime(BrakeUpdate);

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
                    _targetVelocity = IntendedCurrentVelocity;
                    if (!IsBraking)
                        _targetVelocity = _workcart.CurThrottleSetting;
                    else if (_targetVelocity == EngineSpeeds.Zero)
                        _targetVelocity = _departureVelocity;

                    SetThrottle(EngineSpeeds.Zero);
                }

                CancelChilling();
                Invoke(EndChilling, ChillDuration);
            }

            private void EndChilling() => SetThrottle(_targetVelocity);
            private void CancelChilling() => CancelInvoke(EndChilling);

            public EngineSpeeds IntendedCurrentVelocity =>
                IsStopping || IsWaitingAtStop
                    ? EngineSpeeds.Zero
                    : IsChilling || IsBraking
                    ? _targetVelocity
                    : _workcart.CurThrottleSetting;

            private EngineSpeeds IntendedDepartureVelocity =>
                IsStopping || IsWaitingAtStop
                    ? _departureVelocity
                    : IsChilling || IsBraking
                    ? _targetVelocity
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
                var currentSpeed = Vector3.Dot(_transform.forward, _workcart.GetLocalVelocity());
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

                Conductor = GameManager.server.CreateEntity(ShopkeeperPrefab, _transform.position) as NPCShopKeeper;
                if (Conductor == null)
                    return;

                Conductor.enableSaving = false;
                Conductor.Spawn();

                Conductor.CancelInvoke(Conductor.Greeting);
                Conductor.CancelInvoke(Conductor.TickMovement);

                // Simple and performant way to prevent NPCs and turrets from targeting the conductor.
                Conductor.DisablePlayerCollider();
                BaseEntity.Query.Server.RemovePlayer(Conductor);
                Conductor.transform.localScale = Vector3.zero;

                AddOutfit();
                GetDriverSeat()?.AttemptMount(Conductor, doMountChecks: false);
            }

            private BaseMountable GetDriverSeat()
            {
                foreach (var mountPoint in _workcart.mountPoints)
                {
                    if (mountPoint.isDriver)
                        return mountPoint.mountable;
                }
                return null;
            }

            private void MaybeAddMapMarkers()
            {
                if (_pluginConfig.GenericMapMarker.Enabled)
                {
                    _genericMarker = GameManager.server.CreateEntity(GenericMapMarkerPrefab, _transform.position) as MapMarkerGenericRadius;
                    if (_genericMarker != null)
                    {
                        _genericMarker.EnableSaving(false);
                        _genericMarker.EnableGlobalBroadcast(true);
                        _genericMarker.syncPosition = false;
                        _genericMarker.Spawn();

                        _genericMarker.color1 = _pluginConfig.GenericMapMarker.GetColor();
                        _genericMarker.color2 = _genericMarker.color1;
                        _genericMarker.alpha = _pluginConfig.GenericMapMarker.Alpha;
                        _genericMarker.radius = _pluginConfig.GenericMapMarker.Radius;
                        _genericMarker.SendUpdate();
                    }
                }

                if (_pluginConfig.VendingMapMarker.Enabled)
                {
                    _vendingMarker = GameManager.server.CreateEntity(VendingMapMarkerPrefab, _transform.position) as VendingMachineMapMarker;
                    if (_vendingMarker != null)
                    {
                        _vendingMarker.markerShopName = _pluginConfig.VendingMapMarker.Name;

                        _vendingMarker.EnableSaving(false);
                        _vendingMarker.EnableGlobalBroadcast(true);
                        _vendingMarker.syncPosition = false;
                        _vendingMarker.Spawn();
                    }
                }

                if (_genericMarker == null && _vendingMarker == null)
                    return;

                // Periodically update the marker positions since they aren't parented to the workcarts.
                // We could parent them to the workcarts, but then they would only appear to players in network radius,
                // and enabling global broadcast for lots of workcarts would significantly reduce client FPS.
                InvokeRandomized(() =>
                {
                    _pluginInstance.TrackStart();

                    if (_genericMarker != null)
                    {
                        _genericMarker.transform.position = _transform.position;
                        _genericMarker.InvalidateNetworkCache();
                        _genericMarker.SendNetworkUpdate_Position();
                    }

                    if (_vendingMarker != null)
                    {
                        _vendingMarker.transform.position = _transform.position;
                        _vendingMarker.InvalidateNetworkCache();
                        _vendingMarker.SendNetworkUpdate_Position();
                    }

                    _pluginInstance.TrackEnd();
                }, 0, _pluginConfig.MapMarkerUpdateInveralSeconds, _pluginConfig.MapMarkerUpdateInveralSeconds * 0.1f);
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

            private void DisableHazardChecks()
            {
                _workcart.SetFlag(TrainEngine.Flag_HazardAhead, false);
                _workcart.CancelInvoke(_workcart.CheckForHazards);
            }

            private void EnableHazardChecks()
            {
                if (_workcart.IsOn() && !_workcart.IsInvoking(_workcart.CheckForHazards))
                    _workcart.InvokeRandomized(_workcart.CheckForHazards, 0f, 1f, 0.1f);
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

                if (_genericMarker != null)
                    _genericMarker.Kill();

                if (_vendingMarker != null)
                    _vendingMarker.Kill();

                if (_originalProtection != null)
                    _workcart.baseProtection = _originalProtection;

                DisableUnlimitedFuel();
                EnableHazardChecks();
            }
        }

        #endregion

        #region Data

        private class WorkcartData
        {
            [JsonProperty("Route", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Route;

            [JsonProperty("Throttle", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [JsonConverter(typeof(StringEnumConverter))]
            public EngineSpeeds? Throttle;

            [JsonProperty("TrackSelection", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [JsonConverter(typeof(StringEnumConverter))]
            public TrackSelection? TrackSelection;
        }

        private class StoredPluginData
        {
            [JsonProperty("AutomatedWorkcardIds", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public HashSet<uint> AutomatedWorkcardIds;

            [JsonProperty("AutomatedWorkcarts")]
            public Dictionary<uint, WorkcartData> AutomatedWorkcarts = new Dictionary<uint, WorkcartData>();

            public static string Filename => _pluginInstance.Name;

            public static StoredPluginData Load()
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<StoredPluginData>(Filename) ?? new StoredPluginData();

                // Migrate from the legacy `AutomatedWorkcardIds` to `AutomatedWorkcarts` which supports data.
                if (data.AutomatedWorkcardIds != null)
                {
                    foreach (var workcartId in data.AutomatedWorkcardIds)
                        data.AutomatedWorkcarts[workcartId] = new WorkcartData();

                    data.AutomatedWorkcardIds = null;
                }

                return data;
            }

            public static StoredPluginData Clear() => new StoredPluginData().Save();

            public StoredPluginData Save()
            {
                Interface.Oxide.DataFileSystem.WriteObject<StoredPluginData>(Filename, this);
                return this;
            }

            public WorkcartData GetWorkcartData(uint workcartId)
            {
                WorkcartData workcartData;
                return AutomatedWorkcarts.TryGetValue(workcartId, out workcartData)
                    ? workcartData
                    : null;
            }

            public bool HasWorkcartId(uint workcartId)
            {
                return AutomatedWorkcarts.ContainsKey(workcartId);
            }

            public void AddWorkcartId(uint workcartId, WorkcartData workcartData)
            {
                AutomatedWorkcarts[workcartId] = workcartData;
            }

            public void RemoveWorkcartId(uint workcartId)
            {
                AutomatedWorkcarts.Remove(workcartId);
            }

            public void TrimToWorkcartIds(HashSet<uint> foundWorkcartIds)
            {
                foreach (var workcartId in AutomatedWorkcarts.Keys.ToArray())
                {
                    if (!foundWorkcartIds.Contains(workcartId))
                        AutomatedWorkcarts.Remove(workcartId);
                }

                Save();
            }
        }

        private class StoredMapData
        {
            [JsonProperty("MapTriggers")]
            public List<TriggerData> MapTriggers = new List<TriggerData>();

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

            public void AddTrigger(TriggerData customTrigger)
            {
                MapTriggers.Add(customTrigger);
                Save();
            }

            public void RemoveTrigger(TriggerData triggerData)
            {
                MapTriggers.Remove(triggerData);
                Save();
            }
        }

        private class StoredTunnelData
        {
            private const float DefaultStationStopDuration = 15;
            private const float DefaultQuickStopDuration = 5;
            private const float DefaultTriggerHeight = 0.29f;

            [JsonProperty("TunnelTriggers")]
            public List<TriggerData> TunnelTriggers = new List<TriggerData>();

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

            public void AddTrigger(TriggerData triggerData)
            {
                TunnelTriggers.Add(triggerData);
                Save();
            }

            public void RemoveTrigger(TriggerData triggerData)
            {
                TunnelTriggers.Remove(triggerData);
                Save();
            }

            public void MigrateTriggers()
            {
                var migratedTriggers = 0;

                foreach (var triggerData in _tunnelData.TunnelTriggers)
                {
                    var tunnelType = triggerData.GetTunnelType();
                    if (tunnelType == TunnelType.TrainStation)
                    {
                        if (triggerData.Position == new Vector3(0, DefaultTriggerHeight, -84))
                        {
                            triggerData.Position = new Vector3(45, DefaultTriggerHeight, 18);
                            migratedTriggers++;
                            continue;
                        }
                        if (triggerData.Position == new Vector3(0, DefaultTriggerHeight, 84))
                        {
                            triggerData.Position = new Vector3(-45, DefaultTriggerHeight, -18);
                            migratedTriggers++;
                            continue;
                        }
                    }
                }

                if (migratedTriggers > 0)
                {
                    _pluginInstance.LogWarning($"Automatically relocated {migratedTriggers} tunnel triggers to bypass tunnels.");
                    Save();
                }
            }

            public static StoredTunnelData GetDefaultData()
            {
                return new StoredTunnelData()
                {
                    TunnelTriggers =
                    {
                        new TriggerData
                        {
                            Id = 1,
                            Position = new Vector3(4.5f, DefaultTriggerHeight, 52),
                            TunnelType = TunnelType.TrainStation.ToString(),
                            Brake = true,
                            Speed = SpeedInstruction.Zero.ToString(),
                            StopDuration = DefaultStationStopDuration,
                            DepartureSpeed = SpeedInstruction.Hi.ToString(),
                        },
                        new TriggerData
                        {
                            Id = 2,
                            Position = new Vector3(45, DefaultTriggerHeight, 18),
                            TunnelType = TunnelType.TrainStation.ToString(),
                            AddConductor = true,
                            Direction = DirectionInstruction.Fwd.ToString(),
                            Speed = SpeedInstruction.Hi.ToString(),
                            TrackSelection = TrackSelectionInstruction.Left.ToString(),
                        },
                        new TriggerData
                        {
                            Id = 3,
                            Position = new Vector3(-4.5f, DefaultTriggerHeight, -11),
                            TunnelType = TunnelType.TrainStation.ToString(),
                            Brake = true,
                            Speed = SpeedInstruction.Zero.ToString(),
                            StopDuration = DefaultStationStopDuration,
                            DepartureSpeed = SpeedInstruction.Hi.ToString(),
                        },
                        new TriggerData
                        {
                            Id = 4,
                            Position = new Vector3(-45, DefaultTriggerHeight, -18),
                            TunnelType = TunnelType.TrainStation.ToString(),
                            AddConductor = true,
                            Direction = DirectionInstruction.Fwd.ToString(),
                            Speed = SpeedInstruction.Hi.ToString(),
                            TrackSelection = TrackSelectionInstruction.Left.ToString(),
                        },

                        new TriggerData
                        {
                            Id = 5,
                            Position = new Vector3(-4.45f, DefaultTriggerHeight, -31),
                            TunnelType = TunnelType.BarricadeTunnel.ToString(),
                            Brake = true,
                            Speed = SpeedInstruction.Med.ToString(),
                        },
                        new TriggerData
                        {
                            Id = 6,
                            Position = new Vector3(-4.5f, DefaultTriggerHeight, -1f),
                            TunnelType = TunnelType.BarricadeTunnel.ToString(),
                            Brake = true,
                            Speed = SpeedInstruction.Zero.ToString(),
                            StopDuration = 5,
                            DepartureSpeed = SpeedInstruction.Hi.ToString(),
                        },
                        new TriggerData
                        {
                            Id = 7,
                            Position = new Vector3(4.45f, DefaultTriggerHeight, 39),
                            TunnelType = TunnelType.BarricadeTunnel.ToString(),
                            Brake = true,
                            Speed = SpeedInstruction.Med.ToString(),
                        },
                        new TriggerData
                        {
                            Id = 8,
                            Position = new Vector3(4.5f, DefaultTriggerHeight, 9f),
                            TunnelType = TunnelType.BarricadeTunnel.ToString(),
                            Brake = true,
                            Speed = SpeedInstruction.Zero.ToString(),
                            StopDuration = 5,
                            DepartureSpeed = SpeedInstruction.Hi.ToString(),
                        },

                        new TriggerData
                        {
                            Id = 9,
                            Position = new Vector3(3, DefaultTriggerHeight, 35f),
                            TunnelType = TunnelType.LootTunnel.ToString(),
                            Brake = true,
                            Speed = SpeedInstruction.Zero.ToString(),
                            StopDuration = DefaultQuickStopDuration,
                            DepartureSpeed = SpeedInstruction.Hi.ToString(),
                        },
                        new TriggerData
                        {
                            Id = 10,
                            Position = new Vector3(-3, DefaultTriggerHeight, -35f),
                            TunnelType = TunnelType.LootTunnel.ToString(),
                            Brake = true,
                            Speed = SpeedInstruction.Zero.ToString(),
                            StopDuration = DefaultQuickStopDuration,
                            DepartureSpeed = SpeedInstruction.Hi.ToString(),
                        },

                        new TriggerData
                        {
                            Id = 11,
                            Position = new Vector3(35, DefaultTriggerHeight, -3.0f),
                            TunnelType = TunnelType.Intersection.ToString(),
                            Brake = true,
                            Speed = SpeedInstruction.Zero.ToString(),
                            StopDuration = DefaultQuickStopDuration,
                            DepartureSpeed = SpeedInstruction.Hi.ToString(),
                        }
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

        private class GenericMarkerOptions
        {
            [JsonProperty("Enabled")]
            public bool Enabled = false;

            [JsonProperty("Color")]
            public string Color = "#00ff00";

            [JsonProperty("Alpha")]
            public float Alpha = 1;

            [JsonProperty("Radius")]
            public float Radius = 0.05f;

            private Color? _color;
            public Color GetColor()
            {
                if (_color == null)
                    _color = ParseColor(Color, UnityEngine.Color.black);

                return (Color)_color;
            }

            private static Color ParseColor(string colorString, Color defaultColor)
            {
                Color color;
                return ColorUtility.TryParseHtmlString(colorString, out color)
                    ? color
                    : defaultColor;
            }
        }

        private class VendingMarkerOptions
        {
            [JsonProperty("Enabled")]
            public bool Enabled = false;

            [JsonProperty("Name")]
            public string Name = "Automated Workcart";
        }

        private class CustomMarkerOptions
        {
            [JsonProperty("Enabled")]
            public bool Enabled = false;

            [JsonProperty("Prefab")]
            public string Prefab = "assets/prefabs/tools/map/ch47marker.prefab";
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
                [TunnelType.Intersection.ToString()] = false,
                [TunnelType.LargeIntersection.ToString()] = false,
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

            [JsonProperty("ColoredMapMarker")]
            public GenericMarkerOptions GenericMapMarker = new GenericMarkerOptions();

            [JsonProperty("VendingMapMarker")]
            public VendingMarkerOptions VendingMapMarker = new VendingMarkerOptions();

            [JsonProperty("MapMarkerUpdateInveralSeconds")]
            public float MapMarkerUpdateInveralSeconds = 5.0f;

            [JsonProperty("TriggerDisplayDistance")]
            public float TriggerDisplayDistance = 150;

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
            catch (Exception e)
            {
                LogError(e.Message);
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
            var speedOptions = GetMessage(player, Lang.HelpSpeedOptions, GetEnumOptions<SpeedInstruction>());
            var directionOptions = GetMessage(player, Lang.HelpDirectionOptions, GetEnumOptions<DirectionInstruction>());
            var trackSelectionOptions = GetMessage(player, Lang.HelpTrackSelectionOptions, GetEnumOptions<TrackSelectionInstruction>());
            var otherOptions = GetMessage(player, Lang.HelpOtherOptions);

            return $"{speedOptions}\n{directionOptions}\n{trackSelectionOptions}\n{otherOptions}";
        }

        private string GetTriggerPrefix(IPlayer player, WorkcartTriggerType triggerType) =>
            GetMessage(player, triggerType == WorkcartTriggerType.Tunnel ? Lang.InfoTriggerTunnelPrefix : Lang.InfoTriggerMapPrefix);

        private string GetTriggerPrefix(IPlayer player, TriggerData triggerData) =>
            GetTriggerPrefix(player, triggerData.TriggerType);

        private string GetTriggerPrefix(BasePlayer player, WorkcartTriggerType triggerType) =>
            GetTriggerPrefix(player.IPlayer, triggerType);

        private string GetTriggerPrefix(BasePlayer player, TriggerData triggerData) =>
            GetTriggerPrefix(player.IPlayer, triggerData.TriggerType);

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
            public const string ToggleOnWithRouteSuccess = "Toggle.Success.On.WithRoute";
            public const string ToggleOffSuccess = "Toggle.Success.Off";
            public const string ResetAllSuccess = "ResetAll.Success";
            public const string ShowTriggersSuccess = "ShowTriggers.Success";
            public const string ShowTriggersWithRouteSuccess = "ShowTriggers.WithRoute.Success";

            public const string AddTriggerSyntax = "AddTrigger.Syntax";
            public const string AddTriggerSuccess = "AddTrigger.Success";
            public const string MoveTriggerSuccess = "MoveTrigger.Success";
            public const string UpdateTriggerSyntax = "UpdateTrigger.Syntax";
            public const string UpdateTriggerSuccess = "UpdateTrigger.Success";
            public const string SimpleTriggerSyntax = "Trigger.SimpleSyntax";
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

            public const string InfoTriggerrDisabled = "Info.Trigger.Disabled";
            public const string InfoTriggerMap = "Info.Trigger.Map";
            public const string InfoTriggerRoute = "Info.Trigger.Route";
            public const string InfoTriggerTunnel = "Info.Trigger.Tunnel";
            public const string InfoTriggerAddConductor = "Info.Trigger.Conductor";
            public const string InfoTriggerDestroy = "Info.Trigger.Destroy";
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
                [Lang.ToggleOnWithRouteSuccess] = "That workcart is now automated with route <color=#fd4>@{0}</color>.",
                [Lang.ToggleOffSuccess] = "That workcart is no longer automated.",
                [Lang.ResetAllSuccess] = "All {0} conductors have been removed.",
                [Lang.ShowTriggersSuccess] = "Showing all triggers for <color=#fd4>{0}</color>.",
                [Lang.ShowTriggersWithRouteSuccess] = "Showing all triggers for route <color=#fd4>@{0}</color> for <color=#fd4>{1}</color>",

                [Lang.AddTriggerSyntax] = "Syntax: <color=#fd4>{0} <option1> <option2> ...</color>\n{1}",
                [Lang.AddTriggerSuccess] = "Successfully added trigger #<color=#fd4>{0}{1}</color>.",
                [Lang.UpdateTriggerSyntax] = "Syntax: <color=#fd4>{0} <id> <option1> <option2> ...</color>\n{1}",
                [Lang.UpdateTriggerSuccess] = "Successfully updated trigger #<color=#fd4>{0}{1}</color>",
                [Lang.MoveTriggerSuccess] = "Successfully moved trigger #<color=#fd4>{0}{1}</color>",
                [Lang.SimpleTriggerSyntax] = "Syntax: <color=#fd4>{0} <id></color>",
                [Lang.RemoveTriggerSuccess] = "Trigger #<color=#fd4>{0}{1}</color> successfully removed.",

                [Lang.InfoConductorCountLimited] = "Total conductors: <color=#fd4>{0}/{1}</color>.",
                [Lang.InfoConductorCountUnlimited] = "Total conductors: <color=#fd4>{0}</color>.",

                [Lang.HelpSpeedOptions] = "Speeds: {0}",
                [Lang.HelpDirectionOptions] = "Directions: {0}",
                [Lang.HelpTrackSelectionOptions] = "Track selection: {0}",
                [Lang.HelpOtherOptions] = "Other options: <color=#fd4>Conductor</color> | <color=#fd4>Brake</color> | <color=#fd4>Destroy</color> | <color=#fd4>@ROUTE_NAME</color> | <color=#fd4>Enabled</color> | <color=#fd4>Disabled</color>",

                [Lang.InfoTrigger] = "Workcart Trigger #{0}{1}",
                [Lang.InfoTriggerMapPrefix] = "M",
                [Lang.InfoTriggerTunnelPrefix] = "T",

                [Lang.InfoTriggerrDisabled] = "DISABLED",
                [Lang.InfoTriggerMap] = "Map-specific",
                [Lang.InfoTriggerRoute] = "Route: @{0}",
                [Lang.InfoTriggerTunnel] = "Tunnel type: {0} (x{1})",
                [Lang.InfoTriggerAddConductor] = "Adds Conductor",
                [Lang.InfoTriggerDestroy] = "Destroys workcart",
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
