using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using Rust;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Automated Workcarts", "WhiteThunder", "0.1.0")]
    [Description("Spawns conductor NPCs that drive workcarts between stations.")]
    internal class AutomatedWorkcarts : CovalencePlugin
    {
        #region Fields

        private static AutomatedWorkcarts _pluginInstance;
        private static Configuration _pluginConfig;

        private const string PlayerPrefab = "assets/prefabs/player/player.prefab";
        private const string VendingMachineMapMarkerPrefab = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";
        private const string DroneMapMarkerPrefab = "assets/prefabs/misc/marketplace/deliverydronemarker.prefab";

        private List<TrainStation> _trainStations = new List<TrainStation>();

        #endregion

        #region Hooks

        private void Init()
        {
            _pluginInstance = this;
            Unsubscribe(nameof(OnEntitySpawned));
        }

        private void Unload()
        {
            DestroyStations();
            TrainController.DestroyAll();
            _pluginInstance = null;
            _pluginConfig = null;
        }

        private void OnServerInitialized()
        {
            CreateStations();

            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var workcart = entity as TrainEngine;
                if (workcart != null)
                    workcart.gameObject.AddComponent<TrainController>();
            }

            Subscribe(nameof(OnEntitySpawned));
        }

        private void OnEntitySpawned(TrainEngine workcart)
        {
            TryAddTrainController(workcart);
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

        #region Helper Methods

        private static bool AutomationWasBlocked(TrainEngine workcart)
        {
            object hookResult = Interface.CallHook("OnWorkcartAutomate", workcart);
            return hookResult is bool && (bool)hookResult == false;
        }

        private static bool TryAddTrainController(TrainEngine workcart)
        {
            if (AutomationWasBlocked(workcart))
                return false;

            workcart.gameObject.AddComponent<TrainController>();
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

        private void CreateStations()
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

        private void DestroyStations()
        {
            foreach (var station in _trainStations)
                station.Destroy();
        }

        #endregion

        #region Classes

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
                trainController.StopAtStation(_pluginConfig.TimeAtStation);
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
                Invoke(StartTrain, UnityEngine.Random.Range(1f, 3f));
                EnableUnlimitedFuel();
            }

            private TrainState _trainState = TrainState.BetweenStations;
            private TrainEngine.EngineSpeeds _nextSpeed;

            public void ArriveAtStation()
            {
                if (_trainState == TrainState.EnteringStation)
                    return;

                _trainState = TrainState.EnteringStation;

                if (_workcart.TrackSpeed > 15)
                    BrakeToSpeed(TrainEngine.EngineSpeeds.Fwd_Med, 1.7f);
                else
                    SetThrottle(TrainEngine.EngineSpeeds.Fwd_Med);
            }

            public void StopAtStation(float stayDuration)
            {
                if (_trainState == TrainState.StoppedAtStation
                    || _trainState == TrainState.LeavingStation)
                    return;

                _trainState = TrainState.StoppedAtStation;

                BrakeToSpeed(TrainEngine.EngineSpeeds.Zero, 1.5f);
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
                SetThrottle(TrainEngine.EngineSpeeds.Fwd_Med);

                CancelInvoke(ScheduledDeparture);
                return true;
            }

            public void LeaveStation()
            {
                _trainState = TrainState.BetweenStations;
                SetThrottle(TrainEngine.EngineSpeeds.Fwd_Hi);
            }

            // TODO: Automatically figure out break duration for target speed.
            public void BrakeToSpeed(TrainEngine.EngineSpeeds nextSpeed, float duration)
            {
                _workcart.SetThrottle(TrainEngine.EngineSpeeds.Rev_Lo);
                _nextSpeed = nextSpeed;
                Invoke(ScheduledSpeedChange, duration);
            }

            public void ScheduledSpeedChange()
            {
                _workcart.SetThrottle(_nextSpeed);
            }

            public void SetThrottle(TrainEngine.EngineSpeeds throttleSpeed)
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

            private void StartTrain()
            {
                var initialSpeed = _workcart.FrontTrackSection.isStation
                    ? TrainEngine.EngineSpeeds.Fwd_Med
                    : TrainEngine.EngineSpeeds.Fwd_Hi;

                _workcart.engineController.FinishStartingEngine();
                SetThrottle(initialSpeed);
                _workcart.SetTrackSelection(TrainTrackSpline.TrackSelection.Left);
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

        #region Configuration

        private class Configuration : SerializableConfiguration
        {
            [JsonProperty("TimeAtStation")]
            public float TimeAtStation = 30;
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
    }
}
