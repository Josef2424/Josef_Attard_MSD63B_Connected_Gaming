using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Netcode;
using UnityChess;

/// <summary>
/// Integrates the existing ProfileManager DLC store with Firebase Analytics
/// This script should be attached to the same GameObject as ProfileManager
/// </summary>
public class DLCIntegration : MonoBehaviour
{
    // Reference to the existing ProfileManager
    private ProfileManager profileManager;

    // Keep track of currently applied skin - this value should be set by ProfileManager
    private string currentlyAppliedSkin = "default";

    private void Start()
    {
        // Try to get the ProfileManager component
        profileManager = GetComponent<ProfileManager>();

        if (profileManager == null)
        {
            Debug.LogWarning("DLCIntegration: ProfileManager not found, will search for it");
            profileManager = FindObjectOfType<ProfileManager>();
        }

        // We don't need to set up our own purchase buttons as ProfileManager handles that
        // We'll only add the analytics logging functionality by hooking into ProfileManager
        if (profileManager != null)
        {
            Debug.Log("DLCIntegration successfully linked to ProfileManager");
        }
    }

    /// <summary>
    /// Called by ProfileManager when a profile pic is purchased
    /// </summary>
    public void OnProfilePicPurchased(string itemId, string itemName, double price)
    {
        Debug.Log($"DLCIntegration: Received purchase event for {itemName} (ID: {itemId}) at {price} credits");

        // Skip Firebase logging if we're in client mode - only the host should log purchases
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsHost)
        {
            Debug.Log("Client skipping Firebase logging for purchase, but showing notification");
            ShowPurchaseNotification(itemName, price);
            return;
        }

        // Ensure we're only tracking actual purchases, not applications of already-owned skins
        if (price > 0 || itemId == "default")
        {
            Debug.Log($"PURCHASE EVENT: Logging purchase of {itemName} to Firebase Analytics");
            LogPurchaseEvent(itemId, itemName, price);
        }
        else
        {
            Debug.Log($"Not logging free application of {itemName} as a purchase");
            // Still show notification for feedback
            ShowPurchaseNotification(itemName, 0);
        }
    }

    /// <summary>
    /// Called by ProfileManager when skin changes
    /// </summary>
    public void OnSkinChanged(string skinId)
    {
        // Store the current skin
        currentlyAppliedSkin = skinId;
        Debug.Log($"DLCIntegration: Skin changed to: {skinId}");

        // Get the local player's side
        Side localSide = Side.None;
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient &&
            ChessNetworkManager.Instance != null)
        {
            localSide = ChessNetworkManager.Instance.GetPlayerSide(NetworkManager.Singleton.LocalClientId);
            Debug.Log($"Local player is playing as {localSide}, applied skin: {skinId}");
        }
    }

    /// <summary>
    /// Logs a purchase event to Firebase and ensures ownership persistence
    /// </summary>
    private void LogPurchaseEvent(string itemId, string itemName, double price)
    {
        try
        {
            // Log to Firebase if available
            if (FirebaseManager.Instance != null)
            {
                Debug.Log($"Sending purchase event to FirebaseManager: {itemName} (ID: {itemId}) for {price} credits");
                FirebaseManager.Instance.LogPurchaseEvent(itemId, itemName, price);
                Debug.Log($"Logged DLC purchase: {itemName} (ID: {itemId}) for {price} credits");

                // Show a notification
                ShowPurchaseNotification(itemName, price);
            }
            else
            {
                Debug.LogWarning("FirebaseManager not available, but purchase was successful");
                ShowPurchaseNotification(itemName, price);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error logging purchase: {e.Message}");
            // Still show notification to user as the purchase still succeeded in-game
            ShowPurchaseNotification(itemName, price);
        }
    }

    /// <summary>
    /// Shows a temporary notification for a purchase
    /// </summary>
    private void ShowPurchaseNotification(string itemName, double price)
    {
        // Create a notification
        GameObject notificationObj = new GameObject("PurchaseNotification");
        notificationObj.transform.SetParent(transform);

        // Set up the RectTransform
        RectTransform rect = notificationObj.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(300, 100);

        // Add background image
        Image bg = notificationObj.AddComponent<Image>();
        bg.color = new Color(0.2f, 0.2f, 0.2f, 0.8f);

        // Add text
        GameObject textObj = new GameObject("Text");
        textObj.transform.SetParent(notificationObj.transform);

        RectTransform textRect = textObj.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        TMP_Text text = textObj.AddComponent<TextMeshProUGUI>();
        text.text = $"Purchased {itemName}\nfor {price} credits!";
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.Center;
        text.fontSize = 18;

        // Destroy after 2 seconds
        Destroy(notificationObj, 2f);
    }
}