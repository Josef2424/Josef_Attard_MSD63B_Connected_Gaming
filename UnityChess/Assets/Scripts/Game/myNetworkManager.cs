using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using UnityEngine.UI;
using TMPro;

[DefaultExecutionOrder(100)]
public class myNetworkManager : MonoBehaviour
{
    [SerializeField] private Button hostButton;
    [SerializeField] private Button clientButton;
    [SerializeField] private Button serverButton;

    // Example: track connected clients count
    [SerializeField] private TMP_Text infoText;

    private void Awake()
    {
        // If you have UI Buttons, hook them up to the methods below
        if (hostButton != null) hostButton.onClick.AddListener(StartHost);
        if (clientButton != null) clientButton.onClick.AddListener(StartClient);
        if (serverButton != null) serverButton.onClick.AddListener(StartServer);

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
        }
        else
        {
            UpdateInfoText($"Client {clientId} connected. (Local client? {NetworkManager.Singleton.LocalClientId})");
        }
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (NetworkManager.Singleton.IsServer)
        {
            UpdateInfoText($"Server: Client {clientId} disconnected. Total: {GetConnectedCount()}");
        }
        else
        {
            // Possibly the local client got disconnected
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
}
