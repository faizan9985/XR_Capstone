using UnityEngine;

[DisallowMultipleComponent]
public sealed class PuzzleTarget : MonoBehaviour
{
    [SerializeField] private PuzzlePieceType acceptedType;
    private bool occupied;

    private void OnTriggerEnter(Collider other)
    {
        if (occupied)
            return;

        var piece = other.GetComponentInParent<PuzzlePiece>();
        if (piece != null && piece.PieceType == acceptedType)
            occupied = piece.TryLockTo(transform);
    }
}
