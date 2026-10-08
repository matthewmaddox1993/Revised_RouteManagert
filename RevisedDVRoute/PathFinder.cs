using CommandTerminal;
using DV.Logic.Job;
using Priority_Queue;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using UnityAsync;
using UnityEngine;
using UnityModManagerNet;

namespace RevisedDVRoute
{

    // Prepare for loops support
    /*
    public class MultiKeyDictionary<K1, K2, V> : Dictionary<K1, Dictionary<K2, V>>
    {

        public V this[K1 key1, K2 key2]
        {
            get
            {
                if (!ContainsKey(key1) || !this[key1].ContainsKey(key2))
                    throw new ArgumentOutOfRangeException();
                return base[key1][key2];
            }
            set
            {
                if (!ContainsKey(key1))
                    this[key1] = new Dictionary<K2, V>();
                this[key1][key2] = value;
            }
        }

        public void Add(K1 key1, K2 key2, V value)
        {
            if (!ContainsKey(key1))
                this[key1] = new Dictionary<K2, V>();
            this[key1][key2] = value;
        }

        public bool ContainsKey(K1 key1, K2 key2)
        {
            return base.ContainsKey(key1) && this[key1].ContainsKey(key2);
        }

        public new IEnumerable<V> Values
        {
            get
            {
                return from baseDict in base.Values
                       from baseKey in baseDict.Keys
                       select baseDict[baseKey];
            }
        }

    }

    class NodeInfo
    {
        private Dictionary<RailTrack, double> costSoFar;

        public bool HasCameFrom(RailTrack from)
        {
            return costSoFar.ContainsKey(from);
        }
    }
    */

    public class TrackTransition
    {
        public RailTrack track;
        public RailTrack nextTrack;
    }

    public class PathFinder
    {
        private RailTrack start;
        private RailTrack goal;
        private int penalizedSignalReservedTracks;
        private int penalizedAITrafficReservedTracks;
        private int skippedUnsafeDoubleTrackSections;
        private int skippedOccupiedTracks;
        private int skippedBannedTransitions;
        private int skippedDirectionChanges;
        private int skippedShortReversals;
        private Dictionary<SearchState, SearchState> cameFrom;
        private Dictionary<SearchState, double> costSoFar;
        private SearchState startState;

        // ── Turntable cache ──────────────────────────────────────────────────
        // Maps spur RailTrack → the TurntableRailTrack whose rim it touches.
        // Also maps the turntable's own track → its TurntableRailTrack.
        // Rebuilt for each route on the main thread, then read on the background
        // worker. This avoids retaining an empty bootstrap-scene cache.
        private static Dictionary<RailTrack, TurntableRailTrack> _spurToTurntable;
        public  static Dictionary<RailTrack, TurntableRailTrack> _turntableTrackToTRT;

        public static void BuildTurntableCache()
        {
            _spurToTurntable    = new Dictionary<RailTrack, TurntableRailTrack>();
            _turntableTrackToTRT = new Dictionary<RailTrack, TurntableRailTrack>();

            foreach (var trt in UnityEngine.Object.FindObjectsOfType<TurntableRailTrack>())
            {
                if (trt == null || trt.trackEnds == null) continue;

                var ttTrack = trt.Track;
                if (ttTrack != null && !_turntableTrackToTRT.ContainsKey(ttTrack))
                    _turntableTrackToTRT[ttTrack] = trt;

                foreach (var te in trt.trackEnds)
                {
                    if (te?.track != null && !_spurToTurntable.ContainsKey(te.track))
                        _spurToTurntable[te.track] = trt;
                }
            }

            Terminal.Log($"TurntableCache: {_turntableTrackToTRT.Count} turntables, {_spurToTurntable.Count} spurs");
        }
        // ─────────────────────────────────────────────────────────────────────

        // Heuristic that computes approximate distance between two rails
        protected double Heuristic(RailTrack a, RailTrack b)
        {
            return (a.transform.position - b.transform.position).sqrMagnitude; //we dont need exact distance because that result is used only as a priority
        }


        public PathFinder(Track start, Track goal)
        {
            Terminal.Log($"{start.ID.FullID} -> {goal.ID.FullID}");

            RailTrack startTrack = RailTrackRegistryBase.RailTracks.FirstOrDefault((RailTrack track) => track?.LogicTrack().ID.FullID == start.ID.FullID);
            RailTrack goalTrack = RailTrackRegistryBase.RailTracks.FirstOrDefault((RailTrack track) => track?.LogicTrack().ID.FullID == goal.ID.FullID);

            if (startTrack == null || goalTrack == null)
            {
                Terminal.Log("start track or goal track not found");
                return;
            }

            this.start = startTrack;
            this.goal = goalTrack;
        }

        public PathFinder(RailTrack start, RailTrack goal)
        {
            this.start = start;
            this.goal = goal;
        }
        private sealed class SearchState : IEquatable<SearchState>
        {
            public readonly RailTrack Track;
            public readonly RailTrack Previous;

            public SearchState(RailTrack track, RailTrack previous)
            {
                Track = track;
                Previous = previous;
            }

            public bool Equals(SearchState other)
            {
                return other != null
                    && ReferenceEquals(Track, other.Track)
                    && ReferenceEquals(Previous, other.Previous);
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as SearchState);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (RuntimeHelpers.GetHashCode(Track) * 397)
                        ^ (Previous == null ? 0 : RuntimeHelpers.GetHashCode(Previous));
                }
            }
        }

        private sealed class RailTrackNode : GenericPriorityQueueNode<double>
        {
            public readonly SearchState State;
            public readonly double CostAtEnqueue;

            public RailTrackNode(SearchState state, double costAtEnqueue)
            {
                State = state;
                CostAtEnqueue = costAtEnqueue;
            }
        }
        /// <summary>
        /// A* search
        /// </summary>
        /// <param name="allowReverse"></param>
        /// <param name="carsToIgnore"></param>
        /// <param name="consistLength"></param>
        private async System.Threading.Tasks.Task<SearchState> Astar(bool allowReverse,
            HashSet<string> carsToIgnore, double consistLength, List<TrackTransition> bannedTransitions)
        {
            // Snapshot the yard organizer reference on the main thread before
            // going background. Its reservation dictionary changes only during
            // main-thread job generation.
            YardTracksOrganizer yardOrganizer = UnityEngine.Object.FindObjectOfType<YardTracksOrganizer>();

            await Await.BackgroundSyncContext();

            cameFrom = new Dictionary<SearchState, SearchState>();
            costSoFar = new Dictionary<SearchState, double>();
            var queue = new GenericPriorityQueue<RailTrackNode, double>(30000);

            startState = new SearchState(start, null);
            cameFrom.Add(startState, startState);
            costSoFar.Add(startState, 0.0);
            queue.Enqueue(new RailTrackNode(startState, 0.0), 0.0);

            SearchState bestGoal = null;
            double bestGoalCost = double.MaxValue;

            while (queue.Count > 0)
            {
                RailTrackNode queuedNode = queue.Dequeue();
                SearchState currentState = queuedNode.State;
                double currentCost;
                if (!costSoFar.TryGetValue(currentState, out currentCost)
                    || queuedNode.CostAtEnqueue > currentCost + 0.0001)
                {
                    continue;
                }

                RailTrack current = currentState.Track;
                RailTrack prev = currentState.Previous;
                if (current == goal)
                {
                    if (currentCost < bestGoalCost)
                    {
                        bestGoal = currentState;
                        bestGoalCost = currentCost;
                    }
                    continue;
                }

                List<RailTrack> neighbors = new List<RailTrack>();
                HashSet<RailTrack> turntableDirect = new HashSet<RailTrack>();

                TurntableRailTrack currentAsTurntable;
                if (_turntableTrackToTRT != null
                    && _turntableTrackToTRT.TryGetValue(current, out currentAsTurntable))
                {
                    foreach (var trackEnd in currentAsTurntable.trackEnds)
                    {
                        if (trackEnd?.track == null)
                            continue;
                        neighbors.Add(trackEnd.track);
                        turntableDirect.Add(trackEnd.track);
                    }
                }
                else
                {
                    if (current.outIsConnected)
                    {
                        neighbors.AddRange(current.GetAllOutBranches()
                            .Where(branch => branch != null)
                            .Select(branch => branch.track)
                            .Where(track => track != null));
                    }

                    if (current.inIsConnected)
                    {
                        neighbors.AddRange(current.GetAllInBranches()
                            .Where(branch => branch != null)
                            .Select(branch => branch.track)
                            .Where(track => track != null));
                    }

                    TurntableRailTrack adjacentTurntable;
                    if (_spurToTurntable != null
                        && _spurToTurntable.TryGetValue(current, out adjacentTurntable)
                        && adjacentTurntable?.Track != null
                        && !neighbors.Contains(adjacentTurntable.Track))
                    {
                        neighbors.Add(adjacentTurntable.Track);
                        turntableDirect.Add(adjacentTurntable.Track);
                    }
                }

#if DEBUG2
                Terminal.Log("ID: " + current.LogicTrack().ID.FullID
                    + " Prev: " + prev?.LogicTrack().ID.FullID
                    + "\nall branches: " + DumpNodes(neighbors, current));
#endif

                foreach (RailTrack neighbor in neighbors.Distinct())
                {
                    if (neighbor == null)
                        continue;

                    Track neighborLogic = neighbor.LogicTrack();
                    if (neighborLogic == null)
                        continue;

                    if (Compatibility.DoubleTrackCompatibility.IsUnsafeRouteSection(neighbor))
                    {
                        skippedUnsafeDoubleTrackSections++;
                        continue;
                    }

                    if (bannedTransitions != null
                        && bannedTransitions.Any(transition => transition.track == current
                            && transition.nextTrack == neighbor))
                    {
                        skippedBannedTransitions++;
                        continue;
                    }

                    bool neighborSignalReserved = neighbor != start && neighbor != goal
                        && Compatibility.DVSignalsCompatibility.IsReservedInSnapshot(neighbor);
                    bool neighborAITrafficReserved = neighbor != start && neighbor != goal
                        && Compatibility.AITrafficCompatibility.IsReservedInSnapshot(neighbor);

                    if (neighbor != start && neighbor != goal && !neighborLogic.IsFree(carsToIgnore))
                    {
                        skippedOccupiedTracks++;
                        continue;
                    }

                    bool isDirect = turntableDirect.Contains(neighbor)
                        || current.CanGoToDirectly(prev, neighbor);
                    if (!allowReverse && !isDirect)
                    {
                        skippedDirectionChanges++;
                        continue;
                    }

                    if (!isDirect && prev != null && !current.IsDirectLengthEnough(prev, consistLength))
                    {
                        skippedShortReversals++;
                        continue;
                    }

                    double edgeCost = neighborLogic.length / neighbor.GetAverageSpeed();
                    edgeCost = Compatibility.DoubleTrackCompatibility.AdjustRouteCost(
                        prev, current, neighbor, edgeCost);
                    double newCost = currentCost + edgeCost;

                    // The two mods can describe the same protected section.
                    // Apply one penalty while retaining separate diagnostics.
                    if (neighborSignalReserved || neighborAITrafficReserved)
                        newCost += 5000.0;
                    if (neighborSignalReserved)
                        penalizedSignalReservedTracks++;
                    if (neighborAITrafficReserved)
                        penalizedAITrafficReservedTracks++;

                    if (neighbor != start && neighbor != goal && yardOrganizer != null
                        && yardOrganizer.IsTrackManagedByOrganizer(neighborLogic))
                    {
                        double reserved = yardOrganizer.GetReservedSpace(neighborLogic);
                        newCost += reserved > 40.5 || !neighborLogic.IsFree() ? 5000.0 : 300.0;
                    }

                    var neighborState = new SearchState(neighbor, current);
                    double previousCost;
                    if (!costSoFar.TryGetValue(neighborState, out previousCost) || newCost < previousCost)
                    {
                        costSoFar[neighborState] = newCost;
                        cameFrom[neighborState] = currentState;
                        double priority = newCost + Heuristic(neighbor, goal) / 20.0f;
                        queue.Enqueue(new RailTrackNode(neighborState, newCost), priority);
                    }
                }
            }

            await Await.UnitySyncContext();
            return bestGoal;
        }

        private static string DumpNodes(List<RailTrack> neighbors, RailTrack parent)
        {
            return "[" + neighbors.Select(
                t =>
                {
                    string prefix = "NC";
                    
                    if(parent.outJunction != null)
                    {
                        if (parent.outJunction.inBranch.track == t)
                            prefix = "OJin";
                        if (parent.outJunction.outBranches.Any(b => b.track == t))
                            prefix = "OJout";
                    }
                    else if (parent.outIsConnected && parent.outBranch.track == t)
                    {
                        prefix = "OB";
                    }

                    if (parent.inJunction != null)
                    {
                        if (parent.inJunction.inBranch.track == t)
                            prefix = "IJin";
                        if (parent.inJunction.outBranches.Any(b => b.track == t))
                            prefix = "IJout";
                    }
                    else if (parent.inIsConnected && parent.inBranch.track == t)
                    {
                        prefix = "IB";
                    }

                    prefix += ":";
                    return prefix + t.LogicTrack().ID.FullID;
                })
                .Aggregate(string.Empty, (a, b) =>
                {
                    return a + "|" + b;
                })
                + "]";
        }

        // Return a List of Locations representing the found path
        public async Task<List<RailTrack>> FindPath(bool allowReverse, double consistLength,
            List<TrackTransition> bannedTransitions, Trainset trainset)
        {
            List<RailTrack> path = new List<RailTrack>();

            if (start == null || goal == null)
                return null;

            // Build from the currently loaded world before going async.
            BuildTurntableCache();

            // DoubleTrack creates a different topology in Normal and Hard
            // modes. Snapshot the layout that is actually loaded before A*.
            Compatibility.DoubleTrackCompatibility.RefreshForCurrentLayout();

            // The DV Signals API is Unity-thread-only.  The compatibility
            // layer snapshots its reservations here for the A* worker to read.
            Compatibility.DVSignalsCompatibility.RefreshReservationSnapshot();
            Compatibility.AITrafficCompatibility.RefreshReservationSnapshot();
            penalizedSignalReservedTracks = 0;
            penalizedAITrafficReservedTracks = 0;
            skippedUnsafeDoubleTrackSections = 0;
            skippedOccupiedTracks = 0;
            skippedBannedTransitions = 0;
            skippedDirectionChanges = 0;
            skippedShortReversals = 0;

            Module.mod?.Logger.Log("Route search snapshot: "
                + Compatibility.DoubleTrackCompatibility.StatusDescription
                + "; DV Signals reserved tracks=" + Compatibility.DVSignalsCompatibility.ReservationCount
                + "; AI Traffic reserved tracks=" + Compatibility.AITrafficCompatibility.ReservationCount
                + "; DoubleTrack hazards blocked=" + Compatibility.DoubleTrackCompatibility.UnsafeSectionCount
                + "; reversals=" + (allowReverse ? "allowed" : "not allowed") + ".");

            HashSet<string> carsToIgnore = new HashSet<string>();

            if (trainset != null)
            {
                trainset.cars.ForEach(car => carsToIgnore.Add(car.logicCar.ID));
            }
            else if (PlayerManager.LastLoco != null)
            {
                PlayerManager.LastLoco.trainset.cars.ForEach(c => carsToIgnore.Add(c.logicCar.ID));
            }

            SearchState goalState = await Astar(
                allowReverse, carsToIgnore, consistLength, bannedTransitions);

            if (penalizedSignalReservedTracks > 0)
                Terminal.Log("DV Signals: strongly discouraged " + penalizedSignalReservedTracks
                    + " reserved track candidate(s), while retaining them for single-corridor routes.");
            if (penalizedAITrafficReservedTracks > 0)
                Terminal.Log("AI Traffic: strongly discouraged " + penalizedAITrafficReservedTracks
                    + " reserved track candidate(s), while retaining them for single-corridor routes.");

            if (goalState == null)
            {
                Module.mod?.Logger.Log("Route search failed: " + start.LogicTrack().ID.FullID
                    + " -> " + goal.LogicTrack().ID.FullID
                    + "; DoubleTrack hazard candidates=" + skippedUnsafeDoubleTrackSections
                    + "; occupied candidates=" + skippedOccupiedTracks
                    + "; excluded direction=" + skippedBannedTransitions
                    + "; reversal-required candidates=" + skippedDirectionChanges
                    + "; too-short reversal candidates=" + skippedShortReversals + ".");
                return null;
            }

            SearchState currentState = goalState;
            while (!currentState.Equals(startState))
            {
                path.Add(currentState.Track);
                SearchState parent;
                if (!cameFrom.TryGetValue(currentState, out parent))
                {
                    Module.mod?.Logger.Log("Route reconstruction failed at "
                        + currentState.Track.LogicTrack().ID.FullID + ".");
                    return null;
                }
                currentState = parent;
            }

            path.Add(start);
            path.Reverse();

            Module.mod?.Logger.Log("Route selected: " + start.LogicTrack().ID.FullID
                + " -> " + goal.LogicTrack().ID.FullID + "; tracks=" + path.Count
                + "; initial next=" + (path.Count > 1 ? path[1].LogicTrack().ID.FullID : "none")
                + "; DoubleTrack hazard candidates rejected=" + skippedUnsafeDoubleTrackSections
                + "; DV Signals penalized candidates=" + penalizedSignalReservedTracks
                + "; AI Traffic penalized candidates=" + penalizedAITrafficReservedTracks + ".");

            return path;
        }
    }
}
