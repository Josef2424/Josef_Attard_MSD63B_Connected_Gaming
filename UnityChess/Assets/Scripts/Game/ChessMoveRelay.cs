using System;
using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityChess;
using UnityEngine.UI;

/// <summary>
/// Handles networked chess moves without requiring individual NetworkObjects for all chess pieces.
/// This simplifies the networking model by only synchronizing move data rather than object transforms.
/// </summary>
public class ChessMoveRelay : NetworkBehaviour
{
    public static ChessMoveRelay Instance { get; private set; }

    [SerializeField] public GameObject chessBoard;
    [SerializeField] private Text resultText;

    // Flag to prevent recursive move handling
    private bool isHandlingNetworkMove = false;

    private void Awake()
    {
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

    // Start is called when this component is enabled
    private void Start()
    {
        // Listen for local piece moves when this is active
        VisualPiece.VisualPieceMoved += InterceptPieceMove;

        // Subscribe to game events for handling game end conditions
        GameManager.GameEndedEvent += OnGameEnded;
        GameManager.NewGameStartedEvent += OnNewGameStarted;

        // Find the result text if not assigned
        if (resultText == null)
        {
            resultText = GameObject.Find("ResultText")?.GetComponent<Text>();
            if (resultText == null)
            {
                Debug.LogWarning("ResultText not found. Game end messages will not be displayed.");
            }
            else
            {
                // Initially hide the result text
                resultText.gameObject.SetActive(false);
            }
        }
    }

    private void OnDestroy()
    {
        // Clean up event subscription
        VisualPiece.VisualPieceMoved -= InterceptPieceMove;
        GameManager.GameEndedEvent -= OnGameEnded;
        GameManager.NewGameStartedEvent -= OnNewGameStarted;
    }

    /// <summary>
    /// Handles game end event by updating the result text and notifying all clients
    /// </summary>
    private void OnGameEnded()
    {
        if (resultText == null || GameManager.Instance == null) return;

        // Get the game result
        if (GameManager.Instance.HalfMoveTimeline.TryGetCurrent(out HalfMove latestHalfMove))
        {
            // Determine what to display
            if (latestHalfMove.CausedCheckmate)
            {
                string winner = latestHalfMove.Piece.Owner.ToString();
                resultText.text = $"{winner} Wins by checkmate!";

                // Send to all clients if we're the server
                if (IsServer)
                {
                    NotifyGameEndClientRpc(resultText.text);
                    Debug.Log($"Server detected checkmate: {winner} wins. Notifying clients.");
                }
            }
            else if (latestHalfMove.CausedStalemate)
            {
                resultText.text = "Draw by Stalemate.";

                // Send to all clients if we're the server
                if (IsServer)
                {
                    NotifyGameEndClientRpc(resultText.text);
                    Debug.Log("Server detected stalemate. Notifying clients.");
                }
            }

            // Show the result text
            resultText.gameObject.SetActive(true);
        }
    }

    /// <summary>
    /// Notifies all clients about the game end result
    /// </summary>
    [ClientRpc]
    public void NotifyGameEndClientRpc(string resultMessage)
    {
        Debug.Log($"[CLIENT {NetworkManager.Singleton.LocalClientId}] NotifyGameEndClientRpc received: {resultMessage}");
        if (IsServer) return;

        Debug.Log($"Client received game end notification: {resultMessage}");

        // Update the result text
        if (resultText != null)
        {
            resultText.text = resultMessage;
            resultText.gameObject.SetActive(true);
        }

        // Disable all pieces
        if (BoardManager.Instance != null)
        {
            BoardManager.Instance.SetActiveAllPieces(false);
        }
    }

    /// <summary>
    /// Handles new game started event by hiding the result text
    /// </summary>
    private void OnNewGameStarted()
    {
        // Hide result text when a new game starts
        if (resultText != null)
        {
            resultText.text = "";
            resultText.gameObject.SetActive(false);
        }
    }

    /// <summary>
    /// Intercepts piece moves to synchronize them over the network
    /// </summary>
    private void InterceptPieceMove(Square movedPieceInitialSquare, Transform movedPieceTransform,
        Transform closestBoardSquareTransform, Piece promotionPiece = null)
    {
        // Skip if we're already handling a network move
        if (isHandlingNetworkMove) return;

        // Only process if we're in a networked game
        if (!NetworkManager.Singleton.IsConnectedClient || !ChessNetworkManager.Instance.IsInMultiplayerGame())
            return;

        // Additional checks to prevent out-of-sync conditions
        if (!movedPieceTransform || !closestBoardSquareTransform)
        {
            Debug.LogWarning("Piece move had null transform references - ignoring");
            return;
        }

        // Get the moving piece's side
        VisualPiece visualPiece = movedPieceTransform.GetComponent<VisualPiece>();
        if (visualPiece == null) return;

        Square destinationSquare = new Square(closestBoardSquareTransform.name);

        // Check if local player is allowed to move this piece
        if (!ChessNetworkManager.Instance.CanControlSide(visualPiece.PieceColor))
        {
            Debug.Log($"Cannot move opponent's piece from {movedPieceInitialSquare} to {destinationSquare}");
            return;
        }

        Debug.Log($"Intercepted move from {movedPieceInitialSquare} to {destinationSquare}");

        // Don't relay if it's an invalid local move - the GameManager will reset the piece position
        // We let the normal VisualPiece.VisualPieceMoved event flow continue

        // After a short delay to allow local game logic to process, relay the move if it was valid
        StartCoroutine(DelayedMoveRelay(movedPieceInitialSquare, destinationSquare, promotionPiece));
    }

    private IEnumerator DelayedMoveRelay(Square startSquare, Square endSquare, Piece promotionPiece)
    {
        // Give local game logic time to process and validate the move
        yield return new WaitForSeconds(0.2f);

        // Check if the move was successful by seeing if the piece is at the destination
        GameObject pieceAtDest = BoardManager.Instance.GetPieceGOAtPosition(endSquare);
        if (pieceAtDest == null)
        {
            // Move was invalid, no need to relay
            Debug.Log("Move was invalid (no piece at destination), not relaying");
            yield break;
        }

        // If move was successful, relay it
        if (promotionPiece == null)
        {
            RelayValidatedMoveServerRpc(
                startSquare.File,
                startSquare.Rank,
                endSquare.File,
                endSquare.Rank
            );
        }
        else
        {
            int promotionType = GetPromotionPieceType(promotionPiece);
            RelayValidatedPromotionServerRpc(
                startSquare.File,
                startSquare.Rank,
                endSquare.File,
                endSquare.Rank,
                promotionType
            );
        }
    }

    /// <summary>
    /// Determines the promotion piece type as an integer
    /// </summary>
    private int GetPromotionPieceType(Piece promotionPiece)
    {
        if (promotionPiece is Queen) return (int)ElectedPiece.Queen;
        if (promotionPiece is Rook) return (int)ElectedPiece.Rook;
        if (promotionPiece is Bishop) return (int)ElectedPiece.Bishop;
        if (promotionPiece is Knight) return (int)ElectedPiece.Knight;
        return (int)ElectedPiece.Queen; // Default
    }

    /// <summary>
    /// Sends a move from client to server AFTER local validation
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RelayValidatedMoveServerRpc(int startFile, int startRank, int endFile, int endRank)
    {
        // Server receives a move that was already validated locally
        Debug.Log($"Server received validated move: {startFile},{startRank} to {endFile},{endRank}");

        // Broadcast to all clients except the one that sent it
        RelayValidatedMoveClientRpc(startFile, startRank, endFile, endRank, NetworkManager.Singleton.LocalClientId);
    }

    /// <summary>
    /// Broadcasts a validated move from server to all clients
    /// </summary>
    [ClientRpc]
    public void RelayValidatedMoveClientRpc(int startFile, int startRank, int endFile, int endRank,
        ulong sourceClientId)
    {
        // Don't process if we're the originator
        if (sourceClientId == NetworkManager.Singleton.LocalClientId && !IsHost)
            return;

        Debug.Log($"Client received validated move: {startFile},{startRank} to {endFile},{endRank}");

        // Create squares from the coordinates
        Square startSquare = new Square(startFile, startRank);
        Square endSquare = new Square(endFile, endRank);

        // Execute the move in local game logic
        ExecuteRemoteMove(startSquare, endSquare);
    }

    /// <summary>
    /// Sends a promotion move from client to server AFTER local validation
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RelayValidatedPromotionServerRpc(int startFile, int startRank, int endFile, int endRank,
        int promotionChoice)
    {
        // Server receives a validated promotion
        Debug.Log(
            $"Server received validated promotion: {startFile},{startRank} to {endFile},{endRank}, choice: {promotionChoice}");

        // Broadcast to all clients except the one that sent it
        RelayValidatedPromotionClientRpc(startFile, startRank, endFile, endRank, promotionChoice,
            NetworkManager.LocalClientId);
    }

    /// <summary>
    /// Broadcasts a validated promotion move from server to all clients
    /// </summary>
    [ClientRpc]
    public void RelayValidatedPromotionClientRpc(int startFile, int startRank, int endFile, int endRank,
        int promotionChoice, ulong sourceClientId)
    {
        // Don't process if we're the originator
        if (sourceClientId == NetworkManager.Singleton.LocalClientId && !IsHost)
            return;

        Debug.Log(
            $"Client received validated promotion: {startFile},{startRank} to {endFile},{endRank}, choice: {promotionChoice}");

        // Create squares from the coordinates
        Square startSquare = new Square(startFile, startRank);
        Square endSquare = new Square(endFile, endRank);

        // Execute the promotion move in local game state
        ExecuteRemotePromotion(startSquare, endSquare, (ElectedPiece)promotionChoice);
    }

    /// <summary>
    /// Executes a move received from the network by commanding the game logic directly
    /// </summary>
    private void ExecuteRemoteMove(Square startSquare, Square endSquare)
    {
        try
        {
            // Set flag to prevent recursive handling
            isHandlingNetworkMove = true;
            Debug.Log($"Executing remote move: {startSquare} > {endSquare}");

            // Use the standard game flow to execute this move remotely
            GameObject pieceGO = BoardManager.Instance.GetPieceGOAtPosition(startSquare);
            if (pieceGO == null)
            {
                Debug.Log($"Piece not found at {startSquare} - requesting game state resync");

                // Instead of showing an error, request a full game state sync to fix the desync
                if (ChessNetworkManager.Instance != null)
                {
                    ChessNetworkManager.Instance.RequestGameStateSyncServerRpc();
                    return;
                }
            }

            // Get the destination square
            GameObject squareGO = BoardManager.Instance.GetSquareGOByPosition(endSquare);
            if (squareGO == null)
            {
                Debug.LogError($"Failed to find square at {endSquare} for remote move");
                return;
            }

            // Temporarily unsubscribe from the visual piece moved event to avoid loops
            VisualPiece.VisualPieceMoved -= InterceptPieceMove;

            // Trigger the move through the standard event system
            VisualPiece.VisualPieceMoved?.Invoke(startSquare, pieceGO.transform, squareGO.transform);

            // Re-subscribe to the event
            VisualPiece.VisualPieceMoved += InterceptPieceMove;

            Debug.Log($"Successfully executed remote move: {startSquare} to {endSquare}");

            // After successfully executing a remote move, update piece control
            if (ChessNetworkManager.Instance != null)
            {
                Debug.Log("Remote move executed, updating piece control");
                ChessNetworkManager.Instance.UpdatePieceControl();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error executing remote move: {e.Message}\n{e.StackTrace}");

            // If an error occurs, request a full game state sync to restore consistency
            if (ChessNetworkManager.Instance != null)
            {
                Debug.Log("Error in remote move execution - requesting game state resync");
                ChessNetworkManager.Instance.RequestGameStateSyncServerRpc();
            }
        }
        finally
        {
            // Reset flag
            isHandlingNetworkMove = false;
        }
    }

    /// <summary>
    /// Executes a promotion move received from the network
    /// </summary>
    private void ExecuteRemotePromotion(Square startSquare, Square endSquare, ElectedPiece promotionChoice)
    {
        try
        {
            // Set flag to prevent recursive handling
            isHandlingNetworkMove = true;

            // Get the piece GameObject
            GameObject pieceGO = BoardManager.Instance.GetPieceGOAtPosition(startSquare);
            if (pieceGO == null)
            {
                Debug.LogError($"Failed to find piece at {startSquare} for remote promotion");
                return;
            }

            // Get the destination square
            GameObject squareGO = BoardManager.Instance.GetSquareGOByPosition(endSquare);
            if (squareGO == null)
            {
                Debug.LogError($"Failed to find square at {endSquare} for remote promotion");
                return;
            }

            // Get the piece's side
            VisualPiece visualPiece = pieceGO.GetComponent<VisualPiece>();
            if (visualPiece == null)
            {
                Debug.LogError("Failed to get VisualPiece component for promotion");
                return;
            }

            // Create the promotion piece
            Piece promotionPiece = PromotionUtil.GeneratePromotionPiece(promotionChoice, visualPiece.PieceColor);

            // Temporarily unsubscribe from the visual piece moved event to avoid loops
            VisualPiece.VisualPieceMoved -= InterceptPieceMove;

            // Execute the promotion through the same event system
            VisualPiece.VisualPieceMoved?.Invoke(startSquare, pieceGO.transform, squareGO.transform, promotionPiece);

            // Re-subscribe to the event
            VisualPiece.VisualPieceMoved += InterceptPieceMove;

            Debug.Log($"Successfully executed remote promotion: {startSquare} to {endSquare}");
        }
        catch (Exception e)
        {
            Debug.LogError($"Error executing remote promotion: {e.Message}\n{e.StackTrace}");
        }
        finally
        {
            // Reset flag
            isHandlingNetworkMove = false;
        }
    }

    /// <summary>
    /// Server RPC called when a client resigns
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void PlayerResignServerRpc(int resigningSideValue)
    {
        if (!IsServer) return;

        Side resigningSide = (Side)resigningSideValue;
        Side winningSide = resigningSide.Complement();

        Debug.Log($"Server received resignation from {resigningSide}");

        // Notify all clients of the resignation
        string message = $"{winningSide} Wins by Resignation!";
        NotifyGameEndClientRpc(message);

        if (BoardManager.Instance != null)
        {
            BoardManager.Instance.SetActiveAllPieces(false);
        }
    }

    /// <summary>
    /// Exposed method to notify game end with a custom message
    /// </summary>
    public void NotifyGameEnd(string message)
    {
        if (IsServer)
        {
            NotifyGameEndClientRpc(message);
        }
    }
}