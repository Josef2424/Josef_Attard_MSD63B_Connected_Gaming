using UnityEngine;
using UnityEngine.UI;
using UnityChess;

/// <summary>
/// Extends the promotion UI to work with networked games.
/// This script captures promotion choices and feeds them back to the network system.
/// </summary>
public class PromotionExtender : MonoBehaviour
{
    // Buttons in the promotion UI
    [SerializeField] private Button queenButton;
    [SerializeField] private Button rookButton;
    [SerializeField] private Button bishopButton;
    [SerializeField] private Button knightButton;

    private void Start()
    {
        // Add listeners to the promotion buttons
        if (queenButton != null) queenButton.onClick.AddListener(() => OnPromotionChosen(ElectedPiece.Queen));
        if (rookButton != null) rookButton.onClick.AddListener(() => OnPromotionChosen(ElectedPiece.Rook));
        if (bishopButton != null) bishopButton.onClick.AddListener(() => OnPromotionChosen(ElectedPiece.Bishop));
        if (knightButton != null) knightButton.onClick.AddListener(() => OnPromotionChosen(ElectedPiece.Knight));
    }

    /// <summary>
    /// Called when a promotion piece is chosen
    /// </summary>
    private void OnPromotionChosen(ElectedPiece choice)
    {
        // The original election will work automatically through the GameManager
        // We don't need to do anything special here, as the ChessNetworkManager 
        // will intercept the resulting VisualPieceMoved event when the promotion occurs
    }
}