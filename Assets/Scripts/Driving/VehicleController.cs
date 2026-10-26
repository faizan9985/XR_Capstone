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

        Rigidbody body;
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
            body.centerOfMass = centerOfMassOffset;
            body.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            spawnPosition = body.position;
            spawnRotation = body.rotation;
        }

        void OnEnable() { foreach (var reference in Inputs()) reference?.action?.Enable(); waitForRelease = true; }
        void OnDisable()
        {
            foreach (var reference in Inputs()) reference?.action?.Disable();
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
            // Brake to a stop first. A brief deliberate hold at rest then engages reverse.
            if (brake > 0.05f)
            {
                if (speed > 0.05f) { nextSpeed = Mathf.MoveTowards(speed, 0f, brakingForce * brake * dt); reverseHoldTime = 0f; }
                else if (throttle < 0.05f && !waitForRelease && focused && !paused && !(handMenu != null && handMenu.IsMenuOpen))
                {
                    reverseHoldTime += dt;
                    nextSpeed = reverseHoldTime >= 0.4f ? speed - acceleration * brake * dt : Mathf.MoveTowards(speed, 0f, brakingForce * dt);
                }
                else { nextSpeed = Mathf.MoveTowards(speed, 0f, brakingForce * dt); reverseHoldTime = 0f; }
            }
            else
            {
                reverseHoldTime = 0f;
                nextSpeed = speed < -0.05f && throttle > 0.05f
                    ? Mathf.MoveTowards(speed, 0f, brakingForce * throttle * dt)
                    : speed + smoothedThrottle * acceleration * dt;
            }
            nextSpeed = Mathf.MoveTowards(nextSpeed, 0f, rollingResistance * dt);
            nextSpeed = Mathf.Clamp(nextSpeed, -maxReverseSpeed, maxForwardSpeed);
            body.AddForce(forward * ((nextSpeed - speed) / dt), ForceMode.Acceleration);
            float lateral = Vector3.Dot(velocity, transform.right);
            body.AddForce(-transform.right * lateral * Mathf.Min(lateralGrip, 1f / dt), ForceMode.Acceleration);
            float desiredYaw = speed / wheelbase * Mathf.Tan(SteeringNormalized * steeringAngle * Mathf.Deg2Rad);
            float limit = maxYawRate * Mathf.Deg2Rad;
            body.angularVelocity = new Vector3(0f, Mathf.MoveTowards(body.angularVelocity.y, Mathf.Clamp(desiredYaw, -limit, limit), dt * 0.8f), 0f);
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
