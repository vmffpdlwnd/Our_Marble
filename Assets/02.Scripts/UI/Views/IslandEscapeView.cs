using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>무인도 탈출 방법 선택 팝업. Canvas_IslandEscape에 붙는다.</summary>
public class IslandEscapeView : UIView
{
    public override string PanelPath => "IslandEscapePopup/Window";
    public override string DimPath => "IslandEscapePopup/Dim";

    [SerializeField] private TMP_Text turnText;
    [SerializeField] private Button doubleButton;
    [SerializeField] private Button payButton;
    [SerializeField] private TMP_Text paySubText;
    [SerializeField] private Button cardButton;
    [SerializeField] private TMP_Text cardSubText;

    public override void Bind()
    {
        const string w = "IslandEscapePopup/Window/";
        turnText = Find<TMP_Text>(w + "Header/TurnChip/TurnText");
        doubleButton = Find<Button>(w + "Options/DoubleButton");
        payButton = Find<Button>(w + "Options/PayButton");
        paySubText = Find<TMP_Text>(w + "Options/PayButton/SubText");
        cardButton = Find<Button>(w + "Options/CardButton");
        cardSubText = Find<TMP_Text>(w + "Options/CardButton/SubText");
    }

    /// <summary>현금이 부족하면 비용 지불, 탈출권이 없으면 카드 사용이 비활성화된다.</summary>
    public void Show(int turnsRemaining, long escapeCost, long cash, int escapeCardCount,
                     Action onDouble, Action onPay, Action onUseCard)
    {
        if (turnText != null) turnText.text = turnsRemaining.ToString();

        if (paySubText != null)
            paySubText.text = Loc.T("<color={0}><b>{1}</b></color> 내고 탈출", UIPalette.Hex(UIPalette.Red), UIPalette.Money(escapeCost));
        if (payButton != null) payButton.interactable = cash >= escapeCost;

        bool hasCard = escapeCardCount > 0;
        if (cardSubText != null) cardSubText.text = hasCard ? Loc.T("무인도 탈출권 <b>{0}장</b>", escapeCardCount) : Loc.T("보유한 카드 없음");
        if (cardButton != null) cardButton.interactable = hasCard;

        SetOnClick(doubleButton, () => { Close(); onDouble?.Invoke(); });
        SetOnClick(payButton, () => { Close(); onPay?.Invoke(); });
        SetOnClick(cardButton, () => { Close(); onUseCard?.Invoke(); });
        Open();
    }
}