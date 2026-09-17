using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Hands;

/// <summary>Uses the existing XR Hands subsystem; never creates tracking or input systems.</summary>
[DisallowMultipleComponent]
public sealed class HandMenuController : MonoBehaviour
{
    public enum MenuHand { Left, Right }

    [Header("Existing scene references")]
    [SerializeField] private Camera xrCamera;
    [Tooltip("The active XR Origin's Camera Offset, which contains tracking-space poses.")]
    [SerializeField] private Transform trackingSpace;
    [SerializeField] private PuzzleGenerator puzzleGenerator;
    [SerializeField] private PassthroughController passthrough;
    [SerializeField] private MenuHand hand = MenuHand.Left;

    [Header("UI")]
    [SerializeField] private Canvas launcherCanvas;
    [SerializeField] private Canvas menuCanvas;
    [SerializeField] private Button openButton;
    [SerializeField] private Button closeButton;
    [SerializeField] private Button restartButton;
    [SerializeField] private Toggle passthroughToggle;
    [SerializeField] private TMP_Text passthroughLabel;
    [SerializeField] private PrototypeVolumeControl volumeControl;

    [Header("Visibility")]
    [SerializeField, Range(1f, 89f)] private float palmFacingAngle = 65f;
    [SerializeField, Range(1f, 89f)] private float gazeAngle = 45f;
    [SerializeField, Range(0f, 20f)] private float angleHysteresis = 10f;
    [SerializeField, Min(0f)] private float showDelay = 0.12f;
    [SerializeField, Min(0f)] private float hideDelay = 0.25f;
    [SerializeField, Min(0.01f)] private float minimumDistance = 0.15f;
    [SerializeField, Min(0.1f)] private float maximumDistance = 0.9f;
    [SerializeField, Min(0f)] private float distanceHysteresis = 0.05f;

    [Header("Positioning")]
    [Tooltip("Meters in palm-local space: -Y points out of the palm; +Z toward fingers.")]
    [SerializeField] private Vector3 launcherPositionOffset = new Vector3(0f, -0.065f, 0f);
    [SerializeField] private Vector3 menuPositionOffset = new Vector3(0f, -0.10f, 0.03f);
    [Tooltip("Euler offsets from a canvas facing the headset, upright relative to the XR Origin.")]
    [SerializeField] private Vector3 launcherRotationOffset;
    [SerializeField] private Vector3 menuRotationOffset;
    [SerializeField, Min(0f)] private float positionSharpness = 18f;
    [SerializeField, Min(0f)] private float rotationSharpness = 14f;

    private static HandMenuController activeMenu;
    private readonly List<XRHandSubsystem> subsystems = new List<XRHandSubsystem>();
    private XRHandSubsystem handSubsystem;
    private float nextSubsystemSearch;
    private float conditionTime;
    private bool eligible;
    private bool hasPose;
    public bool IsMenuOpen { get; private set; }
    public bool IsLauncherVisible => launcherCanvas != null && launcherCanvas.gameObject.activeSelf;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => activeMenu = null;

    private void OnEnable()
    {
        HideAll();
        if (activeMenu != null && activeMenu != this)
        {
            Debug.LogWarning("Only one Hand Menu Controller can be active.", this);
            enabled = false;
            return;
        }
        if (xrCamera == null || trackingSpace == null || puzzleGenerator == null ||
            launcherCanvas == null || menuCanvas == null || openButton == null ||
            closeButton == null || restartButton == null || passthroughToggle == null ||
            passthrough == null || volumeControl == null)
        {
            Debug.LogError("Hand Menu Controller requires its scene and UI references.", this);
            enabled = false;
            return;
        }
        activeMenu = this;
        launcherCanvas.worldCamera = xrCamera;
        menuCanvas.worldCamera = xrCamera;
        openButton.onClick.AddListener(OpenMenu);
        closeButton.onClick.AddListener(CloseMenu);
        restartButton.onClick.AddListener(RestartPuzzle);
        passthroughToggle.onValueChanged.AddListener(SetPassthrough);
        passthrough.StateChanged += RefreshPassthrough;
        puzzleGenerator.PuzzleSpawned += HideAll;
        nextSubsystemSearch = 0f;
        RefreshPassthrough();
    }

    private void OnDisable()
    {
        if (openButton != null) openButton.onClick.RemoveListener(OpenMenu);
        if (closeButton != null) closeButton.onClick.RemoveListener(CloseMenu);
        if (restartButton != null) restartButton.onClick.RemoveListener(RestartPuzzle);
        if (passthroughToggle != null) passthroughToggle.onValueChanged.RemoveListener(SetPassthrough);
        if (passthrough != null) passthrough.StateChanged -= RefreshPassthrough;
        if (puzzleGenerator != null) puzzleGenerator.PuzzleSpawned -= HideAll;
        if (activeMenu == this) activeMenu = null;
        handSubsystem = null;
        HideAll();
    }

    private void OnApplicationPause(bool paused) { if (paused) HideAll(); }
    private void OnApplicationFocus(bool focused) { if (!focused) HideAll(); }

    private void LateUpdate()
    {
        if (handSubsystem == null || !handSubsystem.running)
        {
            handSubsystem = null;
            // XR can initialize after OnEnable or restart after suspend. No scene searches.
            if (Time.unscaledTime >= nextSubsystemSearch)
            {
                nextSubsystemSearch = Time.unscaledTime + 1f;
                SubsystemManager.GetSubsystems(subsystems);
                foreach (var candidate in subsystems)
                    if (candidate.running) { handSubsystem = candidate; break; }
            }
        }
        if (handSubsystem == null)
        {
            HideAll();
            return;
        }
        var trackedHand = hand == MenuHand.Left ? handSubsystem.leftHand : handSubsystem.rightHand;
        if (!trackedHand.isTracked || !trackedHand.GetJoint(XRHandJointID.Palm).TryGetPose(out var palm))
        {
            HideAll(); // Lost tracking never leaves an interactive panel at a stale pose.
            return;
        }
        palm.position = trackingSpace.TransformPoint(palm.position);
        palm.rotation = trackingSpace.rotation * palm.rotation;
        UpdatePalm(palm, Time.unscaledDeltaTime);
    }

    private void UpdatePalm(Pose palm, float deltaTime)
    {
        if (xrCamera == null || trackingSpace == null) { HideAll(); return; }
        var head = xrCamera.transform;
        var launcherPosition = palm.position + palm.rotation * launcherPositionOffset;
        var menuPosition = palm.position + palm.rotation * menuPositionOffset;
        var toHead = head.position - palm.position;
        var distance = toHead.magnitude;
        var angleMargin = eligible ? angleHysteresis : 0f;
        var distanceMargin = eligible ? distanceHysteresis : 0f;
        // XR Hands defines the outward palm normal as local -Y on BOTH hands.
        var facing = Vector3.Angle(palm.rotation * Vector3.down, toHead) <= palmFacingAngle + angleMargin;
        var gazeTarget = IsMenuOpen ? menuPosition : launcherPosition;
        var looking = Vector3.Angle(head.forward, gazeTarget - head.position) <= gazeAngle + angleMargin;
        var conditions = facing && looking && distance >= Mathf.Max(0.01f, minimumDistance - distanceMargin)
            && distance <= maximumDistance + distanceMargin;

        Follow(launcherCanvas.transform, launcherPosition, launcherRotationOffset, deltaTime);
        Follow(menuCanvas.transform, menuPosition, menuRotationOffset, deltaTime);
        hasPose = true;
        if (conditions == eligible) conditionTime = 0f;
        else
        {
            conditionTime += deltaTime;
            if (conditionTime >= (conditions ? showDelay : hideDelay))
            {
                eligible = conditions;
                conditionTime = 0f;
                if (!eligible) IsMenuOpen = false;
            }
        }
        RefreshVisibility();
    }

    private void Follow(Transform target, Vector3 position, Vector3 eulerOffset, float dt)
    {
        var forward = position - xrCamera.transform.position;
        var up = trackingSpace.up;
        if (Vector3.Cross(forward, up).sqrMagnitude < 0.0001f) up = xrCamera.transform.up;
        var rotation = forward.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(forward, up) * Quaternion.Euler(eulerOffset) : target.rotation;
        var positionBlend = !hasPose || positionSharpness <= 0f ? 1f : 1f - Mathf.Exp(-positionSharpness * dt);
        var rotationBlend = !hasPose || rotationSharpness <= 0f ? 1f : 1f - Mathf.Exp(-rotationSharpness * dt);
        target.SetPositionAndRotation(Vector3.Lerp(target.position, position, positionBlend),
            Quaternion.Slerp(target.rotation, rotation, rotationBlend));
    }

    public void OpenMenu()
    {
        if (!isActiveAndEnabled || !eligible || IsMenuOpen) return;
        IsMenuOpen = true;
        RefreshPassthrough();
        RefreshVisibility();
        volumeControl.Refresh();
    }

    public void CloseMenu()
    {
        IsMenuOpen = false;
        RefreshVisibility();
    }

    public void RestartPuzzle()
    {
        if (!isActiveAndEnabled || !IsMenuOpen) return;
        HideAll();
        puzzleGenerator.RetryCurrentPuzzle();
    }

    private void SetPassthrough(bool value)
    {
        passthrough.SetPassthroughEnabled(value);
        RefreshPassthrough();
    }

    private void RefreshPassthrough()
    {
        passthroughToggle.interactable = passthrough.IsSupported;
        passthroughToggle.SetIsOnWithoutNotify(passthrough.IsPassthroughEnabled);
        if (passthroughLabel != null)
            passthroughLabel.text = passthrough.IsSupported ? "Passthrough" : "Passthrough\nUnavailable";
    }

    private void RefreshVisibility()
    {
        SetVisible(launcherCanvas, eligible && !IsMenuOpen);
        SetVisible(menuCanvas, eligible && IsMenuOpen);
    }

    private static void SetVisible(Canvas canvas, bool value)
    {
        if (canvas != null && canvas.gameObject.activeSelf != value) canvas.gameObject.SetActive(value);
    }

    private void HideAll()
    {
        eligible = false;
        IsMenuOpen = false;
        hasPose = false;
        conditionTime = 0f;
        SetVisible(launcherCanvas, false);
        SetVisible(menuCanvas, false);
    }
}
