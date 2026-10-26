using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 타이틀 화면 매니저. 닉네임을 입력하고 시작하면 LocalPlayer에 저장하고 로비로 간다.
/// 기존 로그인 씬(UI_LoginScene)의 가입 카드(SignupCard)를 닉네임 입력 카드로 다시 쓴다. (이메일·비밀번호 칸은 숨긴다)
/// 씬의 "UI" 루트를 찾아 이름으로 연결한다.
/// </summary>
public class TitleManager : MonoBehaviour
{
    // 지금은 호스트가 나 혼자인 싱글 모드라서 내 playerId를 고정한다. Photon을 붙일 때 접속한 플레이어 번호로 바꾼다.
    private const long LocalHostPlayerId = 1;

    private const string SavedNicknameKey = "Title.SavedNickname";

    [Header("연결")]
    [SerializeField] private Transform uiRoot;

    private TMP_InputField nicknameInput;
    private Button startButton;
    private ToastView toast;

    private bool starting;

    private void Reset()
    {
        var ui = GameObject.Find("UI");
        if (ui != null) uiRoot = ui.transform;
    }

    private void Awake()
    {
        if (uiRoot == null) Reset();
        if (uiRoot == null) { Debug.LogError("[Title] 'UI' 루트를 찾을 수 없습니다.", this); enabled = false; return; }

        if (!Bind()) { enabled = false; return; }
        WireEvents();

        // 지난번에 쓴 닉네임을 채워 둔다
        nicknameInput.SetTextWithoutNotify(PlayerPrefs.GetString(SavedNicknameKey, ""));
    }

    private void Start()
    {
        nicknameInput.ActivateInputField();

        // 로비에서 돌아온 경우처럼, 이전 화면이 남긴 안내가 있으면 띄운다
        string notice = SceneFlow.ConsumeLoginNotice();
        if (!string.IsNullOrEmpty(notice)) ShowToast(notice);
    }

    private static Transform Find(Transform root, string path)
    {
        var t = root.Find(path);
        if (t == null) Debug.LogWarning($"[Title] '{root.name}/{path}' 없음");
        return t;
    }

    private bool Bind()
    {
        var canvas = Find(uiRoot, "Canvas_Login");
        if (canvas == null) return false;

        var loginCard = Find(canvas, "LoginCard");
        var verifyCard = Find(canvas, "EmailVerificationCard");
        var signupCard = Find(canvas, "SignupCard");
        if (signupCard == null) return false;

        // 닉네임 입력에는 가입 카드만 쓴다
        if (loginCard != null) loginCard.gameObject.SetActive(false);
        if (verifyCard != null) verifyCard.gameObject.SetActive(false);
        signupCard.gameObject.SetActive(true);

        // 닉네임 입력과 시작 버튼만 남기고 나머지 칸은 숨긴다
        SetActive(signupCard, "EmailInput", false);
        SetActive(signupCard, "PasswordInput", false);
        SetActive(signupCard, "BackToEmailVerificationButton", false);

        nicknameInput = Find(signupCard, "NicknameInput")?.GetComponent<TMP_InputField>();
        startButton = Find(signupCard, "CompleteSignupButton")?.GetComponent<Button>();
        if (nicknameInput == null || startButton == null) return false;

        // 가입 카드의 글자를 시작 화면에 맞게 바꾼다
        SetText(signupCard, "BrandText", "닉네임 입력");
        SetText(signupCard, "CompleteSignupButton/Label", "시작");

        var toastTransform = uiRoot.Find("Canvas_Toast");
        if (toastTransform != null) toast = toastTransform.GetComponent<ToastView>();
        return true;
    }

    private static void SetActive(Transform root, string path, bool active)
    {
        var t = root.Find(path);
        if (t != null) t.gameObject.SetActive(active);
    }

    private static void SetText(Transform root, string path, string text)
    {
        var t = root.Find(path);
        var label = t != null ? t.GetComponent<TMP_Text>() : null;
        if (label != null) label.text = text;
    }

    private void WireEvents()
    {
        nicknameInput.onValidateInput += NicknameChar;
        nicknameInput.onSubmit.AddListener(_ => TryStart()); // 엔터
        startButton.onClick.AddListener(TryStart);
    }

    // 닉네임에는 서식 문자(< >)를 넣을 수 없다. 화면의 글자는 서식 태그를 읽기 때문에, 닉네임에 <size=500> 같은 글이 있으면 방 목록과 자리 이름이 깨진다.
    private static char NicknameChar(string text, int index, char c) => c == '\t' || c == '<' || c == '>' ? '\0' : c;

    private static string CleanNickname(string nickname) => nickname.Replace("<", "").Replace(">", "").Trim();

    private void TryStart()
    {
        if (starting) return;

        string nickname = CleanNickname(nicknameInput.text); // 입력은 막아 두었지만 한 번 더 걸러 둔다
        if (nickname.Length == 0)
        {
            UIFx.Shake(nicknameInput.transform);
            ShowToast("닉네임을 입력해 주세요");
            return;
        }

        starting = true;
        PlayerPrefs.SetString(SavedNicknameKey, nickname);
        LocalPlayer.Set(LocalHostPlayerId, nickname);

        if (SceneFlow.ToLobby()) return;

        starting = false;
        LocalPlayer.Clear();
        ShowToast("로비 화면을 불러오지 못했어요");
    }

    private void ShowToast(string message)
    {
        if (toast != null) toast.Show(message);
        else Debug.Log($"[Title] {message}");
    }
}