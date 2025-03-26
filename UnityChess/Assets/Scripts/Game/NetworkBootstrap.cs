using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;

/// <summary>
/// Bootstraps the networking components for the chess game.
/// This script ensures all required network components are properly initialized.
/// </summary>
public class NetworkBootstrap : MonoBehaviour
{
    [SerializeField] private GameObject connectionUIPrefab;

    private void Awake()
    {
        // Ensure we have the NetworkManager
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("NetworkManager not found in scene! Make sure to add one.");
        }
        else
        {
            Debug.Log("NetworkManager found. Bootstrap successful.");
        }

        // Ensure we have the ChessNetworkManager
        if (ChessNetworkManager.Instance == null)
        {
            // Instantiate the connection UI if provided
            if (connectionUIPrefab != null)
            {
                Instantiate(connectionUIPrefab);
            }
            else
            {
                Debug.LogWarning("Connection UI prefab not assigned. You'll need to manually set up the UI.");
            }
        }
    }

    private void Start()
    {
        // Make sure all important network components are available
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("NetworkManager not found! Make sure it's included in the scene.");
        }

        if (ChessNetworkManager.Instance == null)
        {
            Debug.LogError("ChessNetworkManager not found! Make sure it's included in the scene.");
        }
    }
}