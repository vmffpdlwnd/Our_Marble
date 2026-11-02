using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 게임 중 연결이 끊겨 자동으로 다시 들어가는 동안 게임 화면을 덮는 안내. 눌러도 게임에 닿지 않게 입력도 막는다.
/// 게임 화면에는 안내 창이 따로 없어서, 필요할 때 만들어 쓰고 씬이 바뀌면 같이 사라진다.
///
/// 글꼴은 화면에 이미 있는 글자의 것을 빌려 쓴다. (이 프로젝트의 글꼴은 쓰는 글자만 담은 고정 글꼴이라,
/// 새 문장의 글자가 없으면 네모로 보일 수 있다. 그럴 때는 영어 문장으로 대신 보여 준다)
/// </summary>
public class PhotonReconnectOverlay : MonoBehaviour
{
    private static PhotonReconnectOverlay instance;

    private TMP_FontAsset font;
    private TMP_Text label;

    /// <summary>안내를 띄운다. korean의 글자가 글꼴에 없으면 fallback을 보여 준다.</summary>
    public static void Show(string korean, string fallback)
    {
        if (instance == null) instance = Create();
        if (instance == null) return; // 빌려 쓸 글꼴이 없는 화면이다

        instance.gameObject.SetActive(true);
        instance.SetMessage(korean, fallback);
    }

    public static void SetText(string korean, string fallback)
    {
        if (instance != null) instance.SetMessage(korean, fallback);
    }

    public static void Hide()
    {
        if (instance != null) instance.gameObject.SetActive(false);
    }

    private static PhotonReconnectOverlay Create()
    {
        TMP_Text reference = FindFirstObjectByType<TMP_Text>(FindObjectsInactive.Include);
        if (reference == null || reference.font == null) return null;

        var go = new GameObject("PhotonReconnectOverlay");
        var overlay = go.AddComponent<PhotonReconnectOverlay>();
        overlay.font = reference.font;
        overlay.Build();
        return overlay;
    }

    private void Build()
    {
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32000; // 다른 어떤 화면보다 위에

        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        gameObject.AddComponent<GraphicRaycaster>();

        // 화면 전체를 어둡게 덮는다. 이 막이 입력을 받아서 아래 게임 화면은 눌리지 않는다.
        var dim = new GameObject("Dim", typeof(RectTransform), typeof(Image));
        dim.transform.SetParent(transform, false);
        Stretch((RectTransform)dim.transform);
        Image dimImage = dim.GetComponent<Image>();
        dimImage.color = new Color(0f, 0f, 0f, 0.6f);
        dimImage.raycastTarget = true;

        var text = new GameObject("Text", typeof(RectTransform));
        text.transform.SetParent(transform, false);
        Stretch((RectTransform)text.transform);
        label = text.AddComponent<TextMeshProUGUI>();
        label.font = font;
        label.fontSize = 48f;
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;
        label.raycastTarget = false;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private void SetMessage(string korean, string fallback)
    {
        if (label != null) label.text = CanShow(korean) ? korean : fallback;
    }

    // 글꼴에 이 문장의 글자가 모두 있는지
    private bool CanShow(string text)
    {
        if (font == null || font.characterLookupTable == null) return false;

        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (!font.characterLookupTable.ContainsKey(c)) return false;
        }
        return true;
    }
}