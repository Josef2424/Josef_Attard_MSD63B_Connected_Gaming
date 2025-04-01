using System;
using UnityEngine;
using Unity.Netcode;
using UnityChess;

/// <summary>
/// Simple component that synchronizes game state between server and clients.
/// Uses ChessNetworkManager's existing logging system.
/// </summary>
public class GameStateSynchronizer : NetworkBehaviour
{
    // Singleton instance for easy access
    public static GameStateSynchronizer Instance { get; private set; }

    private void Awake()
    {
        // Singleton setup
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
        }
        else
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
    }

    /// <summary>
    /// Server RPC to request the current game state
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestGameStateSyncServerRpc(ServerRpcParams serverRpcParams = default)
    {
        if (!IsServer) return;

        // Get the client that sent the request
        ulong clientId = serverRpcParams.Receive.SenderClientId;

        ChessNetworkManager.Instance.LogMessage($"Received game state sync request from client {clientId}", "State");

        // Get the current game state
        string gameState = GameManager.Instance.SerializeGame();

        // Get the client's side
        Side playerSide = Side.None;
        if (ChessNetworkManager.Instance != null)
        {
            playerSide = ChessNetworkManager.Instance.GetPlayerSide(clientId);
        }

        // Get the current turn
        Side currentTurn = GameManager.Instance.SideToMove;

        // Send it to the client
        SyncGameStateClientRpc(
            gameState,
            (int)playerSide,
            (int)currentTurn,
            new ClientRpcParams
            {
                Send = new ClientRpcSendParams
                {
                    TargetClientIds = new[] { clientId }
                }
            }
        );
    }

    /// <summary>
    /// Client RPC to receive the game state
    /// </summary>
    [ClientRpc]
    public void SyncGameStateClientRpc(
        string serializedGameState,
        int playerSideValue,
        int currentTurnValue,
        ClientRpcParams clientRpcParams = default)
    {
        // Skip if we're the host
        if (IsHost) return;

        ChessNetworkManager.Instance.LogMessage(
            $"Received game state: {serializedGameState.Length} chars, side: {(Side)playerSideValue}", "State");

        if (string.IsNullOrEmpty(serializedGameState))
        {
            ChessNetworkManager.Instance.LogMessage("Received empty game state!", null, true);
            return;
        }

        try
        {
            // Load the game state
            GameManager.Instance.LoadGame(serializedGameState);
            ChessNetworkManager.Instance.LogMessage("Game state loaded successfully", "State");

            // Update the player's state in ChessNetworkManager
            if (ChessNetworkManager.Instance != null)
            {
                // Update stored side
                Side playerSide = (Side)playerSideValue;
                ChessNetworkManager.Instance.playerSides[NetworkManager.Singleton.LocalClientId] = playerSide;

                // Mark game as active
                ChessNetworkManager.Instance.isMultiplayerGameActive = true;

                // Update turn indicator and piece control
                ChessNetworkManager.Instance.UpdateTurnIndicator();
                ChessNetworkManager.Instance.UpdatePieceControl();

                // Update status message
                Side currentTurn = (Side)currentTurnValue;
                string statusMessage = (playerSide == currentTurn)
                    ? $"Your turn to move. You are playing as {playerSide}."
                    : $"Waiting for opponent's move. You are playing as {playerSide}.";

                ChessNetworkManager.Instance.UpdateConnectionStatus(statusMessage);
            }

            // Notify RejoinManager if available
            if (RejoinManager.Instance != null)
            {
                RejoinManager.Instance.LogInfo("Game state synchronized successfully");
            }
        }
        catch (Exception e)
        {
            ChessNetworkManager.Instance.LogMessage($"Error applying game state: {e.Message}\n{e.StackTrace}", null,
                true);
        }
    }
}