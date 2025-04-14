using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Firebase.Storage;
using UnityEngine.Networking;
using Unity.Netcode;
using UnityChess;

/// <summary>
/// Manages player profile pictures in a networked chess game.
/// Handles downloading, purchasing, and synchronizing profile data across network clients.
/// Uses RPCs only for network communication (no NetworkVariables).
/// </summary>
public class ProfileManager : NetworkBehaviour
{
    public static ProfileManager Instance { get; private set; }

    // Firebase Storage
    private FirebaseStorage storage;
    private StorageReference storageReference;

    // Player data
    [SerializeField] private int playerCredits = 1000;
    private HashSet<string> ownedProfilePics = new HashSet<string>();
    private Dictionary<string, Texture2D> downloadedProfilePics = new Dictionary<string, Texture2D>();

    // Currently active profile pics
    private string whiteProfilePic = "default";
    private string blackProfilePic = "default";

    // Local storage path
    private string localProfilePicsPath;

    // UI References (based on your UI structure)
    [Header("UI References")] [SerializeField]
    private GameObject dlcStoreUI;

    [SerializeField] private TMP_Text creditsText;
    [SerializeField] private Button closeButton;
    [SerializeField] private Button openStoreButton;

    [Header("White Avatar")] [SerializeField]
    private Image whiteSideAvatar;

    [Header("Black Avatar")] [SerializeField]
    private Image blackSideAvatar;

    [Header("Default Item")] [SerializeField]
    private Image defaultItemPreview;

    [SerializeField] private TMP_Text defaultItemName;
    [SerializeField] private TMP_Text defaultItemPrice;
    [SerializeField] private Button defaultPurchaseBtn;

    [Header("Pixel Item")] [SerializeField]
    private Image pixelItemPreview;

    [SerializeField] private TMP_Text pixelItemName;
    [SerializeField] private TMP_Text pixelItemPrice;
    [SerializeField] private Button pixelPurchaseBtn;

    [Header("Ownership Indicators")] [SerializeField]
    private GameObject defaultOwnedIndicator;

    [SerializeField] private GameObject pixelOwnedIndicator;

    [Header("ClearPurchaseData")] [SerializeField]
    private Button clearPurchaseDataButton;

    [Header("Debug Tools")] [SerializeField]
    private Button resetPlayerDataButton;

    // Currently applied skin
    private string currentlyAppliedSkin = "default";

    // Available profile pics info
    private List<ProfilePicInfo> availableProfilePics = new List<ProfilePicInfo>();

    [Serializable]
    public class ProfilePicInfo
    {
        public string id;
        public string displayName;
        public int price;
        public string storagePath;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Set up local storage path
        localProfilePicsPath = GetLocalStoragePath();

        // Default avatar is always owned
        ownedProfilePics.Add("default");

        // Load data from persistent storage
        LoadPlayerData();
    }

    private void Start()
    {
        // Set up button listeners
        if (openStoreButton != null)
            openStoreButton.onClick.AddListener(OpenStore);

        if (closeButton != null)
            closeButton.onClick.AddListener(CloseStore);

        // Set up purchase buttons
        if (defaultPurchaseBtn != null)
            defaultPurchaseBtn.onClick.AddListener(() => PurchaseProfilePic("default"));

        if (pixelPurchaseBtn != null)
            pixelPurchaseBtn.onClick.AddListener(() => PurchaseProfilePic("pixel"));

        if (clearPurchaseDataButton != null)
            clearPurchaseDataButton.onClick.AddListener(ClearPurchaseData);

        if (resetPlayerDataButton != null)
        {
            resetPlayerDataButton.onClick.AddListener(DeleteAllPlayerDataFiles);
            Debug.Log("Reset player data button connected");
        }

        // Hide store initially
        if (dlcStoreUI != null)
            dlcStoreUI.SetActive(false);

        // Initialize Firebase
        InitializeFirebase();

        // Set up available profile pics
        InitializeAvailableProfilePics();

        // Load locally saved profile pics
        LoadLocalProfilePics();

        // Download owned pics that aren't already downloaded
        DownloadOwnedProfilePics();

        // Update UI
        UpdateCreditsUI();
        UpdateProfileUI();
        UpdateStoreUI();
    }

    private string GetLocalStoragePath()
    {
        string basePath = Path.Combine(Application.persistentDataPath, "ProfilePics");

        // If we're in a network game, use different paths for host and client
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
        {
            if (NetworkManager.Singleton.IsHost)
                basePath = Path.Combine(Application.persistentDataPath, "ProfilePics_Host");
            else
                basePath = Path.Combine(Application.persistentDataPath, "ProfilePics_Client");
        }

        // Ensure the directory exists
        if (!Directory.Exists(basePath))
            Directory.CreateDirectory(basePath);

        return basePath;
    }

    private void OnEnable()
    {
        // Subscribe to NetworkManager events if needed
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        }
    }

    private void OnDisable()
    {
        // Unsubscribe from NetworkManager events
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
        }
    }

    private void OnClientConnected(ulong clientId)
    {
        // When a new client connects, send them the current profile pic data
        if (IsServer && clientId != NetworkManager.Singleton.LocalClientId)
        {
            // Send current profile picture states to the new client
            SyncProfilePicsClientRpc(whiteProfilePic, blackProfilePic, new ClientRpcParams
            {
                Send = new ClientRpcSendParams
                {
                    TargetClientIds = new ulong[] { clientId }
                }
            });

            Debug.Log(
                $"Sent initial profile pics to client {clientId}: White={whiteProfilePic}, Black={blackProfilePic}");
        }
        else if (!IsServer && clientId == NetworkManager.Singleton.LocalClientId)
        {
            // Connected as a client, request current state
            RequestProfilePicSyncServerRpc();
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestProfilePicSyncServerRpc(ServerRpcParams serverRpcParams = default)
    {
        ulong clientId = serverRpcParams.Receive.SenderClientId;
        Debug.Log($"Client {clientId} requested profile pic sync");

        // Send the current state to the requesting client
        SyncProfilePicsClientRpc(whiteProfilePic, blackProfilePic, new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new ulong[] { clientId }
            }
        });
    }

    [ClientRpc]
    private void SyncProfilePicsClientRpc(string whitePic, string blackPic, ClientRpcParams clientRpcParams = default)
    {
        Debug.Log($"Received profile pic sync: White={whitePic}, Black={blackPic}");

        // Don't update if we're the server (host)
        if (IsServer)
            return;

        Side localSide = ChessNetworkManager.Instance.GetPlayerSide(NetworkManager.Singleton.LocalClientId);

        // Update only the OPPONENT'S profile pic
        if (localSide == Side.White)
        {
            // If we're White, update only the Black profile pic
            blackProfilePic = blackPic;

            // Download the Black profile pic if needed
            if (!downloadedProfilePics.ContainsKey(blackPic))
                StartCoroutine(DownloadProfilePic(blackPic));
        }
        else if (localSide == Side.Black)
        {
            // If we're Black, update only the White profile pic
            whiteProfilePic = whitePic;

            // Download the White profile pic if needed
            if (!downloadedProfilePics.ContainsKey(whitePic))
                StartCoroutine(DownloadProfilePic(whitePic));
        }

        // Update UI
        UpdateProfileUI();

        // Update currently applied skin
        UpdateCurrentlyAppliedSkin();
    }

    private async void InitializeFirebase()
    {
        try
        {
            // Initialize Storage
            storage = FirebaseStorage.DefaultInstance;
            storageReference = storage.GetReferenceFromUrl("gs://josef-attard-cg-dlcstore.firebasestorage.app");
            Debug.Log("Firebase Storage initialized successfully");

            // Once Firebase is initialized, download profile pics
            DownloadOwnedProfilePics();
        }
        catch (Exception e)
        {
            Debug.LogError($"Firebase initialization error: {e.Message}");
        }
    }

    private void InitializeAvailableProfilePics()
    {
        // Set up available profile pics
        availableProfilePics.Clear();

        // Default profile pic (free)
        availableProfilePics.Add(new ProfilePicInfo
        {
            id = "default",
            displayName = "Default avatar skin",
            price = 0,
            storagePath = "Queen.PNG"
        });

        // Pixel profile pic (premium)
        availableProfilePics.Add(new ProfilePicInfo
        {
            id = "pixel",
            displayName = "Pixel avatar skin",
            price = 500,
            storagePath = "WhiteQueen.png"
        });
    }

    private void LoadPlayerData()
    {
        // Get a unique player data file path based on the network role
        string fileName = "playerData";

        // If we're in a networked game, use different files for host and client
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
        {
            if (NetworkManager.Singleton.IsHost)
                fileName = "playerData_host";
            else
                fileName = "playerData_client";
        }

        string dataPath = Path.Combine(Application.persistentDataPath, fileName + ".json");
        Debug.Log($"Loading player data from: {dataPath}");

        if (File.Exists(dataPath))
        {
            try
            {
                string json = File.ReadAllText(dataPath);
                PlayerData data = JsonUtility.FromJson<PlayerData>(json);

                playerCredits = data.credits;

                // Only load profile pic for your own side
                if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient &&
                    ChessNetworkManager.Instance != null)
                {
                    Side localSide = ChessNetworkManager.Instance.GetPlayerSide(NetworkManager.Singleton.LocalClientId);

                    if (localSide == Side.White)
                        whiteProfilePic = data.whiteProfilePic;
                    else if (localSide == Side.Black)
                        blackProfilePic = data.blackProfilePic;
                }
                else
                {
                    // Not in a network game, load both
                    whiteProfilePic = data.whiteProfilePic;
                    blackProfilePic = data.blackProfilePic;
                }

                // Parse owned profile pics
                ownedProfilePics.Clear();
                ownedProfilePics.Add("default"); // Always ensure default is owned

                foreach (string picId in data.ownedProfilePics.Split(','))
                {
                    if (!string.IsNullOrEmpty(picId) && picId != "default")
                        ownedProfilePics.Add(picId);
                }

                Debug.Log($"Player data loaded: Credits={playerCredits}, Owned Pics={ownedProfilePics.Count}");
            }
            catch (Exception e)
            {
                Debug.LogError($"Error loading player data: {e.Message}");

                // Set defaults if loading fails
                ResetToDefaultData();
            }
        }
        else
        {
            // Set defaults for new players
            ResetToDefaultData();
        }

        // Update currently applied skin
        UpdateCurrentlyAppliedSkin();
    }

    private void ResetToDefaultData()
    {
        playerCredits = 1000;
        whiteProfilePic = "default";
        blackProfilePic = "default";
        ownedProfilePics.Clear();
        ownedProfilePics.Add("default");

        // Save default data
        SavePlayerData();
    }

    private void SavePlayerData()
    {
        // Get a unique player data file path based on the network role
        string fileName = "playerData";

        // If we're in a networked game, use different files for host and client
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
        {
            if (NetworkManager.Singleton.IsHost)
                fileName = "playerData_host";
            else
                fileName = "playerData_client";
        }

        string dataPath = Path.Combine(Application.persistentDataPath, fileName + ".json");
        Debug.Log($"Saving player data to: {dataPath}");

        try
        {
            PlayerData data = new PlayerData
            {
                credits = playerCredits,
                whiteProfilePic = whiteProfilePic,
                blackProfilePic = blackProfilePic,
                ownedProfilePics = string.Join(",", ownedProfilePics)
            };

            string json = JsonUtility.ToJson(data);
            File.WriteAllText(dataPath, json);
            Debug.Log("Player data saved successfully");
        }
        catch (Exception e)
        {
            Debug.LogError($"Error saving player data: {e.Message}");
        }
    }

    [Serializable]
    private class PlayerData
    {
        public int credits = 1000;
        public string whiteProfilePic = "default";
        public string blackProfilePic = "default";
        public string ownedProfilePics = "default";
    }

    private void LoadLocalProfilePics()
    {
        if (!Directory.Exists(localProfilePicsPath)) return;

        foreach (string file in Directory.GetFiles(localProfilePicsPath, "*.png"))
        {
            string picId = Path.GetFileNameWithoutExtension(file);
            LoadProfilePicFromDisk(picId);
        }
    }

    private void LoadProfilePicFromDisk(string picId)
    {
        string filePath = Path.Combine(GetLocalStoragePath(), $"{picId}.png");

        if (!File.Exists(filePath)) return;

        try
        {
            byte[] fileData = File.ReadAllBytes(filePath);
            Texture2D texture = new Texture2D(2, 2);

            if (texture.LoadImage(fileData))
            {
                downloadedProfilePics[picId] = texture;
                Debug.Log($"Loaded profile pic {picId} from disk");

                // Update UI after loading
                UpdateProfileUI();
                UpdateStoreUI();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error loading profile pic {picId} from disk: {e.Message}");
        }
    }

    private void DownloadOwnedProfilePics()
    {
        // If Firebase isn't initialized yet, we'll skip for now
        if (storage == null || storageReference == null)
        {
            Debug.Log("Firebase not initialized yet, skipping profile pic downloads");
            return;
        }

        foreach (string picId in ownedProfilePics)
        {
            if (!downloadedProfilePics.ContainsKey(picId))
            {
                var picInfo = availableProfilePics.Find(p => p.id == picId);
                if (picInfo != null)
                {
                    StartCoroutine(DownloadProfilePic(picId));
                }
            }
        }
    }

    private IEnumerator DownloadProfilePic(string picId)
    {
        // Skip if already downloaded
        if (downloadedProfilePics.ContainsKey(picId))
        {
            Debug.Log($"Profile pic {picId} already downloaded, skipping");
            yield break;
        }

        // Ensure Firebase is initialized
        if (storage == null || storageReference == null)
        {
            Debug.LogError("Firebase Storage not initialized");
            yield break;
        }

        // Get profile pic info
        var picInfo = availableProfilePics.Find(p => p.id == picId);
        if (picInfo == null)
        {
            Debug.LogError($"Profile pic {picId} not found in available pics");
            yield break;
        }

        Debug.Log($"Starting download for profile pic: {picId} from path: {picInfo.storagePath}");

        // Reference to the file in Firebase Storage
        StorageReference picRef = null;

        try
        {
            picRef = storageReference.Child(picInfo.storagePath);
        }
        catch (Exception e)
        {
            Debug.LogError($"Error creating storage reference: {e.Message}");
            yield break;
        }

        // Get download URL
        Task<Uri> downloadUrlTask = null;
        try
        {
            downloadUrlTask = picRef.GetDownloadUrlAsync();
        }
        catch (Exception e)
        {
            Debug.LogError($"Error starting download task: {e.Message}");
            yield break;
        }

        // Wait for the URL task to complete (outside try/catch)
        yield return new WaitUntil(() => downloadUrlTask.IsCompleted);

        // Check for exceptions in the completed task
        if (downloadUrlTask.Exception != null)
        {
            Debug.LogError($"Failed to get download URL: {downloadUrlTask.Exception}");
            yield break;
        }

        string downloadUrl = downloadUrlTask.Result.ToString();
        Debug.Log($"Got download URL: {downloadUrl}");

        // Download the file - this part needs to be outside try/catch for yield to work
        UnityWebRequest www = UnityWebRequestTexture.GetTexture(downloadUrl);
        yield return www.SendWebRequest();

        // Process the download result
        if (www.result == UnityWebRequest.Result.Success)
        {
            Texture2D texture = DownloadHandlerTexture.GetContent(www);

            if (texture != null)
            {
                // Store the texture
                downloadedProfilePics[picId] = texture;

                try
                {
                    // Save to disk - USING GetLocalStoragePath() INSTEAD OF localProfilePicsPath
                    string savePath = Path.Combine(GetLocalStoragePath(), $"{picId}.png");
                    File.WriteAllBytes(savePath, texture.EncodeToPNG());
                    Debug.Log($"Saved texture to {savePath}");
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error saving texture to disk: {e.Message}");
                    // Continue anyway since we already have the texture in memory
                }

                Debug.Log($"Downloaded profile pic {picInfo.displayName} successfully");

                // Update UI
                UpdateProfileUI();
                UpdateStoreUI();

                // Notify other clients if this is a newly purchased item 
                if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
                {
                    Side localSide = ChessNetworkManager.Instance.GetPlayerSide(NetworkManager.Singleton.LocalClientId);
                    if ((localSide == Side.White && whiteProfilePic == picId) ||
                        (localSide == Side.Black && blackProfilePic == picId))
                    {
                        NotifyNetworkAboutProfilePicChange();
                    }
                }
            }
            else
            {
                Debug.LogError($"Failed to create texture for {picId}");
            }
        }
        else
        {
            Debug.LogError($"Download failed: {www.error}");
        }

        www.Dispose();
    }

    private Sprite CreateSpriteFromTexture(Texture2D texture)
    {
        return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
    }

    public void OpenStore()
    {
        if (dlcStoreUI == null) return;

        dlcStoreUI.SetActive(true);
        UpdateStoreUI();
    }

    public void CloseStore()
    {
        if (dlcStoreUI == null) return;

        dlcStoreUI.SetActive(false);
    }

    private void UpdateStoreUI()
    {
        // Log some debug information
        Side localSide = Side.None;
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient &&
            ChessNetworkManager.Instance != null)
        {
            localSide = ChessNetworkManager.Instance.GetPlayerSide(NetworkManager.Singleton.LocalClientId);
        }

        Debug.Log($"UpdateStoreUI - Player Side: {localSide}, Credits: {playerCredits}, " +
                  $"White: {whiteProfilePic}, Black: {blackProfilePic}, Applied: {currentlyAppliedSkin}, " +
                  $"Owned: {string.Join(",", ownedProfilePics)}");

        // Update credits text
        if (creditsText != null)
            creditsText.text = $"Credits: {playerCredits}";

        // Update default item
        if (defaultItemName != null)
            defaultItemName.text = "Default avatar skin";

        if (defaultItemPrice != null)
            defaultItemPrice.text = "FREE";

        if (defaultItemPreview != null && downloadedProfilePics.TryGetValue("default", out Texture2D defaultTexture))
            defaultItemPreview.sprite = CreateSpriteFromTexture(defaultTexture);

        if (defaultPurchaseBtn != null)
        {
            TMP_Text buttonText = defaultPurchaseBtn.GetComponentInChildren<TMP_Text>();
            if (buttonText != null)
            {
                // Set text based on whether this skin is currently applied
                bool isApplied = currentlyAppliedSkin == "default";
                buttonText.text = isApplied ? "Applied" : "Apply";
                defaultPurchaseBtn.interactable = !isApplied; // Disable button when applied

                Debug.Log(
                    $"Default skin button text: {buttonText.text}, Interactable: {defaultPurchaseBtn.interactable}");
            }
        }

        // Show/hide the "Owned" indicator for default item
        if (defaultOwnedIndicator != null)
        {
            defaultOwnedIndicator.SetActive(ownedProfilePics.Contains("default"));
            Debug.Log($"Default owned indicator active: {defaultOwnedIndicator.activeSelf}");
        }

        // Update pixel item
        if (pixelItemName != null)
            pixelItemName.text = "Pixel avatar skin";

        if (pixelItemPrice != null)
            pixelItemPrice.text = "500 Credits";

        if (pixelItemPreview != null && downloadedProfilePics.TryGetValue("pixel", out Texture2D pixelTexture))
            pixelItemPreview.sprite = CreateSpriteFromTexture(pixelTexture);

        if (pixelPurchaseBtn != null)
        {
            TMP_Text buttonText = pixelPurchaseBtn.GetComponentInChildren<TMP_Text>();
            if (buttonText != null)
            {
                bool owned = ownedProfilePics.Contains("pixel");
                bool isApplied = currentlyAppliedSkin == "pixel";
                bool canAfford = playerCredits >= 500;

                if (!owned)
                {
                    // Not owned yet - show Purchase button
                    buttonText.text = "Purchase";
                    pixelPurchaseBtn.interactable = canAfford;
                }
                else if (isApplied)
                {
                    // Owned and applied - show Applied button (disabled)
                    buttonText.text = "Applied";
                    pixelPurchaseBtn.interactable = false;
                }
                else
                {
                    // Owned but not applied - show Apply button
                    buttonText.text = "Apply";
                    pixelPurchaseBtn.interactable = true;
                }

                Debug.Log(
                    $"Pixel skin button text: {buttonText.text}, Interactable: {pixelPurchaseBtn.interactable}, " +
                    $"Owned: {owned}, Applied: {isApplied}, CanAfford: {canAfford}");
            }
        }

        // Show/hide the "Owned" indicator for pixel item
        if (pixelOwnedIndicator != null)
        {
            pixelOwnedIndicator.SetActive(ownedProfilePics.Contains("pixel"));
            Debug.Log($"Pixel owned indicator active: {pixelOwnedIndicator.activeSelf}");
        }
    }

    private void UpdateProfileUI()
    {
        // Update white profile image
        if (whiteSideAvatar != null)
        {
            if (downloadedProfilePics.TryGetValue(whiteProfilePic, out Texture2D whiteTex))
            {
                whiteSideAvatar.sprite = CreateSpriteFromTexture(whiteTex);
                whiteSideAvatar.color = Color.white; // Reset color to show the image clearly
            }
        }

        // Update black profile image
        if (blackSideAvatar != null)
        {
            if (downloadedProfilePics.TryGetValue(blackProfilePic, out Texture2D blackTex))
            {
                blackSideAvatar.sprite = CreateSpriteFromTexture(blackTex);
                blackSideAvatar.color = Color.white; // Reset color to show the image clearly
            }
        }
    }

    // Method to notify DLCIntegration of purchases 
    private void NotifyDLCIntegrationOfPurchase(string itemId, string displayName, int price)
    {
        // Find DLCIntegration component (should be on same GameObject)
        DLCIntegration dlcIntegration = GetComponent<DLCIntegration>();

        if (dlcIntegration != null)
        {
            // Log clear message
            Debug.Log($"PURCHASE EVENT: Notifying DLCIntegration about purchase of {displayName} for {price} credits");

            // Notify DLCIntegration about the purchase
            dlcIntegration.OnProfilePicPurchased(itemId, displayName, price);
        }
        else
        {
            Debug.LogError("DLCIntegration component not found - purchase won't be logged to analytics");
        }
    }

    public void PurchaseProfilePic(string picId)
    {
        var picInfo = availableProfilePics.Find(p => p.id == picId);
        if (picInfo == null)
        {
            Debug.LogError($"Profile pic {picId} not found in available pics");
            return;
        }

        // If already owned, just apply it
        if (ownedProfilePics.Contains(picId))
        {
            ApplyProfilePic(picId);
            return;
        }

        // Check if player has enough credits
        if (playerCredits < picInfo.price)
        {
            Debug.Log($"Not enough credits to purchase {picInfo.displayName}");
            return;
        }

        // This is an actual purchase (not just applying) - track it with Firebase
        bool isNewPurchase = !ownedProfilePics.Contains(picId);

        // Deduct credits
        playerCredits -= picInfo.price;

        // Add to owned pics
        ownedProfilePics.Add(picId);

        // Save player data
        SavePlayerData();

        // Download the profile pic if not already downloaded
        if (!downloadedProfilePics.ContainsKey(picId))
            StartCoroutine(DownloadProfilePic(picId));

        Debug.Log($"Purchased profile pic {picInfo.displayName} for {picInfo.price} credits");

        // Only notify DLCIntegration if this is a new purchase (not when applying an already owned skin)
        if (isNewPurchase)
        {
            // Notify DLCIntegration about the purchase
            NotifyDLCIntegrationOfPurchase(picId, picInfo.displayName, picInfo.price);
            Debug.Log($"Logged purchase event for {picInfo.displayName}");
        }

        // Update UI
        UpdateStoreUI();
    }

    private void ApplyProfilePic(string picId)
    {
        if (!NetworkManager.Singleton)
        {
            Debug.LogError("NetworkManager is not available");
            return;
        }

        Side localSide = ChessNetworkManager.Instance?.GetPlayerSide(NetworkManager.Singleton.LocalClientId) ??
                         Side.None;

        if (localSide == Side.White)
        {
            whiteProfilePic = picId;
            currentlyAppliedSkin = picId; // Update currently applied skin
            Debug.Log($"Applied skin {picId} for White side");
        }
        else if (localSide == Side.Black)
        {
            blackProfilePic = picId;
            currentlyAppliedSkin = picId; // Update currently applied skin
            Debug.Log($"Applied skin {picId} for Black side");
        }
        else
        {
            Debug.LogError("Unknown local side");
            return;
        }

        // Save player data
        SavePlayerData();

        // Force the store UI to update with the correct status
        UpdateStoreUI();

        // Update UI
        UpdateProfileUI();

        // Notify DLCIntegration about the skin change
        DLCIntegration dlcIntegration = GetComponent<DLCIntegration>();
        if (dlcIntegration != null)
        {
            dlcIntegration.OnSkinChanged(picId);
        }

        // Notify other clients
        NotifyNetworkAboutProfilePicChange();

        Debug.Log($"Applied profile pic {picId} for {localSide}");
    }

    private void NotifyNetworkAboutProfilePicChange()
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
        {
            Side localSide = ChessNetworkManager.Instance.GetPlayerSide(NetworkManager.Singleton.LocalClientId);
            string picId = localSide == Side.White ? whiteProfilePic : blackProfilePic;

            Debug.Log($"Notifying network about profile pic change: {picId} for side {localSide}");

            // Send RPC to server with the updated profile pic
            UpdateProfilePicServerRpc((int)localSide, picId);
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void UpdateProfilePicServerRpc(int sideValue, string picId, ServerRpcParams serverRpcParams = default)
    {
        Debug.Log($"Server received profile pic change: {picId} for side {(Side)sideValue}");

        // Get the client ID that sent this request
        ulong senderId = serverRpcParams.Receive.SenderClientId;

        // Update server's state
        if ((Side)sideValue == Side.White)
            whiteProfilePic = picId;
        else if ((Side)sideValue == Side.Black)
            blackProfilePic = picId;

        // Broadcast to ALL clients EXCEPT the sender
        foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
        {
            // Skip the client that sent the request
            if (clientId == senderId)
                continue;

            // Send update to this specific client
            UpdateProfilePicClientRpc(sideValue, picId, new ClientRpcParams
            {
                Send = new ClientRpcSendParams
                {
                    TargetClientIds = new ulong[] { clientId }
                }
            });

            Debug.Log($"Server sent profile update to client {clientId}: {picId} for side {(Side)sideValue}");
        }
    }

    [ClientRpc]
    private void UpdateProfilePicClientRpc(int sideValue, string picId, ClientRpcParams clientRpcParams = default)
    {
        Debug.Log($"Client received profile pic change: {picId} for side {(Side)sideValue}");

        // Handle the profile pic change for this client
        Side side = (Side)sideValue;
        HandleRemotePlayerProfilePicChange(side, picId);
    }

    private void HandleRemotePlayerProfilePicChange(Side side, string picId)
    {
        Debug.Log($"Handling remote profile change: {picId} for side {side}");

        // Download the profile pic if not already downloaded
        if (!downloadedProfilePics.ContainsKey(picId) && availableProfilePics.Find(p => p.id == picId) != null)
        {
            StartCoroutine(DownloadProfilePic(picId));
        }

        // Update the appropriate side's current profile pic
        if (side == Side.White)
            whiteProfilePic = picId;
        else if (side == Side.Black)
            blackProfilePic = picId;

        // Update UI
        UpdateProfileUI();

        Debug.Log($"Updated profile UI for side {side} with pic {picId}");
    }

    private void UpdateCreditsUI()
    {
        if (creditsText != null)
            creditsText.text = $"Credits: {playerCredits}";
    }

    // Test method to add credits (for debugging)
    public void AddCredits(int amount)
    {
        playerCredits += amount;
        SavePlayerData();
        UpdateCreditsUI();
        UpdateStoreUI();
    }

    /// <summary>
    /// Checks if the player can afford the specified amount
    /// </summary>
    public bool CanAfford(int amount)
    {
        return playerCredits >= amount;
    }

    /// <summary>
    /// Ensures that the currentlyAppliedSkin variable is set correctly based on the network role
    /// </summary>
    private void UpdateCurrentlyAppliedSkin()
    {
        // Only update in a network game
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient &&
            ChessNetworkManager.Instance != null)
        {
            Side localSide = ChessNetworkManager.Instance.GetPlayerSide(NetworkManager.Singleton.LocalClientId);

            if (localSide == Side.White)
            {
                currentlyAppliedSkin = whiteProfilePic;
                Debug.Log($"UpdateCurrentlyAppliedSkin: Set to White's skin: {currentlyAppliedSkin}");
            }
            else if (localSide == Side.Black)
            {
                currentlyAppliedSkin = blackProfilePic;
                Debug.Log($"UpdateCurrentlyAppliedSkin: Set to Black's skin: {currentlyAppliedSkin}");
            }
            else
            {
                Debug.LogWarning($"UpdateCurrentlyAppliedSkin: Unknown side {localSide}, defaulting to 'default'");
                currentlyAppliedSkin = "default";
            }
        }
        else
        {
            // Not in a network game, use default
            currentlyAppliedSkin = "default";
            Debug.Log("UpdateCurrentlyAppliedSkin: Not in network game, using default");
        }

        // Debug log the profile pics and applied skin
        Debug.Log(
            $"Current state - White: {whiteProfilePic}, Black: {blackProfilePic}, Applied: {currentlyAppliedSkin}");
    }

    public void ClearPurchaseData()
    {
        Debug.Log("Clear Purchase Data button clicked");

        // Reset credits to initial amount
        playerCredits = 1000;

        // Keep only the default item as owned
        ownedProfilePics.Clear();
        ownedProfilePics.Add("default");

        // Only reset the profile pic for THIS player's side
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient &&
            ChessNetworkManager.Instance != null)
        {
            Side localSide = ChessNetworkManager.Instance.GetPlayerSide(NetworkManager.Singleton.LocalClientId);

            // Only reset the profile pic for the local player's side
            if (localSide == Side.White)
            {
                whiteProfilePic = "default";
            }
            else if (localSide == Side.Black)
            {
                blackProfilePic = "default";
            }
        }
        else
        {
            // If not in a network game, reset both
            whiteProfilePic = "default";
            blackProfilePic = "default";
        }

        // Reset currently applied skin
        currentlyAppliedSkin = "default";

        // Save the reset data
        SavePlayerData();

        // Update UI
        UpdateCreditsUI();
        UpdateProfileUI();
        UpdateStoreUI();

        // If in a network game, notify other clients about OUR change only
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
        {
            NotifyNetworkAboutProfilePicChange();
        }

        Debug.Log("Purchase data cleared and reset to initial state");
    }

    /// <summary>
    /// Deletes all player data files to completely reset the state
    /// This should be called when testing to ensure old data doesn't affect new runs
    /// </summary>
    public void DeleteAllPlayerDataFiles()
    {
        Debug.Log("Attempting to delete all player data files...");

        // Delete all profile pic files
        string[] storagePaths = new[]
        {
            Path.Combine(Application.persistentDataPath, "ProfilePics"),
            Path.Combine(Application.persistentDataPath, "ProfilePics_Host"),
            Path.Combine(Application.persistentDataPath, "ProfilePics_Client")
        };

        foreach (string path in storagePaths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    string[] files = Directory.GetFiles(path, "*.png");
                    foreach (string file in files)
                    {
                        File.Delete(file);
                        Debug.Log($"Deleted profile pic file: {file}");
                    }

                    Debug.Log($"Cleared directory: {path}");
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error deleting files in {path}: {e.Message}");
                }
            }
        }

        // Delete all player data files
        string[] playerDataFiles = new[]
        {
            Path.Combine(Application.persistentDataPath, "playerData.json"),
            Path.Combine(Application.persistentDataPath, "playerData_host.json"),
            Path.Combine(Application.persistentDataPath, "playerData_client.json")
        };

        foreach (string file in playerDataFiles)
        {
            if (File.Exists(file))
            {
                try
                {
                    File.Delete(file);
                    Debug.Log($"Deleted player data file: {file}");
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error deleting file {file}: {e.Message}");
                }
            }
        }

        // Clear the in-memory collections
        downloadedProfilePics.Clear();
        ownedProfilePics.Clear();
        ownedProfilePics.Add("default"); // Always ensure default is owned

        // Reset to default values
        playerCredits = 1000;
        whiteProfilePic = "default";
        blackProfilePic = "default";
        currentlyAppliedSkin = "default";

        // Update UI if available
        UpdateCreditsUI();
        UpdateProfileUI();
        UpdateStoreUI();

        Debug.Log("All player data has been reset");

        // Show a temporary notification for feedback
        StartCoroutine(ShowResetConfirmation());
    }

    /// <summary>
    /// Shows a temporary confirmation message for successful data reset
    /// </summary>
    private IEnumerator ShowResetConfirmation()
    {
        // Create confirmation UI
        GameObject confirmObj = new GameObject("ResetConfirmation");
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
        text.text = "Player data reset successfully!";
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.Center;
        text.fontSize = 18;

        // Wait for 2 seconds
        yield return new WaitForSeconds(2f);

        // Destroy the confirmation
        Destroy(confirmObj);
    }
}