using CommandTerminal;
using DV.Logic.Job;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace RevisedDVRoute
{
    public class LocoAI : LocoCruiseControl
    {
        private const float TARGET_SPEED_DEFAULT = 20.0f;
        private const float COUPLER_APPROACH_SPEED = 5.0f;
        private const float SPEED_LIMIT_LOOKAHEAD_METERS = 3000.0f;
        private const float SPEED_LIMIT_BRAKING_DECELERATION = 0.35f;
        private const float BRAKE_PIPE_RESPONSE_SECONDS = 4.0f;
        private const float SPEED_LIMIT_MARGIN_METERS = 15.0f;
        private RouteTracker RouteTracker;

        public bool IsRunning => running;
        private bool _aiCoroutineActive;
        private int _aiGeneration;
        public string SignalHoldReason { get; private set; }
        public enum HaulState
        {
            Idle,
            Starting,
            RoutingToCars,
            ApproachingCars,
            Coupling,
            RoutingToDestination,
            DrivingToDestination,
            Uncoupling,
            Completed,
            Failed
        }

        private bool _freightHaulActive = false;
        private int _freightHaulGeneration;
        private HaulState _freightHaulState = HaulState.Idle;
        private string _freightHaulError = null;

        public bool IsFreightHaulActive => _freightHaulActive;
        public HaulState FreightHaulState => _freightHaulState;
        public string FreightHaulError => _freightHaulError;

        // Per-instance cache: computed on first use via BezierArcApproximation (same
        // algorithm the game uses to place speed-limit signs).
        private readonly Dictionary<RailTrack, float> _speedLimitCache = new Dictionary<RailTrack, float>();

        private bool _hasSoundedDepartureHorn = false;
        private bool? _lastHeadlightForward = null;
        private LocoSim.Implementations.Port _cylinderCockPort;
        private bool _cylinderCockPortSearched = false;

        private IEnumerator SoundDepartureHorn(bool isReverse)
        {
            int blasts = isReverse ? 3 : 2;
            for (int i = 0; i < blasts; i++)
            {
                remoteControl.UpdateHorn(1.0f);
                yield return new WaitForSeconds(0.35f);
                remoteControl.UpdateHorn(0.0f);
                if (i < blasts - 1)
                    yield return new WaitForSeconds(0.25f);
            }
        }

        private void UpdateLocomotiveDevices(float speed)
        {
            // 1. Headlights automation
            float reverser = remoteControl.GetReverserValue();
            bool isForward = reverser >= 0.5f;

            if (!_lastHeadlightForward.HasValue || _lastHeadlightForward.Value != isForward)
            {
                _lastHeadlightForward = isForward;
                if (controlsOverrider != null)
                {
                    if (isForward)
                    {
                        controlsOverrider.HeadlightsFront?.Set(2f);
                        controlsOverrider.HeadlightsRear?.Set(0f);
                    }
                    else
                    {
                        controlsOverrider.HeadlightsFront?.Set(0f);
                        controlsOverrider.HeadlightsRear?.Set(2f);
                    }
                }
            }

            // 2. Steam locomotive cylinder cocks automation
            if (simController?.simFlow != null)
            {
                if (_cylinderCockPort == null && !_cylinderCockPortSearched)
                {
                    _cylinderCockPortSearched = true;
                    if (simController.simFlow.AllPorts != null)
                    {
                        foreach (var p in simController.simFlow.AllPorts)
                        {
                            if (p.id != null && (p.id.IndexOf("cylinderCock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 p.id.IndexOf("cylCock", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                _cylinderCockPort = p;
                                break;
                            }
                        }
                    }
                }

                if (_cylinderCockPort != null)
                {
                    // Open cylinder cocks when starting/stopping (< 12 km/h) to drain condensate; close when cruising (> 12 km/h)
                    float targetCockValue = (speed < 12.0f) ? 1.0f : 0.0f;
                    _cylinderCockPort.ExternalValueUpdate(targetCockValue);
                }
            }

            // 3. Departure horn signal
            if (speed < 0.25f && TargetSpeed < 0.5f)
            {
                _hasSoundedDepartureHorn = false;
            }
            else if (!_hasSoundedDepartureHorn && speed > 0.35f && TargetSpeed > 2.0f)
            {
                _hasSoundedDepartureHorn = true;
                Module.StartCoroutine(SoundDepartureHorn(!isForward));
            }
        }

        /// <summary>
        /// Returns true when the next 1–2 tracks ahead in the route path include
        /// a turntable that hasn't finished rotating to its target angle yet.
        /// The AI will hold TargetSpeed = 0 until this returns false.
        /// </summary>
        private bool IsApproachingRotatingTurntable()
        {
            if (PathFinder._turntableTrackToTRT == null || PathFinder._turntableTrackToTRT.Count == 0)
                return false;

            RailTrack currentTrack = trainCar?.Bogies[0]?.track;
            if (currentTrack == null || RouteTracker?.Route?.Path == null)
                return false;

            var path = RouteTracker.Route.Path;
            int idx = RouteTracker.CurrentPathIndex;
            if (idx < 0 || idx >= path.Count)
                idx = path.IndexOf(currentTrack);
            if (idx < 0) return false;

            // Look 1–2 steps ahead in the path
            for (int i = idx + 1; i < Mathf.Min(idx + 3, path.Count); i++)
            {
                TurntableRailTrack trt;
                if (!PathFinder._turntableTrackToTRT.TryGetValue(path[i], out trt) || trt == null)
                    continue;
                if (Mathf.Abs(Mathf.DeltaAngle(trt.currentYRotation, trt.targetYRotation)) > 1f)
                    return true;
            }

            return false;
        }

        private float GetTrackSpeedLimit(RailTrack track)
        {
            if (track == null) return TARGET_SPEED_DEFAULT;
            float limitKph;
            if (!_speedLimitCache.TryGetValue(track, out limitKph))
            {
                limitKph = ComputeTrackSpeedLimit(track);
                _speedLimitCache[track] = limitKph;
            }

            // Revised_Mph changes only the displayed units. Keep the control
            // target in physical km/h, but align it with its rounded MPH sign.
            return Compatibility.MphCompatibility.GetAiTargetSpeedKph(limitKph);
        }

        /// <summary>
        /// Derail Valley maximum operational speeds per locomotive type:
        /// DE2: 50 km/h (traction motor burnout if oversped)
        /// S060: 50 km/h
        /// DM3: 60 km/h
        /// S282: 80 km/h
        /// Railbus/DM1U: 80 km/h
        /// DH4: 90 km/h
        /// DE6 / Slug: 120 km/h
        /// Microshunter (BE2) / Handcar: 30 km/h
        /// </summary>
        public static float GetLocomotiveMaxSpeed(TrainCar car)
        {
            if (car == null) return 120f;

            string liveryId = car.carLivery?.id?.ToUpperInvariant() ?? "";
            string carName = car.name?.ToUpperInvariant() ?? "";
            string carId = car.ID?.ToUpperInvariant() ?? "";

            // Custom locomotives and specific switcher models
            if (liveryId.Contains("C501") || carName.Contains("C501") || carId.Contains("C501") ||
                liveryId.Contains("BALDWIN") || carName.Contains("BALDWIN"))
            {
                return 45f; // Baldwin 0-6-0 switcher (C501): 45 km/h (~28 mph)
            }

            if (liveryId.Contains("AMERICAN_C") || carName.Contains("AMERICAN_C"))
            {
                return 50f; // American C Class: 50 km/h (~31 mph)
            }

            if (liveryId.Contains("MICROSHUNTER") || liveryId.Contains("BE2") || carName.Contains("BE2") || carName.Contains("MICROSHUNTER"))
            {
                return 30f;
            }

            if (liveryId.Contains("S060") || carName.Contains("S060"))
            {
                return 50f;
            }

            if (liveryId.Contains("DE2") || carName.Contains("DE2") || carName.Contains("SHUNTER"))
            {
                return 50f;
            }

            if (liveryId.Contains("DM3") || carName.Contains("DM3"))
            {
                return 60f;
            }

            if (liveryId.Contains("DH4") || carName.Contains("DH4"))
            {
                return 90f;
            }

            if (liveryId.Contains("S282") || carName.Contains("S282") || liveryId.Contains("STEAMHEAVY"))
            {
                return 80f;
            }

            if (liveryId.Contains("DE6") || carName.Contains("DE6"))
            {
                return 120f;
            }

            switch (car.carType)
            {
                case DV.ThingTypes.TrainCarType.LocoMicroshunter:
                case DV.ThingTypes.TrainCarType.HandCar:
                    return 30f;
                case DV.ThingTypes.TrainCarType.LocoShunter:
                case DV.ThingTypes.TrainCarType.LocoS060:
                    return 50f;
                case DV.ThingTypes.TrainCarType.LocoDM3:
                    return 60f;
                case DV.ThingTypes.TrainCarType.LocoSteamHeavy:
                case DV.ThingTypes.TrainCarType.LocoRailbus:
                case DV.ThingTypes.TrainCarType.LocoDM1U:
                    return 80f;
                case DV.ThingTypes.TrainCarType.LocoDH4:
                    return 90f;
                case DV.ThingTypes.TrainCarType.LocoDiesel:
                case DV.ThingTypes.TrainCarType.LocoDE6Slug:
                    return 120f;
                default:
                    if (car.carLivery != null && DV.ThingTypes.CarTypes.IsSteamLocomotive(car.carLivery))
                        return 60f;
                    return 120f;
            }
        }

        /// <summary>
        /// Returns the maximum allowed speed for all locomotives in the active consist.
        /// When multiple locomotives are coupled together (e.g. DE2 + DE6), the consist
        /// must not exceed the lowest locomotive's maximum speed to protect motors and running gear.
        /// </summary>
        public float GetConsistMaxLocoSpeed()
        {
            Trainset trainset = RouteTracker?.Trainset ?? trainCar?.trainset;
            if (trainset?.cars == null || trainset.cars.Count == 0)
                return GetLocomotiveMaxSpeed(trainCar);

            float maxSpeed = 120f;
            for (int i = 0; i < trainset.cars.Count; i++)
            {
                TrainCar c = trainset.cars[i];
                if (c != null && c.IsLoco)
                {
                    float locoSpeed = GetLocomotiveMaxSpeed(c);
                    if (locoSpeed < maxSpeed)
                        maxSpeed = locoSpeed;
                }
            }
            return maxSpeed;
        }

        /// <summary>
        /// Computes the lowest speed limit among all tracks currently occupied by any car
        /// in the consist. This prevents long trains from accelerating when the lead locomotive
        /// clears a slow zone/turnout while the tail cars are still on it.
        /// </summary>
        public float GetConsistOccupiedSpeedLimit()
        {
            Trainset trainset = RouteTracker?.Trainset ?? trainCar?.trainset;
            if (trainset?.cars == null || trainset.cars.Count == 0)
            {
                return GetTrackSpeedLimit(trainCar?.Bogies?[0]?.track);
            }

            float minLimit = 120f;
            for (int i = 0; i < trainset.cars.Count; i++)
            {
                TrainCar car = trainset.cars[i];
                if (car == null || car.Bogies == null) continue;
                for (int b = 0; b < car.Bogies.Length; b++)
                {
                    RailTrack track = car.Bogies[b]?.track;
                    if (track != null)
                    {
                        float limit = GetTrackSpeedLimit(track);
                        if (limit < minLimit)
                            minLimit = limit;
                    }
                }
            }
            return minLimit;
        }

        /// <summary>
        /// Returns a braking-envelope speed for the current position.
        /// Constrained by:
        /// 1. Consist-wide occupied track limits (tail cars on slow curves/turnouts).
        /// 2. Consist locomotive maximum speeds (e.g. 50 km/h DE2 traction motor safety).
        /// 3. Upcoming speed limit drops along the route path (with braking curves).
        /// 4. Signal stops / block reservation waits (DV Signals & ADS).
        /// 5. Obstacles / cars occupying intermediate tracks ahead.
        /// </summary>
        private float GetRouteSpeedTarget(float currentSpeedKph)
        {
            float maxLocoSpeed = GetConsistMaxLocoSpeed();
            float occupiedLimit = GetConsistOccupiedSpeedLimit();
            float target = Mathf.Min(occupiedLimit, maxLocoSpeed);

            Route route = RouteTracker?.Route;
            IList<RailTrack> path = route?.Path;
            if (path == null || path.Count == 0)
            {
                SignalHoldReason = null;
                return target;
            }

            int pathIndex = RouteTracker.CurrentPathIndex;
            if (pathIndex < 0 || pathIndex >= path.Count)
            {
                RailTrack currentTrack = trainCar?.Bogies[0]?.track;
                pathIndex = path.IndexOf(currentTrack);
                if (pathIndex < 0)
                {
                    SignalHoldReason = null;
                    return target;
                }
            }

            target = Mathf.Min(target, GetTrackSpeedLimit(path[pathIndex]));
            double distanceToLimit = Math.Max(0.0, RouteTracker.DistanceToNextTrack);
            float currentSpeedMps = currentSpeedKph / 3.6f;
            Trainset trainset = RouteTracker?.Trainset ?? trainCar?.trainset;
            string activeHoldReason = null;

            // ── Check speed limits & intermediate obstacles ahead on path ─────
            for (int index = pathIndex + 1;
                index < path.Count && distanceToLimit <= SPEED_LIMIT_LOOKAHEAD_METERS;
                index++)
            {
                RailTrack nextRailTrack = path[index];
                if (nextRailTrack == null) break;

                // Upcoming track speed limit
                float nextLimit = Mathf.Min(GetTrackSpeedLimit(nextRailTrack), maxLocoSpeed);
                if (nextLimit < target)
                {
                    float responseDistance = currentSpeedMps * BRAKE_PIPE_RESPONSE_SECONDS;
                    float brakingDistance = Mathf.Max(0f,
                        (float)distanceToLimit - responseDistance - SPEED_LIMIT_MARGIN_METERS);
                    float nextLimitMps = nextLimit / 3.6f;
                    float allowedMps = Mathf.Sqrt(nextLimitMps * nextLimitMps
                        + 2f * SPEED_LIMIT_BRAKING_DECELERATION * brakingDistance);
                    target = Mathf.Min(target, allowedMps * 3.6f);
                }

                Track logicTrack = nextRailTrack.LogicTrack();
                if (logicTrack == null)
                    break;

                // Intermediate track obstruction (foreign cars or train ahead before destination)
                if (index < path.Count - 1 && !logicTrack.IsFree(trainset))
                {
                    float responseDistance = currentSpeedMps * BRAKE_PIPE_RESPONSE_SECONDS;
                    float stopBrakingDistance = Mathf.Max(0f, (float)distanceToLimit - responseDistance - 20.0f);
                    float allowedStopMps = Mathf.Sqrt(2f * SPEED_LIMIT_BRAKING_DECELERATION * stopBrakingDistance);
                    float stopTarget = allowedStopMps * 3.6f;
                    if (distanceToLimit < 15.0 || stopTarget < 3.0f)
                        stopTarget = 0f;

                    if (stopTarget < target)
                    {
                        target = stopTarget;
                        activeHoldReason = "Track occupied: " + (logicTrack.ID?.FullID ?? "Ahead");
                    }
                }

                distanceToLimit += Math.Max(0.0, logicTrack.length);
            }

            // ── Check Signals & Route Block Reservations (DV Signals & ADS) ───
            if (Compatibility.DVSignalsRouteReservation.GetSignalRestrictionAhead(
                route,
                pathIndex,
                RouteTracker.DistanceToNextTrack,
                out double distanceToSignal,
                out float signalSpeedKph,
                out string signalReason))
            {
                float responseDistance = currentSpeedMps * BRAKE_PIPE_RESPONSE_SECONDS;
                float targetSignalMps = signalSpeedKph / 3.6f;
                // Aim to stop 20m before the signal / block boundary
                float signalBrakingDistance = Mathf.Max(0f, (float)distanceToSignal - responseDistance - 20.0f);
                float allowedSignalMps = Mathf.Sqrt(targetSignalMps * targetSignalMps
                    + 2f * SPEED_LIMIT_BRAKING_DECELERATION * signalBrakingDistance);
                float signalTargetKph = allowedSignalMps * 3.6f;

                if (signalSpeedKph <= 0f && (distanceToSignal < 20.0 || signalTargetKph < 3.0f))
                {
                    signalTargetKph = 0f;
                }

                if (signalTargetKph < target)
                {
                    target = signalTargetKph;
                    activeHoldReason = signalReason;
                }
            }

            SignalHoldReason = activeHoldReason;
            return target;
        }

        /// <summary>
        /// Computes the speed limit for a track using the exact same method the game uses
        /// to place speed-limit signs: BezierArcApproximation finds the minimum curve radius,
        /// then the same radius→speed table from SignPlacer.CurveSegmentInfo.GetMaxSpeedForRadius()
        /// maps that to km/h. Works on any track regardless of whether signs are loaded.
        /// </summary>
        private static float ComputeTrackSpeedLimit(RailTrack track)
        {
            if (track == null) return 120f;

            float defaultLimit = 120f;

            // 1. Turntable track
            if (PathFinder._turntableTrackToTRT != null && PathFinder._turntableTrackToTRT.ContainsKey(track))
            {
                return 15f;
            }

            // 2. LogicTrack ID check for yard, siding, or storage track classification
            var logicTrack = track.LogicTrack();
            if (logicTrack?.ID != null)
            {
                string fullId = logicTrack.ID.FullID ?? "";
                string[] parts = fullId.Split('-');
                string tType = parts.Length > 3 ? parts[3] : "";

                if (tType == DV.Logic.Job.TrackID.STORAGE_TYPE ||
                    tType == DV.Logic.Job.TrackID.LOADING_TYPE ||
                    tType == DV.Logic.Job.TrackID.PARKING_TYPE ||
                    tType == DV.Logic.Job.TrackID.STORAGE_PASSENGER_TYPE ||
                    tType == DV.Logic.Job.TrackID.LOADING_PASSENGER_TYPE)
                {
                    defaultLimit = 20f; // 20 km/h (12-15 mph) for storage, loading, and parking
                }
                else if (tType == DV.Logic.Job.TrackID.REGULAR_IN_TYPE ||
                         tType == DV.Logic.Job.TrackID.REGULAR_OUT_TYPE ||
                         (!string.IsNullOrEmpty(logicTrack.ID.yardId) && tType != DV.Logic.Job.TrackID.MAIN_LINE_TYPE))
                {
                    defaultLimit = 30f; // 30 km/h (18-20 mph) for yard running, arrival/departure, sidings
                }
            }

            // 3. Compute curve radius speed limit from geometry
            if (track.curve == null) return defaultLimit;

            var arcs = new System.Collections.Generic.List<BezierArcApproximation.Arc>();
            BezierArcApproximation.CalculateArcs(track.curve, 0.5f, arcs);

            if (arcs.Count == 0) return defaultLimit;

            float minRadius = float.PositiveInfinity;
            foreach (var arc in arcs)
                if (arc.r < minRadius) minRadius = arc.r;

            // Radius → speed table from SignPlacer (identical to in-game signs)
            float curveLimit;
            if (minRadius < 50f)        curveLimit = 10f;
            else if (minRadius < 70f)   curveLimit = 20f;
            else if (minRadius < 95f)   curveLimit = 30f;
            else if (minRadius < 130f)  curveLimit = 40f;
            else if (minRadius < 170f)  curveLimit = 50f;
            else if (minRadius < 230f)  curveLimit = 60f;
            else if (minRadius < 360f)  curveLimit = 70f;
            else if (minRadius < 700f)  curveLimit = 80f;
            else if (minRadius < 900f)  curveLimit = 90f;
            else if (minRadius < 1200f) curveLimit = 100f;
            else                        curveLimit = 120f;

            return Mathf.Min(defaultLimit, curveLimit);
        }

        /// <summary>Called at module startup — no longer needed but kept for compatibility.</summary>
        public static void BuildSignSpeedLimitCache() { }

        public LocoAI(ILocomotiveRemoteControl remoteControl, TrainCar car) :
            base(remoteControl, car)
        {
        }

        // Disables DriverAssist and SteamCruiseControl via reflection so they don't fight us.
        // Both mods are optional — failures are silently swallowed.
        private static void DisableCompetingMods()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            // DriverAssist: EntityManager.Instance.Loco.Components.CruiseControl = null
            try
            {
                var asm = assemblies.FirstOrDefault(a => a.GetName().Name == "DriverAssist");
                if (asm != null)
                {
                    Type entityManagerType = asm.GetType("EntityManager");
                    FieldInfo instanceField = entityManagerType?.GetField("Instance");
                    object entityManager = instanceField?.GetValue(null);
                    FieldInfo locoField = entityManager?.GetType().GetField("Loco");
                    object loco = locoField?.GetValue(entityManager);
                    if (loco != null)
                    {
                        FieldInfo componentsField = loco.GetType().GetField("Components");
                        object components = componentsField?.GetValue(loco);
                        if (components != null)
                        {
                            PropertyInfo cruiseControlProp = components.GetType().GetProperty("CruiseControl");
                            cruiseControlProp?.SetValue(components, null);
                            Terminal.Log("DriverAssist cruise control disabled");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Module.mod.Logger.Log("DisableCompetingMods DriverAssist: " + e.Message);
            }

            // SteamCruiseControl: Main._cruiseControlManager.IsEnabled = false
            try
            {
                var asm = assemblies.FirstOrDefault(a => a.GetName().Name == "SteamCruiseControl");
                if (asm != null)
                {
                    Type mainType = asm.GetType("SteamCruiseControl.Main");
                    FieldInfo managerField = mainType?.GetField("_cruiseControlManager", BindingFlags.Static | BindingFlags.NonPublic);
                    object manager = managerField?.GetValue(null);
                    if (manager != null)
                    {
                        PropertyInfo isEnabledProp = manager.GetType().GetProperty("IsEnabled");
                        isEnabledProp?.SetValue(manager, false);
                        Terminal.Log("SteamCruiseControl disabled");
                    }
                }
            }
            catch (Exception e)
            {
                Module.mod.Logger.Log("DisableCompetingMods SteamCruiseControl: " + e.Message);
            }
        }

        public bool StartAI(RouteTracker routeTracker)
        {
            if (routeTracker == null)
                return false;

            int generation;
            unchecked { generation = ++_aiGeneration; }

            if (RouteTracker != null && RouteTracker != routeTracker)
            {
                RouteTracker.Dispose();
            }

            RouteTracker = routeTracker;

            if (RouteTracker.TrackState == RouteTracker.TrackingState.OnFinish)
            {
                Terminal.Log("[StartAI] Train is already at the destination track");
                return false;
            }

            DisableCompetingMods();
            RouteTracker.Route?.AdjustSwitches();

            TargetSpeed = TARGET_SPEED_DEFAULT;
            ResetControllerState();
            SignalHoldReason = null;

            running = true;
            _aiCoroutineActive = true;
            try
            {
                Module.StartCoroutine(AICoroutine(generation));
            }
            catch (Exception ex)
            {
                running = false;
                _aiCoroutineActive = false;
                SignalHoldReason = null;
                Module.mod?.Logger.Log("StartAI failed: " + ex);
                throw;
            }

            return true;
        }

        private IEnumerator AICoroutine(int generation)
        {
            RouteTracker activeRouteTracker = RouteTracker;
            IEnumerator driver = AICoroutineCore(activeRouteTracker);

            try
            {
                while (running && generation == _aiGeneration && driver.MoveNext())
                    yield return driver.Current;
            }
            finally
            {
                (driver as IDisposable)?.Dispose();
                if (generation == _aiGeneration)
                {
                    running = false;
                    _aiCoroutineActive = false;
                    SignalHoldReason = null;
                    activeRouteTracker?.Dispose();
                }
            }
        }

        private bool DetermineInitialReverserDirection(Route route)
        {
            if (route?.FirstTrack == null || route.SecondTrack == null || trainCar == null)
                return true;

            bool routeExitsOut = route.FirstTrack.IsTrackOutBranch(route.SecondTrack);

            Bogie locoBogie = trainCar.Bogies?.FirstOrDefault(b => b?.track == route.FirstTrack);

            if (locoBogie != null)
            {
                bool locoFacesOut = locoBogie.TrackDirectionSign > 0f;
                return routeExitsOut == locoFacesOut;
            }

            var (forwardBogie, rearBogie) = Utils.GetOuterBogiesRelativeTo(trainCar);
            if (forwardBogie?.track == route.FirstTrack)
            {
                bool forwardEndFacesOut = Utils.OutwardFromBogieFacesTrackOut(forwardBogie);
                return routeExitsOut == forwardEndFacesOut;
            }

            if (rearBogie?.track == route.FirstTrack)
            {
                // Positive reverser moves from the rear endpoint into the consist,
                // opposite that endpoint's outward direction.
                bool rearEndFacesOut = Utils.OutwardFromBogieFacesTrackOut(rearBogie);
                return routeExitsOut != rearEndFacesOut;
            }

            Vector3 toSecond = route.SecondTrack.transform.position - trainCar.transform.position;
            return Vector3.Dot(trainCar.transform.forward, toSecond) >= 0f;
        }

        private IEnumerator AICoroutineCore(RouteTracker routeTracker)
        {
            const float TIME_WAIT = 0.3f;

            Terminal.Log("Autonomous driver start");
            yield return null;

            bool needForward = DetermineInitialReverserDirection(routeTracker.Route);
            yield return SetReverserDirection(needForward);
            if (!running)
                yield break;

            // Direction is selected while secured, like a human driver. Only
            // release the train after the reverser has reached the planned state.
            yield return ReleaseBrakesForDeparture();
            if (!running)
                yield break;

            // A stopped tracker remains in WrongHeading/ReverseTrain until it
            // observes movement in the new direction. Without a latch the old
            // loop toggled the reverser again every 0.3 seconds.
            bool reversalArmed = true;

            float prevSpeed = 0.0f;
            float targetAcceleration = 2.5f;
            float prevTime = Time.time;

            float timeDelta = TIME_WAIT;

            bool couplerApproach = false;
            string lastLoggedHoldReason = null;
            float holdClearCandidateSince = -1f;

            while (running)
            {
                float speed = Mathf.Abs(remoteControl.GetForwardSpeed() * 3.6f);
                float acceleration = (speed - prevSpeed) / timeDelta;

                if (couplerApproach)
                {
                    SignalHoldReason = null;
                    if (IsCouplerInRange(1.00f))
                    {
                        Terminal.Log($"coupler in ranch");
                        break; //stop train
                    }
                    else if (IsCouplerInRange(7.0f))
                    {
                        TargetSpeed = 1.0f;
                    }
                    else
                    {
                        TargetSpeed = COUPLER_APPROACH_SPEED;
                    }
                }
                else if (routeTracker.TrackState == RouteTracker.TrackingState.RightHeading)
                {
                    if (speed > 3.0f)
                        reversalArmed = true;

                    if (routeTracker.DistanceToFinish < 50.0f && !routeTracker.Route.LastTrack.LogicTrack().IsFree(routeTracker.Trainset)) //finds all couplers not only on right rail
                    {
                        SignalHoldReason = null;
                        TargetSpeed = COUPLER_APPROACH_SPEED;
                    }
                    else if (IsApproachingRotatingTurntable())
                    {
                        SignalHoldReason = "Turntable rotating";
                        TargetSpeed = 0f; // hold until turntable finishes rotating
                    }
                    else
                    {
                        TargetSpeed = GetRouteSpeedTarget(speed);
                    }
                }
                else if (routeTracker.TrackState == RouteTracker.TrackingState.OnStart
                    || routeTracker.TrackState == RouteTracker.TrackingState.BeforeStart)
                {
                    TargetSpeed = GetRouteSpeedTarget(speed);
                }
                else if (routeTracker.TrackState == RouteTracker.TrackingState.StopTrainAfterSwitch)
                {
                    SignalHoldReason = null;
                    TargetSpeed = 10.0f;
                }
                else if (routeTracker.TrackState == RouteTracker.TrackingState.WaitingForSignalReservation)
                {
                    SignalHoldReason = "Waiting for switch reservation";
                    // A protected junction must not be approached while the
                    // reverse manoeuvre is waiting for DV Signals to release it.
                    TargetSpeed = 0.0f;
                }
                else if (routeTracker.TrackState == RouteTracker.TrackingState.WrongHeading
                    || routeTracker.TrackState == RouteTracker.TrackingState.ReverseTrain)
                {
                    if (reversalArmed)
                    {
                        TargetSpeed = 0.0f;
                        if (speed < 3.0f)
                        {
                            yield return Module.StartCoroutine(Reverse());
                            if (!running)
                                break;

                            reversalArmed = false;
                        }
                    }

                    // After changing ends, drive far enough for RouteTracker to
                    // confirm the new heading. Signal/occupancy restrictions still
                    // reduce this target to zero when movement is not authorized.
                    if (!reversalArmed)
                        TargetSpeed = GetRouteSpeedTarget(speed);
                }

                if (!string.IsNullOrEmpty(SignalHoldReason))
                {
                    holdClearCandidateSince = -1f;
                    if (SignalHoldReason != lastLoggedHoldReason)
                    {
                        lastLoggedHoldReason = SignalHoldReason;
                        Terminal.Log($"[Loco AI] Holding: {SignalHoldReason}");
                        Module.mod?.Logger.Log($"[Loco AI] Holding: {SignalHoldReason}");
                    }
                }
                else if (lastLoggedHoldReason != null)
                {
                    // Signal aspects can briefly flicker while DV Signals updates
                    // a block. Resume control immediately, but only announce the
                    // clear after it has remained stable long enough to avoid a
                    // hold/clear log storm.
                    if (holdClearCandidateSince < 0f)
                    {
                        holdClearCandidateSince = Time.time;
                    }
                    else if (Time.time - holdClearCandidateSince >= 3f)
                    {
                        Terminal.Log($"[Loco AI] Cleared ({lastLoggedHoldReason}). Resuming.");
                        Module.mod?.Logger.Log($"[Loco AI] Cleared ({lastLoggedHoldReason}). Resuming.");
                        lastLoggedHoldReason = null;
                        holdClearCandidateSince = -1f;
                    }
                }

                targetAcceleration = MaintainSpeed(targetAcceleration, timeDelta, speed, acceleration);
                UpdateLocomotiveDevices(speed);

                prevSpeed = speed;

                yield return new WaitForSeconds(TIME_WAIT);

                timeDelta = Time.time - prevTime;
                prevTime = Time.time;

                if (routeTracker.TrackState == RouteTracker.TrackingState.OutOfWay)
                    break;

                if (routeTracker.TrackState == RouteTracker.TrackingState.OnFinish)
                {
                    if (routeTracker.Route.LastTrack.LogicTrack().IsFree(routeTracker.Trainset))
                    {
                        break;
                    }
                    else
                    {
                        //on last track is some other car so try to go close to it's coupler
                        couplerApproach = true;
                        Terminal.Log($"coupler approach");
                    }
                }
            }

            // Do not advertise completion until the train is actually stopped;
            // freight haul waits on this flag before starting its next leg.
            TargetSpeed = 0f;
            float stopStarted = Time.time;
            float stoppingPreviousSpeed = Mathf.Abs(remoteControl.GetForwardSpeed() * 3.6f);
            while (stoppingPreviousSpeed > 0.25f && Time.time - stopStarted < 45f)
            {
                remoteControl.UpdateThrottle(-100f);
                MoveTrainBrakeTowards(0.55f);
                MoveIndependentBrakeTowards(stoppingPreviousSpeed < 6f ? 0.25f : 0f);
                yield return new WaitForSeconds(0.1f);
                stoppingPreviousSpeed = Mathf.Abs(remoteControl.GetForwardSpeed() * 3.6f);
            }

            for (int i = 0; i < 5; i++)
            {
                remoteControl.UpdateThrottle(-100f);
                MoveTrainBrakeTowards(0.5f);
                MoveIndependentBrakeTowards(0.25f);
                yield return new WaitForSeconds(0.1f);
            }

            if (_cylinderCockPort != null)
            {
                _cylinderCockPort.ExternalValueUpdate(1.0f);
            }
        }

        bool IsCouplerInRange(float range)
        {
            Trainset trainset = RouteTracker?.Trainset;
            if (trainset?.firstCar == null || trainset.lastCar == null)
                return false;

            // firstCar.front and lastCar.rear are already the two outer
            // couplers. Avoid CouplerLogic.GetLastCoupler's recursive chain
            // walk, which can overflow on a temporarily inconsistent coupling
            // graph while consists are being joined.
            Coupler frontEnd = trainset.firstCar.frontCoupler;
            Coupler rearEnd = trainset.lastCar.rearCoupler;
            Coupler firstCouplerInRange = frontEnd?.GetFirstCouplerInRange(range);
            Coupler firstCouplerInRange2 = rearEnd?.GetFirstCouplerInRange(range);
            return firstCouplerInRange != null || firstCouplerInRange2 != null;
        }

        private IEnumerator ReleaseBrakesForDeparture()
        {
            const int MAX_RELEASE_STEPS = 60;
            for (int step = 0; step < MAX_RELEASE_STEPS && running; step++)
            {
                remoteControl.UpdateThrottle(-100f);
                MoveTrainBrakeTowards(0f);
                MoveIndependentBrakeTowards(0f);

                if (remoteControl.GetTargetBrake() <= 0.01f
                    && remoteControl.GetTargetIndependentBrake() <= 0.01f)
                    yield break;

                yield return new WaitForSeconds(0.1f);
            }

            if (running && (remoteControl.GetTargetBrake() > 0.01f
                || remoteControl.GetTargetIndependentBrake() > 0.01f))
            {
                Terminal.Log("Autonomous driver could not release the brakes; departure cancelled");
                running = false;
            }
        }

        private IEnumerator SetReverserDirection(bool forward)
        {
            const float REVERSER_TOLERANCE = 0.1f;
            const float STOP_SPEED_MPS = 0.1f;
            float target = forward ? 1f : 0f;
            float current = remoteControl.GetReverserValue();

            remoteControl.UpdateThrottle(-100f);

            // Do not change direction while rolling. Secure the train first and
            // retain the brakes until the caller explicitly releases them.
            float stopStarted = Time.time;
            while (running && Mathf.Abs(remoteControl.GetForwardSpeed()) > STOP_SPEED_MPS)
            {
                remoteControl.UpdateThrottle(-100f);
                MoveTrainBrakeTowards(0.5f);
                MoveIndependentBrakeTowards(
                    Mathf.Abs(remoteControl.GetForwardSpeed() * 3.6f) < 6f ? 0.25f : 0f);

                if (Time.time - stopStarted > 45f)
                {
                    Terminal.Log("Autonomous driver could not stop before selecting direction; AI stopped");
                    running = false;
                    yield break;
                }

                yield return new WaitForSeconds(0.1f);
            }

            for (int step = 0; running && Mathf.Abs(current - target) > REVERSER_TOLERANCE && step < 4; step++)
            {
                remoteControl.UpdateReverser(current < target ? ToggleDirection.UP : ToggleDirection.DOWN);
                yield return null;
                current = remoteControl.GetReverserValue();
            }

            if (Mathf.Abs(current - target) > REVERSER_TOLERANCE)
            {
                Terminal.Log($"Autonomous driver could not select {(forward ? "forward" : "reverse")}; AI stopped");
                running = false;
                yield break;
            }

            Terminal.Log($"Autonomous driver selected {(forward ? "forward" : "reverse")} for the planned route");
        }

        IEnumerator Reverse()
        {
            bool direction = remoteControl.GetReverserValue() > 0.5f;
            float stopStarted = Time.time;

            while (running && (remoteControl.GetTargetThrottle() > Mathf.Epsilon
                || Mathf.Abs(remoteControl.GetForwardSpeed()) > 0.1f))
            {
                remoteControl.UpdateThrottle(-100.0f);
                MoveTrainBrakeTowards(0.5f);
                MoveIndependentBrakeTowards(
                    Mathf.Abs(remoteControl.GetForwardSpeed() * 3.6f) < 6f ? 0.25f : 0f);

                if (Time.time - stopStarted > 45f)
                {
                    Terminal.Log("Autonomous driver could not stop for reversal; AI stopped");
                    running = false;
                    yield break;
                }
                yield return null;
            }

            if (!running)
                yield break;

            yield return SetReverserDirection(!direction);
        }

        // ─── Freight haul ────────────────────────────────────────────────────

        /// <summary>Stops both the AI driving and any active freight haul.</summary>
        public void StopAll()
        {
            unchecked { _freightHaulGeneration++; }
            _freightHaulActive = false;
            _freightHaulState = HaulState.Idle;
            _freightHaulError = null;
            SignalHoldReason = null;
            Stop();
        }

        /// <summary>
        /// Starts a full freight haul: loco → cars → couple → release HB → destination → uncouple → apply HB.
        /// If already coupled, skips directly to handbrake release and destination routing.
        /// </summary>
        public void StartFreightHaul(RouteTask task, TrainCar loco)
        {
            int generation;
            unchecked { generation = ++_freightHaulGeneration; }
            _freightHaulActive = true;
            _freightHaulState = HaulState.Starting;
            _freightHaulError = null;
            Stop();
            Module.StartCoroutine(FreightHaulCoroutine(task, loco, generation));
        }

        private bool IsFreightHaulCurrent(int generation)
        {
            return _freightHaulActive && generation == _freightHaulGeneration;
        }

        private void SetHaulFailure(string error)
        {
            _freightHaulError = error;
            _freightHaulState = HaulState.Failed;
            _freightHaulActive = false;
            Terminal.Log("Freight haul: " + error);
            Module.mod?.Logger.Log("Freight haul failed: " + error);
        }

        private IEnumerator FreightHaulCoroutine(RouteTask task, TrainCar loco, int generation)
        {
            if (generation != _freightHaulGeneration)
                yield break;

            IEnumerator runner = FreightHaulCore(task, loco, generation);
            while (true)
            {
                object current;
                try
                {
                    if (!runner.MoveNext())
                        yield break;
                    current = runner.Current;
                }
                catch (Exception ex)
                {
                    Module.mod?.Logger.Log("Freight haul error: " + ex);
                    SetHaulFailure("Error: " + ex.Message);
                    yield break;
                }
                yield return current;
            }
        }

        private IEnumerator FreightHaulCore(RouteTask task, TrainCar loco, int generation)
        {
            if (!IsFreightHaulCurrent(generation)) yield break;

            // ── Step 0: Identify cars and check if already coupled ───────────
            Trainset freightTrainset = task.TrainSets.FirstOrDefault();
            if (freightTrainset == null)
            {
                var taskCars = task.DVTask?.GetTaskData()?.cars;
                if (taskCars != null && taskCars.Count > 0)
                {
                    foreach (var car in taskCars)
                    {
                        if (car != null && TrainCarRegistry.Instance.logicCarToTrainCar.TryGetValue(car, out var tc))
                        {
                            freightTrainset = tc.trainset;
                            task.TrainSets.Add(freightTrainset);
                            break;
                        }
                    }
                }
            }

            if (freightTrainset == null)
            {
                SetHaulFailure("No freight trainset found in task");
                yield break;
            }

            // Check if loco is already coupled to the task's cars
            bool alreadyCoupled = false;
            if (task.TrainSets.Contains(loco.trainset))
            {
                alreadyCoupled = true;
            }
            else
            {
                var taskCars = task.DVTask?.GetTaskData()?.cars;
                if (taskCars != null && taskCars.Count > 0)
                {
                    alreadyCoupled = loco.trainset.cars.Any(c => c.logicCar != null && taskCars.Contains(c.logicCar));
                }
            }

            if (!alreadyCoupled)
            {
                Track carTrack = freightTrainset.firstCar.Bogies[0].track.LogicTrack();
                if (freightTrainset.cars.Count > 1 && freightTrainset.lastCar.Bogies != null && freightTrainset.lastCar.Bogies.Length > 0)
                {
                    float distToFirst = Vector3.Distance(loco.transform.position, freightTrainset.firstCar.transform.position);
                    float distToLast = Vector3.Distance(loco.transform.position, freightTrainset.lastCar.transform.position);
                    if (distToLast < distToFirst)
                    {
                        carTrack = freightTrainset.lastCar.Bogies[0].track.LogicTrack();
                    }
                }

                Track locoTrack = loco.Bogies != null && loco.Bogies.Length > 0
                    ? loco.Bogies[0].track.LogicTrack()
                    : loco.trainset.firstCar.Bogies[0].track.LogicTrack();

                if (locoTrack.ID.FullID == carTrack.ID.FullID)
                {
                    // Loco and cars are on the same track — check if within couple range
                    Coupler frontEnd = loco.trainset.firstCar.frontCoupler;
                    Coupler rearEnd = loco.trainset.lastCar.rearCoupler;
                    bool inRange = frontEnd?.GetFirstCouplerInRange(2.5f) != null || rearEnd?.GetFirstCouplerInRange(2.5f) != null;

                    if (inRange)
                    {
                        _freightHaulState = HaulState.Coupling;
                        Terminal.Log("Freight haul: in range on same track – coupling");
                        yield return TryCoupleAndReleaseHandbrakes(loco);
                        yield return new WaitForSeconds(1.5f);
                    }
                    else
                    {
                        SetHaulFailure("Loco on same track as cars; couple manually first");
                        yield break;
                    }
                }
                else
                {
                    // ── Phase 1: drive loco to freight cars ──────────────────────────
                    _freightHaulState = HaulState.RoutingToCars;
                    Terminal.Log("Freight haul: phase 1 – routing to cars");

                    // The target cars intentionally occupy this destination track, so
                    // the approach-to-couple leg cannot treat destination occupancy as
                    // an ADS obstruction. The post-coupling leg below uses the full
                    // ADS safety check once every car belongs to this consist.
                    var (locoStartTrack, locoBannedTransitions) = RouteCommand.GetTrainDepartureInfo(loco);
                    var toCarsTask = Route.FindRoute(
                        locoStartTrack ?? locoTrack, carTrack, Module.settings.ReversingStrategy, loco.trainset, locoBannedTransitions);
                    while (!toCarsTask.IsCompleted) yield return null;

                    if (!IsFreightHaulCurrent(generation)) yield break;

                    if (toCarsTask.IsFaulted || toCarsTask.Result == null)
                    {
                        string msg = toCarsTask.Exception?.InnerException?.Message ?? "Route to cars not found";
                        SetHaulFailure("Route to cars failed: " + msg);
                        yield break;
                    }

                    var chain1 = RouteTaskChain.FromDestination(carTrack, loco.trainset);
                    var tracker1 = new RouteTracker(chain1, true);
                    tracker1.SetRoute(toCarsTask.Result, loco.trainset);
                    Module.ActiveRoute.Route = toCarsTask.Result;
                    Module.ActiveRoute.RouteTracker = tracker1;

                    while (_aiCoroutineActive && IsFreightHaulCurrent(generation)) yield return null;
                    if (!IsFreightHaulCurrent(generation)) yield break;

                    _freightHaulState = HaulState.ApproachingCars;
                    if (!StartAI(tracker1))
                    {
                        SetHaulFailure("Autonomous driver could not start for approach leg");
                        yield break;
                    }
                    while (_aiCoroutineActive && IsFreightHaulCurrent(generation)) yield return null;

                    if (!IsFreightHaulCurrent(generation)) { Stop(); yield break; }

                    // ── Phase 2: couple and release handbrakes ───────────────────────
                    _freightHaulState = HaulState.Coupling;
                    Terminal.Log("Freight haul: phase 2 – coupling");
                    yield return TryCoupleAndReleaseHandbrakes(loco);
                    yield return new WaitForSeconds(1.5f);

                    if (!IsFreightHaulCurrent(generation)) yield break;
                }
            }
            else
            {
                // Loco is already coupled to the cars. Release handbrakes.
                _freightHaulState = HaulState.Coupling;
                Terminal.Log("Freight haul: loco already coupled – releasing handbrakes");
                yield return TryCoupleAndReleaseHandbrakes(loco);
                yield return new WaitForSeconds(1.0f);

                if (!IsFreightHaulCurrent(generation)) yield break;
            }

            // ── Phase 3: drive to destination ────────────────────────────────
            _freightHaulState = HaulState.RoutingToDestination;
            Terminal.Log($"Freight haul: phase 3 – routing to {task.DestinationTrack.ID.FullID}");

            var (preferredStartTrack, bannedTransitions) = RouteCommand.GetTrainDepartureInfo(loco);
            Track trackEnd1 = preferredStartTrack ?? loco.trainset.firstCar.Bogies[0].track.LogicTrack();
            Track firstCarTrack = loco.trainset.firstCar.Bogies[0].track.LogicTrack();
            Track lastCarTrack = loco.trainset.lastCar.Bogies[0].track.LogicTrack();
            Track trackEnd2 = (trackEnd1 == firstCarTrack) ? lastCarTrack : firstCarTrack;

            var toDestTask = RouteCommand.FindRouteWithADSAlternative(
                trackEnd1, task.DestinationTrack, Module.settings.ReversingStrategy, loco.trainset, bannedTransitions);
            while (!toDestTask.IsCompleted) yield return null;

            if (!IsFreightHaulCurrent(generation)) yield break;

            Route bestDestRoute = toDestTask.IsFaulted ? null : toDestTask.Result;

            // If consist spans multiple tracks or trackEnd1 required reversing or failed,
            // also evaluate trackEnd2 to find the cleanest, most direct route
            if (trackEnd2 != null && trackEnd2 != trackEnd1 && (bestDestRoute == null || bestDestRoute.Reverses.Count > 0))
            {
                var toDestTask2 = RouteCommand.FindRouteWithADSAlternative(
                    trackEnd2, task.DestinationTrack, Module.settings.ReversingStrategy, loco.trainset);
                while (!toDestTask2.IsCompleted) yield return null;

                if (!IsFreightHaulCurrent(generation)) yield break;

                Route altRoute = toDestTask2.IsFaulted ? null : toDestTask2.Result;
                if (altRoute != null)
                {
                    if (bestDestRoute == null
                        || altRoute.Reverses.Count < bestDestRoute.Reverses.Count
                        || (bestDestRoute.Reverses.Count > 0 && altRoute.Length < bestDestRoute.Length))
                    {
                        bestDestRoute = altRoute;
                        Terminal.Log($"Freight haul: chose smarter route from opposite end ({trackEnd2.ID.FullID})");
                    }
                }
            }

            if (bestDestRoute == null)
            {
                string failure = Compatibility.ADSCompatibility.AppendFailureNotice(
                    toDestTask.Exception?.InnerException?.Message ?? "Route to destination was not found",
                    loco.trainset);
                SetHaulFailure("Route to destination failed: " + failure);
                yield break;
            }

            LogADSRouteResult("Freight haul: route to destination ready", loco.trainset);

            var chain2 = RouteTaskChain.FromDestination(task.DestinationTrack, loco.trainset);
            var tracker2 = new RouteTracker(chain2, true);
            tracker2.SetRoute(bestDestRoute, loco.trainset);
            Module.ActiveRoute.Route = bestDestRoute;
            Module.ActiveRoute.RouteTracker = tracker2;

            _freightHaulState = HaulState.DrivingToDestination;
            if (!StartAI(tracker2))
            {
                SetHaulFailure("Autonomous driver could not start for destination leg");
                yield break;
            }
            while (_aiCoroutineActive && IsFreightHaulCurrent(generation)) yield return null;

            if (!IsFreightHaulCurrent(generation)) { Stop(); yield break; }

            // ── Phase 4: uncouple and apply handbrakes ───────────────────────
            _freightHaulState = HaulState.Uncoupling;
            Terminal.Log("Freight haul: phase 4 – uncoupling");
            yield return UncoupleAndApplyHandbrakes(loco);

            if (generation == _freightHaulGeneration)
            {
                _freightHaulActive = false;
                _freightHaulState = HaulState.Completed;
            }
            Terminal.Log("Freight haul: complete!");
            Module.PlayClip(Module.trainEnd);
        }

        private static void LogADSRouteResult(string result, Trainset trainset)
        {
            string message = Compatibility.ADSCompatibility.AppendRouteWarningNotice(result, trainset);
            if (!string.IsNullOrWhiteSpace(RouteCommand.LastADSAlternativeNotice))
                message += "\n" + RouteCommand.LastADSAlternativeNotice;
            Terminal.Log(message);
        }

        private IEnumerator TryCoupleAndReleaseHandbrakes(TrainCar loco)
        {
            // Couple any couplers at the ends of the current trainset that are in range
            Coupler frontEnd = loco.trainset.firstCar.frontCoupler;
            Coupler rearEnd = loco.trainset.lastCar.rearCoupler;
            frontEnd?.GetFirstCouplerInRange(2.5f)?.TryCouple();
            rearEnd?.GetFirstCouplerInRange(2.5f)?.TryCouple();
            yield return new WaitForSeconds(0.5f);

            // Release handbrakes on all non-loco cars now in the trainset
            foreach (TrainCar car in loco.trainset.cars)
            {
                if (!car.IsLoco && car.brakeSystem.hasHandbrake)
                {
                    car.brakeSystem.SetHandbrakePosition(0f);
                    Terminal.Log($"Released handbrake on {car.logicCar.ID}");
                }
            }
        }

        private IEnumerator UncoupleAndApplyHandbrakes(TrainCar loco)
        {
            // Record freight cars before uncoupling so we can apply their handbrakes after
            List<TrainCar> freightCars = loco.trainset.cars.Where(c => !c.IsLoco).ToList();

            if (loco.trainset.firstCar == loco)
                loco.rearCoupler.Uncouple();
            else if (loco.trainset.lastCar == loco)
                loco.frontCoupler.Uncouple();
            else
            {
                loco.frontCoupler.Uncouple();
                yield return null;
                loco.rearCoupler.Uncouple();
            }

            yield return new WaitForSeconds(0.5f);

            foreach (TrainCar car in freightCars)
            {
                if (car.brakeSystem.hasHandbrake)
                {
                    car.brakeSystem.SetHandbrakePosition(1f);
                    Terminal.Log($"Applied handbrake on {car.logicCar.ID}");
                }
            }
        }

    }
}
