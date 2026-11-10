using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 선택 제한시간을 보여주는 작은 칩. 각 선택창(구매·건설, 인수, 매각, 목적지, 무인도 탈출)과 주사위 굴리기 UI 안에 하나씩 들어 있다.
/// 남은 초와, 시간이 지날수록 줄어드는 막대를 한 줄에 보여준다. 남은 시간이 5초 이하가 되면 빨갛게 바뀐다.
/// 시간을 세는 일은 UIManager가 하고, 이 칩은 받은 값을 그리기만 한다.
/// </summary>
public class TurnTimerView : MonoBehaviour
{
    private static readonly Color NormalFill = new Color32(200, 214, 255, 255);
    private static readonly Color UrgentFill = new Color32(255, 203, 211, 255);
    private const float UrgentSeconds = 5f; // 남은 시간이 이 초 이하면 빨갛게 (잠수 3초 제한은 처음부터 빨갛다)

    [SerializeField] private Image fill;
    [SerializeField] private RectTransform fillRect;
    [SerializeField] private TMP_Text secondsText;

    private int shownSeconds = -1;
    private bool urgent;

    /// <summary>하위 오브젝트 이름(Fill, SecondsText)으로 참조를 채운다.</summary>
    [ContextMenu("Bind (이름으로 다시 연결)")]
    public void Bind()
    {
        var fillTransform = transform.Find("Fill");
        fill = fillTransform != null ? fillTransform.GetComponent<Image>() : null;
        fillRect = fillTransform as RectTransform;
        var text = transform.Find("SecondsText");
        secondsText = text != null ? text.GetComponent<TMP_Text>() : null;
    }

    /// <summary>시간을 새로 세기 시작할 때 부른다. 막대가 가득 찬 상태로 나타난다.</summary>
    public void Begin(float totalSeconds)
    {
        shownSeconds = -1;
        urgent = false;
        gameObject.SetActive(true);
        SetRemaining(totalSeconds, totalSeconds);
    }

    public void Hide() => gameObject.SetActive(false);

    /// <summary>남은 시간을 그린다. 초 숫자는 올림해서 보여준다.</summary>
    public void SetRemaining(float remaining, float total)
    {
        remaining = Mathf.Clamp(remaining, 0f, total);
        float ratio = total > 0f ? remaining / total : 0f;

        if (fillRect != null) fillRect.anchorMax = new Vector2(ratio, 1f);

        bool nowUrgent = remaining <= UrgentSeconds;
        if (nowUrgent != urgent || shownSeconds < 0)
        {
            if (fill != null) fill.color = nowUrgent ? UrgentFill : NormalFill;
            if (secondsText != null) secondsText.color = nowUrgent ? UIPalette.Red : UIPalette.Ink;
            urgent = nowUrgent;
        }

        int seconds = Mathf.CeilToInt(remaining);
        if (seconds == shownSeconds) return;
        shownSeconds = seconds;
        if (secondsText != null) secondsText.text = Loc.T("{0}초", seconds);
    }
}