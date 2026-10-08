using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityModManagerNet;

namespace RevisedDVRoute.Compatibility
{
    /// <summary>
    /// Snapshots AI Traffic's internal track reservations for route planning
    /// and reports whether it is configured to yield to the player's DV Signals
    /// reservation. No direct reference is used so AI Traffic stays optional.
    /// </summary>
    internal static class AITrafficCompatibility
    {
        private const string ModId = "AITraffic";
        private static readonly HashSet<RailTrack> EmptyReservationSnapshot = new HashSet<RailTrack>();
        private static volatile HashSet<RailTrack> reservationSnapshot = EmptyReservationSnapshot;
        private static volatile bool snapshotIsActive;
        private static PropertyInfo railGraphInstance;
        private static PropertyInfo railGraphIsInitialized;
        private static MethodInfo isTrackReserved;
        private static bool apiFailureLogged;

        public static string StatusDescription
        {
            get
            {
                if (!DVSignalsCompatibility.IsActive)
                    return "AI reservations considered; DV Signals required for player route right-of-way";

                bool? playerPriority = GetPlayerPriority();
                if (!playerPriority.HasValue)
                    return "AI reservations considered; could not read Player Priority";

                return playerPriority.Value
                    ? "AI reservations considered; DV Signals handoff active; Player Priority enabled"
                    : "AI reservations considered; enable Player Priority for player-first dispatching";
            }
        }

        internal static int ReservationCount
        {
            get { return snapshotIsActive ? reservationSnapshot.Count : 0; }
        }

        internal static void RefreshReservationSnapshot()
        {
            if (!IsActive || !EnsureRailGraphApi())
            {
                reservationSnapshot = EmptyReservationSnapshot;
                snapshotIsActive = false;
                return;
            }

            try
            {
                object graph = railGraphInstance.GetValue(null, null);
                if (graph == null
                    || (railGraphIsInitialized != null
                        && !(bool)railGraphIsInitialized.GetValue(graph, null)))
                {
                    reservationSnapshot = EmptyReservationSnapshot;
                    snapshotIsActive = false;
                    return;
                }

                var reserved = new HashSet<RailTrack>();
                foreach (RailTrack track in RailTrackRegistryBase.RailTracks)
                {
                    if (track != null && (bool)isTrackReserved.Invoke(graph, new object[] { track }))
                        reserved.Add(track);
                }

                reservationSnapshot = reserved;
                snapshotIsActive = true;
            }
            catch (Exception exception)
            {
                reservationSnapshot = EmptyReservationSnapshot;
                snapshotIsActive = false;
                if (!apiFailureLogged)
                {
                    apiFailureLogged = true;
                    Module.mod?.Logger.Log("AI Traffic reservation snapshot unavailable after an API error: "
                        + exception.GetType().Name);
                }
            }
        }

        internal static bool IsReservedInSnapshot(RailTrack track)
        {
            return snapshotIsActive && track != null && reservationSnapshot.Contains(track);
        }

        private static bool? GetPlayerPriority()
        {
            try
            {
                Type mainType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType("AITraffic.Main", false))
                    .FirstOrDefault(type => type != null);
                PropertyInfo settingsProperty = mainType?.GetProperty(
                    "Settings",
                    BindingFlags.Public | BindingFlags.Static);
                object settings = settingsProperty?.GetValue(null, null);
                FieldInfo playerPriorityField = settings?.GetType().GetField(
                    "PlayerPriority",
                    BindingFlags.Public | BindingFlags.Instance);
                object value = playerPriorityField?.GetValue(settings);
                return value is bool ? (bool?)value : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool EnsureRailGraphApi()
        {
            if (railGraphInstance != null && isTrackReserved != null)
                return true;

            Type railGraphType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("AITraffic.Navigation.RailGraph", false))
                .FirstOrDefault(type => type != null);
            railGraphInstance = railGraphType?.GetProperty(
                "Instance", BindingFlags.Public | BindingFlags.Static);
            railGraphIsInitialized = railGraphType?.GetProperty(
                "IsInitialized", BindingFlags.Public | BindingFlags.Instance);
            isTrackReserved = railGraphType?.GetMethod(
                "IsTrackReserved",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(RailTrack) },
                null);
            return railGraphInstance != null && isTrackReserved != null;
        }

        private static bool IsActive
        {
            get
            {
                return UnityModManager.modEntries != null
                    && UnityModManager.modEntries.Any(entry => entry != null
                        && entry.Active
                        && string.Equals(entry.Info?.Id, ModId, StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}
