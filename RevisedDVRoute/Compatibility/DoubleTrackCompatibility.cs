using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityModManagerNet;

namespace RevisedDVRoute.Compatibility
{
    /// <summary>
    /// Optional DoubleTrack integration. DoubleTrack creates a different
    /// physical rail graph for Normal and Hard layouts, so routing must use
    /// LoadedTrackMode rather than the setting selected for a future reload.
    /// No DoubleTrack assembly reference is required.
    /// </summary>
    internal static class DoubleTrackCompatibility
    {
        private const string DoubleTrackModId = "DoubleTrack";
        private const double NormalMainlinePreference = 0.85;
        private const double NormalWrongWayPenalty = 120.0;
        private const double NormalCrossoverPenalty = 25.0;
        private const double HardCrossoverPenalty = 8.0;

        private enum LoadedMode
        {
            Unavailable,
            Normal,
            Hard
        }

        private sealed class LayoutSnapshot
        {
            public static readonly LayoutSnapshot Empty = new LayoutSnapshot(LoadedMode.Unavailable,
                new HashSet<RailTrack>(), new HashSet<RailTrack>(), new HashSet<RailTrack>(),
                new Dictionary<RailTrack, bool>());

            public readonly LoadedMode Mode;
            public readonly HashSet<RailTrack> DoubleTrackSections;
            public readonly HashSet<RailTrack> CrossoverSections;
            public readonly HashSet<RailTrack> UnsafeSections;
            public readonly Dictionary<RailTrack, bool> PreferredForward;

            public LayoutSnapshot(LoadedMode mode, HashSet<RailTrack> doubleTrackSections,
                HashSet<RailTrack> crossoverSections, HashSet<RailTrack> unsafeSections,
                Dictionary<RailTrack, bool> preferredForward)
            {
                Mode = mode;
                DoubleTrackSections = doubleTrackSections;
                CrossoverSections = crossoverSections;
                UnsafeSections = unsafeSections;
                PreferredForward = preferredForward;
            }
        }

        // The path finder reads this immutable snapshot on its background
        // worker. It is rebuilt wholesale on Unity's thread before each route.
        private static volatile LayoutSnapshot currentLayout = LayoutSnapshot.Empty;
        private static string lastLoggedLayout;
        private static string lastPendingMode;
        private static bool apiUnavailableLogged;
        private static bool waitingForLayoutLogged;

        public static double AdjustRouteCost(RailTrack previous, RailTrack current, RailTrack candidate, double cost)
        {
            if (candidate == null)
                return cost;

            LayoutSnapshot layout = currentLayout;
            if (layout.Mode == LoadedMode.Unavailable)
                return cost;

            if (layout.DoubleTrackSections.Contains(candidate) && layout.Mode == LoadedMode.Normal)
            {
                cost *= NormalMainlinePreference;
                bool routeForward = current == null || candidate.IsTrackInBranch(current);
                bool preferred;
                if (layout.PreferredForward.TryGetValue(candidate, out preferred) && routeForward != preferred)
                    cost += NormalWrongWayPenalty;
            }

            if (layout.CrossoverSections.Contains(candidate))
                cost += layout.Mode == LoadedMode.Normal ? NormalCrossoverPenalty : HardCrossoverPenalty;

            return cost;
        }

        // This must run on Unity's main thread. PathFinder calls it before it
        // switches to its background A* worker.
        public static void Initialize()
        {
            RefreshForCurrentLayout();
        }

        public static string StatusDescription
        {
            get
            {
                LayoutSnapshot layout = currentLayout;
                LoadedMode selectedMode = GetSelectedMode();
                switch (layout.Mode)
                {
                    case LoadedMode.Normal:
                        return DescribeActiveLayout("Normal", selectedMode, layout.Mode,
                            layout.UnsafeSections.Count);
                    case LoadedMode.Hard:
                        return DescribeActiveLayout("Hard", selectedMode, layout.Mode,
                            layout.UnsafeSections.Count);
                    default:
                        return selectedMode == LoadedMode.Unavailable
                            ? "waiting for the world layout"
                            : selectedMode + " selected; waiting for the world layout";
                }
            }
        }

        internal static bool HasLoadedLayout
        {
            get { return currentLayout.Mode != LoadedMode.Unavailable; }
        }

        internal static int UnsafeSectionCount
        {
            get { return currentLayout.UnsafeSections.Count; }
        }

        /// <summary>
        /// DoubleTrack leaves Hard-layout abandoned segments in the logical
        /// rail graph and deliberately creates Derail-* spurs. Neither is a
        /// safe route candidate even though its RailTrack remains connected.
        /// </summary>
        internal static bool IsUnsafeRouteSection(RailTrack track)
        {
            return track != null && currentLayout.UnsafeSections.Contains(track);
        }

        public static void RefreshForCurrentLayout()
        {
            LoadedMode mode = GetLoadedMode();
            LoadedMode selectedMode = GetSelectedMode();
            if (mode == LoadedMode.Unavailable)
            {
                currentLayout = LayoutSnapshot.Empty;
                return;
            }

            RailTrack[] tracks = UnityEngine.Object.FindObjectsOfType<RailTrack>();
            var doubleTrackSections = new HashSet<RailTrack>();
            var crossoverSections = new HashSet<RailTrack>();
            var candidates = new List<RailTrack>();

            foreach (RailTrack track in tracks)
            {
                if (IsDoubleTrack(track))
                    doubleTrackSections.Add(track);
                if (IsCrossover(track))
                    crossoverSections.Add(track);
            }

            // DoubleTrack exposes its runtime-created rails through this list.
            // It captures sections whose names were inherited from the base
            // game, which cannot be recognised safely from their names alone.
            foreach (RailTrack track in GetAddedTracks())
            {
                if (track != null)
                    doubleTrackSections.Add(track);
            }

            // Some Hard-layout "removed" sections are inactive RailTrack
            // objects that remain referenced by adjacent live tracks. Walk the
            // logical connections as well as active scene objects so those
            // hidden rails cannot slip through the route graph.
            var unsafeSeeds = new List<RailTrack>(tracks);
            if (RailTrackRegistryBase.RailTracks != null)
                unsafeSeeds.AddRange(RailTrackRegistryBase.RailTracks.Where(track => track != null));
            unsafeSeeds.AddRange(doubleTrackSections);
            HashSet<RailTrack> unsafeSections = FindUnsafeConnectedSections(unsafeSeeds);

            foreach (RailTrack track in doubleTrackSections)
            {
                if (track != null && track.curve != null)
                    candidates.Add(track);
            }

            // SetupCommands can run before DoubleTrack's railway-scene hook.
            // Do not publish or report a Normal/Hard layout until that hook has
            // actually created at least one known DoubleTrack rail. A later
            // route-planning refresh will populate the real layout.
            if (doubleTrackSections.Count == 0 && crossoverSections.Count == 0)
            {
                currentLayout = LayoutSnapshot.Empty;
                if (!waitingForLayoutLogged)
                {
                    waitingForLayoutLogged = true;
                    Module.mod?.Logger.Log("DoubleTrack " + selectedMode
                        + " mode detected; waiting for the railway world layout.");
                }
                return;
            }

            waitingForLayoutLogged = false;
            var preferredForward = BuildPreferredDirections(candidates);
            currentLayout = new LayoutSnapshot(mode, doubleTrackSections, crossoverSections,
                unsafeSections, preferredForward);
            LogPendingModeChange(mode, selectedMode);

            string description = mode + ":" + doubleTrackSections.Count + ":"
                + crossoverSections.Count + ":" + unsafeSections.Count + ":" + preferredForward.Count;
            if (description != lastLoggedLayout)
            {
                lastLoggedLayout = description;
                string behaviour = mode == LoadedMode.Normal
                    ? "right-hand-running lanes preferred"
                    : "hard-layout topology respected without normal-mode lane bias";
                Module.mod?.Logger.Log("DoubleTrack " + mode + " layout detected: " + doubleTrackSections.Count
                    + " generated sections, " + crossoverSections.Count + " crossovers, "
                    + unsafeSections.Count + " abandoned/derailment sections blocked, " + behaviour + ".");
            }
        }

        private static Dictionary<RailTrack, bool> BuildPreferredDirections(List<RailTrack> candidates)
        {
            var preferredForward = new Dictionary<RailTrack, bool>();
            for (int i = 0; i < candidates.Count; i++)
            {
                RailTrack first = candidates[i];
                Vector3 firstMid = first.curve.GetPointAt(0.5f);
                Vector3 firstTangent = Horizontal(first.curve.GetTangentAt(0.5f));
                for (int j = i + 1; j < candidates.Count; j++)
                {
                    RailTrack second = candidates[j];
                    Vector3 secondMid = second.curve.GetPointAt(0.5f);
                    float separation = Vector3.Distance(firstMid, secondMid);
                    if (separation < 2.5f || separation > 12f)
                        continue;

                    float alignment = Vector3.Dot(firstTangent, Horizontal(second.curve.GetTangentAt(0.5f)));
                    if (Mathf.Abs(alignment) < 0.85f)
                        continue;

                    Vector3 right = Vector3.Cross(Vector3.up, firstTangent).normalized;
                    bool secondIsRight = Vector3.Dot(secondMid - firstMid, right) > 0f;
                    preferredForward[first] = !secondIsRight;
                    preferredForward[second] = secondIsRight ? alignment > 0f : alignment <= 0f;
                    break;
                }
            }

            return preferredForward;
        }

        private static string DescribeActiveLayout(string activeMode, LoadedMode selectedMode,
            LoadedMode loadedMode, int unsafeSectionCount)
        {
            string safety = "; " + unsafeSectionCount + " abandoned/derailment sections blocked";
            if (selectedMode != LoadedMode.Unavailable && selectedMode != loadedMode)
                return activeMode + " layout active" + safety + "; " + selectedMode
                    + " selected - reload the world to apply";

            return activeMode + " layout active" + safety;
        }

        private static void LogPendingModeChange(LoadedMode loadedMode, LoadedMode selectedMode)
        {
            if (selectedMode == LoadedMode.Unavailable || selectedMode == loadedMode)
            {
                lastPendingMode = null;
                return;
            }

            string message = selectedMode + " selected while " + loadedMode
                + " rails are loaded. Revised DV Route will keep routing on the loaded layout until DoubleTrack reloads the world.";
            if (message == lastPendingMode)
                return;

            lastPendingMode = message;
            Module.mod?.Logger.Log("DoubleTrack: " + message);
        }

        private static LoadedMode GetLoadedMode()
        {
            return GetMode("LoadedTrackMode");
        }

        private static LoadedMode GetSelectedMode()
        {
            return GetMode("StaticTrackMode");
        }

        private static LoadedMode GetMode(string fieldName)
        {
            if (!IsActive)
                return LoadedMode.Unavailable;

            Type settingsType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("DoubleTrack.Settings", false))
                .FirstOrDefault(type => type != null);
            FieldInfo modeField = settingsType?.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            if (modeField == null)
            {
                LogUnavailable("Settings." + fieldName + " was not found");
                return LoadedMode.Unavailable;
            }

            object value = modeField.GetValue(null);
            if (value == null)
                return LoadedMode.Unavailable;

            return string.Equals(value.ToString(), "Hard", StringComparison.OrdinalIgnoreCase)
                ? LoadedMode.Hard
                : LoadedMode.Normal;
        }

        private static IEnumerable<RailTrack> GetAddedTracks()
        {
            Type tracksPatchType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("DoubleTrack.AllTracksPatch", false))
                .FirstOrDefault(type => type != null);
            FieldInfo addedTracks = tracksPatchType?.GetField("AddedTracks", BindingFlags.Public | BindingFlags.Static);
            IEnumerable values = addedTracks?.GetValue(null) as IEnumerable;
            if (values == null)
                yield break;

            foreach (object value in values)
            {
                RailTrack track = value as RailTrack;
                if (track != null)
                    yield return track;
            }
        }

        private static bool IsActive
        {
            get
            {
                return UnityModManager.modEntries != null
                    && UnityModManager.modEntries.Any(entry => entry != null
                        && entry.Active
                        && string.Equals(entry.Info?.Id, DoubleTrackModId, StringComparison.OrdinalIgnoreCase));
            }
        }

        private static void LogUnavailable(string reason)
        {
            if (apiUnavailableLogged)
                return;

            apiUnavailableLogged = true;
            Module.mod?.Logger.Log("DoubleTrack is active but its runtime layout API is unavailable (" + reason + "). Routing will use the base rail graph.");
        }

        private static bool IsDoubleTrack(RailTrack track)
        {
            string name = track != null ? track.name ?? string.Empty : string.Empty;
            return name.IndexOf("DoubleTrack", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("DT-", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsCrossover(RailTrack track)
        {
            string name = track != null ? track.name ?? string.Empty : string.Empty;
            return name.IndexOf("CX", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Crossover", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Cross", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsUnsafeSectionName(RailTrack track)
        {
            string name = track != null ? track.name ?? string.Empty : string.Empty;
            return name.IndexOf("Abandoned", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Derail-", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static HashSet<RailTrack> FindUnsafeConnectedSections(IEnumerable<RailTrack> seeds)
        {
            var unsafeSections = new HashSet<RailTrack>();
            var visited = new HashSet<RailTrack>();
            var pending = new Queue<RailTrack>();

            foreach (RailTrack seed in seeds)
            {
                if (seed != null && visited.Add(seed))
                    pending.Enqueue(seed);
            }

            while (pending.Count > 0)
            {
                RailTrack track = pending.Dequeue();
                if (IsUnsafeSectionName(track))
                    unsafeSections.Add(track);

                if (track.inIsConnected)
                    EnqueueBranches(track.GetAllInBranches(), visited, pending);
                if (track.outIsConnected)
                    EnqueueBranches(track.GetAllOutBranches(), visited, pending);
            }

            return unsafeSections;
        }

        private static void EnqueueBranches(IEnumerable<Junction.Branch> branches,
            HashSet<RailTrack> visited, Queue<RailTrack> pending)
        {
            if (branches == null)
                return;

            foreach (Junction.Branch branch in branches)
            {
                RailTrack track = branch?.track;
                if (track != null && visited.Add(track))
                    pending.Enqueue(track);
            }
        }

        private static Vector3 Horizontal(Vector3 vector)
        {
            vector.y = 0f;
            return vector.sqrMagnitude > Mathf.Epsilon ? vector.normalized : Vector3.forward;
        }
    }
}
