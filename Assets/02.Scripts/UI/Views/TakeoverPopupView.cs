using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>남의 땅 인수 여부 팝업. Canvas_TakeoverPopup에 붙는다.</summary>
public class TakeoverPopupView : UIView
{
    public override string PanelPath => "TakeoverPopup/Window";
    public override string DimPath => "TakeoverPopup/Dim";

    [SerializeField] private Image tileImage;
    [SerializeField] private GameObject imagePlaceholder; // "TILE IMAGE" 자리표시 글자. 땅 그림이 있으면 숨긴다
    private Color placeholderColor;
    private bool placeholderColorCached;
    [SerializeField] private TMP_Text ownerText;
    [SerializeField] private TMP_Text tileNameText;
    [SerializeField] private StageTrack stageTrack = new StageTrack();
    [SerializeField] private GameObject stageTrackRoot;   // 별 단계 표시 (특수 칸에서는 숨김)
    [SerializeField] private GameObject specialBadge;     // "특수 칸" 배지 (특수 칸에서만 보임)
    [SerializeField] private TMP_Text descText;           // 이름 아래 안내 줄 (특수 칸에서는 땅 효과 설명으로 바뀜)
    [SerializeField] private TMP_Text priceText;
    [SerializeField] private TMP_Text cashText;
    [SerializeField] private Button cancelButton;
    [SerializeField] private Button takeoverButton;

    public override void Bind()
    {
        const string w = "TakeoverPopup/Window/";
        tileImage = Find<Image>(w + "TilePortrait/TileImage");
        imagePlaceholder = FindObject(w + "TilePortrait/PlaceholderText");
        ownerText = Find<TMP_Text>(w + "TilePortrait/OwnerChip/OwnerText");
        tileNameText = Find<TMP_Text>(w + "TileNameText");
        stageTrack.Bind(transform.Find(w + "StageTrack"));
        stageTrackRoot = FindObject(w + "StageTrack");
        specialBadge = FindObject(w + "SpecialBadge");
        descText = Find<TMP_Text>(w + "StageDescText");
        priceText = Find<TMP_Text>(w + "InfoPanel/TakeoverRow/ValueText");
        cashText = Find<TMP_Text>(w + "InfoPanel/CashRow/ValueText");
        cancelButton = Find<Button>(w + "CancelButton");
        takeoverButton = Find<Button>(w + "TakeoverButton");
    }

    // 안내 줄의 원래 모양 (특수 칸에서 바꿨다가 되돌린다)
    private string normalDesc;
    private bool normalWrap;
    private float normalDescHeight;

    /// <summary>specialDescription이 있으면 특수 칸: 별 단계 대신 "특수 칸" 배지를, 안내 줄에는 땅 효과 설명을 보여준다.</summary>
    public void Show(string tileName, Sprite image, BuildingLevel level,
                     string ownerName, int ownerColorIndex,
                     long takeoverPrice, long cash, Action onTakeover, Action onCancel,
                     string specialDescription = null)
    {
        if (descText != null && normalDesc == null)
        {
            normalDesc = descText.text;
            normalWrap = descText.enableWordWrapping;
            normalDescHeight = descText.rectTransform.sizeDelta.y;
        }

        bool special = specialDescription != null;
        SetActive(stageTrackRoot, !special);
        SetActive(specialBadge, special);
        if (descText != null)
        {
            descText.text = special ? specialDescription : normalDesc;
            descText.enableWordWrapping = special || normalWrap;
            var size = descText.rectTransform.sizeDelta;
            size.y = special ? 48f : normalDescHeight; // 설명이 두 줄까지 들어가게 한다 (아래 정보칸과 겹치지 않는 높이)
            descText.rectTransform.sizeDelta = size;
        }

        if (tileImage != null)
        {
            if (!placeholderColorCached) { placeholderColor = tileImage.color; placeholderColorCached = true; }
            tileImage.sprite = image;
            tileImage.color = image != null ? Color.white : placeholderColor; // 그림이 없으면 이전 땅 그림이 남지 않게 자리표시로 되돌린다
        }
        SetActive(imagePlaceholder, image == null);
        if (tileNameText != null) tileNameText.text = tileName;
        if (ownerText != null)
            ownerText.text = Loc.T("<color={0}>●</color> {1} 님의 땅", UIPalette.Hex(UIPalette.PlayerColor(ownerColorIndex)), ownerName);
        if (!special) stageTrack.Set(level);

        if (priceText != null)
        {
            priceText.text = UIPalette.Money(takeoverPrice);
            priceText.color = Color.black; // 인수 금액은 검정색
        }
        if (cashText != null)
        {
            cashText.text = UIPalette.Money(cash);
            // 보유 현금: 인수 금액을 낼 수 있으면 초록, 모자라면 빨강
            cashText.color = cash >= takeoverPrice ? UIPalette.GainText : UIPalette.Red;
        }

        if (takeoverButton != null) takeoverButton.interactable = cash >= takeoverPrice;
        SetOnClick(takeoverButton, () => { Close(); onTakeover?.Invoke(); });
        SetOnClick(cancelButton, () => { Close(); onCancel?.Invoke(); });
        Open();
    }
}