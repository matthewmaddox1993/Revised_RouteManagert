using System;
using System.Linq;
using UnityEngine;
using UnityModManagerNet;

namespace RevisedDVRoute.Compatibility
{
    /// <summary>
    /// Optional integration for Revised_Mph. The mod only changes what is shown
    /// on signs and cab gauges; simulation and remote-control speeds remain km/h.
    /// </summary>
    internal static class MphCompatibility
    {
        private const string RevisedMphModId = "Revised_Mph";
        private const float KilometresPerMile = 1.609344f;
        private static bool? lastLoggedActive;

        public static bool IsActive
        {
            get
            {
                return UnityModManager.modEntries != null
                    && UnityModManager.modEntries.Any(entry => entry != null
                        && entry.Active
                        && string.Equals(entry.Info?.Id, RevisedMphModId, StringComparison.OrdinalIgnoreCase));
            }
        }

        public static string DisplaySpeedUnit => IsActive ? "mph" : "km/h";

        /// <summary>
        /// Converts a physical km/h curve limit to Revised_Mph's nearest 5 mph
        /// sign value, then returns the equivalent physical km/h target. This
        /// keeps the AI at or below the speed visible on the converted sign.
        /// </summary>
        public static float GetAiTargetSpeedKph(float trackLimitKph)
        {
            if (!IsActive || trackLimitKph <= 0f)
                return trackLimitKph;

            int displayedMph = Mathf.RoundToInt(trackLimitKph / KilometresPerMile / 5f) * 5;
            return displayedMph * KilometresPerMile;
        }

        public static void Initialize()
        {
            RefreshStatus();
        }

        /// <summary>
        /// Unity Mod Manager can activate optional mods after this assembly has
        /// initialized. Report only real state transitions; speed calculations
        /// already query IsActive dynamically on every use.
        /// </summary>
        public static void RefreshStatus()
        {
            bool active = IsActive;
            if (lastLoggedActive.HasValue && lastLoggedActive.Value == active)
                return;

            lastLoggedActive = active;
            Module.mod?.Logger.Log(active
                ? "Revised_Mph detected: AI speed limits will follow displayed MPH sign values."
                : "Revised_Mph not active: AI speed limits use km/h values.");
        }
    }
}
