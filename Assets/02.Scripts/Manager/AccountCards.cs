using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 타이틀 화면의 이메일 계정 카드를 움직이는 쪽. TitleManager가 만든다.
///
/// 씬에는 이 이름의 오브젝트가 있어야 해요. 없으면 그 부분만 조용히 건너뛰고 닉네임 시작은 그대로 동작한다.
///   Canvas_Login/SignupCard/EmailLoginButton   타이틀 카드의 "이메일 로그인 / 가입"(가입을 기다리는 중에는 "가입 이어서 하기", 가입한 뒤에는 "로그아웃") 버튼
///   Canvas_Login/LoginCard                     이메일 카드 (옛 로그인 씬의 것을 복사해 온다)
///     EmailInput, PasswordInput, SaveIdToggle, ForgotButton, LoginButton, SignupButton, BackButton (BrandIcon, BrandText는 있으면 쓴다)
///
/// 이 PC의 사용자는 처음에 익명 계정이다. 가입은 그 계정에 이메일을 붙이는 것이라 닉네임과 전적이 이어진다.
/// 가입은 두 단계다. (규칙은 SignupRules)
///   1단계  "이메일 인증"  이메일만 넣고 누르면 이메일이 계정에 붙고, 비밀번호를 정하는 메일이 간다.
///   2단계  "가입 완료"    메일의 링크에서 정한 비밀번호를 넣고 누르면 맞는지 확인하고 가입이 끝난다. 그다음 닉네임을 넣고 시작한다.
/// 1단계와 2단계 사이에 앱을 닫아도 타이틀 링크가 "가입 이어서 하기"로 보여서 이어 갈 수 있다.
/// "로그인"은 이미 가입한 계정으로 바꾸는 것이다.
///
/// 카드의 글자(제목, 안내, 버튼 이름)는 코드가 바꾼다. 씬의 글자는 그대로 둬도 된다.
/// </summary>
public class AccountCards : MonoBehaviour
{
    private const string SavedEmailKey = "Login.SavedEmail";
    private const string SaveEmailKey = "Login.SaveEmail";
    private const string SavedNicknameKey = "Title.SavedNickname"; // TitleManager와 같은 키
    private const float EntryButtonWidth = 480f; // 타이틀 카드의 링크 버튼 폭. 입력칸과 같게 한다
    private const int EmailMaxChars = 26;        // 로그아웃 버튼에 보여 줄 이메일의 최대 글자 수

    // 카드 위쪽 배치: 아이콘을 숨기고 제목을 위로 올려서 안내 글이 들어갈 자리를 만든다 (입력칸은 -232부터 시작한다)
    private const float TitleY = -40f;
    private const float HintY = -112f;
    private const float HintHeight = 112f;

    private enum CardMode
    {
        Login,         // 이메일과 비밀번호로 로그인 / 처음이면 이메일 인증(1단계)
        SignupPending, // 1단계를 마쳤고 정한 비밀번호를 입력해 가입을 끝낸다(2단계)
    }

    private Transform titleCard;
    private Transform loginCard;
    private Button emailLoginButton;
    private TMP_Text emailLoginLabel;

    private TMP_InputField emailInput, passwordInput;
    private Toggle saveEmailToggle;
    private Button forgotButton, loginButton, signupButton, backButton;
    private TMP_Text titleText, hintText, loginLabel, signupLabel, forgotLabel, passwordPlaceholder;
    private string passwordPlaceholderDefault;

    private TMP_InputField titleNicknameInput;
    private System.Action<string> showToast;
    private PlayFabIdentity identity;

    private CardMode mode = CardMode.Login;
    private bool busy;
    private bool loginConfirmed;      // 익명 계정에서 로그인하면 사라진다는 안내를 한 번 보여 줬는지
    private bool newAccountConfirmed; // 가입 중인 계정을 버리고 새 계정으로 시작한다는 안내를 한 번 보여 줬는지

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
        emailLoginLabel = CenterLinkButton(entry, EntryButtonWidth); // 입력칸과 같은 폭으로 넓히고 가운데에 둔다 (로그아웃 때 붙는 이메일이 잘리지 않게)

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
        if (backButton != null) CenterLinkButton(backButton.transform, 0f); // 가운데에 둔다 (폭은 그대로)

        loginLabel = LabelOf(loginButton);
        signupLabel = LabelOf(signupButton);
        forgotLabel = LabelOf(forgotButton);
        passwordPlaceholder = passwordInput.placeholder as TMP_Text;
        passwordPlaceholderDefault = passwordPlaceholder != null ? passwordPlaceholder.text : null;
        BuildHeader();

        emailLoginButton.onClick.AddListener(OnEmailLoginButton);
        loginButton.onClick.AddListener(() => _ = (mode == CardMode.SignupPending ? CompleteSignupAsync() : LoginAsync()));
        signupButton.onClick.AddListener(() => _ = (mode == CardMode.SignupPending ? NewAccountAsync() : StartSignupAsync()));
        if (forgotButton != null) forgotButton.onClick.AddListener(() => _ = ForgotAsync());
        if (backButton != null) backButton.onClick.AddListener(CloseLoginCard);

        emailInput.onSubmit.AddListener(_ => passwordInput.ActivateInputField()); // 엔터: 비밀번호 칸으로
        passwordInput.onSubmit.AddListener(text => { _ = (mode == CardMode.SignupPending ? CompleteSignupAsync() : LoginAsync()); }); // 엔터: 로그인, 가입 중이면 가입 완료

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

    /// <summary>
    /// 가운데에 놓는 링크 버튼을 정리한다. 이 버튼들은 로그인 카드의 "비밀번호 찾기"(카드 오른쪽 구석용)를 복사해 만든 것이라
    /// 기준점(pivot)이 오른쪽 위이고 글자가 오른쪽 정렬이라서, 가운데에 놓으면 왼쪽으로 쏠려 보인다.
    /// 기준점을 위쪽 가운데로 옮기고 글자를 가운데 정렬한다. width가 0보다 크면 폭도 그 값으로 바꾼다.
    /// 글자 칸의 TMP_Text를 돌려준다. (없으면 null)
    /// </summary>
    private static TMP_Text CenterLinkButton(Transform button, float width)
    {
        if (button is RectTransform rect)
        {
            rect.pivot = new Vector2(0.5f, 1f);
            if (width > 0f) rect.sizeDelta = new Vector2(width, rect.sizeDelta.y);
        }

        Transform labelTransform = button.Find("Label");
        TMP_Text label = labelTransform != null ? labelTransform.GetComponent<TMP_Text>() : button.GetComponentInChildren<TMP_Text>();
        if (label == null) return null;

        label.alignment = TextAlignmentOptions.Center;
        label.fontSizeMax = label.fontSize;   // 글자가 폭보다 길면 이 크기에서 줄어든다
        label.fontSizeMin = 14f;
        label.enableAutoSizing = true;
        return label;
    }

    private static T GetComponentAt<T>(Transform root, string path) where T : Component
    {
        Transform t = root.Find(path);
        return t != null ? t.GetComponent<T>() : null;
    }

    private static TMP_Text LabelOf(Button button)
    {
        if (button == null) return null;
        Transform label = button.transform.Find("Label");
        return label != null ? label.GetComponent<TMP_Text>() : button.GetComponentInChildren<TMP_Text>();
    }

    /// <summary>
    /// 카드 위쪽을 정리한다. 아이콘을 숨기고 제목(BrandText)을 위로 올린 뒤, 제목을 복제해서 아래에 안내 글(HintText)을 만든다.
    /// (씬을 고치지 않으려고 코드로 한다. 안내 글은 토스트처럼 사라지지 않고 카드에 계속 보인다)
    /// </summary>
    private void BuildHeader()
    {
        Transform icon = loginCard.Find("BrandIcon");
        if (icon != null) icon.gameObject.SetActive(false);

        Transform title = loginCard.Find("BrandText");
        if (title == null) return;

        titleText = title.GetComponent<TMP_Text>();
        if (titleText != null)
        {
            titleText.enableAutoSizing = true;
            titleText.fontSizeMax = titleText.fontSize;
            titleText.fontSizeMin = 30f;
        }
        if (title is RectTransform titleRect) titleRect.anchoredPosition = new Vector2(0f, TitleY);

        GameObject copy = Instantiate(title.gameObject, loginCard);
        copy.name = "HintText";
        copy.SetActive(true);
        hintText = copy.GetComponent<TMP_Text>();
        if (hintText == null) { Destroy(copy); return; }

        var hintRect = (RectTransform)copy.transform;
        hintRect.anchorMin = hintRect.anchorMax = new Vector2(0.5f, 1f);
        hintRect.pivot = new Vector2(0.5f, 1f);
        hintRect.anchoredPosition = new Vector2(0f, HintY);
        hintRect.sizeDelta = new Vector2(EntryButtonWidth, HintHeight);

        hintText.alignment = TextAlignmentOptions.Top;
        hintText.textWrappingMode = TextWrappingModes.Normal; // 제목은 한 줄이라 줄바꿈이 꺼져 있다. 제목을 복제한 안내 글은 여러 줄로 감싸야 한다
        hintText.enableAutoSizing = true;
        hintText.fontSizeMax = 20f;
        hintText.fontSizeMin = 14f;
        hintText.fontSize = 20f;
        hintText.color = new Color(0.42f, 0.46f, 0.55f, 1f);
        hintText.raycastTarget = false;
    }

    // ───────────── 카드 열고 닫기 ─────────────

    private void OnEmailLoginButton()
    {
        if (busy) return;

        if (IsPending())
        {
            OpenLoginCard(CardMode.SignupPending);
            return;
        }

        if (identity.HasEmail)
        {
            Logout();
            return;
        }
        OpenLoginCard(CardMode.Login);
    }

    private void OpenLoginCard(CardMode openMode)
    {
        ShowMode(openMode);

        titleCard.gameObject.SetActive(false);
        loginCard.gameObject.SetActive(true);
        FocusFirstInput();
    }

    private void CloseLoginCard()
    {
        if (busy) return;

        passwordInput.SetTextWithoutNotify("");
        loginCard.gameObject.SetActive(false);
        titleCard.gameObject.SetActive(true);
        titleNicknameInput.ActivateInputField();
        RefreshEntryButton();
    }

    /// <summary>카드의 글자, 입력칸, 버튼 이름을 모드에 맞게 바꾼다. (카드가 이미 열려 있어도 쓸 수 있다)</summary>
    private void ShowMode(CardMode newMode)
    {
        mode = newMode;
        loginConfirmed = false;
        newAccountConfirmed = false;
        passwordInput.SetTextWithoutNotify("");

        bool pending = newMode == CardMode.SignupPending;

        SetText(titleText, pending ? SignupRules.PendingTitle : SignupRules.LoginTitle);
        SetText(hintText, pending ? SignupRules.PendingHint : SignupRules.LoginHint);
        SetText(loginLabel, pending ? SignupRules.CompleteLabel : SignupRules.LoginLabel);
        SetText(signupLabel, pending ? SignupRules.NewAccountLabel : SignupRules.SignupLabel);
        SetText(forgotLabel, pending ? SignupRules.ResendLabel : SignupRules.ForgotLabel);
        if (passwordPlaceholder != null && passwordPlaceholderDefault != null)
            passwordPlaceholder.text = pending ? SignupRules.PendingPasswordHint : passwordPlaceholderDefault;

        if (pending)
        {
            // 가입 중인 이메일은 고칠 수 없다. (이미 계정에 붙어 있다. 틀렸다면 "새 계정으로 시작")
            emailInput.SetTextWithoutNotify(PendingEmail());
            emailInput.interactable = false;
        }
        else
        {
            emailInput.interactable = true;
            if (saveEmailToggle != null && saveEmailToggle.isOn) emailInput.SetTextWithoutNotify(PlayerPrefs.GetString(SavedEmailKey, ""));
        }
    }

    private void FocusFirstInput()
    {
        if (mode == CardMode.SignupPending) { passwordInput.ActivateInputField(); return; }
        (string.IsNullOrEmpty(emailInput.text) ? emailInput : passwordInput).ActivateInputField();
    }

    private static void SetText(TMP_Text label, string text)
    {
        if (label != null) label.text = text;
    }

    // ───────────── 가입을 기다리는 상태 ─────────────

    private string PendingEmail() => PlayerPrefs.GetString(SignupRules.PendingEmailKey, "");

    private SignupRules.PendingState PendingStateNow() =>
        SignupRules.EvaluatePending(PendingEmail(), PlayerPrefs.GetString(SignupRules.PendingIdKey, ""),
            identity.PlayFabId, identity.AccountInfoLoaded, identity.HasEmail, identity.Email);

    private bool IsPending() => PendingStateNow() == SignupRules.PendingState.Active;

    private void SavePending(string email)
    {
        PlayerPrefs.SetString(SignupRules.PendingEmailKey, email);
        PlayerPrefs.SetString(SignupRules.PendingIdKey, identity.PlayFabId ?? "");
        PlayerPrefs.Save();
    }

    private static void ClearPending()
    {
        PlayerPrefs.DeleteKey(SignupRules.PendingEmailKey);
        PlayerPrefs.DeleteKey(SignupRules.PendingIdKey);
        PlayerPrefs.Save();
    }

    // ───────────── 타이틀 카드의 버튼 ─────────────

    // 가입을 기다리는 중이면 "가입 이어서 하기", 이메일 계정이면 "로그아웃"과 이메일을, 아니면 "이메일로 로그인"을 보여 준다
    private void RefreshEntryButton()
    {
        if (emailLoginLabel == null) return;

        // 계정이 바뀌었거나 이메일이 연결돼 있지 않아서 더는 맞지 않는 가입 기다림은 버린다
        if (PendingStateNow() == SignupRules.PendingState.Stale) ClearPending();

        if (IsPending()) emailLoginLabel.text = SignupRules.EntryPendingLabel;
        else emailLoginLabel.text = identity.HasEmail ? $"로그아웃 ({AccountRules.ShortenEmail(identity.Email, EmailMaxChars)})" : SignupRules.EntryLoginLabel;
    }

    // 로그아웃하면 이 기기는 새 익명 계정으로 시작한다. (이메일 계정은 이메일로 로그인해서 다시 쓸 수 있다)
    private void Logout()
    {
        identity.ResetIdentity();
        showToast("로그아웃했어요");
    }

    // ───────────── 로그인, 가입, 비밀번호 찾기 ─────────────

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

    // 가입 1단계: 이메일을 계정에 붙이고 비밀번호를 정하는 메일을 보낸다
    private async Task StartSignupAsync()
    {
        if (busy) return;

        string problem = AccountRules.CheckEmail(emailInput.text);
        if (problem != null) { Fail(problem); return; }

        // 이미 이메일이 붙은 계정이면 타이틀 링크가 "로그아웃"으로 바뀌어 있어야 해서 여기까지 오지 않는다. 그래도 막아 둔다.
        if (identity.HasEmail) { Fail(SignupRules.AlreadyHasEmailMessage); return; }

        SetBusy(true);
        try
        {
            string email = emailInput.text.Trim();
            AccountResult result = await identity.StartEmailSignupAsync(email);
            if (!result.Ok) { Fail(result.Message); return; }

            // 이메일이 붙었으니 이제 비밀번호 입력을 기다리는 상태다. 앱을 닫아도 이어 가려고 저장해 둔다.
            SavePending(email);
            SaveEmailIfWanted(email);
            RefreshEntryButton(); // 이메일이 붙을 때 이미 한 번 불렸지만 그때는 기다림을 저장하기 전이라, 타이틀 링크를 "가입 이어서 하기"로 다시 바꾼다
            ShowMode(CardMode.SignupPending);
            FocusFirstInput();
            showToast(string.IsNullOrEmpty(result.Message) ? SignupRules.MailSentMessage : result.Message);
        }
        finally { SetBusy(false); }
    }

    // 가입 2단계: 메일에서 정한 비밀번호가 맞는지 확인하고 가입을 끝낸다
    private async Task CompleteSignupAsync()
    {
        if (busy) return;

        string email = PendingEmail();
        string problem = AccountRules.CheckEmail(email) ?? AccountRules.CheckPassword(passwordInput.text);
        if (problem != null) { Fail(problem); return; }

        SetBusy(true);
        try
        {
            AccountResult result = await identity.CheckEmailPasswordAsync(email, passwordInput.text);
            if (!result.Ok)
            {
                Fail(result.ErrorCode == "InvalidEmailOrPassword" ? SignupRules.WrongPasswordMessage : result.Message);
                return;
            }

            ClearPending();
            SaveEmailIfWanted(email);
            passwordInput.SetTextWithoutNotify("");
            loginCard.gameObject.SetActive(false);
            titleCard.gameObject.SetActive(true);
            titleNicknameInput.ActivateInputField();
            RefreshEntryButton();
            _ = identity.RefreshAccountInfoAsync();
            showToast(SignupRules.CompletedMessage);
        }
        finally { SetBusy(false); }
    }

    // 가입 중인 계정을 버리고 새 계정으로 시작한다. (이메일을 잘못 넣었을 때. 이메일은 바꿀 수 없다)
    private Task NewAccountAsync()
    {
        if (busy) return Task.CompletedTask;

        // 지금 계정의 기록은 이 기기에서 사라진다. 한 번 알려 주고, 한 번 더 누르면 진행한다.
        if (!newAccountConfirmed)
        {
            newAccountConfirmed = true;
            showToast(SignupRules.NewAccountWarning);
            return Task.CompletedTask;
        }

        ClearPending();
        identity.ResetIdentity();
        passwordInput.SetTextWithoutNotify("");
        loginCard.gameObject.SetActive(false);
        titleCard.gameObject.SetActive(true);
        titleNicknameInput.ActivateInputField();
        showToast(SignupRules.NewAccountDone);
        return Task.CompletedTask;
    }

    private async Task ForgotAsync()
    {
        if (busy) return;

        // 가입 중에는 "메일 다시 보내기"다. 입력칸이 아니라 가입 중인 이메일로 보낸다.
        string email = mode == CardMode.SignupPending ? PendingEmail() : emailInput.text;

        SetBusy(true);
        try
        {
            AccountResult result = await identity.RequestPasswordResetAsync(email);
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