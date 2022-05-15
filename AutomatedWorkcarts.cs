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
using System.Text.RegularExpressions;
using UnityEngine;
using static TrainEngine;
using static TrainTrackSpline;

namespace Oxide.Plugins
{
    [Info("Automated Workcarts", "WhiteThunder", "0.26.0")]
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

        private const string WorkcartPrefab = "assets/content/vehicles/workcart/workcart.entity.prefab";
        private const string AboveGroundWorkcartPrefab = "assets/content/vehicles/workcart/workcart_aboveground.entity.prefab";
        private const string ShopkeeperPrefab = "assets/prefabs/npc/bandit/shopkeepers/bandit_shopkeeper.prefab";
        private const string GenericMapMarkerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string VendingMapMarkerPrefab = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";
        private const string IdPlaceholder = "$id";

        private static readonly object False = false;
        private static readonly Regex IdRegex = new Regex("\\$id", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private WorkcartTriggerManager _triggerManager = new WorkcartTriggerManager();
        private TrainManager _trainManager = new TrainManager();

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

            Unsubscribe(nameof(OnEntitySpawned));

            if (!_pluginConfig.GenericMapMarker.Enabled)
                Unsubscribe(nameof(OnPlayerConnected));
        }

        private void OnServerInitialized()
        {
            if (!_pluginConfig.EnableTerrainCollision)
            {
                foreach (var entity in BaseNetworkable.serverEntities)
                {
                    var trainCar = entity as TrainCar;
                    if (trainCar != null)
                    {
                        EnableTerrainCollision(trainCar, false);
                    }
                }

                Subscribe(nameof(OnEntitySpawned));
            }

            _tunnelData.MigrateTriggers();
            _mapData = StoredMapData.Load();

            _startupCoroutine = ServerMgr.Instance.StartCoroutine(DoStartupRoutine());
        }

        private void Unload()
        {
            if (!_pluginConfig.EnableTerrainCollision)
            {
                foreach (var entity in BaseNetworkable.serverEntities)
                {
                    var trainCar = entity as TrainCar;
                    if (trainCar != null)
                    {
                        EnableTerrainCollision(trainCar, true);
                    }
                }
            }

            if (_startupCoroutine != null)
                ServerMgr.Instance.StopCoroutine(_startupCoroutine);

            OnServerSave();
            _triggerManager.DestroyAll();
            _trainManager.Unload();

            _mapData = null;
            _pluginData = null;
            _tunnelData = null;
            _pluginConfig = null;
            _pluginInstance = null;
        }

        private void OnServerSave()
        {
            _trainManager.UpdateWorkcartData();
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

            _trainManager.ResendAllGenericMarkers();
        }

        private void OnEntitySpawned(TrainCar trainCar)
        {
            EnableTerrainCollision(trainCar, false);
        }

        private void OnEntityEnter(WorkcartTrigger trigger, TrainEngine workcart)
        {
            var trainController = _trainManager.GetTrainController(workcart);
            if (trainController == null)
            {
                if (trigger.TriggerData.AddConductor
                    && !trigger.TriggerData.Destroy
                    && !IsWorkcartOwned(workcart)
                    && CanHaveMoreConductors())
                {
                    _trainManager.TryAddTrainController(workcart, trigger.TriggerData);
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

                var forwardController = _trainManager.GetTrainController(forwardWorkcart);
                var backController = _trainManager.GetTrainController(backwardWorkcart);

                // Do nothing if neither workcart is automated.
                if (forwardController == null && backController == null)
                    return;

                if (forwardController != null)
                {
                    forwardController.DepartEarlyIfStoppedOrStopping();
                }
                else if (_pluginConfig.BulldozeOffendingWorkcarts && Math.Abs(EngineThrottleToNumber(forwardWorkcart.CurThrottleSetting)) < Math.Abs(EngineThrottleToNumber(backwardWorkcart.CurThrottleSetting)))
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
                var controller = _trainManager.GetTrainController(workcart);
                var otherController = _trainManager.GetTrainController(otherWorkcart);

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

        private object OnTrainCarUncouple(TrainCar trainCar, BasePlayer player)
        {
            // Disallow uncoupling train cars from automated trains.
            return _trainManager.IsRegistered(trainCar)
                ? False
                : null;
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

            var trainController = _trainManager.GetTrainController(workcart);
            if (trainController == null)
            {
                if (IsWorkcartOwned(workcart))
                {
                    ReplyToPlayer(player, Lang.ErrorWorkcartOwned);
                    return;
                }

                if (!CanHaveMoreConductors())
                {
                    ReplyToPlayer(player, Lang.ErrorMaxConductors, _trainManager.NumWorkcarts, _pluginConfig.MaxConductors);
                    return;
                }

                WorkcartData workcartData = null;

                if (args.Length > 0)
                {
                    var routeName = GetRouteNameFromArg(player, args[0], requirePrefix: false);
                    if (!string.IsNullOrWhiteSpace(routeName))
                        workcartData = new WorkcartData { Route = routeName };
                }

                if (_trainManager.TryAddTrainController(workcart, workcartData: workcartData))
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
                _trainManager.RemoveTrainController(workcart);
                player.Reply(GetMessage(player, Lang.ToggleOffSuccess) + " " + GetConductorCountMessage(player));
            }
        }

        [Command("aw.resetall")]
        private void CommandResetWorkcarts(IPlayer player, string cmd, string[] args)
        {
            if (!player.IsServer
                && !VerifyPermission(player, PermissionToggle))
                return;

            var numReset = _trainManager.Unload();
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

            AddTriggerShared(player, cmd, args, triggerData, dungeonCellWrapper);
        }

        private void AddTriggerShared(IPlayer player, string cmd, string[] args, TriggerData triggerData, DungeonCellWrapper dungeonCellWrapper = null)
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

            if (triggerData.Spawner)
            {
                var rotation = Quaternion.Euler(basePlayer.viewAngles);
                if (dungeonCellWrapper != null)
                {
                    rotation *= Quaternion.Inverse(dungeonCellWrapper.Rotation);
                }
                triggerData.RotationAngle = rotation.eulerAngles.y % 360;
            }

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

        [Command("aw.rotatetrigger", "awt.rotate")]
        private void CommandSetTriggerRotation(IPlayer player, string cmd, string[] args)
        {
            TriggerData triggerData;
            string[] optionArgs;

            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.SimpleTriggerSyntax, out triggerData, out optionArgs))
                return;

            var basePlayer = player.Object as BasePlayer;
            var playerPosition = basePlayer.transform.position;

            var triggerInstance = _triggerManager.FindNearestTrigger(playerPosition, triggerData);
            var rotation = Quaternion.Euler(basePlayer.viewAngles);
            var tunnelTriggerInstance = triggerInstance as TunnelTriggerInstance;
            if (tunnelTriggerInstance != null)
            {
                rotation *= Quaternion.Inverse(tunnelTriggerInstance.DungeonCellWrapper.Rotation);
            }
            _triggerManager.RotateTrigger(triggerData, rotation.eulerAngles.y % 360);

            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.RotateTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.respawntrigger", "awt.respawn")]
        private void CommandRespawnTrigger(IPlayer player, string cmd, string[] args)
        {
            TriggerData triggerData;
            string[] optionArgs;

            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.SimpleTriggerSyntax, out triggerData, out optionArgs))
                return;

            if (!triggerData.Spawner)
            {
                ReplyToPlayer(player, Lang.ErrorRequiresSpawnTrigger);
                return;
            }

            if (!triggerData.Enabled)
            {
                ReplyToPlayer(player, Lang.ErrorTriggerDisabled);
                return;
            }

            _triggerManager.RespawnTrigger(triggerData);

            var basePlayer = player.Object as BasePlayer;
            _triggerManager.ShowAllRepeatedly(basePlayer);
        }

        [Command("aw.addtriggercommand", "awt.addcommand", "awt.addcmd")]
        private void CommandAddCommand(IPlayer player, string cmd, string[] args)
        {
            TriggerData triggerData;
            string[] optionArgs;

            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.AddCommandSyntax, out triggerData, out optionArgs))
                return;

            if (optionArgs.Length < 1)
            {
                ReplyToPlayer(player, Lang.AddCommandSyntax, cmd);
                return;
            }

            var quotedCommands = optionArgs.Select(command => command.Contains(" ") ? $"\"{command}\"" : command).ToArray();
            _triggerManager.AddTriggerCommand(triggerData, string.Join(" ", quotedCommands));

            var basePlayer = player.Object as BasePlayer;
            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.UpdateTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.removetriggercommand", "awt.removecommand", "awt.removecmd")]
        private void CommandRemoveCommand(IPlayer player, string cmd, string[] args)
        {
            TriggerData triggerData;
            string[] optionArgs;

            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.RemoveCommandSyntax, out triggerData, out optionArgs))
                return;

            int commandIndex;
            if (optionArgs.Length < 1 || !int.TryParse(optionArgs[0], out commandIndex))
            {
                ReplyToPlayer(player, Lang.RemoveCommandSyntax, cmd);
                return;
            }

            if (commandIndex < 1 || commandIndex > triggerData.Commands.Count)
            {
                ReplyToPlayer(player, Lang.RemoveCommandErrorIndex, commandIndex);
                return;
            }

            _triggerManager.RemoveTriggerCommand(triggerData, commandIndex - 1);

            var basePlayer = player.Object as BasePlayer;
            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.UpdateTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.settriggerwagons", "awt.setwagons", "awt.wagons")]
        private void CommandTriggerWagons(IPlayer player, string cmd, string[] args)
        {
            TriggerData triggerData;
            string[] optionArgs;

            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.RemoveCommandSyntax, out triggerData, out optionArgs))
                return;

            if (!triggerData.Spawner)
            {
                ReplyToPlayer(player, Lang.ErrorRequiresSpawnTrigger);
                return;
            }

            var wagonNames = new List<string>();
            foreach (var arg in optionArgs)
            {
                var trainCarPrefab = TrainCarPrefab.FindPrefab(arg);
                if (trainCarPrefab == null)
                {
                    ReplyToPlayer(player, Lang.ErrorUnrecognizedWagon, arg);
                    return;
                }

                wagonNames.Add(trainCarPrefab.TrainCarName);
            }

            _triggerManager.UpdateWagons(triggerData, wagonNames.ToArray());

            var basePlayer = player.Object as BasePlayer;
            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.UpdateTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
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

        [HookMethod("API_AutomateWorkcart")]
        private bool API_AutomateWorkcart(TrainEngine workcart)
        {
            return _trainManager.IsRegistered(workcart)
                ? true
                : _trainManager.TryAddTrainController(workcart);
        }

        [HookMethod("API_StopAutomatingWorkcart")]
        private void API_StopAutomatingWorkcart(TrainEngine workcart, bool immediate = false)
        {
            _trainManager.RemoveTrainController(workcart);
        }

        [HookMethod("API_IsWorkcartAutomated")]
        private bool API_IsWorkcartAutomated(TrainEngine workcart)
        {
            return _trainManager.IsRegistered(workcart);
        }

        [HookMethod("API_GetAutomatedWorkcarts")]
        private TrainEngine[] API_GetAutomatedWorkcarts()
        {
            return _trainManager.GetWorkcarts();
        }

        #endregion

        #region Exposed Hooks

        private static class ExposedHooks
        {
            public static object OnWorkcartAutomationStart(TrainEngine workcart)
            {
                return Interface.CallHook("OnWorkcartAutomationStart", workcart);
            }

            public static void OnWorkcartAutomationStarted(TrainEngine workcart)
            {
                Interface.CallHook("OnWorkcartAutomationStarted", workcart);
            }

            public static void OnWorkcartAutomationStopped(TrainEngine workcart)
            {
                Interface.CallHook("OnWorkcartAutomationStopped", workcart);
            }
        }

        #endregion

        #region Dependencies

        private bool IsCargoTrain(TrainEngine workcart)
        {
            var result = CargoTrainEvent?.Call("IsTrainSpecial", workcart.net.ID);
            return result is bool && (bool)result;
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

            if (argLower.StartsWith("brake"))
            {
                triggerData.Brake = true;
                return true;
            }

            if (argLower.StartsWith("destroy"))
            {
                triggerData.Destroy = true;
                return true;
            }

            if (argLower.StartsWith("spawn"))
            {
                triggerData.Spawner = true;
                return true;
            }

            if (argLower.StartsWith("enable"))
            {
                triggerData.Enabled = true;
                return true;
            }

            if (argLower.StartsWith("disable"))
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

        #region Helper Methods - Coupling

        private static void UpdateAllowedCouplings(TrainCar trainCar, bool allowFront, bool allowRear)
        {
            var coupling = trainCar.coupling;

            if (allowFront == coupling.frontCoupling.isValid
                && allowRear == coupling.rearCoupling.isValid)
            {
                // No change to couplings needed.
                return;
            }

            if (trainCar.frontCoupling == null || trainCar.rearCoupling == null)
            {
                // Some train cars do not allow coupling, such as the classic workcart.
                return;
            }

            if (!trainCar.IsFullySpawned())
            {
                LogError($"Cannot update couplings before spawn.");
                return;
            }

            var frontTransform = trainCar.frontCoupling;
            var rearTransform = trainCar.rearCoupling;

            var frontTarget = coupling.frontCoupling.CoupledTo;
            var rearTarget = coupling.rearCoupling.CoupledTo;

            if (coupling.frontCoupling.IsCoupled)
            {
                coupling.frontCoupling.Uncouple(reflect: true);
            }

            if (coupling.rearCoupling.IsCoupled)
            {
                coupling.rearCoupling.Uncouple(reflect: true);
            }

            if (!allowFront)
            {
                trainCar.frontCoupling = null;
            }

            if (!allowRear)
            {
                trainCar.rearCoupling = null;
            }

            trainCar.coupling = new TrainCouplingController(trainCar);
            trainCar.frontCoupling = frontTransform;
            trainCar.rearCoupling = rearTransform;

            if (allowFront && frontTarget != null)
            {
                trainCar.coupling.frontCoupling.TryCouple(frontTarget, reflect: true);
            }

            if (allowRear && rearTarget != null)
            {
                trainCar.coupling.rearCoupling.TryCouple(rearTarget, reflect: true);
            }
        }

        private static void DisableTrainCoupling(CompleteTrain completeTrain)
        {
            var firstTrainCar = completeTrain.trainCars.FirstOrDefault();
            var lastTrainCar = completeTrain.trainCars.LastOrDefault();
            if (firstTrainCar == null || lastTrainCar == null)
                return;

            UpdateAllowedCouplings(firstTrainCar, firstTrainCar.coupling.IsFrontCoupled, firstTrainCar.coupling.IsRearCoupled);

            if (lastTrainCar != firstTrainCar)
            {
                UpdateAllowedCouplings(lastTrainCar, lastTrainCar.coupling.IsFrontCoupled, lastTrainCar.coupling.IsRearCoupled);
            }
        }

        private static void EnableTrainCoupling(CompleteTrain completeTrain)
        {
            var firstTrainCar = completeTrain.trainCars.FirstOrDefault();
            var lastTrainCar = completeTrain.trainCars.LastOrDefault();
            if (firstTrainCar == null || lastTrainCar == null)
                return;

            UpdateAllowedCouplings(firstTrainCar, allowFront: true, allowRear: true);

            if (lastTrainCar != firstTrainCar)
            {
                UpdateAllowedCouplings(lastTrainCar, allowFront: true, allowRear: true);
            }
        }

        #endregion

        #region Helper Methods

        private IEnumerator DoStartupRoutine()
        {
            yield return _triggerManager.CreateAll();
            TrackStart();

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
                            && !_trainManager.IsRegistered(workcart))
                        {
                            _trainManager.TryAddTrainController(workcart, workcartData: workcartData);
                        }
                    });
                }
            }

            _pluginData.TrimToWorkcartIds(foundWorkcartIds);
            TrackEnd();
        }

        private void EnableTerrainCollision(TrainCar trainCar, bool enabled)
        {
            Physics.IgnoreCollision(trainCar.frontCollisionTrigger.triggerCollider, TerrainMeta.Collider, !enabled);
            Physics.IgnoreCollision(trainCar.rearCollisionTrigger.triggerCollider, TerrainMeta.Collider, !enabled);
        }

        private bool AutomationWasBlocked(TrainEngine workcart)
        {
            object hookResult = ExposedHooks.OnWorkcartAutomationStart(workcart);
            if (hookResult is bool && (bool)hookResult == false)
                return true;

            if (IsCargoTrain(workcart))
                return true;

            return false;
        }

        private bool CanHaveMoreConductors() =>
            _pluginConfig.MaxConductors < 0
            || _trainManager.NumWorkcarts < _pluginConfig.MaxConductors;

        public static void LogInfo(string message) => Interface.Oxide.LogInfo($"[Automated Workcarts] {message}");
        public static void LogError(string message) => Interface.Oxide.LogError($"[Automated Workcarts] {message}");
        public static void LogWarning(string message) => Interface.Oxide.LogWarning($"[Automated Workcarts] {message}");

        private static void EnableSavingRecursive(BaseEntity entity, bool enableSaving)
        {
            entity.EnableSaving(enableSaving);

            foreach (var child in entity.children)
            {
                if (child is BasePlayer)
                    continue;

                EnableSavingRecursive(child, enableSaving);
            }
        }

        private static TrainEngine SpawnWorkcart(string prefabName, Vector3 position, Quaternion rotation)
        {
            var workcart = GameManager.server.CreateEntity(prefabName, position, rotation) as TrainEngine;

            // Ensure the workcart does not decay for some time.
            workcart.lastDecayTick = Time.realtimeSinceStartup;

            workcart.EnableSaving(false);
            workcart.Spawn();

            if (workcart.IsDestroyed)
                return null;

            workcart.Invoke(() => EnableSavingRecursive(workcart, false), 0);

            return workcart;
        }

        private static TrainCar SpawnWagon(string prefabName, Vector3 position, Quaternion rotation)
        {
            var trainCar = GameManager.server.CreateEntity(prefabName, position, rotation) as TrainCar;

            // Ensure the workcart does not decay for some time.
            trainCar.lastDecayTick = Time.realtimeSinceStartup;

            // Enable invincibility.
            trainCar.initialSpawnTime = float.MaxValue;

            trainCar.EnableSaving(false);
            trainCar.Spawn();

            if (trainCar.IsDestroyed)
                return null;

            trainCar.Invoke(() => EnableSavingRecursive(trainCar, false), 0);

            return trainCar;
        }

        private static float GetSplineDistance(TrainTrackSpline spline, Vector3 position)
        {
            float distanceOnSpline;
            spline.GetDistance(position, 1, out distanceOnSpline);
            return distanceOnSpline;
        }

        private static TrainCar AddWagon(TrainCar trainCar, string prefabName, TrackSelection trackSelection, bool allowRearCoupling = true)
        {
            var rearSpline = trainCar.FrontTrackSection;
            var position = trainCar.transform.position;
            var distanceOnSpline = GetSplineDistance(rearSpline, position);
            var carLength = GetTrainCarLength(trainCar.PrefabName);

            var askerIsForward = rearSpline.IsForward(trainCar.transform.forward, distanceOnSpline);
            var splineInfo = new SplineInfo
            {
                Spline = rearSpline,
                Distance = distanceOnSpline,
                Ascending = !askerIsForward,
                IsForward = askerIsForward,
            };

            var distanceFromTrainCar = carLength + GetTrainCarLength(prefabName) - 0.6f;

            SplineInfo resultSplineInfo;
            var resultPosition = GetPositionAlongTrack(splineInfo, distanceFromTrainCar, trackSelection, out resultSplineInfo);
            var resultRotation = GetSplineTangentRotation(resultSplineInfo.Spline, resultSplineInfo.Distance, trainCar.transform.rotation);

            var wagon = SpawnWagon(prefabName, resultPosition, resultRotation, allowFrontCoupling: true, allowRearCoupling: allowRearCoupling);
            if (wagon != null)
            {
                TryCoupleTrainCars(trainCar, wagon);
            }

            return wagon;
        }

        private static void TryCoupleTrainCars(TrainCar front, TrainCar rear)
        {
            front.coupling.rearCoupling.TryCouple(rear.coupling.frontCoupling, reflect: true);
        }

        private static float GetTrainCarLength(string prefabName)
        {
            var prefab = GameManager.server.FindPrefab(prefabName)?.GetComponent<TrainCar>();
            if (prefab == null)
                return 0;

            return prefab.WorldSpaceBounds().extents.z;
        }

        private static ConnectedTrackInfo GetAdjacentTrackInfo(TrainTrackSpline spline, TrainTrackSpline.TrackSelection selection, bool isAscending = true, bool askerIsForward = true)
        {
            var trackOptions = isAscending
                ? spline.nextTracks
                : spline.prevTracks;

            if (trackOptions.Count == 0)
                return null;

            if (trackOptions.Count == 1)
                return trackOptions[0];

            switch (selection)
            {
                case TrainTrackSpline.TrackSelection.Left:
                    return isAscending == askerIsForward
                        ? trackOptions.FirstOrDefault()
                        : trackOptions.LastOrDefault();

                case TrainTrackSpline.TrackSelection.Right:
                    return isAscending == askerIsForward
                        ? trackOptions.LastOrDefault()
                        : trackOptions.FirstOrDefault();

                default:
                    return trackOptions[isAscending ? spline.straightestNextIndex : spline.straightestPrevIndex];
            }
        }

        private static Quaternion GetSplineTangentRotation(TrainTrackSpline spline, float distanceOnSpline, Quaternion approximateRotation)
        {
            Vector3 tangentDirection;
            spline.GetPositionAndTangent(distanceOnSpline, approximateRotation * Vector3.forward, out tangentDirection);
            return Quaternion.LookRotation(tangentDirection);
        }

        private struct SplineInfo
        {
            public TrainTrackSpline Spline;
            public float Distance;
            public bool Ascending;
            public bool IsForward;
        }

        private static Vector3 GetPositionAlongTrack(SplineInfo splineInfo, float desiredDistance, TrackSelection trackSelection, out SplineInfo resultSplineInfo, out float remainingDistance)
        {
            resultSplineInfo = splineInfo;
            remainingDistance = desiredDistance;

            var i = 0;

            while (remainingDistance > 0)
            {
                if (i++ > 1000)
                {
                    LogError("Something is wrong. Please contact the plugin developer.");
                    return Vector3.zero;
                }

                var splineLength = resultSplineInfo.Spline.GetLength();
                var newDistanceOnSpline = resultSplineInfo.Ascending
                    ? resultSplineInfo.Distance + remainingDistance
                    : resultSplineInfo.Distance - remainingDistance;

                remainingDistance -= resultSplineInfo.Ascending
                    ? splineLength - resultSplineInfo.Distance
                    : resultSplineInfo.Distance;

                if (newDistanceOnSpline >= 0 && newDistanceOnSpline <= splineLength)
                {
                    // Reached desired distance.
                    resultSplineInfo.Distance = newDistanceOnSpline;
                    return resultSplineInfo.Spline.GetPosition(resultSplineInfo.Distance);
                }

                var adjacentTrackInfo = GetAdjacentTrackInfo(resultSplineInfo.Spline, trackSelection, resultSplineInfo.Ascending, resultSplineInfo.IsForward);
                if (adjacentTrackInfo == null)
                {
                    // Track is a dead end.
                    resultSplineInfo.Distance = resultSplineInfo.Ascending ? splineLength : 0;
                    return resultSplineInfo.Spline.GetPosition(resultSplineInfo.Distance);
                }

                if (adjacentTrackInfo.orientation == TrainTrackSpline.TrackOrientation.Reverse)
                {
                    resultSplineInfo.Ascending = !resultSplineInfo.Ascending;
                    resultSplineInfo.IsForward = !resultSplineInfo.IsForward;
                }

                resultSplineInfo.Spline = adjacentTrackInfo.track;
                resultSplineInfo.Distance = resultSplineInfo.Ascending ? 0 : resultSplineInfo.Spline.GetLength();
            }

            return Vector3.zero;
        }

        private static Vector3 GetPositionAlongTrack(SplineInfo splineInfo, float desiredDistance, TrackSelection trackSelection, out SplineInfo resultSplineInfo)
        {
            float remainingDistance;
            return GetPositionAlongTrack(splineInfo, desiredDistance, trackSelection, out resultSplineInfo, out remainingDistance);
        }

        private static Vector3 GetPositionAlongTrack(SplineInfo splineInfo, float desiredDistance, TrackSelection trackSelection)
        {
            SplineInfo resultSplineInfo;
            float remainingDistance;
            return GetPositionAlongTrack(splineInfo, desiredDistance, trackSelection, out resultSplineInfo, out remainingDistance);
        }

        private static bool IsWorkcartOwned(TrainEngine workcart) => workcart.OwnerID != 0;

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
            LogError($"Unrecognized engine speed: {speedName}");
            return false;
        }

        private static bool TryParseTrackSelection(string selectionName, out TrackSelection trackSelection)
        {
            if (Enum.TryParse<TrackSelection>(selectionName, true, out trackSelection))
                return true;

            LogError($"Unrecognized track selection: {selectionName}");
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
            var throttle = trainController != null
                ? trainController.DepartureThrottle
                : workcart.CurThrottleSetting;

            return EngineThrottleToNumber(throttle) >= 0 ?
                workcart.transform.forward
                : -workcart.transform.forward;
        }

        private static TrainEngine GetFasterWorkcart(TrainEngine workcart, TrainEngine otherWorkcart)
        {
            return Math.Abs(workcart.GetTrackSpeed()) >= Math.Abs(otherWorkcart.GetTrackSpeed())
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

        #region Train Car Prefabs

        private class TrainCarPrefab
        {
            private static readonly Dictionary<string, TrainCarPrefab> AllowedPrefabs = new Dictionary<string, TrainCarPrefab>(StringComparer.InvariantCultureIgnoreCase)
            {
                ["WagonA"] = new TrainCarPrefab("WagonA", "assets/content/vehicles/train/trainwagona.entity.prefab"),
                ["WagonB"] = new TrainCarPrefab("WagonB", "assets/content/vehicles/train/trainwagonb.entity.prefab"),
                ["WagonC"] = new TrainCarPrefab("WagonC", "assets/content/vehicles/train/trainwagonc.entity.prefab"),
                ["WagonD"] = new TrainCarPrefab("WagonD", "assets/content/vehicles/train/trainwagond.entity.prefab"),
                ["Workcart"] = new TrainCarPrefab("Workcart", AboveGroundWorkcartPrefab),
            };

            public static TrainCarPrefab FindPrefab(string trainCarName)
            {
                TrainCarPrefab trainCarPrefab;
                return AllowedPrefabs.TryGetValue(trainCarName, out trainCarPrefab)
                    ? trainCarPrefab
                    : null;
            }

            public string TrainCarName;
            public string PrefabPath;

            public TrainCarPrefab(string trainCarName, string prefabPath)
            {
                TrainCarName = trainCarName;
                PrefabPath = prefabPath;
            }
        }

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

            public Vector3 InverseTransformPoint(Vector3 worldPosition) =>
                Quaternion.Inverse(Rotation) * (worldPosition - Position);

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

        private static int EngineThrottleToNumber(EngineSpeeds throttle)
        {
            switch (throttle)
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

        private static EngineSpeeds EngineThrottleFromNumber(int speedNumber)
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

        private static EngineSpeeds DetermineNextThrottle(EngineSpeeds currentThrottle, SpeedInstruction? desiredSpeed, DirectionInstruction? directionInstruction)
        {
            // -3 to 3
            var signedSpeed = EngineThrottleToNumber(currentThrottle);

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

            return EngineThrottleFromNumber(sign * unsignedSpeed);
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

            [JsonProperty("Spawner", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool Spawner;

            [JsonProperty("Commands", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public List<string> Commands;

            [JsonProperty("RotationAngle", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float RotationAngle;

            [JsonProperty("Wagons", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string[] Wagons;

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

            public SpeedInstruction GetSpeedInstructionOrZero() =>
                GetSpeedInstruction() ?? SpeedInstruction.Zero;

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
            public SpeedInstruction GetDepartureSpeedInstruction()
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
                Spawner = triggerData.Spawner;
                Commands = triggerData.Commands;
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

                if (Spawner)
                    return new Color(0, 1, 0.75f);

                if (AddConductor)
                    return Color.cyan;

                var speedInstruction = GetSpeedInstruction();
                var directionInstruction = GetDirectionInstruction();
                var trackSelectionInstruction = GetTrackSelectionInstruction();

                float hue, saturation;

                if (Brake)
                {
                    var brakeSpeedInstruction = GetSpeedInstructionOrZero();

                    // Orange
                    hue = 0.5f/6f;
                    saturation = brakeSpeedInstruction == SpeedInstruction.Zero ? 1
                        : brakeSpeedInstruction == SpeedInstruction.Lo ? 0.8f
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

        #region Spawned Workcart Tracker

        private class SpawnedWorkcartComponent : FacepunchBehaviour
        {
            public static void AddToWorkcart(TrainEngine workcart, BaseTriggerInstance triggerInstance)
            {
                var component = workcart.gameObject.AddComponent<SpawnedWorkcartComponent>();
                component._workcart = workcart;
                component._triggerInstance = triggerInstance;
            }

            private TrainEngine _workcart;
            private BaseTriggerInstance _triggerInstance;

            private void OnDestroy()
            {
                _triggerInstance.HandleWorkcartKilled(_workcart);
            }
        }

        #endregion

        #region Trigger Instances

        private abstract class BaseTriggerInstance
        {
            private const int MaxSpawnedWorkcarts = 1;
            private const float TimeBetweenSpawns = 30;

            public TriggerData TriggerData { get; protected set; }
            public TrainTrackSpline Spline { get; private set; }
            public float DistanceOnSpline { get; private set; }

            public abstract Vector3 WorldPosition { get; }
            public abstract Quaternion WorldRotation { get; }
            public Quaternion SpawnRotation => GetSplineTangentRotation(Spline, DistanceOnSpline, WorldRotation);

            private GameObject _gameObject;
            private WorkcartTrigger _workcartTrigger;
            private List<TrainEngine> _spawnedWorkcarts;

            protected BaseTriggerInstance(TriggerData triggerData)
            {
                TriggerData = triggerData;
            }

            public void CreateTrigger()
            {
                _gameObject = new GameObject();
                OnMove();

                var sphereCollider = _gameObject.AddComponent<SphereCollider>();
                sphereCollider.isTrigger = true;
                sphereCollider.radius = 1;
                sphereCollider.gameObject.layer = 6;

                _workcartTrigger = _gameObject.AddComponent<WorkcartTrigger>();
                _workcartTrigger.TriggerData = TriggerData;
                _workcartTrigger.interestLayers = Layers.Mask.Vehicle_World;

                if (TriggerData.Spawner)
                {
                    StartSpawningWorkcarts();
                }
            }

            public void OnMove()
            {
                if (_gameObject == null)
                    return;

                _gameObject.transform.SetPositionAndRotation(WorldPosition, WorldRotation);

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

            public void OnRotate()
            {
                if (_gameObject == null)
                    return;

                _gameObject.transform.rotation = WorldRotation;
            }

            public void OnEnabledToggled()
            {
                if (TriggerData.Enabled)
                {
                    Enable();
                }
                else
                {
                    Disable();
                }
            }

            public void OnSpawnerToggled()
            {
                if (TriggerData.Spawner)
                {
                    if (TriggerData.Enabled)
                    {
                        StartSpawningWorkcarts();
                    }
                }
                else
                {
                    KillWorkcarts();
                    StopSpawningWorkcarts();
                }
            }

            public void Respawn()
            {
                if (!TriggerData.Spawner || !TriggerData.Enabled)
                    return;

                KillWorkcarts();
                SpawnWorkcart();
            }

            public void HandleWorkcartKilled(TrainEngine workcart)
            {
                _spawnedWorkcarts.Remove(workcart);
            }

            public void Destroy()
            {
                UnityEngine.Object.Destroy(_gameObject);
                KillWorkcarts();
            }

            private void Enable()
            {
                if (_gameObject != null)
                {
                    _gameObject.SetActive(true);
                    if (TriggerData.Spawner)
                    {
                        StartSpawningWorkcarts();
                    }
                }
                else
                {
                    CreateTrigger();
                }
            }

            private void Disable()
            {
                if (_gameObject != null)
                {
                    _gameObject.SetActive(false);
                }

                KillWorkcarts();
                StopSpawningWorkcarts();
            }

            private void StartSpawningWorkcarts()
            {
                if (_spawnedWorkcarts == null)
                {
                    _spawnedWorkcarts = new List<TrainEngine>(MaxSpawnedWorkcarts);
                }

                _workcartTrigger.InvokeRepeating(SpawnWorkcartTracked, UnityEngine.Random.Range(0f, 1f), TimeBetweenSpawns);
            }

            private void StopSpawningWorkcarts()
            {
                _workcartTrigger.CancelInvoke(SpawnWorkcartTracked);
            }

            private void SpawnWorkcart()
            {
                if (_spawnedWorkcarts.Count >= MaxSpawnedWorkcarts)
                    return;

                if (Spline == null)
                    return;

                var worldPosition = WorldPosition;
                var terrainHeight = TerrainMeta.HeightMap.GetHeight(worldPosition);
                var prefab = worldPosition.y - terrainHeight > -1 || (TriggerData.Wagons?.Length ?? 0) > 0
                    ? AboveGroundWorkcartPrefab
                    : WorkcartPrefab;

                var workcart = AutomatedWorkcarts.SpawnWorkcart(prefab, worldPosition, SpawnRotation);
                if (workcart == null)
                    return;

                if (TriggerData.Wagons != null)
                {
                    var trackSelection = DetermineNextTrackSelection(TrackSelection.Default, TriggerData.GetTrackSelectionInstruction());

                    TrainCar previousWagon = workcart;
                    foreach (var wagonName in TriggerData.Wagons)
                    {
                        var trainCarPrefab = TrainCarPrefab.FindPrefab(wagonName);
                        if (trainCarPrefab == null)
                            continue;

                        previousWagon = AddWagon(previousWagon, trainCarPrefab.PrefabPath, trackSelection);
                        if ((object)previousWagon == null)
                            break;
                    }
                }

                _spawnedWorkcarts.Add(workcart);
                SpawnedWorkcartComponent.AddToWorkcart(workcart, this);
            }

            private void SpawnWorkcartTracked()
            {
                _pluginInstance?.TrackStart();
                SpawnWorkcart();
                _pluginInstance?.TrackEnd();
            }

            private void KillWorkcarts()
            {
                if (_spawnedWorkcarts == null)
                    return;

                for (var i = _spawnedWorkcarts.Count - 1; i >= 0; i--)
                {
                    var workcart = _spawnedWorkcarts[i];
                    if (workcart != null && !workcart.IsDestroyed)
                    {
                        foreach (var trainCar in workcart.completeTrain.trainCars.ToList())
                        {
                            if (trainCar != null && !trainCar.IsDestroyed)
                            {
                                trainCar.Kill();
                            }
                        }
                    }
                    _spawnedWorkcarts.RemoveAt(i);
                }
            }
        }

        private class MapTriggerInstance : BaseTriggerInstance
        {
            public override Vector3 WorldPosition => TriggerData.Position;
            public override Quaternion WorldRotation => Quaternion.Euler(0, TriggerData.RotationAngle, 0);

            public MapTriggerInstance(TriggerData triggerData) : base(triggerData) {}
        }

        private class TunnelTriggerInstance : BaseTriggerInstance
        {
            public DungeonCellWrapper DungeonCellWrapper { get; private set; }

            public override Vector3 WorldPosition => DungeonCellWrapper.TransformPoint(TriggerData.Position);
            public override Quaternion WorldRotation => DungeonCellWrapper.Rotation * Quaternion.Euler(0, TriggerData.RotationAngle, 0);

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
                {
                    triggerInstance.OnMove();
                }
            }

            public void OnRotate()
            {
                foreach (var triggerInstance in TriggerInstanceList)
                {
                    triggerInstance.OnRotate();
                }
            }

            public void OnEnabledToggled()
            {
                foreach (var triggerInstance in TriggerInstanceList)
                {
                    triggerInstance.OnEnabledToggled();
                }
            }

            public void OnSpawnerToggled()
            {
                foreach (var triggerInstance in TriggerInstanceList)
                {
                    triggerInstance.OnSpawnerToggled();
                }
            }

            public void Respawn()
            {
                foreach (var triggerInstance in TriggerInstanceList)
                {
                    triggerInstance.Respawn();
                }
            }

            public void Destroy()
            {
                if (TriggerInstanceList == null)
                    return;

                foreach (var triggerInstance in TriggerInstanceList)
                {
                    triggerInstance.Destroy();
                }
            }

            public BaseTriggerInstance FindNearest(Vector3 position, float maxDistanceSquared, out float closestDistanceSquared)
            {
                BaseTriggerInstance closestTrigger = null;
                closestDistanceSquared = float.MaxValue;

                foreach (var triggerInstance in TriggerInstanceList)
                {
                    var distanceSquared = (position - triggerInstance.WorldPosition).sqrMagnitude;
                    if (distanceSquared < closestDistanceSquared && distanceSquared <= maxDistanceSquared)
                    {
                        closestTrigger = triggerInstance;
                        closestDistanceSquared = distanceSquared;
                    }
                }

                return closestTrigger;
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
                var spawnerChanged = triggerData.Spawner != newTriggerData.Spawner;

                triggerData.CopyFrom(newTriggerData);
                triggerData.InvalidateCache();

                if (enabledChanged)
                {
                    triggerController.OnEnabledToggled();

                    if (triggerData.Enabled)
                    {
                        RegisterTriggerWithSpline(triggerController);
                    }
                    else
                    {
                        UnregisterTriggerFromSpline(triggerController);
                    }
                }

                if (spawnerChanged)
                {
                    triggerController.OnSpawnerToggled();
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

            public void RotateTrigger(TriggerData triggerData, float rotationAngle)
            {
                var triggerController = GetTriggerController(triggerData);
                if (triggerController == null)
                    return;

                triggerData.RotationAngle = rotationAngle;
                triggerController.OnRotate();
                SaveTrigger(triggerData);
            }

            public void RespawnTrigger(TriggerData triggerData)
            {
                var triggerController = GetTriggerController(triggerData);
                if (triggerController == null)
                    return;

                triggerController.Respawn();
            }

            public void AddTriggerCommand(TriggerData triggerData, string command)
            {
                if (triggerData.Commands == null)
                {
                    triggerData.Commands = new List<string>();
                }

                if (triggerData.Commands.Contains(command, StringComparer.InvariantCultureIgnoreCase))
                    return;

                triggerData.Commands.Add(command);
                SaveTrigger(triggerData);
            }

            public void RemoveTriggerCommand(TriggerData triggerData, int index)
            {
                triggerData.Commands.RemoveAt(index);
                SaveTrigger(triggerData);
            }

            public void UpdateWagons(TriggerData triggerData, string[] wagonNames)
            {
                if (triggerData.Wagons?.SequenceEqual(wagonNames) ?? false)
                    return;

                triggerData.Wagons = wagonNames;
                RespawnTrigger(triggerData);
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

                if (playerInfo.Timer != null && !playerInfo.Timer.Destroyed)
                {
                    var newDuration = duration >= 0 ? duration : Math.Max(playerInfo.Timer.Repetitions, 60);
                    playerInfo.Timer.Reset(delay: -1, repetitions: newDuration);
                    return;
                }

                if (duration == -1)
                    duration = 60;

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
                    if (triggerData.Spawner)
                    {
                        infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerSpawner));

                        if (triggerData.Spawner && triggerData.Wagons != null && triggerData.Wagons.Length > 0)
                        {
                            var wagonList = new List<string>();
                            for (var i = 0; i < triggerData.Wagons.Length; i++)
                            {
                                var wagonName = triggerData.Wagons[i];
                                if (TrainCarPrefab.FindPrefab(wagonName) != null)
                                {
                                    wagonList.Add(wagonName);
                                }
                            }
                            infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerWagons, string.Join(" ", wagonList)));
                        }

                        var spawnRotation = trigger.SpawnRotation;
                        var arrowBack = spherePosition + Vector3.up + spawnRotation * Vector3.back * 1.5f;
                        var arrowForward = spherePosition + Vector3.up + spawnRotation * Vector3.forward * 1.5f;
                        player.SendConsoleCommand("ddraw.arrow", TriggerDisplayDuration, color, arrowBack, arrowForward, 0.5f);
                    }

                    if (triggerData.AddConductor)
                        infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerAddConductor));

                    var directionInstruction = triggerData.GetDirectionInstruction();
                    var speedInstruction = triggerData.GetSpeedInstruction();

                    // When speed is zero, departure direction will be shown instead of direction.
                    if (directionInstruction != null && speedInstruction != SpeedInstruction.Zero)
                        infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerDirection, directionInstruction));

                    if (triggerData.Brake)
                        infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerBrakeToSpeed, triggerData.GetSpeedInstructionOrZero()));
                    else if (speedInstruction != null)
                        infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerSpeed, speedInstruction));

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

                if (triggerData.Commands != null && triggerData.Commands.Count > 0)
                {
                    var commandList = "";
                    for (var i = 0; i < triggerData.Commands.Count; i++)
                    {
                        commandList += $"\n({i+1}): {triggerData.Commands[i]}";
                    }

                    infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerCommands, commandList));
                }

                var textPosition = trigger.WorldPosition + new Vector3(0, 1.5f + infoLines.Count * 0.075f, 0);
                player.SendConsoleCommand("ddraw.text", TriggerDisplayDuration, color, textPosition, string.Join("\n", infoLines));
            }

            public BaseTriggerInstance FindNearestTrigger(Vector3 position, float maxDistanceSquared = 10)
            {
                BaseTriggerInstance closestTriggerInstance = null;
                float closestDistanceSquared = float.MaxValue;

                foreach (var triggerController in _triggerControllers.Values)
                {
                    float distanceSquared;
                    var triggerInstance = triggerController.FindNearest(position, maxDistanceSquared, out distanceSquared);

                    if (distanceSquared < closestDistanceSquared && distanceSquared <= maxDistanceSquared)
                    {
                        closestTriggerInstance = triggerInstance;
                        closestDistanceSquared = distanceSquared;
                    }
                }

                return closestTriggerInstance;
            }

            public BaseTriggerInstance FindNearestTrigger(Vector3 position, TriggerData triggerData, float maxDistanceSquared = float.MaxValue)
            {
                float distanceSquared;
                return GetTriggerController(triggerData)?.FindNearest(position, maxDistanceSquared, out distanceSquared);
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

        #region Train Manager

        private class TrainManager
        {
            private Dictionary<TrainEngine, TrainController> _trainControllers = new Dictionary<TrainEngine, TrainController>();
            private bool _isUnloading = false;

            public int NumWorkcarts => _trainControllers.Count;
            public TrainEngine[] GetWorkcarts() => _trainControllers.Keys.ToArray();

            public bool IsRegistered(TrainCar trainCar)
            {
                var workcart = trainCar as TrainEngine;
                if ((object)workcart != null)
                {
                    return _trainControllers.ContainsKey(workcart);
                }

                foreach (var possibleWorkcart in _trainControllers.Keys)
                {
                    if (possibleWorkcart.completeTrain == trainCar.completeTrain)
                    {
                        return true;
                    }
                }

                return false;
            }

            public TrainController GetTrainController(TrainEngine workcart)
            {
                TrainController trainController;
                return _trainControllers.TryGetValue(workcart, out trainController)
                    ? trainController
                    : null;
            }

            public bool TryAddTrainController(TrainEngine workcart, TriggerData triggerData = null, WorkcartData workcartData = null)
            {
                if (_pluginInstance.AutomationWasBlocked(workcart))
                    return false;

                if (workcartData == null)
                {
                    workcartData = new WorkcartData
                    {
                        Route = triggerData?.Route,
                    };
                }

                var trainController = TrainController.AddToWorkcart(workcart, this, workcartData);
                if (triggerData != null)
                {
                    trainController.HandleConductorTrigger(triggerData);
                }

                Register(workcart, trainController, workcartData);
                ExposedHooks.OnWorkcartAutomationStarted(workcart);

                return true;
            }

            public void RemoveTrainController(TrainEngine workcart)
            {
                var controller = GetTrainController(workcart);
                if (controller == null)
                    return;

                UnityEngine.Object.DestroyImmediate(controller);
                ExposedHooks.OnWorkcartAutomationStopped(workcart);
            }

            public void Register(TrainEngine workcart, TrainController trainController, WorkcartData workcartData)
            {
                _trainControllers[workcart] = trainController;
                _pluginData.AddWorkcartId(workcart.net.ID, workcartData);
            }

            public void Unregister(TrainEngine workcart, uint netId)
            {
                _trainControllers.Remove(workcart);

                if (!_isUnloading)
                {
                    _pluginData.RemoveWorkcartId(netId);
                }
            }

            public int Unload()
            {
                _isUnloading = true;

                var numWorkcarts = NumWorkcarts;

                foreach (var workcart in _trainControllers.Keys.ToList())
                {
                    RemoveTrainController(workcart);
                }

                return numWorkcarts;
            }

            public void ResendAllGenericMarkers()
            {
                foreach (var trainController in _trainControllers.Values)
                {
                    if (trainController == null)
                        continue;

                    trainController.ResendGenericMarker();
                }
            }

            public void UpdateWorkcartData()
            {
                foreach (var trainController in _trainControllers.Values)
                {
                    if (trainController == null)
                        continue;

                    trainController.UpdateWorkcartData();
                }
            }
        }

        #endregion

        #region Workcart State

        private static float GetThrottleFraction(EngineSpeeds throttle)
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

        private abstract class WorkcartState
        {
            public EngineSpeeds NextThrottle;

            protected TrainController _controller;

            public abstract void Enter();
            public abstract void Exit();

            public WorkcartState(TrainController controller, EngineSpeeds nextThrottle)
            {
                _controller = controller;
                NextThrottle = nextThrottle;
            }
        }

        private class BrakingState : WorkcartState
        {
            public bool IsStopping => _stopDuration != null;

            private float? _stopDuration;
            private EngineSpeeds _targetSpeed => IsStopping ? EngineSpeeds.Zero : NextThrottle;

            public BrakingState(TrainController controller, EngineSpeeds nextThrottle, float? stopDuration = null) : base(controller, nextThrottle)
            {
                _stopDuration = stopDuration;
            }

            public override void Enter()
            {
                var brakeThrottle = DetermineNextThrottle(_controller.DepartureThrottle, SpeedInstruction.Lo, DirectionInstruction.Invert);
                _controller.SetThrottle(brakeThrottle);
                _controller.InvokeRepeatingFixedTime(BrakeUpdate);
            }

            public override void Exit()
            {
                _controller.CancelInvokeFixedTime(BrakeUpdate);
                _controller.SetThrottle(NextThrottle);
            }

            private bool IsNearSpeed(EngineSpeeds desiredThrottle, float leeway = 0.1f)
            {
                var workcart = _controller.Workcart;

                var currentSpeed = Vector3.Dot(_controller.Transform.forward, workcart.GetLocalVelocity());
                var desiredSpeed = workcart.maxSpeed * GetThrottleFraction(desiredThrottle);

                // If desiring a negative speed, current speed is expected to increase (e.g., -10 to -5).
                // If desiring positive speed, current speed is expected to decrease (e.g., 10 to 5).
                // If desiring zero speed, the direction depends on the throttle being applied (e.g., if positive, -10 to -5).
                return desiredSpeed < 0 || (desiredSpeed == 0 && GetThrottleFraction(workcart.CurThrottleSetting) > 0)
                    ? currentSpeed + leeway >= desiredSpeed
                    : currentSpeed - leeway <= desiredSpeed;
            }

            private void BrakeUpdate()
            {
                if (IsNearSpeed(_targetSpeed))
                {
                    if (IsStopping)
                        _controller.SwitchState(new StoppedState(_controller, NextThrottle, (float)_stopDuration));
                    else
                        _controller.SwitchState(null);
                }
            }
        }

        private class StoppedState : WorkcartState
        {
            private float _stopDuration;

            public StoppedState(TrainController controller, EngineSpeeds departureVelocity, float duration) : base(controller, departureVelocity)
            {
                _stopDuration = duration;
            }

            public override void Enter()
            {
                _controller.SetThrottle(EngineSpeeds.Zero);
                _controller.Invoke(DepartFromStop, _stopDuration);
            }

            public override void Exit()
            {
                _controller.CancelInvoke(DepartFromStop);
                _controller.SetThrottle(NextThrottle);
            }

            private void DepartFromStop()
            {
                _controller.SwitchState(null);
            }
        }

        private class ChillingState : WorkcartState
        {
            private const float ChillDuration = 3f;

            public ChillingState(TrainController controller, EngineSpeeds nextThrottle) : base(controller, nextThrottle) {}

            public override void Enter()
            {
                _controller.SetThrottle(EngineSpeeds.Zero);
                _controller.Invoke(StopChilling, ChillDuration);
            }

            public override void Exit()
            {
                _controller.CancelInvoke(StopChilling);
                _controller.SetThrottle(NextThrottle);
            }

            private void StopChilling()
            {
                _controller.SwitchState(null);
            }
        }

        #endregion

        #region Train Controller

        private class TrainController : FacepunchBehaviour
        {
            public static TrainController GetForWorkcart(TrainEngine workcart) =>
                workcart.GetComponent<TrainController>();

            public static TrainController AddToWorkcart(TrainEngine workcart, TrainManager workcartManager, WorkcartData workcartData)
            {
                var controller = GetForWorkcart(workcart);
                if (controller != null)
                    return controller;

                controller = workcart.gameObject.AddComponent<TrainController>();
                controller.Init(workcartManager, workcartData);
                return controller;
            }

            public NPCShopKeeper Conductor { get; private set; }
            public TrainEngine Workcart { get; private set; }
            public Transform Transform { get; private set; }

            private TrainManager _workcartManager;
            private uint _netId;
            private string _netIdString;
            private MapMarkerGenericRadius _genericMarker;
            private VendingMachineMapMarker _vendingMarker;

            private WorkcartState _workcartState;
            private WorkcartData _workcartData;

            public bool IsDestroying => IsInvoking(DestroyCinematically);

            // Desired velocity regardless of circumstances like stopping/braking/chilling.
            public EngineSpeeds DepartureThrottle =>
                _workcartState?.NextThrottle ?? Workcart.CurThrottleSetting;

            private void Awake()
            {
                Workcart = GetComponent<TrainEngine>();
                if (Workcart == null || Workcart.IsDestroyed)
                    return;

                Transform = Workcart.transform;
                Workcart.SetHealth(Workcart.MaxHealth());
                _netId = Workcart.net.ID;
                _netIdString = _netId.ToString();

                AddConductor();
                MaybeAddMapMarkers();
                EnableUnlimitedFuel();
            }

            public void Init(TrainManager workcartManager, WorkcartData workcartData)
            {
                _workcartManager = workcartManager;
                _workcartData = workcartData;

                DisableTrainCoupling(Workcart.completeTrain);

                Workcart.engineController.TryStartEngine(Conductor);

                EnableInvincibility();

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
                _workcartData.Throttle = DepartureThrottle;
                _workcartData.TrackSelection = Workcart.localTrackSelection;
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

            public void SwitchState(WorkcartState nextState)
            {
                _workcartState?.Exit();
                _workcartState = nextState;
                nextState?.Enter();
            }

            public void HandleWorkcartTrigger(TriggerData triggerData)
            {
                if (!triggerData.MatchesRoute(_workcartData.Route))
                    return;

                if (triggerData.Commands != null && triggerData.Commands.Count > 0)
                {
                    foreach (var command in triggerData.Commands)
                    {
                        var fullCommand = IdRegex.Replace(command, _netIdString);
                        if (!string.IsNullOrWhiteSpace(fullCommand))
                        {
                            _pluginInstance?.server.Command(fullCommand);
                        }
                    }
                }

                if (triggerData.Destroy)
                {
                    Invoke(() => Workcart.Kill(BaseNetworkable.DestroyMode.Gib), 0);
                    return;
                }

                var directionInstruction = triggerData.GetDirectionInstruction();
                var departureSpeedInstruction = triggerData.GetDepartureSpeedInstruction();

                var currentDepartureThrottle = DepartureThrottle;
                var newDepartureThrottle = DetermineNextThrottle(currentDepartureThrottle, departureSpeedInstruction, directionInstruction);

                Workcart.SetTrackSelection(DetermineNextTrackSelection(Workcart.localTrackSelection, triggerData.GetTrackSelectionInstruction()));

                if (triggerData.Brake)
                {
                    var brakeSpeedInstruction = triggerData.GetSpeedInstructionOrZero();
                    if (brakeSpeedInstruction == SpeedInstruction.Zero)
                    {
                        SwitchState(new BrakingState(this, newDepartureThrottle, triggerData.GetStopDuration()));
                        return;
                    }

                    var brakeUntilVelocity = DetermineNextThrottle(currentDepartureThrottle, brakeSpeedInstruction, directionInstruction);
                    SwitchState(new BrakingState(this, brakeUntilVelocity));
                    return;
                }

                var speedInstruction = triggerData.GetSpeedInstruction();
                if (speedInstruction == SpeedInstruction.Zero)
                {
                    // Trigger with speed Zero, but no braking.
                    SwitchState(new StoppedState(this, newDepartureThrottle, triggerData.GetStopDuration()));
                    return;
                }

                var nextThrottle = DetermineNextThrottle(DepartureThrottle, speedInstruction, directionInstruction);
                if (_workcartState != null)
                {
                    // Update brake-to speed, departure speed, or post-chill speed.
                    _workcartState.NextThrottle = nextThrottle;
                    return;
                }

                SetThrottle(nextThrottle);
            }

            public void ResendGenericMarker()
            {
                if (_genericMarker != null)
                    _genericMarker.SendUpdate();
            }

            public void StartChilling()
            {
                SwitchState(new ChillingState(this, DepartureThrottle));
            }

            public void DepartEarlyIfStoppedOrStopping()
            {
                SwitchState(null);
            }

            public void SetThrottle(EngineSpeeds engineSpeed)
            {
                Workcart.SetThrottle(engineSpeed);
            }

            private void SetTrackSelection(TrackSelection trackSelection)
            {
                Workcart.SetTrackSelection(trackSelection);
            }

            public void ScheduleDestruction() => Invoke(DestroyCinematically, 0);
            private void DestroyCinematically() => DestroyWorkcart(Workcart);

            private void AddConductor()
            {
                Workcart.DismountAllPlayers();

                Conductor = GameManager.server.CreateEntity(ShopkeeperPrefab, Transform.position) as NPCShopKeeper;
                if (Conductor == null)
                    return;

                Conductor.EnableSaving(false);
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
                foreach (var mountPoint in Workcart.mountPoints)
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
                    _genericMarker = GameManager.server.CreateEntity(GenericMapMarkerPrefab, Transform.position) as MapMarkerGenericRadius;
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
                    _vendingMarker = GameManager.server.CreateEntity(VendingMapMarkerPrefab, Transform.position) as VendingMachineMapMarker;
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
                        _genericMarker.transform.position = Transform.position;
                        _genericMarker.InvalidateNetworkCache();
                        _genericMarker.SendNetworkUpdate_Position();
                    }

                    if (_vendingMarker != null)
                    {
                        _vendingMarker.transform.position = Transform.position;
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

            private void EnableInvincibility()
            {
                Workcart.initialSpawnTime = float.MaxValue;
            }

            private void DisableInvincibility()
            {
                Workcart.initialSpawnTime = Time.time;
            }

            private void DisableHazardChecks()
            {
                Workcart.SetFlag(TrainEngine.Flag_HazardAhead, false);
                Workcart.CancelInvoke(Workcart.CheckForHazards);
            }

            private void EnableHazardChecks()
            {
                if (Workcart.IsOn() && !Workcart.IsInvoking(Workcart.CheckForHazards))
                {
                    Workcart.InvokeRandomized(Workcart.CheckForHazards, 0f, 1f, 0.1f);
                }
            }

            private void EnableUnlimitedFuel()
            {
                var fuelSystem = Workcart.GetFuelSystem();
                fuelSystem.cachedHasFuel = true;
                fuelSystem.nextFuelCheckTime = float.MaxValue;
            }

            private void DisableUnlimitedFuel()
            {
                Workcart.GetFuelSystem().nextFuelCheckTime = 0;
            }

            private void OnDestroy()
            {
                _workcartManager.Unregister(Workcart, _netId);

                if (Conductor != null)
                {
                    Conductor.EnsureDismounted();
                    Conductor.Kill();
                }

                if (_genericMarker != null)
                    _genericMarker.Kill();

                if (_vendingMarker != null)
                    _vendingMarker.Kill();

                if (Workcart != null && !Workcart.IsDestroyed)
                {
                    DisableInvincibility();
                    DisableUnlimitedFuel();
                    EnableHazardChecks();
                    EnableTrainCoupling(Workcart.completeTrain);
                }
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

            // Return example: proceduralmap.1500.548423.212
            private static string GetPerWipeSaveName() =>
                World.SaveFileName.Substring(0, World.SaveFileName.LastIndexOf("."));

            // Return example: proceduralmap.1500.548423
            private static string GetCrossWipeSaveName()
            {
                var saveName = GetPerWipeSaveName();
                return saveName.Substring(0, saveName.LastIndexOf("."));
            }

            private static bool IsProcedural() => World.SaveFileName.StartsWith("proceduralmap");

            private static string GetPerWipeFilePath() => $"{_pluginInstance.Name}/{GetPerWipeSaveName()}";
            private static string GetCrossWipeFilePath() => $"{_pluginInstance.Name}/{GetCrossWipeSaveName()}";
            private static string GetFilepath() => IsProcedural() ? GetPerWipeFilePath() : GetCrossWipeFilePath();

            public static StoredMapData Load()
            {
                var filepath = GetFilepath();

                if (Interface.Oxide.DataFileSystem.ExistsDatafile(filepath))
                    return Interface.Oxide.DataFileSystem.ReadObject<StoredMapData>(filepath) ?? new StoredMapData();

                if (!IsProcedural())
                {
                    var perWipeFilepath = GetPerWipeFilePath();
                    if (Interface.Oxide.DataFileSystem.ExistsDatafile(perWipeFilepath))
                    {
                        var data = Interface.Oxide.DataFileSystem.ReadObject<StoredMapData>(perWipeFilepath);
                        if (data != null)
                        {
                            LogWarning($"Migrating map data file from '{perWipeFilepath}.json' to '{filepath}.json'");
                            data.Save();
                            return data;
                        }
                    }
                }

                return new StoredMapData();
            }

            public StoredMapData Save()
            {
                Interface.Oxide.DataFileSystem.WriteObject(GetFilepath(), this);
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
                    LogWarning($"Automatically relocated {migratedTriggers} tunnel triggers to bypass tunnels.");
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
                        LogError($"Invalid item short name in config: '{ShortName}'");

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
            [JsonProperty("EnableTerrainCollision")]
            public bool EnableTerrainCollision = true;

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
             ? GetMessage(player, Lang.InfoConductorCountLimited, _trainManager.NumWorkcarts, _pluginConfig.MaxConductors)
             : GetMessage(player, Lang.InfoConductorCountUnlimited, _trainManager.NumWorkcarts);

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
            public const string ErrorRequiresSpawnTrigger = "Error.RequiresSpawnTrigger";
            public const string ErrorTriggerDisabled = "Error.TriggerDisabled";
            public const string ErrorUnrecognizedWagon = "Error.UnrecognizedWagon";

            public const string ToggleOnSuccess = "Toggle.Success.On";
            public const string ToggleOnWithRouteSuccess = "Toggle.Success.On.WithRoute";
            public const string ToggleOffSuccess = "Toggle.Success.Off";
            public const string ResetAllSuccess = "ResetAll.Success";
            public const string ShowTriggersSuccess = "ShowTriggers.Success";
            public const string ShowTriggersWithRouteSuccess = "ShowTriggers.WithRoute.Success";

            public const string AddTriggerSyntax = "AddTrigger.Syntax";
            public const string AddTriggerSuccess = "AddTrigger.Success";
            public const string MoveTriggerSuccess = "MoveTrigger.Success";
            public const string RotateTriggerSuccess = "RotateTrigger.Success";
            public const string UpdateTriggerSyntax = "UpdateTrigger.Syntax";
            public const string UpdateTriggerSuccess = "UpdateTrigger.Success";
            public const string SimpleTriggerSyntax = "Trigger.SimpleSyntax";
            public const string RemoveTriggerSuccess = "RemoveTrigger.Success";

            public const string AddCommandSyntax = "AddCommand.Syntax";
            public const string RemoveCommandSyntax = "RemoveCommand.Syntax";
            public const string RemoveCommandErrorIndex = "RemoveCommand.Error.Index";

            public const string InfoConductorCountLimited = "Info.ConductorCount.Limited";
            public const string InfoConductorCountUnlimited = "Info.ConductorCount.Unlimited";

            public const string HelpSpeedOptions = "Help.SpeedOptions";
            public const string HelpDirectionOptions = "Help.DirectionOptions";
            public const string HelpTrackSelectionOptions = "Help.TrackSelectionOptions";
            public const string HelpOtherOptions = "Help.OtherOptions2";

            public const string InfoTrigger = "Info.Trigger";
            public const string InfoTriggerMapPrefix = "Info.Trigger.Prefix.Map";
            public const string InfoTriggerTunnelPrefix = "Info.Trigger.Prefix.Tunnel";

            public const string InfoTriggerrDisabled = "Info.Trigger.Disabled";
            public const string InfoTriggerMap = "Info.Trigger.Map";
            public const string InfoTriggerRoute = "Info.Trigger.Route";
            public const string InfoTriggerTunnel = "Info.Trigger.Tunnel";
            public const string InfoTriggerSpawner = "Info.Trigger.Spawner";
            public const string InfoTriggerWagons = "Info.Trigger.Wagons";
            public const string InfoTriggerAddConductor = "Info.Trigger.Conductor";
            public const string InfoTriggerDestroy = "Info.Trigger.Destroy";
            public const string InfoTriggerStopDuration = "Info.Trigger.StopDuration";

            public const string InfoTriggerSpeed = "Info.Trigger.Speed";
            public const string InfoTriggerBrakeToSpeed = "Info.Trigger.BrakeToSpeed";
            public const string InfoTriggerDepartureSpeed = "Info.Trigger.DepartureSpeed";
            public const string InfoTriggerDirection = "Info.Trigger.Direction";
            public const string InfoTriggerDepartureDirection = "Info.Trigger.DepartureDirection";
            public const string InfoTriggerTrackSelection = "Info.Trigger.TrackSelection";
            public const string InfoTriggerCommands = "Info.Trigger.Command";
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
                [Lang.ErrorRequiresSpawnTrigger] = "Error: That is not a spawn trigger.",
                [Lang.ErrorTriggerDisabled] = "Error: That trigger is disabled.",
                [Lang.ErrorUnrecognizedWagon] = "Error: Unrecognized wagon: {0}.",

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
                [Lang.RotateTriggerSuccess] = "Successfully rotated trigger #<color=#fd4>{0}{1}</color>",
                [Lang.SimpleTriggerSyntax] = "Syntax: <color=#fd4>{0} <id></color>",
                [Lang.RemoveTriggerSuccess] = "Trigger #<color=#fd4>{0}{1}</color> successfully removed.",

                [Lang.AddCommandSyntax] = "Syntax: <color=#fd4>{0} <id> <command></color>",
                [Lang.RemoveCommandSyntax] = "Syntax: <color=#fd4>{0} <id> <number></color>",
                [Lang.RemoveCommandErrorIndex] = "Error: Invalid command index <color=#fd4>{0}</color>.",

                [Lang.InfoConductorCountLimited] = "Total conductors: <color=#fd4>{0}/{1}</color>.",
                [Lang.InfoConductorCountUnlimited] = "Total conductors: <color=#fd4>{0}</color>.",

                [Lang.HelpSpeedOptions] = "Speeds: {0}",
                [Lang.HelpDirectionOptions] = "Directions: {0}",
                [Lang.HelpTrackSelectionOptions] = "Track selection: {0}",
                [Lang.HelpOtherOptions] = "Other options: <color=#fd4>Spawn</color> | <color=#fd4>Conductor</color> | <color=#fd4>Brake</color> | <color=#fd4>Destroy</color> | <color=#fd4>@ROUTE_NAME</color> | <color=#fd4>Enabled</color> | <color=#fd4>Disabled</color>",

                [Lang.InfoTrigger] = "Workcart Trigger #{0}{1}",
                [Lang.InfoTriggerMapPrefix] = "M",
                [Lang.InfoTriggerTunnelPrefix] = "T",

                [Lang.InfoTriggerrDisabled] = "DISABLED",
                [Lang.InfoTriggerMap] = "Map-specific",
                [Lang.InfoTriggerRoute] = "Route: @{0}",
                [Lang.InfoTriggerTunnel] = "Tunnel type: {0} (x{1})",
                [Lang.InfoTriggerSpawner] = "Spawns workcart",
                [Lang.InfoTriggerAddConductor] = "Adds Conductor",
                [Lang.InfoTriggerWagons] = "Wagons: {0}",
                [Lang.InfoTriggerDestroy] = "Destroys workcart",
                [Lang.InfoTriggerStopDuration] = "Stop duration: {0}s",

                [Lang.InfoTriggerSpeed] = "Speed: {0}",
                [Lang.InfoTriggerBrakeToSpeed] = "Brake to speed: {0}",
                [Lang.InfoTriggerDepartureSpeed] = "Departure speed: {0}",
                [Lang.InfoTriggerDirection] = "Direction: {0}",
                [Lang.InfoTriggerDepartureDirection] = "Departure direction: {0}",
                [Lang.InfoTriggerTrackSelection] = "Track selection: {0}",
                [Lang.InfoTriggerCommands] = "Commands: {0}",
            }, this, "en");

            // Brazilian Portuguese
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.ErrorNoPermission] = "Você não tem permissão para fazer isso.",
                [Lang.ErrorNoTriggers] = "Não há gatilhos de carrinho de trabalho neste mapa.",
                [Lang.ErrorTriggerNotFound] = "Erro: Trigger id #<color=#fd4>{0}{1}</color> não encontrado.",
                [Lang.ErrorNoTrackFound] = "Erro: nenhuma trilha encontrada nas proximidades.",
                [Lang.ErrorNoWorkcartFound] = "Erro: Nenhum carrinho de trabalho encontrado.",
                [Lang.ErrorAutomateBlocked] = "Erro: outro plug-in bloqueado automatizando esse carrinho de trabalho.",
                [Lang.ErrorUnsupportedTunnel] = "Erro: não é um túnel ferroviário compatível.",
                [Lang.ErrorTunnelTypeDisabled] = "Erro: o tipo de túnel <color=#fd4>{0}</color> está atualmente desativado.",
                [Lang.ErrorMapTriggersDisabled] = "Erro: os gatilhos do mapa estão desativados.",
                [Lang.ErrorMaxConductors] = "Erro: já existem <color=#fd4>{0}</color> de <color=#fd4>{1}</color>condutores.",
                [Lang.ErrorWorkcartOwned] = "Erro: esse carrinho de trabalho tem um proprietário.",
                [Lang.ErrorNoAutomatedWorkcarts] = "Erro: não há carrinhos de trabalho automatizados.",
                [Lang.ErrorRequiresSpawnTrigger] = "Erro: Isso não é um gatilho de desova.",
                [Lang.ErrorTriggerDisabled] = "Erro: esse gatilho está desativado.",
                [Lang.ErrorUnrecognizedWagon] = "Erro: Vagão de trem não reconhecido: {0}.",

                [Lang.ToggleOnSuccess] = "Esse carrinho de trabalho agora é automatizado.",
                [Lang.ToggleOnWithRouteSuccess] = "Esse carrinho de trabalho agora é automatizado com rota <color=#fd4>@{0}</color>.",
                [Lang.ToggleOffSuccess] = "Esse carrinho de trabalho não é mais automatizado.",
                [Lang.ResetAllSuccess] = "Todos os {0} condutores foram removidos.",
                [Lang.ShowTriggersSuccess] = "Mostrando todos os gatilhos para <color=#fd4>{0}</color>.",
                [Lang.ShowTriggersWithRouteSuccess] = "Mostrando todos os gatilhos para a rota <color=#fd4>@{0}</color> para <color=#fd4>{1}</color>",

                [Lang.AddTriggerSyntax] = "Syntax: <color=#fd4>{0} <option1> <option2> ...</color>\n{1}",
                [Lang.AddTriggerSuccess] = "Gatilho adicionado com sucesso #<color=#fd4>{0}{1}</color>.",
                [Lang.UpdateTriggerSyntax] = "Syntax: <color=#fd4>{0} <id> <option1> <option2> ...</color>\n{1}",
                [Lang.UpdateTriggerSuccess] = "Gatilho atualizado com sucesso #<color=#fd4>{0}{1}</color>",
                [Lang.MoveTriggerSuccess] = "Gatilho movido com sucesso #<color=#fd4>{0}{1}</color>",
                [Lang.RotateTriggerSuccess] = "Gatilho girado com sucesso #<color=#fd4>{0}{1}</color>",
                [Lang.SimpleTriggerSyntax] = "Syntax: <color=#fd4>{0} <id></color>",
                [Lang.RemoveTriggerSuccess] = "Trigger #<color=#fd4>{0}{1}</color> removido com sucesso.",

                [Lang.AddCommandSyntax] = "Syntax: <color=#fd4>{0} <id> <comando></color>",
                [Lang.RemoveCommandSyntax] = "Syntax: <color=#fd4>{0} <id> <número></color>",
                [Lang.RemoveCommandErrorIndex] = "Erro: índice de comando inválido <color=#fd4>{0}</color>.",

                [Lang.InfoConductorCountLimited] = "Condutores totais: <color=#fd4>{0}/{1}</color>.",
                [Lang.InfoConductorCountUnlimited] = "Condutores totais: <color=#fd4>{0}</color>.",

                [Lang.HelpSpeedOptions] = "Velocidades: {0}",
                [Lang.HelpDirectionOptions] = "Direções: {0}",
                [Lang.HelpTrackSelectionOptions] = "Seleção de faixa: {0}",
                [Lang.HelpOtherOptions] = "Outras opções: <color=#fd4>Spawn</color> | <color=#fd4>Conductor</color> | <color=#fd4>Brake</color> | <color=#fd4>Destroy</color> | <color=#fd4>@ROUTE_NAME</color> | <color=#fd4>Enabled</color> | <color=#fd4>Disabled</color>",

                [Lang.InfoTrigger] = "Acionador de carrinho de trabalho #{0}{1}",
                [Lang.InfoTriggerMapPrefix] = "M",
                [Lang.InfoTriggerTunnelPrefix] = "T",

                [Lang.InfoTriggerrDisabled] = "DESATIVADO",
                [Lang.InfoTriggerMap] = "Específico do mapa",
                [Lang.InfoTriggerRoute] = "Rota: @{0}",
                [Lang.InfoTriggerTunnel] = "Tipo de túnel: {0} (x{1})",
                [Lang.InfoTriggerSpawner] = "Gera carrinho de trabalho",
                [Lang.InfoTriggerAddConductor] = "Adiciona Condutor",
                [Lang.InfoTriggerWagons] = "Vagões de trem: {0}",
                [Lang.InfoTriggerDestroy] = "Destrói o carrinho de trabalho",
                [Lang.InfoTriggerStopDuration] = "Duração da parada: {0}s",

                [Lang.InfoTriggerSpeed] = "Velocidade: {0}",
                [Lang.InfoTriggerBrakeToSpeed] = "Freie para aumentar a velocidade: {0}",
                [Lang.InfoTriggerDepartureSpeed] = "Velocidade de partida: {0}",
                [Lang.InfoTriggerDirection] = "Direção: {0}",
                [Lang.InfoTriggerDepartureDirection] = "Direção de partida: {0}",
                [Lang.InfoTriggerTrackSelection] = "Seleção de faixa: {0}",
                [Lang.InfoTriggerCommands] = "Eventos: {0}",
            }, this, "pt-BR");
        }

        #endregion
    }
}
