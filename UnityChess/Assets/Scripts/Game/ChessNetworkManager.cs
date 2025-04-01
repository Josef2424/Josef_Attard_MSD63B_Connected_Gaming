using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using UnityChess;

/// <summary>
/// Manages the network functionality for the chess game, handling connections, 
/// game sessions, and move synchronization without using Unity Relay.
/// </summary>
public class ChessNetworkManager : NetworkBehaviour
{
    // Singleton instance
    public static ChessNetworkManager Instance { get; private set; }

    public delegate void ChessMoveEvent(Square from, Square to, Transform pieceTransform, Transform squareTransform,
        Piece promotionPiece);

    public static event ChessMoveEvent OnChessMove;
    private bool isGameUiVisible = false;

    // UI References
    [Header("Connection UI")] [SerializeField]
    private GameObject connectionPanel;

    [SerializeField] private Button hostButton;
    [SerializeField] private Button clientButton;
    [SerializeField] private Button rejoinButton;
    [SerializeField] private Button disconnectButton;
    [SerializeField] private InputField ipAddressInput;
    [SerializeField] private Text connectionStatusText;

    [Header("Game Status")] [SerializeField]
    private Text playerSideText;

    [SerializeField] private Text turnIndicatorText;

    private bool isRestoringState = false;
    private int currentRetryCount = 0;
    [SerializeField] private int maxSyncRetries = 3;

    [Header("Game UI")] [SerializeField] private GameObject gamePanel;

    // Port for the game server
    [SerializeField] private ushort networkPort = 7777;

    [Header("Network Diagnostics")] [SerializeField]
    private float pingInterval = 2.0f; // How often to measure ping in seconds

    private float lastPingTime;

    private Dictionary<ulong, float>
        clientPingTimes = new Dictionary<ulong, float>(); // Stores the send time for ping requests

    private Dictionary<ulong, int> lastPingResults = new Dictionary<ulong, int>();


    // Dictionary to store connected players and their assigned sides
    public Dictionary<ulong, Side> playerSides = new Dictionary<ulong, Side>();

    [Header("Debug Settings")] [SerializeField]
    private bool enableDetailedLogs = true;

    [SerializeField] private bool enableNetworkLogs = true;
    [SerializeField] private bool enableStateLogs = true;

    // Game state
    public bool isMultiplayerGameActive = false;

    // Events
    public event Action<ulong> OnPlayerJoined;
    public event Action<ulong> OnPlayerLeft;
    public event Action OnConnectionFailed;

    private void Awake()
    {
        // Ensure singleton behavior
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
        // Set up UI button listeners
        hostButton.onClick.AddListener(CreateGame);
        clientButton.onClick.AddListener(JoinGame);
        rejoinButton.onClick.AddListener(RejoinGame);
        disconnectButton.onClick.AddListener(LeaveGame);

        // Initialize the UI
        InitializeUI();

        // Set up network event handlers
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;

        // Subscribe to the GameManager's MoveExecutedEvent instead of VisualPiece.VisualPieceMoved
        GameManager.MoveExecutedEvent += OnMoveExecuted;

        //Initialise ping measurement
        lastPingTime = 0f;
        clientPingTimes.Clear();
        lastPingResults.Clear();
    }

    private void OnDestroy()
    {
        // Unsubscribe from events
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnect;
        }

        GameManager.MoveExecutedEvent -= OnMoveExecuted;
        //VisualPiece.VisualPieceMoved -= InterceptPieceMove;
    }

    public void UpdateUIButtons()
    {
        bool isConnected = NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient;
        bool isInGame = isMultiplayerGameActive;

        // ENSURE ALL BUTTONS ARE ALWAYS VISIBLE
        if (hostButton != null) hostButton.gameObject.SetActive(true);
        if (clientButton != null) clientButton.gameObject.SetActive(true);
        if (ipAddressInput != null) ipAddressInput.gameObject.SetActive(true);
        if (disconnectButton != null) disconnectButton.gameObject.SetActive(true);
        if (rejoinButton != null) rejoinButton.gameObject.SetActive(true);

        // Enable/disable buttons based on connection state
        if (hostButton != null) hostButton.interactable = !isConnected;
        if (clientButton != null) clientButton.interactable = !isConnected;
        if (ipAddressInput != null) ipAddressInput.interactable = !isConnected;
        if (disconnectButton != null) disconnectButton.interactable = isConnected;

        // Rejoin button is only interactable if we're disconnected but a game exists
        if (rejoinButton != null) rejoinButton.interactable = !isConnected && isInGame;

        Debug.Log($"UI Updated - Connected: {isConnected}, In Game: {isInGame}");
    }

    /// <summary>
    /// Initializes button visibility and interactability 
    /// </summary>
    private void InitializeUI()
    {
        // Make sure all buttons are visible
        if (hostButton != null) hostButton.gameObject.SetActive(true);
        if (clientButton != null) clientButton.gameObject.SetActive(true);
        if (ipAddressInput != null) ipAddressInput.gameObject.SetActive(true);
        if (disconnectButton != null) disconnectButton.gameObject.SetActive(true);
        if (rejoinButton != null) rejoinButton.gameObject.SetActive(true);

        // Set initial interactability
        if (disconnectButton != null) disconnectButton.interactable = false;
        if (rejoinButton != null) rejoinButton.interactable = false;

        // Make sure all UI panels are visible
        if (connectionPanel != null) connectionPanel.SetActive(true);
        if (gamePanel != null) gamePanel.SetActive(true);
    }

    /// <summary>
    /// Creates a new game as the host
    /// </summary>
    public void CreateGame()
    {
        try
        {
            Debug.Log("Starting host...");

            if (NetworkManager.Singleton == null)
            {
                Debug.LogError("NetworkManager.Singleton is null! Make sure NetworkManager exists in the scene.");
                return;
            }

            // Use direct property setting instead of method call
            var transport = NetworkManager.Singleton.GetComponent<Unity.Netcode.Transports.UTP.UnityTransport>();
            if (transport != null)
            {
                transport.ConnectionData.Address = "127.0.0.1";
                transport.ConnectionData.Port = networkPort;
            }

            bool success = NetworkManager.Singleton.StartHost();
            Debug.Log("StartHost result: " + success);

            if (success)
            {
                playerSides[NetworkManager.Singleton.LocalClientId] = Side.White;

                // Update UI
                if (playerSideText != null)
                    UpdatePlayerSideDisplay(Side.White);

                UpdateConnectionStatus("Hosting game. Waiting for oppponent to join...");

                // Update button interactability, keeping all buttons visible
                UpdateUIButtons();
            }
            else
            {
                UpdateConnectionStatus("Failed to start host.");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Exception in CreateGame: {e.Message}\n{e.StackTrace}");
            UpdateConnectionStatus($"Error: {e.Message}");
        }
    }

    /// <summary>
    /// Joins an existing game as a client
    /// </summary>
    public void JoinGame()
    {
        Debug.Log("JoinGame method called");

        // Check if NetworkManager.Singleton exists
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("NetworkManager.Singleton is null when trying to join game");
            UpdateConnectionStatus("Error: Network manager not found");
            return;
        }

        try
        {
            UpdateConnectionStatus("Connecting to host...");

            // Get the transport component safely
            var transport = NetworkManager.Singleton.GetComponent<Unity.Netcode.Transports.UTP.UnityTransport>();
            if (transport == null)
            {
                Debug.LogError("Unity Transport component not found on NetworkManager");
                UpdateConnectionStatus("Error: Transport component not found");
                return;
            }

            // Get IP address from input field, with proper validation
            string ipAddress = "127.0.0.1"; // Default to localhost

            if (ipAddressInput != null)
            {
                string inputText = ipAddressInput.text;
                // Only use the input if it's not empty and looks like a valid IP format
                if (!string.IsNullOrWhiteSpace(inputText) &&
                    (inputText == "localhost" ||
                     System.Text.RegularExpressions.Regex.IsMatch(inputText, @"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$")))
                {
                    ipAddress = inputText.Trim();
                }

                Debug.Log($"Using IP address: '{ipAddress}'");
            }
            else
            {
                Debug.LogWarning("ipAddressInput is null, using default localhost (127.0.0.1)");
            }

            // Configure the transport with validated IP
            transport.ConnectionData.Address = ipAddress;
            transport.ConnectionData.Port = networkPort;

            Debug.Log($"Connecting to: {ipAddress}:{networkPort}");

            // Start as client
            if (NetworkManager.Singleton.StartClient())
            {
                UpdateConnectionStatus($"Connecting to {ipAddress}...");

                // Update button interactability (all buttons remain visible)
                UpdateUIButtons();
            }
            else
            {
                UpdateConnectionStatus("Failed to connect to host.");
                OnConnectionFailed?.Invoke();
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to join game: {e.Message}\n{e.StackTrace}");
            UpdateConnectionStatus($"Failed to join game: {e.Message}");
            OnConnectionFailed?.Invoke();
        }
    }

    /// <summary>
    /// Rejoins an existing game session
    /// </summary>
    public void RejoinGame()
    {
        Debug.Log("RejoinGame method called");

        // First check if there's a network manager running
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("NetworkManager.Singleton is null when trying to rejoin");
            UpdateConnectionStatus("Error: Network manager not found");
            return;
        }

        // If we're already connected, don't rejoin
        if (NetworkManager.Singleton.IsConnectedClient)
        {
            Debug.Log("Already connected to a game");
            return;
        }

        try
        {
            // Update UI
            UpdateConnectionStatus("Attempting to rejoin game...");

            // Get the IP address from the input field
            string ipAddress = "127.0.0.1"; // Default to localhost
            if (ipAddressInput != null && !string.IsNullOrWhiteSpace(ipAddressInput.text))
            {
                ipAddress = ipAddressInput.text.Trim();
            }

            Debug.Log($"Rejoining to IP: {ipAddress}");

            // Configure the transport
            var transport = NetworkManager.Singleton.GetComponent<Unity.Netcode.Transports.UTP.UnityTransport>();
            if (transport != null)
            {
                transport.ConnectionData.Address = ipAddress;
                transport.ConnectionData.Port = networkPort;
            }

            // Start as client
            if (NetworkManager.Singleton.StartClient())
            {
                Debug.Log($"Successfully started client for rejoin to: {ipAddress}:{networkPort}");

                // Mark the game as active immediately
                isMultiplayerGameActive = true;

                // Update UI buttons
                UpdateUIButtons();

                // Request game state synchronization using the dedicated synchronizer
                StartCoroutine(DelayedGameStateSyncRequest());

                // Let the RejoinManager handle the process if available
                if (RejoinManager.Instance != null)
                {
                    RejoinManager.Instance.LogInfo("RejoinGame called from ChessNetworkManager");
                    RejoinManager.Instance.HandleRejoinProcess();
                }
                else
                {
                    // Otherwise use our simplified rejoin process
                    StartCoroutine(RejoinProcess());
                }
            }
            else
            {
                Debug.LogError("Failed to start client for rejoin");
                UpdateConnectionStatus("Failed to rejoin. The host may have closed the game.");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error during rejoin: {e.Message}\n{e.StackTrace}");
            UpdateConnectionStatus($"Rejoin failed: {e.Message}");
        }
    }

    private System.Collections.IEnumerator DelayedGameStateSyncRequest()
    {
        // Wait for connection to stabilize
        yield return new WaitForSeconds(0.5f);

        // Check if GameStateSynchronizer exists
        if (GameStateSynchronizer.Instance != null)
        {
            Debug.Log("Requesting game state sync via GameStateSynchronizer");
            GameStateSynchronizer.Instance.RequestGameStateSyncServerRpc();
        }
        else
        {
            Debug.LogWarning("GameStateSynchronizer not found, using fallback method");
            RequestGameStateSyncServerRpc();
        }
    }

    /// <summary>
    /// Disconnects from the current game session
    /// </summary>
    public void LeaveGame()
    {
        Debug.Log("LeaveGame method called");

        // Check if NetworkManager exists and we're connected
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("NetworkManager.Singleton is null when trying to leave game");
            return;
        }

        try
        {
            if (NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsClient)
            {
                //Store whether we are the host before shutting down
                bool wasHost = NetworkManager.Singleton.IsHost;
                bool wasClient = NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsHost;
                bool wasInGame = isMultiplayerGameActive;

                Debug.Log($"Shutting down network connection (Host: {wasHost}, Client: {wasClient})");

                // Shut down the network connection
                NetworkManager.Singleton.Shutdown();

                Debug.Log("Network connection shutdown successful");

                // Clear player dictionary
                playerSides.Clear();

                // Keep track of the game state for potential rejoin
                // Only set to false if we were the host - client can rejoin
                if (wasHost)
                {
                    isMultiplayerGameActive = false;
                }

                isGameUiVisible = false;

                // Reset UI
                UpdateConnectionStatus(wasHost
                    ? "Disconnected. Ready to start a new game."
                    : "Disconnected from host.");

                // Update button interactability (all buttons remain visible)
                UpdateUIButtons();

                Debug.Log("Button interactability updated");

                // Hide turn indicator by setting empty text
                if (turnIndicatorText != null)
                {
                    turnIndicatorText.text = "";
                    Debug.Log("Turn indicator text cleared");
                }
            }
            else
            {
                Debug.Log("Not connected to any game - nothing to disconnect from");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error in LeaveGame: {e.Message}\n{e.StackTrace}");
        }
    }

    /// <summary>
    /// Update the connection status text displayed to the user
    /// </summary>
    public void UpdateConnectionStatus(string message)
    {
        connectionStatusText.text = message;
        Debug.Log($"Connection status: {message}");
    }

    /// <summary>
    /// Called when a client connects to the network
    /// </summary>
    private void OnClientConnected(ulong clientId)
    {
        Debug.Log($"Client connected: {clientId}");

        // If the connecting player is not the host
        if (clientId != NetworkManager.Singleton.LocalClientId && NetworkManager.Singleton.IsHost)
        {
            // Check if this player was previously in the game (rejoining)
            bool isRejoining = playerSides.ContainsKey(clientId);
            Debug.Log($"Client {clientId} is rejoining: {isRejoining}");

            if (!isRejoining)
            {
                // New client - add to players dictionary as Black
                playerSides[clientId] = Side.Black;

                // Inform the game that a player has joined
                UpdateConnectionStatus("Opponent connected. Game starting.");
                OnPlayerJoined?.Invoke(clientId);

                // Tell the client they are the black side
                AssignPlayerSideClientRpc(clientId, (int)Side.Black);

                // Start a new game when a new player joins
                StartGameClientRpc();
            }
            else
            {
                // This is a rejoining player, restore their previous side
                Side previousSide = playerSides[clientId];
                UpdateConnectionStatus($"Opponent reconnected as {previousSide}.");

                // Tell them which side they were playing as
                AssignPlayerSideClientRpc(clientId, (int)previousSide);

                // IMPORTANT: Send current game state WITHOUT starting a new game
                Debug.Log("Sending current game state to rejoining client without starting new game");
                string currentState = GameManager.Instance.SerializeGame();

                // Send the current game state to the rejoining client
                SyncGameStateClientRpc(currentState, false, new ClientRpcParams
                {
                    Send = new ClientRpcSendParams
                    {
                        TargetClientIds = new[] { clientId }
                    }
                });
            }
        }
        else if (NetworkManager.Singleton.IsClient && clientId == NetworkManager.Singleton.LocalClientId)
        {
            UpdateConnectionStatus("Connected to host.");
            // Update UI buttons
            UpdateUIButtons();

            // If we're rejoining, request the current game state
            if (isMultiplayerGameActive)
            {
                Debug.Log("Client is rejoining - requesting current game state");

                // Try to use GameStateSynchronizer first
                if (GameStateSynchronizer.Instance != null)
                {
                    Debug.Log("Using GameStateSynchronizer for state sync");
                    StartCoroutine(DelayedGameStateSyncRequest());
                }
                else
                {
                    // Fall back to direct method
                    RequestGameStateSyncServerRpc();
                }
            }
        }
    }

    public void OnRejoinGame(ulong clientId, Side side)
    {
        Debug.Log($"OnRejoinGame called for client {clientId} with side {side}");

        // Store the side information
        playerSides[clientId] = side;

        // Mark the game as active
        isMultiplayerGameActive = true;

        // Update UI
        UpdateConnectionStatus($"Rejoined as {side}");

        // Make game UI visible
        if (gamePanel != null)
        {
            gamePanel.SetActive(true);
            isGameUiVisible = true;
        }

        // Update UI buttons
        UpdateUIButtons();

        // Request the current side to move (turn) to update the turn indicator
        if (GameManager.Instance != null)
        {
            // This should trigger events that update the UI
            GameManager.Instance.ResetGameToHalfMoveIndex(GameManager.Instance.LatestHalfMoveIndex);
        }
    }

    /// <summary>
    /// Called when a client disconnects from the network
    /// </summary>
    private void OnClientDisconnect(ulong clientId)
    {
        Debug.Log($"[NETWORK] Client {clientId} disconnected.");

        bool isLocalClientDisconnect = clientId == NetworkManager.Singleton.LocalClientId;

        if (playerSides.ContainsKey(clientId))
        {
            OnPlayerLeft?.Invoke(clientId);
        }

        // If we're the host and a client disconnected
        if (NetworkManager.Singleton.IsHost && !isLocalClientDisconnect)
        {
            UpdateConnectionStatus("Opponent disconnected. Waiting for reconnection...");
        }
        // If we're disconnecting as a client
        else if (isLocalClientDisconnect)
        {
            // For client disconnects, update button interactability but keep all buttons visible
            UpdateUIButtons();

            // Update connection status
            UpdateConnectionStatus("Disconnected from host. Click Rejoin to reconnect.");
        }

        // Check if we had ping results for this client
        if (lastPingResults.TryGetValue(clientId, out int lastPing))
        {
            Debug.Log($"[NETWORK] Client {clientId} disconnected. Last measured ping: {lastPing}ms");
        }
        else
        {
            Debug.Log($"[NETWORK] Client {clientId} disconnected. No ping measurement available.");
        }
    }

    /// <summary>
    /// Assigns a side to a client
    /// </summary>
    [ClientRpc]
    private void AssignPlayerSideClientRpc(ulong clientId, int sideValue)
    {
        if (NetworkManager.Singleton.LocalClientId == clientId)
        {
            Side assignedSide = (Side)sideValue; // Make sure this cast is correct
            Debug.Log($"I've been assigned side: {assignedSide}");

            // Add this client to the local players dictionary
            playerSides[clientId] = assignedSide;

            // Update UI
            UpdatePlayerSideDisplay(assignedSide);
            UpdateConnectionStatus($"Connected as {assignedSide}. Waiting for game to start.");
        }
    }

    /// <summary>
    /// Starts the game on all clients
    /// </summary>
    [ClientRpc]
    private void StartGameClientRpc()
    {
        isMultiplayerGameActive = true;

        // Tell the GameManager to start a new game
        GameManager.Instance.StartNewGame();

        // We don't need to spawn all network objects anymore
        // Just make sure ChessMoveRelay is active
        EnsureMoveRelayIsActive();

        // IMPORTANT: Update pieces to only allow movement of own side
        // Call this BEFORE UI updates to ensure pieces are properly controlled
        UpdatePieceControl();

        //Update the turn indicator
        UpdateTurnIndicator();

        // Update UI with message
        UpdateConnectionStatus("Game started!");

        // Update button interactability
        UpdateUIButtons();
    }

    private void EnsureMoveRelayIsActive()
    {
        // Find or create the ChessMoveRelay
        ChessMoveRelay relay = FindObjectOfType<ChessMoveRelay>();
        if (relay == null)
        {
            Debug.Log("Creating new ChessMoveRelay GameObject");
            GameObject relayObj = new GameObject("ChessMoveRelay");
            relay = relayObj.AddComponent<ChessMoveRelay>();

            // Assign the board reference
            GameObject board = GameObject.FindGameObjectWithTag("Board");
            if (board != null)
            {
                relay.GetComponent<ChessMoveRelay>().chessBoard = board;
            }

            // Add NetworkObject component
            NetworkObject netObj = relayObj.AddComponent<NetworkObject>();

            // Spawn the relay object if we're the server
            if (NetworkManager.Singleton.IsServer && !netObj.IsSpawned)
            {
                netObj.Spawn();
                Debug.Log("ChessMoveRelay spawned successfully");
            }
        }
        else
        {
            Debug.Log("ChessMoveRelay already exists");
        }
    }

    private void EnsureAllNetworkObjectsSpawned()
    {
        // If we're the server, handle NetworkObjects properly
        if (NetworkManager.Singleton.IsServer)
        {
            // First, find pieces that need to be network-controlled
            VisualPiece[] allPieces = FindObjectsOfType<VisualPiece>(true);

            foreach (VisualPiece piece in allPieces)
            {
                GameObject pieceObj = piece.gameObject;
                NetworkObject netObj = pieceObj.GetComponent<NetworkObject>();

                // Add NetworkObject if missing
                if (netObj == null)
                {
                    netObj = pieceObj.AddComponent<NetworkObject>();
                }

                // Only spawn if not already spawned
                if (!netObj.IsSpawned)
                {
                    Debug.Log($"Spawning network object for piece: {pieceObj.name}");
                    netObj.Spawn();
                }
            }

            // We don't need to spawn static board squares - they should be set up
            // but not spawned individually as this creates conflicts
        }
    }

    /// <summary>
    /// Returns the side (White/Black) assigned to the specified client
    /// </summary>
    public Side GetPlayerSide(ulong clientId)
    {
        if (playerSides.TryGetValue(clientId, out Side side))
        {
            return side;
        }

        return Side.None;
    }

    /// <summary>
    /// Checks if the local player is allowed to move pieces of the given side
    /// </summary>
    public bool CanControlSide(Side side)
    {
        if (!NetworkManager.Singleton.IsConnectedClient)
        {
            Debug.Log("CanControlSide: Not in network mode, allowing all pieces");
            return true;
        }

        // Get the side of the local player
        Side localPlayerSide = GetPlayerSide(NetworkManager.Singleton.LocalClientId);
        Debug.Log(
            $"CanControlSide check: Local player ID {NetworkManager.Singleton.LocalClientId} has side {localPlayerSide}, checking against {side}");

        // Check if the side matches the local player's side
        bool canControl = side == localPlayerSide;
        Debug.Log($"CanControlSide result: {canControl}");
        return canControl;
    }

    /// <summary>
    /// Updates the player side display in the UI
    /// </summary>
    private void UpdatePlayerSideDisplay(Side side)
    {
        playerSideText.text = $"You are playing as: {side}";
    }

    /// <summary>
    /// Updates piece controls to strictly enforce turn-based movement
    /// </summary>
    public void UpdatePieceControl()
    {
        // If not in network mode, default behavior applies
        if (!NetworkManager.Singleton.IsConnectedClient)
        {
            Debug.Log("Not in network mode, allowing all pieces to be controlled");
            return;
        }

        // If game is not active, don't apply network controls
        if (!isMultiplayerGameActive)
        {
            Debug.Log("Not in multiplayer game, not restricting piece control");
            return;
        }

        // Get the local player's side
        Side localPlayerSide = GetPlayerSide(NetworkManager.Singleton.LocalClientId);

        // Get the current side to move from the game manager
        Side currentTurn = GameManager.Instance.SideToMove;

        Debug.Log($"Updating piece control: Local player is {localPlayerSide}, current turn is {currentTurn}");

        // Get all visual pieces
        VisualPiece[] allPieces = FindObjectsOfType<VisualPiece>(true);
        int enabledCount = 0;
        int disabledCount = 0;

        foreach (VisualPiece piece in allPieces)
        {
            // IMPORTANT: First record current state for debug purposes
            bool wasEnabled = piece.enabled;

            // Then disable ALL pieces
            piece.enabled = false;
            disabledCount++;

            // Only enable pieces if ALL these conditions are met:
            // 1. It matches the player's side
            // 2. It's that player's turn
            // 3. The piece has legal moves
            if (piece.PieceColor == localPlayerSide && localPlayerSide == currentTurn)
            {
                Piece chessPiece = GameManager.Instance.CurrentBoard[piece.CurrentSquare];
                if (chessPiece != null && GameManager.Instance.HasLegalMoves(chessPiece))
                {
                    piece.enabled = true;
                    enabledCount++;
                    disabledCount--;

                    // If the state changed, log it
                    if (!wasEnabled)
                    {
                        Debug.Log($"Enabled piece at {piece.CurrentSquare}");
                    }
                }
            }
            else if (wasEnabled)
            {
                // If we disabled a previously enabled piece, log it
                Debug.Log($"Disabled piece at {piece.CurrentSquare} (Color: {piece.PieceColor}, Turn: {currentTurn})");
            }
        }

        Debug.Log(
            $"Piece control updated: {enabledCount} pieces enabled, {disabledCount} pieces disabled for {localPlayerSide}");
    }

    private void TransitionToGameUI()
    {
        Debug.Log("TransitionToGameUI called");

        // Update status message
        UpdateConnectionStatus("Game in progress");

        // Update player control permissions
        UpdatePieceControl();
    }

    /// <summary>
    /// Updates the turn indicator text to show whose turn it is
    /// Enhanced to be more explicit about turns
    /// </summary>
    public void UpdateTurnIndicator()
    {
        if (turnIndicatorText == null) return;

        // If we're not in a multiplayer game, clear the indicator
        if (!isMultiplayerGameActive || !NetworkManager.Singleton.IsConnectedClient)
        {
            turnIndicatorText.text = "";
            return;
        }

        Side currentTurn = GameManager.Instance.SideToMove;
        Side localPlayerSide = GetPlayerSide(NetworkManager.Singleton.LocalClientId);

        Debug.Log($"Updating turn indicator: Current turn is {currentTurn}, local player is {localPlayerSide}");

        if (currentTurn == localPlayerSide)
        {
            turnIndicatorText.text = "YOUR TURN";
            turnIndicatorText.color = Color.green;
        }
        else
        {
            turnIndicatorText.text = "OPPONENT'S TURN";
            turnIndicatorText.color = Color.red;
        }

        // Force UI update
        if (turnIndicatorText.gameObject.activeInHierarchy)
        {
            turnIndicatorText.gameObject.SetActive(false);
            turnIndicatorText.gameObject.SetActive(true);
        }
    }

    private void Update()
    {
        // Check network connection status every 2 seconds
        if (Time.frameCount % 120 == 0) // 60 fps × 2 seconds = 120 frames
        {
            // Update UI buttons based on current state
            UpdateUIButtons();

            // Make sure both panels are always visible
            if (connectionPanel != null && !connectionPanel.activeInHierarchy)
            {
                connectionPanel.SetActive(true);
            }

            if (gamePanel != null && !gamePanel.activeInHierarchy)
            {
                gamePanel.SetActive(true);
            }
        }

        // Log network stats periodically
        if (Time.frameCount % 300 == 0) // Every 5 seconds at 60fps
        {
            LogNetworkStats();
        }

        // Measure ping at regular intervals
        if (Time.time - lastPingTime > pingInterval)
        {
            lastPingTime = Time.time;
            MeasurePing();
        }

        // Add a safety check every second to ensure turn-based controls are working
        if (isMultiplayerGameActive && NetworkManager.Singleton.IsConnectedClient && Time.frameCount % 60 == 0)
        {
            // Get the current side to move
            Side currentTurn = GameManager.Instance.SideToMove;
            Side localPlayerSide = GetPlayerSide(NetworkManager.Singleton.LocalClientId);

            // Check if any pieces are enabled that shouldn't be
            bool needsRefresh = false;
            VisualPiece[] allPieces = FindObjectsOfType<VisualPiece>(true);

            foreach (VisualPiece piece in allPieces)
            {
                if (piece.enabled)
                {
                    // If a piece is enabled when it shouldn't be, mark for refresh
                    if (piece.PieceColor != localPlayerSide ||
                        localPlayerSide != currentTurn ||
                        !GameManager.Instance.HasLegalMoves(GameManager.Instance.CurrentBoard[piece.CurrentSquare]))
                    {
                        needsRefresh = true;
                        Debug.LogWarning(
                            $"Found piece that should be disabled: {piece.gameObject.name} at {piece.CurrentSquare}");
                        break;
                    }
                }
                else if (piece.PieceColor == localPlayerSide && localPlayerSide == currentTurn)
                {
                    // If a piece is disabled when it should be enabled, check if it has legal moves
                    Piece chessPiece = GameManager.Instance.CurrentBoard[piece.CurrentSquare];
                    if (chessPiece != null && GameManager.Instance.HasLegalMoves(chessPiece))
                    {
                        needsRefresh = true;
                        Debug.LogWarning(
                            $"Found piece that should be enabled: {piece.gameObject.name} at {piece.CurrentSquare}");
                        break;
                    }
                }
            }

            // If turn control is out of sync, refresh it
            if (needsRefresh)
            {
                Debug.Log("Turn control is out of sync - refreshing piece control");
                UpdatePieceControl();
            }
        }
    }

    /// <summary>
    /// Sends the current game state to a specific client
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestGameStateSyncServerRpc(ServerRpcParams serverRpcParams = default)
    {
        if (!NetworkManager.Singleton.IsServer) return;

        // Get the client ID that sent the request
        ulong clientId = serverRpcParams.Receive.SenderClientId;
        Debug.Log($"Received game state sync request from client {clientId}");

        // Generate a serialized game state
        string gameState = GameManager.Instance.SerializeGame();

        // Send the game state to the requesting client
        SyncGameStateClientRpc(gameState, true, new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { clientId }
            }
        });
    }

    /// <summary>
    /// Receives and applies the current game state from the server with option to restart game
    /// </summary>
    [ClientRpc]
    public void SyncGameStateClientRpc(string serializedGameState, bool restartGame = true,
        ClientRpcParams clientRpcParams = default)
    {
        // Skip if we're the host - the host already has the correct state
        if (IsHost)
        {
            Debug.Log("Host received SyncGameStateClientRpc but ignoring as we're the host");
            return;
        }

        Debug.Log(
            $"Client received game state from server, length: {serializedGameState?.Length ?? 0}, restartGame: {restartGame}");

        // If we received an empty state, exit early
        if (string.IsNullOrEmpty(serializedGameState))
        {
            Debug.LogError("Received empty game state from server!");
            return;
        }

        try
        {
            // Set the restoring state flag to prevent feedback loops
            isRestoringState = true;

            // Mark the game as active
            isMultiplayerGameActive = true;

            // Apply the game state through GameManager
            if (GameManager.Instance != null)
            {
                // Load the game state
                GameManager.Instance.LoadGame(serializedGameState);
                Debug.Log($"Game state successfully applied {(restartGame ? "with" : "without")} restart");

                // If this is a rejoin without restart, update controls directly
                if (!restartGame)
                {
                    // Make sure pieces reflect current player control
                    UpdatePieceControl();
                    UpdateTurnIndicator();

                    // Update UI
                    Side localSide = GetPlayerSide(NetworkManager.Singleton.LocalClientId);
                    Side currentTurn = GameManager.Instance.SideToMove;

                    UpdateConnectionStatus($"Rejoined as {localSide}. " +
                                           (localSide == currentTurn ? "Your turn." : "Waiting for opponent."));
                }

                // Make sure game is marked as active
                MarkGameAsActive();

                // Wait a bit longer before finalizing state to ensure pieces are created
                StartCoroutine(SyncGameStateFinalize());

                // Reset retry counter on success
                currentRetryCount = 0;
            }
            else
            {
                Debug.LogError("GameManager.Instance is null, cannot load game state!");

                // Try again if we haven't exceeded max retries
                if (currentRetryCount < maxSyncRetries)
                {
                    currentRetryCount++;
                    Debug.Log($"Retrying state sync (attempt {currentRetryCount}/{maxSyncRetries})");
                    StartCoroutine(RetryRequestGameState());
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error applying game state: {e.Message}\n{e.StackTrace}");

            // Try again if we haven't exceeded max retries
            if (currentRetryCount < maxSyncRetries)
            {
                currentRetryCount++;
                Debug.Log($"Retrying state sync after error (attempt {currentRetryCount}/{maxSyncRetries})");
                StartCoroutine(RetryRequestGameState());
            }
        }
        finally
        {
            // Clear the restoring state flag
            isRestoringState = false;
        }
    }

    private System.Collections.IEnumerator SyncGameStateFinalize()
    {
        // Wait a frame for the game state to fully apply
        yield return null;

        // Give more time for the visual pieces to be created
        yield return new WaitForSeconds(0.3f);

        // Now that pieces are created, ensure proper turn-based controls
        if (ChessNetworkManager.Instance != null)
        {
            // Use the consolidated method for UI updates
            yield return StartCoroutine(UpdateUIAndControls(0.1f));

            // Force GameManager to reset to current half-move to ensure all events fire correctly
            GameManager.Instance.ResetGameToHalfMoveIndex(GameManager.Instance.LatestHalfMoveIndex);

            // Log detailed game state for debugging
            LogGameState();
        }
    }

    private System.Collections.IEnumerator RetryRequestGameState()
    {
        // Wait a second before retrying
        yield return new WaitForSeconds(1.0f);

        if (IsClient && !IsHost)
        {
            Debug.Log($"Retrying game state sync (attempt {currentRetryCount}/{maxSyncRetries})...");

            RequestGameStateSyncServerRpc();
        }
    }

    /// <summary>
    /// Add this to the OnEnable method to ensure buttons are properly initialized
    /// </summary>
    private void OnNetworkEnable()
    {
        // Initialize UI buttons
        UpdateUIButtons();
    }

    /// <summary>
    /// Determines if a move is a potential promotion move
    /// </summary>
    private bool IsPromotionMove(Side side, Square startSquare, Square endSquare)
    {
        // Check if it's a pawn
        Piece piece = GameManager.Instance.CurrentBoard[startSquare];
        if (!(piece is Pawn))
            return false;

        // Check if the move ends on the promotion rank
        return (side == Side.White && endSquare.Rank == 8) ||
               (side == Side.Black && endSquare.Rank == 1);
    }

    /// <summary>
    /// Determines the elected piece type from a promotion piece
    /// </summary>
    private ElectedPiece GetPromotionPieceType(Piece promotionPiece)
    {
        if (promotionPiece is Queen) return ElectedPiece.Queen;
        if (promotionPiece is Rook) return ElectedPiece.Rook;
        if (promotionPiece is Bishop) return ElectedPiece.Bishop;
        if (promotionPiece is Knight) return ElectedPiece.Knight;

        return ElectedPiece.Queen; // Default to queen
    }

    /// <summary>
    /// Shows or hides the connection UI
    /// </summary>
    public void ShowConnectionUI(bool show)
    {
        connectionPanel.SetActive(show);
    }

    /// <summary>
    /// Check if we're in a multiplayer game
    /// </summary>
    public bool IsInMultiplayerGame()
    {
        return isMultiplayerGameActive && NetworkManager.Singleton.IsConnectedClient;
    }

    /// <summary>
    /// Listen for game events that require updating piece control
    /// </summary>
    private void OnEnable()
    {
        if (GameManager.Instance != null)
        {
            GameManager.MoveExecutedEvent += OnMoveExecuted;
            // Add this line to handle game resets
            GameManager.GameResetToHalfMoveEvent += OnGameResetToHalfMove;
        }
    }

    /// <summary>
    /// Unsubscribe from game events
    /// </summary>
    private void OnDisable()
    {
        if (GameManager.Instance != null)
        {
            GameManager.MoveExecutedEvent -= OnMoveExecuted;
            // Remove the event handler
            GameManager.GameResetToHalfMoveEvent -= OnGameResetToHalfMove;
        }
    }

    private void OnGameResetToHalfMove()
    {
        if (isMultiplayerGameActive && NetworkManager.Singleton.IsConnectedClient)
        {
            Debug.Log("Game reset to half move - updating piece control");
            UpdateTurnIndicator();
            UpdatePieceControl();
        }
    }

    /// <summary>
    /// Called when a move is executed in the game
    /// Enhanced to ensure turn control is properly updated
    /// </summary>
    private void OnMoveExecuted()
    {
        if (!isMultiplayerGameActive || !NetworkManager.Singleton.IsConnectedClient)
        {
            return;
        }

        Debug.Log("Move executed - updating piece control and turn indicator");

        // Get the new side to move
        Side newSideToMove = GameManager.Instance.SideToMove;

        // Update turn indicator first
        UpdateTurnIndicator();

        // Then update piece control based on new turn
        UpdatePieceControl();

        // Log the new state
        Side localPlayerSide = GetPlayerSide(NetworkManager.Singleton.LocalClientId);
        bool isMyTurn = (localPlayerSide == newSideToMove);

        UpdateConnectionStatus(isMyTurn
            ? "Your turn to move"
            : "Waiting for opponent's move");

        Debug.Log($"Turn changed to {newSideToMove}, local player is {localPlayerSide}, isMyTurn: {isMyTurn}");
    }

    private void MeasurePing()
    {
        // Only send ping requests if we're connected
        if (!NetworkManager.Singleton.IsConnectedClient)
            return;

        // Store the current time when sending the ping with higher precision
        ulong localClientId = NetworkManager.Singleton.LocalClientId;
        clientPingTimes[localClientId] = (float)System.Diagnostics.Stopwatch.GetTimestamp() /
                                         System.Diagnostics.Stopwatch.Frequency;

        // Send ping to server
        PingServerRpc();

        // Log to console for debugging
        LogMessage($"Sent ping request at {Time.realtimeSinceStartup:F3}s", "Network");
    }

    [ClientRpc]
    private void PingResponseClientRpc(ClientRpcParams clientRpcParams = default)
    {
        // When client receives ping response, calculate the round-trip time with higher precision
        double currentTime = (double)System.Diagnostics.Stopwatch.GetTimestamp() /
                             System.Diagnostics.Stopwatch.Frequency;
        ulong localClientId = NetworkManager.Singleton.LocalClientId;

        if (clientPingTimes.TryGetValue(localClientId, out float startTime))
        {
            // Calculate ping time in milliseconds with higher precision
            double pingTimeSeconds = currentTime - startTime;
            int pingTimeMs = Mathf.RoundToInt((float)(pingTimeSeconds * 1000));

            // Store the calculated ping (minimum 1ms for display purposes)
            lastPingResults[localClientId] = Mathf.Max(1, pingTimeMs);

            // Log the ping measurement to console
            Debug.Log($"[PING] Measured latency: {lastPingResults[localClientId]}ms");
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void PingServerRpc(ServerRpcParams serverRpcParams = default)
    {
        // When server receives ping, immediately respond back to the client
        ulong clientId = serverRpcParams.Receive.SenderClientId;

        // Log the receipt of ping at server
        Debug.Log($"[PING] Server received ping from client {clientId}");

        // Send response back to the client
        PingResponseClientRpc(new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { clientId }
            }
        });
    }

    private void LogNetworkStats()
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
        {
            string role = NetworkManager.Singleton.IsHost ? "Host" : "Client";
            ulong localClientId = NetworkManager.Singleton.LocalClientId;

            // Only try to get client count on the server/host
            int connectedClients = 0;
            if (NetworkManager.Singleton.IsServer)
            {
                connectedClients = NetworkManager.Singleton.ConnectedClientsList.Count;
            }

            // Get last ping if available
            string pingInfo = "No ping data";
            if (lastPingResults.TryGetValue(localClientId, out int lastPing))
            {
                pingInfo = $"{lastPing}ms";
            }

            Debug.Log($"[NETWORK STATS] Role: {role}, ClientID: {localClientId}, " +
                      (NetworkManager.Singleton.IsServer ? $"Connected Clients: {connectedClients}, " : "") +
                      $"Last Measured Ping: {pingInfo}");
        }
    }

    /// <summary>
    /// Updates the player's side when rejoining a game
    /// </summary>
    public void UpdatePlayerSideOnRejoin(ulong clientId, Side side)
    {
        // Add this client to the local players dictionary if not already there
        playerSides[clientId] = side;

        // Mark the game as active since we're rejoining
        isMultiplayerGameActive = true;

        // Transition to game UI since we're in a game now
        TransitionToGameUI();
    }

    /// <summary>
    /// Wait a short time after connecting before requesting game state
    /// </summary>
    private System.Collections.IEnumerator RequestGameStateSyncAfterDelay()
    {
        // Wait for everything to initialize
        yield return new WaitForSeconds(0.5f);

        try
        {
            Debug.Log("Attempting to request game state sync");

            // Use the create method to ensure a valid synchronizer
            GameStateSynchronizer synchronizer = CreateGameStateSynchronizer();

            if (synchronizer != null)
            {
                // Only proceed if the network object is properly spawned
                NetworkObject netObj = synchronizer.GetComponent<NetworkObject>();
                if (netObj != null && netObj.IsSpawned)
                {
                    Debug.Log("Requesting game state sync from server...");
                    synchronizer.RequestGameStateSyncServerRpc();
                }
                else
                {
                    // If not spawned, use the direct method instead of RPC
                    Debug.Log("Using direct game state sync since NetworkObject isn't spawned");
                    RequestDirectGameStateSync();
                }
            }
            else
            {
                Debug.LogError("Failed to create GameStateSynchronizer!");
                // Fallback to direct sync
                RequestDirectGameStateSync();
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error during game state sync request: {e.Message}\n{e.StackTrace}");
            // Fallback to direct sync
            RequestDirectGameStateSync();
        }
    }

// Add this fallback method
    private void RequestDirectGameStateSync()
    {
        // Only for clients, not for host
        if (NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsHost)
        {
            Debug.Log("Requesting game state directly using RequestGameStateSyncServerRpc");
            RequestGameStateSyncServerRpc();
        }
    }

    /// <summary>
    /// Creates or finds the GameStateSynchronizer and ensures it's properly set up
    /// </summary>
    private GameStateSynchronizer CreateGameStateSynchronizer()
    {
        // Check if the synchronizer already exists
        GameStateSynchronizer synchronizer = FindObjectOfType<GameStateSynchronizer>();

        if (synchronizer == null)
        {
            Debug.Log("Creating new GameStateSynchronizer");
            GameObject syncObj = new GameObject("GameStateSynchronizer");
            synchronizer = syncObj.AddComponent<GameStateSynchronizer>();

            // Only add NetworkObject if we're in a networked game
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            {
                NetworkObject netObj = syncObj.AddComponent<NetworkObject>();

                // Important: Only the server should spawn network objects
                if (NetworkManager.Singleton.IsServer)
                {
                    netObj.Spawn();
                    Debug.Log("GameStateSynchronizer NetworkObject spawned");
                }
            }
        }

        return synchronizer;
    }

    /// <summary>
    /// Notifies a client that the game is already in progress (for rejoining)
    /// </summary>
    [ClientRpc]
    private void GameInProgressClientRpc(ulong clientId)
    {
        if (NetworkManager.Singleton.LocalClientId == clientId)
        {
            Debug.Log($"Received game in progress notification for client {clientId}");

            // Mark the game as active
            isMultiplayerGameActive = true;

            // Update UI
            UpdateConnectionStatus("Rejoined existing game. Synchronizing state...");

            // Ensure all panels are visible
            if (connectionPanel != null) connectionPanel.SetActive(true);
            if (gamePanel != null) gamePanel.SetActive(true);

            // Wait a moment for things to settle, then update game controls
            StartCoroutine(UpdateControlsAfterDelay());
        }
    }

    private System.Collections.IEnumerator UpdateControlsAfterDelay()
    {
        // Just use the consolidated method
        yield return StartCoroutine(UpdateUIAndControls());

        LogMessage("Rejoined game state fully synchronized", "State");
    }

    /// <summary>
    /// Marks the game as active - needed for rejoining clients
    /// </summary>
    public void MarkGameAsActive()
    {
        isMultiplayerGameActive = true;

        // Make sure game panel is visible
        if (gamePanel != null)
        {
            gamePanel.SetActive(true);
            isGameUiVisible = true;
        }

        // Update UI buttons
        UpdateUIButtons();

        Debug.Log("Game marked as active for multiplayer");
    }

    /// <summary>
    /// Synchronization process for rejoining players
    /// </summary>
    private System.Collections.IEnumerator RejoinProcess()
    {
        LogMessage("Starting rejoin process", "State");

        // Wait for connection to stabilize
        yield return new WaitForSeconds(0.5f);

        // Request game state from server
        RequestGameStateSyncServerRpc();

        // Wait for state to be applied
        yield return new WaitForSeconds(0.5f);

        // Update UI and controls
        yield return StartCoroutine(UpdateUIAndControls());

        LogMessage("Rejoin process completed", "State");
    }

    /// <summary>
    /// Forces a complete game state resync for all connected clients
    /// </summary>
    public void ForceGameStateResync()
    {
        if (!NetworkManager.Singleton.IsServer) return;

        Debug.Log("Forcing game state resync for all clients");

        try
        {
            // Generate the current game state
            string gameState = GameManager.Instance.SerializeGame();

            if (string.IsNullOrEmpty(gameState))
            {
                Debug.LogError("Failed to serialize game state for resync - game state is empty");
                return;
            }

            Debug.Log($"Generated game state for resync, length: {gameState.Length}");

            // Send it to all clients
            foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
            {
                // Skip the server itself if it's the host
                if (clientId == NetworkManager.Singleton.LocalClientId && NetworkManager.Singleton.IsHost)
                    continue;

                Debug.Log($"Sending forced state sync to client {clientId}");

                // Send the state
                SyncGameStateClientRpc(gameState, true, new ClientRpcParams
                {
                    Send = new ClientRpcSendParams
                    {
                        TargetClientIds = new[] { clientId }
                    }
                });
            }

            Debug.Log("Forced game state resync completed");
        }
        catch (Exception e)
        {
            Debug.LogError($"Error during forced game state resync: {e.Message}\n{e.StackTrace}");
        }
    }

    /// <summary>
    /// Logs the current game state for debugging purposes
    /// </summary>
    private void LogGameState()
    {
        try
        {
            // Get the current state
            Side currentTurn = GameManager.Instance.SideToMove;
            Side localSide = GetPlayerSide(NetworkManager.Singleton.LocalClientId);
            int halfMoveIndex = GameManager.Instance.LatestHalfMoveIndex;
            int pieceCount = GameManager.Instance.CurrentPieces.Count;

            // Log it
            Debug.Log($"[STATE] CurrentTurn: {currentTurn}, LocalSide: {localSide}, " +
                      $"HalfMoveIndex: {halfMoveIndex}, InMultiplayerGame: {isMultiplayerGameActive}, " +
                      $"Piece Count: {pieceCount}");

            // Log piece state
            VisualPiece[] allPieces = FindObjectsOfType<VisualPiece>(true);
            int enabledCount = 0;
            int whiteEnabledCount = 0;
            int blackEnabledCount = 0;

            foreach (VisualPiece piece in allPieces)
            {
                if (piece.enabled)
                {
                    enabledCount++;
                    if (piece.PieceColor == Side.White) whiteEnabledCount++;
                    if (piece.PieceColor == Side.Black) blackEnabledCount++;

                    // Only log a few pieces to avoid spamming the console
                    if (enabledCount <= 5)
                    {
                        Debug.Log($"[PIECE] Enabled piece at {piece.CurrentSquare}, Color: {piece.PieceColor}");
                    }
                }
            }

            Debug.Log($"[PIECES] Total: {allPieces.Length}, Enabled: {enabledCount}, " +
                      $"White enabled: {whiteEnabledCount}, Black enabled: {blackEnabledCount}");

            // Log network information
            if (NetworkManager.Singleton != null)
            {
                string role = NetworkManager.Singleton.IsHost
                    ? "Host"
                    : (NetworkManager.Singleton.IsServer
                        ? "Server"
                        : (NetworkManager.Singleton.IsClient ? "Client" : "None"));

                int connectedClientCount = NetworkManager.Singleton.IsServer
                    ? NetworkManager.Singleton.ConnectedClientsList.Count
                    : -1;

                Debug.Log($"[NETWORK] Role: {role}, Connected Clients: {connectedClientCount}, " +
                          $"Local ClientId: {NetworkManager.Singleton.LocalClientId}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error logging game state: {e.Message}");
        }
    }

    /// <summary>
    /// Updates UI controls and status based on current game state
    /// </summary>
    private System.Collections.IEnumerator UpdateUIAndControls(float delaySeconds = 0.5f)
    {
        // Wait for everything to initialize
        if (delaySeconds > 0)
            yield return new WaitForSeconds(delaySeconds);

        // Update UI elements
        UpdateTurnIndicator();
        UpdatePieceControl();
        UpdateUIButtons();

        // Update status message
        Side localSide = GetPlayerSide(NetworkManager.Singleton.LocalClientId);
        Side currentTurn = GameManager.Instance.SideToMove;

        string statusMessage = (localSide == currentTurn)
            ? $"Your turn to move. You are playing as {localSide}."
            : $"Waiting for opponent's move. You are playing as {localSide}.";

        UpdateConnectionStatus(statusMessage);

        LogMessage($"UI and controls updated - Playing as {localSide}, current turn is {currentTurn}", "State");
    }

    /// <summary>
    /// Conditionally logs a message with optional categories
    /// </summary>
    public void LogMessage(string message, string category = null, bool isError = false, bool forceLog = false)
    {
        // Simple category enable/disable system
        bool shouldLog = forceLog ||
                         enableDetailedLogs ||
                         (category == "Network" && enableNetworkLogs) ||
                         (category == "State" && enableStateLogs);

        if (!shouldLog)
            return;

        // Format the message with optional category prefix
        string formattedMessage = category != null ? $"[{category}] {message}" : message;

        // Choose the appropriate logging method
        if (isError)
            Debug.LogError(formattedMessage);
        else
            Debug.Log(formattedMessage);
    }
}