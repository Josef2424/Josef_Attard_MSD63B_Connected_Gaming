using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityChess;
using Firebase;
using Firebase.Extensions;
using Firebase.Firestore;
using Firebase.Storage;
using Firebase.Analytics;
using Unity.Netcode;

/// <summary>
/// Manages Firebase Analytics and Storage integration for the chess game.
/// Handles event tracking, game state saving/restoring, and analytics display.
/// </summary>
public class FirebaseManager : MonoBehaviourSingleton<FirebaseManager>
{
    // Firebase components
    private FirebaseApp app;
    private FirebaseFirestore firestoreDb;
    private StorageReference storageReference;
    private bool firebaseInitialized = false;
    private bool isNetworkClient = false;

    // Game data
    private string currentGameId;
    private string currentMatchFEN;
    private int moveCount = 0;
    private string openingMove = "";
    private float gameStartTime;
    private List<string> savedGameIds = new List<string>();

    // Analytics data
    private int totalGames = 0;
    private int matchStarts = 0;
    private int matchEnds = 0;
    private int totalPurchases = 0;
    private float totalGameDuration = 0;

    // Constants for storage paths
    private const string GAME_COLLECTION = "chess_games";
    private const string ANALYTICS_KEY = "chess_analytics";
    private const string SAVED_GAMES_META = "saved_games_list.json";

    // Flag to limit operations
    private bool isOperationInProgress = false;
    private float operationCooldown = 0f;
    private const float MIN_OPERATION_INTERVAL = 1.0f; // Minimum time between operations

    // Task queue to prevent too many simultaneous Firebase operations
    private Queue<Action> operationQueue = new Queue<Action>();

    // Set this to true to disable all Firebase remote operations when in client mode
    [SerializeField] private bool disableClientOperations = true;

    private void Start()
    {
        // Check if we're in a networked game and if we're just a client
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback += OnNetworkClientConnected;

            // If we're already connected, check our role
            if (NetworkManager.Singleton.IsConnectedClient)
            {
                isNetworkClient = NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsHost;
                Debug.Log(
                    $"Network detected on start: IsClient={isNetworkClient}, IsHost={NetworkManager.Singleton.IsHost}");
            }
        }

        // Don't initialize Firebase immediately - wait for a frame to let the system settle
        StartCoroutine(DelayedFirebaseInit());

        // Subscribe to game events
        GameManager.NewGameStartedEvent += OnNewGameStarted;
        GameManager.GameEndedEvent += OnGameEnded;
        GameManager.MoveExecutedEvent += OnMoveExecuted;
    }

    private void OnNetworkClientConnected(ulong clientId)
    {
        // Update our status if we're the one who connected
        if (clientId == NetworkManager.Singleton.LocalClientId)
        {
            isNetworkClient = NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsHost;
            Debug.Log(
                $"Network client connected: IsClient={isNetworkClient}, IsHost={NetworkManager.Singleton.IsHost}");
        }
    }

    private IEnumerator DelayedFirebaseInit()
    {
        // Wait a frame to let the system settle
        yield return null;

        // If we're just a network client and client operations are disabled, skip Firebase initialization
        if (isNetworkClient && disableClientOperations)
        {
            Debug.Log("Skipping Firebase initialization for client - will use data from host");

            // Set these to true so local functionality still works
            firebaseInitialized = true;
            yield break;
        }

        // Normal initialization
        InitializeFirebase();
    }

    private void Update()
    {
        // Process operation queue
        ProcessOperationQueue();

        // Update operation cooldown
        if (operationCooldown > 0)
        {
            operationCooldown -= Time.deltaTime;
        }
    }

    private void ProcessOperationQueue()
    {
        // If we're not initialized, or operation in progress, or on cooldown, skip
        if (!firebaseInitialized || isOperationInProgress || operationCooldown > 0 || operationQueue.Count == 0)
            return;

        // Start the next operation
        isOperationInProgress = true;
        operationCooldown = MIN_OPERATION_INTERVAL;

        Action nextOperation = operationQueue.Dequeue();
        try
        {
            nextOperation.Invoke();
        }
        catch (Exception e)
        {
            Debug.LogError($"Error executing Firebase operation: {e.Message}");
        }
        finally
        {
            isOperationInProgress = false;
        }
    }

    private void OnDestroy()
    {
        // Unsubscribe from game events
        GameManager.NewGameStartedEvent -= OnNewGameStarted;
        GameManager.GameEndedEvent -= OnGameEnded;
        GameManager.MoveExecutedEvent -= OnMoveExecuted;

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnNetworkClientConnected;
        }
    }

    /// <summary>
    /// Initializes Firebase services
    /// </summary>
    private void InitializeFirebase()
    {
        Debug.Log("Initializing Firebase...");

        // Add error handling and platform checks
#if UNITY_IOS
            // iOS specific initialization if needed
#endif

        // Set default value for flags
        firebaseInitialized = false;

        // Initialize Firebase
        FirebaseApp.CheckAndFixDependenciesAsync().ContinueWithOnMainThread(task =>
        {
            if (task.IsFaulted)
            {
                Debug.LogError($"Firebase dependency check failed: {task.Exception}");
                return;
            }

            var dependencyStatus = task.Result;
            if (dependencyStatus == DependencyStatus.Available)
            {
                try
                {
                    // Get the default app
                    app = FirebaseApp.DefaultInstance;

                    // Initialize Analytics first - this is the most reliable service
                    InitializeAnalytics();

                    // Initialize other Firebase services if needed, but only if Analytics worked
                    if (firebaseInitialized)
                    {
                        Debug.Log("Analytics initialized successfully, now initializing other services");
                        StartCoroutine(InitializeFirebaseServicesWithDelay());
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"Firebase initialization exception: {e.Message}\n{e.StackTrace}");
                    firebaseInitialized = false;
                }
            }
            else
            {
                Debug.LogError($"Could not resolve Firebase dependencies: {dependencyStatus}");
                firebaseInitialized = false;
            }
        });
    }

    private void InitializeAnalytics()
    {
        try
        {
            // Just initialize Analytics - don't try to log anything yet
            Firebase.Analytics.FirebaseAnalytics.SetAnalyticsCollectionEnabled(true);

            // Mark as initialized so we can use Analytics
            firebaseInitialized = true;
            Debug.Log("Firebase Analytics initialized successfully");
        }
        catch (Exception e)
        {
            Debug.LogError($"Firebase Analytics initialization failed: {e.Message}");
            firebaseInitialized = false;
        }
    }

    private IEnumerator InitializeFirebaseServicesWithDelay()
    {
        // Wait a bit before initializing Firestore
        yield return new WaitForSeconds(0.5f);

        // Initialize Firestore
        try
        {
            firestoreDb = FirebaseFirestore.DefaultInstance;
            Debug.Log("Firebase Firestore initialized successfully");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"Firestore initialization failed (will continue without it): {e.Message}");
            // Continue execution even if Firestore fails
        }

        // Wait again before initializing Storage
        yield return new WaitForSeconds(0.5f);

        // Initialize Storage
        try
        {
            Firebase.Storage.FirebaseStorage storage = Firebase.Storage.FirebaseStorage.DefaultInstance;
            storageReference = storage.GetReference("chess_data");
            Debug.Log("Firebase Storage initialized successfully");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"Firebase Storage initialization failed (will continue without it): {e.Message}");
            // Continue execution even if Storage fails
        }

        // Log a simple event now that everything is set up
        LogSimpleEvent("firebase_initialized");
    }

    private void LogSimpleEvent(string eventName)
    {
        if (firebaseInitialized)
        {
            try
            {
                Firebase.Analytics.FirebaseAnalytics.LogEvent(eventName);
                Debug.Log($"Analytics event logged: {eventName}");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"Failed to log simple event {eventName}: {e.Message}");
            }
        }
    }

    #region Event Handling

    /// <summary>
    /// Called when a new game starts
    /// </summary>
    private void OnNewGameStarted()
    {
        // Generate a unique ID for this game
        currentGameId = System.Guid.NewGuid().ToString();
        moveCount = 0;
        openingMove = "";
        gameStartTime = Time.time;

        // Track match start in analytics
        matchStarts++;

        // For clients, don't try to log to Firebase
        if (isNetworkClient && disableClientOperations)
        {
            Debug.Log("Client skipping Firebase logging for match start");
            return;
        }

        // Log game start event - only use Analytics which is the most reliable
        try
        {
            if (firebaseInitialized)
            {
                Dictionary<string, object> parameters = new Dictionary<string, object>
                {
                    { "game_id", currentGameId },
                    {
                        "is_multiplayer", NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient
                    },
                    { "timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") }
                };

                LogEvent("match_started", parameters);
                Debug.Log($"New game started with ID: {currentGameId}");

                // Queue Firestore operation
                operationQueue.Enqueue(() => SaveMatchStartToFirestore());
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to log match start: {e.Message}");
        }
    }

    /// <summary>
    /// Called when a move is executed
    /// </summary>
    private void OnMoveExecuted()
    {
        moveCount++;

        // Get the current board state
        currentMatchFEN = GameManager.Instance.SerializeGame();

        // For clients, don't try to log to Firebase
        if (isNetworkClient && disableClientOperations)
            return;

        // Record the opening move (first move by white)
        // Record the opening move (first move by white)
        if (moveCount == 1 && firebaseInitialized)
        {
            try
            {
                // Get the algebraic notation of the move
                if (!GameManager.Instance.HalfMoveTimeline.TryGetCurrent(out HalfMove move))
                {
                    Debug.LogError("Failed to get current move from timeline");
                    return;
                }

                openingMove = move.ToAlgebraicNotation();
                Debug.Log($"Captured opening move: {openingMove}");

                // Log opening move event to Analytics
                Dictionary<string, object> parameters = new Dictionary<string, object>
                {
                    { "game_id", currentGameId },
                    { "opening_move", openingMove }
                };

                LogEvent("opening_move", parameters);
                Debug.Log($"Opening move logged to Analytics: {openingMove}");

                // Queue the Firestore operation - this ensures it's handled properly
                operationQueue.Enqueue(() => SaveOpeningMoveToFirestore(openingMove, currentGameId));
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to log opening move with detailed error: {e.Message}\n{e.StackTrace}");
            }
        }
    }

    /// <summary>
    /// Called when a game ends
    /// </summary>
    private void OnGameEnded()
    {
        float gameDuration = Time.time - gameStartTime;

        // Track match end in analytics
        matchEnds++;

        // For clients, don't try to log to Firebase
        if (isNetworkClient && disableClientOperations)
            return;

        if (firebaseInitialized)
        {
            try
            {
                // Get the game result
                string result = "unknown";
                GameManager.Instance.HalfMoveTimeline.TryGetCurrent(out HalfMove lastMove);

                if (lastMove.CausedCheckmate)
                    result = lastMove.Piece.Owner.ToString() + "_win";
                else if (lastMove.CausedStalemate)
                    result = "draw";

                // Log game end event
                Dictionary<string, object> parameters = new Dictionary<string, object>
                {
                    { "game_id", currentGameId },
                    { "move_count", moveCount },
                    { "duration_seconds", gameDuration },
                    { "result", result }
                };

                LogEvent("match_ended", parameters);

                // Update analytics totals
                totalGames++;
                totalGameDuration += gameDuration;

                // Queue game state save
                operationQueue.Enqueue(() => SaveGameState());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to log game end: {e.Message}");
            }
        }
    }

    #endregion

    #region Analytics

    /// <summary>
    /// Logs an event to Firebase Analytics
    /// </summary>
    public void LogEvent(string eventName, Dictionary<string, object> parameters)
    {
        if (!firebaseInitialized)
        {
            Debug.LogWarning("Firebase not initialized yet, event not logged: " + eventName);
            return;
        }

        // Skip for clients if disabled
        if (isNetworkClient && disableClientOperations)
            return;

        try
        {
            if (parameters != null)
            {
                // Create a parameter array
                Parameter[] firebaseParams = new Parameter[parameters.Count];
                int index = 0;

                foreach (var kvp in parameters)
                {
                    if (kvp.Value is string stringValue)
                        firebaseParams[index] = new Parameter(kvp.Key, stringValue);
                    else if (kvp.Value is int intValue)
                        firebaseParams[index] = new Parameter(kvp.Key, intValue);
                    else if (kvp.Value is long longValue)
                        firebaseParams[index] = new Parameter(kvp.Key, longValue);
                    else if (kvp.Value is double doubleValue)
                        firebaseParams[index] = new Parameter(kvp.Key, doubleValue);
                    else if (kvp.Value is float floatValue)
                        firebaseParams[index] = new Parameter(kvp.Key, floatValue);
                    else if (kvp.Value is bool boolValue)
                        firebaseParams[index] = new Parameter(kvp.Key, boolValue ? 1 : 0);

                    index++;
                }

                FirebaseAnalytics.LogEvent(eventName, firebaseParams);
            }
            else
            {
                FirebaseAnalytics.LogEvent(eventName);
            }

            Debug.Log($"Analytics event logged: {eventName}");
        }
        catch (Exception e)
        {
            Debug.LogError($"Error logging event {eventName}: {e.Message}");
        }
    }

    /// <summary>
    /// Logs a DLC purchase event
    /// </summary>
    public void LogPurchaseEvent(string itemId, string itemName, double price)
    {
        if (!firebaseInitialized)
            return;

        // Skip for clients if disabled
        if (isNetworkClient && disableClientOperations)
            return;

        // Increment the counter - make sure this happens before calling other methods
        totalPurchases++;
        Debug.Log($"Incremented totalPurchases to {totalPurchases}");

        try
        {
            // Log purchase event to Analytics
            Dictionary<string, object> parameters = new Dictionary<string, object>
            {
                { "item_id", itemId },
                { "item_name", itemName },
                { "price", price }
            };

            LogEvent("dlc_purchased", parameters);

            // Queue an operation to update the purchase count in analytics
            operationQueue.Enqueue(() => UpdatePurchaseCountInAnalytics());

            // Also log to Firestore
            if (firestoreDb != null)
            {
                Dictionary<string, object> purchaseData = new Dictionary<string, object>
                {
                    { "itemId", itemId },
                    { "itemName", itemName },
                    { "price", price },
                    { "timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") }
                };

                // Queue the operation
                operationQueue.Enqueue(() => LogPurchaseToFirestore(purchaseData));
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error logging purchase: {e.Message}");
        }
    }

    private void LogPurchaseToFirestore(Dictionary<string, object> purchaseData)
    {
        if (firestoreDb == null)
            return;

        firestoreDb.Collection("purchases").AddAsync(purchaseData);
    }

    /// <summary>
    /// Shows analytics data for the specified key
    /// </summary>
    public void ShowAnalytics()
    {
        if (!firebaseInitialized)
        {
            Debug.LogWarning("Firebase not initialized, using default analytics values");

            // Use default values
            if (AnalyticsUIManager.Instance != null)
            {
                AnalyticsUIManager.Instance.UpdateAnalyticsDisplay();
            }

            return;
        }

        // Skip for clients if disabled
        if (isNetworkClient && disableClientOperations)
        {
            Debug.Log("Client skipping analytics fetch - using default values");
            if (AnalyticsUIManager.Instance != null)
            {
                AnalyticsUIManager.Instance.UpdateAnalyticsDisplay();
            }

            return;
        }

        if (firestoreDb != null)
        {
            Debug.Log("Fetching analytics data from Firestore...");

            try
            {
                // Get the latest analytics document
                firestoreDb.Collection("analytics").Document("stats").GetSnapshotAsync()
                    .ContinueWithOnMainThread(task =>
                    {
                        if (task.IsFaulted)
                        {
                            Debug.LogError("Error fetching analytics: " + task.Exception);

                            // Use default values
                            if (AnalyticsUIManager.Instance != null)
                            {
                                AnalyticsUIManager.Instance.UpdateAnalyticsDisplay();
                            }
                        }
                        else if (task.IsCompleted)
                        {
                            DocumentSnapshot snapshot = task.Result;
                            if (snapshot.Exists)
                            {
                                Debug.Log("Analytics data retrieved:");
                                Dictionary<string, object> data = snapshot.ToDictionary();

                                // Log the entire data received for debugging
                                foreach (var entry in data)
                                {
                                    Debug.Log($"{entry.Key}: {entry.Value}");
                                }

                                // Make sure the field name matches exactly - fill in missing data
                                if (!data.ContainsKey("currentOpeningMove") && !string.IsNullOrEmpty(openingMove))
                                {
                                    Debug.LogWarning(
                                        "Field 'currentOpeningMove' not found in Firestore document, using local value");
                                    data["currentOpeningMove"] = openingMove;
                                }

                                if (!data.ContainsKey("totalGames"))
                                {
                                    data["totalGames"] = totalGames;
                                }

                                if (!data.ContainsKey("totalPurchases"))
                                {
                                    data["totalPurchases"] = totalPurchases;
                                }

                                if (!data.ContainsKey("timestamp"))
                                {
                                    data["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                                }

                                // Make sure we have all the required fields
                                Debug.Log("Sending analytics data to UI:");
                                foreach (var entry in data)
                                {
                                    Debug.Log($"  {entry.Key}: {entry.Value}");
                                }

                                if (AnalyticsUIManager.Instance != null)
                                {
                                    AnalyticsUIManager.Instance.UpdateAnalyticsDisplay(data);
                                }
                            }
                            else
                            {
                                Debug.Log("No analytics data found in Firestore, creating default document");

                                // Create a default document
                                Dictionary<string, object> defaultData = new Dictionary<string, object>
                                {
                                    { "totalGames", totalGames },
                                    { "matchStarts", matchStarts },
                                    { "matchEnds", matchEnds },
                                    { "totalPurchases", totalPurchases },
                                    { "currentOpeningMove", openingMove ?? "None" },
                                    { "totalGameDuration", totalGameDuration },
                                    { "timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") }
                                };

                                // Save the default document
                                firestoreDb.Collection("analytics").Document("stats").SetAsync(defaultData)
                                    .ContinueWithOnMainThread(setTask =>
                                    {
                                        if (setTask.IsCompleted && !setTask.IsFaulted)
                                        {
                                            Debug.Log("Created default analytics document in Firestore");
                                        }
                                    });

                                // Use the default data for UI
                                if (AnalyticsUIManager.Instance != null)
                                {
                                    AnalyticsUIManager.Instance.UpdateAnalyticsDisplay(defaultData);
                                }
                            }
                        }
                    });
            }
            catch (Exception e)
            {
                Debug.LogError($"Error showing analytics: {e.Message}");

                // Use default values
                if (AnalyticsUIManager.Instance != null)
                {
                    AnalyticsUIManager.Instance.UpdateAnalyticsDisplay();
                }
            }
        }
        else
        {
            // Use default values if Firestore is not initialized
            if (AnalyticsUIManager.Instance != null)
            {
                AnalyticsUIManager.Instance.UpdateAnalyticsDisplay();
            }
        }
    }

    private void UpdatePurchaseCountInAnalytics()
    {
        if (!firebaseInitialized || firestoreDb == null)
        {
            Debug.LogWarning("Firebase Firestore not initialized, cannot update purchase count");
            return;
        }

        if (isNetworkClient && disableClientOperations)
        {
            Debug.Log("Client skipping Firebase update for purchase count");
            return;
        }

        try
        {
            // Get the current analytics document first to ensure we have the latest data
            firestoreDb.Collection("analytics").Document("stats").GetSnapshotAsync()
                .ContinueWithOnMainThread(getTask =>
                {
                    if (getTask.IsFaulted)
                    {
                        Debug.LogError($"Failed to get analytics document: {getTask.Exception?.Message}");

                        // Create a new document with the updated purchase count
                        Dictionary<string, object> newData = new Dictionary<string, object>
                        {
                            { "totalPurchases", totalPurchases },
                            { "timestamp", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") }
                        };

                        firestoreDb.Collection("analytics").Document("stats").SetAsync(newData);
                        Debug.Log($"Created new analytics document with purchase count: {totalPurchases}");
                    }
                    else
                    {
                        DocumentSnapshot snapshot = getTask.Result;
                        if (snapshot.Exists)
                        {
                            // Get existing data
                            Dictionary<string, object> data = snapshot.ToDictionary();

                            // Update purchase count
                            data["totalPurchases"] = totalPurchases;
                            data["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

                            // Update the document
                            firestoreDb.Collection("analytics").Document("stats").UpdateAsync(data)
                                .ContinueWithOnMainThread(updateTask =>
                                {
                                    if (updateTask.IsCompleted && !updateTask.IsFaulted)
                                    {
                                        Debug.Log($"Updated analytics document with purchase count: {totalPurchases}");
                                    }
                                    else
                                    {
                                        Debug.LogError(
                                            $"Failed to update analytics document: {updateTask.Exception?.Message}");
                                    }
                                });
                        }
                        else
                        {
                            // Document doesn't exist, create it
                            Dictionary<string, object> newData = new Dictionary<string, object>
                            {
                                { "totalGames", totalGames },
                                { "matchStarts", matchStarts },
                                { "matchEnds", matchEnds },
                                { "totalPurchases", totalPurchases },
                                { "currentOpeningMove", openingMove ?? "None" },
                                { "totalGameDuration", totalGameDuration },
                                { "timestamp", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") }
                            };

                            firestoreDb.Collection("analytics").Document("stats").SetAsync(newData)
                                .ContinueWithOnMainThread(createTask =>
                                {
                                    if (createTask.IsCompleted && !createTask.IsFaulted)
                                    {
                                        Debug.Log(
                                            $"Created new analytics document with purchase count: {totalPurchases}");
                                    }
                                    else
                                    {
                                        Debug.LogError(
                                            $"Failed to create analytics document: {createTask.Exception?.Message}");
                                    }
                                });
                        }
                    }
                });
        }
        catch (Exception e)
        {
            Debug.LogError($"Error updating purchase count: {e.Message}");
        }
    }

    /// <summary>
    /// Updates the analytics database with the latest game count data
    /// </summary>
    private void UpdateGameCountInAnalytics()
    {
        if (!firebaseInitialized || firestoreDb == null)
        {
            Debug.LogWarning("Firebase Firestore not initialized, cannot update game count");
            return;
        }

        if (isNetworkClient && disableClientOperations)
        {
            Debug.Log("Client skipping Firebase update for game count");
            return;
        }

        try
        {
            // Get the current analytics document first to ensure we have the latest data
            firestoreDb.Collection("analytics").Document("stats").GetSnapshotAsync()
                .ContinueWithOnMainThread(getTask =>
                {
                    if (getTask.IsFaulted)
                    {
                        Debug.LogError($"Failed to get analytics document: {getTask.Exception?.Message}");

                        // Create a new document with the updated game count
                        Dictionary<string, object> newData = new Dictionary<string, object>
                        {
                            { "totalGames", totalGames },
                            { "matchStarts", matchStarts },
                            { "matchEnds", matchEnds },
                            { "timestamp", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") }
                        };

                        firestoreDb.Collection("analytics").Document("stats").SetAsync(newData);
                        Debug.Log($"Created new analytics document with game count: {totalGames}");
                    }
                    else
                    {
                        DocumentSnapshot snapshot = getTask.Result;
                        if (snapshot.Exists)
                        {
                            // Get existing data
                            Dictionary<string, object> data = snapshot.ToDictionary();

                            // Update game count and related fields
                            data["totalGames"] = totalGames;
                            data["matchStarts"] = matchStarts;
                            data["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

                            // Update the document
                            firestoreDb.Collection("analytics").Document("stats").UpdateAsync(data)
                                .ContinueWithOnMainThread(updateTask =>
                                {
                                    if (updateTask.IsCompleted && !updateTask.IsFaulted)
                                    {
                                        Debug.Log($"Updated analytics document with game count: {totalGames}");
                                    }
                                    else
                                    {
                                        Debug.LogError(
                                            $"Failed to update analytics document: {updateTask.Exception?.Message}");
                                    }
                                });
                        }
                        else
                        {
                            // Document doesn't exist, create it
                            Dictionary<string, object> newData = new Dictionary<string, object>
                            {
                                { "totalGames", totalGames },
                                { "matchStarts", matchStarts },
                                { "matchEnds", matchEnds },
                                { "totalPurchases", totalPurchases },
                                { "currentOpeningMove", openingMove ?? "None" },
                                { "totalGameDuration", totalGameDuration },
                                { "timestamp", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") }
                            };

                            firestoreDb.Collection("analytics").Document("stats").SetAsync(newData)
                                .ContinueWithOnMainThread(createTask =>
                                {
                                    if (createTask.IsCompleted && !createTask.IsFaulted)
                                    {
                                        Debug.Log($"Created new analytics document with game count: {totalGames}");
                                    }
                                    else
                                    {
                                        Debug.LogError(
                                            $"Failed to create analytics document: {createTask.Exception?.Message}");
                                    }
                                });
                        }
                    }
                });
        }
        catch (Exception e)
        {
            Debug.LogError($"Error updating game count: {e.Message}");
        }
    }

    /// <summary>
    /// Increments the game count and updates analytics
    /// Can be called by ChessNetworkManager when both players connect to a game
    /// </summary>
    public void IncrementGameCount()
    {
        if (!firebaseInitialized)
        {
            Debug.LogWarning("Firebase not initialized, cannot increment game count");
            return;
        }

        // Skip for clients if disabled
        if (isNetworkClient && disableClientOperations)
        {
            Debug.Log("Client skipping Firebase game count increment");
            return;
        }

        // Increment the counter - make sure this happens before calling other methods
        totalGames++;
        Debug.Log($"GAME COUNT EVENT: Incremented totalGames to {totalGames}");

        try
        {
            // Log game count event to Analytics
            Dictionary<string, object> parameters = new Dictionary<string, object>
            {
                { "game_id", currentGameId ?? System.Guid.NewGuid().ToString() },
                { "is_multiplayer", true },
                { "player_count", NetworkManager.Singleton.ConnectedClientsIds.Count },
                { "timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") }
            };

            LogEvent("multiplayer_game_started", parameters);
            Debug.Log($"Logged multiplayer game start event to analytics");

            // Queue an operation to update the game count in analytics
            operationQueue.Enqueue(() => UpdateGameCountInAnalytics());
        }
        catch (Exception e)
        {
            Debug.LogError($"Error logging game count increment: {e.Message}");
        }
    }

    #endregion

    #region Game State Management

    /// <summary>
    /// Saves the current game state to Firebase Storage
    /// </summary>
    public void SaveGameState()
    {
        if (!firebaseInitialized || string.IsNullOrEmpty(currentGameId) || storageReference == null)
        {
            Debug.LogWarning("Firebase not initialized or no game in progress, state not saved");
            return;
        }

        // Skip for clients if disabled
        if (isNetworkClient && disableClientOperations)
        {
            Debug.Log("Client skipping Firebase Storage operations");
            return;
        }

        try
        {
            // Create a reference to the game state file
            StorageReference gameRef = storageReference.Child(GAME_COLLECTION).Child(currentGameId + ".fen");

            // Convert the FEN string to bytes
            byte[] bytes = Encoding.UTF8.GetBytes(currentMatchFEN);

            // Upload the data
            gameRef.PutBytesAsync(bytes).ContinueWithOnMainThread(task =>
            {
                // Mark operation completed
                isOperationInProgress = false;

                if (task.IsFaulted || task.IsCanceled)
                {
                    Debug.LogError("Error saving game state: " + task.Exception);
                }
                else
                {
                    Debug.Log($"Game state saved successfully: {currentGameId}");

                    // Add to saved games list if not already there
                    if (!savedGameIds.Contains(currentGameId))
                    {
                        savedGameIds.Add(currentGameId);
                    }

                    // Log event for successful save
                    Dictionary<string, object> parameters = new Dictionary<string, object>
                    {
                        { "game_id", currentGameId }
                    };

                    LogEvent("game_state_saved", parameters);
                }
            });
        }
        catch (Exception e)
        {
            Debug.LogError($"Error initiating save game state: {e.Message}");
            isOperationInProgress = false;
        }
    }

    /// <summary>
    /// Restores a game state from Firebase Storage
    /// </summary>
    public void RestoreGameState(string gameId)
    {
        if (!firebaseInitialized || storageReference == null)
        {
            Debug.LogWarning("Firebase not initialized, state not restored");
            return;
        }

        // Skip for clients if disabled
        if (isNetworkClient && disableClientOperations)
        {
            Debug.Log("Client skipping Firebase Storage operations for restore");
            return;
        }

        try
        {
            // Create a reference to the game state file
            StorageReference gameRef = storageReference.Child(GAME_COLLECTION).Child(gameId + ".fen");

            // Download the data
            const long maxAllowedSize = 1024 * 1024; // 1MB max size
            gameRef.GetBytesAsync(maxAllowedSize).ContinueWithOnMainThread(task =>
            {
                // Mark operation completed
                isOperationInProgress = false;

                if (task.IsFaulted || task.IsCanceled)
                {
                    Debug.LogError("Error restoring game state: " + task.Exception);
                }
                else
                {
                    // Convert bytes to string
                    string fenString = Encoding.UTF8.GetString(task.Result);

                    // Load the game
                    GameManager.Instance.LoadGame(fenString);

                    Debug.Log($"Game state restored successfully: {gameId}");

                    // Update current game ID to the restored one
                    currentGameId = gameId;

                    // Log event for successful restore
                    Dictionary<string, object> parameters = new Dictionary<string, object>
                    {
                        { "game_id", gameId }
                    };

                    LogEvent("game_state_restored", parameters);
                }
            });
        }
        catch (Exception e)
        {
            Debug.LogError($"Error initiating restore game state: {e.Message}");
            isOperationInProgress = false;
        }
    }

    /// <summary>
    /// Lists all saved game states
    /// </summary>
    public void ListSavedGames(Action<List<string>> callback)
    {
        if (!firebaseInitialized)
        {
            Debug.LogWarning("Firebase not initialized, can't list saved games");
            callback?.Invoke(new List<string>());
            return;
        }

        // Skip for clients if disabled
        if (isNetworkClient && disableClientOperations)
        {
            Debug.Log("Client skipping Firebase Storage operations for list");
            callback?.Invoke(new List<string>());
            return;
        }

        // Return the list of saved game IDs
        callback?.Invoke(savedGameIds);
    }

    #endregion

    /// <summary>
    /// Saves match start info to Firestore
    /// </summary>
    private void SaveMatchStartToFirestore()
    {
        if (!firebaseInitialized || firestoreDb == null)
        {
            Debug.LogWarning("Firebase Firestore not initialized, match start data not saved");
            return;
        }

        try
        {
            // Create a document with the match start data
            Dictionary<string, object> data = new Dictionary<string, object>
            {
                { "gameId", currentGameId },
                { "startTime", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") },
                { "isMultiplayer", NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient },
                { "status", "started" }
            };

            // Save to Firestore
            firestoreDb.Collection("matches").Document(currentGameId).SetAsync(data)
                .ContinueWithOnMainThread(task =>
                {
                    if (task.IsFaulted || task.IsCanceled)
                    {
                        Debug.LogError("Error saving match start to Firestore: " + task.Exception);
                    }
                    else
                    {
                        Debug.Log("Match start saved to Firestore successfully");
                    }
                });

            // Update analytics stats
            Dictionary<string, object> statsData = new Dictionary<string, object>
            {
                { "totalGames", totalGames },
                { "matchStarts", matchStarts },
                { "matchEnds", matchEnds },
                { "totalPurchases", totalPurchases },
                { "currentOpeningMove", openingMove },
                { "totalGameDuration", totalGameDuration },
                { "timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") }
            };

            firestoreDb.Collection("analytics").Document("stats").SetAsync(statsData);
        }
        catch (Exception e)
        {
            Debug.LogError($"Error saving match start to Firestore: {e.Message}");
        }
    }

    /// <summary>
    /// Saves the opening move to both collections to ensure consistency
    /// </summary>
    private void SaveOpeningMoveToFirestore(string moveNotation, string gameId)
    {
        if (firestoreDb == null)
            return;

        Debug.Log($"Attempting to save opening move '{moveNotation}' to Firestore");

        try
        {
            // 1. Save to the openingMoves collection
            Dictionary<string, object> moveData = new Dictionary<string, object>
            {
                { "move", moveNotation },
                { "count", 1 }, // Initial count or increment existing
                { "timestamp", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") }
            };

            // Create a document with the move name or a random ID
            firestoreDb.Collection("openingMoves").Document(moveNotation).SetAsync(moveData)
                .ContinueWithOnMainThread(task =>
                {
                    if (task.IsCompleted && !task.IsFaulted)
                    {
                        Debug.Log($"Successfully saved opening move '{moveNotation}' to openingMoves collection");
                    }
                    else if (task.IsFaulted)
                    {
                        Debug.LogError($"Failed to save to openingMoves: {task.Exception?.Message}");
                    }
                });

            // 2. Update the analytics/stats document with the current opening move
            Dictionary<string, object> statsUpdate = new Dictionary<string, object>
            {
                { "currentOpeningMove", moveNotation },
                { "timestamp", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") }
            };

            // Try to get the document first to ensure it exists
            firestoreDb.Collection("analytics").Document("stats").GetSnapshotAsync()
                .ContinueWithOnMainThread(getTask =>
                {
                    if (getTask.IsFaulted)
                    {
                        Debug.LogError($"Failed to check if stats document exists: {getTask.Exception?.Message}");

                        // Create the full document with default values since we couldn't read it
                        Dictionary<string, object> newStatsDoc = new Dictionary<string, object>(statsUpdate)
                        {
                            { "totalGames", totalGames },
                            { "matchStarts", matchStarts },
                            { "matchEnds", matchEnds },
                            { "totalPurchases", totalPurchases },
                            { "totalGameDuration", totalGameDuration }
                        };

                        // Try to create the document
                        firestoreDb.Collection("analytics").Document("stats").SetAsync(newStatsDoc)
                            .ContinueWithOnMainThread(createTask =>
                            {
                                if (createTask.IsCompleted && !createTask.IsFaulted)
                                {
                                    Debug.Log("Created new analytics/stats document with opening move");
                                }
                                else
                                {
                                    Debug.LogError(
                                        $"Failed to create analytics/stats: {createTask.Exception?.Message}");
                                }
                            });
                    }
                    else
                    {
                        DocumentSnapshot snapshot = getTask.Result;
                        if (snapshot.Exists)
                        {
                            // Document exists, update it
                            firestoreDb.Collection("analytics").Document("stats").UpdateAsync(statsUpdate)
                                .ContinueWithOnMainThread(updateTask =>
                                {
                                    if (updateTask.IsCompleted && !updateTask.IsFaulted)
                                    {
                                        Debug.Log($"Updated analytics/stats with opening move: {moveNotation}");
                                    }
                                    else
                                    {
                                        Debug.LogError(
                                            $"Failed to update analytics/stats: {updateTask.Exception?.Message}");
                                    }
                                });
                        }
                        else
                        {
                            // Document doesn't exist, create it
                            Dictionary<string, object> newStatsDoc = new Dictionary<string, object>(statsUpdate)
                            {
                                { "totalGames", totalGames },
                                { "matchStarts", matchStarts },
                                { "matchEnds", matchEnds },
                                { "totalPurchases", totalPurchases },
                                { "totalGameDuration", totalGameDuration }
                            };

                            firestoreDb.Collection("analytics").Document("stats").SetAsync(newStatsDoc)
                                .ContinueWithOnMainThread(createTask =>
                                {
                                    if (createTask.IsCompleted && !createTask.IsFaulted)
                                    {
                                        Debug.Log("Created new analytics/stats document with opening move");
                                    }
                                    else
                                    {
                                        Debug.LogError(
                                            $"Failed to create analytics/stats: {createTask.Exception?.Message}");
                                    }
                                });
                        }
                    }
                });
        }
        catch (Exception e)
        {
            Debug.LogError($"Error in SaveOpeningMoveToFirestore: {e.Message}\n{e.StackTrace}");
        }
    }
}