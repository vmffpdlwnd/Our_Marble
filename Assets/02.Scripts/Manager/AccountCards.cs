using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 타이틀 화면의 이메일 계정 카드를 움직이는 쪽. TitleManager가 만든다.
///
/// 씬에는 이 이름의 오브젝트가 있어야 해요. 없으면 그 부분만 조용히 건너뛰고 닉네임 시작은 그대로 동작한다.
///   Canvas_Login/SignupCard/EmailLoginButton   타이틀 카드의 "이메일로 로그인"(로그인한 뒤에는 "로그아웃") 버튼
///   Canvas_Login/LoginCard                     이메일 로그인 카드 (옛 로그인 씬의 것을 복사해 온다)
///     EmailInput, PasswordInput, SaveIdToggle, ForgotButton, LoginButton, SignupButton, BackButton
///
/// 이 PC의 사용자는 처음에 익명 계정이다. "회원가입"은 그 계정에 이메일과 비밀번호를 붙이는 것이고(닉네임과 전적이 이어진다),
/// "로그인"은 이미 가입한 계정으로 바꾸는 것이다.
/// </summary>
public class AccountCards : MonoBehaviour
{
    private const string SavedEmailKey = "Login.SavedEmail";
    private const string SaveEmailKey = "Login.SaveEmail";
    private const string SavedNicknameKey = "Title.SavedNickname"; // TitleManager와 같은 키

    private Transform titleCard;
    private Transform loginCard;
    private Button emailLoginButton;
    private TMP_Text emailLoginLabel;

    private TMP_InputField emailInput, passwordInput;
    private Toggle saveEmailToggle;
    private Button forgotButton, loginButton, signupButton, backButton;

    private TMP_InputField titleNicknameInput;
    private System.Action<string> showToast;
    private PlayFabIdentity identity;

    private bool busy;
    private bool loginConfirmed; // 익명 계정에서 로그인하면 사라진다는 안내를 한 번 보여 줬는지

    /// <summary>카드를 연결한다. 하나도 못 찾으면 false (이 기능은 꺼진다)</summary>
    public bool Init(Transform canvas, Transform titleCardTransform, TMP_InputField nicknameInput, System.Action<string> toastAction)
    {
        titleCard = titleCardTransform;
        titleNicknameInput = nicknameInput;
        showToast = toastAction ?? (_ => { });
        identity = PhotonConnection.Instance.Identity;

        loginCard = canvas.Find("LoginCard");
        Transform entry = titleCard.Find("EmailLoginButton");
        if (loginCard == null || entry == null)
        {
            Debug.Log("[Account] 이메일 로그인 카드가 씬에 없어서 건너뜁니다 (LoginCard, SignupCard/EmailLoginButton)");
            return false;
        }

        emailLoginButton = entry.GetComponent<Button>();
        emailLoginLabel = entry.Find("Label") != null ? entry.Find("Label").GetComponent<TMP_Text>() : entry.GetComponentInChildren<TMP_Text>();

        emailInput = GetComponentAt<TMP_InputField>(loginCard, "EmailInput");
        passwordInput = GetComponentAt<TMP_InputField>(loginCard, "PasswordInput");
        saveEmailToggle = GetComponentAt<Toggle>(loginCard, "SaveIdToggle");
        forgotButton = GetComponentAt<Button>(loginCard, "ForgotButton");
        loginButton = GetComponentAt<Button>(loginCard, "LoginButton");
        signupButton = GetComponentAt<Button>(loginCard, "SignupButton");
        backButton = GetComponentAt<Button>(loginCard, "BackButton");
        if (emailInput == null || passwordInput == null || loginButton == null || signupButton == null || emailLoginButton == null)
        {
            Debug.LogWarning("[Account] 로그인 카드의 입력칸이나 버튼이 모자라서 이메일 로그인을 건너뜁니다");
            return false;
        }

        loginCard.gameObject.SetActive(false);

        emailLoginButton.onClick.AddListener(OnEmailLoginButton);
        loginButton.onClick.AddListener(() => _ = LoginAsync());
        signupButton.onClick.AddListener(() => _ = SignupAsync());
        if (forgotButton != null) forgotButton.onClick.AddListener(() => _ = ForgotAsync());
        if (backButton != null) backButton.onClick.AddListener(CloseLoginCard);

        emailInput.onSubmit.AddListener(_ => passwordInput.ActivateInputField()); // 엔터: 비밀번호 칸으로
        passwordInput.onSubmit.AddListener(text => { _ = LoginAsync(); });            // 엔터: 로그인

        if (saveEmailToggle != null) saveEmailToggle.SetIsOnWithoutNotify(PlayerPrefs.GetInt(SaveEmailKey, 1) == 1);

        identity.Changed += RefreshEntryButton;
        RefreshEntryButton();

        // 이 기기의 계정이 이메일 계정인지 알아 둔다. (버튼 글자를 "로그아웃"으로 바꾸려고) 못 받아도 게임에는 영향이 없다.
        _ = identity.RefreshAccountInfoAsync();
        return true;
    }

    private void OnDestroy()
    {
        if (identity != null) identity.Changed -= RefreshEntryButton;
    }

    private void Update()
    {
        if (loginCard != null && loginCard.gameObject.activeSelf && Input.GetKeyDown(KeyCode.Escape)) CloseLoginCard();
    }

    private static T GetComponentAt<T>(Transform root, string path) where T : Component
    {
        Transform t = root.Find(path);
        return t != null ? t.GetComponent<T>() : null;
    }

    // ───────────── 카드 열고 닫기 ─────────────

    private void OnEmailLoginButton()
    {
        if (busy) return;

        if (identity.HasEmail)
        {
            Logout();
            return;
        }
        OpenLoginCard();
    }

    private void OpenLoginCard()
    {
        loginConfirmed = false;
        passwordInput.SetTextWithoutNotify("");
        if (saveEmailToggle != null && saveEmailToggle.isOn) emailInput.SetTextWithoutNotify(PlayerPrefs.GetString(SavedEmailKey, ""));

        titleCard.gameObject.SetActive(false);
        loginCard.gameObject.SetActive(true);
        (string.IsNullOrEmpty(emailInput.text) ? emailInput : passwordInput).ActivateInputField();
    }

    private void CloseLoginCard()
    {
        if (busy) return;

        passwordInput.SetTextWithoutNotify("");
        loginCard.gameObject.SetActive(false);
        titleCard.gameObject.SetActive(true);
        titleNicknameInput.ActivateInputField();
    }

    // ───────────── 타이틀 카드의 버튼 ─────────────

    // 이메일 계정이면 "로그아웃"과 이메일을, 아니면 "이메일로 로그인"을 보여 준다
    private void RefreshEntryButton()
    {
        if (emailLoginLabel == null) return;

        emailLoginLabel.text = identity.HasEmail ? $"로그아웃 ({identity.Email})" : "이메일로 로그인";
    }

    // 로그아웃하면 이 기기는 새 익명 계정으로 시작한다. (이메일 계정은 이메일로 로그인해서 다시 쓸 수 있다)
    private void Logout()
    {
        identity.ResetIdentity();
        showToast("로그아웃했어요");
    }

    // ───────────── 로그인, 회원가입, 비밀번호 찾기 ─────────────

    private async Task LoginAsync()
    {
        if (busy) return;

        string problem = AccountRules.CheckEmail(emailInput.text) ?? AccountRules.CheckPassword(passwordInput.text);
        if (problem != null) { Fail(problem); return; }

        // 이 기기에서 쓰던 익명 계정은 로그인하면 이 기기와의 연결이 끊긴다. 한 번 알려 주고, 한 번 더 누르면 진행한다.
        if (!identity.HasEmail && !loginConfirmed)
        {
            loginConfirmed = true;
            showToast("이 기기에서 쓰던 계정의 기록은 사라지고 이메일 계정으로 바뀌어요. 계속하려면 로그인을 한 번 더 눌러 주세요");
            return;
        }

        SetBusy(true);
        try
        {
            AccountResult result = await identity.LoginWithEmailAsync(emailInput.text, passwordInput.text);
            if (!result.Ok) { Fail(result.Message); return; }

            SaveEmailIfWanted(emailInput.text.Trim());

            // 그 계정에 저장해 둔 닉네임을 닉네임 칸에 채워 준다
            await identity.RefreshAccountInfoAsync();
            string nickname = PlayFabProfile.ExtractNickname(identity.ServerDisplayName);
            if (!string.IsNullOrEmpty(nickname))
            {
                PlayerPrefs.SetString(SavedNicknameKey, nickname);
                titleNicknameInput.SetTextWithoutNotify(nickname);
            }

            passwordInput.SetTextWithoutNotify("");
            loginCard.gameObject.SetActive(false);
            titleCard.gameObject.SetActive(true);
            showToast(string.IsNullOrEmpty(result.Message) ? "로그인했어요" : result.Message);
        }
        finally { SetBusy(false); }
    }

    private async Task SignupAsync()
    {
        if (busy) return;

        string problem = AccountRules.CheckEmail(emailInput.text) ?? AccountRules.CheckPassword(passwordInput.text);
        if (problem != null) { Fail(problem); return; }

        SetBusy(true);
        try
        {
            AccountResult result = await identity.RegisterEmailAsync(emailInput.text, passwordInput.text);
            if (!result.Ok) { Fail(result.Message); return; }

            SaveEmailIfWanted(emailInput.text.Trim());
            passwordInput.SetTextWithoutNotify("");
            loginCard.gameObject.SetActive(false);
            titleCard.gameObject.SetActive(true);
            showToast("이메일 계정으로 가입했어요");
        }
        finally { SetBusy(false); }
    }

    private async Task ForgotAsync()
    {
        if (busy) return;

        SetBusy(true);
        try
        {
            AccountResult result = await identity.RequestPasswordResetAsync(emailInput.text);
            if (!result.Ok) { Fail(result.Message); return; }

            showToast(result.Message);
        }
        finally { SetBusy(false); }
    }

    // ───────────── 도우미 ─────────────

    private void Fail(string message)
    {
        UIFx.Shake(loginCard);
        showToast(message);
    }

    // 요청 중에는 버튼을 눌러도 아무 일도 없게 한다 (같은 요청이 두 번 가지 않게)
    private void SetBusy(bool value)
    {
        busy = value;
        loginButton.interactable = !value;
        signupButton.interactable = !value;
        if (forgotButton != null) forgotButton.interactable = !value;
        if (backButton != null) backButton.interactable = !value;
    }

    private void SaveEmailIfWanted(string email)
    {
        bool save = saveEmailToggle == null || saveEmailToggle.isOn;
        PlayerPrefs.SetInt(SaveEmailKey, save ? 1 : 0);
        PlayerPrefs.SetString(SavedEmailKey, save ? email : "");
        PlayerPrefs.Save();
    }
}