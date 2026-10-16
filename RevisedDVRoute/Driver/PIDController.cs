// Adapted from Killermops27/dv-ai-traffic (src/Driver/PIDController.cs).
using System;
using UnityEngine;

namespace RevisedDVRoute.Driver
{
    /// <summary>
    /// Generic float PID Controller designed for train locomotive speed and brake regulation.
    /// Includes integral anti-windup clamping, smooth low-pass derivative filtering,
    /// and optional derivative-on-measurement to eliminate derivative kick.
    /// </summary>
    public class PIDController
    {
        #region Configuration Properties

        /// <summary>
        /// Proportional gain.
        /// </summary>
        public float Kp { get; set; }

        /// <summary>
        /// Integral gain.
        /// </summary>
        public float Ki { get; set; }

        /// <summary>
        /// Derivative gain.
        /// </summary>
        public float Kd { get; set; }

        /// <summary>
        /// Minimum output limit (clamping lower bound).
        /// </summary>
        public float MinOutput { get; set; }

        /// <summary>
        /// Maximum output limit (clamping upper bound).
        /// </summary>
        public float MaxOutput { get; set; }

        /// <summary>
        /// Time constant for low-pass smoothing of the derivative term (in seconds).
        /// Higher values provide smoother derivative at the cost of slight lag.
        /// Default is 0.1s.
        /// </summary>
        public float DerivativeFilterTimeConstant { get; set; }

        /// <summary>
        /// If true, derivative is calculated from changes in the process variable (measurement)
        /// rather than the error, preventing derivative kicks on setpoint steps.
        /// Default is true.
        /// </summary>
        public bool DerivativeOnMeasurement { get; set; }

        #endregion

        #region State Properties

        /// <summary>
        /// Current accumulated integral value.
        /// </summary>
        public float Integral { get; private set; }

        /// <summary>
        /// Most recent error value (setpoint - actual).
        /// </summary>
        public float LastError { get; private set; }

        /// <summary>
        /// Most recent process variable (actual measurement).
        /// </summary>
        public float LastMeasurement { get; private set; }

        /// <summary>
        /// Filtered derivative value.
        /// </summary>
        public float FilteredDerivative { get; private set; }

        /// <summary>
        /// Last computed controller output.
        /// </summary>
        public float LastOutput { get; private set; }

        /// <summary>
        /// Proportional contribution from the last calculation step.
        /// </summary>
        public float ProportionalTerm { get; private set; }

        /// <summary>
        /// Integral contribution from the last calculation step.
        /// </summary>
        public float IntegralTerm { get; private set; }

        /// <summary>
        /// Derivative contribution from the last calculation step.
        /// </summary>
        public float DerivativeTerm { get; private set; }

        /// <summary>
        /// True if the output reached its minimum or maximum saturation limit on the last step.
        /// </summary>
        public bool IsSaturated { get; private set; }

        private bool _isInitialized;

        #endregion

        #region Constructors

        /// <summary>
        /// Creates a new PIDController instance with specified gains and output bounds.
        /// </summary>
        /// <param name="kp">Proportional gain.</param>
        /// <param name="ki">Integral gain.</param>
        /// <param name="kd">Derivative gain.</param>
        /// <param name="minOutput">Minimum clamped output.</param>
        /// <param name="maxOutput">Maximum clamped output.</param>
        /// <param name="filterTimeConstant">Derivative low-pass filter time constant.</param>
        /// <param name="derivativeOnMeasurement">Whether to compute derivative on measurement.</param>
        public PIDController(
            float kp,
            float ki,
            float kd,
            float minOutput = 0.0f,
            float maxOutput = 1.0f,
            float filterTimeConstant = 0.1f,
            bool derivativeOnMeasurement = true)
        {
            Kp = kp;
            Ki = ki;
            Kd = kd;
            MinOutput = minOutput;
            MaxOutput = maxOutput;
            DerivativeFilterTimeConstant = Mathf.Max(0.001f, filterTimeConstant);
            DerivativeOnMeasurement = derivativeOnMeasurement;

            Reset();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Configures PID gains.
        /// </summary>
        public void SetGains(float kp, float ki, float kd)
        {
            Kp = kp;
            Ki = ki;
            Kd = kd;
        }

        /// <summary>
        /// Configures output saturation limits.
        /// </summary>
        public void SetOutputLimits(float minOutput, float maxOutput)
        {
            if (minOutput > maxOutput)
            {
                float temp = minOutput;
                minOutput = maxOutput;
                maxOutput = temp;
            }

            MinOutput = minOutput;
            MaxOutput = maxOutput;
        }

        /// <summary>
        /// Resets the internal controller state (integral, last error, derivative history).
        /// </summary>
        public void Reset()
        {
            Integral = 0.0f;
            LastError = 0.0f;
            LastMeasurement = 0.0f;
            FilteredDerivative = 0.0f;
            LastOutput = 0.0f;
            ProportionalTerm = 0.0f;
            IntegralTerm = 0.0f;
            DerivativeTerm = 0.0f;
            IsSaturated = false;
            _isInitialized = false;
        }

        /// <summary>
        /// Computes the PID control output given setpoint, actual process measurement, and time delta.
        /// </summary>
        /// <param name="setpoint">Target setpoint value.</param>
        /// <param name="actual">Current measured value.</param>
        /// <param name="deltaTime">Time step in seconds since last update.</param>
        /// <returns>Clamped control output.</returns>
        public float Update(float setpoint, float actual, float deltaTime)
        {
            if (deltaTime <= 0.00001f)
            {
                return LastOutput;
            }

            float error = setpoint - actual;

            // --- 1. Derivative Calculation with Smooth Low-Pass Filtering ---
            float rawDerivative;
            if (!_isInitialized)
            {
                rawDerivative = 0.0f;
                FilteredDerivative = 0.0f;
            }
            else
            {
                if (DerivativeOnMeasurement)
                {
                    // Derivative on measurement eliminates step derivative kick on setpoint change:
                    // d(error)/dt = d(setpoint)/dt - d(actual)/dt. Assuming constant setpoint: -d(actual)/dt
                    float deltaMeasurement = actual - LastMeasurement;
                    rawDerivative = -deltaMeasurement / deltaTime;
                }
                else
                {
                    float deltaError = error - LastError;
                    rawDerivative = deltaError / deltaTime;
                }

                // First-order low-pass filter (exponential smoothing): alpha = dt / (tau + dt)
                float alpha = deltaTime / (DerivativeFilterTimeConstant + deltaTime);
                FilteredDerivative += alpha * (rawDerivative - FilteredDerivative);
            }

            // --- 2. Proportional & Derivative Terms ---
            ProportionalTerm = Kp * error;
            DerivativeTerm = Kd * FilteredDerivative;

            // --- 3. Integral Anti-Windup Clamping & Update ---
            if (Mathf.Abs(Ki) > 1e-6f)
            {
                float tentativeIntegral = Integral + error * deltaTime;
                float tentativeIntegralTerm = Ki * tentativeIntegral;
                float unsaturatedOutput = ProportionalTerm + tentativeIntegralTerm + DerivativeTerm;

                // Anti-windup: conditional integration (clamping)
                // If output exceeds upper bound and error is positive, do not integrate upwards.
                // If output exceeds lower bound and error is negative, do not integrate downwards.
                bool saturatingHigh = unsaturatedOutput >= MaxOutput && error > 0.0f;
                bool saturatingLow = unsaturatedOutput <= MinOutput && error < 0.0f;

                if (!saturatingHigh && !saturatingLow)
                {
                    Integral = tentativeIntegral;
                    // Clamp integral itself to prevent massive accumulation beyond controller authority
                    float maxAllowedIntegral = Mathf.Abs(MaxOutput - MinOutput) / Ki;
                    Integral = Mathf.Clamp(Integral, -maxAllowedIntegral, maxAllowedIntegral);
                    IntegralTerm = Ki * Integral;
                }
                else
                {
                    // Freeze integral at current value
                    IntegralTerm = Ki * Integral;
                }
            }
            else
            {
                Integral = 0.0f;
                IntegralTerm = 0.0f;
            }

            // --- 4. Total Output Saturation Clamping ---
            float rawOutput = ProportionalTerm + IntegralTerm + DerivativeTerm;
            float clampedOutput = Mathf.Clamp(rawOutput, MinOutput, MaxOutput);

            IsSaturated = (clampedOutput <= MinOutput) || (clampedOutput >= MaxOutput);
            LastOutput = clampedOutput;
            LastError = error;
            LastMeasurement = actual;
            _isInitialized = true;

            return clampedOutput;
        }

        /// <summary>
        /// Computes the PID control output given an already calculated error and time delta.
        /// </summary>
        /// <param name="error">The error signal (setpoint - actual).</param>
        /// <param name="deltaTime">Time step in seconds since last update.</param>
        /// <returns>Clamped control output.</returns>
        public float Update(float error, float deltaTime)
        {
            if (deltaTime <= 0.00001f)
            {
                return LastOutput;
            }

            float rawDerivative;
            if (!_isInitialized)
            {
                rawDerivative = 0.0f;
                FilteredDerivative = 0.0f;
            }
            else
            {
                float deltaError = error - LastError;
                rawDerivative = deltaError / deltaTime;
                float alpha = deltaTime / (DerivativeFilterTimeConstant + deltaTime);
                FilteredDerivative += alpha * (rawDerivative - FilteredDerivative);
            }

            ProportionalTerm = Kp * error;
            DerivativeTerm = Kd * FilteredDerivative;

            if (Mathf.Abs(Ki) > 1e-6f)
            {
                float tentativeIntegral = Integral + error * deltaTime;
                float tentativeIntegralTerm = Ki * tentativeIntegral;
                float unsaturatedOutput = ProportionalTerm + tentativeIntegralTerm + DerivativeTerm;

                bool saturatingHigh = unsaturatedOutput >= MaxOutput && error > 0.0f;
                bool saturatingLow = unsaturatedOutput <= MinOutput && error < 0.0f;

                if (!saturatingHigh && !saturatingLow)
                {
                    Integral = tentativeIntegral;
                    float maxAllowedIntegral = Mathf.Abs(MaxOutput - MinOutput) / Ki;
                    Integral = Mathf.Clamp(Integral, -maxAllowedIntegral, maxAllowedIntegral);
                    IntegralTerm = Ki * Integral;
                }
                else
                {
                    IntegralTerm = Ki * Integral;
                }
            }
            else
            {
                Integral = 0.0f;
                IntegralTerm = 0.0f;
            }

            float rawOutput = ProportionalTerm + IntegralTerm + DerivativeTerm;
            float clampedOutput = Mathf.Clamp(rawOutput, MinOutput, MaxOutput);

            IsSaturated = (clampedOutput <= MinOutput) || (clampedOutput >= MaxOutput);
            LastOutput = clampedOutput;
            LastError = error;
            _isInitialized = true;

            return clampedOutput;
        }

        #endregion
    }
}

