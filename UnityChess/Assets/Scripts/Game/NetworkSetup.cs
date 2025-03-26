using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Sets up networking components for the chess game.
/// This script avoids adding NetworkObjects to individual squares.
/// </summary>
public class NetworkSetup : MonoBehaviour
{
    [SerializeField] private GameObject chessBoard;

    private void Start()
    {
        // Only set up minimal networking, avoiding NetworkObjects on individual squares
        SetupMinimalNetworking();
    }

    private void SetupMinimalNetworking()
    {
        Debug.Log("Setting up minimal networking for the chess game");

        // Find chessBoard if not assigned
        if (chessBoard == null)
        {
            chessBoard = GameObject.FindGameObjectWithTag("Board");
            if (chessBoard == null)
            {
                // Try to find by name
                chessBoard = GameObject.Find("Board");
            }
        }

        if (chessBoard != null)
        {
            Debug.Log("Found chess board, configuring for networking");

            // Ensure the board itself has a NetworkObject
            if (chessBoard.GetComponent<NetworkObject>() == null)
            {
                NetworkObject boardNetObj = chessBoard.AddComponent<NetworkObject>();
                boardNetObj.DontDestroyWithOwner = true;
            }

            // DO NOT add NetworkObjects to individual squares as it causes conflicts

            // Make sure there's a ChessMoveRelay in the scene
            SetupChessMoveRelay();
        }
        else
        {
            Debug.LogWarning("Chess board reference not assigned or found in NetworkSetup");
        }
    }

    private void SetupChessMoveRelay()
    {
        // Find or create ChessMoveRelay
        ChessMoveRelay relay = FindObjectOfType<ChessMoveRelay>();
        if (relay == null)
        {
            GameObject relayObj = new GameObject("ChessMoveRelay");
            relay = relayObj.AddComponent<ChessMoveRelay>();

            // Set the chess board reference
            relay.gameObject.AddComponent<NetworkObject>();
        }
    }
}