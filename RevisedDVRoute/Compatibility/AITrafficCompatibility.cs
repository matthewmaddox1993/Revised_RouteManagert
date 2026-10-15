using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityModManagerNet;
using UnityEngine;

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
        private static float nextSnapshotRefreshTime;
        private static MethodInfo findUpcomingSignals;
        private static MethodInfo findUpcomingObstacle;
        private static object upcomingSignals;
        private static FieldInfo upcomingSignalValue;
        private static FieldInfo upcomingSignalDistance;
        private static bool safetyApiFailureLogged;
        private static PropertyInfo junctionControllerInstance;
        private static MethodInfo isJunctionLockedByAI;

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

        internal static void RefreshReservationSnapshot(bool force = false)
        {
            if (!force && Time.time < nextSnapshotRefreshTime)
                return;

            nextSnapshotRefreshTime = Time.time + 1.0f;

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

        /// <summary>
        /// Reads the live reservation state for a single track. The full route
        /// planner may use a snapshot, but a moving AI train must not rely on a
        /// one-second-old traffic reservation when deciding whether to enter a
        /// block. The return value reports whether the integration state could
        /// be read; callers can fail safe when an active mod is unavailable.
        /// </summary>
        internal static bool TryIsTrackReservedLive(RailTrack track, out bool reserved)
        {
            reserved = false;
            if (!IsActive)
                return true;

            if (track == null || !EnsureRailGraphApi())
                return false;

            try
            {
                object graph = railGraphInstance.GetValue(null, null);
                if (graph == null
                    || (railGraphIsInitialized != null
                        && !(bool)railGraphIsInitialized.GetValue(graph, null)))
                    return false;

                reserved = (bool)isTrackReserved.Invoke(graph, new object[] { track });
                return true;
            }
            catch (Exception exception)
            {
                reserved = IsReservedInSnapshot(track);
                if (!apiFailureLogged)
                {
                    apiFailureLogged = true;
                    Module.mod?.Logger.Log("AI Traffic live reservation check failed: "
                        + exception.GetType().Name + "; the AI will hold until the reservation API is available.");
                }
                return false;
            }
        }

        internal static bool IsJunctionReserved(Junction junction, out RailTrack reservedTrack)
        {
            reservedTrack = null;
            if (junction == null || !IsActive)
                return false;

            // AI Traffic's junction locks are separate from RailGraph track
            // reservations. A train may protect a switch even when none of
            // its adjacent tracks appears in our one-second snapshot.
            try
            {
                if (junctionControllerInstance == null || isJunctionLockedByAI == null)
                {
                    Type controllerType = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(assembly => assembly.GetType("AITraffic.Navigation.JunctionController", false))
                        .FirstOrDefault(type => type != null);
                    junctionControllerInstance = controllerType?.GetProperty(
                        "Instance", BindingFlags.Public | BindingFlags.Static);
                    isJunctionLockedByAI = controllerType?.GetMethod(
                        "IsJunctionLockedByAI", BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { typeof(Junction) }, null);
                }

                if (junctionControllerInstance == null || isJunctionLockedByAI == null)
                    return true;

                object controller = junctionControllerInstance.GetValue(null, null);
                if (controller == null || (bool)isJunctionLockedByAI.Invoke(controller, new object[] { junction }))
                    return true;
            }
            catch (Exception)
            {
                return true;
            }

            if (!snapshotIsActive)
                return true;

            RailTrack incoming = junction.inBranch?.track;
            if (IsReservedInSnapshot(incoming))
            {
                reservedTrack = incoming;
                return true;
            }

            if (junction.outBranches != null)
            {
                foreach (var branch in junction.outBranches)
                {
                    if (IsReservedInSnapshot(branch?.track))
                    {
                        reservedTrack = branch.track;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Use AI Traffic's own live, facing-signal and bogie-position scans.
        /// A track-level reservation alone does not describe either hazard.
        /// Returns false when an active AI Traffic installation cannot provide
        /// a trustworthy scan, so the driver can fail safe.
        /// </summary>
        internal static bool TryGetHazardsAhead(
            RailTrack currentTrack, double currentSpan, float direction,
            IList<RailTrack> upcomingRoute, Trainset ownTrainset, TrainCar ownLoco,
            out float obstacleDistance, out float stopSignalDistance)
        {
            obstacleDistance = float.PositiveInfinity;
            stopSignalDistance = float.PositiveInfinity;
            if (!IsActive)
                return true;

            if (currentTrack == null || !EnsureSafetyApi())
                return false;

            try
            {
                object[] obstacleArgs = { currentTrack, currentSpan, direction,
                    upcomingRoute, ownTrainset, 0f, ownLoco, null };
                if ((bool)findUpcomingObstacle.Invoke(null, obstacleArgs))
                    obstacleDistance = (float)obstacleArgs[5];

                object[] signalArgs = { currentTrack, currentSpan, direction,
                    upcomingRoute, upcomingSignals };
                findUpcomingSignals.Invoke(null, signalArgs);
                foreach (object item in (IEnumerable)upcomingSignals)
                {
                    object signal = upcomingSignalValue.GetValue(item);
                    if (signal == null)
                        continue;

                    Type signalType = signal.GetType();
                    object isOn = signalType.GetProperty("IsOn")?.GetValue(signal, null);
                    if (isOn is bool && !(bool)isOn)
                        continue;

                    object aspect = signalType.GetProperty("CurrentAspect")?.GetValue(signal, null);
                    if (aspect == null)
                        continue;

                    PropertyInfo disallowProperty = aspect.GetType().GetProperty("DisallowPassing")
                        ?? aspect.GetType().GetInterfaces()
                            .Select(type => type.GetProperty("DisallowPassing"))
                            .FirstOrDefault(property => property != null);
                    if (disallowProperty == null)
                        return false;

                    bool disallowPassing = (bool)disallowProperty.GetValue(aspect, null);
                    if (!disallowPassing)
                    {
                        MethodInfo getDefinition = aspect.GetType().GetMethod("GetDefinition",
                            BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                        object definition = getDefinition?.Invoke(aspect, null);
                        object definitionDisallow = definition?.GetType()
                            .GetProperty("DisallowPassing")?.GetValue(definition, null);
                        disallowPassing = definitionDisallow is bool && (bool)definitionDisallow;
                    }
                    if (disallowPassing)
                    {
                        float distance = (float)upcomingSignalDistance.GetValue(item);
                        if (distance < stopSignalDistance)
                            stopSignalDistance = distance;
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                if (!safetyApiFailureLogged)
                {
                    safetyApiFailureLogged = true;
                    Module.mod?.Logger.Log("AI Traffic signal/obstacle scan failed: "
                        + exception.GetType().Name + "; the AI will hold until safety checks recover.");
                }
                return false;
            }
        }

        private static bool EnsureSafetyApi()
        {
            if (findUpcomingSignals != null && findUpcomingObstacle != null
                && upcomingSignals != null && upcomingSignalValue != null
                && upcomingSignalDistance != null)
                return true;

            Type registry = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("AITraffic.Navigation.SignalRegistry", false))
                .FirstOrDefault(type => type != null);
            if (registry == null)
                return false;

            Type entryType = registry.GetNestedType("UpcomingSignal", BindingFlags.Public);
            if (entryType == null)
                return false;

            Type listType = typeof(List<>).MakeGenericType(entryType);
            findUpcomingSignals = registry.GetMethod("TryFindUpcomingSignals",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(RailTrack), typeof(double), typeof(float),
                    typeof(IList<RailTrack>), listType }, null);
            findUpcomingObstacle = registry.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "TryFindUpcomingObstacle"
                    && method.GetParameters().Length == 8);
            upcomingSignalValue = entryType.GetField("Signal", BindingFlags.Public | BindingFlags.Instance);
            upcomingSignalDistance = entryType.GetField("Distance", BindingFlags.Public | BindingFlags.Instance);
            if (findUpcomingSignals == null || findUpcomingObstacle == null
                || upcomingSignalValue == null || upcomingSignalDistance == null)
                return false;

            upcomingSignals = Activator.CreateInstance(listType);
            return true;
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

        internal static bool IsActive
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
