using CommandTerminal;
using DV.Logic.Job;
using RevisedDVRoute.Extensions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Assertions.Must;

namespace RevisedDVRoute
{

    public class CommandException : Exception
    {
        public CommandException(string message) : base(message)
        {
        }
    }

    public static class RouteCommand
    {
        public static Trainset LastRouteAttemptTrainset { get; private set; }
        public static string LastADSAlternativeNotice { get; private set; }

        public static async void DoTerminalCommand(CommandArg[] args)
        {
            try
            {
                if (!Terminal.IssuedError)
                {
                    if (args.Length == 0)
                    {
                        Terminal.Log("Build route command");
                        Terminal.Log("Examples:");
                        Terminal.Log("route job [jobname]");
                        Terminal.Log("route from [from_track] to [to_track]");
                        Terminal.Log("route loco");
                        Terminal.Log("route clear");
                        return;
                    }

                    await DoCommand(args);
                    if (!string.IsNullOrWhiteSpace(LastADSAlternativeNotice))
                    {
                        string adsResult = Compatibility.ADSCompatibility.AppendRouteWarningNotice(
                            "ADS route check", LastRouteAttemptTrainset);
                        Terminal.Log(adsResult + "\n" + LastADSAlternativeNotice);
                    }
                }
            }
            catch (CommandException exc)
            {
                Terminal.Log(Compatibility.ADSCompatibility.AppendFailureNotice(
                    exc.Message, LastRouteAttemptTrainset));
            }
            catch (Exception exc)
            {
                Terminal.Log($"{exc.Message} {exc.InnerException} {exc.StackTrace}");
            }
        }
        public async static System.Threading.Tasks.Task DoCommand(CommandArg[] args)
        {
            ResetRouteAttempt(null);
            if(args.Length == 0)
            {
                throw new CommandException("No command args");
            }

            if (args[0].String == "job" || args[0].String == "loco")
            {
                List<JobBooklet> allJobBooklets = JobBooklet.allExistingJobBooklets != null
                    ? JobBooklet.allExistingJobBooklets.Where(j => j != null && j.job != null).ToList()
                    : new List<JobBooklet>();
                JobBooklet jobBooklet = null;

                if (allJobBooklets.Count == 0)
                {
                    throw new CommandException("No current job");
                }

                if (allJobBooklets.Count > 1 || args.Length > 1)
                {
                    if (args.Length < 2)
                    {
                        throw new CommandException("Multiple jobs, specify job name");
                    }

                    jobBooklet = allJobBooklets.FirstOrDefault(j => j?.job?.ID == args[1].String);
                }
                else
                {
                    jobBooklet = allJobBooklets[0];
                }


                if (jobBooklet == null || jobBooklet.job == null)
                {
                    throw new CommandException("Unknown job");
                }

                RouteTaskChain chain = RouteTaskChain.FromDVJob(jobBooklet.job);

                if (chain == null)
                {
                    throw new CommandException("Not supported job type yet");
                }

                Terminal.Log($"Using job {jobBooklet.job.ID} chain {chain}");

                TrainCar trainCar;


                RouteTracker routeTracker = new RouteTracker(chain, true);

                if (args[0].String == "loco")
                {
                    trainCar = PlayerManager.LastLoco;

                    if (trainCar == null)
                    {
                        throw new CommandException("No last loco");
                    }
                }
                else
                {
                    trainCar = routeTracker.CurrentTask.TrainSets.FirstOrDefault()?.firstCar;
                }

                LastRouteAttemptTrainset = trainCar?.trainset;

                if (routeTracker.CurrentTask == null)
                {
                    throw new CommandException("No suitable task");
                }

                var (startTrack, bannedTransitions) = GetTrainDepartureInfo(trainCar);

                await FindAndSwitch(startTrack, routeTracker.CurrentTask.DestinationTrack, Module.settings.ReversingStrategy, trainCar.trainset, bannedTransitions);

                routeTracker.SetRoute(Module.ActiveRoute.Route, trainCar.trainset);

                Module.ActiveRoute.RouteTracker = routeTracker;

            }
            else if (args[0].String == "tracker")
            {
                TrainCar trainCar = PlayerManager.LastLoco;

                if (trainCar == null)
                {
                    throw new CommandException("No last loco");
                }

                LastRouteAttemptTrainset = trainCar.trainset;

                if (Module.ActiveRoute.RouteTracker.CurrentTask == null)
                {
                    throw new CommandException("Tracker has no task");
                }

                var (startTrack, bannedTransitions) = GetTrainDepartureInfo(trainCar);

                await FindAndSwitch(startTrack, Module.ActiveRoute.RouteTracker.CurrentTask.DestinationTrack, Module.settings.ReversingStrategy, trainCar.trainset, bannedTransitions);

                Module.ActiveRoute.RouteTracker.SetRoute(Module.ActiveRoute.Route, trainCar.trainset);
            }
            else if (args[0].String == "from" && args.Length == 4 && args[2].String == "to")
            {
                Trainset trainset = null; //default value if we don't have consist (trainset)

                if (args[1].String == "loco")
                {
                    TrainCar trainCar = PlayerManager.LastLoco;

                    if (trainCar == null)
                    {
                        throw new CommandException("No last loco");
                    }

                    var (startTrackLogic, _) = GetTrainDepartureInfo(trainCar);
                    args[1].String = startTrackLogic?.ID?.FullID ?? trainCar.Bogies[0].track.LogicTrack().ID.FullID;

                    trainset = trainCar.trainset;
                    Terminal.Log($"current loco track end");
                }
                else if (args[1].String == "job.trainset")
                {
                    //TODO
                }

                LastRouteAttemptTrainset = trainset;

                if (trainset == null)
                {
                    throw new CommandException("Goal track must be associated with car");
                }


                RailTrack startTrack = RailTrackRegistryBase.RailTracks.FirstOrDefault((RailTrack track) => track?.LogicTrack().ID.FullID == args[1].String);

                RailTrack goalTrack = RailTrackRegistryBase.RailTracks.FirstOrDefault((RailTrack track) => track?.LogicTrack().ID.FullID == args[3].String);

                RouteTaskChain chain = RouteTaskChain.FromDestination(goalTrack.LogicTrack(), trainset);
                var tracker = new RouteTracker(chain, false);


                if (startTrack == null || goalTrack == null)
                {
                    throw new CommandException("start track or goal track not found");
                }

                await FindAndSwitch(startTrack.LogicTrack(), goalTrack.LogicTrack(), Module.settings.ReversingStrategy, trainset);

                tracker.SetRoute(Module.ActiveRoute.Route, trainset);
                Module.ActiveRoute.RouteTracker = tracker;
            }
            else if (args[0].String == "opposite")
            {
                if ( ! Module.ActiveRoute.IsSet)
                {
                    throw new CommandException("No route active");
                }

                LastRouteAttemptTrainset = Module.ActiveRoute.RouteTracker?.Trainset
                    ?? Module.ActiveRoute.Route?.Trainset;


                var route = await Module.ActiveRoute.Route.FindOppositeRoute();

                if (route != null)
                {

                    Module.ActiveRoute.Route = route;
                    route.AdjustSwitches();

                    Module.ActiveRoute.RouteTracker.SetRoute(route, route.Trainset);

                    Terminal.Log($"Route {route.Length} {route?.SecondTrack?.LogicTrack().ID.FullDisplayID}");
                }
                else
                {
                    throw new CommandException("Opposite route could not be found");
                }
            }
            else if (args[0].String == "clear")
            {
                if (Module.ActiveRoute.IsSet)
                {
                    Module.ActiveRoute.ClearRoute();
                }
                else
                {
                    throw new CommandException("no active route");
                }
            }
            else if (args[0].String == "info")
            {
                if(Module.ActiveRoute.IsSet)
                {
                    Terminal.Log("Active route:");
                    Terminal.Log( Module.ActiveRoute.Route.ToString());
#if DEBUG
                    Terminal.Log( Module.ActiveRoute.Route.Path.Select(t => t.LogicTrack().ID.FullID).Aggregate((i, j) => i + "->" + j) );
#endif
                }
                else
                {
                    throw new CommandException("no active route");
                }
            }
#if DEBUG
            else if (args[0].String == "track")
            {
                var track = RailTrackRegistryBase.RailTracks.Where(t => t.LogicTrack().ID.FullID.ToLower() == args[1].String.ToLower()).FirstOrDefault();
                if(track == null)
                {
                    throw new CommandException("track not found");
                }

                Terminal.Log($"{track.LogicTrack().ID.FullID} {track.LogicTrack().length}m");

                if(track.inIsConnected)
                {
                    Terminal.Log("IN: " + track.GetAllInBranches().Select(b => b.track.LogicTrack().ID.FullID).Aggregate((a, b) => a + "; " + b)  );
                }
                if (track.outIsConnected)
                {
                    Terminal.Log("OUT: " + track.GetAllOutBranches().Select(b => b.track.LogicTrack().ID.FullID).Aggregate((a, b) => a + "; " + b));
                }
            }
            else if (args[0].String == "trainset")
            {
                TrainCar trainCar = PlayerManager.LastLoco;

                if (trainCar == null)
                {
                    throw new CommandException("No last loco");
                }

                Terminal.Log($"loco trainset cars: {trainCar.trainset.cars.Count}");
                Terminal.Log($"first car: {trainCar.trainset.firstCar.logicCar.ID}");
                Terminal.Log($"last car: {trainCar.trainset.lastCar.logicCar.ID}");
                Terminal.Log($"first car track: {trainCar.trainset.firstCar.logicCar.CurrentTrack?.ID.FullDisplayID}");
                Terminal.Log($"last car track: {trainCar.trainset.lastCar.logicCar.CurrentTrack?.ID.FullDisplayID}");
                Terminal.Log($"first car front boogie track: {trainCar.trainset.firstCar.logicCar.FrontBogieTrack?.ID.FullDisplayID}");
                Terminal.Log($"first car rear boogie track: {trainCar.trainset.firstCar.logicCar.RearBogieTrack?.ID.FullDisplayID}");
            }

#endif
            else if (args[0].String == "auto")
            {
                TrainCar trainCar = PlayerManager.LastLoco;

                if (trainCar == null)
                {
                    throw new CommandException("No last loco");
                }

                LastRouteAttemptTrainset = trainCar.trainset;

                if(args[1].String == "stop")
                {
                    var locoAI = Module.GetLocoAI(trainCar);
                    locoAI.StopAll();
                    return;
                }

                RailTrack goalTrack = RailTrackRegistryBase.RailTracks.FirstOrDefault((RailTrack track) => track?.LogicTrack().ID.FullID == args[1].String);
                if (goalTrack == null)
                {
                    throw new CommandException("Goal track not found");
                }

                var (startTrack, bannedTransitions) = GetTrainDepartureInfo(trainCar);
                Track destTrack = goalTrack.LogicTrack();

                Route route;
                try
                {
                    route = await FindRouteWithADSAlternative(startTrack, destTrack,
                        Module.settings.ReversingStrategy, trainCar.trainset, bannedTransitions);
                }
                catch
                {
                    Module.ActiveRoute.ClearRoute();
                    throw;
                }

                Module.ActiveRoute.Route = route;
                route.AdjustSwitches();

                RouteTaskChain chain = RouteTaskChain.FromDestination(destTrack, trainCar.trainset);
                var tracker = new RouteTracker(chain, false);
                tracker.SetRoute(route, trainCar.trainset);
                Module.ActiveRoute.RouteTracker = tracker;

                var driver = Module.GetLocoAI(trainCar);
                driver.StartAI(tracker);
            }
            else
            {
                throw new CommandException($"Unknown subcommand {args[0]}");
            }

        }

        public static (Track startTrack, List<TrackTransition> bannedTransitions) GetTrainDepartureInfo(TrainCar loco)
        {
            if (loco == null)
                return (null, null);

            // Route from the physical end lying ahead of the controlled locomotive.
            // The old implementation used Bogies[0] and the current reverser, both
            // of which can describe the opposite end of the train.
            var (leadingBogie, _) = Utils.GetOuterBogiesRelativeTo(loco);
            RailTrack startRailTrack = leadingBogie?.track
                ?? loco.Bogies?.FirstOrDefault(b => b != null && !b.HasDerailed && b.track != null)?.track;
            Track startTrack = startRailTrack?.LogicTrack();

            List<TrackTransition> bannedTransitions = null;
            if (startRailTrack != null && leadingBogie != null)
            {
                bannedTransitions = new List<TrackTransition>();
                bool departureFacesOut = Utils.OutwardFromBogieFacesTrackOut(leadingBogie);
                var rearBranches = departureFacesOut
                    ? (startRailTrack.inIsConnected ? startRailTrack.GetAllInBranches() : null)
                    : (startRailTrack.outIsConnected ? startRailTrack.GetAllOutBranches() : null);

                if (rearBranches != null)
                {
                    foreach (var branch in rearBranches)
                    {
                        if (branch?.track != null)
                        {
                            bannedTransitions.Add(new TrackTransition() { track = startRailTrack, nextTrack = branch.track });
                        }
                    }
                }

                string endName = leadingBogie.isFrontBogie ? "front" : "rear";
                Terminal.Log($"AI departure: loco {loco.logicCar?.ID}, {leadingBogie.Car?.logicCar?.ID} {endName} bogie on "
                    + $"{startTrack?.ID?.FullID}, heading {(departureFacesOut ? "Out" : "In")}");
            }

            return (startTrack, bannedTransitions);
        }

        private async static System.Threading.Tasks.Task FindAndSwitch(Track begin, Track end, ReversingStrategy reversingStrategy, Trainset trainset, List<TrackTransition> bannedTransitions = null)
        {
            if (begin is null)
            {
                throw new CommandException("Empty begin");
            }

            if (end is null)
            {
                throw new CommandException("Empty end");
            }

            Route route;
            try
            {
                route = await FindRouteWithADSAlternative(begin, end, reversingStrategy, trainset, bannedTransitions);
            }
            catch
            {
                Module.ActiveRoute.ClearRoute();
                throw;
            }

            Module.ActiveRoute.Route = route;
            route.AdjustSwitches();
        }

        internal async static Task<Route> FindRouteWithADSAlternative(Track begin, Track end,
            ReversingStrategy reversingStrategy, Trainset trainset,
            List<TrackTransition> trackTransitions = null)
        {
            ResetRouteAttempt(trainset);

            // A replacement route must not treat this mod's previous rolling
            // DV Signals reservation as a foreign ADS signal conflict.
            Route previousActiveRoute = Module.ActiveRoute?.Route;
            Compatibility.DVSignalsCompatibility.ClearPlayerRouteReservation();

            try
            {
                return await FindRouteWithADSAlternativeCore(begin, end, reversingStrategy,
                    trainset, trackTransitions);
            }
            catch
            {
                // A failed flip keeps the previous route active. Restore its
                // rolling reservation; callers which abandon it will clear it
                // through ActiveRoute immediately after this task faults.
                if (previousActiveRoute != null
                    && Module.ActiveRoute != null
                    && Module.ActiveRoute.Route == previousActiveRoute)
                {
                    Compatibility.DVSignalsCompatibility.BeginPlayerRouteReservation(
                        previousActiveRoute);
                }
                throw;
            }
        }

        private async static Task<Route> FindRouteWithADSAlternativeCore(Track begin, Track end,
            ReversingStrategy reversingStrategy, Trainset trainset,
            List<TrackTransition> trackTransitions)
        {
            Route route = await Route.FindRoute(begin, end, reversingStrategy, trainset,
                trackTransitions);
            if (route == null)
                throw new CommandException("Path cannot be found");

            HashSet<RailTrack> blockingTracks;
            if (!Compatibility.ADSCompatibility.TryGetBlockingTracks(trainset, route.Path,
                out blockingTracks))
            {
                if (Compatibility.ADSCompatibility.LastRouteRejected)
                    throw new CommandException(
                        "ADS blocked the original route; no different route can avoid the conflict");
                return route;
            }

            string blockedNames = string.Join(", ", blockingTracks
                .Select(track => track?.LogicTrack()?.ID?.FullID)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Take(4)
                .ToArray());
            Module.mod?.Logger.Log("ADS reported blocking conflicts on the calculated route; "
                + "searching once more with " + blockingTracks.Count + " track(s) excluded"
                + (string.IsNullOrWhiteSpace(blockedNames) ? "." : ": " + blockedNames + "."));

            try
            {
                Route alternative = await Route.FindRoute(begin, end, reversingStrategy, trainset,
                    trackTransitions, blockingTracks);
                if (alternative == null)
                    throw new CommandException("alternate path cannot be found");

                if (route.Path.SequenceEqual(alternative.Path))
                    throw new CommandException("alternate search returned the original route");

                if (Compatibility.ADSCompatibility.LastBlockIsSoft
                    && IsExcessiveDetour(route.Length, alternative.Length))
                {
                    Module.mod?.Logger.Log("ADS alternate route rejected as an excessive detour for a "
                        + "signal-level conflict: original=" + Math.Round(route.Length)
                        + " m; alternate=" + Math.Round(alternative.Length) + " m. Keeping the direct route.");
                    LastADSAlternativeNotice = "ADS signal conflict ignored for routing: the alternate path was "
                        + Math.Round(alternative.Length - route.Length) + " m longer. Direct route kept.";
                    return route;
                }

                int divergenceIndex = FirstDivergenceIndex(route.Path, alternative.Path);
                string divergenceTrack = divergenceIndex >= 0 && divergenceIndex < alternative.Path.Count
                    ? alternative.Path[divergenceIndex]?.LogicTrack()?.ID?.FullID
                    : null;
                LastADSAlternativeNotice = "ADS ALTERNATE CONFIRMED: a different route was selected"
                    + (string.IsNullOrWhiteSpace(divergenceTrack)
                        ? ". "
                        : " at " + divergenceTrack + ". ")
                    + blockingTracks.Count + " blocked original-route track"
                    + (blockingTracks.Count == 1 ? " avoided." : "s avoided.");
                Module.mod?.Logger.Log("ADS alternate route confirmed: original tracks="
                    + route.Path.Count + "; alternate tracks=" + alternative.Path.Count
                    + "; first divergence index=" + divergenceIndex
                    + (string.IsNullOrWhiteSpace(divergenceTrack)
                        ? "."
                        : "; alternate track=" + divergenceTrack + "."));
                return alternative;
            }
            catch (CommandException exception)
            {
                Module.mod?.Logger.Log("ADS alternate-route search failed: " + exception.Message);
                throw new CommandException(
                    "ADS reported a blocked route; no alternate route was found");
            }
        }

        private static void ResetRouteAttempt(Trainset trainset)
        {
            LastRouteAttemptTrainset = trainset;
            LastADSAlternativeNotice = null;
            Compatibility.ADSCompatibility.BeginRouteAttempt();
        }

        // An alternate is excessive when it is both more than twice as long
        // as the direct route and at least 750 m longer, so short routes
        // are not rejected over a few extra metres.
        private static bool IsExcessiveDetour(double originalLength, double alternativeLength)
        {
            return alternativeLength > originalLength * 2.0
                && alternativeLength - originalLength > 750.0;
        }

        private static int FirstDivergenceIndex(IList<RailTrack> original, IList<RailTrack> alternative)
        {
            int commonLength = Math.Min(original?.Count ?? 0, alternative?.Count ?? 0);
            for (int index = 0; index < commonLength; index++)
            {
                if (original[index] != alternative[index])
                    return index;
            }

            return (original?.Count ?? 0) == (alternative?.Count ?? 0)
                ? -1
                : commonLength;
        }

    }
}
