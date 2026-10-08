using DV.Logic.Job;
using DV.Simulation.Cars;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace RevisedDVRoute.Compatibility
{
    // Current DriverAssist versions can fail their own registration/change-car
    // bookkeeping for task structures and locomotives Route Manager supports.
    // Patch only when DriverAssist is loaded and only suppress those known errors.
    [HarmonyPatch]
    internal static class DriverAssistCompatibilityPatch
    {
        private static bool Prepare()
        {
            return AccessTools.TypeByName("DriverAssist.Implementation.DriverAssistController") != null;
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type controllerType = AccessTools.TypeByName("DriverAssist.Implementation.DriverAssistController");
            if (controllerType == null)
                yield break;

            MethodBase onRegisterJob = controllerType.GetMethod("OnRegisterJob", BindingFlags.Instance | BindingFlags.NonPublic);
            if (onRegisterJob != null)
                yield return onRegisterJob;

            MethodBase changeCar = controllerType.GetMethod("ChangeCar", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (changeCar != null)
                yield return changeCar;
        }

        private static Exception Finalizer(Exception __exception, MethodBase __originalMethod, object[] __args)
        {
            if (__exception == null)
                return null;

            string methodName = __originalMethod != null ? __originalMethod.Name : "unknown";
            if (methodName == "OnRegisterJob")
            {
                Job job = __args != null && __args.Length > 0 ? __args[0] as Job : null;
                Module.mod?.Logger.Log("DriverAssist job registration skipped for " + (job != null ? job.ID : "unknown")
                    + ": " + __exception.GetType().Name + " - " + __exception.Message);
                return null;
            }

            if (methodName == "ChangeCar" && __exception is KeyNotFoundException)
            {
                TrainCar trainCar = __args != null && __args.Length > 0 ? __args[0] as TrainCar : null;
                string carType = trainCar != null ? trainCar.carType.ToString() : "unknown";
                string livery = trainCar != null ? trainCar.carLivery?.id ?? trainCar.logicCar?.ID ?? "unknown" : "unknown";
                Module.mod?.Logger.Log("DriverAssist car change skipped for unsupported " + carType + "/" + livery
                    + ": " + __exception.GetType().Name + " - " + __exception.Message);
                return null;
            }

            return __exception;
        }
    }
}
