using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 화면 아래에 잠깐 떴다 사라지는 짧은 알림 메시지. (앱 개발에서 이런 알림을 "토스트"라고 부른다) Canvas_Toast에 붙는다. 로비와 로그인 화면이 같은 프리팹을 쓴다.
/// 꺼져 있던 캔버스에서도 바로 부를 수 있게 필요할 때 이름으로 연결한다.
/// </summary>
public class ToastView : MonoBehaviour
{
    private const float FadeIn = 0.18f;
    private const float Hold = 2f;
    private const float FadeOut = 0.3f;

    private CanvasGroup group;
    private TMP_Text text;
    private Tween tween;

    private void EnsureBound()
    {
        if (group != null) return;
        var toast = transform.Find("Toast");
        group = UIFx.EnsureGroup(toast.gameObject);
        text = toast.Find("Text").GetComponent<TMP_Text>();
    }

    public void Show(string message)
    {
        EnsureBound();
        text.text = Loc.T(message); // 영어면 번역해서 보여 준다 (번역이 없으면 그대로)
        gameObject.SetActive(true);
        LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)group.transform);

        tween?.Kill();
        group.alpha = 0f;
        tween = DOTween.Sequence().SetUpdate(true).SetLink(gameObject)
            .Append(group.DOFade(1f, FadeIn))
            .AppendInterval(Hold)
            .Append(group.DOFade(0f, FadeOut))
            .OnComplete(() => gameObject.SetActive(false));
    }
}