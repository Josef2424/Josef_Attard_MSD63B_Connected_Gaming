using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityChess;
using Unity.Netcode;
using UnityEngine.UI;
using TMPro;

[DefaultExecutionOrder(100)]
public class myNetworkManager : MonoBehaviour
{
    [Header("Start Buttons")]
    [SerializeField] private Button hostButton;
    [SerializeField] private Button clientButton;
    [SerializeField] private Button serverButton;

    [Header("Stop Buttons")]
    [SerializeField] private Button stopHostButton;
    [SerializeField] private Button stopClientButton;
    [SerializeField] private Button stopServerButton;

    [Header("Session Buttons")]
    [SerializeField] private Button joinButton;
    [SerializeField] private Button leaveButton;
    [SerializeField] private Button rejoinButton;

    // Optional: If you want to use a session code input field
    [Header("Optional Session Code")]
    [SerializeField] private TMP_InputField sessionCodeInputField;

    [Header("Info Text")]
    [SerializeField] private TMP_Text infoText;

    private void Awake()
    {
        // --- Existing Start buttons ---
        if (hostButton != null) hostButton.onClick.AddListener(StartHost);
        if (clientButton != null) clientButton.onClick.AddListener(StartClient);
        if (serverButton != null) serverButton.onClick.AddListener(StartServer);

        // --- New Stop buttons ---
        if (stopHostButton != null) stopHostButton.onClick.AddListener(StopHost);
        if (stopClientButton != null) stopClientButton.onClick.AddListener(StopClient);
        if (stopServerButton != null) stopServerButton.onClick.AddListener(StopServer);

        // --- New Session buttons ---
        if (joinButton != null) joinButton.onClick.AddListener(JoinSession);
        if (leaveButton != null) leaveButton.onClick.AddListener(LeaveSession);
        if (rejoinButton != null) rejoinButton.onClick.AddListener(RejoinSession);

        // Register for Netcode callbacks if needed
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        NetworkManager.Singleton.OnServerStarted += OnServerStarted;
    }

    private void OnDestroy()
    {
        // Clean up callbacks to avoid issues if this object is destroyed
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
            NetworkManager.Singleton.OnServerStarted -= OnServerStarted;
        }
    }

    // --------------------- Existing Start Methods ---------------------

    /// <summary>
    /// Start as Host (acts as both Server and Client).
    /// </summary>
    public void StartHost()
    {
        NetworkManager.Singleton.StartHost();
        UpdateInfoText("Starting as Host...");
    }

    /// <summary>
    /// Start as Server only (no local client).
    /// </summary>
    public void StartServer()
    {
        NetworkManager.Singleton.StartServer();
        UpdateInfoText("Starting as Server...");
    }

    /// <summary>
    /// Start as a Client. Make sure the NetworkManager’s transport settings (IP, port, etc.)
    /// are set appropriately, or you’re using Relay, etc.
    /// </summary>
    public void StartClient()
    {
        NetworkManager.Singleton.StartClient();
        UpdateInfoText("Starting as Client...");
    }

    // --------------------- New Stop Methods ---------------------

    /// <summary>
    /// Stop hosting if we are currently Host.
    /// </summary>
    public void StopHost()
    {
        if (NetworkManager.Singleton.IsHost)
        {
            NetworkManager.Singleton.Shutdown();
            UpdateInfoText("Stopped Host.");
        }
        else
        {
            UpdateInfoText("Cannot stop Host: Not currently hosting.");
        }
    }

    /// <summary>
    /// Stop the client if we are currently only a client (not a host).
    /// </summary>
    public void StopClient()
    {
        if (NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsHost)
        {
            NetworkManager.Singleton.Shutdown();
            UpdateInfoText("Stopped Client.");
        }
        else
        {
            UpdateInfoText("Cannot stop Client: Not currently in client mode.");
        }
    }

    /// <summary>
    /// Stop the server if we are a dedicated server (not a host).
    /// </summary>
    public void StopServer()
    {
        if (NetworkManager.Singleton.IsServer && !NetworkManager.Singleton.IsHost)
        {
            NetworkManager.Singleton.Shutdown();
            UpdateInfoText("Stopped Server.");
        }
        else
        {
            UpdateInfoText("Cannot stop Server: Not currently a dedicated server.");
        }
    }

    // --------------------- New Session Methods ---------------------

    /// <summary>
    /// Join a session (effectively just start as a client).
    /// You can also read a session code from an input field if you want.
    /// </summary>
    public void JoinSession()
    {
        // If you want to validate a session code:
        if (sessionCodeInputField != null && !string.IsNullOrEmpty(sessionCodeInputField.text))
        {
            // e.g. check length or format
            UpdateInfoText("Joining session code: " + sessionCodeInputField.text);
        }
        else
        {
            UpdateInfoText("Joining session with no code...");
        }

        // Start the client
        NetworkManager.Singleton.StartClient();
    }

    /// <summary>
    /// Leave the current session (host or client).
    /// </summary>
    public void LeaveSession()
    {
        if (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsHost)
        {
            NetworkManager.Singleton.Shutdown();
            UpdateInfoText("Left the session.");
        }
        else
        {
            UpdateInfoText("Not in a session to leave.");
        }
    }

    /// <summary>
    /// Rejoin a session. Typically you'd check if you're not connected and then re-StartClient().
    /// </summary>
    public void RejoinSession()
    {
        // If we’re already in a session, show a message
        if (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsHost)
        {
            UpdateInfoText("Already in a session. Cannot rejoin.");
            return;
        }
        // Otherwise, attempt to rejoin
        UpdateInfoText("Rejoining session...");
        NetworkManager.Singleton.StartClient();
    }

    // --------------------- Callbacks ---------------------

    private void OnServerStarted()
    {
        // This fires on the server/host when it has fully started.
        if (NetworkManager.Singleton.IsHost)
        {
            UpdateInfoText("Host started. You are also a client.");
        }
        else
        {
            UpdateInfoText("Server started. No local client.");
        }
        if (NetworkManager.Singleton.IsServer)
        {
            // Ensure GameManager.Instance is not null.
            BoardManager.Instance.CreateBoardSquares();
            GameManager.Instance.StartNewGame();
            Debug.Log("New game started on server.");
        }
    }

    private void OnClientConnected(ulong clientId)
    {
        // Called on both host and clients
        if (NetworkManager.Singleton.IsServer)
        {
            UpdateInfoText($"Server: Client {clientId} connected. Total: {GetConnectedCount()}");

            // Don't process the server's own connection
            if (clientId != NetworkManager.Singleton.LocalClientId)
            {
                // Reconstruct the game for this client
                ReconstructGameForClient(clientId);
            }
        }
        else
        {
            UpdateInfoText($"Client {clientId} connected. (Local client? {NetworkManager.Singleton.LocalClientId})");
        }
    }

    private IEnumerator HandleClientConnection(ulong clientId)
    {
        // Wait to ensure all network objects are properly synchronized
        yield return new WaitForSeconds(2.0f);

        // Notify the GameManager about the client reconnection
        GameManager.Instance.HandleClientReconnect(clientId);
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (NetworkManager.Singleton.IsServer)
        {
            UpdateInfoText($"Server: Client {clientId} disconnected. Total: {GetConnectedCount()}");

            // Take ownership of black pieces but don't destroy them
            if (clientId != NetworkManager.Singleton.LocalClientId)
            {
                VisualPiece[] allPieces = FindObjectsOfType<VisualPiece>();
                foreach (var piece in allPieces)
                {
                    if (piece.PieceColor == Side.Black)
                    {
                        NetworkObject netObj = piece.GetComponentInParent<NetworkObject>();
                        if (netObj != null && netObj.IsSpawned && netObj.OwnerClientId == clientId)
                        {
                            // Transfer ownership to the server
                            netObj.ChangeOwnership(NetworkManager.Singleton.LocalClientId);
                        }
                    }
                }
            }
        }
        else
        {
            UpdateInfoText($"Client {clientId} disconnected.");
        }
    }

    // --------------------- Helpers ---------------------

    private void UpdateInfoText(string message)
    {
        if (infoText != null)
        {
            infoText.text = message;
        }
        Debug.Log(message);
    }

    private int GetConnectedCount()
    {
        // The server has a dictionary of connected clients
        return NetworkManager.Singleton.ConnectedClients.Count;
    }

    // --------------------- Authority ---------------------
    public void AssignPieceAuthority()
    {
        // Only run this on the server
        if (!NetworkManager.Singleton.IsServer) return;

        Debug.Log("Assigning piece authority to clients...");

        VisualPiece[] allPieces = FindObjectsOfType<VisualPiece>();

        // Find a client ID that isn't the server
        ulong clientId = 0;
        foreach (var client in NetworkManager.Singleton.ConnectedClients)
        {
            if (client.Key != NetworkManager.Singleton.LocalClientId)
            {
                clientId = client.Key;
                break;
            }
        }

        if (clientId == 0)
        {
            Debug.Log("No clients connected to assign black pieces to.");
            return;
        }

        int piecesMoved = 0;

        // Assign all black pieces to this client
        foreach (var piece in allPieces)
        {
            if (piece.PieceColor == Side.Black)
            {
                var netObj = piece.GetComponentInParent<NetworkObject>();
                if (netObj != null && netObj.IsSpawned)
                {
                    Debug.Log($"Changing ownership of {piece.name} to client {clientId}");
                    netObj.ChangeOwnership(clientId);
                    piecesMoved++;
                }
            }
        }

        Debug.Log($"Changed ownership of {piecesMoved} black pieces to client {clientId}");
    }
    //--------------------- Additional Methods ---------------------

    // Add these methods to myNetworkManager.cs
    private void ReconstructGameForClient(ulong clientId)
    {
        if (!NetworkManager.Singleton.IsServer) return;

        Debug.Log($"Reconstructing game state for client {clientId}");

        // First make sure the client has a board
        StartCoroutine(DelayedGameReconstruction(clientId));
    }

    private IEnumerator DelayedGameReconstruction(ulong clientId)
    {
        // Allow time for client to initialize
        yield return new WaitForSeconds(3.0f);

        // Step 1: Make sure the board squares exist on client
        SendReconstructBoardClientRpc();

        yield return new WaitForSeconds(1.0f);

        // Step 2: Synchronize pieces
        SendReconstructPiecesClientRpc();

        yield return new WaitForSeconds(1.0f);

        // Step 3: Assign ownership of black pieces to the client
        AssignBlackPiecesToClient(clientId);
    }

    [ClientRpc]
    private void SendReconstructBoardClientRpc()
    {
        if (!NetworkManager.Singleton.IsServer)
        {
            // Client should create board squares if they don't exist
            BoardManager.Instance.CreateBoardSquares();
        }
    }

    [ClientRpc]
    private void SendReconstructPiecesClientRpc()
    {
        if (!NetworkManager.Singleton.IsServer)
        {
            // Request the current game state from server
            RequestGameStateServerRpc();
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestGameStateServerRpc(ServerRpcParams serverRpcParams = default)
    {
        ulong clientId = serverRpcParams.Receive.SenderClientId;
        Debug.Log($"Client {clientId} requested game state");

        // Force a new game with current state
        GameManager.Instance.SynchronizeGameStateToClient(clientId);
    }

    private void AssignBlackPiecesToClient(ulong clientId)
    {
        if (!NetworkManager.Singleton.IsServer) return;

        VisualPiece[] allPieces = FindObjectsOfType<VisualPiece>();
        int reassignedCount = 0;

        foreach (var piece in allPieces)
        {
            if (piece.PieceColor == Side.Black)
            {
                NetworkObject netObj = piece.GetComponentInParent<NetworkObject>();
                if (netObj != null && netObj.IsSpawned)
                {
                    netObj.ChangeOwnership(clientId);
                    reassignedCount++;
                }
            }
        }

        Debug.Log($"Assigned {reassignedCount} black pieces to client {clientId}");
    }
}
