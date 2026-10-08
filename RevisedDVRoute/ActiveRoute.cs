using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace RevisedDVRoute
{
    public class ActiveRoute
    {
        private Route route;
        private RouteTracker routeTracker;

        public bool IsSet { get => Route != null; }

        public Route Route
        {
            get => route;
            set
            {
                if (value == null)
                {
                    ClearRoute();
                    return;
                }

                if (route != null && route != value)
                    Compatibility.DVSignalsCompatibility.ClearPlayerRouteReservation();

                route = value;
                PathMapMarker.DrawPathToMap(route);
            }
        }

        public void ClearRoute()
        {
            Compatibility.DVSignalsCompatibility.ClearPlayerRouteReservation();
            route = null;
            RouteTracker = null;
            PathMapMarker.DestroyAllPoints();
        }

        public RouteTracker RouteTracker
        {
            get => routeTracker;
            set
            {
                if (routeTracker != null)
                {
                    routeTracker.Dispose();
                }

                routeTracker = value;
            }
        }

        public PathMapMarkers PathMapMarker { get; } = new PathMapMarkers();


    }
}
