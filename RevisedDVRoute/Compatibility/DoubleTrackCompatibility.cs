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
                new Dictionary<RailTrack, bool>(), null);

            public readonly LoadedMode Mode;
            public readonly HashSet<RailTrack> DoubleTrackSections;
            public readonly HashSet<RailTrack> CrossoverSections;
            public readonly HashSet<RailTrack> UnsafeSections;
            public readonly Dictionary<RailTrack, bool> PreferredForward;
            public readonly RailTrack SampleTrack;

            public LayoutSnapshot(LoadedMode mode, HashSet<RailTrack> doubleTrackSections,
                HashSet<RailTrack> crossoverSections, HashSet<RailTrack> unsafeSections,
                Dictionary<RailTrack, bool> preferredForward, RailTrack sampleTrack = null)
            {
                Mode = mode;
                DoubleTrackSections = doubleTrackSections;
                CrossoverSections = crossoverSections;
                UnsafeSections = unsafeSections;
                PreferredForward = preferredForward;
                SampleTrack = sampleTrack ?? doubleTrackSections?.FirstOrDefault(t => t != null)
                    ?? crossoverSections?.FirstOrDefault(t => t != null);
            }
        }

        // The path finder reads this immutable snapshot during its
        // frame-yielding main-thread search. It is rebuilt before each route.
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

        internal static bool IsLayoutValid()
        {
            LayoutSnapshot layout = currentLayout;
            if (layout == null || layout.Mode == LoadedMode.Unavailable)
                return false;

            if (layout.SampleTrack == null)
                return false;

            try
            {
                var reg = RailTrackRegistryBase.RailTracks;
                if (reg == null || reg.Length == 0)
                    return false;
            }
            catch (Exception)
            {
                return false;
            }

            return true;
        }

        public static void RefreshForCurrentLayout(bool force = false)
        {
            LoadedMode mode = GetLoadedMode();
            LoadedMode selectedMode = GetSelectedMode();
            if (mode == LoadedMode.Unavailable)
            {
                currentLayout = LayoutSnapshot.Empty;
                return;
            }

            if (!force && HasLoadedLayout && currentLayout.Mode == mode && IsLayoutValid())
            {
                LogPendingModeChange(mode, selectedMode);
                return;
            }

            RailTrack[] registeredTracks;
            try
            {
                // RailTrackRegistryBase throws while the game is still on the
                // main menu. Treat that expected startup state as "not loaded"
                // so the compatibility monitor remains alive for the world load.
                registeredTracks = RailTrackRegistryBase.RailTracks?
                    .Where(track => track != null)
                    .ToArray() ?? new RailTrack[0];
            }
            catch (Exception)
            {
                currentLayout = LayoutSnapshot.Empty;
                return;
            }

            if (registeredTracks.Length == 0)
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
            unsafeSeeds.AddRange(registeredTracks);
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

        private struct CandidateInfo
        {
            public RailTrack Track;
            public Vector3 Mid;
            public Vector3 Tangent;
        }

        private static Dictionary<RailTrack, bool> BuildPreferredDirections(List<RailTrack> candidates)
        {
            var preferredForward = new Dictionary<RailTrack, bool>();
            if (candidates == null || candidates.Count == 0)
                return preferredForward;

            var info = new List<CandidateInfo>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                RailTrack track = candidates[i];
                if (track != null && track.curve != null)
                {
                    info.Add(new CandidateInfo
                    {
                        Track = track,
                        Mid = track.curve.GetPointAt(0.5f),
                        Tangent = Horizontal(track.curve.GetTangentAt(0.5f))
                    });
                }
            }

            const float cellSize = 25f;
            var grid = new Dictionary<long, List<int>>();
            for (int i = 0; i < info.Count; i++)
            {
                Vector3 mid = info[i].Mid;
                int cx = Mathf.FloorToInt(mid.x / cellSize);
                int cz = Mathf.FloorToInt(mid.z / cellSize);
                long key = ((long)cx << 32) | (uint)cz;
                List<int> cell;
                if (!grid.TryGetValue(key, out cell))
                {
                    cell = new List<int>();
                    grid[key] = cell;
                }
                cell.Add(i);
            }

            for (int i = 0; i < info.Count; i++)
            {
                CandidateInfo first = info[i];
                if (preferredForward.ContainsKey(first.Track))
                    continue;

                int cx = Mathf.FloorToInt(first.Mid.x / cellSize);
                int cz = Mathf.FloorToInt(first.Mid.z / cellSize);

                bool matched = false;
                for (int dx = -1; dx <= 1 && !matched; dx++)
                {
                    for (int dz = -1; dz <= 1 && !matched; dz++)
                    {
                        long key = ((long)(cx + dx) << 32) | (uint)(cz + dz);
                        List<int> cell;
                        if (!grid.TryGetValue(key, out cell))
                            continue;

                        for (int k = 0; k < cell.Count; k++)
                        {
                            int j = cell[k];
                            if (j <= i)
                                continue;

                            CandidateInfo second = info[j];
                            float separation = Vector3.Distance(first.Mid, second.Mid);
                            if (separation < 2.5f || separation > 12f)
                                continue;

                            float alignment = Vector3.Dot(first.Tangent, second.Tangent);
                            if (Mathf.Abs(alignment) < 0.85f)
                                continue;

                            Vector3 right = Vector3.Cross(Vector3.up, first.Tangent).normalized;
                            bool secondIsRight = Vector3.Dot(second.Mid - first.Mid, right) > 0f;
                            preferredForward[first.Track] = !secondIsRight;
                            preferredForward[second.Track] = secondIsRight ? alignment > 0f : alignment <= 0f;
                            matched = true;
                            break;
                        }
                    }
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

        internal static bool IsActive
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
