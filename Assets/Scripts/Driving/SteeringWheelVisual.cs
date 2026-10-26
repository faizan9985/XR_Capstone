using UnityEngine;

namespace Driving
{
    public sealed class SteeringWheelVisual : MonoBehaviour
    {
        [SerializeField] VehicleController vehicle;
        [SerializeField] Transform steeringWheel;
        [SerializeField] Transform[] frontWheels = System.Array.Empty<Transform>();
        [SerializeField, Range(0f, 180f)] float wheelRotation = 95f;
        [SerializeField, Range(0f, 40f)] float roadWheelAngle = 22f;
        Quaternion restingRotation;
        void Awake() { if (steeringWheel != null) restingRotation = steeringWheel.localRotation; }
        void LateUpdate()
        {
            if (vehicle == null) return;
            if (steeringWheel != null) steeringWheel.localRotation = restingRotation * Quaternion.AngleAxis(-vehicle.SteeringNormalized * wheelRotation, Vector3.forward);
            foreach (var wheel in frontWheels)
                if (wheel != null) wheel.localRotation = Quaternion.Euler(0f, vehicle.SteeringNormalized * roadWheelAngle, 0f);
        }
    }
}
