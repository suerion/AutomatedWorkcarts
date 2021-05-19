using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Rust;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static TrainEngine;
using static TrainTrackSpline;

namespace Oxide.Plugins
{
    [Info("Automated Workcarts", "WhiteThunder", "0.3.0")]
    [Description("Spawns conductor NPCs that drive workcarts between stations.")]
    internal class AutomatedWorkcarts : CovalencePlugin
    {
        #region Fields

        private static AutomatedWorkcarts _pluginInstance;
        private static Configuration _pluginConfig;
        private static StoredPluginData _pluginData;
        private static StoredMapData _mapData;

        private const string PermissionToggle = "automatedworkcarts.toggle";
        private const string PermissionManageTriggers = "automatedworkcarts.managetriggers";

        private const string PlayerPrefab = "assets/prefabs/player/player.prefab";
        private const string VendingMachineMapMarkerPrefab = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";
        private const string DroneMapMarkerPrefab = "assets/prefabs/misc/marketplace/deliverydronemarker.prefab";

        private static readonly Vector3 TriggerOffsetFromWorkcart = new Vector3(0, 1, 0);

        private TrainStationManager _trainStationManager = new TrainStationManager();
        private CustomTriggerManager _customTriggerManager = new CustomTriggerManager();

        #endregion

        #region Hooks

        private void Init()
        {
            _pluginInstance = this;
            _pluginData = StoredPluginData.Load();
            _mapData = StoredMapData.Load();

            permission.RegisterPermission(PermissionToggle, this);
            permission.RegisterPermission(PermissionManageTriggers, this);

            Unsubscribe(nameof(OnEntitySpawned));
        }

        private void Unload()
        {
            _trainStationManager.DestroyAll();
            _customTriggerManager.DestroyAll();

            TrainController.DestroyAll();

            _mapData = null;
            _pluginData = null;
            _pluginConfig = null;
            _pluginInstance = null;
        }

        private void OnServerInitialized()
        {
            _customTriggerManager.CreateAll();

            if (_pluginConfig.AutoDetectStations)
                _trainStationManager.CreateAll();

            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var workcart = entity as TrainEngine;
                if (workcart != null && (_pluginConfig.AutomateAllWorkcarts || _pluginData.AutomatedWorkcardIds.Contains(workcart.net.ID)))
                    TryAddTrainController(workcart);
            }

            if (_pluginConfig.AutomateAllWorkcarts)
                Subscribe(nameof(OnEntitySpawned));
        }

        private void OnNewSave()
        {
            _pluginData = StoredPluginData.Clear();
        }

        private void OnEntitySpawned(TrainEngine workcart)
        {
            // Delay so that other plugins that spawn workcarts can save its net id to allow blocking automation.
            NextTick(() =>
            {
                if (workcart != null)
                    TryAddTrainController(workcart);
            });
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

        private void OnEntityEnter(TriggerCustom trigger, TrainEngine workcart)
        {
            var trainController = workcart.GetComponent<TrainController>();
            if (trainController == null)
            {
                if (trigger.TriggerInfo.StartsAutomation)
                {
                    TryAddTrainController(workcart, trigger.TriggerInfo);
                    if (!_pluginConfig.AutomateAllWorkcarts)
                        _pluginData.AddWorkcart(workcart);
                }

                return;
            }

            if (trigger.entityContents?.Contains(workcart) ?? false)
                return;

            trainController.HandleCustomTrigger(trigger.TriggerInfo);
        }

        private void OnEntityEnter(TriggerStation trigger, TrainEngine workcart)
        {
            var trainController = workcart.GetComponent<TrainController>();
            if (trainController == null)
                return;

            trigger.StationTrack.OnTrainArrive(trainController);
        }

        private void OnEntityEnter(TriggerStationStop trigger, TrainEngine workcart)
        {
            var trainController = workcart.GetComponent<TrainController>();
            if (trainController == null)
                return;

            trigger.StationTrack.OnTrainReachStop(trainController);
        }

        private void OnEntityLeave(TriggerStation trigger, TrainEngine workcart)
        {
            if (workcart == null || trigger is TriggerStationStop)
                return;

            var trainController = workcart.GetComponent<TrainController>();
            if (trainController == null)
                return;

            trigger.StationTrack.OnTrainDepart(trainController);
        }

        #endregion

        #region Commands

        [Command("aw.toggle")]
        private void CommandAutomateWorkcart(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer)
                return;

            if (!player.HasPermission(PermissionToggle))
            {
                ReplyToPlayer(player, Lang.ErrorNoPermission);
                return;
            }

            if (_pluginConfig.AutomateAllWorkcarts)
            {
                ReplyToPlayer(player, Lang.ErrorFullyAutomated);
                return;
            }

            var basePlayer = player.Object as BasePlayer;

            var workcart = GetPlayerCart(basePlayer);
            if (workcart == null)
            {
                ReplyToPlayer(player, Lang.ErrorNoWorkcartFound);
                return;
            }

            var trainController = workcart.GetComponent<TrainController>();
            if (trainController == null)
            {
                if (TryAddTrainController(workcart))
                {
                    _pluginData.AddWorkcart(workcart);
                    ReplyToPlayer(player, Lang.ToggleOnSuccess);
                }
                else
                    ReplyToPlayer(player, Lang.ErrorAutomateBlocked);
            }
            else
            {
                UnityEngine.Object.Destroy(trainController);
                ReplyToPlayer(player, Lang.ToggleOffSuccess);
                _pluginData.RemoveWorkcart(workcart);
            }
        }

        [Command("aw.addtrigger", "awt.add")]
        private void CommandAddTrigger(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer
                || !VerifyPermission(player, PermissionManageTriggers))
                return;

            var basePlayer = player.Object as BasePlayer;

            Vector3 trackPosition;
            if (!TryGetTrackPosition(basePlayer, out trackPosition))
            {
                ReplyToPlayer(player, Lang.ErrorNoTrackFound);
                return;
            }

            var triggerInfo = new CustomTriggerInfo() { Position = trackPosition };

            if (args.Length == 0)
            {
                triggerInfo.EngineSpeed = EngineSpeeds.Zero.ToString();
            }
            else
            {
                foreach (var arg in args)
                {
                    if (!TryParseArg(player, cmd, arg, triggerInfo, Lang.AddTriggerSyntax))
                        return;
                }
            }

            _customTriggerManager.AddTrigger(triggerInfo);
            _customTriggerManager.ShowAllToPlayer(basePlayer);
            ReplyToPlayer(player, Lang.AddTriggerSuccess, triggerInfo.Id);
        }

        [Command("aw.updatetrigger", "awt.update")]
        private void CommandUpdateTrigger(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer
                || !VerifyPermission(player, PermissionManageTriggers))
                return;

            int triggerId;
            if (args.Length < 2 || !int.TryParse(args[0], out triggerId))
            {
                ReplyToPlayer(player, Lang.UpdateTriggerSyntax, cmd, GetEnumOptions<EngineSpeeds>(), GetEnumOptions<TrackSelection>());
                return;
            }

            var basePlayer = player.Object as BasePlayer;

            CustomTriggerInfo triggerInfo;
            if (!VerifyTriggerExists(player, triggerId, out triggerInfo))
                return;

            foreach (var arg in args.Skip(1))
            {
                if (!TryParseArg(player, cmd, arg, triggerInfo, Lang.UpdateTriggerSyntax))
                    return;
            }

            _customTriggerManager.UpdateTrigger(triggerInfo);
            _customTriggerManager.ShowAllToPlayer(basePlayer);
            ReplyToPlayer(player, Lang.UpdateTriggerSuccess, triggerInfo.Id);
        }

        [Command("aw.movetrigger", "awt.move")]
        private void CommandMoveTrigger(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer
                || !VerifyPermission(player, PermissionManageTriggers))
                return;

            int triggerId;
            if (args.Length < 1 || !int.TryParse(args[0], out triggerId))
            {
                ReplyToPlayer(player, Lang.RemoveTriggerSyntax, cmd);
                return;
            }

            var basePlayer = player.Object as BasePlayer;

            CustomTriggerInfo triggerInfo;
            Vector3 trackPosition;

            if (!VerifyTriggerExists(player, triggerId, out triggerInfo)
                || !VerifyTrackPosition(player, out trackPosition))
                return;

            _customTriggerManager.MoveTrigger(triggerInfo, trackPosition);
            _customTriggerManager.ShowAllToPlayer(basePlayer);
            ReplyToPlayer(player, Lang.MoveTriggerSuccess, triggerInfo.Id);
        }

        [Command("aw.removetrigger", "awt.remove")]
        private void CommandRemoveTrigger(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer
                || !VerifyPermission(player, PermissionManageTriggers))
                return;

            int triggerId;
            if (args.Length < 1 || !int.TryParse(args[0], out triggerId))
            {
                ReplyToPlayer(player, Lang.RemoveTriggerSyntax, cmd);
                return;
            }

            var basePlayer = player.Object as BasePlayer;
            var triggerInfo = _customTriggerManager.FindTrigger(triggerId);
            if (triggerInfo == null)
            {
                ReplyToPlayer(player, Lang.ErrorTriggerNotFound, triggerId);
                _customTriggerManager.ShowAllToPlayer(basePlayer);
                return;
            }

            _customTriggerManager.RemoveTrigger(triggerInfo);
            _customTriggerManager.ShowAllToPlayer(basePlayer);
            ReplyToPlayer(player, Lang.RemoveTriggerSuccess, triggerId);
        }

        [Command("aw.showtriggers")]
        private void CommandShowStops(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer
                || !VerifyPermission(player, PermissionManageTriggers))
                return;

            if (_mapData.CustomTriggers.Count == 0)
            {
                ReplyToPlayer(player, Lang.ErrorNoTriggers);
                return;
            }

            _customTriggerManager.ShowAllToPlayer(player.Object as BasePlayer);
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

        private bool VerifyTriggerExists(IPlayer player, int triggerId, out CustomTriggerInfo triggerInfo)
        {
            triggerInfo = _customTriggerManager.FindTrigger(triggerId);
            if (triggerInfo != null)
                return true;

            ReplyToPlayer(player, Lang.ErrorTriggerNotFound, triggerId);
            _customTriggerManager.ShowAllToPlayer(player.Object as BasePlayer);
            return false;
        }

        private bool VerifyTrackPosition(IPlayer player, out Vector3 trackPosition, float distanceFromHit = 0)
        {
            if (TryGetTrackPosition(player.Object as BasePlayer, out trackPosition, distanceFromHit))
                return true;

            ReplyToPlayer(player, Lang.ErrorNoTrackFound);
            return false;
        }

        #endregion

        #region Helper Methods

        private static bool AutomationWasBlocked(TrainEngine workcart)
        {
            object hookResult = Interface.CallHook("OnWorkcartAutomate", workcart);
            return hookResult is bool && (bool)hookResult == false;
        }

        private static bool TryAddTrainController(TrainEngine workcart, CustomTriggerInfo triggerInfo = null)
        {
            if (AutomationWasBlocked(workcart))
                return false;

            var trainController = workcart.gameObject.AddComponent<TrainController>();
            if (triggerInfo != null)
                trainController.StartImmediately(triggerInfo);

            workcart.SetHealth(workcart.MaxHealth());
            Interface.CallHook("OnWorkcartSafeZoneCreated", workcart);

            return true;
        }

        private static float DistanceToNearestElevator(BaseEntity entity)
        {
            var shortestDistance = float.MaxValue;

            var position = entity.transform.position;
            foreach (var ent in BaseNetworkable.serverEntities)
            {
                var elevator = ent as ElevatorStatic;
                if (elevator == null || elevator.Floor != 0)
                    continue;

                var distance = entity.Distance(elevator);
                if (distance < shortestDistance)
                    shortestDistance = distance;
            }

            return shortestDistance;
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

        private static bool TryGetTrackPosition(BasePlayer player, out Vector3 trackPosition, float distanceFromHit = 0, float maxDistance = 20)
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

            trackPosition = spline.GetPosition(distanceResult + distanceFromHit);
            return true;
        }

        private bool TryParseArg(IPlayer player, string cmd, string arg, CustomTriggerInfo triggerInfo, string errorMessageName)
        {
            if (arg.ToLower() == "start")
            {
                triggerInfo.StartsAutomation = true;
                return true;
            }

            EngineSpeeds engineSpeed;
            if (Enum.TryParse<EngineSpeeds>(arg, true, out engineSpeed))
            {
                triggerInfo.EngineSpeed = engineSpeed.ToString();
                return true;
            }

            TrackSelection trackSelection;
            if (Enum.TryParse<TrackSelection>(arg, true, out trackSelection))
            {
                triggerInfo.TrackSelection = trackSelection.ToString();
                return true;
            }

            ReplyToPlayer(player, errorMessageName, cmd, GetEnumOptions<EngineSpeeds>(), GetEnumOptions<TrackSelection>());
            return false;
        }

        private static BaseEntity GetLookEntity(BasePlayer player, float maxDistance = 20)
        {
            RaycastHit hit;
            return Physics.Raycast(player.eyes.HeadRay(), out hit, maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                ? hit.GetEntity()
                : null;
        }

        private static TrainEngine GetMountedCart(BasePlayer player)
        {
            var mountedWorkcart = player.GetMountedVehicle() as TrainEngine;
            if (mountedWorkcart != null)
                return mountedWorkcart;

            var parentWorkcart = player.GetParentEntity() as TrainEngine;
            if (parentWorkcart != null)
                return parentWorkcart;

            return null;
        }

        private static TrainEngine GetPlayerCart(BasePlayer player) =>
            GetLookEntity(player) as TrainEngine ?? GetMountedCart(player);

        #endregion

        #region Custom Triggers

        private class TriggerCustom : TriggerBase
        {
            public CustomTriggerInfo TriggerInfo;
        }

        private enum SpeedDirection { Forward, Reverse, None }

        private class CustomTriggerInfo
        {
            [JsonProperty("Id")]
            public int Id;

            [JsonProperty("Position")]
            public Vector3 Position;

            [JsonProperty("StartsAutomation", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool StartsAutomation = false;

            [JsonProperty("EngineSpeed", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string EngineSpeed;

            [JsonProperty("TrackSelection", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string TrackSelection;

            private EngineSpeeds? _engineSpeed;
            public bool TryGetEngineSpeed(out EngineSpeeds engineSpeed)
            {
                engineSpeed = EngineSpeeds.Zero;

                if (string.IsNullOrEmpty(EngineSpeed))
                    return false;

                if (_engineSpeed != null)
                {
                    engineSpeed = (EngineSpeeds)_engineSpeed;
                    return true;
                }

                if (TryParseEngineSpeed(EngineSpeed, out engineSpeed))
                {
                    _engineSpeed = engineSpeed;
                    return true;
                }

                return false;
            }

            private TrackSelection? _trackSelection;
            public bool TryGetTrackSelection(out TrackSelection trackSelection)
            {
                trackSelection = TrainTrackSpline.TrackSelection.Default;

                if (string.IsNullOrEmpty(TrackSelection))
                    return false;

                if (_trackSelection != null)
                {
                    trackSelection = (TrackSelection)_trackSelection;
                    return true;
                }

                if (TryParseTrackSelection(TrackSelection, out trackSelection))
                {
                    _trackSelection = trackSelection;
                    return true;
                }

                return false;
            }

            public void InvalidateCache()
            {
                _engineSpeed = null;
                _trackSelection = null;
            }

            public Color GetColor()
            {
                if (StartsAutomation)
                    return Color.cyan;

                EngineSpeeds engineSpeed;
                if (!TryGetEngineSpeed(out engineSpeed))
                {
                    TrackSelection trackSelection;
                    if (TryGetTrackSelection(out trackSelection))
                        return Color.yellow;
                    else
                        return Color.white;
                }

                switch (engineSpeed)
                {
                    case EngineSpeeds.Fwd_Hi:
                        return Color.HSVToRGB(1 / 3f, 1, 1);
                    case EngineSpeeds.Fwd_Med:
                        return Color.HSVToRGB(1 / 3f, 0.75f, 1);
                    case EngineSpeeds.Fwd_Lo:
                        return Color.HSVToRGB(1 / 3f, 0.5f, 1);

                    case EngineSpeeds.Rev_Hi:
                        return Color.HSVToRGB(0, 1, 1);
                    case EngineSpeeds.Rev_Med:
                        return Color.HSVToRGB(0, 0.75f, 1);
                    case EngineSpeeds.Rev_Lo:
                        return Color.HSVToRGB(0, 0.5f, 1);

                    default:
                        return Color.white;
                }
            }

            public SpeedDirection GetDirection(out int magnitude)
            {
                EngineSpeeds engineSpeed;
                if (!TryGetEngineSpeed(out engineSpeed))
                {
                    magnitude = 0;
                    return SpeedDirection.None;
                }

                switch (engineSpeed)
                {
                    case EngineSpeeds.Fwd_Hi:
                        magnitude = 3;
                        return SpeedDirection.Forward;

                    case EngineSpeeds.Fwd_Med:
                        magnitude = 2;
                        return SpeedDirection.Forward;

                    case EngineSpeeds.Fwd_Lo:
                        magnitude = 1;
                        return SpeedDirection.Forward;

                    case EngineSpeeds.Rev_Hi:
                        magnitude = 3;
                        return SpeedDirection.Reverse;

                    case EngineSpeeds.Rev_Med:
                        magnitude = 2;
                        return SpeedDirection.Reverse;

                    case EngineSpeeds.Rev_Lo:
                        magnitude = 1;
                        return SpeedDirection.Reverse;

                    default:
                        magnitude = 0;
                        return SpeedDirection.None;
                }
            }
        }

        private class CustomTriggerWrapper
        {
            private CustomTriggerInfo _triggerInfo;
            private GameObject _gameObject;

            public CustomTriggerWrapper(CustomTriggerInfo triggerInfo)
            {
                _triggerInfo = triggerInfo;
                CreateTrigger();
            }

            private void CreateTrigger()
            {
                _gameObject = new GameObject();
                _gameObject.transform.position = _triggerInfo.Position + TriggerOffsetFromWorkcart;

                var sphereCollider = _gameObject.AddComponent<SphereCollider>();
                sphereCollider.isTrigger = true;
                sphereCollider.radius = 1;
                sphereCollider.gameObject.layer = 6;

                var trigger = _gameObject.AddComponent<TriggerCustom>();
                trigger.TriggerInfo = _triggerInfo;
                trigger.interestLayers = Layers.Mask.Vehicle_World;
            }

            public void Move(Vector3 position)
            {
                _gameObject.transform.position = position;
            }

            public void Destroy()
            {
                UnityEngine.Object.Destroy(_gameObject);
            }
        }

        private class CustomTriggerManager
        {
            private const float TriggerDisplayDuration = 1f;
            private const float TriggerDisplayRadius = 1f;

            private Dictionary<CustomTriggerInfo, CustomTriggerWrapper> _customTriggers = new Dictionary<CustomTriggerInfo, CustomTriggerWrapper>();
            private Dictionary<ulong, Timer> _drawTimers = new Dictionary<ulong, Timer>();

            private int GetHighestTriggerId()
            {
                var highestTriggerId = 0;

                foreach (var triggerInfo in _customTriggers.Keys)
                    highestTriggerId = Math.Max(highestTriggerId, triggerInfo.Id);

                return highestTriggerId;
            }

            public CustomTriggerInfo FindTrigger(int triggerId)
            {
                foreach (var triggerInfo in _customTriggers.Keys)
                {
                    if (triggerInfo.Id == triggerId)
                        return triggerInfo;
                }

                return null;
            }

            public void AddTrigger(CustomTriggerInfo triggerInfo)
            {
                if (triggerInfo.Id == 0)
                    triggerInfo.Id = GetHighestTriggerId() + 1;

                _customTriggers[triggerInfo] = new CustomTriggerWrapper(triggerInfo);
                _mapData.AddTrigger(triggerInfo);
            }

            public void UpdateTrigger(CustomTriggerInfo triggerInfo)
            {
                triggerInfo.InvalidateCache();
                _mapData.Save();
            }

            public void MoveTrigger(CustomTriggerInfo triggerInfo, Vector3 position)
            {
                triggerInfo.Position = position;
                _mapData.Save();

                CustomTriggerWrapper customTrigger;
                if (_customTriggers.TryGetValue(triggerInfo, out customTrigger))
                    customTrigger.Move(position);
            }

            public void RemoveTrigger(CustomTriggerInfo triggerInfo)
            {
                CustomTriggerWrapper customTrigger;
                if (_customTriggers.TryGetValue(triggerInfo, out customTrigger))
                {
                    customTrigger.Destroy();
                    _customTriggers.Remove(triggerInfo);
                }

                _mapData.RemoveTrigger(triggerInfo);
            }

            public void CreateAll()
            {
                foreach (var triggerInfo in _mapData.CustomTriggers)
                    _customTriggers[triggerInfo] = new CustomTriggerWrapper(triggerInfo);
            }

            public void DestroyAll()
            {
                foreach (var customTrigger in _customTriggers.Values)
                    customTrigger.Destroy();
            }

            public void ShowAllToPlayer(BasePlayer player)
            {
                foreach (var triggerInfo in _customTriggers.Keys)
                    ShowTrigger(player, triggerInfo);

                Timer existingTimer;
                if (_drawTimers.TryGetValue(player.userID, out existingTimer))
                    existingTimer.Destroy();

                _drawTimers[player.userID] = _pluginInstance.timer.Repeat(TriggerDisplayDuration - 0.1f, 60, () =>
                {
                    foreach (var triggerInfo in _customTriggers.Keys)
                        ShowTrigger(player, triggerInfo);
                });
            }

            private static void ShowTrigger(BasePlayer player, CustomTriggerInfo triggerInfo)
            {
                var color = triggerInfo.GetColor();

                var spherePosition = triggerInfo.Position + TriggerOffsetFromWorkcart;
                player.SendConsoleCommand("ddraw.sphere", TriggerDisplayDuration, color, spherePosition, TriggerDisplayRadius);

                var infoLines = new List<string>() { _pluginInstance.GetMessage(player, Lang.InfoTrigger, triggerInfo.Id) };

                if (triggerInfo.StartsAutomation)
                    infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoStart));

                EngineSpeeds engineSpeed;
                if (triggerInfo.TryGetEngineSpeed(out engineSpeed))
                    infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerSpeed, engineSpeed));

                TrackSelection trackSelection;
                if (triggerInfo.TryGetTrackSelection(out trackSelection))
                    infoLines.Add(_pluginInstance.GetMessage(player, Lang.InfoTriggerTrackSelection, trackSelection));

                var textPosition = triggerInfo.Position + Vector3.up * 2.5f;
                player.SendConsoleCommand("ddraw.text", TriggerDisplayDuration, color, textPosition, string.Join("\n", infoLines));
            }
        }

        #endregion

        #region Train Stations

        private class TrainStationManager
        {
            private List<TrainStation> _trainStations = new List<TrainStation>();

            public void CreateAll()
            {
                foreach (var dungeon in TerrainMeta.Path.DungeonCells)
                {
                    if (dungeon == null)
                        continue;

                    TrainStation track1, track2;
                    if (TrainStation.TryCreateStationTracks(dungeon, out track1, out track2))
                    {
                        _trainStations.Add(track1);
                        _trainStations.Add(track2);
                    }
                }
            }

            public void DestroyAll()
            {
                foreach (var station in _trainStations)
                    station.Destroy();
            }
        }

        private class TriggerStation : TriggerBase
        {
            public TrainStation StationTrack;
        }

        private class TriggerStationStop : TriggerStation {}

        private class TrainStation
        {
            private static readonly Vector3 TriggerDimensions = new Vector3(3, 3, 1);
            private static readonly Vector3 FullLengthTriggerDimensions = new Vector3(3, 3, 144);

            private static readonly Vector3 LeftTriggerPosition = new Vector3(4.5f, 2.5f, 0);
            private static readonly Vector3 LeftEntrancePosition = new Vector3(4.5f, 2.5f, 72);
            private static readonly Vector3 LeftStopPosition = new Vector3(4.5f, 2.5f, 24); // 18 is exact elevator spot
            private static readonly Vector3 LeftExitPosition = new Vector3(4.5f, 2.5f, -72);

            private static readonly Vector3 RightTriggerPosition = new Vector3(-4.5f, 2.5f, 0);
            private static readonly Vector3 RightEntrancePosition = new Vector3(-4.5f, 2.5f, -72);
            private static readonly Vector3 RightStopPosition = new Vector3(-4.5f, 2.5f, 18); // 24 is exact elevator spot
            private static readonly Vector3 RightExitPosition = new Vector3(-4.5f, 2.5f, 72);

            private static readonly Dictionary<string, Quaternion> StationRotations = new Dictionary<string, Quaternion>()
            {
                ["station-sn-0"] = Quaternion.Euler(0, 180, 0),
                ["station-sn-1"] = Quaternion.identity,
                ["station-sn-2"] = Quaternion.Euler(0, 180, 0),
                ["station-sn-3"] = Quaternion.identity,
                ["station-we-0"] = Quaternion.Euler(0, 90, 0),
                ["station-we-1"] = Quaternion.Euler(0, -90, 0),
                ["station-we-2"] = Quaternion.Euler(0, 90, 0),
                ["station-we-3"] = Quaternion.Euler(0, -90, 0),
            };

            public static bool TryCreateStationTracks(DungeonCell dungeon, out TrainStation track1, out TrainStation track2)
            {
                var dungeonShortName = GetShortName(dungeon.name);

                Quaternion rotation;
                if (!StationRotations.TryGetValue(dungeonShortName, out rotation))
                {
                    track1 = null;
                    track2 = null;
                    return false;
                }

                track1 = new TrainStation(dungeon, rotation, isLeftTrack: true);
                track2 = new TrainStation(dungeon, rotation, isLeftTrack: false);

                return true;
            }

            private DungeonCell _dungeon;
            private Quaternion _rotation;

            private TriggerStation _fullLengthTrigger;
            private List<GameObject> _gameObjects = new List<GameObject>();

            public TrainStation(DungeonCell dungeon, Quaternion rotation, bool isLeftTrack)
            {
                _dungeon = dungeon;
                _rotation = rotation;
                _fullLengthTrigger = CreateTrigger<TriggerStation>(isLeftTrack ? LeftTriggerPosition : RightTriggerPosition, FullLengthTriggerDimensions);
                CreateTrigger<TriggerStationStop>(isLeftTrack ? LeftStopPosition : RightStopPosition, TriggerDimensions);
            }

            public void OnTrainArrive(TrainController trainController)
            {
                trainController.ArriveAtStation();
                DepartExistingTrains(_fullLengthTrigger, trainController);
            }

            public void OnTrainReachStop(TrainController trainController)
            {
                trainController.StopAtStation(_pluginConfig.EngineOffDuration);
            }

            public void OnTrainDepart(TrainController trainController)
            {
                trainController.LeaveStation();
            }

            public bool DepartExistingTrains(TriggerStation trigger, TrainController exceptTrain)
            {
                if (trigger.entityContents == null)
                    return false;

                var wasExistingTrain = false;

                foreach (var item in trigger.entityContents)
                {
                    if (item == null)
                        continue;

                    var otherController = item.GetComponent<TrainController>();
                    if (otherController == null || otherController == exceptTrain)
                        continue;

                    if (otherController.StartLeavingStation())
                        wasExistingTrain = true;
                }

                return wasExistingTrain;
            }

            public void Destroy()
            {
                foreach (var obj in _gameObjects)
                    UnityEngine.Object.Destroy(obj);
            }

            private TriggerStation CreateTrigger<T>(Vector3 localPosition, Vector3 dimensions) where T : TriggerStation
            {
                var gameObject = new GameObject();
                gameObject.transform.position = _dungeon.transform.TransformPoint(_rotation * localPosition);
                gameObject.transform.rotation = _rotation;

                var boxCollider = gameObject.AddComponent<BoxCollider>();
                boxCollider.isTrigger = true;
                boxCollider.size = dimensions;
                boxCollider.gameObject.layer = 6;

                var trigger = gameObject.AddComponent<T>();
                trigger.interestLayers = Layers.Mask.Vehicle_World;
                trigger.StationTrack = this;

                _gameObjects.Add(gameObject);

                return trigger;
            }
        }

        #endregion

        #region Train Controller

        private class TrainController : FacepunchBehaviour
        {
            private enum TrainState
            {
                BetweenStations,
                EnteringStation,
                StoppedAtStation,
                LeavingStation
            }

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
            private VendingMachineMapMarker _mapMarker;

            private void Awake()
            {
                _workcart = GetComponent<TrainEngine>();
                if (_workcart == null)
                    return;

                // AddMapMarker();
                Invoke(AddConductor, 0);
                Invoke(ScheduledStartTrain, UnityEngine.Random.Range(1f, 3f));
                EnableUnlimitedFuel();
            }

            private TrainState _trainState = TrainState.BetweenStations;
            private EngineSpeeds _nextSpeed;

            public void StartImmediately(CustomTriggerInfo triggerInfo)
            {
                EngineSpeeds initialSpeed;
                if (!triggerInfo.TryGetEngineSpeed(out initialSpeed))
                    initialSpeed = _pluginConfig.GetDefaultSpeed();

                CancelInvoke(ScheduledStartTrain);
                Invoke(() =>
                {
                    StartTrain(initialSpeed);
                    HandleCustomTrigger(triggerInfo);
                }, 1);
            }

            public void HandleCustomTrigger(CustomTriggerInfo triggerInfo)
            {
                EngineSpeeds engineSpeed;
                if (triggerInfo.TryGetEngineSpeed(out engineSpeed))
                {
                    SetThrottle(engineSpeed);

                    if (engineSpeed == EngineSpeeds.Zero)
                        Invoke(ScheduledDepartureForCustomTrigger, _pluginConfig.EngineOffDuration);
                    else
                        CancelInvoke(ScheduledDepartureForCustomTrigger);
                }

                TrackSelection trackSelection;
                if (triggerInfo.TryGetTrackSelection(out trackSelection))
                {
                    _workcart.SetTrackSelection(trackSelection);
                }
            }

            public void ScheduledDepartureForCustomTrigger()
            {
                SetThrottle(_pluginConfig.GetDepartureSpeed());
            }

            public void ArriveAtStation()
            {
                if (_trainState == TrainState.EnteringStation)
                    return;

                _trainState = TrainState.EnteringStation;

                if (_workcart.TrackSpeed > 15)
                    BrakeToSpeed(EngineSpeeds.Fwd_Med, 1.7f);
                else
                    SetThrottle(EngineSpeeds.Fwd_Med);
            }

            public void StopAtStation(float stayDuration)
            {
                if (_trainState == TrainState.StoppedAtStation
                    || _trainState == TrainState.LeavingStation)
                    return;

                _trainState = TrainState.StoppedAtStation;

                BrakeToSpeed(EngineSpeeds.Zero, 1.5f);
                Invoke(ScheduledDeparture, stayDuration);
            }

            public void ScheduledDeparture()
            {
                StartLeavingStation();
            }

            public bool StartLeavingStation()
            {
                if (_trainState == TrainState.LeavingStation)
                    return false;

                _trainState = TrainState.LeavingStation;
                SetThrottle(_pluginConfig.GetDepartureSpeed());

                CancelInvoke(ScheduledDeparture);
                return true;
            }

            public void LeaveStation()
            {
                _trainState = TrainState.BetweenStations;
                SetThrottle(_pluginConfig.GetDefaultSpeed());
            }

            // TODO: Automatically figure out break duration for target speed.
            public void BrakeToSpeed(EngineSpeeds nextSpeed, float duration)
            {
                _workcart.SetThrottle(EngineSpeeds.Rev_Lo);
                _nextSpeed = nextSpeed;
                Invoke(ScheduledSpeedChange, duration);
            }

            public void ScheduledSpeedChange()
            {
                _workcart.SetThrottle(_nextSpeed);
            }

            public void SetThrottle(EngineSpeeds throttleSpeed)
            {
                CancelInvoke(ScheduledSpeedChange);
                _workcart.SetThrottle(throttleSpeed);
            }

            private bool IsParked()
            {
                return _workcart.Distance(_workcart.spawnOrigin) < 5;
            }

            private void AddConductor()
            {
                _workcart.DismountAllPlayers();

                Conductor = GameManager.server.CreateEntity(PlayerPrefab, _workcart.transform.position) as BasePlayer;
                if (Conductor == null)
                    return;

                Conductor.enableSaving = false;
                Conductor.Spawn();

                AddOutfit();

                _workcart.platformParentTrigger.OnTriggerEnter(Conductor.playerCollider);

                Conductor.displayName = "Conductor";
                _workcart.AttemptMount(Conductor, false);
            }

            private void StartTrain(EngineSpeeds initialSpeed)
            {
                _workcart.engineController.FinishStartingEngine();
                SetThrottle(initialSpeed);
                _workcart.SetTrackSelection(_pluginConfig.GetDefaultTrackSelection());
            }

            private void ScheduledStartTrain()
            {
                var initialSpeed = _workcart.FrontTrackSection.isStation
                    ? _pluginConfig.GetDepartureSpeed()
                    : _pluginConfig.GetDefaultSpeed();

                StartTrain(initialSpeed);
            }

            private void AddOutfit()
            {
                Conductor.inventory.Strip();

                var container = Conductor.inventory.containerWear;

                // TODO: Kits
                var items = new string[] { "jumpsuit.suit", "sunglasses03chrome", "hat.boonie" };

                foreach (var itemShortName in items)
                {
                    var item = ItemManager.CreateByName(itemShortName);
                    if (item == null)
                        // TODO: Error logging
                        continue;

                    if (!item.MoveToContainer(container))
                        item.Remove();
                }

                Conductor.SendNetworkUpdate();
            }

            private void AddMapMarker()
            {
                _mapMarker = GameManager.server.CreateEntity(VendingMachineMapMarkerPrefab) as VendingMachineMapMarker;
                if (_mapMarker == null)
                    return;

                _workcart.EnableGlobalBroadcast(true);

                _mapMarker.enableSaving = false;
                _mapMarker.markerShopName = "Workcart";

                _mapMarker.SetParent(_workcart);
                _mapMarker.Spawn();
            }

            private void OnDestroy()
            {
                if (_mapMarker != null)
                    _mapMarker.Kill();

                if (Conductor != null)
                    Conductor.Kill();

                DisableUnlimitedFuel();
            }

            private void EnableUnlimitedFuel()
            {
                _workcart.fuelSystem.cachedHasFuel = true;
                _workcart.fuelSystem.nextFuelCheckTime = float.MaxValue;
            }

            private void DisableUnlimitedFuel()
            {
                _workcart.fuelSystem.cachedHasFuel = true;
                _workcart.fuelSystem.nextFuelCheckTime = 0;
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

            public void AddWorkcart(TrainEngine workcart)
            {
                AutomatedWorkcardIds.Add(workcart.net.ID);
                Save();
            }

            public void RemoveWorkcart(TrainEngine workcart)
            {
                AutomatedWorkcardIds.Remove(workcart.net.ID);
                Save();
            }
        }

        private class StoredMapData
        {
            [JsonProperty("CustomTriggers")]
            public List<CustomTriggerInfo> CustomTriggers = new List<CustomTriggerInfo>();

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

            public void AddTrigger(CustomTriggerInfo customTrigger)
            {
                CustomTriggers.Add(customTrigger);
                Save();
            }

            public void RemoveTrigger(CustomTriggerInfo triggerInfo)
            {
                CustomTriggers.Remove(triggerInfo);
                Save();
            }
        }

        #endregion

        #region Configuration

        private class Configuration : SerializableConfiguration
        {
            [JsonProperty("AutomateAllWorkcarts")]
            public bool AutomateAllWorkcarts = false;

            [JsonProperty("AutoDetectStations")]
            public bool AutoDetectStations = true;

            [JsonProperty("EngineOffDuration")]
            public float EngineOffDuration = 30;

            [JsonProperty("DefaultSpeed")]
            public string DefaultSpeed = EngineSpeeds.Fwd_Hi.ToString();

            [JsonProperty("DepartureSpeed")]
            public string DepartureSpeed = EngineSpeeds.Fwd_Lo.ToString();

            [JsonProperty("DefaultTrackSelection")]
            public string DefaultTrackSelection = TrackSelection.Left.ToString();

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

            private EngineSpeeds? _departureSpeed;
            public EngineSpeeds GetDepartureSpeed()
            {
                if (_departureSpeed != null)
                    return (EngineSpeeds)_departureSpeed;

                EngineSpeeds engineSpeed;
                if (TryParseEngineSpeed(DepartureSpeed, out engineSpeed))
                {
                    _departureSpeed = engineSpeed;
                    return engineSpeed;
                }

                return EngineSpeeds.Fwd_Lo;
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

        private class Lang
        {
            public const string ErrorNoPermission = "Error.NoPermission";
            public const string ErrorNoTriggers = "Error.NoTriggers";
            public const string ErrorTriggerNotFound = "Error.TriggerNotFound";
            public const string ErrorNoTrackFound = "Error.ErrorNoTrackFound";
            public const string ErrorNoWorkcartFound = "Error.NoWorkcartFound";
            public const string ErrorFullyAutomated = "Error.FullyAutomated";
            public const string ErrorAutomateBlocked = "Error.AutomateBlocked";

            public const string ToggleOnSuccess = "Toggle.Success.On";
            public const string ToggleOffSuccess = "Toggle.Success.Off";

            public const string AddTriggerSyntax = "AddTrigger.Syntax";
            public const string AddTriggerSuccess = "AddTrigger.Success";
            public const string MoveTriggerSuccess = "MoveTrigger.Success";
            public const string UpdateTriggerSyntax = "UpdateTrigger.Syntax";
            public const string UpdateTriggerSuccess = "UpdateTrigger.Success";
            public const string RemoveTriggerSyntax = "RemoveTrigger.Syntax";
            public const string RemoveTriggerSuccess = "RemoveTrigger.Success";

            public const string InfoTrigger = "Info.Trigger";
            public const string InfoStart = "Info.Trigger.Start";
            public const string InfoTriggerSpeed = "Info.Trigger.Speed";
            public const string InfoTriggerTrackSelection = "Info.Trigger.TrackSelection";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.ErrorNoPermission] = "You don't have permission to do that.",
                [Lang.ErrorNoTriggers] = "There are no workcart triggers on this map.",
                [Lang.ErrorTriggerNotFound] = "Error: Trigger id #{0} not found.",
                [Lang.ErrorNoTrackFound] = "Error: No track found nearby.",
                [Lang.ErrorNoWorkcartFound] = "Error: No workcart found.",
                [Lang.ErrorFullyAutomated] = "Error: You cannot do that while full automation is on.",
                [Lang.ErrorAutomateBlocked] = "Error: Another plugin blocked automating that workcart.",

                [Lang.ToggleOnSuccess] = "That workcart is now automated.",
                [Lang.ToggleOffSuccess] = "That workcart is no longer automated.",
                [Lang.AddTriggerSyntax] = "Syntax: <color=#fd4>{0} <speed> <track selection></color>\nSpeeds: {1}\nTrack selections: {2}",
                [Lang.AddTriggerSuccess] = "Successfully added trigger #{0}.",
                [Lang.UpdateTriggerSyntax] = "Syntax: <color=#fd4>{0} <id> <speed> <track selection></color>\nSpeeds: {1}\nTrack selections: {2}",
                [Lang.UpdateTriggerSuccess] = "Successfully updated trigger #{0}",
                [Lang.MoveTriggerSuccess] = "Successfully moved trigger #{0}",
                [Lang.RemoveTriggerSyntax] = "Syntax: <color=#fd4>{0} <id></color>",
                [Lang.RemoveTriggerSuccess] = "Trigger #{0} successfully removed.",

                [Lang.InfoTrigger] = "Workcart Trigger #{0}",
                [Lang.InfoStart] = "Starts automation",
                [Lang.InfoTriggerSpeed] = "Speed: {0}",
                [Lang.InfoTriggerTrackSelection] = "Track selection: {0}",
            }, this, "en");
        }

        #endregion
    }
}
