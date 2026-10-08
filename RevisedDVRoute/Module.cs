using CommandTerminal;
using CommsRadioAPI;
using DV;
using DV.Logic.Job;
using DV.Simulation.Cars;
using RevisedDVRoute.CommsRadio;
using HarmonyLib;
using SimpleJson;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityAsync;
using UnityEngine;
using UnityEngine.Networking;
using UnityModManagerNet;
using static UnityModManagerNet.UnityModManager;

namespace RevisedDVRoute
{
#if DEBUG
    [EnableReloading]
#endif
    static class Module
    {
        private const string AUDIO_DIRECTORY = "audio\\";
        public static UnityModManager.ModEntry mod;
        public static Settings settings;

        public static ActiveRoute ActiveRoute { get; private set; }

        public static AudioClip stopTrainClip { get; private set; }
        public static AudioClip trainEnd { get; private set; }
        public static AudioClip wrongWayClip { get; private set; }
        public static AudioClip offClip { get; private set; }
        public static AudioClip onClip { get; private set; }
        public static AudioClip setClip { get; private set; }
        public static AudioSource generalAudioSource { get; private set; }

        public static string ModulePath
        {
            get
            {
                return mod.Path;
            }
        }

        private static Dictionary<string, LocoAI> locosAI = new Dictionary<string, LocoAI>();
        public static LocoAI TryGetLocoAI(TrainCar car)
        {
            if (car == null || car.logicCar == null) return null;
            LocoAI locoAI;
            locosAI.TryGetValue(car.logicCar.ID, out locoAI);
            return locoAI;
        }

        public static LocoAI GetLocoAI(TrainCar car)
        {
            if (car == null || car.logicCar == null)
                throw new CommandException("No locomotive selected");

            LocoAI locoAI;
            if (!locosAI.TryGetValue(car.logicCar.ID, out locoAI))
            {
                SimController simController = car.GetComponent<SimController>();
                if (simController == null || simController.controlsOverrider == null)
                {
                    throw new CommandException("Unsupported locomotive");
                }

                // Engine-on check removed; control fails naturally if engine is off

                ILocomotiveRemoteControl remote = car.GetComponent<ILocomotiveRemoteControl>();
                if (remote == null)
                {
                    // Loco has no RemoteControllerModule (e.g. DM3) — use BaseControlsOverrider directly
                    mod.Logger.Log($"No ILocomotiveRemoteControl on {car.carLivery?.id ?? car.logicCar.ID} — using ControlsOverriderRemote");
                    remote = new ControlsOverriderRemote(car, simController);
                }

                locoAI = new LocoAI(remote, car);
                locosAI.Add(car.logicCar.ID, locoAI);
            }

            return locoAI;
        }

        public class VersionInfo
        {
            public string Version;
            public string downloadUrl;
        }
        public static VersionInfo VersionForUpdate { get; private set; }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "<Pending>")]
        static bool Load(UnityModManager.ModEntry modEntry)
        {
            try
            {
                settings = Settings.Load<Settings>(modEntry);
                mod = modEntry;
                mod.OnToggle = OnToggle;
                mod.OnUpdate = OnUpdate;
                mod.OnGUI = OnGUI;
                mod.OnSaveGUI = OnSaveGUI;
#if DEBUG
                modEntry.OnUnload = Unload;
#endif
                var harmony = new Harmony(modEntry.Info.Id);
                harmony.PatchAll(Assembly.GetExecutingAssembly());

                AsyncManager.Initialize();

                ActiveRoute = new ActiveRoute();

                modEntry.Logger.Log("RevisedDVRoute initialized");
                modEntry.Logger.Log("CommsRadioAPI " + (typeof(CommsRadioMode).Assembly.GetName().Version?.ToString() ?? "unknown") + " detected");
            }
            catch (Exception exc)
            {
                modEntry.Logger.LogException(exc);
                return false;
            }

            return true;
        }

#if DEBUG
        static bool Unload(UnityModManager.ModEntry modEntry)
        {
            //Before unloading OnToggle with active = false is called
            return true;
        }
#endif

        private static void OnSaveGUI(ModEntry modEntry)
        {
            settings.Save(modEntry);
        }

        private static void OnGUI(ModEntry modEntry)
        {
            settings.Draw(modEntry);
            DrawCompatibilityStatus();
        }

        private static void DrawCompatibilityStatus()
        {
            GUILayout.Space(12f);
            GUILayout.Label("Compatible mods");
            GUILayout.BeginVertical("box");
            DrawCompatibilityRow("Comms Radio API", "CommsRadioAPI", true, "required");
            DrawCompatibilityRow("DoubleTrack", "DoubleTrack", false, Compatibility.DoubleTrackCompatibility.StatusDescription);
            DrawCompatibilityRow("DV Signals", "DVSignals", false, Compatibility.DVSignalsCompatibility.StatusDescription);
            DrawCompatibilityRow("AI Traffic", "AITraffic", false, Compatibility.AITrafficCompatibility.StatusDescription);
            DrawCompatibilityRow("ADS (not yet integrated)", "AdvancedDispatcherSystem", false,
                "independent routing; Revised DV Route path is not published to ADS");
            DrawCompatibilityRow("DriverAssist", "DriverAssist", false, null);
            DrawCompatibilityRow("Revised_Mph", "Revised_Mph", false, null);
            GUILayout.EndVertical();
        }

        private static void DrawCompatibilityRow(string displayName, string modId, bool required, string detail)
        {
            ModEntry compatibleMod = UnityModManager.modEntries == null
                ? null
                : UnityModManager.modEntries.FirstOrDefault(entry => entry != null
                    && string.Equals(entry.Info?.Id, modId, StringComparison.OrdinalIgnoreCase));

            string state;
            Color stateColor;
            if (compatibleMod == null)
            {
                state = "Not installed";
                stateColor = required ? new Color(1f, 0.45f, 0.45f) : new Color(0.75f, 0.75f, 0.75f);
            }
            else if (!compatibleMod.Active)
            {
                state = "Installed, disabled";
                stateColor = new Color(1f, 0.78f, 0.25f);
            }
            else
            {
                state = "Installed and enabled";
                stateColor = new Color(0.45f, 1f, 0.45f);
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(displayName + (required ? " (required)" : " (optional)"), GUILayout.Width(235f));
            GUILayout.FlexibleSpace();
            Color previousColor = GUI.color;
            GUI.color = stateColor;
            GUILayout.Label(state);
            GUI.color = previousColor;
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(detail) && compatibleMod != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(12f);
                GUILayout.Label(detail);
                GUILayout.EndHorizontal();
            }
        }

        public static IEnumerator CheckUpdates()
        {
            if (string.IsNullOrWhiteSpace(mod?.Info?.Repository))
                yield break;

            while (Terminal.Shell == null || Terminal.Autocomplete == null)
            {
                yield return null;
            }

            UnityWebRequest www = null;

            try
            {
                www = UnityWebRequest.Get(mod.Info.Repository);
                www.downloadHandler = new DownloadHandlerBuffer();
                www.timeout = 10;
            }
            catch (Exception e)
            {
                Terminal.Log(e.Message + " " + e.StackTrace);
            }

            if (www != null)
            {
                yield return www.SendWebRequest();

                while (!www.downloadHandler.isDone)
                    yield return null;

                if (!www.isHttpError && !www.isNetworkError)
                {
                    var json = (IDictionary<string, object>)SimpleJson.SimpleJson.DeserializeObject(www.downloadHandler.text);

                    JsonObject releaseInfo = ((json["Releases"] as JsonArray)?[0] as JsonObject);
                    string version = (string)releaseInfo?["Version"];
                    Version latestVersion;
                    Version moduleVersion;

                    if (Version.TryParse(version, out latestVersion)
                        && Version.TryParse(mod.Info.Version, out moduleVersion)
                        && latestVersion > moduleVersion)
                    {
                        VersionForUpdate = new VersionInfo();
                        VersionForUpdate.Version = version;
                        VersionForUpdate.downloadUrl = (string)releaseInfo["DownloadUrl"]; ;
                        Terminal.Log($"{version} {VersionForUpdate.downloadUrl}");
                    }

                }
            }

        }
        public static IEnumerator SetupCommands()
        {
            while (moduleActive && (Terminal.Shell == null || Terminal.Autocomplete == null))
            {
                yield return null;
            }

            if (!moduleActive)
                yield break;

            stopTrainClip = AudioUtils.LoadAudioClip(AUDIO_DIRECTORY + "stoptrain.wav", "stoptrain");
            trainEnd = AudioUtils.LoadAudioClip(AUDIO_DIRECTORY + "trainend.wav", "trainend");
            wrongWayClip = AudioUtils.LoadAudioClip(AUDIO_DIRECTORY + "wrongway.wav", "wrongway");
            onClip = AudioUtils.LoadAudioClip(AUDIO_DIRECTORY + "on.wav", "on");
            offClip = AudioUtils.LoadAudioClip(AUDIO_DIRECTORY + "off.wav", "off");
            setClip = AudioUtils.LoadAudioClip(AUDIO_DIRECTORY + "set.wav", "set");

            Terminal.Shell.Commands.Remove("route");
            Terminal.Shell.AddCommand("route", RouteCommand.DoTerminalCommand, 0, -1, "", null);
            CommandInfo ci = new CommandInfo();
            ci.name = "route";
            try
            {
                Terminal.Autocomplete.Register(ci);
            }
            catch (ArgumentException)
            {
                // CommandTerminal has no public unregister API. Re-enabling or
                // hot-reloading the mod can therefore leave this harmless name
                // registered from the earlier instance.
                mod?.Logger.Log("Route autocomplete was already registered; continuing initialization.");
            }
            Terminal.Log("Route command registered");
            mod?.Logger.Log("Route command initialization complete.");

            LocoAI.BuildSignSpeedLimitCache();
        }

        public static IEnumerator SetupAudio()
        {
            AudioListener listener = null;

            //yield return new WaitForSeconds(5.0f);

            while (listener == null)
            {
                yield return new UnityEngine.WaitForSeconds(0.5f);
                listener = UnityEngine.Object.FindObjectOfType<AudioListener>();
            }

            SetupAudioSource(listener);

#if DEBUG
            Terminal.Log($"AudioListener found {generalAudioSource}");
            mod.Logger.Log("AudioListener found");
#endif
        }

        private static void SetupAudioSource(AudioListener listener)
        {
            if (listener == null)
                return;

            generalAudioSource = listener.gameObject.AddComponent<AudioSource>();
            //audioSource.outputAudioMixerGroup = Engine_Layered_Audio.audioMixerGroup;
            generalAudioSource.playOnAwake = true;
            generalAudioSource.loop = false;
            generalAudioSource.maxDistance = 300f;
            //generalAudioSource.clip = Module.stopTrainClip;
            generalAudioSource.spatialBlend = 0f;
            generalAudioSource.dopplerLevel = 0f;
            generalAudioSource.spread = 10f;
        }

        public static void PlayClip(AudioClip clip)
        {
            if (generalAudioSource == null)
            {
                AudioListener listener = UnityEngine.Object.FindObjectOfType<AudioListener>();
#if DEBUG
                Terminal.Log("PlayClip Init #2");
#endif
                if (listener != null)
                    SetupAudioSource(listener);
            }

            if (generalAudioSource != null && clip != null)
            {
                generalAudioSource.clip = clip;
                generalAudioSource.Play();
            }
            else if(clip == null)
            {
                Terminal.Log("Cannot play sound, clip == null");
            }
            else if (generalAudioSource == null)
            {
                Terminal.Log("Cannot play sound, generalAudioSource == null");
            }
        }

        public static Coroutine StartCoroutine(IEnumerator coroutine)
        {
            return AsyncManager.StartCoroutine(coroutine);
        }
        public static void StartCoroutines(IEnumerator[] coroutines)
        {
            foreach (var coroutine in coroutines)
            {
                AsyncManager.StartCoroutine(coroutine);
            }
        }

        private static void StartInitCoroutines()
        {
            AsyncManager.StartCoroutine(SetupCommands());
            AsyncManager.StartCoroutine(SetupAudio());
            AsyncManager.StartCoroutine(CheckUpdates());
            AsyncManager.StartCoroutine(MonitorCompatibility());
        }

        private static void StopInitCoroutines()
        {
        }



        static bool OnToggle(UnityModManager.ModEntry _, bool active)
        {
            moduleActive = active;
            if (active)
            {
                SubscribeToCommsRadioReady();
                StartInitCoroutines();
                EnsureCommsRadioRegistration();
            }
            else
            {
                Deactivate();
            }

            return true;
        }

        private static void Deactivate()
        {
            moduleActive = false;
            mod?.Logger.Log("RevisedDVRoute deactivating");

            UnsubscribeFromCommsRadioReady();
            RemoveCommsRouteManager();
            if (Terminal.Shell != null)
                Terminal.Shell.Commands.Remove("route");
            StopInitCoroutines();
            //Terminal.Autocomplete.UnRegister("route"); //currently not able unregister
            Module.ActiveRoute?.ClearRoute();
        }

        private static void OnUpdate(ModEntry arg1, float arg2)
        {
            if (Module.ActiveRoute.IsSet && Module.ActiveRoute.RouteTracker != null && Module.settings.TrainEndAlarm.Down())
            {
                Module.ActiveRoute.RouteTracker.NotifyTrainEnd();
            }

        }


        private static CommsRadioMode commsRadioMode;
        private static bool moduleActive;
        private static bool commsRegistrationStarted;
        private static bool commsReadySubscribed;
        private static string lastCommsRegistrationError;

        private static void SubscribeToCommsRadioReady()
        {
            if (commsReadySubscribed)
                return;

            ControllerAPI.Ready += OnCommsRadioReady;
            commsReadySubscribed = true;
        }

        private static void UnsubscribeFromCommsRadioReady()
        {
            if (!commsReadySubscribed)
                return;

            ControllerAPI.Ready -= OnCommsRadioReady;
            commsReadySubscribed = false;
        }

        private static void OnCommsRadioReady()
        {
            if (!moduleActive)
                return;

            // CommsRadioAPI raises Ready from the controller's Awake postfix,
            // after its internal Accessor and mode list have been populated.
            // Register synchronously so the physical radio sees this mode on
            // its first initialization pass.
            TryRegisterCommsRadioMode();
        }

        private static void EnsureCommsRadioRegistration()
        {
            if (commsRadioMode != null || commsRegistrationStarted)
                return;

            commsRegistrationStarted = true;
            Module.mod?.Logger.Log("Waiting for the CommsRadioAPI controller to initialize.");
            AsyncManager.StartCoroutine(AddCommsRouteManagerWhenReady());
        }

        private static IEnumerator AddCommsRouteManagerWhenReady()
        {
            // Ready is the primary path. This loop is a fallback for games in
            // which the controller was created before this mod subscribed.
            while (moduleActive && commsRadioMode == null)
            {
                if (UnityEngine.Object.FindObjectOfType<CommsRadioController>() != null)
                    TryRegisterCommsRadioMode();

                if (commsRadioMode == null)
                    yield return new UnityEngine.WaitForSeconds(0.5f);
            }

            commsRegistrationStarted = false;
        }

        private static bool TryRegisterCommsRadioMode()
        {
            if (commsRadioMode != null)
                return true;

            try
            {
                commsRadioMode = CommsRadioMode.Create(new RouteManagerInitialState(), new Color(0.5f, 0.5f, 0.5f));
                lastCommsRegistrationError = null;
                Module.mod?.Logger.Log("Comms Radio mode registered for the active controller.");
                return true;
            }
            catch (Exception e)
            {
                string error = e.GetType().Name + ": " + e.Message;
                if (error != lastCommsRegistrationError)
                {
                    lastCommsRegistrationError = error;
                    Module.mod?.Logger.Log("Comms Radio registration is not ready yet; retrying (" + error + ").");
                }
                return false;
            }
        }

        private static IEnumerator MonitorCompatibility()
        {
            Compatibility.DVSignalsCompatibility.Initialize();
            Compatibility.MphCompatibility.Initialize();

            while (moduleActive)
            {
                Compatibility.MphCompatibility.RefreshStatus();
                Compatibility.DoubleTrackCompatibility.RefreshForCurrentLayout();
                yield return new UnityEngine.WaitForSeconds(
                    Compatibility.DoubleTrackCompatibility.HasLoadedLayout ? 5f : 0.5f);
            }
        }

        private static void RemoveCommsRouteManager()
        {
            // CommsRadioAPI does not support runtime removal; mode persists until game restart
        }
    }
}
