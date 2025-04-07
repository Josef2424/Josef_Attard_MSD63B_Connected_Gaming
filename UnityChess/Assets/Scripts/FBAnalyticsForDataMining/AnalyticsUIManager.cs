using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Manages the UI for displaying analytics data and game state management.
/// </summary>
public class AnalyticsUIManager : MonoBehaviourSingleton<AnalyticsUIManager>
{
    [Header("Analytics Panel")] [SerializeField]
    private GameObject analyticsPanel;

    [SerializeField] private Button closeAnalyticsButton;
    [SerializeField] private Button openAnalyticsButton;

    [Header("Analytics Data Display")] [SerializeField]
    private TMP_Text totalGamesText;

    [SerializeField] private TMP_Text topOpeningText;
    [SerializeField] private TMP_Text topDLCText;
    [SerializeField] private TMP_Text gameTimeText;

    [Header("Game State Management")] [SerializeField]
    private GameObject savedGamesPanel;

    [SerializeField] private Button saveGameButton;
    [SerializeField] private Button loadSavedButton; // The Load/Saved button that opens the panel
    [SerializeField] private Button closeSavedGamesButton; // Button to close the saved games panel

    [Header("Saved Game Items")] [SerializeField]
    private Transform savedGamesContainer; // Parent transform for saved game items

    [SerializeField] private GameObject savedGameItemPrefab; // Prefab for individual saved game items

    // List of currently displayed saved game items
    private List<GameObject> savedGameItems = new List<GameObject>();

    private void Start()
    {
        // Set up UI buttons
        if (closeAnalyticsButton != null)
            closeAnalyticsButton.onClick.AddListener(HideAnalyticsPanel);

        if (openAnalyticsButton != null)
            openAnalyticsButton.onClick.AddListener(ShowAnalyticsPanel);

        if (saveGameButton != null)
            saveGameButton.onClick.AddListener(SaveCurrentGame);

        if (loadSavedButton != null)
            loadSavedButton.onClick.AddListener(ShowSavedGamesPanel);

        if (closeSavedGamesButton != null)
            closeSavedGamesButton.onClick.AddListener(HideSavedGamesPanel);

        // Hide panels initially
        if (analyticsPanel != null)
            analyticsPanel.SetActive(false);

        if (savedGamesPanel != null)
            savedGamesPanel.SetActive(false);
    }

    /// <summary>
    /// Shows the analytics panel
    /// </summary>
    public void ShowAnalyticsPanel()
    {
        if (analyticsPanel != null)
        {
            analyticsPanel.SetActive(true);

            // Request FirebaseManager to update analytics
            if (FirebaseManager.Instance != null)
            {
                FirebaseManager.Instance.ShowAnalytics();
            }
        }
    }

    /// <summary>
    /// Hides the analytics panel
    /// </summary>
    public void HideAnalyticsPanel()
    {
        if (analyticsPanel != null)
            analyticsPanel.SetActive(false);
    }

    /// <summary>
    /// Saves the current game state
    /// </summary>
    public void SaveCurrentGame()
    {
        if (FirebaseManager.Instance != null)
        {
            FirebaseManager.Instance.SaveGameState();

            // Show feedback to user
            StartCoroutine(ShowSaveConfirmation());
        }
    }

    /// <summary>
    /// Shows a temporary confirmation message for successful save
    /// </summary>
    private IEnumerator ShowSaveConfirmation()
    {
        // Create confirmation UI
        GameObject confirmObj = new GameObject("SaveConfirmation");
        confirmObj.transform.SetParent(transform);

        RectTransform rect = confirmObj.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(300, 80);

        Image bg = confirmObj.AddComponent<Image>();
        bg.color = new Color(0.2f, 0.2f, 0.2f, 0.8f);

        GameObject textObj = new GameObject("Text");
        textObj.transform.SetParent(confirmObj.transform);

        RectTransform textRect = textObj.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        TMP_Text text = textObj.AddComponent<TextMeshProUGUI>();
        text.text = "Game state saved successfully!";
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.Center;
        text.fontSize = 18;

        // Wait for 2 seconds
        yield return new WaitForSeconds(2f);

        // Destroy the confirmation
        Destroy(confirmObj);
    }

    /// <summary>
    /// Shows saved games panel with list of available saved games
    /// </summary>
    public void ShowSavedGamesPanel()
    {
        if (savedGamesPanel == null)
        {
            Debug.LogError("Saved games panel is not assigned!");
            return;
        }

        // Show the panel
        savedGamesPanel.SetActive(true);

        // Clear existing items
        ClearSavedGameItems();

        // Fetch saved games from Firebase if available
        if (FirebaseManager.Instance != null)
        {
            FirebaseManager.Instance.ListSavedGames(gameIds =>
            {
                // Create UI items for each saved game
                PopulateSavedGamesUI(gameIds);
            });
        }
        else
        {
            Debug.LogWarning("FirebaseManager not available, can't list saved games");
        }
    }

    /// <summary>
    /// Clears all saved game items from the container
    /// </summary>
    private void ClearSavedGameItems()
    {
        // Destroy all existing saved game items
        foreach (GameObject item in savedGameItems)
        {
            Destroy(item);
        }

        savedGameItems.Clear();
    }

    /// <summary>
    /// Creates UI elements for each saved game
    /// </summary>
    private void PopulateSavedGamesUI(List<string> gameIds)
    {
        if (savedGamesContainer == null || savedGameItemPrefab == null)
        {
            Debug.LogError("Saved games container or prefab not assigned!");
            return;
        }

        // Clear existing items first
        ClearSavedGameItems();

        if (gameIds.Count == 0)
        {
            // Create a message for no saved games
            GameObject noGamesObj = new GameObject("NoGamesMessage");
            noGamesObj.transform.SetParent(savedGamesContainer);

            RectTransform rect = noGamesObj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 0);
            rect.anchorMax = new Vector2(1, 1);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            TMP_Text text = noGamesObj.AddComponent<TextMeshProUGUI>();
            text.text = "No saved games found";
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.Center;
            text.fontSize = 18;

            savedGameItems.Add(noGamesObj);
        }
        else
        {
            // Create UI items for each saved game
            for (int i = 0; i < gameIds.Count; i++)
            {
                string gameId = gameIds[i];

                // Instantiate the prefab
                GameObject item = Instantiate(savedGameItemPrefab, savedGamesContainer);
                savedGameItems.Add(item);

                // Set the game ID text
                TMP_Text idText = item.GetComponentInChildren<TMP_Text>();
                if (idText != null)
                {
                    // Truncate long IDs
                    string displayId = gameId;
                    if (displayId.Length > 8)
                    {
                        displayId = displayId.Substring(0, 8) + "...";
                    }

                    idText.text = $"Game {i + 1}: {displayId}";
                }

                // Set up the load button
                Button loadButton = item.GetComponentInChildren<Button>();
                if (loadButton != null)
                {
                    string capturedId = gameId; // Capture for lambda
                    loadButton.onClick.RemoveAllListeners();
                    loadButton.onClick.AddListener(() => LoadGame(capturedId));
                }
            }
        }
    }

    /// <summary>
    /// Loads a specific saved game
    /// </summary>
    private void LoadGame(string gameId)
    {
        if (FirebaseManager.Instance != null)
        {
            Debug.Log($"Loading game: {gameId}");
            FirebaseManager.Instance.RestoreGameState(gameId);

            // Hide the saved games panel
            HideSavedGamesPanel();
        }
    }

    /// <summary>
    /// Hides the saved games panel
    /// </summary>
    public void HideSavedGamesPanel()
    {
        if (savedGamesPanel != null)
            savedGamesPanel.SetActive(false);
    }

    /// <summary>
    /// Updates the analytics display with data
    /// </summary>
    public void UpdateAnalyticsDisplay(Dictionary<string, object> data)
    {
        // Update game count
        if (totalGamesText != null && data.TryGetValue("totalGames", out object gamesValue))
        {
            long totalGames = Convert.ToInt64(gamesValue);
            totalGamesText.text = $"Total Games: {totalGames}";
        }

        // Update opening moves
        if (topOpeningText != null && data.TryGetValue("currentOpeningMove", out object moveValue))
        {
            string openingMove = moveValue.ToString();
            if (string.IsNullOrEmpty(openingMove))
                openingMove = "None";

            topOpeningText.text = $"Current Opening: {openingMove}";
        }

        // Update DLC purchases
        if (topDLCText != null && data.TryGetValue("totalPurchases", out object purchasesValue))
        {
            long purchases = Convert.ToInt64(purchasesValue);
            topDLCText.text = $"Total DLC Purchases: {purchases}";
        }

        // Update game time info
        if (gameTimeText != null && data.TryGetValue("timestamp", out object timeValue))
        {
            string timestamp = timeValue.ToString();
            gameTimeText.text = $"Last Game: {timestamp}";
        }
    }

    /// <summary>
    /// Updates the analytics display with default values
    /// </summary>
    public void UpdateAnalyticsDisplay()
    {
        // Set default values when no data is available
        if (totalGamesText != null)
            totalGamesText.text = "Total Games: 0";

        if (topOpeningText != null)
            topOpeningText.text = "Current Opening: None";

        if (topDLCText != null)
            topDLCText.text = "Total DLC Purchases: 0";

        if (gameTimeText != null)
            gameTimeText.text = "Last Game: Never";
    }
}