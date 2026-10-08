using CommandTerminal;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace RevisedDVRoute.Compatibility
{
    /// <summary>
    /// Owns the short, rolling player reservation used by an active Route
    /// Manager route. The implementation deliberately uses reflection: DV
    /// Signals remains optional and this assembly must still load without it.
    ///
    /// A reservation is made through the next governing signal rather than by
    /// marking arbitrary rails. This preserves DV Signals' block, aspect, and
    /// interlocking rules. AI Traffic already treats these non-AI reservations
    /// as player right-of-way.
    /// </summary>
    internal static class DVSignalsRouteReservation
    {
        private const float RetryInterval = 2f;

        private sealed class ReservationCandidate
        {
            public object Signal;
            public string SignalName;
            public int FirstPathIndex;
            public int LastPathIndex;
        }

        private static Type signalType;
        private static Type signalManagerType;
        private static Type trackUtilsType;
        private static Type trackReserverType;

        private static PropertyInfo signalManagerInstance;
        private static PropertyInfo allControllers;
        private static PropertyInfo allSignals;
        private static PropertyInfo signalBlock;
        private static PropertyInfo signalParent;
        private static PropertyInfo signalName;
        private static PropertyInfo blockTracks;

        private static FieldInfo signalAllowReserving;
        private static FieldInfo trackInfoTrack;
        private static FieldInfo trackInfoDirection;

        private static MethodInfo reserveForSignal;
        private static MethodInfo clearFromSignal;
        private static MethodInfo hasReservation;
        private static MethodInfo trackDirectionFromTrack;

        private static object reservedSignal;
        private static Route reservedRoute;
        private static int reservedLastPathIndex = -1;
        private static bool ownsReservation;
        private static float nextAttemptTime;
        private static string lastOutcome;
        private static bool apiUnavailableLogged;
        private static bool apiErrorLogged;

        public static string StatusDescription
        {
            get
            {
                if (!DVSignalsCompatibility.IsActive)
                    return "DV Signals not active";

                if (!DVSignalsCompatibility.PlayerRouteReservationEnabled)
                    return "player route reservation disabled";

                if (Module.ActiveRoute == null || !Module.ActiveRoute.IsSet)
                    return "player route reservation ready; no active route";

                if (reservedSignal != null && ownsReservation && HasReservation(reservedSignal))
                    return "player route block reserved at " + GetSignalName(reservedSignal);

                if (!string.IsNullOrEmpty(lastOutcome))
                    return lastOutcome.TrimEnd('.');

                return "searching for the next player route block";
            }
        }

        public static void Begin(Route route)
        {
            if (!CanManage(route))
            {
                Clear();
                return;
            }

            if (reservedRoute != null && reservedRoute != route)
                Clear();

            reservedRoute = route;
            if (reservedSignal != null && ownsReservation && HasReservation(reservedSignal))
                return;

            TryReserveNextBlock(route, 0);
        }

        public static void Update(Route route, int frontPathIndex, int rearPathIndex)
        {
            if (!CanManage(route))
            {
                Clear();
                return;
            }

            if (reservedRoute != null && reservedRoute != route)
                Clear();

            reservedRoute = route;

            if (reservedSignal != null)
            {
                if (!ownsReservation || !HasReservation(reservedSignal))
                {
                    ResetReservationState();
                }
                else if (rearPathIndex <= reservedLastPathIndex)
                {
                    return;
                }
                else
                {
                    ClearCurrentReservation();
                }
            }

            TryReserveNextBlock(route, Math.Max(0, frontPathIndex));
        }

        public static void Clear()
        {
            ClearCurrentReservation();
            reservedRoute = null;
            nextAttemptTime = 0f;
        }

        internal static bool OwnsSignal(object signal)
        {
            return signal != null
                && ownsReservation
                && reservedSignal != null
                && ReferenceEquals(reservedSignal, signal);
        }

        private static bool CanManage(Route route)
        {
            return route != null
                && route.Path != null
                && route.Path.Count > 1
                && DVSignalsCompatibility.PlayerRouteReservationEnabled
                && EnsureApi();
        }

        private static void TryReserveNextBlock(Route route, int startPathIndex)
        {
            if (Time.time < nextAttemptTime)
                return;

            ReservationCandidate candidate;
            try
            {
                // A foreign reservation may have prevented one or more route
                // switches from being aligned when the route was first created.
                // Recheck before requesting the next block; protected switches
                // remain untouched until their reservation is released.
                route.AdjustSwitchesForSignalReservation();
                candidate = FindNextCandidate(route, startPathIndex);
            }
            catch (Exception exception)
            {
                // An optional compatibility failure must never abort creation
                // or tracking of the underlying Route Manager route.
                LogApiError(exception);
                return;
            }

            if (candidate == null)
            {
                nextAttemptTime = Time.time + RetryInterval;
                SetOutcome("No reservable DV Signals block covers the remaining player route.");
                return;
            }

            bool reserved;
            try
            {
                reserved = (bool)reserveForSignal.Invoke(null, new[] { candidate.Signal });
            }
            catch (Exception exception)
            {
                LogApiError(exception);
                return;
            }

            if (!reserved)
            {
                nextAttemptTime = Time.time + RetryInterval;
                SetOutcome("Player route is waiting for DV Signals block " + candidate.SignalName + " to become available.");
                return;
            }

            reservedSignal = candidate.Signal;
            reservedLastPathIndex = candidate.LastPathIndex;
            ownsReservation = true;
            nextAttemptTime = 0f;
            SetOutcome("Reserved DV Signals player route block at " + candidate.SignalName + ".");
        }

        private static ReservationCandidate FindNextCandidate(Route route, int startPathIndex)
        {
            ReservationCandidate best = null;
            foreach (object signal in GetSignals())
            {
                if (!CanReserve(signal))
                    continue;

                ReservationCandidate candidate = BuildCandidate(signal, route, startPathIndex);
                if (candidate == null)
                    continue;

                if (best == null
                    || candidate.FirstPathIndex < best.FirstPathIndex
                    || (candidate.FirstPathIndex == best.FirstPathIndex
                        && candidate.LastPathIndex > best.LastPathIndex))
                {
                    best = candidate;
                }
            }

            return best;
        }

        private static bool CanReserve(object signal)
        {
            if (signal == null)
                return false;

            try
            {
                // Distant/repeater signals share a block with their parent and
                // are not valid owners for a route reservation.
                if (signalParent.GetValue(signal, null) != null)
                    return false;

                object allow = signalAllowReserving.GetValue(signal);
                return allow is bool && (bool)allow && signalBlock.GetValue(signal, null) != null;
            }
            catch (Exception exception)
            {
                LogApiError(exception);
                return false;
            }
        }

        private static ReservationCandidate BuildCandidate(object signal, Route route, int startPathIndex)
        {
            object block;
            try
            {
                block = signalBlock.GetValue(signal, null);
            }
            catch (Exception exception)
            {
                LogApiError(exception);
                return null;
            }

            IEnumerable tracks = blockTracks.GetValue(block, null) as IEnumerable;
            if (tracks == null)
                return null;

            var directedTracks = new List<KeyValuePair<RailTrack, object>>();
            foreach (object trackInfo in tracks)
            {
                RailTrack track = trackInfoTrack.GetValue(trackInfo) as RailTrack;
                object direction = trackInfoDirection.GetValue(trackInfo);
                if (track != null && direction != null)
                    directedTracks.Add(new KeyValuePair<RailTrack, object>(track, direction));
            }

            int first = -1;
            int last = -1;
            for (int i = Math.Max(0, startPathIndex); i < route.Path.Count; i++)
            {
                RailTrack routeTrack = route.Path[i];
                object routeDirection = GetRouteDirection(route, i);
                if (routeTrack == null || routeDirection == null)
                    continue;

                if (!directedTracks.Any(item => item.Key == routeTrack && Equals(item.Value, routeDirection)))
                    continue;

                if (first < 0)
                    first = i;
                last = i;
            }

            if (first < 0)
                return null;

            return new ReservationCandidate
            {
                Signal = signal,
                SignalName = GetSignalName(signal),
                FirstPathIndex = first,
                LastPathIndex = last
            };
        }

        private static object GetRouteDirection(Route route, int pathIndex)
        {
            try
            {
                if (pathIndex > 0)
                    return trackDirectionFromTrack.Invoke(null, new object[] { route.Path[pathIndex], route.Path[pathIndex - 1] });

                if (route.Path.Count > 1)
                {
                    object nextDirection = trackDirectionFromTrack.Invoke(null, new object[] { route.Path[1], route.Path[0] });
                    if (nextDirection != null && nextDirection.GetType().IsEnum)
                    {
                        int value = Convert.ToInt32(nextDirection);
                        return Enum.ToObject(nextDirection.GetType(), value == 0 ? 1 : 0);
                    }
                }
            }
            catch (Exception exception)
            {
                LogApiError(exception);
            }

            return null;
        }

        private static IEnumerable<object> GetSignals()
        {
            var result = new List<object>();
            try
            {
                object manager = signalManagerInstance.GetValue(null, null);
                if (manager == null)
                    return result;

                IEnumerable controllers = allControllers.GetValue(manager, null) as IEnumerable;
                if (controllers == null)
                    return result;

                foreach (object controller in controllers)
                {
                    IEnumerable signals = allSignals.GetValue(controller, null) as IEnumerable;
                    if (signals == null)
                        continue;

                    foreach (object signal in signals)
                    {
                        if (signal != null)
                            result.Add(signal);
                    }
                }
            }
            catch (Exception exception)
            {
                LogApiError(exception);
            }

            return result;
        }

        private static void ClearCurrentReservation()
        {
            if (reservedSignal == null)
            {
                ResetReservationState();
                return;
            }

            if (ownsReservation && EnsureApi() && HasReservation(reservedSignal))
            {
                try
                {
                    clearFromSignal.Invoke(null, new[] { reservedSignal });
                    SetOutcome("Released DV Signals player route block at " + GetSignalName(reservedSignal) + ".");
                }
                catch (Exception exception)
                {
                    LogApiError(exception);
                }
            }

            ResetReservationState();
        }

        private static void ResetReservationState()
        {
            reservedSignal = null;
            reservedLastPathIndex = -1;
            ownsReservation = false;
        }

        private static bool HasReservation(object signal)
        {
            if (signal == null || !EnsureApi())
                return false;

            try
            {
                return (bool)hasReservation.Invoke(null, new[] { signal });
            }
            catch (Exception exception)
            {
                LogApiError(exception);
                return false;
            }
        }

        private static string GetSignalName(object signal)
        {
            if (signal == null || signalName == null)
                return "unknown signal";

            try
            {
                return signalName.GetValue(signal, null) as string ?? "unknown signal";
            }
            catch (Exception)
            {
                return "unknown signal";
            }
        }

        private static bool EnsureApi()
        {
            if (reserveForSignal != null)
                return true;

            if (!DVSignalsCompatibility.IsActive)
                return false;

            signalType = FindType("Signals.Game.Signal");
            signalManagerType = FindType("Signals.Game.SignalManager");
            trackUtilsType = FindType("Signals.Game.Railway.TrackUtils");
            trackReserverType = FindType("Signals.Game.Railway.TrackReserver");

            signalManagerInstance = signalManagerType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            allControllers = signalManagerType?.GetProperty("AllControllers", BindingFlags.Public | BindingFlags.Instance);
            signalBlock = signalType?.GetProperty("Block", BindingFlags.Public | BindingFlags.Instance);
            signalParent = signalType?.GetProperty("Parent", BindingFlags.Public | BindingFlags.Instance);
            signalAllowReserving = signalType?.GetField("AllowReserving", BindingFlags.Public | BindingFlags.Instance);
            signalName = signalType?.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);

            Type controllerType = FindType("Signals.Game.Controllers.BasicSignalController");
            allSignals = controllerType?.GetProperty("AllSignals", BindingFlags.Public | BindingFlags.Instance);

            Type blockType = FindType("Signals.Game.Railway.TrackBlock");
            blockTracks = blockType?.GetProperty("Tracks", BindingFlags.Public | BindingFlags.Instance);

            Type trackInfoType = FindType("Signals.Game.Railway.TrackInfo");
            trackInfoTrack = trackInfoType?.GetField("Track", BindingFlags.Public | BindingFlags.Instance);
            trackInfoDirection = trackInfoType?.GetField("Direction", BindingFlags.Public | BindingFlags.Instance);

            if (trackReserverType != null && signalType != null)
            {
                reserveForSignal = trackReserverType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(method => MethodMatches(method, "ReserveForSignal", signalType));
                clearFromSignal = trackReserverType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(method => MethodMatches(method, "ClearFromSignal", signalType));
                hasReservation = trackReserverType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(method => MethodMatches(method, "HasReservation", signalType));
            }

            trackDirectionFromTrack = trackUtilsType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "TrackDirectionFromTrack"
                    && method.GetParameters().Length == 2
                    && method.GetParameters().All(parameter => parameter.ParameterType == typeof(RailTrack)));

            if (reserveForSignal == null || clearFromSignal == null || hasReservation == null
                || signalManagerInstance == null || allControllers == null || allSignals == null
                || signalBlock == null || signalParent == null || signalAllowReserving == null
                || blockTracks == null || trackInfoTrack == null || trackInfoDirection == null
                || trackDirectionFromTrack == null)
            {
                LogUnavailable("the player route reservation API was not found");
                return false;
            }

            return true;
        }

        private static bool MethodMatches(MethodInfo method, string name, Type parameterType)
        {
            if (method.Name != name)
                return false;

            if (name == "ClearFromSignal")
            {
                if (method.ReturnType != typeof(void))
                    return false;
            }
            else if (method.ReturnType != typeof(bool))
                return false;

            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == parameterType;
        }

        private static Type FindType(string fullName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, false))
                .FirstOrDefault(type => type != null);
        }

        private static void SetOutcome(string message)
        {
            if (message == lastOutcome)
                return;

            lastOutcome = message;
            Terminal.Log("DV Signals: " + message);
            Module.mod?.Logger.Log("DV Signals: " + message);
        }

        private static void LogUnavailable(string reason)
        {
            if (apiUnavailableLogged)
                return;

            apiUnavailableLogged = true;
            Module.mod?.Logger.Log("DV Signals player route reservation unavailable: " + reason + ".");
        }

        private static void LogApiError(Exception exception)
        {
            nextAttemptTime = Time.time + RetryInterval;

            if (apiErrorLogged)
                return;

            apiErrorLogged = true;
            Module.mod?.Logger.Log("DV Signals player route reservation encountered an API error and will retry: "
                + exception.GetType().Name);
        }
    }
}
