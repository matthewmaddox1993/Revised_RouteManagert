using CommandTerminal;
using DV;
using RevisedDVRoute.Compatibility;
using RevisedDVRoute.Internals;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace RevisedDVRoute.CommsRadio
{
    public class NewRoutePage : CRMSelectorPage
    {
        bool fromLoco = true;
        public NewRoutePage(ICRMPageManager manager) :
            base(manager)
        {
        }

        protected override List<MenuItem> CreateMenuItems()
        {
            return new List<MenuItem>()
            {
                new MenuItem("From last used locomotive\nto job destination", "Build route", () => CreateJobRoute(true)),
                new MenuItem("From last used locomotive\nto specific track", "Select", () => CreateTrackRoute()),
                new MenuItem("From job cars\nto job destination", "Build route", () => CreateJobRoute(false)),
                GetExitMenu()
            };
        }

        private void CreateTrackRoute()
        {
            SetSubPage(typeof(SelectTownStationTrackPage), null, (lastPage) =>
            {
                SelectTrackPage trackPage = lastPage as SelectTrackPage;

                Terminal.Log($"Selected track {trackPage.SelectedTrack}");
                CommandArg[] args = new CommandArg[]
                {
                            new CommandArg() { String = "from" },
                            new CommandArg() { String = "loco" },
                            new CommandArg() { String = "to" },
                            new CommandArg() { String = trackPage.SelectedTrack }
                };
                BuildRoute(args, this);
            });
        }

        private void CreateJobRoute(bool fromLoco)
        {
            this.fromLoco = fromLoco;

            var allJobBooklets = JobBooklet.allExistingJobBooklets != null
                ? JobBooklet.allExistingJobBooklets.Where(b => b != null && b.job != null).ToList()
                : new List<JobBooklet>();

            if (allJobBooklets.Count == 0)
            {
                RedirectToMessagePage("No job", "Confirm");
                return;
            }

            if (allJobBooklets.Count > 1)
            {
                SetSubPage(typeof(SelectJobPage), null, (lastPage) =>
                {
                    SelectJobPage jobPage = lastPage as SelectJobPage;
                    if (jobPage != null)
                    {
                        UseJob(jobPage.SelectedJobName);
                    }
                });
            }
            else
            {
                UseJob(allJobBooklets[0].job.ID);
            }
        }

        public void UseJob(string jobName)
        {
            CommandArg[] args;
            if (fromLoco)
            {
                args = new CommandArg[]
                    {
                    new CommandArg() { String = "loco" },
                    new CommandArg() { String = jobName }
                    };
            }
            else
            {
                args = new CommandArg[]
                    {
                    new CommandArg() { String = "job" },
                    new CommandArg() { String = jobName }
                    };
            }

            BuildRoute(args, this);
        }

        public static async void BuildRoute(CommandArg[] args, CRMPage page)
        {
            try
            {
                page.DisplayText("Computing route", "");

                await RouteCommand.DoCommand(args);

                if (Module.ActiveRoute.IsSet)
                {
                    StringBuilder via = Module.ActiveRoute.Route.Path.Select(p => p.LogicTrack().ID.FullID)
                        .Where(s => !s.StartsWith(SelectTrackPage.GENERAL_TRACK_PREFIX))
                        .Select(s => s.GetUntilOrEmpty(SelectTrackPage.TRACK_PARTS_SEPARATOR))
                        .Distinct()
                        .Aggregate(new StringBuilder(), (current, next) => current.Append(current.Length == 0 ? "" : ", ").Append(next));

                    string routeInfo = $"Route {(Module.ActiveRoute.Route.Length / 1000.0):0.#}km\nHeading: {Module.ActiveRoute.Route.StartHeading}\nvia: {via}";

                    if (Module.ActiveRoute.Route.Reverses.Count > 0)
                        routeInfo += $"\nReverses: {Module.ActiveRoute.Route.Reverses.Count}";

                    routeInfo = ADSCompatibility.AppendRouteWarningNotice(routeInfo,
                        RouteCommand.LastRouteAttemptTrainset);
                    if (!string.IsNullOrWhiteSpace(RouteCommand.LastADSAlternativeNotice))
                        routeInfo += "\n" + RouteCommand.LastADSAlternativeNotice;

                    page.RedirectToMessagePage(routeInfo, "MENU");
                }
                else
                {
                    string failure = ADSCompatibility.AppendFailureNotice("Route not found",
                        RouteCommand.LastRouteAttemptTrainset);
                    page.RedirectToMessagePage(failure, "MENU");
                }
            }
            catch (CommandException exc)
            {
                string failure = ADSCompatibility.AppendFailureNotice(exc.Message,
                    RouteCommand.LastRouteAttemptTrainset);
                page.RedirectToMessagePage(failure, "MENU");
            }
            catch (Exception exc)
            {
                Terminal.Log(exc.Message + ": " + exc.StackTrace);
                string failure = ADSCompatibility.AppendFailureNotice("Error in building path, see console",
                    RouteCommand.LastRouteAttemptTrainset);
                page.RedirectToMessagePage(failure, "MENU");
            }
        }
    }
}
