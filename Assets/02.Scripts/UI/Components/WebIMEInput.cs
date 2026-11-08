using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
using System.Text;
#endif

/// <summary>
/// 웹(WebGL) 빌드에서 입력칸에 한글을 직접 칠 수 있게 한다.
/// Unity 웹 빌드는 브라우저의 한글 입력기(IME)가 조합 중인 글자를 입력칸에 전달하지 못한다.
/// 그래서 입력칸을 누르면 그 위에 투명한 HTML 입력칸을 겹쳐 띄워 브라우저가 조합을 처리하게 하고,
/// 칠한 글자를 Unity 입력칸(TMP_InputField)에 실시간으로 넘긴다. (브라우저 쪽은 Assets/Plugins/WebGL/WebIMEInput.jslib)
///
/// 쓰는 법: 입력칸을 찾은 뒤 WebIMEInput.Attach(입력칸) 한 줄이면 된다.
/// 웹이 아닌 곳(exe, 에디터)과 모바일 브라우저에서는 아무 일도 하지 않아서, 기존 입력 방식 그대로다.
///
/// 이 클래스는 입력칸마다 붙는 컴포넌트이면서, 브라우저가 SendMessage로 부르는 받는 쪽(host)도 겸한다.
/// </summary>
public class WebIMEInput : MonoBehaviour, IPointerClickHandler
{
    /// <param name="keepOpenOnSubmit">엔터로 제출한 뒤에도 입력을 이어가게 둘지. (채팅처럼 연달아 보내는 칸)</param>
    public static void Attach(TMP_InputField field, bool keepOpenOnSubmit = false)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        if (field == null || Application.isMobilePlatform) return;
        EnsureHost();

        var component = field.GetComponent<WebIMEInput>();
        if (component == null) component = field.gameObject.AddComponent<WebIMEInput>();
        component.field = field;
        component.keepOpenOnSubmit = keepOpenOnSubmit;
#endif
    }

    // 인터페이스 함수라서 에디터와 exe에서도 있어야 컴파일된다. 웹이 아니면 아무 일도 안 한다
    public void OnPointerClick(PointerEventData eventData)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        if (isHost || field == null || !field.interactable || field.readOnly) return;
        if (active == this) return;
        Open();
#endif
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    private const string HostName = "WebIMEHost";

    [DllImport("__Internal")] private static extern void WebIME_Open(string hostName, string text, int maxLength, string styleJson);
    [DllImport("__Internal")] private static extern void WebIME_SetText(string text);
    [DllImport("__Internal")] private static extern void WebIME_Close();

    // 브라우저로 넘기는 HTML 입력칸의 위치와 크기. 화면 픽셀이고, y는 화면 위쪽에서부터다
    [System.Serializable]
    private struct OverlayStyle
    {
        public float x, y, w, h;
        public float padLeft, padRight;
        public float fontPx;
        public float screenW, screenH;
    }

    private static WebIMEInput host;     // 브라우저가 부르는 받는 쪽
    private static WebIMEInput active;   // 지금 HTML 입력칸이 열려 있는 입력칸

    private TMP_InputField field;
    private bool keepOpenOnSubmit;
    private bool isHost;

    private static void EnsureHost()
    {
        if (host != null) return;

        // 켜 두면 Unity가 페이지 전체의 키 입력을 가로채서 HTML 입력칸에 칠 수 없다
        WebGLInput.captureAllKeyboardInput = false;

        var go = new GameObject(HostName);
        DontDestroyOnLoad(go);
        host = go.AddComponent<WebIMEInput>();
        host.isHost = true;
    }

    // ───────────── 입력칸 쪽 ─────────────

    private void Open()
    {
        var rt = (RectTransform)field.transform;
        var canvas = GetComponentInParent<Canvas>();
        canvas = canvas != null ? canvas.rootCanvas : null;
        Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;

        Rect box = ScreenRect(rt, cam);
        Rect view = ScreenRect(field.textViewport != null ? field.textViewport : rt, cam); // 글자가 실제로 그려지는 영역
        float unitToPx = rt.rect.height > 0f ? box.height / rt.rect.height : 1f;
        float fontSize = field.textComponent != null ? field.textComponent.fontSize : 24f;

        var style = new OverlayStyle
        {
            x = box.xMin,
            y = Screen.height - box.yMax,
            w = box.width,
            h = box.height,
            padLeft = Mathf.Max(0f, view.xMin - box.xMin),
            padRight = Mathf.Max(0f, box.xMax - view.xMax),
            fontPx = fontSize * unitToPx,
            screenW = Screen.width,
            screenH = Screen.height,
        };

        active = this;
        WebIME_Open(HostName, field.text, field.characterLimit, JsonUtility.ToJson(style));
        field.caretPosition = field.text.Length;
    }

    private static Rect ScreenRect(RectTransform rt, Camera cam)
    {
        var corners = new Vector3[4];
        rt.GetWorldCorners(corners);
        Vector2 min = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
        Vector2 max = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    private void OnDisable()
    {
        // 입력칸이 꺼지거나 화면이 바뀌면 떠 있는 HTML 입력칸도 닫는다
        if (isHost || active != this) return;
        active = null;
        WebIME_Close();
    }

    // 브라우저에서 넘어온 글자를 Unity 입력칸 규칙(글자 수 제한, 막는 글자)에 맞춰 넣는다
    private void Apply(string raw)
    {
        string text = Sanitize(raw);
        if (field.text == text) return;
        field.text = text;
        field.caretPosition = text.Length;
    }

    private string Sanitize(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        int limit = field.characterLimit;
        bool singleLine = field.lineType == TMP_InputField.LineType.SingleLine;
        var sb = new StringBuilder(raw.Length);
        foreach (char original in raw)
        {
            char c = original;
            if (singleLine && (c == '\n' || c == '\r')) continue;
            if (field.onValidateInput != null)
            {
                c = field.onValidateInput(sb.ToString(), sb.Length, c);
                if (c == '\0') continue;
            }
            sb.Append(c);
            if (limit > 0 && sb.Length >= limit) break;
        }
        return sb.ToString();
    }

    // ───────────── 브라우저가 부르는 쪽 (jslib의 SendMessage가 이 이름으로 부른다) ─────────────

    // 글자가 바뀔 때마다 (한글 조합 중에도)
    public void OnWebIMEChanged(string text)
    {
        if (active != null) active.Apply(text);
    }

    // 엔터를 눌렀을 때
    public void OnWebIMESubmit(string text)
    {
        var target = active;
        if (target == null) return;

        target.Apply(text);
        target.field.onSubmit.Invoke(target.field.text);
        if (active != target) return; // 제출 처리 중에 이미 닫혔으면 끝

        if (target.keepOpenOnSubmit)
        {
            WebIME_SetText(target.field.text); // 채팅은 보낸 뒤 비워진 글자를 HTML 입력칸에도 반영하고 계속 입력한다
        }
        else
        {
            active = null;
            WebIME_Close();
            target.field.DeactivateInputField();
        }
    }

    // 다른 곳을 눌러 포커스를 잃었거나 Esc로 닫혔을 때 (마지막 글자를 확정한다)
    public void OnWebIMEClosed(string text)
    {
        var target = active;
        if (target == null) return;

        active = null;
        target.Apply(text);
        target.field.DeactivateInputField();
    }
#endif
}