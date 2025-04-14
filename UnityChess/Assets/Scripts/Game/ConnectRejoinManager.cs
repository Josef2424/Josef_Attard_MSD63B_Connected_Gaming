using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Helper component to connect your existing Rejoin button with the new RejoinManager
/// Attach this to the same GameObject as your Rejoin button
/// </summary>
public class ConnectRejoinManager : MonoBehaviour
{
    // Reference to your existing Rejoin button
    private Button rejoinButton;

    private void Start()
    {
        // Get the button component
        rejoinButton = GetComponent<Button>();

        if (rejoinButton != null)
        {
            // Add the RejoinManager's handler as a listener to the button
            // This will work alongside any existing listeners
            rejoinButton.onClick.AddListener(OnRejoinButtonClicked);
            Debug.Log("ConnectRejoinManager: Successfully connected to rejoin button");
        }
        else
        {
            Debug.LogError("ConnectRejoinManager: No Button component found on this GameObject");
        }
    }

    private void OnRejoinButtonClicked()
    {
        // Forward the click to the RejoinManager
        if (RejoinManager.Instance != null)
        {
            Debug.Log("ConnectRejoinManager: Forwarding rejoin button click to RejoinManager");
            RejoinManager.Instance.OnRejoinButtonClicked();
        }
        else
        {
            Debug.LogError("ConnectRejoinManager: RejoinManager.Instance is null");
        }
    }
}