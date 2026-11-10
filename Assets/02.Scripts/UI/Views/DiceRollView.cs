using System;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 주사위 굴리기 버튼 + 파워 게이지 + 무인도 상태. Canvas_DiceRoll에 붙는다.
/// 버튼을 누르고 있으면 게이지가 오르내리고, 떼는 순간의 파워(0~1)를 넘긴다.
/// </summary>
public class DiceRollView : UIView
{
    // 게이지 치수 (PowerGauge 안쪽 채움 영역)
    private const float FillWidth = 456f;
    private const float TipRadius = 11f;
    private const float Inset = 4f;
    private static readonly Color FillStart = new Color32(184, 198, 255, 255);
    private static readonly Color FillEnd = new Color32(111, 140, 255, 255);

    [SerializeField] private Button rollButton;
    [SerializeField] private HoldButton rollHold;
    [SerializeField] private Image fill;
    [SerializeField] private Image tip;
    [SerializeField] private RectTransform glow;
    [SerializeField] private GameObject islandStatus;
    [SerializeField] private TMP_Text islandTurnText;

    [Tooltip("게이지가 0→1까지 차는 데 걸리는 시간(초)")]
    [SerializeField] private float gaugeCycleSeconds = 1.2f;

    private Action<float> onRoll;
    private float holdTime;

    public float Power { get; private set; }

    public override void Bind()
    {
        rollButton = Find<Button>("DiceRollPanel/RollButton");
        rollHold = Find<HoldButton>("DiceRollPanel/RollButton");
        fill = Find<Image>("DiceRollPanel/PowerGauge/Fill");
        tip = Find<Image>("DiceRollPanel/PowerGauge/Tip");
        glow = Find<RectTransform>("DiceRollPanel/PowerGauge/Glow");
        islandStatus = FindObject("DiceRollPanel/IslandStatus");
        islandTurnText = Find<TMP_Text>("DiceRollPanel/IslandStatus/TurnText");
    }

    private void OnEnable()
    {
        if (rollHold == null) return;
        rollHold.PointerDown += HandlePointerDown;
        rollHold.PointerUp += HandlePointerUp;
    }

    private void OnDisable()
    {
        if (rollHold == null) return;
        rollHold.PointerDown -= HandlePointerDown;
        rollHold.PointerUp -= HandlePointerUp;
    }

    private void Update()
    {
        if (rollHold == null || !rollHold.IsHolding) return;
        holdTime += Time.deltaTime;
        SetGauge(Mathf.PingPong(holdTime / gaugeCycleSeconds, 1f));
        ShakeGauge();
    }

    // ───────────── 게이지 떨림 / 번쩍임 ─────────────

    private const float ShakeFrom = 0.8f;     // 이 값부터 떨리기 시작
    private const float ShakeMaxPixels = 3.5f;
    private RectTransform gaugeRect;
    private Vector2 gaugeRest;
    private bool hasGaugeRest;
    private Sequence releaseFlash;

    private RectTransform GaugeRect
    {
        get
        {
            if (gaugeRect == null && fill != null) gaugeRect = fill.transform.parent as RectTransform;
            if (gaugeRect != null && !hasGaugeRest) { gaugeRest = gaugeRect.anchoredPosition; hasGaugeRest = true; }
            return gaugeRect;
        }
    }

    /// <summary>게이지가 끝에 가까울수록 세게 떨린다.</summary>
    private void ShakeGauge()
    {
        var gauge = GaugeRect;
        if (gauge == null) return;
        float strength = Mathf.InverseLerp(ShakeFrom, 1f, Power);
        gauge.anchoredPosition = gaugeRest + UnityEngine.Random.insideUnitCircle * (ShakeMaxPixels * strength * strength);
    }

    /// <summary>떼는 순간 게이지 끝이 번쩍인다.</summary>
    private void FlashRelease()
    {
        var gauge = GaugeRect;
        if (gauge != null) gauge.anchoredPosition = gaugeRest;

        releaseFlash?.Kill(complete: true);
        releaseFlash = DOTween.Sequence().SetUpdate(true).SetLink(gameObject);
        if (glow != null)
        {
            glow.localScale = Vector3.one;
            releaseFlash.Join(glow.DOScale(2.2f, 0.14f).SetEase(Ease.OutQuad).SetLoops(2, LoopType.Yoyo));
        }
        if (tip != null)
        {
            tip.transform.localScale = Vector3.one;
            releaseFlash.Join(tip.transform.DOPunchScale(Vector3.one * 0.6f, 0.3f, 5, 0.6f));
        }
    }

    /// <summary>
    /// 주사위 굴리기 UI를 연다. 버튼을 떼면 onRoll(파워)가 호출된다.
    /// islandTurnsRemaining: 이 화면의 플레이어가 무인도에 갇혀 있을 때만 남은 턴을 넘긴다(그 외 0).
    /// 무인도 표시는 이 값이 있을 때만 보인다. 주사위 결과가 나오면 ResolveIslandRoll(더블 여부)을 불러
    /// 남은 턴을 줄이거나(더블 아님) "무인도 탈출!"로 바꿔 사라지게(더블) 한다.
    /// </summary>
    public void Show(Action<float> onRollReleased, int islandTurnsRemaining = 0)
    {
        onRoll = onRollReleased;
        SetIslandTurns(islandTurnsRemaining, animate: false);
        SetGauge(0f);
        SetInteractable(true);
        Open();
    }

    public void SetInteractable(bool interactable)
    {
        if (rollButton != null) rollButton.interactable = interactable;
    }

    /// <summary>굴리기 버튼을 지금 누를 수 있는지</summary>
    public bool CanRoll => rollButton != null && rollButton.IsInteractable();

    /// <summary>
    /// 굴리기를 더 이상 못 하게 막는다. (제한 시간이 끝났을 때)
    /// 버튼을 누르고 있던 중이어도, 손을 뗐을 때 주사위가 굴러가지 않는다. 다시 Show하면 풀린다.
    /// </summary>
    public void LockRoll()
    {
        SetInteractable(false);
        onRoll = null;
    }

    /// <summary>화면에 표시 중인 무인도 남은 턴. 0이면 표시가 꺼져 있다.</summary>
    public int IslandTurnsRemaining { get; private set; }

    private Sequence islandSequence;

    /// <summary>
    /// 무인도 남은 턴. 0이면 표시를 끈다.
    /// animate면 숫자가 뒤집히며 바뀌고, 0이 되면 서서히 사라진다.
    /// </summary>
    public void SetIslandTurns(int turnsRemaining, bool animate = false)
    {
        turnsRemaining = Mathf.Max(0, turnsRemaining);
        IslandTurnsRemaining = turnsRemaining;
        if (islandStatus == null) return;

        var group = islandStatus.GetComponent<CanvasGroup>();
        if (group == null) group = islandStatus.AddComponent<CanvasGroup>();
        var number = islandTurnText != null ? islandTurnText.transform : null;

        islandSequence?.Kill();
        RestoreIslandLook();
        group.alpha = 1f;
        if (number != null) number.localScale = Vector3.one;

        bool play = animate && islandStatus.activeInHierarchy;
        if (!play)
        {
            islandStatus.SetActive(turnsRemaining > 0);
            if (islandTurnText != null) islandTurnText.text = turnsRemaining.ToString();
            return;
        }

        islandSequence = DOTween.Sequence().SetUpdate(true).SetLink(gameObject);
        if (number != null)
        {
            // 숫자가 세로로 접혔다가 새 값으로 펼쳐진다
            islandSequence.Append(number.DOScaleY(0f, 0.12f).SetEase(Ease.InQuad));
            islandSequence.AppendCallback(() => { if (islandTurnText != null) islandTurnText.text = turnsRemaining.ToString(); });
            islandSequence.Append(number.DOScaleY(1f, 0.2f).SetEase(Ease.OutBack, 2f));
        }
        if (turnsRemaining == 0)
        {
            // 탈출: 잠깐 보여준 뒤 사라진다
            islandSequence.AppendInterval(0.35f);
            islandSequence.Append(group.DOFade(0f, 0.3f));
            islandSequence.AppendCallback(() => { islandStatus.SetActive(false); group.alpha = 1f; });
        }
    }

    /// <summary>게이지 값(0~1). 끝부분(Tip)이 둥근 끝을 만들도록 채움을 원 중심까지만 그린다.</summary>
    public void SetGauge(float value)
    {
        Power = Mathf.Clamp01(value);
        float center = Mathf.Max(TipRadius, FillWidth * Power - TipRadius);
        bool visible = Power > 0f;

        if (fill != null) fill.fillAmount = visible ? center / FillWidth : 0f;
        if (tip != null)
        {
            tip.gameObject.SetActive(visible);
            tip.rectTransform.anchoredPosition = new Vector2(Inset + center, 0f);
            tip.color = Color.Lerp(FillStart, FillEnd, center / FillWidth);
        }
        if (glow != null)
        {
            glow.gameObject.SetActive(visible);
            glow.anchoredPosition = new Vector2(Inset + center, 0f);
        }
    }

    // ───────────── 무인도 결과 ─────────────

    private static readonly Color EscapeBg = new Color32(227, 246, 236, 255);
    private static readonly Color EscapeStroke = new Color32(180, 228, 203, 255);
    private static readonly Color EscapeText = new Color32(47, 158, 106, 255);

    private Image islandBg, islandStroke, islandIcon;
    private TMP_Text islandStatusText;
    private GameObject[] islandTurnParts;
    private Color bgRest, strokeRest, iconRest, statusRest;
    private string statusRestText;
    private bool islandLookCached;

    /// <summary>
    /// 갇힌 플레이어가 굴린 주사위 결과를 반영한다. 갇혀 있지 않으면 아무것도 하지 않는다.
    /// 더블이면 "무인도 탈출!"로 바뀐 뒤 사라지고, 아니면 남은 턴이 1 줄어든다(0이 되면 사라진다).
    /// </summary>
    public void ResolveIslandRoll(bool isDouble)
    {
        if (IslandTurnsRemaining <= 0) return;
        if (isDouble) EscapeIsland();
        else SetIslandTurns(IslandTurnsRemaining - 1, animate: true);
    }

    /// <summary>"무인도 탈출!"로 바꿔 톡 튀게 한 뒤 서서히 사라진다.</summary>
    public void EscapeIsland()
    {
        IslandTurnsRemaining = 0;
        if (islandStatus == null || !islandStatus.activeInHierarchy)
        {
            SetActive(islandStatus, false);
            return;
        }
        CacheIslandLook();
        var group = islandStatus.GetComponent<CanvasGroup>();
        if (group == null) group = islandStatus.AddComponent<CanvasGroup>();
        var pill = islandStatus.transform;

        islandSequence?.Kill();
        group.alpha = 1f;
        pill.localScale = Vector3.one;

        islandSequence = DOTween.Sequence().SetUpdate(true).SetLink(gameObject)
            // 살짝 접혔다가 탈출 문구로 펼쳐진다
            .Append(pill.DOScaleY(0f, 0.1f).SetEase(Ease.InQuad))
            .AppendCallback(() =>
            {
                foreach (var part in islandTurnParts) SetActive(part, false);
                if (islandStatusText != null) { islandStatusText.text = Loc.T("무인도 탈출!"); islandStatusText.color = EscapeText; }
                if (islandBg != null) islandBg.color = EscapeBg;
                if (islandStroke != null) islandStroke.color = EscapeStroke;
                if (islandIcon != null) islandIcon.color = EscapeText;
            })
            .Append(pill.DOScaleY(1f, 0.22f).SetEase(Ease.OutBack, 2f))
            .Append(pill.DOPunchScale(Vector3.one * 0.15f, 0.35f, 5, 0.6f))
            .AppendInterval(0.7f)
            .Append(group.DOFade(0f, 0.35f))
            .AppendCallback(() =>
            {
                islandStatus.SetActive(false);
                group.alpha = 1f;
                pill.localScale = Vector3.one;
                RestoreIslandLook();
            });
    }

    // 탈출 표시로 바꾸기 전의 모습을 기억해 두었다가 다음에 갇힐 때 되돌린다
    private void CacheIslandLook()
    {
        if (islandLookCached || islandStatus == null) return;
        var t = islandStatus.transform;
        islandBg = islandStatus.GetComponent<Image>();
        islandStroke = t.Find("Stroke") != null ? t.Find("Stroke").GetComponent<Image>() : null;
        islandIcon = t.Find("Icon") != null ? t.Find("Icon").GetComponent<Image>() : null;
        islandStatusText = t.Find("StatusText") != null ? t.Find("StatusText").GetComponent<TMP_Text>() : null;
        var parts = new System.Collections.Generic.List<GameObject>();
        foreach (var n in new[] { "Divider", "TurnLabel", "TurnText" })
            if (t.Find(n) != null) parts.Add(t.Find(n).gameObject);
        islandTurnParts = parts.ToArray();

        if (islandBg != null) bgRest = islandBg.color;
        if (islandStroke != null) strokeRest = islandStroke.color;
        if (islandIcon != null) iconRest = islandIcon.color;
        if (islandStatusText != null) { statusRest = islandStatusText.color; statusRestText = islandStatusText.text; }
        islandLookCached = true;
    }

    private void RestoreIslandLook()
    {
        if (!islandLookCached) return;
        foreach (var part in islandTurnParts) SetActive(part, true);
        if (islandBg != null) islandBg.color = bgRest;
        if (islandStroke != null) islandStroke.color = strokeRest;
        if (islandIcon != null) islandIcon.color = iconRest;
        if (islandStatusText != null) { islandStatusText.color = statusRest; islandStatusText.text = statusRestText; }
        if (islandStatus != null) islandStatus.transform.localScale = Vector3.one;
    }

    private void HandlePointerDown()
    {
        holdTime = 0f;
        SetGauge(0f);
    }

    private void HandlePointerUp()
    {
        SetInteractable(false);
        FlashRelease();
        onRoll?.Invoke(Power);
    }
}