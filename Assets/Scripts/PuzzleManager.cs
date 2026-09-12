using UnityEngine;

[DisallowMultipleComponent]
public sealed class PuzzleManager : MonoBehaviour
{
    [SerializeField] private PuzzlePiece[] pieces;
    private bool completed;

    private void Update()
    {
        if (completed || pieces == null || pieces.Length == 0)
            return;

        foreach (var piece in pieces)
        {
            if (piece == null || !piece.IsCompleted)
                return;
        }

        completed = true;
        Debug.Log("Puzzle Complete", this);
    }
}
