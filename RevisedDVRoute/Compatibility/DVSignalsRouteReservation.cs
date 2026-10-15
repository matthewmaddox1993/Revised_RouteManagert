using CommandTerminal;
using DV.Logic.Job;
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
        private static FieldInfo signalId;
        private static PropertyInfo blockTracks;

        private static FieldInfo signalAllowReserving;
        private static FieldInfo trackInfoTrack;
        private static FieldInfo trackInfoDirection;

        private static MethodInfo reserveForSignal;
        private static MethodInfo clearFromSignal;
        private static MethodInfo hasReservation;
        private static MethodInfo trackDirectionFromTrack;
        private static PropertyInfo multiplayerActive;
        private static MethodInfo multiplayerReserve;
        private static MethodInfo multiplayerCancel;
        private static FieldInfo multiplayerReserveResult;
        private static FieldInfo multiplayerClearResult;

        private static object reservedSignal;
        private static Route reservedRoute;
        private static int reservedLastPathIndex = -1;
        private static bool ownsReservation;
        private static bool reservationSuspendedForRoute;
        private static float nextAttemptTime;
        private static string lastOutcome;
        private static bool apiUnavailableLogged;
        private static bool apiErrorLogged;
        private static object pendingReservationSignal;
        private static Route pendingReservationRoute;
        private static int pendingReservationLastPathIndex = -1;
        private static bool reservationRequestPending;
        private static object pendingClearSignal;
        private static bool clearRequestPending;
        private static readonly Action<int, bool> ReservationResultHandler = OnMultiplayerReservationResult;
        private static readonly Action<int> ClearResultHandler = OnMultiplayerClearResult;

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

                if (reservationRequestPending)
                    return "waiting for the DV Signals multiplayer host to reserve the next block";

                if (clearRequestPending)
                    return "waiting for the DV Signals multiplayer host to release the previous block";

                if (reservationSuspendedForRoute)
                    return "player route reservation suspended after DV Signals released it";

                if (reservedSignal != null && ownsReservation && HasReservation(reservedSignal))
                    return "player route block reserved at " + GetSignalName(reservedSignal);

                if (!string.IsNullOrEmpty(lastOutcome))
                    return lastOutcome.TrimEnd('.');

                return "searching for the next player route block";
            }
        }

        internal static bool OwnsActiveReservation => ownsReservation && reservedSignal != null && HasReservation(reservedSignal);
        internal static int ReservedLastPathIndex => reservedLastPathIndex;

        /// <summary>
        /// Scans the remaining route for reservation and ADS block conflicts.
        /// Physical STOP aspects are checked separately at their mast positions
        /// by DVSignalsCompatibility.TryGetStopSignalAhead.
        /// </summary>
        internal static bool GetSignalRestrictionAhead(
            Route route,
            int currentPathIndex,
            double distanceToNextTrack,
            out double distanceToRestriction,
            out float restrictedSpeedKph,
            out string signalReason)
        {
            distanceToRestriction = double.MaxValue;
            restrictedSpeedKph = float.MaxValue;
            signalReason = null;

            if (route?.Path == null || currentPathIndex < 0)
                return false;

            bool adsManagesSignals = ADSCompatibility.ManagesDVSignals;
            if (adsManagesSignals)
                ADSCompatibility.RefreshActiveRouteSafety(route);

            double dist = Math.Max(0.0, distanceToNextTrack);

            for (int i = currentPathIndex + 1; i < route.Path.Count; i++)
            {
                RailTrack track = route.Path[i];
                if (track == null) break;

                // 1. An AI never owns the player route's rolling reservation.
                // Treat every active DV Signals reservation as foreign here;
                // the player planner uses a separate API that ignores its own.
                bool reserved;
                string owner;
                if (!DVSignalsCompatibility.TryIsTrackReservedForAI(track,
                    out reserved, out owner))
                {
                    // With no trustworthy authority state there is no safe
                    // signal distance to calculate. Hold here rather than
                    // continuing on an assumed-clear aspect.
                    distanceToRestriction = 0.0;
                    restrictedSpeedKph = 0f;
                    signalReason = "DV Signals reservation status unavailable; holding";
                    return true;
                }

                if (reserved)
                {
                    distanceToRestriction = dist;
                    restrictedSpeedKph = 0f;
                    signalReason = "Signal STOP: " + (owner ?? track.LogicTrack()?.ID?.FullID ?? "Ahead");
                    return true;
                }

                // 2. Check if the player route's DV Signals block authority ends before this track
                if (DVSignalsCompatibility.PlayerRouteReservationEnabled && !reservationSuspendedForRoute)
                {
                    if (reservedLastPathIndex >= 0 && i > reservedLastPathIndex && (!ownsReservation || !HasReservation(reservedSignal)))
                    {
                        distanceToRestriction = dist;
                        restrictedSpeedKph = 0f;
                        signalReason = "Waiting for signal clearance";
                        return true;
                    }
                }

                // 3. Check ADS signal / reservation state if ADS manages signals
                if (adsManagesSignals)
                {
                    if (ADSCompatibility.IsTrackBlockedAhead(track))
                    {
                        distanceToRestriction = dist;
                        restrictedSpeedKph = 0f;
                        signalReason = "ADS route conflict on "
                            + (track.LogicTrack()?.ID?.FullID ?? "track ahead");
                        return true;
                    }
                }

                Track lt = track.LogicTrack();
                if (lt != null)
                    dist += Math.Max(0.0, lt.length);

                if (dist > 2500.0)
                    break;
            }

            return false;
        }

        public static void Begin(Route route)
        {
            if (ADSCompatibility.OwnsSignalAuthority(route, null))
            {
                YieldToADS(route);
                return;
            }

            if (!CanManage(route))
            {
                Clear();
                return;
            }

            if (reservedRoute != null && reservedRoute != route)
                Clear();

            reservedRoute = route;
            if (reservationSuspendedForRoute)
                return;

            if (reservedSignal != null && ownsReservation && HasReservation(reservedSignal))
                return;

            TryReserveNextBlock(route, 0);
        }

        public static void Update(Route route, int frontPathIndex, int rearPathIndex)
        {
            if (ADSCompatibility.OwnsSignalAuthority(route, null))
            {
                YieldToADS(route);
                return;
            }

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
                    string releasedSignalName = GetSignalName(reservedSignal);
                    ResetReservationState();
                    // DV Signals can reject an update after its own occupancy or
                    // interlocking state changes. Reclaiming the same signal every
                    // two seconds creates an aspect/reservation feedback loop and
                    // floods both the signal network and Player.log. Keep respecting
                    // foreign reservations, but stop making local claims until a new
                    // route is selected.
                    reservationSuspendedForRoute = true;
                    nextAttemptTime = 0f;
                    SetOutcome("DV Signals released player route block " + releasedSignalName
                        + "; local reservation suspended for this route.");
                    return;
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

        private static void YieldToADS(Route route)
        {
            if (reservedSignal != null && ownsReservation && HasReservation(reservedSignal))
            {
                int id = GetSignalId(reservedSignal);
                if (id < 0 || !ADSCompatibility.OwnsSignalAuthority(route, id))
                    ClearCurrentReservation();
                else
                    ResetReservationState();
            }
            else
            {
                ResetReservationState();
            }

            reservedRoute = route;
            nextAttemptTime = 0f;
            SetOutcome("ADS owns the matching route's DV Signals reservation; local reservation suspended.");
        }

        public static void Clear()
        {
            ClearCurrentReservation();
            reservedRoute = null;
            reservationSuspendedForRoute = false;
            nextAttemptTime = 0f;
        }

        internal static bool OwnsSignal(object signal)
        {
            return signal != null
                && ownsReservation
                && reservedSignal != null
                && ReferenceEquals(reservedSignal, signal);
        }

        internal static bool IsADSAuthoritySignal(object signal)
        {
            if (signal == null || !EnsureApi())
                return false;

            int id = GetSignalId(signal);
            return id >= 0
                && ADSCompatibility.OwnsSignalAuthority(Module.ActiveRoute?.Route, id);
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
            if (reservationSuspendedForRoute || Time.time < nextAttemptTime
                || reservationRequestPending || clearRequestPending)
                return;

            ReservationCandidate candidate;
            try
            {
                // A foreign reservation may have prevented one or more route
                // switches from being aligned when the route was first created.
                // Recheck before requesting the next block; protected switches
                // remain untouched until their reservation is released.
                route.AdjustSwitchesForSignalReservation(startPathIndex);
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

            if (IsMultiplayerActive())
            {
                if (multiplayerReserve == null || multiplayerReserveResult == null)
                {
                    nextAttemptTime = Time.time + RetryInterval;
                    SetOutcome("DV Signals multiplayer reservation API is unavailable; waiting without changing local signal state.");
                    return;
                }

                pendingReservationSignal = candidate.Signal;
                pendingReservationRoute = route;
                pendingReservationLastPathIndex = candidate.LastPathIndex;
                reservationRequestPending = true;
                AddMultiplayerHandler(multiplayerReserveResult, ReservationResultHandler);
                try
                {
                    // Zero requests an indefinite reservation. Route Manager
                    // explicitly releases it after the complete train clears
                    // the protected block.
                    multiplayerReserve.Invoke(null, new object[] { candidate.Signal, 0f });
                    SetOutcome("Requested DV Signals player route block "
                        + candidate.SignalName + " from the multiplayer host.");
                }
                catch (Exception exception)
                {
                    RemoveMultiplayerHandler(multiplayerReserveResult, ReservationResultHandler);
                    ResetPendingReservation();
                    LogApiError(exception);
                }
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
                    if (IsMultiplayerActive())
                    {
                        RequestMultiplayerClear(reservedSignal);
                    }
                    else
                    {
                        clearFromSignal.Invoke(null, new[] { reservedSignal });
                        SetOutcome("Released DV Signals player route block at "
                            + GetSignalName(reservedSignal) + ".");
                    }
                }
                catch (Exception exception)
                {
                    LogApiError(exception);
                }
            }

            ResetReservationState();
        }

        private static void RequestMultiplayerClear(object signal)
        {
            if (signal == null || clearRequestPending)
                return;
            if (multiplayerCancel == null || multiplayerClearResult == null)
            {
                SetOutcome("DV Signals multiplayer release API is unavailable; the host remains authoritative.");
                return;
            }

            pendingClearSignal = signal;
            clearRequestPending = true;
            AddMultiplayerHandler(multiplayerClearResult, ClearResultHandler);
            try
            {
                multiplayerCancel.Invoke(null, new[] { signal });
                SetOutcome("Requested release of DV Signals player route block at "
                    + GetSignalName(signal) + " from the multiplayer host.");
            }
            catch
            {
                RemoveMultiplayerHandler(multiplayerClearResult, ClearResultHandler);
                pendingClearSignal = null;
                clearRequestPending = false;
                throw;
            }
        }

        private static void OnMultiplayerReservationResult(int id, bool result)
        {
            if (!reservationRequestPending || GetSignalId(pendingReservationSignal) != id)
                return;

            RemoveMultiplayerHandler(multiplayerReserveResult, ReservationResultHandler);
            object signal = pendingReservationSignal;
            Route route = pendingReservationRoute;
            int lastPathIndex = pendingReservationLastPathIndex;
            ResetPendingReservation();

            if (!result)
            {
                nextAttemptTime = Time.time + RetryInterval;
                SetOutcome("DV Signals multiplayer host rejected player route block "
                    + GetSignalName(signal) + ".");
                return;
            }

            if (route == null || !ReferenceEquals(route, reservedRoute)
                || Module.ActiveRoute?.Route != route)
            {
                RequestMultiplayerClear(signal);
                return;
            }

            if (ADSCompatibility.OwnsSignalAuthority(route, id))
            {
                ResetReservationState();
                SetOutcome("ADS accepted the matching DV Signals reservation; local ownership detached.");
                return;
            }

            if (ADSCompatibility.OwnsSignalAuthority(route, null))
            {
                // ADS owns this path through a different native signal head.
                // Do not retain a second overlapping reservation requested
                // before ADS's route transaction completed.
                RequestMultiplayerClear(signal);
                SetOutcome("ADS accepted the matching route through another signal; requested release of the redundant DV Signals block.");
                return;
            }

            reservedSignal = signal;
            reservedLastPathIndex = lastPathIndex;
            ownsReservation = true;
            nextAttemptTime = 0f;
            SetOutcome("Reserved DV Signals player route block at " + GetSignalName(signal)
                + " through the multiplayer host.");
        }

        private static void OnMultiplayerClearResult(int id)
        {
            if (!clearRequestPending || GetSignalId(pendingClearSignal) != id)
                return;

            RemoveMultiplayerHandler(multiplayerClearResult, ClearResultHandler);
            string name = GetSignalName(pendingClearSignal);
            pendingClearSignal = null;
            clearRequestPending = false;
            nextAttemptTime = 0f;
            SetOutcome("Released DV Signals player route block at " + name
                + " through the multiplayer host.");
        }

        private static void ResetPendingReservation()
        {
            pendingReservationSignal = null;
            pendingReservationRoute = null;
            pendingReservationLastPathIndex = -1;
            reservationRequestPending = false;
        }

        private static bool IsMultiplayerActive()
        {
            if (multiplayerActive == null)
                return false;
            try
            {
                return Convert.ToBoolean(multiplayerActive.GetValue(null, null));
            }
            catch
            {
                return false;
            }
        }

        private static void AddMultiplayerHandler(FieldInfo field, Delegate handler)
        {
            if (field == null || handler == null)
                return;
            Delegate current = field.GetValue(null) as Delegate;
            field.SetValue(null, Delegate.Combine(current, handler));
        }

        private static void RemoveMultiplayerHandler(FieldInfo field, Delegate handler)
        {
            if (field == null || handler == null)
                return;
            Delegate current = field.GetValue(null) as Delegate;
            field.SetValue(null, Delegate.Remove(current, handler));
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

        private static int GetSignalId(object signal)
        {
            if (signal == null || signalId == null)
                return -1;
            try
            {
                return Convert.ToInt32(signalId.GetValue(signal));
            }
            catch
            {
                return -1;
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
            Type multiplayerType = FindType("Signals.Game.MultiplayerIntegration");

            signalManagerInstance = signalManagerType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            allControllers = signalManagerType?.GetProperty("AllControllers", BindingFlags.Public | BindingFlags.Instance);
            signalBlock = signalType?.GetProperty("Block", BindingFlags.Public | BindingFlags.Instance);
            signalParent = signalType?.GetProperty("Parent", BindingFlags.Public | BindingFlags.Instance);
            signalAllowReserving = signalType?.GetField("AllowReserving", BindingFlags.Public | BindingFlags.Instance);
            signalName = signalType?.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            signalId = signalType?.GetField("Id", BindingFlags.Public | BindingFlags.Instance);

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

            if (multiplayerType != null && signalType != null)
            {
                multiplayerActive = multiplayerType.GetProperty("IsMpActive",
                    BindingFlags.Public | BindingFlags.Static);
                multiplayerReserve = multiplayerType.GetMethod("SendReservationRequest",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { signalType, typeof(float) }, null);
                multiplayerCancel = multiplayerType.GetMethod("SendReservationCancelRequest",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { signalType }, null);
                multiplayerReserveResult = multiplayerType.GetField(
                    "OnReservationRequestResultReceived", BindingFlags.Public | BindingFlags.Static);
                multiplayerClearResult = multiplayerType.GetField(
                    "OnReservationClearResultReceived", BindingFlags.Public | BindingFlags.Static);
            }

            if (reserveForSignal == null || clearFromSignal == null || hasReservation == null
                || signalManagerInstance == null || allControllers == null || allSignals == null
                || signalBlock == null || signalParent == null || signalAllowReserving == null
                || signalId == null
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
