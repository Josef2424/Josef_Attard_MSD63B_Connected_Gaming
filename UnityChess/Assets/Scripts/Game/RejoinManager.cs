using System.Collections;
using UnityEngine;
using Unity.Netcode;
using UnityChess;

/// <summary>
/// Manages the rejoin functionality for the chess game.
/// </summary>
public class RejoinManager : MonoBehaviour
{
    // Singleton instance
    public static RejoinManager Instance { get; private set; }

    [Header("Settings")] [SerializeField] private float reconnectDelay = 0.5f;
    [SerializeField] private bool debugLogging = true;

    // Internal tracking
    private bool hasDisconnected = false;
    private Side playerSide = Side.None;
    private string lastGameState = string.Empty;
    private float disconnectTime;

    private void Awake()
    {
        // Singleton pattern
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

    private void Start()
    {
        // Subscribe to network and game events
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;
        }

        if (GameManager.Instance != null)
        {
            GameManager.MoveExecutedEvent += OnMoveExecuted;
        }

        LogInfo("RejoinManager initialized");
    }

    private void OnDestroy()
    {
        // Unsubscribe from events
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnect;
        }

        if (GameManager.Instance != null)
        {
            GameManager.MoveExecutedEvent -= OnMoveExecuted;
        }
    }

    /// <summary>
    /// Called when a client connects to the network
    /// </summary>
    private void OnClientConnected(ulong clientId)
    {
        // Only handle our own connections
        if (clientId != NetworkManager.Singleton.LocalClientId) return;

        // If we had previously disconnected, start the rejoin process
        if (hasDisconnected)
        {
            LogInfo($"Reconnected after {Time.time - disconnectTime:F2} seconds");
            StartCoroutine(ProcessRejoin());
        }
        else
        {
            // First connection - store the player's side for later
            StartCoroutine(StoreSide());
        }
    }

    /// <summary>
    /// Called when a client disconnects from the network
    /// </summary>
    private void OnClientDisconnect(ulong clientId)
    {
        // Only handle our own disconnections
        if (clientId != NetworkManager.Singleton.LocalClientId) return;

        // Record the disconnect time and state
        disconnectTime = Time.time;
        hasDisconnected = true;

        // Store the current game state if available
        if (GameManager.Instance != null)
        {
            lastGameState = GameManager.Instance.SerializeGame();
            LogInfo($"Stored game state on disconnect ({lastGameState.Length} chars)");
        }
    }

    /// <summary>
    /// Called when a move is executed in the game.
    /// </summary>
    private void OnMoveExecuted()
    {
        // Update our stored game state after each move
        if (GameManager.Instance != null && NetworkManager.Singleton != null &&
            NetworkManager.Singleton.IsConnectedClient)
        {
            lastGameState = GameManager.Instance.SerializeGame();
        }
    }

    /// <summary>
    /// Called when the rejoin button is clicked
    /// </summary>
    public void OnRejoinButtonClicked()
    {
        // If we're already connected, don't try to rejoin
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
        {
            LogInfo("Already connected, no need to rejoin");
            return;
        }

        // Use ChessNetworkManager to handle the network connection
        if (ChessNetworkManager.Instance != null)
        {
            LogInfo("Initiating rejoin process");
            hasDisconnected = true;
            ChessNetworkManager.Instance.RejoinGame();
        }
    }

    /// <summary>
    /// Handles the rejoin process
    /// </summary>
    public void HandleRejoinProcess()
    {
        StartCoroutine(ProcessRejoin());
    }

    /// <summary>
    /// Main rejoin process
    /// </summary>
    private IEnumerator ProcessRejoin()
    {
        LogInfo("Starting rejoin process");

        // Wait for connection to stabilize
        yield return new WaitForSeconds(reconnectDelay);

        // Request the current game state
        RequestGameState();

        // Wait for state to be applied
        yield return new WaitForSeconds(0.5f);

        // If we have a stored side, notify ChessNetworkManager
        if (playerSide != Side.None && ChessNetworkManager.Instance != null)
        {
            LogInfo($"Rejoining as {playerSide}");
            ChessNetworkManager.Instance.OnRejoinGame(NetworkManager.Singleton.LocalClientId, playerSide);
        }

        // Reset disconnect flag
        hasDisconnected = false;

        // Update the UI
        if (ChessNetworkManager.Instance != null)
        {
            ChessNetworkManager.Instance.UpdatePieceControl();
            ChessNetworkManager.Instance.UpdateTurnIndicator();

            // Force a refresh of the game state
            if (GameManager.Instance != null)
            {
                int moveIndex = GameManager.Instance.LatestHalfMoveIndex;
                GameManager.Instance.ResetGameToHalfMoveIndex(moveIndex);
            }
        }

        LogInfo("Rejoin process completed");
    }

    /// <summary>
    /// Stores the player's side after a short delay
    /// </summary>
    private IEnumerator StoreSide()
    {
        yield return new WaitForSeconds(0.5f);

        if (ChessNetworkManager.Instance != null && NetworkManager.Singleton != null)
        {
            playerSide = ChessNetworkManager.Instance.GetPlayerSide(NetworkManager.Singleton.LocalClientId);
            LogInfo($"Stored player side: {playerSide}");
        }
    }

    /// <summary>
    /// Requests the current game state from the server
    /// </summary>
    private void RequestGameState()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsConnectedClient) return;

        // Use GameStateSynchronizer as the primary method for state synchronization
        if (GameStateSynchronizer.Instance != null)
        {
            LogInfo("Requesting game state via GameStateSynchronizer");
            GameStateSynchronizer.Instance.RequestGameStateSyncServerRpc();
        }
        else
        {
            // Fallback to ChessNetworkManager if synchronizer isn't available
            LogInfo("GameStateSynchronizer not found, falling back to ChessNetworkManager");
            ChessNetworkManager.Instance?.RequestGameStateSyncServerRpc();
        }
    }

    /// <summary>
    /// Logs information if debug logging is enabled
    /// </summary>
    public void LogInfo(string message)
    {
        if (debugLogging)
        {
            Debug.Log($"[REJOIN] {message}");
        }
    }

    // Public getters for stored data
    public string GetLastGameState() => lastGameState;
    public Side GetPlayerSide() => playerSide;
}