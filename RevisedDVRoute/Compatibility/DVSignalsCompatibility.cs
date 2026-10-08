using CommandTerminal;
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

        // The path finder reads this immutable snapshot from its background
        // worker.  It is replaced wholesale on Unity's thread before a search.
        private static volatile HashSet<RailTrack> reservationSnapshot = EmptyReservationSnapshot;
        private static volatile bool snapshotIsActive;

        private static MethodInfo isTrackReserved;
        private static PropertyInfo signalName;
        private static bool apiUnavailableLogged;
        private static bool apiAvailableLogged;
        private static bool invocationFailureLogged;

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

                if (!Module.settings.ReserveDVSignalsRoute)
                    return "reservation protection enabled; player route reservation disabled";

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

        /// <summary>
        /// A junction can only be changed when none of its connected rails is
        /// protected by a foreign DV Signals reservation. The rolling
        /// reservation owned by the active player route is filtered out.
        /// </summary>
        public static bool CanSwitchJunction(Junction junction, out string reservationOwner)
        {
            reservationOwner = null;
            if (!ProtectionEnabled || junction == null || !EnsureApi())
                return true;

            RailTrack incoming = junction.inBranch?.track;
            if (TryGetReservation(incoming, out reservationOwner))
                return false;

            if (junction.outBranches != null)
            {
                foreach (var branch in junction.outBranches)
                {
                    if (TryGetReservation(branch?.track, out reservationOwner))
                        return false;
                }
            }

            return true;
        }

        public static void LogBlockedJunctionSwitch(Junction junction, string reservationOwner)
        {
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
                    && Module.settings.ReserveDVSignalsRoute;
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

        private static bool TryGetReservation(RailTrack track, out string reservationOwner)
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
                if (DVSignalsRouteReservation.OwnsSignal(signal))
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
