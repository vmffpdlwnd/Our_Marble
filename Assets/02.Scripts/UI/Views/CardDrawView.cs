using System;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>황금열쇠 카드 뽑기 팝업. 뒷면에서 앞면으로 뒤집으며 연다. Canvas_CardDraw에 붙는다.</summary>
public class CardDrawView : UIView
{
    public override string PanelPath => "CardDrawPopup/Card";
    public override string DimPath => "CardDrawPopup/Dim";

    private static readonly Color KeepTagBg = new Color32(207, 242, 224, 255);
    private static readonly Color KeepTagText = new Color32(63, 168, 119, 255);
    private static readonly Color InstantTagBg = new Color32(255, 211, 216, 255);
    private static readonly Color InstantTagText = new Color32(214, 90, 110, 255);

    [SerializeField] private RectTransform card;
    [SerializeField] private GameObject front;
    [SerializeField] private GameObject back;
    [SerializeField] private Image cardImage;
    [SerializeField] private GameObject imagePlaceholder; // "CARD IMAGE" 자리표시 글자. 카드 그림이 있으면 숨긴다
    [SerializeField] private Image typeTag;
    [SerializeField] private TMP_Text typeTagText;
    [SerializeField] private TMP_Text cardNameText;
    [SerializeField] private TMP_Text descriptionText;
    [SerializeField] private Button confirmButton;
    [SerializeField] private TMP_Text confirmLabel;
    [SerializeField] private float flipSeconds = 0.5f;

    public override void Bind()
    {
        const string c = "CardDrawPopup/Card/";
        card = Find<RectTransform>("CardDrawPopup/Card");
        front = FindObject(c + "Front");
        back = FindObject(c + "Back");
        cardImage = Find<Image>(c + "Front/CardArt/CardImage");
        imagePlaceholder = FindObject(c + "Front/CardArt/PlaceholderText");
        typeTag = Find<Image>(c + "Front/TypeTag");
        typeTagText = Find<TMP_Text>(c + "Front/TypeTag/Text");
        cardNameText = Find<TMP_Text>(c + "Front/CardNameText");
        descriptionText = Find<TMP_Text>(c + "Front/DescriptionText");
        confirmButton = Find<Button>(c + "Front/ConfirmButton");
        confirmLabel = Find<TMP_Text>(c + "Front/ConfirmButton/Label");
    }

    /// <summary>
    /// keepable: 보관 카드면 "보관하기", 즉시 발동이면 "확인".
    /// autoCloseSeconds > 0: 확인 버튼 없이, 카드가 뒤집힌 뒤 그 시간만큼 보여주고 스스로 닫힌 다음 onConfirm을 호출한다. (다른 플레이어가 뽑은 카드)
    /// </summary>
    public void Show(string cardName, string description, Sprite image, bool keepable, Action onConfirm, float autoCloseSeconds = 0f)
    {
        autoCloseTween?.Kill();
        bool autoClose = autoCloseSeconds > 0f;

        if (cardNameText != null) cardNameText.text = cardName;
        if (descriptionText != null) descriptionText.text = description;
        if (cardImage != null)
        {
            if (!placeholderColorCached) { placeholderColor = cardImage.color; placeholderColorCached = true; }
            cardImage.sprite = image;
            cardImage.color = image != null ? Color.white : placeholderColor; // 그림이 없으면 이전 카드 그림이 남지 않게 자리표시로 되돌린다
        }
        SetActive(imagePlaceholder, image == null);

        if (typeTag != null) typeTag.color = keepable ? KeepTagBg : InstantTagBg;
        if (typeTagText != null)
        {
            typeTagText.text = Loc.T(keepable ? "보관 가능" : "즉시 발동");
            typeTagText.color = keepable ? KeepTagText : InstantTagText;
        }
        if (confirmLabel != null) confirmLabel.text = Loc.T(keepable ? "보관하기" : "확인");
        if (confirmButton != null) confirmButton.gameObject.SetActive(!autoClose); // 자동으로 닫히는 카드는 확인 버튼을 숨긴다
        SetOnClick(confirmButton, () => { Close(); onConfirm?.Invoke(); });

        Open();
        PlayReveal(autoClose ? () => ScheduleAutoClose(autoCloseSeconds, onConfirm) : (Action)null);
    }

    private Tween autoCloseTween;
    private Color placeholderColor;
    private bool placeholderColorCached;

    /// <summary>seconds 뒤에 창을 닫고 onClosed를 호출한다.</summary>
    private void ScheduleAutoClose(float seconds, Action onClosed)
    {
        autoCloseTween?.Kill();
        autoCloseTween = DOVirtual.DelayedCall(seconds, () =>
        {
            autoCloseTween = null;
            Close();
            onClosed?.Invoke();
        }).SetUpdate(true).SetLink(gameObject);
    }

    private const float RiseDistance = 320f;
    private Sequence reveal;
    private Vector2 cardRestPosition;
    private bool hasCardRest;
    private float glowRestAlpha = -1f;

    /// <summary>카드가 아래에서 솟아오른 뒤 뒤집히고, 앞면이 나올 때 금빛이 번쩍인다. 연출이 끝나면 onRevealed를 호출한다.</summary>
    private void PlayReveal(Action onRevealed = null)
    {
        if (card == null) { onRevealed?.Invoke(); return; }
        if (!hasCardRest) { cardRestPosition = card.anchoredPosition; hasCardRest = true; }

        var glow = card.Find("Glow") != null ? card.Find("Glow").GetComponent<Image>() : null;
        if (glow != null && glowRestAlpha < 0f) glowRestAlpha = glow.color.a;

        reveal?.Kill();
        card.localRotation = Quaternion.identity;
        card.anchoredPosition = cardRestPosition + new Vector2(0f, -RiseDistance);
        if (glow != null) { glow.transform.localScale = Vector3.one; SetAlpha(glow, glowRestAlpha); }
        SetActive(back, true);
        SetActive(front, false);
        if (confirmButton != null) confirmButton.interactable = false;

        float half = flipSeconds * 0.5f;
        reveal = DOTween.Sequence().SetUpdate(true).SetLink(gameObject)
            // 1) 아래에서 솟아오른다
            .Append(card.DOAnchorPos(cardRestPosition, 0.45f).SetEase(Ease.OutBack, 1.2f))
            .AppendInterval(0.12f)
            // 2) 반쯤 돌았을 때 앞면으로 바꾼다
            .Append(card.DOLocalRotate(new Vector3(0f, 90f, 0f), half * 0.8f).SetEase(Ease.InQuad))
            .AppendCallback(() =>
            {
                SetActive(back, false);
                SetActive(front, true);
            })
            .Append(card.DOLocalRotate(Vector3.zero, half * 1.2f).SetEase(Ease.OutBack, 1.6f));

        // 3) 앞면이 나오는 순간 금빛이 번쩍인다
        if (glow != null)
        {
            reveal.Join(glow.DOFade(1f, 0.15f).SetLoops(2, LoopType.Yoyo));
            reveal.Join(glow.transform.DOScale(1.3f, 0.2f).SetEase(Ease.OutQuad).SetLoops(2, LoopType.Yoyo));
        }

        reveal.AppendCallback(() =>
        {
            if (glow != null) SetAlpha(glow, glowRestAlpha);
            if (confirmButton != null) confirmButton.interactable = true;
            onRevealed?.Invoke();
        });
    }

    private static void SetAlpha(Graphic graphic, float alpha)
    {
        var c = graphic.color;
        c.a = alpha;
        graphic.color = c;
    }
}