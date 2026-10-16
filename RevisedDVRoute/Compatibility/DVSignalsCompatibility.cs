using CommandTerminal;
using System.Collections;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityModManagerNet;

namespace RevisedDVRoute.Compatibility
{
    /// <summary>
    /// Optional, runtime-only integration with DV Signals.  A direct assembly
    /// reference would make DV Signals mandatory, so the small public surface
    /// needed here is resolved only when the mod is active.
    /// </summary>
    internal static class DVSignalsCompatibility
    {
        private const string DVSignalsModId = "DVSignals";
        private static readonly HashSet<RailTrack> EmptyReservationSnapshot = new HashSet<RailTrack>();

        // The path finder reads this immutable snapshot during its
        // frame-yielding main-thread search. It is replaced before each search.
        private static volatile HashSet<RailTrack> reservationSnapshot = EmptyReservationSnapshot;
        private static volatile bool snapshotIsActive;

        private static MethodInfo isTrackReserved;
        private static PropertyInfo signalName;
        private static bool apiUnavailableLogged;
        private static bool apiAvailableLogged;
        private static bool invocationFailureLogged;
        private static PropertyInfo signalManagerInstance;
        private static PropertyInfo signalManagerRunning;
        private static PropertyInfo allControllers;
        private static PropertyInfo controllerPlacement;
        private static MethodInfo controllerGoverningSignal;
        private static MethodInfo controllerShuntingSignal;
        private static FieldInfo placementTrack;
        private static FieldInfo placementDirection;
        private static FieldInfo placementSpan;
        private static PropertyInfo signalIsOn;
        private static PropertyInfo signalCurrentAspect;
        private static PropertyInfo aspectDisallowPassing;
        private static bool aspectFailureLogged;

        private struct SignalRouteSegment
        {
            internal double EntryDistance;
            internal double Length;
            internal float Direction;
        }

        /// <summary>
        /// Finds the nearest facing STOP aspect along the actual route. A red
        /// signal need not have a TrackReserver reservation, so reservations
        /// cannot be used as a substitute for this check.
        /// </summary>
        internal static bool TryGetStopSignalAhead(IList<RailTrack> path,
            int currentPathIndex, double currentSpan, float currentDirection,
            out float stopDistance)
        {
            stopDistance = float.PositiveInfinity;
            if (!IsActive)
                return true;
            if (path == null || currentPathIndex < 0
                || currentPathIndex >= path.Count || !EnsureAspectApi())
                return false;

            try
            {
                if (!(bool)signalManagerRunning.GetValue(null, null))
                    return false;
                object manager = signalManagerInstance.GetValue(null, null);
                if (manager == null)
                    return false;

                var segments = new Dictionary<RailTrack, List<SignalRouteSegment>>();
                double distance = 0.0;
                for (int index = currentPathIndex; index < path.Count && distance <= 2500.0; index++)
                {
                    RailTrack track = path[index];
                    if (track == null || track.curve == null)
                        return false;

                    double length = track.curve.length;
                    float direction = currentDirection;
                    double entryDistance;
                    if (index == currentPathIndex)
                    {
                        entryDistance = -(direction >= 0f ? currentSpan : length - currentSpan);
                        distance += direction >= 0f ? length - currentSpan : currentSpan;
                    }
                    else
                    {
                        RailTrack previous = path[index - 1];
                        if (track.IsTrackInBranch(previous))
                            direction = 1f;
                        else if (track.IsTrackOutBranch(previous))
                            direction = -1f;
                        else
                            return false;
                        entryDistance = distance;
                        distance += length;
                    }

                    if (!segments.TryGetValue(track, out List<SignalRouteSegment> occurrences))
                    {
                        occurrences = new List<SignalRouteSegment>();
                        segments.Add(track, occurrences);
                    }
                    occurrences.Add(new SignalRouteSegment
                    {
                        EntryDistance = entryDistance,
                        Length = length,
                        Direction = direction
                    });
                }

                foreach (object controller in (IEnumerable)allControllers.GetValue(manager, null))
                {
                    object placement = controllerPlacement.GetValue(controller, null);
                    if (placement == null)
                        continue;
                    RailTrack signalTrack = placementTrack.GetValue(placement) as RailTrack;
                    if (signalTrack == null
                        || !segments.TryGetValue(signalTrack, out List<SignalRouteSegment> occurrences))
                        continue;

                    int facing = Convert.ToInt32(placementDirection.GetValue(placement));
                    double span = Convert.ToDouble(placementSpan.GetValue(placement));
                    foreach (SignalRouteSegment segment in occurrences)
                    {
                        // DV Signals placement direction is the direction the
                        // lamps face, opposite to the train's traversal.
                        if (facing != (segment.Direction >= 0f ? 1 : 0))
                            continue;
                        double mastDistance = segment.EntryDistance
                            + (segment.Direction >= 0f ? span : segment.Length - span);
                        if (mastDistance < 0.0 || mastDistance >= stopDistance)
                            continue;

                        // Junction controllers can own one signal per branch.
                        // Read the selected governing head, not an inactive
                        // branch's red head or a display-only signal.
                        object signal = controllerGoverningSignal.Invoke(controller, null)
                            ?? controllerShuntingSignal.Invoke(controller, null);
                        if (signal == null || !(bool)signalIsOn.GetValue(signal, null))
                            continue;
                        object aspect = signalCurrentAspect.GetValue(signal, null);
                        if (aspect != null && (bool)aspectDisallowPassing.GetValue(aspect, null))
                            stopDistance = (float)mastDistance;
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                if (!aspectFailureLogged)
                {
                    aspectFailureLogged = true;
                    Module.mod?.Logger.Log("DV Signals aspect scan failed: "
                        + exception.GetType().Name + "; AI will hold until it recovers.");
                }
                return false;
            }
        }

        private static bool EnsureAspectApi()
        {
            if (signalManagerInstance != null && signalManagerRunning != null
                && allControllers != null && controllerPlacement != null
                && controllerGoverningSignal != null && controllerShuntingSignal != null
                && placementTrack != null
                && placementDirection != null && placementSpan != null
                && signalIsOn != null && signalCurrentAspect != null
                && aspectDisallowPassing != null)
                return true;

            Type managerType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Signals.Game.SignalManager", false))
                .FirstOrDefault(type => type != null);
            Type controllerType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Signals.Game.Controllers.BasicSignalController", false))
                .FirstOrDefault(type => type != null);
            Type placementType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Signals.Game.SignalPlacementInfo", false))
                .FirstOrDefault(type => type != null);
            Type signalType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Signals.Game.Signal", false))
                .FirstOrDefault(type => type != null);
            Type aspectType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Signals.Game.Aspects.IAspect", false))
                .FirstOrDefault(type => type != null);

            signalManagerInstance = managerType?.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            signalManagerRunning = managerType?.GetProperty("Running",
                BindingFlags.Public | BindingFlags.Static);
            allControllers = managerType?.GetProperty("AllControllers",
                BindingFlags.Public | BindingFlags.Instance);
            controllerPlacement = controllerType?.GetProperty("PlacementInfo",
                BindingFlags.Public | BindingFlags.Instance);
            controllerGoverningSignal = controllerType?.GetMethod("GetControllerSignal",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            controllerShuntingSignal = controllerType?.GetMethod("GetControllerShuntingSignal",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            placementTrack = placementType?.GetField("Track");
            placementDirection = placementType?.GetField("Direction");
            placementSpan = placementType?.GetField("Span");
            signalIsOn = signalType?.GetProperty("IsOn");
            signalCurrentAspect = signalType?.GetProperty("CurrentAspect");
            aspectDisallowPassing = aspectType?.GetProperty("DisallowPassing");
            return signalManagerInstance != null && signalManagerRunning != null
                && allControllers != null && controllerPlacement != null
                && controllerGoverningSignal != null && controllerShuntingSignal != null
                && placementTrack != null
                && placementDirection != null && placementSpan != null
                && signalIsOn != null && signalCurrentAspect != null
                && aspectDisallowPassing != null;
        }

        public static void Initialize()
        {
            if (!IsActive)
            {
                Module.mod?.Logger.Log("DV Signals not active: signal-reservation protection is unavailable.");
                return;
            }

            EnsureApi();
        }

        public static string StatusDescription
        {
            get
            {
                if (Module.settings == null || !Module.settings.RespectDVSignalsReservations)
                    return "reservation protection disabled";

                if (!Module.settings.ReserveDVSignalsBlock)
                    return "reservation protection enabled; player route reservation disabled";

                if (ADSCompatibility.ManagesDVSignals)
                    return "reservation protection enabled; player signal authority delegated to ADS";

                return "reservation protection enabled; " + DVSignalsRouteReservation.StatusDescription;
            }
        }

        /// <summary>
        /// Claims the next DV Signals block on an active player route, if the
        /// route and signal layout provide an unambiguous governing signal.
        /// AI Traffic identifies these non-AI signal reservations as player
        /// right-of-way and yields through its normal signaling logic.
        /// </summary>
        public static void BeginPlayerRouteReservation(Route route)
        {
            ClearLoggedBlockedJunctions();
            DVSignalsRouteReservation.Begin(route);
        }

        /// <summary>
        /// Advances the player-owned reservation only after the rear of the
        /// train has cleared its current signal block.
        /// </summary>
        public static void UpdatePlayerRouteReservation(Route route, int frontPathIndex, int rearPathIndex)
        {
            DVSignalsRouteReservation.Update(route, frontPathIndex, rearPathIndex);
        }

        /// <summary>
        /// Clears the signal block owned by the current active route. It never
        /// attempts to clear reservations made by another signal or mod.
        /// </summary>
        public static void ClearPlayerRouteReservation()
        {
            ClearLoggedBlockedJunctions();
            DVSignalsRouteReservation.Clear();
        }

        /// <summary>
        /// Captures active DV Signals reservations while on Unity's thread.
        /// Call before beginning a background path search.
        /// </summary>
        public static void RefreshReservationSnapshot()
        {
            if (!ProtectionEnabled || !EnsureApi())
            {
                reservationSnapshot = EmptyReservationSnapshot;
                snapshotIsActive = false;
                return;
            }

            var reserved = new HashSet<RailTrack>();
            foreach (RailTrack track in RailTrackRegistryBase.RailTracks)
            {
                string ignoredOwner;
                if (track != null && TryGetReservation(track, out ignoredOwner))
                    reserved.Add(track);
            }

            reservationSnapshot = reserved;
            snapshotIsActive = true;
        }

        /// <summary>
        /// Safe for the route finder's background worker because it only reads
        /// the reservation snapshot created before the search began.
        /// </summary>
        public static bool IsReservedInSnapshot(RailTrack track)
        {
            return snapshotIsActive && track != null && reservationSnapshot.Contains(track);
        }

        internal static int ReservationCount
        {
            get { return snapshotIsActive ? reservationSnapshot.Count : 0; }
        }

        private static readonly HashSet<int> LoggedBlockedJunctions = new HashSet<int>();

        public static void ClearLoggedBlockedJunctions()
        {
            LoggedBlockedJunctions.Clear();
        }

        public static void UnlogBlockedJunction(Junction junction)
        {
            if (junction != null)
                LoggedBlockedJunctions.Remove(junction.GetInstanceID());
        }

        /// <summary>
        /// A junction can only be changed when none of its connected rails is
        /// protected by a foreign DV Signals reservation. The rolling
        /// reservation owned by the active player route is filtered out.
        /// </summary>
        public static bool CanSwitchJunction(Junction junction, out string reservationOwner)
        {
            reservationOwner = null;
            if (!ProtectionEnabled || junction == null)
            {
                UnlogBlockedJunction(junction);
                return true;
            }

            if (!EnsureApi())
            {
                reservationOwner = "status unavailable";
                return false;
            }

            RailTrack incoming = junction.inBranch?.track;
            if (TryGetReservation(incoming, out reservationOwner))
                return false;
            if (isTrackReserved == null)
            {
                reservationOwner = "status unavailable";
                return false;
            }

            if (junction.outBranches != null)
            {
                foreach (var branch in junction.outBranches)
                {
                    if (TryGetReservation(branch?.track, out reservationOwner))
                        return false;
                    if (isTrackReserved == null)
                    {
                        reservationOwner = "status unavailable";
                        return false;
                    }
                }
            }

            UnlogBlockedJunction(junction);
            return true;
        }

        public static void LogBlockedJunctionSwitch(Junction junction, string reservationOwner)
        {
            if (junction != null && !LoggedBlockedJunctions.Add(junction.GetInstanceID()))
                return;

            string junctionName = junction == null ? null : junction.junctionData.junctionIdLong;
            if (string.IsNullOrEmpty(junctionName))
                junctionName = junction?.name ?? "unknown junction";

            string owner = string.IsNullOrEmpty(reservationOwner)
                ? "an active DV Signals reservation"
                : "DV Signals reservation " + reservationOwner;
            string message = owner + " protects " + junctionName + "; automatic switch skipped.";
            Terminal.Log(message);
            Module.mod?.Logger.Log(message);
        }

        private static bool ProtectionEnabled
        {
            get
            {
                return Module.settings != null
                    && Module.settings.RespectDVSignalsReservations
                    && IsActive;
            }
        }

        internal static bool PlayerRouteReservationEnabled
        {
            get
            {
                return ProtectionEnabled
                    && Module.settings != null
                    && Module.settings.ReserveDVSignalsBlock
                    && !ADSCompatibility.ManagesDVSignals;
            }
        }

        internal static bool IsActive
        {
            get
            {
                return UnityModManager.modEntries != null
                    && UnityModManager.modEntries.Any(entry => entry != null
                        && entry.Active
                        && string.Equals(entry.Info?.Id, DVSignalsModId, StringComparison.OrdinalIgnoreCase));
            }
        }

        private static bool EnsureApi()
        {
            if (isTrackReserved != null)
                return true;

            if (!IsActive)
                return false;

            Type reserverType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Signals.Game.Railway.TrackReserver", false))
                .FirstOrDefault(type => type != null);
            if (reserverType == null)
            {
                LogUnavailable("TrackReserver type was not loaded yet");
                return false;
            }

            MethodInfo candidate = reserverType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "IsTrackReserved" || method.ReturnType != typeof(bool))
                        return false;

                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == 2
                        && parameters[0].ParameterType == typeof(RailTrack)
                        && parameters[1].IsOut;
                });
            if (candidate == null)
            {
                LogUnavailable("TrackReserver.IsTrackReserved(RailTrack, out Signal) was not found");
                return false;
            }

            isTrackReserved = candidate;
            Type signalType = candidate.GetParameters()[1].ParameterType.GetElementType();
            signalName = signalType?.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            if (!apiAvailableLogged)
            {
                apiAvailableLogged = true;
                Module.mod?.Logger.Log("DV Signals detected: routes prefer unreserved blocks, automatic switches respect protected junctions, and active player routes can reserve their next block.");
            }
            return true;
        }

        internal static bool IsTrackReservedByOther(RailTrack track, out string reservationOwner)
        {
            EnsureApi();
            return TryGetReservation(track, out reservationOwner, true);
        }

        /// <summary>
        /// AI trains do not own the active player's rolling reservation. Keep
        /// it visible to their safety checks so they yield to player-protected
        /// blocks instead of treating the player's authority as their own.
        /// </summary>
        internal static bool IsTrackReservedForAI(RailTrack track, out string reservationOwner)
        {
            bool reserved;
            return TryIsTrackReservedForAI(track, out reserved, out reservationOwner)
                && reserved;
        }

        internal static bool TryIsTrackReservedForAI(RailTrack track, out bool reserved,
            out string reservationOwner)
        {
            reserved = false;
            reservationOwner = null;
            if (!IsActive)
                return true;
            if (track == null || !EnsureApi())
                return false;

            try
            {
                object[] arguments = { track, null };
                reserved = (bool)isTrackReserved.Invoke(null, arguments);
                if (reserved)
                {
                    object signal = arguments[1];
                    // The player's own rolling reservation must not stop the
                    // player's own locomotive AI.
                    if (DVSignalsRouteReservation.OwnsSignal(signal))
                    {
                        reserved = false;
                        return true;
                    }
                    reservationOwner = signalName?.GetValue(signal, null) as string;
                }
                return true;
            }
            catch (Exception exception)
            {
                isTrackReserved = null;
                reservationSnapshot = EmptyReservationSnapshot;
                snapshotIsActive = false;
                if (!invocationFailureLogged)
                {
                    invocationFailureLogged = true;
                    Module.mod?.Logger.Log("DV Signals reservation check disabled after an API error: "
                        + exception.GetType().Name);
                }
                return false;
            }
        }

        internal static bool TryGetTrackReservationHolder(RailTrack track, out object holder)
        {
            holder = null;
            if (track == null || isTrackReserved == null)
                return false;

            try
            {
                object[] arguments = { track, null };
                if (!(bool)isTrackReserved.Invoke(null, arguments))
                    return false;
                holder = arguments[1];
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetReservation(RailTrack track, out string reservationOwner)
        {
            return TryGetReservation(track, out reservationOwner, true);
        }

        private static bool TryGetReservation(RailTrack track, out string reservationOwner,
            bool ignoreActiveRouteReservations)
        {
            reservationOwner = null;
            if (track == null || isTrackReserved == null)
                return false;

            try
            {
                object[] arguments = { track, null };
                if (!(bool)isTrackReserved.Invoke(null, arguments))
                    return false;

                object signal = arguments[1];
                if (ignoreActiveRouteReservations && DVSignalsRouteReservation.OwnsSignal(signal))
                    return false;

                // ADS reserves through the same native DV Signals head. Once
                // ADS has accepted this exact consist/path/signal, that
                // reservation is our route authority rather than a foreign
                // blocker.
                if (ignoreActiveRouteReservations
                    && DVSignalsRouteReservation.IsADSAuthoritySignal(signal))
                    return false;

                reservationOwner = signalName?.GetValue(signal, null) as string;
                return true;
            }
            catch (Exception exception)
            {
                // Do not make a broken optional integration block normal routing.
                isTrackReserved = null;
                reservationSnapshot = EmptyReservationSnapshot;
                snapshotIsActive = false;
                if (!invocationFailureLogged)
                {
                    invocationFailureLogged = true;
                    Module.mod?.Logger.Log("DV Signals reservation check disabled after an API error: " + exception.GetType().Name);
                }
                return false;
            }
        }

        private static void LogUnavailable(string reason)
        {
            if (apiUnavailableLogged)
                return;

            apiUnavailableLogged = true;
            Module.mod?.Logger.Log("DV Signals is active but its reservation API is unavailable (" + reason + "). Protection will be retried when a route is created.");
        }
    }
}
