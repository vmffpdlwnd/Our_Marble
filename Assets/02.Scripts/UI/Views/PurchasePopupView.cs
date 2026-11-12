using System;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 땅 구매 / 확장 팝업. Canvas_PurchasePopup에 붙는다.
/// "건물 / 별 1개 / 별 2개 / 별 3개" 카드 중 하나를 골라 산다.
/// 각 카드는 잠글 수 있다(예: 2바퀴째부터, 이미 보유 중).
/// </summary>
public class PurchasePopupView : UIView
{
    public override string PanelPath => "PurchasePopup/Window";
    public override string DimPath => "PurchasePopup/Dim";

    /// <summary>선택지 1개. 사고 나면 TargetLevel이 되고, 이용료는 Toll이 된다.</summary>
    public struct Option
    {
        public BuildingLevel TargetLevel;
        public long Cost;
        public long Toll;
        public bool Locked;
        public string LockReason;   // 잠겼을 때 카드에 표시. 예) "2바퀴부터", "보유 중"
        public bool ShowInfoWhenNone; // 아무것도 선택되지 않은 상태(금액 부족 등)에서도 이 카드의 이용료·비용을 보여준다
        public bool Owned;          // 이미 지어 둔(보유 중인) 단계. 별 단계 표시에 보유한 만큼 채워 보여준다
    }

    [Serializable]
    private class OptionRefs
    {
        public RectTransform root;
        public Toggle toggle;
        public TMP_Text costText;
        public GameObject lockOverlay;
        public TMP_Text lockText;
    }

    private const int OptionCount = 4; // 건물, 별 1~3개 (BuildingLevel 순서)

    [SerializeField] private Image tileImage;
    [SerializeField] private GameObject imagePlaceholder; // "TILE IMAGE" 자리표시 글자. 땅 그림이 있으면 숨긴다
    private Color placeholderColor;
    private bool placeholderColorCached;
    [SerializeField] private TMP_Text tileNameText;
    [SerializeField] private GameObject stageTrackRoot;    // 별 단계 표시 (특수 칸에서는 숨김)
    [SerializeField] private GameObject optionsRoot;       // 건물/별 선택 카드 (특수 칸에서는 숨김)
    [SerializeField] private GameObject specialBadge;      // "특수 칸" 배지 (특수 칸에서만 보임)
    [SerializeField] private GameObject effectBox;         // 땅 효과 설명 상자 (특수 칸에서만 보임)
    [SerializeField] private TMP_Text effectText;
    [SerializeField] private TMP_Text ownerText;   // 타일 사진 왼쪽 아래 칩: "빈 땅" / "● 이름 님의 땅"
    [SerializeField] private StageTrack stageTrack = new StageTrack();
    [SerializeField] private OptionRefs[] optionCards = new OptionRefs[OptionCount];
    [SerializeField] private TMP_Text tollText;
    [SerializeField] private TMP_Text costText;
    [SerializeField] private TMP_Text cashText;
    [SerializeField] private Button cancelButton;
    [SerializeField] private Button buyButton;

    private readonly Option?[] options = new Option?[OptionCount];
    private Option selected;
    private bool hasSelection;   // 선택된 카드가 있는지 (살 수 있는 카드가 없으면 아무것도 선택하지 않은 채로 시작한다)
    private long cash;
    private Sequence boughtSequence;

    public override void Bind()
    {
        const string w = "PurchasePopup/Window/";
        tileImage = Find<Image>(w + "TilePortrait/TileImage");
        imagePlaceholder = FindObject(w + "TilePortrait/PlaceholderText");
        tileNameText = Find<TMP_Text>(w + "TileNameText");
        ownerText = Find<TMP_Text>(w + "TilePortrait/OwnerChip/OwnerText");
        stageTrackRoot = FindObject(w + "StageTrack");
        optionsRoot = FindObject(w + "Options");
        specialBadge = FindObject(w + "SpecialBadge");
        effectBox = FindObject(w + "EffectBox");
        effectText = Find<TMP_Text>(w + "EffectBox/DescText");
        stageTrack.Bind(transform.Find(w + "StageTrack"));
        for (int i = 0; i < OptionCount; i++)
        {
            string p = $"{w}Options/Option_{i}";
            optionCards[i] = new OptionRefs
            {
                root = Find<RectTransform>(p),
                toggle = Find<Toggle>(p),
                costText = Find<TMP_Text>(p + "/CostText"),
                lockOverlay = FindObject(p + "/LockOverlay"),
                lockText = Find<TMP_Text>(p + "/LockOverlay/LockText"),
            };
        }
        tollText = Find<TMP_Text>(w + "InfoPanel/TollRow/ValueText");
        costText = Find<TMP_Text>(w + "InfoPanel/CostRow/ValueText");
        cashText = Find<TMP_Text>(w + "InfoPanel/CashRow/ValueText");
        cancelButton = Find<Button>(w + "CancelButton");
        buyButton = Find<Button>(w + "BuyButton");
    }

    /// <summary>
    /// options: 보여줄 선택지들. TargetLevel에 맞는 카드(건물, 별 1~3개)에 들어간다. 없는 단계의 카드는 숨긴다.
    /// 처음엔 잠기지 않은 첫 카드가 선택된다. onBuy에는 고른 단계가 넘어간다.
    /// </summary>
    public void Show(string tileName, Sprite image, IList<Option> options,
                     long cash, Action<BuildingLevel> onBuy, Action onCancel, string ownerLabel = null,
                     string specialDescription = null)
    {
        boughtSequence?.Kill(complete: false);
        this.cash = cash;
        for (int i = 0; i < OptionCount; i++) this.options[i] = null;
        if (options != null)
            foreach (var o in options)
            {
                int index = (int)o.TargetLevel;
                if (index >= 0 && index < OptionCount) this.options[index] = o;
            }

        if (cancelButton != null) cancelButton.interactable = true;
        if (tileImage != null)
        {
            if (!placeholderColorCached) { placeholderColor = tileImage.color; placeholderColorCached = true; }
            tileImage.sprite = image;
            tileImage.color = image != null ? Color.white : placeholderColor; // 그림이 없으면 이전 땅 그림이 남지 않게 자리표시로 되돌린다
        }
        SetActive(imagePlaceholder, image == null);
        if (tileNameText != null) tileNameText.text = tileName;
        // 특수 칸(별 건설 없음): 선택 카드와 별 단계 대신 "특수 칸" 배지와 효과 설명을 보여준다. 살 수 있는 것은 땅(건물)뿐이다
        bool special = specialDescription != null;
        SetActive(stageTrackRoot, !special);
        SetActive(optionsRoot, !special);
        SetActive(specialBadge, special);
        SetActive(effectBox, special);
        if (effectText != null) effectText.text = specialDescription ?? "";

        if (ownerText != null)
        {
            ownerText.text = ownerLabel ?? "";
            if (ownerText.transform.parent != null) ownerText.transform.parent.gameObject.SetActive(!string.IsNullOrEmpty(ownerLabel));
        }
        if (cashText != null) cashText.text = UIPalette.Money(cash);
        if (costText != null) costText.color = Color.black; // 구매(건설) 비용은 검정색

        int firstUnlocked = -1;
        for (int i = 0; i < OptionCount; i++)
        {
            SetupCard(i);
            if (!this.options[i].HasValue) continue;
            if (firstUnlocked < 0 && !this.options[i].Value.Locked) firstUnlocked = i;
        }
        // 살 수 있는(잠기지 않은) 첫 단계를 고른다. 하나도 없으면(금액 부족 등) 아무것도 선택하지 않는다
        int pick = firstUnlocked;
        if (pick >= 0) Select(pick, notifyToggle: true);
        else ClearSelection();

        SetOnClick(buyButton, () => { if (hasSelection) PlayBought(selected.TargetLevel, onBuy); });
        SetOnClick(cancelButton, () => { Close(); onCancel?.Invoke(); });
        Open();

        // 창이 열린 뒤에 기본 선택을 한 번 더 확실히 맞춘다. (닫혀 있는 동안 바꾼 토글은 선택 표시가 갱신되지 않을 수 있다)
        // 살 수 있는 첫 단계가 선택된 상태로, 없으면 아무것도 선택되지 않은 상태로 보이게 한다.
        if (pick >= 0 && optionCards[pick] != null && optionCards[pick].toggle != null)
        {
            var pickToggle = optionCards[pick].toggle;
            pickToggle.SetIsOnWithoutNotify(false);
            pickToggle.isOn = true;
            Select(pick, notifyToggle: false);
        }
        else if (pick < 0)
        {
            ClearSelection();
        }
    }

    // 모든 카드의 선택을 끈다. 이용료·비용은 "-"로 두고 구매 버튼도 끈다.
    private void ClearSelection()
    {
        hasSelection = false;
        foreach (var refs in optionCards)
        {
            var toggle = refs != null ? refs.toggle : null;
            if (toggle == null) continue;
            // 토글 그룹이 "하나는 꼭 켜져 있어야" 하는 설정이면 끌 수 없으므로 잠깐 풀었다가 되돌린다
            var group = toggle.group;
            bool allowSwitchOff = group != null && group.allowSwitchOff;
            if (group != null) group.allowSwitchOff = true;
            toggle.isOn = false; // 선택 표시(배경·테두리)는 영구 리스너가 끈다
            if (group != null) group.allowSwitchOff = allowSwitchOff;
        }
        // 선택이 없어도 별 단계 표시에는 이미 보유한 단계까지 채워 보여준다 (예: 호텔이면 별 3개)
        BuildingLevel owned = BuildingLevel.Land;
        foreach (var o in options)
            if (o.HasValue && o.Value.Owned && o.Value.TargetLevel > owned) owned = o.Value.TargetLevel;
        stageTrack.Set(owned, false);

        // 선택은 없어도, 살 수 있었을 카드(금액 부족)의 이용료와 비용은 보여준다. 없으면 "-"
        Option? info = null;
        foreach (var o in options)
            if (o.HasValue && o.Value.ShowInfoWhenNone) { info = o; break; }
        if (tollText != null) tollText.text = info.HasValue ? UIPalette.Money(info.Value.Toll) : "-";
        // 비용이 없는 카드(이미 최고 단계 등)는 "-"
        if (costText != null) costText.text = info.HasValue && info.Value.Cost > 0 ? UIPalette.Money(info.Value.Cost) : "-";
        if (buyButton != null) buyButton.interactable = false;
        UpdateCashColor(info.HasValue ? info.Value.Cost : 0);
    }

    private void SetupCard(int index)
    {
        var refs = optionCards[index];
        if (refs == null || refs.root == null) return;
        var option = options[index];
        refs.root.gameObject.SetActive(option.HasValue);
        if (!option.HasValue || refs.toggle == null) return;

        var o = option.Value;
        // 비용이 없는 카드(이미 보유 중인 단계)는 "-"
        if (refs.costText != null) refs.costText.text = o.Cost > 0 ? UIPalette.Money(o.Cost) : "-";
        SetActive(refs.lockOverlay, o.Locked);
        if (refs.lockText != null) refs.lockText.text = Loc.T(string.IsNullOrEmpty(o.LockReason) ? "선택 불가" : o.LockReason);
        refs.toggle.interactable = !o.Locked;

        refs.toggle.onValueChanged.RemoveAllListeners(); // 선택 표시용 영구 리스너는 유지된다
        refs.toggle.onValueChanged.AddListener(isOn => { if (isOn) Select(index, notifyToggle: false); });
    }

    private void Select(int index, bool notifyToggle)
    {
        var option = options[index];
        if (!option.HasValue) return;
        selected = option.Value;
        hasSelection = true;
        // 토글을 켜면 선택 표시(배경·테두리)가 영구 리스너로 따라 켜진다
        var toggle = optionCards[index] != null ? optionCards[index].toggle : null;
        if (notifyToggle && toggle != null) toggle.isOn = true;

        bool isStar = selected.TargetLevel != BuildingLevel.Land;
        int target = (int)selected.TargetLevel;
        // 별이면 목표 바로 아래 단계까지 채우고 목표 별을 강조
        stageTrack.Set(isStar ? (BuildingLevel)(target - 1) : BuildingLevel.Land, isStar);

        if (tollText != null) tollText.text = UIPalette.Money(selected.Toll);
        if (costText != null) costText.text = UIPalette.Money(selected.Cost);
        if (buyButton != null) buyButton.interactable = !selected.Locked && cash >= selected.Cost;
        UpdateCashColor(selected.Cost);
    }

    // 보유 현금 글자색: 필요한 비용을 낼 수 있으면 초록, 모자라면 빨강
    private void UpdateCashColor(long requiredCost)
    {
        if (cashText != null) cashText.color = cash >= requiredCost ? UIPalette.GainText : UIPalette.Red;
    }

    /// <summary>구매 확정: 새 별(건물만이면 "건물" 칩)이 튀어오르며 반짝인 뒤 창을 닫는다.</summary>
    private void PlayBought(BuildingLevel targetLevel, Action<BuildingLevel> onBuy)
    {
        if (buyButton != null) buyButton.interactable = false;
        if (cancelButton != null) cancelButton.interactable = false;

        var target = stageTrack.GetStageTransform(targetLevel);
        var glow = stageTrack.NewStarGlow;
        boughtSequence?.Kill();
        boughtSequence = DOTween.Sequence().SetUpdate(true).SetLink(gameObject);

        if (target != null)
        {
            target.localScale = Vector3.one;
            target.localRotation = Quaternion.identity;
            boughtSequence.Append(target.DOPunchScale(Vector3.one * 0.7f, 0.45f, 6, 0.5f));
            // 별만 한 바퀴 돈다 (건물 칩은 튀기만)
            if (targetLevel != BuildingLevel.Land)
                boughtSequence.Join(target.DOLocalRotate(new Vector3(0f, 0f, -360f), 0.45f, RotateMode.FastBeyond360).SetEase(Ease.OutCubic));
        }
        if (glow != null && glow.gameObject.activeInHierarchy)
        {
            glow.localScale = Vector3.one;
            boughtSequence.Join(glow.DOScale(1.8f, 0.2f).SetEase(Ease.OutQuad).SetLoops(2, LoopType.Yoyo));
        }

        boughtSequence.AppendInterval(0.15f);
        boughtSequence.AppendCallback(() =>
        {
            if (cancelButton != null) cancelButton.interactable = true;
            Close();
            onBuy?.Invoke(targetLevel);
        });
    }
}