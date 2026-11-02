using UnityEngine;
using UnityEngine.InputSystem;

namespace Driving
{
    /// <summary>Low-speed, flat-surface prototype. Chassis contacts support weight; four ground probes gate traction.</summary>
    [RequireComponent(typeof(Rigidbody)), DisallowMultipleComponent]
    public sealed class VehicleController : MonoBehaviour
    {
        [Header("Driving-only input actions (never modifies XRI bindings)")]
        [SerializeField] InputActionReference accelerateInput;
        [SerializeField] InputActionReference brakeReverseInput;
        [SerializeField] InputActionReference steeringInput;
        [SerializeField] InputActionReference keyboardAccelerate;
        [SerializeField] InputActionReference keyboardBrakeReverse;
        [SerializeField] InputActionReference keyboardSteer;
        [SerializeField] bool keyboardFallbackInEditor = true;
        [SerializeField] HandMenuController handMenu;
        [SerializeField] DriverSeatController driverSeat;

        [Header("Conservative defaults (meters, seconds)")]
        [SerializeField, Min(0.1f)] float maxForwardSpeed = 6f;
        [SerializeField, Min(0.1f)] float maxReverseSpeed = 2f;
        [SerializeField, Min(0.1f)] float acceleration = 1.5f;
        [SerializeField, Min(0.1f)] float brakingForce = 4f;
        [SerializeField, Range(1f, 40f)] float steeringAngle = 22f;
        [SerializeField, Min(0.1f)] float steeringResponsiveness = 1.5f;
        [SerializeField, Min(0f)] float rollingResistance = 0.25f;
        [SerializeField, Min(0f)] float lateralGrip = 8f;
        [SerializeField, Min(0.1f)] float wheelbase = 2.3f;
        [SerializeField, Min(0.1f)] float maxYawRate = 30f;
        [SerializeField] Vector3 centerOfMassOffset = new Vector3(0f, -0.25f, 0f);
        [Header("Four ground probes; exclude vehicle/rig layers")]
        [SerializeField] Transform[] groundProbes;
        [SerializeField] LayerMask groundMask = 1;
        [SerializeField, Min(0.1f)] float groundProbeDistance = 0.7f;

        [Header("Slippery-road training: multipliers of the baseline forces above")]
        [Tooltip("Remaining lateral grip versus slipperiness. Zero slipperiness always retains the baseline; grip never reaches zero.")]
        [SerializeField] AnimationCurve lateralTractionCurve = new AnimationCurve(
            new Keyframe(0f, 1f, -1.2f, -1.2f),
            new Keyframe(0.25f, 0.7f, -1.2f, -1.2f),
            new Keyframe(0.5f, 0.4f, -1.1f, -1.1f),
            new Keyframe(0.75f, 0.15f, -0.65f, -0.65f),
            new Keyframe(1f, 0.04f, -0.44f, -0.44f));
        [SerializeField, Range(0.1f, 1f)] float minimumLongitudinalTraction = 0.35f;
        [Tooltip("Remaining yaw recovery and Rigidbody angular damping at full slipperiness; fades in smoothly above 50%.")]
        [SerializeField, Range(0.01f, 1f)] float minimumYawRecovery = 0.12f;
        public float RoadSlipperiness { get; private set; }
        public float LateralTractionMultiplier => RoadSlipperiness == 0f ? 1f
            : Mathf.Clamp(lateralTractionCurve.Evaluate(RoadSlipperiness), 0.01f, 1f);
        public float LongitudinalTractionMultiplier => Mathf.Lerp(1f, Mathf.Clamp(minimumLongitudinalTraction, 0.1f, 1f), RoadSlipperiness);
        public float YawRecoveryMultiplier => Mathf.Lerp(1f, Mathf.Clamp(minimumYawRecovery, 0.01f, 1f),
            Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.5f, 1f, RoadSlipperiness)));
        public void SetRoadSlipperiness(float value)
        {
            RoadSlipperiness = DrivingEnvironmentController.Normalize(value);
            ApplyAngularDamping();
        }

        Rigidbody body;
        float baselineAngularDamping;
        void ApplyAngularDamping() { if (body != null) body.angularDamping = baselineAngularDamping * YawRecoveryMultiplier; }
        Vector3 spawnPosition;
        Quaternion spawnRotation;
        float throttle, brake, steering, smoothedThrottle;
        bool focused = true, paused, waitForRelease;
        float reverseHoldTime;
        public float SteeringNormalized { get; private set; }
        public float ForwardSpeed => body == null ? 0f : Vector3.Dot(body.linearVelocity, transform.forward);
        public bool IsGrounded { get; private set; }
        public float MaxForwardSpeed => maxForwardSpeed;
        public float MaxReverseSpeed => maxReverseSpeed;

        void Awake()
        {
            body = GetComponent<Rigidbody>();
            baselineAngularDamping = body.angularDamping;
            ApplyAngularDamping();
            body.centerOfMass = centerOfMassOffset;
            body.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            spawnPosition = body.position;
            spawnRotation = body.rotation;
        }

        void OnEnable() { ApplyAngularDamping(); foreach (var reference in Inputs()) reference?.action?.Enable(); waitForRelease = true; }
        void OnDisable()
        {
            foreach (var reference in Inputs()) reference?.action?.Disable();
            if (body != null) body.angularDamping = baselineAngularDamping;
            ClearInput();
        }
        InputActionReference[] Inputs() => new[] { accelerateInput, brakeReverseInput, steeringInput, keyboardAccelerate, keyboardBrakeReverse, keyboardSteer };

        void Update()
        {
            float gas = Read(accelerateInput), stop = Read(brakeReverseInput);
            Vector2 axis = steeringInput?.action?.ReadValue<Vector2>() ?? Vector2.zero;
            float turn = axis.x;
            if (keyboardFallbackInEditor && Application.isEditor)
            {
                gas = Mathf.Max(gas, Read(keyboardAccelerate));
                stop = Mathf.Max(stop, Read(keyboardBrakeReverse));
                var keyTurn = Read(keyboardSteer);
                if (Mathf.Abs(keyTurn) > Mathf.Abs(turn)) turn = keyTurn;
            }
            bool blocked = !focused || paused || (handMenu != null && handMenu.IsMenuOpen);
            if (blocked) waitForRelease = true;
            if (waitForRelease && gas < 0.05f && stop < 0.05f && Mathf.Abs(turn) < 0.1f && !blocked) waitForRelease = false;
            throttle = blocked || waitForRelease ? 0f : Mathf.Clamp01(gas);
            brake = blocked || waitForRelease ? 1f : Mathf.Clamp01(stop);
            steering = blocked || waitForRelease ? 0f : Mathf.Clamp(turn, -1f, 1f);
        }
        static float Read(InputActionReference reference) => reference?.action?.ReadValue<float>() ?? 0f;

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            int grounded = 0;
            if (groundProbes != null)
                foreach (var probe in groundProbes)
                    if (probe != null && Physics.Raycast(probe.position, Vector3.down, groundProbeDistance, groundMask, QueryTriggerInteraction.Ignore)) grounded++;
            IsGrounded = grounded >= 2;
            SteeringNormalized = Mathf.MoveTowards(SteeringNormalized, steering, steeringResponsiveness * dt);
            smoothedThrottle = Mathf.MoveTowards(smoothedThrottle, throttle, dt * 2f);
            if (!IsGrounded) { reverseHoldTime = 0f; return; }

            var velocity = body.linearVelocity;
            var forward = transform.forward;
            float speed = Vector3.Dot(velocity, forward);
            float nextSpeed = speed;
            float traction = LongitudinalTractionMultiplier;
            // Brake to a stop first. A brief deliberate hold at rest then engages reverse.
            if (brake > 0.05f)
            {
                if (speed > 0.05f) { nextSpeed = Mathf.MoveTowards(speed, 0f, brakingForce * traction * brake * dt); reverseHoldTime = 0f; }
                else if (throttle < 0.05f && !waitForRelease && focused && !paused && !(handMenu != null && handMenu.IsMenuOpen))
                {
                    reverseHoldTime += dt;
                    nextSpeed = reverseHoldTime >= 0.4f ? speed - acceleration * traction * brake * dt : Mathf.MoveTowards(speed, 0f, brakingForce * traction * dt);
                }
                else { nextSpeed = Mathf.MoveTowards(speed, 0f, brakingForce * traction * dt); reverseHoldTime = 0f; }
            }
            else
            {
                reverseHoldTime = 0f;
                nextSpeed = speed < -0.05f && throttle > 0.05f
                    ? Mathf.MoveTowards(speed, 0f, brakingForce * traction * throttle * dt)
                    : speed + smoothedThrottle * acceleration * traction * dt;
            }
            nextSpeed = Mathf.MoveTowards(nextSpeed, 0f, rollingResistance * dt);
            nextSpeed = Mathf.Clamp(nextSpeed, -maxReverseSpeed, maxForwardSpeed);
            body.AddForce(forward * ((nextSpeed - speed) / dt), ForceMode.Acceleration);
            float lateral = Vector3.Dot(velocity, transform.right);
            body.AddForce(-transform.right * lateral * Mathf.Min(lateralGrip * LateralTractionMultiplier, 1f / dt), ForceMode.Acceleration);
            float desiredYaw = speed / wheelbase * Mathf.Tan(SteeringNormalized * steeringAngle * Mathf.Deg2Rad);
            float limit = maxYawRate * Mathf.Deg2Rad;
            float targetYaw = Mathf.Clamp(desiredYaw, -limit, limit);
            float currentYaw = body.angularVelocity.y;
            // Keep steering into a turn responsive, but do not instantly arrest or reverse a skid.
            bool recoveringYaw = currentYaw * targetYaw <= 0f || Mathf.Abs(targetYaw) < Mathf.Abs(currentYaw);
            float yawResponse = recoveringYaw ? YawRecoveryMultiplier : 1f;
            body.angularVelocity = new Vector3(0f, Mathf.MoveTowards(currentYaw, targetYaw, dt * 0.8f * yawResponse), 0f);
            // Also bound existing horizontal momentum (e.g. after a barrier impact).
            var horizontal = Vector3.ProjectOnPlane(velocity, Vector3.up);
            if (horizontal.magnitude > maxForwardSpeed)
                body.linearVelocity = horizontal.normalized * maxForwardSpeed + Vector3.up * velocity.y;
        }

        public void ResetVehicle()
        {
            body.position = spawnPosition;
            body.rotation = spawnRotation;
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            // Reset is an explicit teleport: synchronize the interpolated hierarchy before seat calibration.
            transform.SetPositionAndRotation(spawnPosition, spawnRotation);
            ClearInput();
            waitForRelease = true;
            body.Sleep();
            Physics.SyncTransforms();
            if (driverSeat != null) driverSeat.RecenterSeat();
        }
        void ClearInput() { throttle = brake = steering = smoothedThrottle = reverseHoldTime = 0f; SteeringNormalized = 0f; }
        void OnApplicationFocus(bool value) { focused = value; if (!value) { ClearInput(); waitForRelease = true; } }
        void OnApplicationPause(bool value) { paused = value; if (value) { ClearInput(); waitForRelease = true; } }
    }
}

