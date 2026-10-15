using CommandTerminal;
using DV.Logic.Job;
using RevisedDVRoute.Extensions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace RevisedDVRoute
{
    public enum ReversingStrategy
    {
        NoReversing,
        OnlyIfNeeded,
        ChooseBest
    }

    public class Route
    {
        public const double REVERSE_SECTOR_LENGTH = 10.0;
        private const double SWITCH_ALIGNMENT_LOOKAHEAD_METERS = 500.0;

        public List<RailTrack> Path { get; }
        public Track Destination { get; }
        public double Length { get; }

        public Dictionary<string, Junction> Reverses { get; } = new Dictionary<string, Junction>();

        public RailTrack FirstTrack { get => Path.FirstOrDefault(); }
        public RailTrack SecondTrack { get => Path.Skip(1).FirstOrDefault(); }
        public RailTrack LastTrack { get => Path.LastOrDefault(); }

        public Trainset Trainset { get; private set; }

        public Route(List<RailTrack> path, Track destination, Trainset trainset)
            : this(path, destination)
        {
            Trainset = trainset;
        }
        public Route(List<RailTrack> path, Track destination)
        {
            this.Path = path ?? throw new ArgumentNullException(nameof(path));
            this.Destination = destination ?? throw new ArgumentNullException(nameof(destination));

            IEnumerator<RailTrack> enumerator = Path.GetEnumerator();

            double length = 0.0;

            WalkPath((walkData) =>
            {
                Junction reversingJunction;

                if (walkData.nextTrack != null && !walkData.currentTrack.CanGoToDirectly(walkData.prevTrack, walkData.nextTrack, out reversingJunction))
                {
                    if (reversingJunction != null)
                    {
                        Reverses.Add(walkData.junctionId, reversingJunction);
                        Terminal.Log($"Reversing needed on junction {walkData.junctionId}");
                    }
                }

                length = walkData.distanceFromStart;

                return true;
            });

            Length = length;
        }

        public override string ToString()
        {
            return $"Route to {Destination.ID.FullDisplayID} {Length} length";
        }

        public string StartHeading
        {
            get
            {
                if (Path.Count < 2)
                    return "??";

                Vector2 v1 = new Vector2(Path[0].transform.position.x, Path[0].transform.position.z);
                Vector2 v2 = new Vector2(Path[1].transform.position.x, Path[1].transform.position.z);

                var v = (v2 - v1);

                if (Vector2.Angle(v, Vector2.up) <= 45.0)
                {
                    return "N";
                }
                else if (Vector2.Angle(v, Vector2.right) <= 45.0)
                {
                    return "E";
                }
                else if (Vector2.Angle(v, Vector2.down) <= 45.0)
                {
                    return "S";
                }


                return "W";

            }
        }

        public static string GetJunctionId(RailTrack prevTrack, RailTrack track, RailTrack nextTrack)
        {
            return $"{prevTrack?.LogicTrack().ID.FullID}->{track?.LogicTrack().ID.FullID}->{nextTrack?.LogicTrack().ID.FullID}";
        }

        public class WalkPathData
        {
            public RailTrack prevTrack;
            public RailTrack currentTrack;
            public RailTrack nextTrack;
            public string junctionId;
            public int pathIndex;
            public double distanceFromStart;
        }

        /// <summary>
        /// Goes throuh whole path and calls callback with current, previous and next track
        /// </summary>
        /// <param name="callback"></param>
        public void WalkPath(Func<WalkPathData, bool> callback)
        {
            IEnumerator<RailTrack> enumerator = Path.GetEnumerator();

            WalkPathData walkData = new WalkPathData();
            walkData.pathIndex = -1;
            walkData.distanceFromStart = 0.0;

            if (enumerator.MoveNext())
            {
                RailTrack prevTrack = null;

                while (enumerator.Current != null)
                {
                    var track = enumerator.Current;

#if DEBUG2
                    Terminal.Log($"Track ID: {track.LogicTrack().ID.FullID}");
#endif

                    RailTrack nextTrack = null;

                    if (enumerator.MoveNext())
                        nextTrack = enumerator.Current;

                    string junctionId = GetJunctionId(prevTrack, track, nextTrack);


                    walkData.prevTrack = prevTrack;
                    walkData.currentTrack = track;
                    walkData.nextTrack = nextTrack;
                    walkData.junctionId = junctionId;
                    walkData.pathIndex++;

                    if (!callback(walkData))
                    {
                        break;
                    }

                    if (nextTrack == null)
                        break;

                    if (Reverses.ContainsKey(junctionId))
                    {
                        walkData.distanceFromStart += REVERSE_SECTOR_LENGTH;
                    }
                    else
                    {
                        walkData.distanceFromStart += track.LogicTrack().length;
                    }

                    prevTrack = track;
                }
            }
        }

        public void AdjustSwitches()
        {
            AdjustSwitches(true);
        }

        internal void AdjustSwitchesForSignalReservation(int startPathIndex = 0)
        {
            if (startPathIndex > 0)
            {
                double dist = 0.0;
                for (int i = 0; i < startPathIndex && i < Path.Count; i++)
                    dist += Path[i]?.LogicTrack()?.length ?? 0.0;
                AdjustSwitchesAhead(startPathIndex, dist);
            }
            else
            {
                AdjustSwitchesAhead(0, 0.0);
            }
        }

        /// <summary>
        /// Re-aligns switches which are still ahead of the train.  A route is
        /// normally aligned when it is created, but another train (or a manual
        /// switch) can change a junction afterwards.  Only future path
        /// transitions are considered so a switch behind the train is never
        /// changed while the consist may still be clearing it.
        /// </summary>
        internal void AdjustSwitchesAhead(int currentPathIndex, double currentPosition)
        {
            if (currentPathIndex < 0)
                return;

            // A reversing or looping route can encounter the same junction
            // more than once with a different desired branch. Aligning every
            // occurrence in one pass makes the junction switch away and then
            // immediately back, which also makes DV Signals alternate its
            // aspect continuously. The first remaining occurrence is the one
            // the train will reach next; a later tracker pass will handle any
            // subsequent occurrence after the train advances.
            var handledJunctions = new HashSet<Junction>();

            WalkPath((walkData) =>
            {
                if (walkData.pathIndex < currentPathIndex)
                    return true;

                if (walkData.nextTrack == null)
                    return false;

                double transitionPosition = walkData.distanceFromStart
                    + walkData.currentTrack.LogicTrack().length;

                // The whole route is aligned once when it is created. Runtime
                // repair only needs a bounded look-ahead, avoiding a complete
                // scan of a long route twice per second and leaving distant
                // junctions available to other traffic until they are needed.
                if (transitionPosition > currentPosition + SWITCH_ALIGNMENT_LOOKAHEAD_METERS)
                    return false;

                // The junction is already behind the leading end of the
                // consist.  Do not change it even if another train has since
                // left it in a different position.
                if (transitionPosition < currentPosition - 1.0)
                    return true;

                Junction junction = GetJunctionBetween(walkData.currentTrack, walkData.nextTrack);
                if (junction != null
                    && handledJunctions.Add(junction)
                    && junction.IsFree())
                    SwitchJunctionIfNeeded(walkData.currentTrack, walkData.nextTrack, junction);

                return true;
            });
        }

        /// <summary>
        /// Returns whether the selected junctions currently allow the route's
        /// next transition.  This is also used after a live switch correction
        /// so the tracker does not briefly report WrongHeading from a stale
        /// cached track-next value.
        /// </summary>
        internal bool IsTransitionAligned(WalkPathData walkData)
        {
            if (walkData == null || walkData.currentTrack == null || walkData.nextTrack == null)
                return true;

            Junction junction = GetJunctionBetween(walkData.currentTrack, walkData.nextTrack);
            if (junction != null)
            {
                if (junction.inBranch == null || junction.outBranches == null)
                    return false;

                if (junction.selectedBranch < 0 || junction.selectedBranch >= junction.outBranches.Count)
                    return false;

                RailTrack selectedTrack = junction.outBranches[junction.selectedBranch].track;
                if (junction.inBranch.track == walkData.currentTrack)
                    return selectedTrack == walkData.nextTrack;

                if (junction.inBranch.track == walkData.nextTrack)
                    return selectedTrack == walkData.currentTrack;

                return false;
            }

            if (walkData.prevTrack != null)
            {
                RailTrack selectedNext = walkData.currentTrack.GetNextFromPrev(walkData.prevTrack);
                if (selectedNext != null)
                    return selectedNext == walkData.nextTrack;
            }

            return walkData.currentTrack.GetOutTrack() == walkData.nextTrack
                || walkData.currentTrack.GetInTrack() == walkData.nextTrack;
        }

        private static Junction GetJunctionBetween(RailTrack currentTrack, RailTrack nextTrack)
        {
            Junction[] candidates =
            {
                currentTrack?.inJunction,
                currentTrack?.outJunction,
                nextTrack?.inJunction,
                nextTrack?.outJunction
            };

            foreach (Junction junction in candidates)
            {
                if (junction == null || junction.inBranch == null || junction.outBranches == null)
                    continue;

                bool currentIsInBranch = junction.inBranch.track == currentTrack;
                bool nextIsInBranch = junction.inBranch.track == nextTrack;
                bool currentIsOutBranch = junction.outBranches.Any(branch => branch.track == currentTrack);
                bool nextIsOutBranch = junction.outBranches.Any(branch => branch.track == nextTrack);

                if ((currentIsInBranch && nextIsOutBranch)
                    || (nextIsInBranch && currentIsOutBranch))
                    return junction;
            }

            return null;
        }

        private void AdjustSwitches(bool beginSignalReservation)
        {
            int count = 0;

            HashSet<Junction> junctionsForReversing = new HashSet<Junction>();
            HashSet<Junction> handledJunctions = new HashSet<Junction>();

            WalkPath((walkData) =>
            {
                Junction reversingJunction = null;

                Reverses.TryGetValue(walkData.junctionId, out reversingJunction);


                if (walkData.currentTrack.inJunction != null && walkData.prevTrack != null)
                {
                    string branches = "[" + walkData.currentTrack.inJunction.outBranches.Select(b => b.track.LogicTrack().ID.FullID).Aggregate((a, b) => a + "|" + b) + "]";
#if DEBUG
                        Terminal.Log($"InJunction track: {walkData.currentTrack.LogicTrack().ID.FullID} nexttrack {walkData.nextTrack.LogicTrack().ID.FullID} inbranch {walkData.currentTrack.inJunction.inBranch.track.LogicTrack().ID.FullID} outbranches {branches} selectedBranch {walkData.currentTrack.inJunction.selectedBranch}");
#endif
                    if (handledJunctions.Add(walkData.currentTrack.inJunction) && !junctionsForReversing.Contains(walkData.currentTrack.inJunction) && SwitchJunctionIfNeeded(walkData.currentTrack, walkData.prevTrack, walkData.currentTrack.inJunction))
                    {
                        count++;
                    }
                }

                if (walkData.currentTrack.outJunction != null && walkData.nextTrack != null)
                {
                    string branches = "[" + walkData.currentTrack.outJunction.outBranches.Select(b => b.track.LogicTrack().ID.FullID).Aggregate((a, b) => a + "|" + b) + "]";
#if DEBUG
                        Terminal.Log($"OutJunction track: {walkData.currentTrack.LogicTrack().ID.FullID} nexttrack {walkData.nextTrack.LogicTrack().ID.FullID} inbranch {walkData.currentTrack.outJunction.inBranch.track.LogicTrack().ID.FullID} outbranches {branches} selectedBranch {walkData.currentTrack.outJunction.selectedBranch}");
#endif
                    if (handledJunctions.Add(walkData.currentTrack.outJunction) && !junctionsForReversing.Contains(walkData.currentTrack.outJunction) && SwitchJunctionIfNeeded(walkData.currentTrack, walkData.nextTrack, walkData.currentTrack.outJunction))
                    {
                        count++;
                    }
                }

                if (reversingJunction != null)
                {
#if DEBUG
                    Terminal.Log($"reversing junction {reversingJunction.GetInstanceID()}");
#endif
                    junctionsForReversing.Add(reversingJunction);
                }

                // Rotate turntable to align with the path entry/exit spurs
                if (walkData.prevTrack != null && walkData.nextTrack != null)
                {
                    TurntableRailTrack trt;
                    if (PathFinder._turntableTrackToTRT != null &&
                        PathFinder._turntableTrackToTRT.TryGetValue(walkData.currentTrack, out trt) && trt != null)
                    {
                        var entryEnd = trt.trackEnds?.FirstOrDefault(te => te?.track == walkData.prevTrack);
                        if (entryEnd != null)
                        {
                            trt.targetYRotation = entryEnd.angle;
                            trt.RotateToTargetRotation(true);
                            count++;
                            Terminal.Log($"Turntable rotated to {entryEnd.angle:0.#}° ({walkData.prevTrack.LogicTrack().ID.FullID} → {walkData.nextTrack.LogicTrack().ID.FullID})");
                        }
                    }
                }

                return true;
            });

            if (beginSignalReservation || count > 0)
                Terminal.Log($"Switched {count}");

            // Route Manager only asks DV Signals for the next governing block
            // after its own route switches have been aligned. This lets DV
            // Signals validate the actual selected path, and lets AI Traffic
            // yield through its existing player-priority behavior.
            if (beginSignalReservation
                && Module.ActiveRoute != null
                && Module.ActiveRoute.Route == this)
                Compatibility.DVSignalsCompatibility.BeginPlayerRouteReservation(this);

        }

        private static bool SwitchJunctionIfNeeded(RailTrack track, RailTrack nextTrack, Junction junction)
        {
            int branchIndex = -1;

            RailTrack trackToSwitch = junction.inBranch.track == track ? nextTrack : track;

            for (int i = 0; i < junction.outBranches.Count; i++)
            {
                if (junction.outBranches[i].track == trackToSwitch)
                {
                    branchIndex = i;
                    break;
                }
            }

            if (branchIndex != -1 && branchIndex != junction.selectedBranch)
            {
                Compatibility.AITrafficCompatibility.RefreshReservationSnapshot();
                if (Compatibility.AITrafficCompatibility.IsJunctionReserved(junction, out _))
                    return false;

                string reservationOwner;
                if (!Compatibility.DVSignalsCompatibility.CanSwitchJunction(junction, out reservationOwner))
                {
                    Compatibility.DVSignalsCompatibility.LogBlockedJunctionSwitch(junction, reservationOwner);
                    return false;
                }

                string adsRouteOwner;
                if (!Compatibility.ADSCompatibility.CanSwitchJunction(
                    junction, branchIndex, out adsRouteOwner))
                {
                    Compatibility.ADSCompatibility.LogBlockedJunctionSwitch(junction, adsRouteOwner);
                    return false;
                }

                Terminal.Log($"Switch {track.LogicTrack().ID.FullID} -> {trackToSwitch.LogicTrack().ID.FullID}");
                // Select the exact requested branch. Cycling the junction can
                // choose the wrong exit on turnouts with more than two
                // branches and prevents ADS from validating the intended
                // protected-switch transition.
                junction.Switch(Junction.SwitchMode.NO_SOUND, (byte)branchIndex);
                Compatibility.DVSignalsCompatibility.UnlogBlockedJunction(junction);
                return true;
            }

            return false;
        }


        public WalkPathData GetPrevTrack(RailTrack currentTrack, RailTrack nextTrack)
        {
            WalkPathData result = null;

            WalkPath((walkData) =>
            {
                if (currentTrack == walkData.currentTrack && nextTrack == walkData.nextTrack)
                {
                    result = walkData;
                    return false;
                }

                return true;
            });

            return result;
        }

        public WalkPathData GetNextTrack(RailTrack currentTrack, RailTrack prevTrack)
        {
            WalkPathData result = null;

            WalkPath((walkData) =>
            {
                if (currentTrack == walkData.currentTrack && prevTrack == walkData.prevTrack)
                {
                    result = walkData;
                    return false;
                }

                return true;
            });

            return result;
        }

        public RailTrack GetPrevTrack(RailTrack currentTrack)
        {
            RailTrack result = null;

            WalkPath((walkData) =>
            {
                if (currentTrack == walkData.currentTrack)
                {
                    result = walkData.nextTrack;
                    return false;
                }

                return true;
            });

            return result;
        }

        public async Task<Route> FindOppositeRoute()
        {
            if (FirstTrack == null || SecondTrack == null || LastTrack == null)
                throw new CommandException("Active route is too short to flip");

            var trackTransitions = new List<TrackTransition>
            {
                new TrackTransition() { track = FirstTrack, nextTrack = SecondTrack }
            };

            string startId = FirstTrack.LogicTrack().ID.FullID;
            string excludedNextId = SecondTrack.LogicTrack().ID.FullID;
            string destinationId = LastTrack.LogicTrack().ID.FullID;
            Module.mod?.Logger.Log("Opposite-heading route requested: " + startId
                + " -> " + destinationId + "; excluding initial direction through "
                + excludedNextId + ".");

            try
            {
                return await RouteCommand.FindRouteWithADSAlternative(
                    FirstTrack.LogicTrack(), LastTrack.LogicTrack(),
                    Module.settings.ReversingStrategy, Trainset, trackTransitions);
            }
            catch (CommandException exception)
            {
                Module.mod?.Logger.Log("Opposite-heading route unavailable in the loaded track layout: "
                    + startId + " -> " + destinationId + "; excluded " + excludedNextId
                    + "; planner result: " + exception.Message + ".");

                if (exception.Message.StartsWith("ADS ", StringComparison.OrdinalIgnoreCase))
                    throw;

                throw new CommandException("No opposite-heading path in loaded layout");
            }
        }

        public async static Task<Route> FindRoute(Track begin, Track end, ReversingStrategy reversingStrategy,
            Trainset trainset, List<TrackTransition> trackTransitions = null,
            ISet<RailTrack> excludedTracks = null)
        {
            if (begin.ID.FullID == end.ID.FullID)
                throw new CommandException("You're already there");

            Route route = await FindRoute(begin, end, trainset, false, trackTransitions, excludedTracks);

            if (reversingStrategy != ReversingStrategy.NoReversing && (route == null || reversingStrategy == ReversingStrategy.ChooseBest))
            {
#if DEBUG
                Terminal.Log($"Trying path with allowed reversing");
#endif
                // First try reversing while still respecting initial forward heading
                var routeWithReversing = await FindRoute(begin, end, trainset, true, trackTransitions,
                    excludedTracks);

                // If that failed and no route exists, try allowing reversing out of the start track
                if (routeWithReversing == null && route == null && trackTransitions != null)
                {
                    routeWithReversing = await FindRoute(begin, end, trainset, true, null,
                        excludedTracks);
                }

                if (routeWithReversing != null)
                {
                    Terminal.Log($"withoutreversing: {route?.Length} withReversing: {routeWithReversing.Length}");

                    // If a direct route without reversing exists, only replace it if reversing is substantially shorter (at least 30% shorter)
                    if (route == null || (reversingStrategy == ReversingStrategy.ChooseBest && routeWithReversing.Length < route.Length * 0.70))
                    {
                        route = routeWithReversing;
                    }
                }
            }

            if (route == null)
            {
                throw new CommandException("Route not found in loaded track layout");
            }

            Terminal.Log($"Found {route}");

            return route;
        }

        private async static Task<Route> FindRoute(Track begin, Track end, Trainset trainset,
            bool allowReverses, List<TrackTransition> trackTransitions = null,
            ISet<RailTrack> excludedTracks = null)
        {
            PathFinder pathFinder = new PathFinder(begin, end);

            double consistLength = 30.0;

            if (trainset != null)
            {
                consistLength = trainset.Length();
            }

            var path = await pathFinder.FindPath(
                allowReverses, consistLength, trackTransitions, trainset, excludedTracks);

            if (path == null || path.Count == 0)
                return null;

            return new Route(path, end, trainset);
        }


    }

}
