using System.Collections;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 모서리 플레이어 정보 카드 1장. PlayerInfo_N에 붙는다.
/// 이름·등수·자산 표시, 차례 강조, 파산 표시, 돈 변화 연출을 담당한다.
/// </summary>
public class PlayerInfoView : UIView
{
    [SerializeField] private TMP_Text nameText;
    [SerializeField] private Image portraitImage;
    [SerializeField] private Image rankBadge;
    [SerializeField] private TMP_Text rankText;
    [SerializeField] private TMP_Text totalAssetText;
    [SerializeField] private TMP_Text cashText;
    [SerializeField] private GameObject turnHighlight;
    [SerializeField] private GameObject bankruptOverlay;

    [Header("돈 변화")]
    [SerializeField] private CanvasGroup moneyChange;
    [SerializeField] private Image moneyChangeBackground;
    [SerializeField] private Image moneyChangeStroke;
    [SerializeField] private TMP_Text moneyChangeReason;
    [SerializeField] private TMP_Text moneyChangeAmount;
    [SerializeField] private float moneyChangeDuration = 1.4f;
    [SerializeField] private float moneyChangeDistance = 24f;

    private Coroutine moneyChangeRoutine;
    private Vector2 moneyChangeOrigin;

    public override void Bind()
    {
        nameText = Find<TMP_Text>("Card/Info/NameText");
        portraitImage = Find<Image>("Card/Portrait/PortraitMask/PortraitImage");
        rankBadge = Find<Image>("Card/RankBadge");
        rankText = Find<TMP_Text>("Card/RankBadge/RankText");
        totalAssetText = Find<TMP_Text>("Card/Info/TotalAssetRow/TotalAssetText");
        cashText = Find<TMP_Text>("Card/Info/CashRow/CashText");
        turnHighlight = FindObject("TurnHighlight");
        bankruptOverlay = FindObject("Card/BankruptOverlay");

        // MoneyChange_N (N은 카드 번호)
        foreach (Transform child in transform)
        {
            if (!child.name.StartsWith("MoneyChange")) continue;
            moneyChange = child.GetComponent<CanvasGroup>();
            moneyChangeBackground = child.Find("Background")?.GetComponent<Image>();
            moneyChangeStroke = child.Find("Stroke")?.GetComponent<Image>();
            moneyChangeReason = child.Find("ReasonText")?.GetComponent<TMP_Text>();
            moneyChangeAmount = child.Find("AmountText")?.GetComponent<TMP_Text>();
        }
        if (moneyChange == null) Debug.LogWarning($"[PlayerInfoView] {name}에 MoneyChange 없음", this);
    }

    private void Awake()
    {
        if (moneyChange != null)
        {
            moneyChangeOrigin = ((RectTransform)moneyChange.transform).anchoredPosition;
            moneyChange.gameObject.SetActive(false);
        }
    }

    // 카드가 꺼지면 돈 변화 연출이 중간 위치·투명도로 남지 않게 정리한다 (코루틴은 꺼질 때 멈춘다)
    private void OnDisable()
    {
        moneyChangeRoutine = null;
        if (moneyChange == null) return;
        ((RectTransform)moneyChange.transform).anchoredPosition = moneyChangeOrigin;
        moneyChange.alpha = 1f;
        moneyChange.gameObject.SetActive(false);
    }

    /// <summary>이름, 초상화, 플레이어 색(0~3).</summary>
    public void SetProfile(string playerName, Sprite portrait, int playerColorIndex)
    {
        if (nameText != null) nameText.text = Loc.TName(playerName);
        SetPortrait(portraitImage, portrait, UIPalette.PlayerLightColor(playerColorIndex));
    }
    
    // 초상화 시스템 구현 전, 이름만이라도 갱신하기 위해 임시 구현한 함수
    public void SetName(string playerName)
    {
        if (nameText != null) nameText.text = Loc.TName(playerName);
    }

    /// <summary>등수(1~4). 이전과 달라지면 배지가 뒤집히며 바뀐다(첫 설정은 바로 표시).</summary>
    public void SetRank(int rank, bool animate = true)
    {
        if (rank == currentRank) return;
        bool flip = animate && currentRank > 0 && rankBadge != null && rankBadge.isActiveAndEnabled;
        currentRank = rank;

        if (!flip)
        {
            // 진행 중인 뒤집기가 나중에 낡은 등수로 덮어쓰지 않게 끊고 모양을 되돌린다
            rankFlip?.Kill();
            if (rankBadge != null) rankBadge.transform.localScale = Vector3.one;
            ApplyRank(rank);
            return;
        }

        var badge = rankBadge.transform;
        rankFlip?.Kill();
        badge.localScale = Vector3.one;
        rankFlip = DOTween.Sequence().SetUpdate(true).SetLink(gameObject)
            .Append(badge.DOScaleX(0f, 0.12f).SetEase(Ease.InQuad))
            .AppendCallback(() => ApplyRank(rank))
            .Append(badge.DOScaleX(1f, 0.22f).SetEase(Ease.OutBack, 2f))
            // 세로만 살짝 튄다 (가로 뒤집기와 같은 축을 건드리지 않도록 Y만 트윈)
            .Join(badge.DOScaleY(1.15f, 0.11f).SetLoops(2, LoopType.Yoyo));
    }

    private void ApplyRank(int rank)
    {
        if (rankText != null) rankText.text = Loc.Rank(rank, 60);
        if (rankBadge != null) rankBadge.color = UIPalette.Rank[Mathf.Clamp(rank - 1, 0, UIPalette.Rank.Length - 1)];
    }

    private int currentRank;
    private Sequence rankFlip;

    /// <summary>화면에 맞춰 둔(굴러가는 중이면 굴러서 도착할) 현금. 동기화 때 서버 값과 비교한다.</summary>
    public long CashTarget { get; private set; }

    /// <summary>지금 차례 강조가 켜져 있는지.</summary>
    public bool IsTurnShown => turnHighlight != null && turnHighlight.activeSelf;

    /// <summary>현금과 총 자산. animate면 이전 값에서 굴려서 바뀐다(첫 설정은 바로 표시).</summary>
    public void SetMoney(long cash, long totalAsset, bool animate = true)
    {
        CashTarget = cash;
        CashRoll.Set(cash, animate);
        TotalRoll.Set(totalAsset, animate);
    }

    // Awake 전에 불릴 수 있어 처음 쓸 때 만든다
    private RollingNumber cashRoll;
    private RollingNumber totalRoll;
    private RollingNumber CashRoll => cashRoll ??= new RollingNumber(cashText, UIPalette.Money);
    private RollingNumber TotalRoll => totalRoll ??= new RollingNumber(totalAssetText, UIPalette.Money);

    /// <summary>차례 강조. animate면 서서히 켜지고 꺼진다.</summary>
    public void SetTurn(bool isMyTurn, bool animate = true)
    {
        if (turnHighlight == null) return;
        var group = turnHighlight.GetComponent<CanvasGroup>();
        if (group == null) group = turnHighlight.AddComponent<CanvasGroup>();
        group.DOKill();

        if (!animate || !isActiveAndEnabled)
        {
            group.alpha = 1f;
            turnHighlight.SetActive(isMyTurn);
            return;
        }

        if (isMyTurn)
        {
            if (!turnHighlight.activeSelf) group.alpha = 0f;
            turnHighlight.SetActive(true);
            group.DOFade(1f, 0.35f).SetUpdate(true).SetLink(gameObject);
        }
        else if (turnHighlight.activeSelf)
        {
            group.DOFade(0f, 0.25f).SetUpdate(true).SetLink(gameObject)
                .OnComplete(() => { turnHighlight.SetActive(false); group.alpha = 1f; });
        }
    }

    /// <summary>현금 옆 금화 아이콘. 코인 이동 연출의 출발·도착 지점.</summary>
    public RectTransform CashIcon
    {
        get
        {
            if (cashIcon == null && cashText != null) cashIcon = cashText.transform.parent.Find("CashIcon") as RectTransform;
            return cashIcon;
        }
    }
    private RectTransform cashIcon;

    /// <summary>파산 표시. animate면 카드가 어두워지고 도장이 "쾅" 찍히며 카드가 흔들린다.</summary>
    public void SetBankrupt(bool isBankrupt, bool animate = true)
    {
        if (bankruptOverlay == null) return;
        var group = bankruptOverlay.GetComponent<CanvasGroup>();
        if (group == null) group = bankruptOverlay.AddComponent<CanvasGroup>();
        var stamp = bankruptOverlay.transform.Find("Stamp");
        if (stamp != null && !hasStampRest)
        {
            stampRestRotation = stamp.localRotation;
            hasStampRest = true;
        }

        // 이전 연출이 중간에 끊겨도 원래 상태로 돌려 놓는다
        var root = (RectTransform)transform;
        if (!hasRootRest) { rootRestPosition = root.anchoredPosition; hasRootRest = true; }
        bankruptSequence?.Kill();
        root.anchoredPosition = rootRestPosition;
        group.alpha = 1f;
        if (stamp != null)
        {
            stamp.localScale = Vector3.one;
            stamp.localRotation = stampRestRotation;
            foreach (var g in stamp.GetComponentsInChildren<Graphic>()) g.canvasRenderer.SetAlpha(1f);
        }

        bool play = animate && isBankrupt && !bankruptOverlay.activeSelf && isActiveAndEnabled;
        bankruptOverlay.SetActive(isBankrupt);
        if (!play) return;

        group.alpha = 0f;
        bankruptSequence = DOTween.Sequence().SetUpdate(true).SetLink(gameObject)
            .Append(group.DOFade(1f, 0.3f));
        if (stamp == null) return;

        // 크고 비스듬하게 떠 있다가 빠르게 내려찍힌다
        stamp.localScale = Vector3.one * 2.4f;
        stamp.localRotation = stampRestRotation * Quaternion.Euler(0f, 0f, -14f);
        var stampImages = stamp.GetComponentsInChildren<Graphic>();
        foreach (var g in stampImages) g.canvasRenderer.SetAlpha(0f);

        bankruptSequence
            .Insert(0.15f, stamp.DOScale(1f, 0.22f).SetEase(Ease.InQuad))
            .Insert(0.15f, stamp.DOLocalRotateQuaternion(stampRestRotation, 0.22f).SetEase(Ease.InQuad))
            .InsertCallback(0.15f, () => { foreach (var g in stampImages) g.CrossFadeAlpha(1f, 0.12f, true); })
            // 찍히는 순간 카드가 흔들리고 도장이 살짝 튄다
            .Insert(0.37f, ((RectTransform)transform).DOShakeAnchorPos(0.35f, 12f, 22, 90f, false, true))
            .Insert(0.37f, stamp.DOPunchScale(Vector3.one * 0.12f, 0.25f, 5, 0.6f));
    }

    private Sequence bankruptSequence;
    private Quaternion stampRestRotation = Quaternion.identity;
    private bool hasStampRest;
    private Vector2 rootRestPosition;
    private bool hasRootRest;

    /// <summary>카드 옆에 "사유 +금액"을 띄웠다가 사라지게 한다. amount가 음수면 손실.</summary>
    public void ShowMoneyChange(string reason, long amount)
    {
        // 꺼진 카드에서는 코루틴을 시작할 수 없어서 건너뛴다
        if (moneyChange == null || !isActiveAndEnabled) return;

        bool gain = amount >= 0;
        if (moneyChangeBackground != null) moneyChangeBackground.color = gain ? UIPalette.GainBg : UIPalette.LossBg;
        if (moneyChangeStroke != null) moneyChangeStroke.color = gain ? UIPalette.GainStroke : UIPalette.LossStroke;
        if (moneyChangeReason != null)
        {
            moneyChangeReason.text = Loc.T(reason); // 사유는 한국어 문장을 키로 번역한다
            moneyChangeReason.color = gain ? UIPalette.GainText : UIPalette.LossText;
        }
        if (moneyChangeAmount != null)
        {
            moneyChangeAmount.text = UIPalette.SignedMoney(amount);
            moneyChangeAmount.color = gain ? UIPalette.GainText : UIPalette.LossText;
        }

        if (moneyChangeRoutine != null) StopCoroutine(moneyChangeRoutine);
        moneyChangeRoutine = StartCoroutine(PlayMoneyChange());
    }

    private IEnumerator PlayMoneyChange()
    {
        var rect = (RectTransform)moneyChange.transform;
        // 위쪽 카드는 아래로, 아래쪽 카드는 위로 (카드에서 멀어지는 방향)
        float direction = rect.pivot.y > 0.5f ? -1f : 1f;

        moneyChange.gameObject.SetActive(true);
        for (float t = 0f; t < moneyChangeDuration; t += Time.deltaTime)
        {
            float p = t / moneyChangeDuration;
            rect.anchoredPosition = moneyChangeOrigin + new Vector2(0f, direction * moneyChangeDistance * p);
            // 앞 60%는 그대로 보이고 이후 사라진다
            moneyChange.alpha = p < 0.6f ? 1f : 1f - (p - 0.6f) / 0.4f;
            yield return null;
        }

        rect.anchoredPosition = moneyChangeOrigin;
        moneyChange.gameObject.SetActive(false);
        moneyChangeRoutine = null;
    }
}