using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityChess;
using UnityEngine;
using Unity.Netcode;

public class GameManager : NetworkMonoBehaviourSingleton<GameManager>
{
	// Events signalling various game state changes.
	public static event Action NewGameStartedEvent;
	public static event Action GameEndedEvent;
	public static event Action GameResetToHalfMoveEvent;
	public static event Action MoveExecutedEvent;

	// Use a local variable for the current turn instead of a NetworkVariable.
	private Side currentTurn = Side.White;
	public Side CurrentTurn => currentTurn;

	public Board CurrentBoard
	{
		get
		{
			game.BoardTimeline.TryGetCurrent(out Board currentBoard);
			return currentBoard;
		}
	}

	public Side SideToMove
	{
		get
		{
			game.ConditionsTimeline.TryGetCurrent(out GameConditions currentConditions);
			return currentConditions.SideToMove;
		}
	}

	public Side StartingSide => game.ConditionsTimeline[0].SideToMove;
	public Timeline<HalfMove> HalfMoveTimeline => game.HalfMoveTimeline;
	public int LatestHalfMoveIndex => game.HalfMoveTimeline.HeadIndex;
	public int FullMoveNumber => StartingSide switch
	{
		Side.White => LatestHalfMoveIndex / 2 + 1,
		Side.Black => (LatestHalfMoveIndex + 1) / 2 + 1,
		_ => -1
	};

	private bool isWhiteAI;
	private bool isBlackAI;
	private readonly List<(Square, Piece)> currentPiecesBacking = new List<(Square, Piece)>();

	public List<(Square, Piece)> CurrentPieces
	{
		get
		{
			currentPiecesBacking.Clear();
			for (int file = 1; file <= 8; file++)
			{
				for (int rank = 1; rank <= 8; rank++)
				{
					Piece piece = CurrentBoard[file, rank];
					if (piece != null) currentPiecesBacking.Add((new Square(file, rank), piece));
				}
			}
			return currentPiecesBacking;
		}
	}

	[SerializeField] private UnityChessDebug unityChessDebug;
	private Game game;
	private FENSerializer fenSerializer;
	private PGNSerializer pgnSerializer;
	private CancellationTokenSource promotionUITaskCancellationTokenSource;
	private ElectedPiece userPromotionChoice = ElectedPiece.None;
	private Dictionary<GameSerializationType, IGameSerializer> serializersByType;
	private GameSerializationType selectedSerializationType = GameSerializationType.FEN;

	public void Start()
	{
		VisualPiece.VisualPieceMoved += OnPieceMoved;
		// Initialize the serializers.
		serializersByType = new Dictionary<GameSerializationType, IGameSerializer>
		{
			[GameSerializationType.FEN] = new FENSerializer(),
			[GameSerializationType.PGN] = new PGNSerializer()
		};

#if DEBUG_VIEW
        unityChessDebug.gameObject.SetActive(true);
        unityChessDebug.enabled = true;
#endif
	}

	public override void OnNetworkSpawn()
	{
		base.OnNetworkSpawn();
		if (IsServer)
		{
			currentTurn = Side.White;
			Debug.Log("Server sets currentTurn to White.");
		}
	}

	public async void StartNewGame()
	{
		game = new Game();
		if (IsServer && IsSpawned)
		{
			currentTurn = Side.White;
		}
		NewGameStartedEvent?.Invoke();
	}

	// This method now uses RPCs exclusively to update the turn.
	public void ChangeTurn()
	{
		if (!NetworkManager.Singleton.IsServer || !IsSpawned) return;
		currentTurn = currentTurn.Complement();
		Debug.Log("Server changed turn to: " + currentTurn);
		UpdateTurnClientRpc(currentTurn);
	}

	[ClientRpc]
	private void UpdateTurnClientRpc(Side newTurn)
	{
		currentTurn = newTurn;
		Debug.Log("Client updated turn to: " + currentTurn);
		// Update UI or any turn indicators as needed.
	}

	public string SerializeGame()
	{
		return serializersByType.TryGetValue(selectedSerializationType, out IGameSerializer serializer)
			? serializer?.Serialize(game)
			: null;
	}

	public void LoadGame(string serializedGame)
	{
		game = serializersByType[selectedSerializationType].Deserialize(serializedGame);
		NewGameStartedEvent?.Invoke();
	}

	public void ResetGameToHalfMoveIndex(int halfMoveIndex)
	{
		if (!game.ResetGameToHalfMoveIndex(halfMoveIndex)) return;
		UIManager.Instance.SetActivePromotionUI(false);
		promotionUITaskCancellationTokenSource?.Cancel();
		GameResetToHalfMoveEvent?.Invoke();
	}

	private bool TryExecuteMove(Movement move)
	{
		if (!game.TryExecuteMove(move))
		{
			return false;
		}
		HalfMoveTimeline.TryGetCurrent(out HalfMove latestHalfMove);
		if (latestHalfMove.CausedCheckmate || latestHalfMove.CausedStalemate)
		{
			BoardManager.Instance.SetActiveAllPieces(false);
			GameEndedEvent?.Invoke();
		}
		else
		{
			BoardManager.Instance.EnsureOnlyPiecesOfSideAreEnabled(SideToMove);
		}
		MoveExecutedEvent?.Invoke();
		return true;
	}

	private async Task<bool> TryHandleSpecialMoveBehaviourAsync(SpecialMove specialMove)
	{
		switch (specialMove)
		{
			case CastlingMove castlingMove:
				BoardManager.Instance.CastleRook(castlingMove.RookSquare, castlingMove.GetRookEndSquare());
				return true;
			case EnPassantMove enPassantMove:
				BoardManager.Instance.TryDestroyVisualPiece(enPassantMove.CapturedPawnSquare);
				return true;
			case PromotionMove { PromotionPiece: null } promotionMove:
				UIManager.Instance.SetActivePromotionUI(true);
				BoardManager.Instance.SetActiveAllPieces(false);
				promotionUITaskCancellationTokenSource?.Cancel();
				promotionUITaskCancellationTokenSource = new CancellationTokenSource();
				ElectedPiece choice = await Task.Run(GetUserPromotionPieceChoice, promotionUITaskCancellationTokenSource.Token);
				UIManager.Instance.SetActivePromotionUI(false);
				BoardManager.Instance.SetActiveAllPieces(true);
				if (promotionUITaskCancellationTokenSource == null || promotionUITaskCancellationTokenSource.Token.IsCancellationRequested)
				{
					return false;
				}
				promotionMove.SetPromotionPiece(PromotionUtil.GeneratePromotionPiece(choice, SideToMove));
				BoardManager.Instance.TryDestroyVisualPiece(promotionMove.Start);
				BoardManager.Instance.TryDestroyVisualPiece(promotionMove.End);
				BoardManager.Instance.CreateAndPlacePieceGO(promotionMove.PromotionPiece, promotionMove.End);
				promotionUITaskCancellationTokenSource = null;
				return true;
			case PromotionMove promotionMove:
				BoardManager.Instance.TryDestroyVisualPiece(promotionMove.Start);
				BoardManager.Instance.TryDestroyVisualPiece(promotionMove.End);
				BoardManager.Instance.CreateAndPlacePieceGO(promotionMove.PromotionPiece, promotionMove.End);
				return true;
			default:
				return false;
		}
	}

	public void HandleClientReconnect(ulong clientId)
	{
		if (!IsServer) return;

		Debug.Log($"Client {clientId} is reconnecting. Reassigning pieces...");

		// Reassign all black pieces to the reconnected client
		VisualPiece[] allPieces = FindObjectsOfType<VisualPiece>();
		int reassignedCount = 0;

		foreach (var piece in allPieces)
		{
			if (piece.PieceColor == Side.Black)
			{
				var netObj = piece.GetComponentInParent<NetworkObject>();
				if (netObj != null && netObj.IsSpawned)
				{
					netObj.ChangeOwnership(clientId);
					reassignedCount++;
				}
			}
		}

		Debug.Log($"Reassigned {reassignedCount} black pieces to client {clientId}");
	}

	private ElectedPiece GetUserPromotionPieceChoice()
	{
		while (userPromotionChoice == ElectedPiece.None) { }
		ElectedPiece result = userPromotionChoice;
		userPromotionChoice = ElectedPiece.None;
		return result;
	}

	public void ElectPiece(ElectedPiece choice)
	{
		userPromotionChoice = choice;
	}

	private async void OnPieceMoved(Square movedPieceInitialSquare, Transform movedPieceTransform, Transform closestBoardSquareTransform, Piece promotionPiece = null)
	{
		Square endSquare = new Square(closestBoardSquareTransform.name);
		Piece pieceToMove = CurrentBoard[movedPieceInitialSquare];
		if (pieceToMove == null || pieceToMove.Owner != CurrentTurn)
		{
			Debug.Log("It's not your turn or invalid piece to move.");
			return;
		}
		if (!game.TryGetLegalMove(movedPieceInitialSquare, endSquare, out Movement move))
		{
			movedPieceTransform.position = movedPieceTransform.parent.position;
#if DEBUG_VIEW
            Piece movedPiece = CurrentBoard[movedPieceInitialSquare];
            game.TryGetLegalMovesForPiece(movedPiece, out ICollection<Movement> legalMoves);
            UnityChessDebug.ShowLegalMovesInLog(legalMoves);
#endif
			return;
		}
		if (move is PromotionMove promotionMove)
		{
			promotionMove.SetPromotionPiece(promotionPiece);
		}
		if ((move is not SpecialMove specialMove || await TryHandleSpecialMoveBehaviourAsync(specialMove))
			&& TryExecuteMove(move))
		{
			if (move is not SpecialMove)
			{
				BoardManager.Instance.TryDestroyVisualPiece(move.End);
			}
			if (move is PromotionMove)
			{
				movedPieceTransform = BoardManager.Instance.GetPieceGOAtPosition(move.End).transform;
			}
			movedPieceTransform.parent = closestBoardSquareTransform;
			movedPieceTransform.position = closestBoardSquareTransform.position;
			ChangeTurn();
		}
	}

	public bool HasLegalMoves(Piece piece)
	{
		return game.TryGetLegalMovesForPiece(piece, out _);
	}

	// Add this to GameManager.cs
	public void SynchronizeGameStateToClient(ulong clientId)
	{
		if (!IsServer) return;

		Debug.Log($"Synchronizing game state to client {clientId}");

		// Clear any existing pieces from the client view
		ClearBoardClientRpc();

		// Recreate all pieces based on current state
		foreach ((Square square, Piece piece) in CurrentPieces)
		{
			// Send a ClientRPC to create this piece on all clients
			string pieceName = $"{piece.Owner} {piece.GetType().Name}";
			CreatePieceOnClientRpc(pieceName, square.File, square.Rank);
		}

		// Update turn information
		SyncTurnClientRpc(currentTurn);
	}

	[ClientRpc]
	private void ClearBoardClientRpc()
	{
		// Find and destroy all visual pieces
		VisualPiece[] pieces = FindObjectsOfType<VisualPiece>();
		foreach (var piece in pieces)
		{
			if (piece != null)
			{
				Destroy(piece.gameObject);
			}
		}
	}

	[ClientRpc]
	private void CreatePieceOnClientRpc(string pieceName, int file, int rank)
	{
		if (NetworkManager.Singleton.IsServer) return; // Server already has the pieces

		// Create a square object
		Square square = new Square(file, rank);

		// Client local version of piece creation
		GameObject pieceGO = Instantiate(
			Resources.Load("PieceSets/Marble/" + pieceName) as GameObject,
			BoardManager.Instance.GetSquareGOByPosition(square).transform
		);

		// Don't need NetworkObject since this is a client-only visual representation
	}

	[ClientRpc]
	private void SyncTurnClientRpc(Side turn)
	{
		currentTurn = turn;
	}
}
