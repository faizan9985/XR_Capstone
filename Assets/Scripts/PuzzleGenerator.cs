using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using Random = UnityEngine.Random;

[DisallowMultipleComponent]
public sealed class PuzzleGenerator : MonoBehaviour
{
    [Serializable]
    public sealed class ShapeDefinition
    {
        public PuzzlePiece piecePrefab;
        public PuzzleTarget targetPrefab;
    }

    [SerializeField] private PuzzleManager puzzleManager;
    [SerializeField] private XRInteractionManager interactionManager;
    [SerializeField, Min(1)] private int minPieceCount = 3;
    [SerializeField, Min(1)] private int maxPieceCount = 5;
    [SerializeField] private ShapeDefinition[] shapes;
    [SerializeField] private Transform pieceSpawnArea;
    [SerializeField] private Transform targetSpawnArea;
    [Tooltip("Local spawn bounds. Use unit-scale, upright area transforms; Y size is zero for a tabletop.")]
    [SerializeField] private Bounds spawnBounds = new Bounds(Vector3.zero, new Vector3(0.94f, 0f, 1.34f));
    [Tooltip("Minimum center spacing in area-local units. Keep at least as large as the widest prefab footprint.")]
    [SerializeField, Min(0.01f)] private float minimumSpacing = 0.4f;
    private GameObject generatedRoot;
    private readonly List<PuzzlePiece> generatedPieces = new List<PuzzlePiece>();

    private void Start() => Generate();

    [ContextMenu("Generate Puzzle (Play Mode)")]
    public void Generate()
    {
        if (!Application.isPlaying)
            return;
        if (puzzleManager == null || interactionManager == null || pieceSpawnArea == null ||
            targetSpawnArea == null || minPieceCount < 1 || maxPieceCount < minPieceCount ||
            minimumSpacing <= 0f || shapes == null || shapes.Length == 0)
        {
            Debug.LogError("PuzzleGenerator requires valid managers, spawn areas, counts and shape prefabs.", this);
            return;
        }
        foreach (var shape in shapes)
        {
            if (shape == null || shape.piecePrefab == null || shape.targetPrefab == null)
            {
                Debug.LogError("PuzzleGenerator has an incomplete shape definition.", this);
                return;
            }
        }

        var piecePositions = CreatePositions(pieceSpawnArea);
        var targetPositions = CreatePositions(targetSpawnArea);
        if (piecePositions.Count < maxPieceCount || targetPositions.Count < maxPieceCount)
        {
            Debug.LogError("Puzzle spawn bounds are too small for the maximum count at the configured spacing.", this);
            return;
        }

        // Validate before removing the current puzzle, and unsubscribe before destroying pieces.
        ClearGeneratedPuzzle();
        generatedRoot = new GameObject("Generated Puzzle");
        generatedRoot.transform.SetParent(transform, false);
        var count = Random.Range(minPieceCount, maxPieceCount + 1);
        for (var i = 0; i < count; i++)
        {
            var shape = shapes[Random.Range(0, shapes.Length)];
            var piece = Instantiate(shape.piecePrefab, piecePositions[i], pieceSpawnArea.rotation, generatedRoot.transform);
            var target = Instantiate(shape.targetPrefab, targetPositions[i], targetSpawnArea.rotation, generatedRoot.transform);
            piece.name = $"Piece {i + 1} - {piece.PieceType}";
            target.name = $"Target {i + 1} - {piece.PieceType}";
            piece.GetComponent<XRGrabInteractable>().interactionManager = interactionManager;
            var color = Color.HSVToRGB((float)i / count, 0.7f, 0.95f);
            piece.GetComponent<GrabColorFeedback>().SetRestingColor(color);
            target.Initialize(piece, color);
            generatedPieces.Add(piece);
        }
        puzzleManager.SetPieces(generatedPieces);
    }

    private List<Vector3> CreatePositions(Transform area)
    {
        var positions = new List<Vector3>();
        var columns = Mathf.FloorToInt(spawnBounds.size.x / minimumSpacing);
        var rows = Mathf.FloorToInt(spawnBounds.size.z / minimumSpacing);
        if (columns < 1 || rows < 1)
            return positions;
        var cellX = spawnBounds.size.x / columns;
        var cellZ = spawnBounds.size.z / rows;
        // Jitter only inside spare cell space, preserving spacing and edge clearance.
        var jitterX = (cellX - minimumSpacing) * 0.5f;
        var jitterZ = (cellZ - minimumSpacing) * 0.5f;
        for (var z = 0; z < rows; z++)
        for (var x = 0; x < columns; x++)
        {
            var local = new Vector3(
                spawnBounds.min.x + (x + 0.5f) * cellX + Random.Range(-jitterX, jitterX),
                Random.Range(spawnBounds.min.y, spawnBounds.max.y),
                spawnBounds.min.z + (z + 0.5f) * cellZ + Random.Range(-jitterZ, jitterZ));
            positions.Add(area.TransformPoint(local));
        }
        for (var i = positions.Count - 1; i > 0; i--)
        {
            var j = Random.Range(0, i + 1);
            (positions[i], positions[j]) = (positions[j], positions[i]);
        }
        return positions;
    }

    private void ClearGeneratedPuzzle()
    {
        if (puzzleManager != null)
            puzzleManager.ClearPieces();
        // XRI can reparent a held piece out of generatedRoot. Track ownership explicitly.
        foreach (var piece in generatedPieces)
        {
            if (piece == null)
                continue;
            var grab = piece.GetComponent<XRGrabInteractable>();
            grab.throwOnDetach = false;
            if (grab.isSelected && grab.interactionManager != null)
                grab.interactionManager.CancelInteractableSelection((IXRSelectInteractable)grab);
            piece.gameObject.SetActive(false);
            Destroy(piece.gameObject);
        }
        generatedPieces.Clear();
        if (generatedRoot == null)
            return;
        generatedRoot.SetActive(false);
        Destroy(generatedRoot);
        generatedRoot = null;
    }

    private void OnDestroy() => ClearGeneratedPuzzle();

    private void OnDrawGizmosSelected()
    {
        DrawArea(pieceSpawnArea, Color.cyan);
        DrawArea(targetSpawnArea, Color.yellow);
    }

    private void DrawArea(Transform area, Color color)
    {
        if (area == null)
            return;
        var previousMatrix = Gizmos.matrix;
        Gizmos.matrix = area.localToWorldMatrix;
        Gizmos.color = color;
        Gizmos.DrawWireCube(spawnBounds.center, spawnBounds.size);
        Gizmos.matrix = previousMatrix;
    }
}
