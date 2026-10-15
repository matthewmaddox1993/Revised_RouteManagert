using CommandTerminal;
using DV.HUD;
using DV.Simulation.Cars;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace RevisedDVRoute
{
    public class LocoCruiseControl : IDisposable
    {
        protected ILocomotiveRemoteControl remoteControl;
        protected TrainCar trainCar;

        public float TargetSpeed { get; protected set; } = 20.0f;
        protected bool running;
        protected bool stopRequested;

        private static LocoCruiseControl CruiseControl;

        public static bool IsSet { get => CruiseControl != null && CruiseControl.Running; }
        protected bool Running { get => running; }

        // ── DM3 gear state ──────────────────────────────────────────────────
        private const string LOCO_DM3 = "LocoDM3";
        private const float RPM_SHIFT_UP   = 800f;
        private const float RPM_SHIFT_DOWN = 600f;
        private const float SHIFT_COOLDOWN = 3.0f; // seconds between shifts
        private const float DM3_MAX_SPEED  = 70f;  // km/h

        private float _lastGearRpm    = 0f;
        private float _lastShiftTime  = -999f;
        private bool  _awaitingShift  = false; // throttle zeroed, waiting for RPM to drop before moving lever
        private int   _pendingShiftDir = 0;    // +1 up, -1 down

        // Cached components (lazy-initialised per loco session)
        private LocoIndicatorReader        _indicators;
        private InteriorControlsManager    _interiorControls;
        private bool                       _isDM3;

        // ────────────────────────────────────────────────────────────────────

        protected BaseControlsOverrider controlsOverrider;
        protected SimController simController;

        private float _lastThermalLogTime = -999f;
        private float _sandHoldUntilTime = -999f;
        private float _slipCooldownUntilTime = -999f;
        private bool _sanderActive = false;
        private float _lastThrottleStepTime = -999f;
        private bool _hillStartAssistActive = false;

        public LocoCruiseControl(ILocomotiveRemoteControl remoteControl, TrainCar car = null)
        {
            this.remoteControl = remoteControl;
            this.trainCar      = car;

            if (car != null)
            {
                simController     = car.GetComponent<SimController>();
                controlsOverrider = simController?.controlsOverrider ?? car.GetComponentInChildren<BaseControlsOverrider>();
                _isDM3            = car.carLivery?.parentType?.id == LOCO_DM3;
                _indicators       = car.GetComponentInChildren<LocoIndicatorReader>();
            }

            if (controlsOverrider == null && remoteControl is ControlsOverriderRemote cor)
            {
                controlsOverrider = cor.ControlsOverrider;
            }
        }

        public bool StartCruiseControl(float targetSpeed)
        {
            this.TargetSpeed = targetSpeed;
            ResetControllerState();
            running = true;
            Module.StartCoroutine(CruiseControlCoroutine());
            return true;
        }

        private const float Kp_SPEED             = 1.5f;
        private const float Ki_SPEED             = 0.0005f;
        private const float Kd_SPEED             = 7f;
        private const float Kp_SPEED_RETURN_FACTOR = 1.5f;

        private const float BRAKE_DEADBAND_KPH = 0.75f;
        private const float BRAKE_RELEASE_EPSILON = 0.01f;
        private const float LOW_SPEED_KPH = 6.0f;

        public enum ThermalProtectionLevel
        {
            Normal,
            Warning,
            Critical
        }

        protected float GetMaxConsistAmps()
        {
            float maxAmps = 0f;
            if (trainCar?.trainset?.cars != null)
            {
                for (int i = 0; i < trainCar.trainset.cars.Count; i++)
                {
                    TrainCar c = trainCar.trainset.cars[i];
                    if (c == null || !c.IsLoco) continue;
                    LocoIndicatorReader ind = c.GetComponentInChildren<LocoIndicatorReader>();
                    if (ind?.amps != null && ind.amps.Value > maxAmps)
                        maxAmps = ind.amps.Value;
                }
            }
            else if (_indicators?.amps != null)
            {
                maxAmps = _indicators.amps.Value;
            }
            return maxAmps;
        }

        protected float GetConsistAmpCautionThreshold()
        {
            string typeId = trainCar?.carLivery?.parentType?.id ?? "";
            if (typeId.IndexOf("DE2", StringComparison.OrdinalIgnoreCase) >= 0)
                return 520f;
            return 750f;
        }

        protected void MoveDynamicBrakeTowards(float target)
        {
            if (controlsOverrider?.DynamicBrake != null)
            {
                float current = controlsOverrider.DynamicBrake.Value;
                float difference = Mathf.Clamp01(target) - current;
                if (Mathf.Abs(difference) > 0.02f)
                {
                    float step = Mathf.Sign(difference) * Mathf.Min(Mathf.Abs(difference), 0.20f);
                    controlsOverrider.DynamicBrake.Set(Mathf.Clamp01(current + step));
                }
            }
        }

        protected void UpdateSmartSanding(bool slipping, float speed, bool braking)
        {
            if (slipping)
            {
                _sandHoldUntilTime = Time.time + 4.0f;
                _slipCooldownUntilTime = Time.time + 2.5f;
                remoteControl.UpdateThrottle(-0.25f);
            }
            else if (speed < 12.0f && remoteControl.GetTargetThrottle() > 0.15f)
            {
                _sandHoldUntilTime = Mathf.Max(_sandHoldUntilTime, Time.time + 2.5f);
            }
            else if (braking && remoteControl.GetTargetBrake() > 0.35f && speed > 5.0f)
            {
                _sandHoldUntilTime = Mathf.Max(_sandHoldUntilTime, Time.time + 2.0f);
            }

            bool shouldSand = Time.time < _sandHoldUntilTime || slipping;
            if (shouldSand != _sanderActive)
            {
                _sanderActive = shouldSand;
                remoteControl.UpdateSand(shouldSand ? ToggleDirection.UP : ToggleDirection.DOWN);
                if (controlsOverrider?.Sander != null)
                    controlsOverrider.Sander.Set(shouldSand ? 1f : 0f);
            }
        }

        protected ThermalProtectionLevel CheckThermalState(out string thermalDetail)
        {
            thermalDetail = "";
            ThermalProtectionLevel level = ThermalProtectionLevel.Normal;

            // 1. Check MultipleUnitStateObserver across the entire MU chain (includeMUConnections = true)
            var muTempState = remoteControl.GetEngineTemperatureState(true);
            if (muTempState.HasFlag(MultipleUnitStateObserver.TemperatureState.Critical))
            {
                level = ThermalProtectionLevel.Critical;
                thermalDetail = "MU Engine Temp CRITICAL";
            }
            else if (muTempState.HasFlag(MultipleUnitStateObserver.TemperatureState.Warning))
            {
                level = ThermalProtectionLevel.Warning;
                thermalDetail = "MU Engine Temp WARNING";
            }

            // 2. Check all locomotives in the trainset for Traction Motor and Oil overheating
            if (trainCar?.trainset?.cars != null)
            {
                foreach (TrainCar c in trainCar.trainset.cars)
                {
                    if (c == null || !c.IsLoco) continue;

                    LocoIndicatorReader ind = c.GetComponentInChildren<LocoIndicatorReader>();
                    if (ind == null) continue;

                    string locoName = c.carLivery?.id ?? c.logicCar?.ID ?? "Loco";

                    // Traction Motor Temperature (DE2, DE6, etc.)
                    float tmT = ind.tmTemp?.Value ?? 0f;
                    if (tmT >= 105f)
                    {
                        level = ThermalProtectionLevel.Critical;
                        thermalDetail = $"{locoName} TM Overheat CRITICAL ({tmT:0}°C)";
                        break;
                    }
                    else if (tmT >= 95f && level < ThermalProtectionLevel.Warning)
                    {
                        level = ThermalProtectionLevel.Warning;
                        thermalDetail = $"{locoName} TM Temp Warning ({tmT:0}°C)";
                    }

                    // Transmission Oil Temperature (DH4, etc.)
                    float oilT = ind.oilTemp?.Value ?? 0f;
                    if (oilT >= 110f)
                    {
                        level = ThermalProtectionLevel.Critical;
                        thermalDetail = $"{locoName} Oil Temp CRITICAL ({oilT:0}°C)";
                        break;
                    }
                    else if (oilT >= 100f && level < ThermalProtectionLevel.Warning)
                    {
                        level = ThermalProtectionLevel.Warning;
                        thermalDetail = $"{locoName} Oil Temp Warning ({oilT:0}°C)";
                    }

                    // Amperage Redline Protection (DE2 > 750A, DE6 > 950A)
                    float amps = ind.amps?.Value ?? 0f;
                    string typeId = c.carLivery?.parentType?.id ?? "";
                    float ampRedline = typeId.IndexOf("DE2", StringComparison.OrdinalIgnoreCase) >= 0 ? 750f : 950f;
                    if (amps > ampRedline && level < ThermalProtectionLevel.Warning)
                    {
                        level = ThermalProtectionLevel.Warning;
                        thermalDetail = $"{locoName} Current Redline ({amps:0}A)";
                    }
                }
            }

            return level;
        }

        float integral      = 0;
        float previousError = 0;

        protected void ResetControllerState()
        {
            integral = 0f;
            previousError = 0f;
            stopRequested = false;
            _awaitingShift = false;
            _pendingShiftDir = 0;
            _sandHoldUntilTime = -999f;
            _slipCooldownUntilTime = -999f;
            _lastThrottleStepTime = -999f;
            _hillStartAssistActive = false;
        }

        /// <summary>
        /// Returns positive speed when the locomotive is moving in the direction
        /// selected by the reverser and negative speed when it is rolling against it.
        /// </summary>
        protected float GetSpeedInSelectedDirectionKph()
        {
            float signedSpeedKph = remoteControl.GetForwardSpeed() * 3.6f;
            float reverser = remoteControl.GetReverserValue();

            if (reverser > 0.5f)
                return signedSpeedKph;
            if (reverser < 0.5f)
                return -signedSpeedKph;
            return 0f;
        }

        // ── Main speed controller ────────────────────────────────────────────
        //https://en.wikipedia.org/wiki/PID_controller
        protected float MaintainSpeed(float targetAcceleration, float dt, float speed, float acceleration)
        {
            if (remoteControl.GetReverserSymbol() == "N")
            {
                this.stopRequested = true;
                TargetSpeed = 0f;
            }

            float selectedDirectionSpeed = GetSpeedInSelectedDirectionKph();
            if (TargetSpeed < Mathf.Epsilon)
            {
                _hillStartAssistActive = false;
            }
            else if (selectedDirectionSpeed < -0.25f)
            {
                // Gravity is moving the train against the selected reverser.
                // Hold it with the independent brake while traction builds.
                _hillStartAssistActive = true;
            }
            else if (_hillStartAssistActive
                && selectedDirectionSpeed > 1.5f
                && remoteControl.GetTargetIndependentBrake() <= BRAKE_RELEASE_EPSILON)
            {
                _hillStartAssistActive = false;
            }

            if (_hillStartAssistActive)
                _sandHoldUntilTime = Mathf.Max(_sandHoldUntilTime, Time.time + 2.5f);

            // ── DM3: cap speed and handle gear shift ─────────────────────────
            if (_isDM3)
            {
                if (TargetSpeed > DM3_MAX_SPEED)
                    TargetSpeed = DM3_MAX_SPEED;

                // A queued gear change must never delay braking for a lower
                // speed limit. Shifting is only useful while holding or
                // increasing speed.
                if (TargetSpeed >= speed - BRAKE_DEADBAND_KPH && HandleDM3GearShift(dt))
                    return targetAcceleration; // PID skipped during shift
            }

            // ── Temperature & TM Protection ──────────────────────────────────
            string thermalReason;
            ThermalProtectionLevel thermalLevel = CheckThermalState(out thermalReason);

            // ── PID ──────────────────────────────────────────────────────────
            dt = Mathf.Max(dt, 0.01f);
            float error = TargetSpeed - speed;
            integral += error * dt;

            if (error < 0)
                integral = 0;

            float derivative = (error - previousError) / dt;

            float Kp = error < 0 ? Kp_SPEED * Kp_SPEED_RETURN_FACTOR : Kp_SPEED;

            float controlValue = Kp * error + Ki_SPEED * integral + Kd_SPEED * derivative;

            previousError = error;

            if (controlValue > 20.0f)
                controlValue = 20.0f;

            if (acceleration > targetAcceleration)
                controlValue = 0f;

            if (TargetSpeed < Mathf.Epsilon)
                controlValue = -10f;

            // Thermal Limiting:
            // Critical → immediately cut throttle to idle, disallow any power
            if (thermalLevel == ThermalProtectionLevel.Critical)
            {
                controlValue = -25f;
                remoteControl.UpdateThrottle(-100f);
                if (Time.time - _lastThermalLogTime > 5f)
                {
                    _lastThermalLogTime = Time.time;
                    Terminal.Log($"[Thermal Protection] {thermalReason} - Throttle cut to idle.");
                    Module.mod?.Logger.Log($"[Thermal Protection] {thermalReason} - Throttle cut to idle.");
                }
            }
            else if (thermalLevel == ThermalProtectionLevel.Warning)
            {
                controlValue = Mathf.Min(controlValue, -2f);
                if (remoteControl.GetTargetThrottle() > 0.35f)
                    remoteControl.UpdateThrottle(-0.5f);

                if (Time.time - _lastThermalLogTime > 10f)
                {
                    _lastThermalLogTime = Time.time;
                    Terminal.Log($"[Thermal Protection] {thermalReason} - Reducing throttle.");
                }
            }

            // ── Smart Sanding ────────────────────────────────────────────────
            bool slipping = remoteControl.IsWheelslipping(true);
            bool stopRequested = TargetSpeed < Mathf.Epsilon;
            bool braking = error < -BRAKE_DEADBAND_KPH || stopRequested;

            UpdateSmartSanding(slipping, speed, braking);

            if (braking)
            {
                remoteControl.UpdateThrottle(-100f);

                float overspeed = Mathf.Max(0f, -error);
                bool hasDynamicBrake = controlsOverrider?.DynamicBrake != null;
                float desiredDynamicBrake = 0f;
                float desiredTrainBrake = 0f;

                if (stopRequested)
                {
                    if (hasDynamicBrake && speed > 10f)
                    {
                        // Use dynamic braking for the mainline portion of a stop.
                        // Keep a light train-brake application as a safety margin;
                        // increase it if the consist is still gaining speed.
                        desiredDynamicBrake = 1.0f;
                        desiredTrainBrake = acceleration > 0.15f ? 0.35f : 0.15f;
                    }
                    else
                    {
                        // Dynamic braking fades at low speed; finish the stop on air.
                        desiredDynamicBrake = 0f;
                        desiredTrainBrake = Mathf.Lerp(0.35f, 0.75f, Mathf.Clamp01(speed / 30f));
                    }
                }
                else
                {
                    // Blend dynamic and train braking smoothly as overspeed grows.
                    // The old 8 km/h threshold abruptly applied a large air-brake
                    // command just after dynamic braking had reached full effort.
                    if (hasDynamicBrake)
                    {
                        desiredDynamicBrake = Mathf.Clamp01(overspeed / 5.0f);
                        desiredTrainBrake = Mathf.Clamp((overspeed - 5.0f) * 0.05f
                            + Mathf.Max(0f, acceleration) * 0.05f, 0f, 0.55f);
                    }
                    else
                    {
                        desiredDynamicBrake = 0f;
                        float accelFactor = Mathf.Max(0f, acceleration * 0.10f);
                        desiredTrainBrake = Mathf.Clamp(0.15f + (overspeed * 0.05f) + accelFactor, 0.15f, 0.75f);
                    }
                }

                MoveDynamicBrakeTowards(desiredDynamicBrake);
                MoveTrainBrakeTowards(desiredTrainBrake);

                float desiredIndependentBrake = stopRequested && speed < LOW_SPEED_KPH ? 0.30f : 0f;
                MoveIndependentBrakeTowards(desiredIndependentBrake);
            }
            else
            {
                // Not braking: release the train and dynamic brakes. During a
                // hill start, retain a modest independent-brake hold until the
                // locomotive has enough power to prevent further rollback.
                MoveDynamicBrakeTowards(0f);
                MoveTrainBrakeTowards(0f);
                float hillHoldBrake = _hillStartAssistActive
                    && remoteControl.GetTargetThrottle() < 0.5f ? 0.25f : 0f;
                MoveIndependentBrakeTowards(hillHoldBrake);

                bool dynBrakeReleased = controlsOverrider?.DynamicBrake == null || controlsOverrider.DynamicBrake.Value <= 0.05f;
                bool airBrakesReleased = remoteControl.GetTargetBrake() <= BRAKE_RELEASE_EPSILON
                    && (remoteControl.GetTargetIndependentBrake() <= BRAKE_RELEASE_EPSILON
                        || _hillStartAssistActive);

                if (airBrakesReleased && dynBrakeReleased && thermalLevel != ThermalProtectionLevel.Critical && Time.time >= _slipCooldownUntilTime)
                {
                    // 1. Calculate maximum allowed throttle ceiling based on TargetSpeed
                    float maxThrottleCeiling;
                    if (TargetSpeed <= 15f)
                        maxThrottleCeiling = 0.25f; // Notch 2 max for switching/coupling/tight curves
                    else if (TargetSpeed <= 30f)
                        maxThrottleCeiling = 0.40f; // Notch 3 max for yards, sidings
                    else if (TargetSpeed <= 50f)
                        maxThrottleCeiling = 0.58f; // Notch 4-5 max
                    else if (TargetSpeed <= 70f)
                        maxThrottleCeiling = 0.75f; // Notch 6 max
                    else
                        maxThrottleCeiling = 0.88f; // Notch 7 max for mainline

                    // A low-speed train that is not accelerating may be starting
                    // on a grade. Permit the controller to keep adding power rather
                    // than trapping a heavy consist at the normal speed-based cap.
                    // The existing rate, amperage, wheel-slip and thermal limits
                    // still apply, and coupling speeds remain deliberately gentle.
                    if (TargetSpeed >= 10f
                        && speed < 3f
                        && speed < TargetSpeed - 5f
                        && acceleration < 0.25f
                        && thermalLevel == ThermalProtectionLevel.Normal)
                    {
                        maxThrottleCeiling = 1.0f;
                    }

                    // If heavily loaded on open line and accelerating very slowly (< 0.25 km/h/s), permit full throttle
                    if (TargetSpeed > 45f && speed < TargetSpeed - 15f && acceleration < 0.25f && thermalLevel == ThermalProtectionLevel.Normal)
                    {
                        maxThrottleCeiling = 1.0f;
                    }

                    // 2. Amperage limiting
                    float currentAmps = GetMaxConsistAmps();
                    float ampCaution = GetConsistAmpCautionThreshold();
                    if (currentAmps > ampCaution)
                    {
                        maxThrottleCeiling = Mathf.Min(maxThrottleCeiling, remoteControl.GetTargetThrottle());
                        if (currentAmps > ampCaution * 1.15f)
                            maxThrottleCeiling = Mathf.Max(0f, remoteControl.GetTargetThrottle() - 0.125f);
                    }

                    // 3. Thermal warning ceiling
                    if (thermalLevel == ThermalProtectionLevel.Warning)
                    {
                        maxThrottleCeiling = Mathf.Min(maxThrottleCeiling, 0.35f);
                    }

                    // 4. Approach roll-off (anticipation)
                    float speedMargin = TargetSpeed - speed;
                    if (speedMargin < 6.0f)
                    {
                        float taper = Mathf.Clamp01(speedMargin / 6.0f);
                        float cruisingHoldThrottle = 0.15f; // sufficient to overcome rolling resistance
                        maxThrottleCeiling = Mathf.Min(maxThrottleCeiling, Mathf.Lerp(cruisingHoldThrottle, maxThrottleCeiling, taper));
                    }

                    if (speedMargin <= 0.8f && acceleration > 0.05f)
                    {
                        // Holding speed: ease off to coast/idle
                        maxThrottleCeiling = Mathf.Min(maxThrottleCeiling, Mathf.Max(0f, remoteControl.GetTargetThrottle() - 0.05f));
                    }

                    // 5. Determine target throttle
                    float currentThrottle = remoteControl.GetTargetThrottle();
                    float desiredThrottle = Mathf.Clamp(controlValue * 0.05f, 0f, maxThrottleCeiling);

                    // Use the normal smooth ramp while running. A rollback needs
                    // a faster, human-like power-brake start before gravity gains
                    // more speed.
                    if (desiredThrottle > currentThrottle)
                    {
                        float throttleStepInterval = _hillStartAssistActive ? 0.25f : 0.8f;
                        if (Time.time - _lastThrottleStepTime >= throttleStepInterval)
                        {
                            float step = Mathf.Min(desiredThrottle - currentThrottle, 0.125f);
                            remoteControl.UpdateThrottle(step * 10f);
                            _lastThrottleStepTime = Time.time;
                        }
                    }
                    else if (desiredThrottle < currentThrottle - 0.02f)
                    {
                        float step = Mathf.Min(currentThrottle - desiredThrottle, 0.25f);
                        remoteControl.UpdateThrottle(-step * 10f);
                        _lastThrottleStepTime = Time.time;
                    }
                }
                else
                {
                    remoteControl.UpdateThrottle(-100f);
                }
            }

            return targetAcceleration;
        }

        /// <summary>
        /// Moves a remote brake control toward an absolute 0..1 target by at
        /// most one control step. This works for both notched and continuous
        /// locomotive controls without assuming a particular notch count.
        /// </summary>
        protected void MoveTrainBrakeTowards(float target)
        {
            MoveBrakeTowards(remoteControl.GetTargetBrake(), target, remoteControl.UpdateBrake);
        }

        protected void MoveIndependentBrakeTowards(float target)
        {
            MoveBrakeTowards(remoteControl.GetTargetIndependentBrake(), target,
                remoteControl.UpdateIndependentBrake);
        }

        private static void MoveBrakeTowards(float current, float target, Action<float> update)
        {
            float difference = Mathf.Clamp01(target) - current;
            if (Mathf.Abs(difference) <= BRAKE_RELEASE_EPSILON)
                return;

            // RemoteControllerModule and ControlsOverriderRemote both treat
            // +/-1 as one lever step. Smaller factors prevent overshooting a
            // nearby target on continuous controls.
            update(Mathf.Clamp(difference * 10f, -1f, 1f));
        }

        // ── DM3 gear management ──────────────────────────────────────────────
        // Returns true while a shift is in progress (PID caller should skip output).
        private bool HandleDM3GearShift(float dt)
        {
            float rpm = _indicators?.engineRpm?.Value ?? 0f;
            float now = Time.time;

            if (_awaitingShift)
            {
                // Zero throttle and wait for RPM to settle before moving the lever
                remoteControl.UpdateThrottle(-100f);

                if (rpm < 750f)
                {
                    // RPM low enough — move the gear lever
                    var controls = GetInteriorControls();
                    if (controls != null)
                    {
                        controls.MoveScrollable(InteriorControlsManager.ControlType.GearboxA, _pendingShiftDir);
                        controls.MoveScrollable(InteriorControlsManager.ControlType.GearboxB, _pendingShiftDir);
#if DEBUG
                        Terminal.Log($"DM3: gear lever moved {(_pendingShiftDir > 0 ? "up" : "down")} (RPM {rpm:0})");
#endif
                    }
                    _awaitingShift = false;
                    _lastShiftTime = now;
                }

                _lastGearRpm = rpm;
                return true; // PID suppressed
            }

            // Cooldown between shifts
            if (now - _lastShiftTime < SHIFT_COOLDOWN)
            {
                _lastGearRpm = rpm;
                return false;
            }

            // Decide whether a shift is needed
            if (rpm > RPM_SHIFT_UP)
            {
                _awaitingShift  = true;
                _pendingShiftDir = 1;
#if DEBUG
                Terminal.Log($"DM3: shift-up queued (RPM {rpm:0})");
#endif
            }
            else if (rpm < RPM_SHIFT_DOWN && rpm <= _lastGearRpm)
            {
                _awaitingShift  = true;
                _pendingShiftDir = -1;
#if DEBUG
                Terminal.Log($"DM3: shift-down queued (RPM {rpm:0})");
#endif
            }

            _lastGearRpm = rpm;
            return _awaitingShift;
        }

        private InteriorControlsManager GetInteriorControls()
        {
            if (_interiorControls != null) return _interiorControls;
            if (trainCar?.interior == null) return null;
            _interiorControls = trainCar.interior.GetComponentInChildren<InteriorControlsManager>(true);
            return _interiorControls;
        }

        // ────────────────────────────────────────────────────────────────────

        public void Stop()
        {
            stopRequested = true;
            TargetSpeed = 0f;
        }

        protected IEnumerator CruiseControlCoroutine()
        {
            float prevSpeed = 0.0f;
            float targetAcceleration = 2.5f;
            float prevTime = Time.time;

            const float TIME_WAIT = 0.1f;
            float timeDelta = TIME_WAIT;

            while (running)
            {
                float absoluteSpeed = Mathf.Abs(remoteControl.GetForwardSpeed() * 3.6f);
                float speed = TargetSpeed < Mathf.Epsilon
                    ? absoluteSpeed
                    : Mathf.Max(0f, GetSpeedInSelectedDirectionKph());
                float acceleration = (speed - prevSpeed) / timeDelta;

                targetAcceleration = MaintainSpeed(targetAcceleration, timeDelta, speed, acceleration);

                float targetThrottle       = remoteControl.GetTargetThrottle();
                float targetIndependentBrake = remoteControl.GetTargetIndependentBrake();
                float targetBrake          = remoteControl.GetTargetBrake();

                prevSpeed = speed;

                yield return new WaitForSeconds(TIME_WAIT);

                timeDelta = Time.time - prevTime;
                prevTime  = Time.time;

                if (Mathf.Abs(targetThrottle - remoteControl.GetTargetThrottle()) > 1.0f * TIME_WAIT)
                    running = false;

                if (Mathf.Abs(targetIndependentBrake - remoteControl.GetTargetIndependentBrake()) > 1.0f * TIME_WAIT
                    || Mathf.Abs(targetBrake - remoteControl.GetTargetBrake()) > 0.1f * TIME_WAIT)
                {
                    if (remoteControl.GetTargetThrottle() > Mathf.Epsilon)
                    {
                        remoteControl.UpdateThrottle(-100.0f);
                        running = false;
                    }
                }

                if (!running)
                    Module.PlayClip(Module.offClip);
            }
        }

        protected float ThrottleCurveFactor(float target, bool increasing)
        {
            if (target < 0.4f)    return 0.01f;
            if (target > 0.6f && increasing) return 0.0005f;
            return increasing ? 0.002f : 0.01f;
        }

        public void Dispose()
        {
            running = false;
        }

        // ── Static cruise control helpers (legacy, not in UI) ────────────────

        public static void ToggleCruiseControl(float? speed = null)
        {
            if (CruiseControl == null || !CruiseControl.Running)
                SetCruiseControl(speed);
            else
            {
                Module.PlayClip(Module.offClip);
                ResetCruiseControl();
            }
        }

        public static void ResetCruiseControl()
        {
            if (CruiseControl != null)
            {
                CruiseControl.Dispose();
                CruiseControl = null;
                OnCruiseControlChange?.Invoke(null, null);
            }
        }

        public static event EventHandler OnCruiseControlChange;

        public static float SetCruiseControl(float? speed = null)
        {
            TrainCar car = PlayerManager.LastLoco;
            if (car == null)
                throw new ArgumentNullException(nameof(car));

            if (!speed.HasValue)
                speed = Mathf.Abs(car.GetForwardSpeed() * 3.6f);

            if (CruiseControl != null && CruiseControl.Running)
            {
                UpdateTargetSpeed(speed.Value - CruiseControl.TargetSpeed);
                return speed.Value;
            }

            ResetCruiseControl();
            Module.PlayClip(Module.onClip);

            ILocomotiveRemoteControl remote = car.GetComponent<ILocomotiveRemoteControl>();
            CruiseControl = new LocoCruiseControl(remote, car);
            CruiseControl.StartCruiseControl(speed.Value);

            OnCruiseControlChange?.Invoke(null, null);
            return speed.Value;
        }

        public static float UpdateTargetSpeed(float speedOffset)
        {
            if (CruiseControl != null)
            {
                CruiseControl.TargetSpeed += speedOffset;
                if (CruiseControl.TargetSpeed < 0.0f)
                    CruiseControl.TargetSpeed = 0.0f;
                OnCruiseControlChange?.Invoke(null, null);
                return CruiseControl.TargetSpeed;
            }
            return 0.0f;
        }

        public static float? GetTargetSpeed()
        {
            if (CruiseControl != null)
                return CruiseControl.TargetSpeed;
            return null;
        }
    }
}
