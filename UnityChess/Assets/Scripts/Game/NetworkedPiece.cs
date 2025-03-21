using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

public class NetworkedPiece : NetworkBehaviour
{
    /// <summary>
    /// Called on the client when the player moves the piece.
    /// Instead of moving the piece locally, we send a request to the server.
    /// </summary>
    /// <param name="targetPosition">
    /// The world position of the board square that the piece should move to.
    /// (This would be determined by your VisualPiece logic.)
    /// </param>
    public void RequestMove(Vector3 targetPosition)
    {
        // Only the owning client should request a move.
        if (!IsOwner) return;

        // Call the server to request the move.
        RequestMoveServerRpc(targetPosition);
    }

    /// <summary>
    /// This ServerRpc is called on the server when a client requests a move.
    /// Here the server should validate the move (e.g., via your GameManager/Chess engine logic)
    /// and update the authoritative game state. If valid, the server broadcasts the new position.
    /// </summary>
    /// <param name="targetPosition">The requested destination position.</param>
    [ServerRpc(RequireOwnership = true)]
    private void RequestMoveServerRpc(Vector3 targetPosition, ServerRpcParams rpcParams = default)
    {
        // Retrieve the starting position.
        Vector3 startPosition = transform.position;

        // *** Insert your move validation logic here ***
        // For example, you might call:
        // if (!GameManager.Instance.TryExecuteMove(...)) { return; }
        // where you pass in the piece's current square (derived from startPosition) and the target square.
        //
        // For this example, we assume the move is valid.

        // Optionally, if the move involves special behavior (e.g., promotion, castling) you can handle that here.

        // Now, update the authoritative game state on the server and notify all clients.
        UpdatePositionClientRpc(targetPosition);
    }

    /// <summary>
    /// This ClientRpc is called on all clients to update the piece’s position.
    /// All clients (including the server) will set the piece’s transform to the new target position.
    /// </summary>
    /// <param name="targetPosition">The new world position for the piece.</param>
    [ClientRpc]
    private void UpdatePositionClientRpc(Vector3 targetPosition, ClientRpcParams rpcParams = default)
    {
        transform.position = targetPosition;
    }
}
