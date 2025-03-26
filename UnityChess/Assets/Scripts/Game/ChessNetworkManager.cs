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
    public delegate void ChessMoveEvent(Square from, Square to, Transform pieceTransform, Transform squareTransform, Piece promotionPiece);
    public static event ChessMoveEvent OnChessMove;
    private bool isGameUiVisible = false;

    // UI References
    [Header("Connection UI")]
    [SerializeField] private GameObject connectionPanel;
    [SerializeField] private Button hostButton;
    [SerializeField] private Button clientButton;
    [SerializeField] private Button disconnectButton;
    [SerializeField] private InputField ipAddressInput;
    [SerializeField] private Text connectionStatusText;
    [SerializeField] private Text playerSideText;
    [Header("Game UI")]
    [SerializeField] private GameObject gamePanel;

    // Port for the game server
    [SerializeField] private ushort networkPort = 7777;

    // Dictionary to store connected players and their assigned sides
    private Dictionary<ulong, Side> playerSides = new Dictionary<ulong, Side>();

    // Game state
    private bool isMultiplayerGameActive = false;

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
        disconnectButton.onClick.AddListener(LeaveGame);

        // Set initial UI state
        connectionPanel.SetActive(true);
        disconnectButton.gameObject.SetActive(false);

        // Set up network event handlers
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;

        // Subscribe to the GameManager's MoveExecutedEvent instead of VisualPiece.VisualPieceMoved
        GameManager.MoveExecutedEvent += OnMoveExecuted;

        // Have ONLY ONE subscription to VisualPiece.VisualPieceMoved
        //VisualPiece.VisualPieceMoved += InterceptPieceMove;
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

            // Rest of your method...
            if (success)
            {
                playerSides[NetworkManager.Singleton.LocalClientId] = Side.White;

                // Update UI
                if (playerSideText != null)
                    UpdatePlayerSideDisplay(Side.White);

                UpdateConnectionStatus("Hosting game. Waiting for oppponent to join...");

                if (hostButton != null) hostButton.gameObject.SetActive(false);
                if (clientButton != null) clientButton.gameObject.SetActive(false);
                if (ipAddressInput != null) ipAddressInput.gameObject.SetActive(false);
                if (disconnectButton != null) disconnectButton.gameObject.SetActive(true);
            }
            else
            {
                UpdateConnectionStatus("Failed to start host.");
            }
        }
        catch (Exception e)
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

                // Update UI safely
                if (hostButton != null) hostButton.gameObject.SetActive(false); // Hide host button
                if (clientButton != null) clientButton.gameObject.SetActive(false); // Hide client button
                if (ipAddressInput != null) ipAddressInput.gameObject.SetActive(false); // Hide IP input
                if (disconnectButton != null) disconnectButton.gameObject.SetActive(true); // Show disconnect button
            }
            else
            {
                UpdateConnectionStatus("Failed to connect to host.");
                OnConnectionFailed?.Invoke();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to join game: {e.Message}\n{e.StackTrace}");
            UpdateConnectionStatus($"Failed to join game: {e.Message}");
            OnConnectionFailed?.Invoke();
        }
    }

    /// <summary>
    /// Disconnects from the current game session
    /// </summary>
    public void LeaveGame()
    {
        if (NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsClient)
        {

            //Store whether we are the host before shutting down
            bool wasHost = NetworkManager.Singleton.IsHost;
            bool wasClient = NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsHost;

            // Shut down the network connection
            NetworkManager.Singleton.Shutdown();

            // Clear player dictionary
            playerSides.Clear();
            isMultiplayerGameActive = false;
            isGameUiVisible = false;

            // Reset UI
            UpdateConnectionStatus(wasHost ? "Disconnected. Ready to start a new game." : "Disconnected from host. Ready to start a new game.");
            // Show connection UI
            if (connectionPanel != null) connectionPanel.SetActive(true);
            if (gamePanel != null) gamePanel.SetActive(false);

            // Show buttons
            if (hostButton != null) hostButton.gameObject.SetActive(true);
            if (clientButton != null) clientButton.gameObject.SetActive(true);
            if (ipAddressInput != null) ipAddressInput.gameObject.SetActive(true);
            if (disconnectButton != null) disconnectButton.gameObject.SetActive(false);
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

        // If the connecting player is not the host, assign them to the black side
        if (clientId != NetworkManager.Singleton.LocalClientId && NetworkManager.Singleton.IsHost)
        {
            // Add client to players dictionary as Black
            playerSides[clientId] = Side.Black;

            // Inform the game that a player has joined
            UpdateConnectionStatus("Opponent connected. Game starting.");
            OnPlayerJoined?.Invoke(clientId);

            // Tell the client they are the black side
            AssignPlayerSideClientRpc(clientId, (int)Side.Black);

            // Start the game when both players are connected
            StartGameClientRpc();
        }
        else if (NetworkManager.Singleton.IsClient && clientId == NetworkManager.Singleton.LocalClientId)
        {
            UpdateConnectionStatus("Connected to host.");
        }
    }

    /// <summary>
    /// Called when a client disconnects from the network
    /// </summary>
    private void OnClientDisconnect(ulong clientId)
    {
        Debug.Log($"Client disconnected: {clientId}");

        bool isLocalClientDisconnect = clientId == NetworkManager.Singleton.LocalClientId;

        if (playerSides.ContainsKey(clientId))
        {
            playerSides.Remove(clientId);
            OnPlayerLeft?.Invoke(clientId);
        }

        // If we're the host and a client disconnected
        if (NetworkManager.Singleton.IsHost && !isLocalClientDisconnect)
        {
            UpdateConnectionStatus("Opponent disconnected. Waiting for new opponent...");
        }
        // If we're disconnecting as a client
        else if (!isLocalClientDisconnect)
        {
            LeaveGame();
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

        // Update pieces to only allow movement of own side
        UpdatePieceControl();

        // Update UI with message
        UpdateConnectionStatus("Game started!");

        TransitionToGameUI();

        // Instead of hiding connection panel, just add game UI
        if (gamePanel != null)
        {
            gamePanel.SetActive(true);
            isGameUiVisible = true;
            Debug.Log("GamePanel activated");
        }
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
        Debug.Log($"CanControlSide check: Local player ID {NetworkManager.Singleton.LocalClientId} has side {localPlayerSide}, checking against {side}");

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
    /// Updates piece controls to only allow movement of the player's side
    /// </summary>
    private void UpdatePieceControl()
    {
        if (!NetworkManager.Singleton.IsConnectedClient || !isMultiplayerGameActive)
            return;

        // Get all visual pieces
        VisualPiece[] allPieces = FindObjectsOfType<VisualPiece>(true);
        Side localPlayerSide = GetPlayerSide(NetworkManager.Singleton.LocalClientId);

        foreach (VisualPiece piece in allPieces)
        {
            // Only enable pieces that match the player's side
            if (piece.enabled)
            {
                // Don't override pieces that are already disabled for game logic reasons
                piece.enabled = piece.PieceColor == localPlayerSide;
            }
        }
    }

    private void TransitionToGameUI()
    {
        Debug.Log($"TransitionToGameUI - gamePanel is {(gamePanel == null ? "null" : "not null")}");

        // Make sure game UI is visible
        // If you have a separate game UI panel, activate it here
        if (gamePanel != null)
        {
            gamePanel.SetActive(true);
            isGameUiVisible = true;
            Debug.Log("GamePanel activated");
        }
        else
        {
            Debug.LogWarning("GamePanel reference is null!");
        }

        // Update status message
        UpdateConnectionStatus("Game in progress");

        // Update player control permissions
        UpdatePieceControl();
    }

    private void Update()
    {
        // Make sure both connection panel and game panel are visible when needed
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
        {
            // Connection panel should always be visible
            if (connectionPanel != null && !connectionPanel.activeInHierarchy)
            {
                connectionPanel.SetActive(true);

                // Ensure correct button visibility
                if (isMultiplayerGameActive)
                {
                    // In a game - show disconnect, hide others
                    if (hostButton != null) hostButton.gameObject.SetActive(false);
                    if (clientButton != null) clientButton.gameObject.SetActive(false);
                    if (ipAddressInput != null) ipAddressInput.gameObject.SetActive(false);
                    if (disconnectButton != null) disconnectButton.gameObject.SetActive(true);
                }
            }

            // Game panel should be visible if we're in a game
            if (isMultiplayerGameActive && gamePanel != null && !gamePanel.activeInHierarchy)
            {
                gamePanel.SetActive(true);
            }
        }
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
        }
    }

    /// <summary>
    /// Called when a move is executed in the game
    /// </summary>
    private void OnMoveExecuted()
    {
        // Update piece control after moves to ensure only the correct side can move
        if (isMultiplayerGameActive)
        {
            UpdatePieceControl();
        }
    }
}