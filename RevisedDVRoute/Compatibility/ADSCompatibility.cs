using CommandTerminal;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityModManagerNet;

namespace RevisedDVRoute.Compatibility
{
    /// <summary>
    /// Reads ADS route warnings and the dispatcher's live occupancy,
    /// reservation, and signal snapshots. This integration never sends
    /// commands to ADS.
    /// </summary>
    internal static class ADSCompatibility
    {
        private const string ADSModId = "AdvancedDispatcherSystem";
        private const BindingFlags InstanceMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags StaticMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static FieldInfo runtimeField;
        private static PropertyInfo runtimeStatusProperty;
        private static MethodInfo routeStatesMethod;
        private static MethodInfo trackIdMethod;
        private static FieldInfo statePlanField;
        private static FieldInfo stateIdField;
        private static FieldInfo stateLifecycleField;
        private static FieldInfo stateReservationField;
        private static FieldInfo stateSignalsField;
        private static FieldInfo stateReasonField;
        private static FieldInfo stateTimeField;
        private static FieldInfo planNameField;
        private static FieldInfo planTrainCarsField;
        private static FieldInfo planTracksField;
        private static FieldInfo planSwitchesField;
        private static FieldInfo planReservedSignalsField;
        private static FieldInfo planWarningsField;
        private static FieldInfo planConflictsField;
        private static FieldInfo planReasonField;
        private static FieldInfo planInvalidationReasonField;
        private static FieldInfo planFromField;
        private static FieldInfo planToField;
        private static FieldInfo planActiveFromField;
        private static FieldInfo planActiveToField;
        private static FieldInfo conflictResolvedAtField;
        private static FieldInfo conflictCodeField;
        private static FieldInfo conflictKindField;
        private static FieldInfo conflictTargetField;
        private static FieldInfo conflictTrackField;
        private static FieldInfo conflictTrainField;
        private static FieldInfo dispatcherOccupancyStatesField;
        private static FieldInfo dispatcherSignalStatesField;
        private static FieldInfo dispatcherBlockStatesField;
        private static FieldInfo dispatcherRouteLocksField;
        private static FieldInfo dispatcherJunctionIdsField;
        private static MethodInfo routeLocksTrackOwnerMethod;
        private static MethodInfo routeLocksSwitchOwnerMethod;
        private static MethodInfo routeLocksAllowsSwitchMethod;
        private static FieldInfo occupancyIdField;
        private static FieldInfo occupancyOccupiedField;
        private static FieldInfo occupancyCarsKnownField;
        private static FieldInfo occupancyCarsField;
        private static FieldInfo signalReservedField;
        private static FieldInfo signalStopField;
        private static FieldInfo signalAuthorityTrackField;
        private static FieldInfo signalTrackField;
        private static FieldInfo blockReservedField;
        private static FieldInfo blockTracksField;
        private static FieldInfo blockExtraTracksField;
        private static FieldInfo routeStepIdField;
        private static FieldInfo routeStepBranchField;
        private static MethodInfo protectedSwitchAllowedMethod;
        private static bool apiResolved;
        private static bool signalsDelegationLogged;
        private static bool localSignalAuthorityLogged;
        private static Route lastLiveSafetyRoute;
        private static long lastLiveSafetyRefresh;
        private const long AuthorityCacheMilliseconds = 500;
        private static Route cachedAuthorityRoute;
        private static long cachedAuthorityTime;
        private static bool cachedAuthorityResult;
        private static Route cachedSignalAuthorityRoute;
        private static int cachedSignalAuthorityId = -1;
        private static long cachedSignalAuthorityTime;
        private static bool cachedSignalAuthorityResult;
        private static string lastLoggedError;
        private static string lastLoggedSwitchError;
        private static readonly HashSet<int> LoggedBlockedJunctions = new HashSet<int>();

        private static string statusDescription
            = "Read-only route safety check ready; no route checked yet.";
        internal static string StatusDescription
        {
            get
            {
                UnityModManager.ModEntry adsMod = FindADSMod();
                if (adsMod == null)
                    return "read-only integration inactive; ADS is not installed";
                if (!adsMod.Active)
                    return "read-only integration inactive while ADS is disabled";
                return statusDescription;
            }
            private set { statusDescription = value; }
        }

        /// <summary>
        /// ADS owns DV Signals authority only when it has an active, reserved
        /// route for this exact consist and path. Merely loading ADS's signal
        /// adapter does not register a Revised DV Route path with ADS.
        /// </summary>
        internal static bool ManagesDVSignals
        {
            get
            {
                Route route = Module.ActiveRoute?.Route;
                bool delegated = OwnsSignalAuthority(route, null);
                if (delegated && !signalsDelegationLogged && Module.mod != null)
                {
                    Module.mod.Logger.Log(
                        "ADS has a matching reserved route: DV Signals authority delegated for this route.");
                    signalsDelegationLogged = true;
                    localSignalAuthorityLogged = false;
                }
                else if (!delegated && signalsDelegationLogged && !localSignalAuthorityLogged
                    && route != null && Module.mod != null)
                {
                    Module.mod.Logger.Log(
                        "ADS no longer owns the active route's signal reservation; Revised DV Route resumed rolling DV Signals authority.");
                    localSignalAuthorityLogged = true;
                    signalsDelegationLogged = false;
                }

                return delegated;
            }
        }
        private static string[] Warnings { get; set; } = new string[0];
        private static string[] BlockingTrackIds { get; set; } = new string[0];
        private static string[] NonReroutableTrackIds { get; set; } = new string[0];
        private static bool HasMatchingRoute { get; set; }
        private static bool HasBlockingRouteReason { get; set; }
        private static string RouteReason { get; set; }
        private static string MatchedRouteId { get; set; }
        internal static string BlockingReason { get; private set; }
        internal static bool LastRouteRejected { get; private set; }

        internal static bool IsTrackBlockedAhead(RailTrack track)
        {
            if (track == null || BlockingTrackIds == null || !EnsureApi()) return false;

            try
            {
                object dispatcher = runtimeField?.GetValue(null);
                string trackId = dispatcher == null || trackIdMethod == null
                    ? null
                    : Convert.ToString(trackIdMethod.Invoke(dispatcher, new object[] { track }));
                return !string.IsNullOrWhiteSpace(trackId)
                    && BlockingTrackIds.Any(id => string.Equals(id, trackId,
                        StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Refreshes the active path's ADS conflict ids at a bounded rate for
        /// locomotive automation. Route planning performs its own immediate
        /// check; this closes the gap when traffic changes after activation.
        /// </summary>
        internal static void RefreshActiveRouteSafety(Route route)
        {
            if (route?.Path == null || route.Path.Count == 0)
                return;

            long now = UtcNowMilliseconds();
            if (ReferenceEquals(lastLiveSafetyRoute, route)
                && now - lastLiveSafetyRefresh >= 0
                && now - lastLiveSafetyRefresh < 500)
                return;

            lastLiveSafetyRoute = route;
            lastLiveSafetyRefresh = now;
            UnityModManager.ModEntry adsMod = FindADSMod();
            if (adsMod == null || !adsMod.Active || !EnsureApi())
                return;

            List<RailTrack> path = route.Path.Where(track => track != null).ToList();
            RefreshStatus(route.Trainset, path);
            if (!apiResolved || runtimeField == null || trackIdMethod == null)
                return;

            try
            {
                object dispatcher = runtimeField.GetValue(null);
                if (dispatcher == null)
                    return;

                var pathIds = path.Select(track => Convert.ToString(
                        trackIdMethod.Invoke(dispatcher, new object[] { track })))
                    .ToList();
                var blocked = new HashSet<string>(BlockingTrackIds ?? new string[0],
                    StringComparer.OrdinalIgnoreCase);
                var nonReroutable = new HashSet<string>(
                    NonReroutableTrackIds ?? new string[0], StringComparer.OrdinalIgnoreCase);
                int occupied;
                int reserved;
                int signals;
                AddLiveBlockingTrackIds(dispatcher, pathIds,
                    GetTrainCarIds(route.Trainset), MatchedRouteId, !HasMatchingRoute,
                    blocked, nonReroutable, out occupied, out reserved, out signals);
                BlockingTrackIds = blocked.ToArray();
                NonReroutableTrackIds = nonReroutable.ToArray();
            }
            catch (Exception exception)
            {
                string message = exception.GetBaseException().Message;
                if (lastLoggedError != message)
                {
                    lastLoggedError = message;
                    Module.mod?.Logger.Log("ADS live route-safety refresh failed: " + message);
                }
            }
        }

        /// <summary>
        /// True when the last rejection was based only on signal state or a
        /// route-level ADS reason, not on real occupancy or a route lock.
        /// Such a conflict is transient, so a drastically longer detour is
        /// not an acceptable way to avoid it.
        /// </summary>
        internal static bool LastBlockIsSoft { get; private set; }

        /// <summary>
        /// True when ADS only exposes live occupancy or signal pressure on the
        /// route, without a matching ADS route or route-lock conflict. Revised
        /// DV Route still tries one alternate search, but keeps the direct
        /// route when no different path exists because ADS treats that state as
        /// advisory rather than a hard route-registration failure.
        /// </summary>
        internal static bool LastDirectRouteFallbackAllowed { get; private set; }
        private static bool LastRouteCheckPerformed { get; set; }
        private static string LastRouteCheckNotice { get; set; }

        private const long RecentFailedRouteWindowMilliseconds = 5L * 60L * 1000L;

        private static readonly HashSet<string> BlockingConflictCodes = new HashSet<string>(
            new[]
            {
                "TRACK_OCCUPIED",
                "PROTECTION_OCCUPIED",
                "ROUTE_OBSTRUCTED",
                "OPPOSING_TRAIN",
                "BLOCK_OCCUPIED",
                "SIGNAL_RESERVED",
                "SIGNAL_STOP",
                "SIGNAL_OFF",
                "ROUTE_OVERLAP",
                "SWITCH_PLAN_CONFLICT",
                "OCCUPANCY_UNKNOWN"
            },
            StringComparer.OrdinalIgnoreCase);

        internal static void BeginRouteAttempt()
        {
            InvalidateAuthorityCache();
            LastRouteRejected = false;
            LastBlockIsSoft = false;
            LastDirectRouteFallbackAllowed = false;
            LastRouteCheckPerformed = false;
            LastRouteCheckNotice = null;
        }

        /// <summary>
        /// Returns true only when ADS has accepted signal ownership for this
        /// exact consist and route. An optional signal id further verifies that
        /// ADS owns the same native DV Signals head before local bookkeeping is
        /// detached from it.
        /// </summary>
        internal static bool OwnsSignalAuthority(Route route, int? signalId)
        {
            long now = UtcNowMilliseconds();
            if (!signalId.HasValue)
            {
                if (ReferenceEquals(cachedAuthorityRoute, route)
                    && now - cachedAuthorityTime >= 0
                    && now - cachedAuthorityTime < AuthorityCacheMilliseconds)
                    return cachedAuthorityResult;
            }
            else if (ReferenceEquals(cachedSignalAuthorityRoute, route)
                && cachedSignalAuthorityId == signalId.Value
                && now - cachedSignalAuthorityTime >= 0
                && now - cachedSignalAuthorityTime < AuthorityCacheMilliseconds)
            {
                return cachedSignalAuthorityResult;
            }

            bool result = EvaluateSignalAuthority(route, signalId);
            if (!signalId.HasValue)
            {
                cachedAuthorityRoute = route;
                cachedAuthorityTime = now;
                cachedAuthorityResult = result;
            }
            else
            {
                cachedSignalAuthorityRoute = route;
                cachedSignalAuthorityId = signalId.Value;
                cachedSignalAuthorityTime = now;
                cachedSignalAuthorityResult = result;
            }

            return result;
        }

        private static bool EvaluateSignalAuthority(Route route, int? signalId)
        {
            UnityModManager.ModEntry adsMod = FindADSMod();
            if (adsMod == null || !adsMod.Active || route?.Path == null
                || route.Path.Count == 0 || route.Trainset == null || !EnsureApi()
                || stateReservationField == null || planTracksField == null)
                return false;

            try
            {
                object dispatcher = runtimeField.GetValue(null);
                if (dispatcher == null || trackIdMethod == null || routeStatesMethod == null)
                    return false;

                string[] localPath = route.Path.Where(track => track != null)
                    .Select(track => Convert.ToString(trackIdMethod.Invoke(dispatcher,
                        new object[] { track })))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToArray();
                if (localPath.Length != route.Path.Count)
                    return false;

                var localCars = new HashSet<string>(GetTrainCarIds(route.Trainset),
                    StringComparer.OrdinalIgnoreCase);
                if (localCars.Count == 0)
                    return false;

                IEnumerable states = routeStatesMethod.Invoke(dispatcher, null) as IEnumerable;
                if (states == null)
                    return false;

                foreach (object state in states)
                {
                    if (state == null
                        || !string.Equals(ReadFieldString(stateLifecycleField, state), "active",
                            StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(ReadFieldString(stateReservationField, state), "reserved",
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    object plan = statePlanField?.GetValue(state);
                    if (plan == null || !FieldsEqualSet(planTrainCarsField, plan, localCars))
                        continue;

                    string[] adsPath = ReadStringArray(planTracksField, plan);
                    if (!IsContiguousRouteSuffix(adsPath, localPath))
                        continue;

                    string[] signals = ReadStringArray(stateSignalsField, state);
                    if (signals.Length == 0)
                        signals = ReadStringArray(planReservedSignalsField, plan);
                    if (signals.Length == 0)
                        continue;

                    if (!signalId.HasValue || signals.Any(id =>
                        string.Equals(id, "s" + signalId.Value, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(id, signalId.Value.ToString(), StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }
            catch (Exception exception)
            {
                string message = exception.GetBaseException().Message;
                if (lastLoggedError != message)
                {
                    lastLoggedError = message;
                    Module.mod?.Logger.Log("ADS signal-authority check failed open: " + message);
                }
            }

            return false;
        }

        private static void InvalidateAuthorityCache()
        {
            cachedAuthorityRoute = null;
            cachedAuthorityTime = 0;
            cachedAuthorityResult = false;
            cachedSignalAuthorityRoute = null;
            cachedSignalAuthorityId = -1;
            cachedSignalAuthorityTime = 0;
            cachedSignalAuthorityResult = false;
        }

        private static bool FieldsEqualSet(FieldInfo field, object owner,
            HashSet<string> expected)
        {
            string[] values = ReadStringArray(field, owner);
            return values.Length == expected.Count && expected.SetEquals(values);
        }

        private static string[] ReadStringArray(FieldInfo field, object owner)
        {
            IEnumerable values = field?.GetValue(owner) as IEnumerable;
            if (values == null)
                return new string[0];

            var result = new List<string>();
            foreach (object value in values)
            {
                string text = Convert.ToString(value);
                if (!string.IsNullOrWhiteSpace(text))
                    result.Add(text);
            }
            return result.ToArray();
        }

        private static bool IsContiguousRouteSuffix(IList<string> adsPath,
            IList<string> localPath)
        {
            if (adsPath == null || localPath == null || localPath.Count == 0
                || localPath.Count > adsPath.Count
                || !string.Equals(adsPath[adsPath.Count - 1], localPath[localPath.Count - 1],
                    StringComparison.OrdinalIgnoreCase))
                return false;

            for (int start = 0; start <= adsPath.Count - localPath.Count; start++)
            {
                bool matches = true;
                for (int index = 0; index < localPath.Count; index++)
                {
                    if (string.Equals(adsPath[start + index], localPath[index],
                        StringComparison.OrdinalIgnoreCase))
                        continue;
                    matches = false;
                    break;
                }

                if (matches)
                    return start + localPath.Count == adsPath.Count;
            }

            return false;
        }

        /// <summary>
        /// Adds ADS warning context only to a failed route result. If ADS is
        /// absent, the original failure message is returned unchanged.
        /// </summary>
        public static string AppendFailureNotice(string failureMessage, Trainset trainset)
        {
            UnityModManager.ModEntry adsMod = FindADSMod();
            if (adsMod == null)
                return failureMessage;

            if (!adsMod.Active)
                return failureMessage + "\nADS warning details unavailable: ADS is installed but disabled.";

            if (LastRouteCheckPerformed && !string.IsNullOrWhiteSpace(LastRouteCheckNotice))
                return failureMessage + "\n" + LastRouteCheckNotice;

            RefreshStatus(trainset);
            if (!HasMatchingRoute)
                return failureMessage + "\nADS warning details unavailable: " + Shorten(StatusDescription, 160);

            if (Warnings.Length == 0)
            {
                string noWarningDetails = FormatRouteReason(RouteReason);
                if (string.IsNullOrWhiteSpace(noWarningDetails))
                    noWarningDetails = "ADS reports no warnings for the matching train.";
                return failureMessage + "\n" + noWarningDetails;
            }

            var warningLines = Warnings.Take(3)
                .Select(warning => "ADS warning: " + Shorten(warning, 110))
                .ToList();
            string routeReason = FormatRouteReason(RouteReason);
            if (!string.IsNullOrWhiteSpace(routeReason))
                warningLines.Insert(0, routeReason);
            if (Warnings.Length > 3)
                warningLines.Add("ADS reports " + (Warnings.Length - 3) + " more warning(s).");

            return failureMessage + "\n" + string.Join("\n", warningLines.ToArray());
        }

        /// <summary>
        /// Appends ADS status to a successful route result so the radio makes
        /// clear whether a matching ADS route was found and whether ADS
        /// reported a warning. This never affects route calculation.
        /// </summary>
        public static string AppendRouteWarningNotice(string routeResult, Trainset trainset)
        {
            UnityModManager.ModEntry adsMod = FindADSMod();
            if (adsMod == null)
                return routeResult;

            if (!adsMod.Active)
                return routeResult + "\nADS status unavailable: ADS is installed but disabled.";

            // TryGetBlockingTracks performs the route-specific ADS check before
            // the route is activated. Preserve that result; refreshing here
            // without the attempted path used to replace it with the confusing
            // "warning status unavailable" message.
            if (LastRouteCheckPerformed && !string.IsNullOrWhiteSpace(LastRouteCheckNotice))
                return routeResult + "\n" + LastRouteCheckNotice;

            RefreshStatus(trainset);
            if (!HasMatchingRoute)
                return routeResult + (apiResolved
                    ? "\nADS connected; no ADS route plan exists to report warnings for."
                    : "\nADS check unavailable: " + Shorten(StatusDescription, 150));

            var warningLines = new List<string>();
            string routeReason = FormatKnownRouteWarningReason(RouteReason);
            if (!string.IsNullOrWhiteSpace(routeReason))
                warningLines.Add(routeReason);

            foreach (string warning in Warnings.Take(3))
                warningLines.Add("ADS warning: " + Shorten(warning, 110));

            if (warningLines.Count == 0)
                return routeResult + "\nADS reports no active warnings for the matching train.";

            if (Warnings.Length > 3)
                warningLines.Add("ADS reports " + (Warnings.Length - 3) + " more warning(s).");

            return routeResult + "\n" + string.Join("\n", warningLines.ToArray());
        }

        /// <summary>
        /// Maps unresolved blocking conflicts on the matching ADS route back to
        /// the physical rails used by the attempted Revised DV Route path. ADS
        /// owns session-local track IDs, so its public TrackId method is the
        /// authoritative bridge between the two route representations.
        /// </summary>
        internal static bool TryGetBlockingTracks(Trainset trainset,
            IEnumerable<RailTrack> attemptedPath, out HashSet<RailTrack> blockingTracks)
        {
            blockingTracks = new HashSet<RailTrack>();
            UnityModManager.ModEntry adsMod = FindADSMod();
            if (adsMod == null || !adsMod.Active || attemptedPath == null)
                return false;

            List<RailTrack> path = attemptedPath.Where(track => track != null).ToList();
            RefreshStatus(trainset, path);
            if (!apiResolved || runtimeField == null)
            {
                LastRouteCheckPerformed = true;
                LastRouteCheckNotice = "ADS check unavailable: " + Shorten(StatusDescription, 140);
                return false;
            }

            try
            {
                object dispatcher = runtimeField.GetValue(null);
                if (dispatcher == null || trackIdMethod == null)
                {
                    LastRouteCheckPerformed = true;
                    LastRouteCheckNotice = "ADS check unavailable: waiting for the ADS game dispatcher.";
                    return false;
                }

                var blockedIds = new HashSet<string>(BlockingTrackIds,
                    StringComparer.OrdinalIgnoreCase);
                var nonReroutableIds = new HashSet<string>(NonReroutableTrackIds,
                    StringComparer.OrdinalIgnoreCase);
                var pathIds = new List<string>(path.Count);
                foreach (RailTrack track in path)
                {
                    pathIds.Add(Convert.ToString(trackIdMethod.Invoke(dispatcher,
                        new object[] { track })));
                }

                int occupiedBlockers;
                int reservedBlockers;
                int signalBlockers;
                bool liveStateAvailable = AddLiveBlockingTrackIds(dispatcher, pathIds,
                    GetTrainCarIds(trainset), MatchedRouteId, !HasMatchingRoute,
                    blockedIds, nonReroutableIds, out occupiedBlockers,
                    out reservedBlockers, out signalBlockers);

                // ADS can also record a signal conflict on the origin or
                // destination from its own plan. Those rails are on every
                // possible path, so a signal-only conflict there cannot be
                // routed around and must not drive a proxy exclusion.
                blockedIds.RemoveWhere(id => IsEndpointOnly(pathIds, id)
                    && !nonReroutableIds.Contains(id));

                bool recordedBlockingTrackOnAttemptedPath = pathIds.Any(id =>
                    !string.IsNullOrWhiteSpace(id)
                    && !IsEndpointOnly(pathIds, id)
                    && BlockingTrackIds.Contains(id, StringComparer.OrdinalIgnoreCase));
                bool blockingTrackOnAttemptedPath = pathIds.Any(id =>
                    !string.IsNullOrWhiteSpace(id) && blockedIds.Contains(id));
                bool routeLevelBlockerWithoutTrack = HasBlockingRouteReason
                    && BlockingTrackIds.Length == 0;
                bool advisoryLiveConflictWithoutMatchingRoute = !HasMatchingRoute
                    && !HasBlockingRouteReason
                    && reservedBlockers == 0
                    && (occupiedBlockers > 0 || signalBlockers > 0);
                LastDirectRouteFallbackAllowed = advisoryLiveConflictWithoutMatchingRoute;
                LastRouteRejected = !advisoryLiveConflictWithoutMatchingRoute
                    && (blockingTrackOnAttemptedPath || routeLevelBlockerWithoutTrack);
                LastBlockIsSoft = LastRouteRejected
                    && occupiedBlockers + reservedBlockers == 0
                    && !recordedBlockingTrackOnAttemptedPath;
                LastRouteCheckPerformed = true;

                if (!LastRouteRejected)
                {
                    if (HasMatchingRoute && Warnings.Length > 0)
                    {
                        LastRouteCheckNotice = "ADS check: " + Warnings.Length
                            + (Warnings.Length == 1 ? " non-blocking warning; " : " non-blocking warnings; ")
                            + Shorten(Warnings[0], 105);
                    }
                    else if (liveStateAvailable)
                    {
                        LastRouteCheckNotice = "ADS CHECK: connected; no blocking reservation, "
                            + "occupancy, or signal conflict found on this route.";
                    }
                    else
                    {
                        LastRouteCheckNotice = "ADS connected, but this ADS version does not expose "
                            + "live route-safety state.";
                    }

                    StatusDescription = LastRouteCheckNotice;
                    Module.mod?.Logger.Log("ADS route check passed: " + LastRouteCheckNotice);
                    return false;
                }

                BlockingTrackIds = blockedIds.ToArray();
                NonReroutableTrackIds = nonReroutableIds.ToArray();
                if (string.IsNullOrWhiteSpace(BlockingReason)
                    || (!recordedBlockingTrackOnAttemptedPath
                        && occupiedBlockers + reservedBlockers + signalBlockers > 0))
                {
                    BlockingReason = signalBlockers > 0 ? "SIGNAL_RESERVED"
                        : occupiedBlockers > 0 ? "TRACK_OCCUPIED"
                        : reservedBlockers > 0 ? "ROUTE_OVERLAP"
                        : RouteReason;
                }

                string knownWarning = FormatKnownRouteWarningReason(BlockingReason);
                LastRouteCheckNotice = !string.IsNullOrWhiteSpace(knownWarning)
                    ? knownWarning
                    : "ADS WARNING: the original route has a blocking conflict.";

                if (occupiedBlockers + reservedBlockers + signalBlockers > 0)
                {
                    LastRouteCheckNotice += " Live ADS check: "
                        + occupiedBlockers + " occupied, "
                        + reservedBlockers + " route-reserved, "
                        + signalBlockers + " signal-protected track(s).";
                }

                bool blockedStart = false;
                bool blockedDestination = false;
                bool blockedEndpointCannotBeRerouted = false;

                for (int index = 0; index < path.Count; index++)
                {
                    RailTrack track = path[index];
                    string adsTrackId = pathIds[index];
                    if (string.IsNullOrWhiteSpace(adsTrackId) || !blockedIds.Contains(adsTrackId))
                        continue;

                    if (index == 0)
                    {
                        if (nonReroutableIds.Contains(adsTrackId))
                            blockedEndpointCannotBeRerouted = true;
                        else
                            blockedStart = true;
                    }
                    else if (index == path.Count - 1)
                    {
                        if (nonReroutableIds.Contains(adsTrackId))
                            blockedEndpointCannotBeRerouted = true;
                        else
                            blockedDestination = true;
                    }
                    else
                        blockingTracks.Add(track);
                }

                // Occupancy and route ownership on the requested origin or
                // destination remain present on every possible path. A proxy
                // exclusion would only make the geometry look different while
                // leaving the actual ADS conflict in place.
                if (blockedEndpointCannotBeRerouted)
                {
                    blockingTracks.Clear();
                    LastRouteCheckNotice += " The blocked route endpoint cannot be bypassed.";
                    if (LastDirectRouteFallbackAllowed)
                    {
                        LastRouteCheckNotice += " Direct route kept because ADS reported only live occupancy/signal pressure without a matching ADS route.";
                        StatusDescription = LastRouteCheckNotice;
                        Module.mod?.Logger.Log("ADS reported an advisory live endpoint conflict without a matching ADS route; keeping the direct route.");
                        return false;
                    }

                    StatusDescription = LastRouteCheckNotice;
                    Module.mod?.Logger.Log("ADS rejected the route because its origin or destination "
                        + "has a non-reroutable occupancy or route-lock conflict.");
                    return false;
                }

                // The requested endpoints themselves cannot be removed from a
                // route. If ADS reports one as blocked, exclude the first or
                // last intermediate rail instead. This forces A* to choose a
                // genuinely different departure or approach rather than
                // silently returning the same path.
                if (blockedStart)
                    AddEndpointProxyExclusion(path, blockingTracks, true);
                if (blockedDestination)
                    AddEndpointProxyExclusion(path, blockingTracks, false);

                // Route-level failures such as RESERVATION_LOST can arrive
                // without a concrete conflict track. Force a different first
                // leg so the warning still results in one alternate search.
                if (blockingTracks.Count == 0 && blockedIds.Count == 0 && HasBlockingRouteReason)
                    AddEndpointProxyExclusion(path, blockingTracks, true);

                if (blockingTracks.Count == 0)
                {
                    Module.mod?.Logger.Log("ADS reported a blocking warning, but no avoidable rail "
                        + "on the calculated route could be excluded.");
                }
                else
                {
                    StatusDescription = LastRouteCheckNotice;
                    Module.mod?.Logger.Log("ADS rejected the original path: " + LastRouteCheckNotice);
                }

                return blockingTracks.Count > 0;
            }
            catch (Exception exception)
            {
                Exception cause = exception.GetBaseException();
                Module.mod?.Logger.Log("ADS conflict-track mapping failed: " + cause.Message);
                LastRouteCheckPerformed = true;
                LastRouteCheckNotice = "ADS check unavailable: " + Shorten(cause.Message, 120);
                LastRouteRejected = false;
                blockingTracks.Clear();
                return false;
            }
        }

        /// <summary>
        /// Checks ADS's current protected switch claims without asking ADS to
        /// set or release anything. Normal (non-protected) ADS routes retain
        /// ADS's own permissive switch semantics.
        /// </summary>
        internal static bool CanSwitchJunction(Junction junction, int branch,
            out string routeOwner)
        {
            routeOwner = null;
            if (junction == null)
                return true;

            UnityModManager.ModEntry adsMod = FindADSMod();
            if (adsMod == null || !adsMod.Active)
            {
                LoggedBlockedJunctions.Remove(junction.GetInstanceID());
                return true;
            }

            if (!EnsureApi())
                return true;

            try
            {
                object dispatcher = runtimeField.GetValue(null);
                if (dispatcher == null)
                    return true;

                bool protectedAllowed = protectedSwitchAllowedMethod == null
                    || Convert.ToBoolean(protectedSwitchAllowedMethod.Invoke(null,
                        new object[] { junction, branch }));

                object routeLocks = dispatcherRouteLocksField?.GetValue(dispatcher);
                IDictionary junctionIds = dispatcherJunctionIdsField?.GetValue(dispatcher) as IDictionary;
                if (routeLocks == null || junctionIds == null
                    || routeLocksAllowsSwitchMethod == null || !junctionIds.Contains(junction))
                    return protectedAllowed;

                string junctionId = Convert.ToString(junctionIds[junction]);
                if (string.IsNullOrWhiteSpace(junctionId))
                    return true;

                bool allowed = protectedAllowed && Convert.ToBoolean(
                    routeLocksAllowsSwitchMethod.Invoke(routeLocks,
                        new object[] { junctionId, branch }));
                if (routeLocksSwitchOwnerMethod != null)
                {
                    routeOwner = Convert.ToString(routeLocksSwitchOwnerMethod.Invoke(routeLocks,
                        new object[] { junctionId }));

                    // ADS intentionally permits manual changes through normal
                    // reservations, then invalidates the route as misaligned.
                    // Automatic Route Manager alignment should be more
                    // cooperative: preserve any active ADS route that requires
                    // a different branch, regardless of reservation mode.
                    int requiredBranch;
                    if (allowed && !string.IsNullOrWhiteSpace(routeOwner)
                        && TryGetADSRouteSwitchBranch(dispatcher, routeOwner,
                            junctionId, out requiredBranch)
                        && requiredBranch != branch)
                        allowed = false;
                }

                if (allowed)
                {
                    LoggedBlockedJunctions.Remove(junction.GetInstanceID());
                    routeOwner = null;
                }

                lastLoggedSwitchError = null;
                return allowed;
            }
            catch (Exception exception)
            {
                string message = exception.GetBaseException().Message;
                if (lastLoggedSwitchError != message)
                {
                    lastLoggedSwitchError = message;
                    Module.mod?.Logger.Log("ADS switch-lock check failed open: " + message);
                }
                return true;
            }
        }

        private static bool TryGetADSRouteSwitchBranch(object dispatcher,
            string routeId, string junctionId, out int branch)
        {
            branch = -1;
            if (dispatcher == null || string.IsNullOrWhiteSpace(routeId)
                || string.IsNullOrWhiteSpace(junctionId) || routeStatesMethod == null
                || planSwitchesField == null || routeStepIdField == null
                || routeStepBranchField == null)
                return false;

            IEnumerable states = routeStatesMethod.Invoke(dispatcher, null) as IEnumerable;
            if (states == null)
                return false;

            foreach (object state in states)
            {
                if (state == null
                    || !string.Equals(ReadFieldString(stateIdField, state), routeId,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(ReadFieldString(stateLifecycleField, state), "active",
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                object plan = statePlanField?.GetValue(state);
                if (plan == null)
                    return false;
                IEnumerable steps = planSwitchesField.GetValue(plan) as IEnumerable;
                if (steps == null)
                    return false;

                foreach (object step in steps)
                {
                    if (step == null || !string.Equals(
                        Convert.ToString(routeStepIdField.GetValue(step)), junctionId,
                        StringComparison.OrdinalIgnoreCase))
                        continue;
                    branch = Convert.ToInt32(routeStepBranchField.GetValue(step));
                    return true;
                }
                return false;
            }

            return false;
        }

        internal static void LogBlockedJunctionSwitch(Junction junction, string routeOwner)
        {
            if (junction != null && !LoggedBlockedJunctions.Add(junction.GetInstanceID()))
                return;

            string junctionName = junction == null ? null : junction.junctionData.junctionIdLong;
            if (string.IsNullOrWhiteSpace(junctionName))
                junctionName = junction?.name ?? "unknown junction";

            string owner = string.IsNullOrWhiteSpace(routeOwner)
                ? "an ADS route"
                : "ADS route " + routeOwner;
            string message = owner + " locks " + junctionName + "; automatic switch skipped.";
            Terminal.Log(message);
            Module.mod?.Logger.Log(message);
        }

        /// <summary>
        /// Checks the path against ADS's current game-side snapshots even when
        /// the player has not created a separate ADS route. This closes the
        /// gap where RouteStates() was empty and the radio incorrectly called
        /// the ADS warning status unavailable.
        /// </summary>
        private static bool AddLiveBlockingTrackIds(object dispatcher,
            IList<string> pathIds, IEnumerable<string> trainCarIds,
            string ignoredRouteOwner, bool includeSignalReservations,
            HashSet<string> blockedIds, HashSet<string> nonReroutableIds,
            out int occupiedBlockers, out int reservedBlockers,
            out int signalBlockers)
        {
            var occupiedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reservedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var signalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pathIdSet = new HashSet<string>(pathIds
                .Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
            var ownCars = new HashSet<string>(trainCarIds ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            bool stateAvailable = false;

            occupiedBlockers = 0;
            reservedBlockers = 0;
            signalBlockers = 0;
            if (pathIdSet.Count == 0)
                return false;

            IDictionary occupancy = dispatcherOccupancyStatesField?.GetValue(dispatcher) as IDictionary;
            if (occupancy != null && occupancyOccupiedField != null)
            {
                stateAvailable = true;
                foreach (object value in occupancy.Values)
                {
                    if (value == null || !Convert.ToBoolean(occupancyOccupiedField.GetValue(value)))
                        continue;

                    string trackId = Convert.ToString(occupancyIdField?.GetValue(value));
                    if (string.IsNullOrWhiteSpace(trackId) || !pathIdSet.Contains(trackId))
                        continue;

                    bool carsKnown = occupancyCarsKnownField != null
                        && Convert.ToBoolean(occupancyCarsKnownField.GetValue(value));
                    IEnumerable cars = occupancyCarsField?.GetValue(value) as IEnumerable;
                    var observedCars = new List<string>();
                    if (cars != null)
                    {
                        foreach (object car in cars)
                        {
                            string carId = Convert.ToString(car);
                            if (!string.IsNullOrWhiteSpace(carId))
                                observedCars.Add(carId);
                        }
                    }

                    // The train necessarily occupies its own origin. Do not
                    // report that as a conflict when ADS identifies every car
                    // on the occupied track as part of this exact consist.
                    bool occupiedOnlyByThisTrain = carsKnown && observedCars.Count > 0
                        && ownCars.Count > 0 && observedCars.All(ownCars.Contains);
                    if (!occupiedOnlyByThisTrain)
                        occupiedIds.Add(trackId);
                }
            }

            object routeLocks = dispatcherRouteLocksField?.GetValue(dispatcher);
            if (routeLocks != null && routeLocksTrackOwnerMethod != null)
            {
                stateAvailable = true;
                foreach (string pathId in pathIdSet)
                {
                    string owner = Convert.ToString(routeLocksTrackOwnerMethod.Invoke(routeLocks,
                        new object[] { pathId }));
                    if (!string.IsNullOrWhiteSpace(owner)
                        && !string.Equals(owner, ignoredRouteOwner,
                            StringComparison.OrdinalIgnoreCase))
                        reservedIds.Add(pathId);
                }
            }

            IDictionary signals = dispatcherSignalStatesField?.GetValue(dispatcher) as IDictionary;
            if (includeSignalReservations && signals != null && signalReservedField != null)
            {
                stateAvailable = true;
                foreach (object value in signals.Values)
                {
                    if (value == null)
                        continue;

                    bool reserved = Convert.ToBoolean(signalReservedField.GetValue(value));
                    bool atStop = signalStopField != null
                        && Convert.ToBoolean(signalStopField.GetValue(value));
                    if (!reserved && !atStop)
                        continue;

                    // A reserved signal protects its authority track. ADS 1.0.1
                    // can also omit authorityTrack for simpler signals, where
                    // the signal's own track is the best available mapping.
                    string authorityTrack = Convert.ToString(signalAuthorityTrackField?.GetValue(value));
                    string signalTrack = Convert.ToString(signalTrackField?.GetValue(value));
                    string protectedTrack = pathIdSet.Contains(authorityTrack)
                        ? authorityTrack
                        : pathIdSet.Contains(signalTrack) ? signalTrack : null;

                    // A signal on the origin (train waiting at its own signal)
                    // or destination track exists on every possible path.
                    // Excluding a neighbouring rail cannot avoid it and only
                    // forces a needlessly long detour, so signal-only
                    // conflicts there are not routing blockers. Occupancy and
                    // route locks on endpoints are still reported above.
                    if (!string.IsNullOrWhiteSpace(protectedTrack)
                        && IsEndpointOnly(pathIds, protectedTrack))
                        continue;

                    if (!string.IsNullOrWhiteSpace(protectedTrack)
                        && (reserved || reservedIds.Contains(protectedTrack)
                            || occupiedIds.Contains(protectedTrack)))
                        signalIds.Add(protectedTrack);
                }
            }

            // ADS exposes each DV Signals block's complete native footprint.
            // Prefer that over the signal mast/authority track so junction
            // flanks and ExtraTracks are not missed by the conflict check.
            IDictionary blocks = dispatcherBlockStatesField?.GetValue(dispatcher) as IDictionary;
            if (includeSignalReservations && blocks != null && blockReservedField != null)
            {
                stateAvailable = true;
                foreach (object value in blocks.Values)
                {
                    if (value == null || !Convert.ToBoolean(blockReservedField.GetValue(value)))
                        continue;

                    foreach (string protectedTrack in ReadStrings(
                        blockTracksField?.GetValue(value), blockExtraTracksField?.GetValue(value)))
                    {
                        if (pathIdSet.Contains(protectedTrack)
                            && !IsEndpointOnly(pathIds, protectedTrack))
                            signalIds.Add(protectedTrack);
                    }
                }
            }

            foreach (string id in occupiedIds)
            {
                blockedIds.Add(id);
                nonReroutableIds.Add(id);
            }
            foreach (string id in reservedIds)
            {
                blockedIds.Add(id);
                nonReroutableIds.Add(id);
            }
            foreach (string id in signalIds)
                blockedIds.Add(id);

            occupiedBlockers = occupiedIds.Count;
            reservedBlockers = reservedIds.Count;
            signalBlockers = signalIds.Count;
            return stateAvailable;
        }

        private static IEnumerable<string> ReadStrings(params object[] sources)
        {
            foreach (object source in sources)
            {
                IEnumerable values = source as IEnumerable;
                if (values == null)
                    continue;
                foreach (object value in values)
                {
                    string text = Convert.ToString(value);
                    if (!string.IsNullOrWhiteSpace(text))
                        yield return text;
                }
            }
        }

        private static bool IsEndpointOnly(IList<string> pathIds, string trackId)
        {
            if (pathIds == null || pathIds.Count == 0 || string.IsNullOrWhiteSpace(trackId))
                return false;

            bool isEndpoint = string.Equals(pathIds[0], trackId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pathIds[pathIds.Count - 1], trackId, StringComparison.OrdinalIgnoreCase);
            if (!isEndpoint)
                return false;

            // A looping route may also pass through the same rail mid-route;
            // that occurrence is still avoidable, so keep it as a blocker.
            for (int index = 1; index < pathIds.Count - 1; index++)
            {
                if (string.Equals(pathIds[index], trackId, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private static bool AddEndpointProxyExclusion(List<RailTrack> path,
            HashSet<RailTrack> blockingTracks, bool fromStart)
        {
            if (path == null || path.Count < 3)
                return false;

            int index = fromStart ? 1 : path.Count - 2;
            int limit = fromStart ? path.Count - 1 : 0;
            int step = fromStart ? 1 : -1;
            RailTrack endpoint = fromStart ? path[0] : path[path.Count - 1];

            while (index != limit)
            {
                RailTrack candidate = path[index];
                if (candidate != null && candidate != endpoint)
                {
                    blockingTracks.Add(candidate);
                    return true;
                }
                index += step;
            }

            return false;
        }

        private static void RefreshStatus(Trainset trainset,
            IEnumerable<RailTrack> attemptedPath = null)
        {
            UnityModManager.ModEntry adsMod = FindADSMod();
            if (adsMod == null)
            {
                SetUnavailable("ADS is not installed.");
                return;
            }

            if (!adsMod.Active)
            {
                SetUnavailable("ADS is installed but disabled.");
                return;
            }

            if (!EnsureApi())
            {
                SetUnavailable("ADS is active; its read-only route status API is unavailable.");
                return;
            }

            try
            {
                object dispatcher = runtimeField.GetValue(null);
                if (dispatcher == null)
                {
                    SetUnavailable("ADS is active; waiting for its game dispatcher.");
                    return;
                }

                string adsStatus = Convert.ToString(runtimeStatusProperty.GetValue(dispatcher, null));
                if (string.IsNullOrWhiteSpace(adsStatus))
                    adsStatus = "running";

                string[] activeTrainCars = GetTrainCarIds(trainset);
                if (activeTrainCars.Length == 0)
                {
                    SetUnavailable("ADS " + adsStatus + "; no trainset is available to match.");
                    return;
                }

                string attemptedFromTrackId = null;
                string attemptedToTrackId = null;
                if (attemptedPath != null)
                {
                    List<RailTrack> path = attemptedPath.Where(track => track != null).ToList();
                    if (path.Count > 0)
                    {
                        attemptedFromTrackId = Convert.ToString(trackIdMethod.Invoke(dispatcher,
                            new object[] { path[0] }));
                        attemptedToTrackId = Convert.ToString(trackIdMethod.Invoke(dispatcher,
                            new object[] { path[path.Count - 1] }));
                    }
                }

                IEnumerable routeStates = routeStatesMethod.Invoke(dispatcher, null) as IEnumerable;
                bool recentFailedRoute;
                object matchedState = FindMatchingRoute(routeStates, activeTrainCars,
                    attemptedFromTrackId, attemptedToTrackId, out recentFailedRoute);
                if (matchedState == null)
                {
                    SetUnavailable("ADS " + adsStatus + "; no current or recent failed ADS route matches "
                        + "the attempted train and destination. Revised DV Route does not publish its path to ADS.");
                    return;
                }

                object plan = statePlanField.GetValue(matchedState);
                if (plan == null)
                {
                    SetUnavailable("ADS " + adsStatus + "; the matching route has no warning snapshot yet.");
                    return;
                }

                string routeName = Convert.ToString(planNameField.GetValue(plan));
                if (string.IsNullOrWhiteSpace(routeName))
                    routeName = "unnamed route";

                string lifecycle = Convert.ToString(stateLifecycleField.GetValue(matchedState));
                List<string> warnings = ReadWarnings(plan);
                string blockingConflictCode;
                List<string> nonReroutableTrackIds;
                List<string> blockingTrackIds = ReadBlockingTrackIds(plan,
                    out blockingConflictCode, out nonReroutableTrackIds);
                string reason = GetRouteReason(matchedState, plan);

                Warnings = warnings.ToArray();
                BlockingTrackIds = blockingTrackIds.ToArray();
                NonReroutableTrackIds = nonReroutableTrackIds.ToArray();
                HasMatchingRoute = true;
                MatchedRouteId = ReadFieldString(stateIdField, matchedState);
                HasBlockingRouteReason = !string.IsNullOrWhiteSpace(blockingConflictCode)
                    || IsBlockingRouteReason(reason);
                RouteReason = reason;
                BlockingReason = !string.IsNullOrWhiteSpace(blockingConflictCode)
                    ? blockingConflictCode
                    : HasBlockingRouteReason ? reason : null;
                StatusDescription = "ADS " + adsStatus + "; matched " + routeName
                    + (string.IsNullOrWhiteSpace(lifecycle) ? string.Empty : " (" + lifecycle + ")")
                    + (recentFailedRoute ? "; using its recent failure" : string.Empty)
                    + "; " + Warnings.Length + (Warnings.Length == 1 ? " warning" : " warnings")
                    + (string.IsNullOrWhiteSpace(reason) ? "." : "; " + reason + ".");
                lastLoggedError = null;
            }
            catch (Exception exception)
            {
                Exception cause = exception.GetBaseException();
                SetUnavailable("ADS read-only status unavailable: " + cause.Message);
                if (lastLoggedError != cause.Message)
                {
                    lastLoggedError = cause.Message;
                    Module.mod?.Logger.Log("ADS read-only warning check failed: " + cause.Message);
                }
            }
        }

        private static bool EnsureApi()
        {
            if (apiResolved)
                return true;

            Type mainType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("AdvancedDispatcherSystem.Game.Main", false))
                .FirstOrDefault(type => type != null);
            if (mainType == null)
                return false;

            runtimeField = mainType.GetField("Runtime", StaticMembers);
            if (runtimeField == null)
                return false;

            Type dispatcherType = runtimeField.FieldType;
            runtimeStatusProperty = dispatcherType.GetProperty("Status", InstanceMembers);
            routeStatesMethod = dispatcherType.GetMethod("RouteStates", InstanceMembers);
            trackIdMethod = dispatcherType.GetMethod("TrackId", InstanceMembers, null,
                new[] { typeof(RailTrack) }, null);
            protectedSwitchAllowedMethod = dispatcherType.GetMethod("ProtectedSwitchAllowed",
                StaticMembers, null, new[] { typeof(Junction), typeof(int) }, null);
            if (runtimeStatusProperty == null || routeStatesMethod == null || trackIdMethod == null)
                return false;

            Type routeStateType = routeStatesMethod.ReturnType.GetElementType();
            statePlanField = routeStateType?.GetField("plan", InstanceMembers);
            stateIdField = routeStateType?.GetField("id", InstanceMembers);
            stateLifecycleField = routeStateType?.GetField("lifecycle", InstanceMembers);
            stateReservationField = routeStateType?.GetField("reservation", InstanceMembers);
            stateSignalsField = routeStateType?.GetField("signals", InstanceMembers);
            stateReasonField = routeStateType?.GetField("reason", InstanceMembers);
            stateTimeField = routeStateType?.GetField("time", InstanceMembers);
            Type planType = statePlanField?.FieldType;
            planNameField = planType?.GetField("name", InstanceMembers);
            planTrainCarsField = planType?.GetField("trainCars", InstanceMembers);
            planTracksField = planType?.GetField("tracks", InstanceMembers);
            planSwitchesField = planType?.GetField("switches", InstanceMembers);
            planReservedSignalsField = planType?.GetField("reservedSignals", InstanceMembers);
            planWarningsField = planType?.GetField("warnings", InstanceMembers);
            planConflictsField = planType?.GetField("conflicts", InstanceMembers);
            planReasonField = planType?.GetField("reason", InstanceMembers);
            planInvalidationReasonField = planType?.GetField("invalidationReason", InstanceMembers);
            planFromField = planType?.GetField("from", InstanceMembers);
            planToField = planType?.GetField("to", InstanceMembers);
            planActiveFromField = planType?.GetField("activeFrom", InstanceMembers);
            planActiveToField = planType?.GetField("activeTo", InstanceMembers);
            Type conflictType = planConflictsField?.FieldType.GetElementType();
            conflictResolvedAtField = conflictType?.GetField("resolvedAt", InstanceMembers);
            conflictCodeField = conflictType?.GetField("code", InstanceMembers);
            conflictKindField = conflictType?.GetField("kind", InstanceMembers);
            conflictTargetField = conflictType?.GetField("target", InstanceMembers);
            conflictTrackField = conflictType?.GetField("track", InstanceMembers);
            conflictTrainField = conflictType?.GetField("train", InstanceMembers);

            // These fields are optional so older ADS releases still retain
            // route-record compatibility. ADS 1.0.1 exposes them and permits a
            // real path safety check before any ADS route record exists.
            dispatcherOccupancyStatesField = dispatcherType.GetField("occupancyStates", InstanceMembers);
            dispatcherSignalStatesField = dispatcherType.GetField("signalStates", InstanceMembers);
            dispatcherBlockStatesField = dispatcherType.GetField("blockStates", InstanceMembers);
            dispatcherRouteLocksField = dispatcherType.GetField("routeLocks", InstanceMembers);
            dispatcherJunctionIdsField = dispatcherType.GetField("junctionIds", InstanceMembers);

            Type occupancyType = DictionaryValueType(dispatcherOccupancyStatesField?.FieldType);
            occupancyIdField = occupancyType?.GetField("id", InstanceMembers);
            occupancyOccupiedField = occupancyType?.GetField("occupied", InstanceMembers);
            occupancyCarsKnownField = occupancyType?.GetField("carsKnown", InstanceMembers);
            occupancyCarsField = occupancyType?.GetField("cars", InstanceMembers);

            Type signalType = DictionaryValueType(dispatcherSignalStatesField?.FieldType);
            signalReservedField = signalType?.GetField("reserved", InstanceMembers);
            signalStopField = signalType?.GetField("stop", InstanceMembers);
            signalAuthorityTrackField = signalType?.GetField("authorityTrack", InstanceMembers);
            signalTrackField = signalType?.GetField("track", InstanceMembers);

            Type blockType = DictionaryValueType(dispatcherBlockStatesField?.FieldType);
            blockReservedField = blockType?.GetField("reserved", InstanceMembers);
            blockTracksField = blockType?.GetField("tracks", InstanceMembers);
            blockExtraTracksField = blockType?.GetField("extraTracks", InstanceMembers);

            Type routeStepType = planSwitchesField?.FieldType.GetElementType();
            routeStepIdField = routeStepType?.GetField("id", InstanceMembers);
            routeStepBranchField = routeStepType?.GetField("branch", InstanceMembers);

            Type routeLocksType = dispatcherRouteLocksField?.FieldType;
            routeLocksTrackOwnerMethod = routeLocksType?.GetMethod("TrackOwner", InstanceMembers,
                null, new[] { typeof(string) }, null);
            routeLocksSwitchOwnerMethod = routeLocksType?.GetMethod("SwitchOwner", InstanceMembers,
                null, new[] { typeof(string) }, null);
            routeLocksAllowsSwitchMethod = routeLocksType?.GetMethod("AllowsSwitch", InstanceMembers,
                null, new[] { typeof(string), typeof(int) }, null);

            apiResolved = statePlanField != null
                && stateLifecycleField != null
                && stateReasonField != null
                && planNameField != null
                && planTrainCarsField != null
                && planWarningsField != null
                && planConflictsField != null
                && conflictResolvedAtField != null
                && conflictCodeField != null
                && conflictKindField != null
                && conflictTargetField != null
                && conflictTrackField != null
                && conflictTrainField != null;
            return apiResolved;
        }

        private static Type DictionaryValueType(Type dictionaryType)
        {
            if (dictionaryType == null || !dictionaryType.IsGenericType)
                return null;

            Type[] arguments = dictionaryType.GetGenericArguments();
            return arguments.Length == 2 ? arguments[1] : null;
        }

        private static object FindMatchingRoute(IEnumerable routeStates, IEnumerable<string> activeTrainCars,
            string attemptedFromTrackId, string attemptedToTrackId, out bool recentFailedRoute)
        {
            recentFailedRoute = false;
            if (routeStates == null)
                return null;

            var activeCars = new HashSet<string>(activeTrainCars, StringComparer.OrdinalIgnoreCase);
            object newestActiveState = null;
            long newestActiveTime = long.MinValue;
            object newestFailedState = null;
            long newestFailedTime = long.MinValue;
            long now = UtcNowMilliseconds();

            foreach (object state in routeStates)
            {
                if (state == null)
                    continue;

                object plan = statePlanField.GetValue(state);
                IEnumerable routeCars = plan == null ? null : planTrainCarsField.GetValue(plan) as IEnumerable;
                if (routeCars == null)
                    continue;

                var routeCarIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (object carId in routeCars)
                {
                    if (carId != null && !string.IsNullOrWhiteSpace(carId.ToString()))
                        routeCarIds.Add(carId.ToString());
                }

                if (routeCarIds.Count != activeCars.Count || !routeCarIds.SetEquals(activeCars))
                    continue;

                string lifecycle = Convert.ToString(stateLifecycleField.GetValue(state));
                string routeFrom = ReadFieldString(planActiveFromField, plan);
                if (string.IsNullOrWhiteSpace(routeFrom))
                    routeFrom = ReadFieldString(planFromField, plan);
                string routeTo = ReadFieldString(planActiveToField, plan);
                if (string.IsNullOrWhiteSpace(routeTo))
                    routeTo = ReadFieldString(planToField, plan);

                if (!string.IsNullOrWhiteSpace(attemptedToTrackId)
                    && !string.IsNullOrWhiteSpace(routeTo)
                    && !string.Equals(routeTo, attemptedToTrackId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                long stateTime = ReadStateTime(state);
                if (string.Equals(lifecycle, "active", StringComparison.OrdinalIgnoreCase))
                {
                    if (newestActiveState == null || stateTime > newestActiveTime)
                    {
                        newestActiveState = state;
                        newestActiveTime = stateTime;
                    }
                    continue;
                }

                bool failedLifecycle = string.Equals(lifecycle, "interrupted", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(lifecycle, "failed", StringComparison.OrdinalIgnoreCase);
                if (!failedLifecycle || !IsBlockingRouteReason(GetRouteReason(state, plan)))
                    continue;

                if (!string.IsNullOrWhiteSpace(attemptedFromTrackId)
                    && !string.IsNullOrWhiteSpace(routeFrom)
                    && !string.Equals(routeFrom, attemptedFromTrackId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                long age = now - stateTime;
                if (stateTime <= 0 || age < 0 || age > RecentFailedRouteWindowMilliseconds)
                    continue;

                if (newestFailedState == null || stateTime > newestFailedTime)
                {
                    newestFailedState = state;
                    newestFailedTime = stateTime;
                }
            }

            if (newestActiveState != null)
                return newestActiveState;

            recentFailedRoute = newestFailedState != null;
            return newestFailedState;
        }

        private static string GetRouteReason(object state, object plan)
        {
            string reason = ReadFieldString(stateReasonField, state);
            if (string.IsNullOrWhiteSpace(reason))
                reason = ReadFieldString(planInvalidationReasonField, plan);
            if (string.IsNullOrWhiteSpace(reason))
                reason = ReadFieldString(planReasonField, plan);
            return reason;
        }

        private static string ReadFieldString(FieldInfo field, object instance)
        {
            return field == null || instance == null ? null : Convert.ToString(field.GetValue(instance));
        }

        private static long ReadStateTime(object state)
        {
            if (stateTimeField == null || state == null)
                return 0;

            object value = stateTimeField.GetValue(state);
            return value == null ? 0 : Convert.ToInt64(value);
        }

        private static long UtcNowMilliseconds()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0,
                DateTimeKind.Utc)).TotalMilliseconds;
        }

        private static bool IsBlockingRouteReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return false;

            string code = reason.Trim();
            return BlockingConflictCodes.Contains(code)
                || string.Equals(code, "RESERVATION_LOST", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "NO_ALTERNATIVE_ROUTE", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> ReadWarnings(object plan)
        {
            var warnings = new List<string>();
            var uniqueWarnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IEnumerable planWarnings = planWarningsField.GetValue(plan) as IEnumerable;
            if (planWarnings != null)
            {
                foreach (object warning in planWarnings)
                    AddWarning(warnings, uniqueWarnings, warning == null ? null : warning.ToString());
            }

            IEnumerable conflicts = planConflictsField.GetValue(plan) as IEnumerable;
            if (conflicts != null)
            {
                foreach (object conflict in conflicts)
                {
                    if (conflict == null || Convert.ToInt64(conflictResolvedAtField.GetValue(conflict)) != 0)
                        continue;

                    string kind = Convert.ToString(conflictKindField.GetValue(conflict));
                    string code = Convert.ToString(conflictCodeField.GetValue(conflict));
                    string target = Convert.ToString(conflictTargetField.GetValue(conflict));
                    string track = Convert.ToString(conflictTrackField.GetValue(conflict));
                    string train = Convert.ToString(conflictTrainField.GetValue(conflict));
                    string description = "ADS active conflict";
                    if (!string.IsNullOrWhiteSpace(kind))
                        description += ": " + kind;
                    if (!string.IsNullOrWhiteSpace(code))
                        description += " [" + code + "]";
                    if (!string.IsNullOrWhiteSpace(target))
                        description += " at " + target;
                    if (!string.IsNullOrWhiteSpace(track))
                        description += " on " + track;
                    if (!string.IsNullOrWhiteSpace(train))
                        description += " (train " + train + ")";
                    AddWarning(warnings, uniqueWarnings, description);
                }
            }

            return warnings;
        }

        private static List<string> ReadBlockingTrackIds(object plan, out string firstBlockingCode,
            out List<string> nonReroutableTrackIds)
        {
            firstBlockingCode = null;
            var trackIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nonReroutableIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            nonReroutableTrackIds = new List<string>();
            IEnumerable conflicts = planConflictsField.GetValue(plan) as IEnumerable;
            if (conflicts == null)
                return trackIds.ToList();

            foreach (object conflict in conflicts)
            {
                if (conflict == null || Convert.ToInt64(conflictResolvedAtField.GetValue(conflict)) != 0)
                    continue;

                string code = Convert.ToString(conflictCodeField.GetValue(conflict));
                string track = Convert.ToString(conflictTrackField.GetValue(conflict));
                if (!BlockingConflictCodes.Contains((code ?? string.Empty).Trim()))
                    continue;

                if (string.IsNullOrWhiteSpace(firstBlockingCode))
                    firstBlockingCode = code.Trim();

                if (!string.IsNullOrWhiteSpace(track))
                {
                    string trackId = track.Trim();
                    trackIds.Add(trackId);
                    if (IsNonReroutableEndpointConflict(code))
                        nonReroutableIds.Add(trackId);
                }
            }

            nonReroutableTrackIds = nonReroutableIds.ToList();
            return trackIds.ToList();
        }

        private static bool IsNonReroutableEndpointConflict(string code)
        {
            string normalized = (code ?? string.Empty).Trim();
            return !string.Equals(normalized, "SIGNAL_RESERVED", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(normalized, "SIGNAL_STOP", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(normalized, "SIGNAL_OFF", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(normalized, "SWITCH_PLAN_CONFLICT", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddWarning(List<string> warnings, HashSet<string> uniqueWarnings, string warning)
        {
            if (!string.IsNullOrWhiteSpace(warning) && uniqueWarnings.Add(warning.Trim()))
                warnings.Add(warning.Trim());
        }

        private static string[] GetTrainCarIds(Trainset trainset)
        {
            if (trainset?.cars == null)
                return new string[0];

            return trainset.cars
                .Where(car => !string.IsNullOrWhiteSpace(car?.CarGUID))
                .Select(car => car.CarGUID)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static UnityModManager.ModEntry FindADSMod()
        {
            return UnityModManager.modEntries == null
                ? null
                : UnityModManager.modEntries.FirstOrDefault(entry => entry != null
                    && string.Equals(entry.Info?.Id, ADSModId, StringComparison.OrdinalIgnoreCase));
        }

        private static void SetUnavailable(string description)
        {
            StatusDescription = description;
            Warnings = new string[0];
            BlockingTrackIds = new string[0];
            NonReroutableTrackIds = new string[0];
            HasMatchingRoute = false;
            HasBlockingRouteReason = false;
            RouteReason = null;
            MatchedRouteId = null;
            BlockingReason = null;
        }

        private static string Shorten(string value, int maximumLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maximumLength)
                return value;

            return value.Substring(0, maximumLength - 3) + "...";
        }

        private static string FormatRouteReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return null;

            string warning = FormatKnownRouteWarningReason(reason);
            return warning ?? "ADS route reason: " + Shorten(reason, 110);
        }

        private static string FormatKnownRouteWarningReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return null;

            switch (reason.Trim().ToUpperInvariant())
            {
                case "TRACK_OCCUPIED":
                    return "ADS WARNING: a track on ADS's route is occupied.";
                case "PROTECTION_OCCUPIED":
                    return "ADS WARNING: an occupied track is protected by another route.";
                case "ROUTE_OBSTRUCTED":
                    return "ADS WARNING: its route is obstructed.";
                case "RESERVATION_LOST":
                    return "ADS WARNING: its route reservation was lost.";
                case "SIGNAL_RESERVED":
                    return "ADS WARNING: a signal block required by its route is reserved.";
                case "SIGNAL_STOP":
                    return "ADS WARNING: a governing signal on the original route is at stop.";
                case "SIGNAL_OFF":
                    return "ADS WARNING: a governing signal on the original route is off.";
                case "NO_ALTERNATIVE_ROUTE":
                    return "ADS WARNING: ADS could not calculate an alternative route.";
                case "TRAIN_CHANGED":
                    return "ADS WARNING: the train on its route changed; ADS may need to recalculate.";
                default:
                    return null;
            }
        }
    }
}
