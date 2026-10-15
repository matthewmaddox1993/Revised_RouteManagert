using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace RevisedDVRoute
{
    static class Utils
    {
        public static object NextEnumItem(object currentEnumItem)
        {
            if (!currentEnumItem.GetType().IsEnum)
                throw new ArgumentException(String.Format("Argument is not an Enum"));
            Array Arr = Enum.GetValues(currentEnumItem.GetType());
            int j = Array.IndexOf(Arr, currentEnumItem) + 1;
            return (Arr.Length == j) ? Arr.GetValue(0) : Arr.GetValue(j);
        }

        public static object PreviousEnumItem(object currentEnumItem)
        {
            if (!currentEnumItem.GetType().IsEnum)
                throw new ArgumentException(String.Format("Argument is not an Enum"));
            Array Arr = Enum.GetValues(currentEnumItem.GetType());
            int j = Array.IndexOf(Arr, currentEnumItem) - 1;
            return (j == -1) ? Arr.GetValue(Arr.Length - 1) : Arr.GetValue(j);
        }

        public static (Bogie aBoogie, Bogie bBogie) GetBogiesWithMaxDistance(TrainCar aCar, TrainCar bCar)
        {
            float maxDistanceSquare = 0.0f;

            Bogie aBoogie = null;
            Bogie bBoogie = null;

            foreach (var a in aCar.Bogies)
            {
                if (a.HasDerailed || a.track == null)
                    continue; //it is derailed, skip it

                foreach (var b in bCar.Bogies)
                {
                    if (b.HasDerailed || b.track == null)
                        continue; //it is derailed, skip it

                    float lengthSquare = (a.transform.position - b.transform.position).sqrMagnitude;
                    if(lengthSquare > maxDistanceSquare)
                    {
                        maxDistanceSquare = lengthSquare;
                        aBoogie = a;
                        bBoogie = b;
                    }
                }
            }

            return (aBoogie, bBoogie);
        }

        /// <summary>
        /// Returns the two physical ends of the consist, ordered relative to the
        /// controlled locomotive rather than Trainset's arbitrary list order.
        /// </summary>
        public static (Bogie forwardBogie, Bogie rearBogie) GetOuterBogiesRelativeTo(TrainCar loco)
        {
            if (loco == null)
                return (null, null);

            Trainset trainset = loco.trainset;
            TrainCar firstCar = trainset?.firstCar ?? loco;
            TrainCar lastCar = trainset?.lastCar ?? loco;
            var (firstOuter, lastOuter) = GetBogiesWithMaxDistance(firstCar, lastCar);

            // A single vehicle has an unambiguous front/rear bogie definition.
            if (firstCar == lastCar)
            {
                Bogie front = loco.Bogies?.FirstOrDefault(b => b != null && !b.HasDerailed
                    && b.track != null && b.isFrontBogie);
                Bogie rear = loco.Bogies?.FirstOrDefault(b => b != null && !b.HasDerailed
                    && b.track != null && !b.isFrontBogie);
                if (front != null)
                    return (front, rear ?? lastOuter);
            }

            if (firstOuter == null || lastOuter == null)
            {
                Bogie fallback = loco.Bogies?.FirstOrDefault(b => b != null && !b.HasDerailed && b.track != null);
                return (fallback, null);
            }

            int locoIndex = trainset?.cars?.IndexOf(loco) ?? -1;
            if (locoIndex >= 0 && trainset.cars.Count > 1)
            {
                int neighborIndex = locoIndex < trainset.cars.Count - 1 ? locoIndex + 1 : locoIndex - 1;
                TrainCar neighbor = trainset.cars[neighborIndex];
                bool neighborIsAhead = Vector3.Dot(loco.transform.forward,
                    neighbor.transform.position - loco.transform.position) > 0f;
                bool forwardIsHigherIndex = (neighborIndex > locoIndex) == neighborIsAhead;
                return forwardIsHigherIndex
                    ? (lastOuter, firstOuter)
                    : (firstOuter, lastOuter);
            }

            // Defensive fallback for an incomplete Trainset: choose the endpoint
            // lying most in front of the locomotive.
            float firstDot = Vector3.Dot(loco.transform.forward,
                firstOuter.transform.position - loco.transform.position);
            float lastDot = Vector3.Dot(loco.transform.forward,
                lastOuter.transform.position - loco.transform.position);
            return firstDot >= lastDot
                ? (firstOuter, lastOuter)
                : (lastOuter, firstOuter);
        }

        /// <summary>
        /// True when moving away from the consist through this outer bogie heads
        /// toward the rail track's Out connection. A rear bogie's outward motion
        /// is opposite its car-forward direction.
        /// </summary>
        public static bool OutwardFromBogieFacesTrackOut(Bogie bogie)
        {
            return bogie != null && ((bogie.TrackDirectionSign > 0f) == bogie.isFrontBogie);
        }


        public static float GetAngleDifference(float alpha, float beta)
        {
            float phi = Mathf.Abs(beta - alpha) % (Mathf.PI * 2.0f);
            float distance = phi > Mathf.PI ? (Mathf.PI * 2.0f) - phi : phi;
            return distance;
        }



    }
}
