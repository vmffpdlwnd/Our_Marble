using System;
using System.Threading.Tasks;
using Photon.Realtime;
using UnityEngine;

/// <summary>
/// 이 PC(브라우저)의 사용자를 PlayFab에서 알아보는 쪽. 계정을 만들지 않고 시작하는 "익명 로그인"이다.
///
///   사용자 구분   브라우저(PC)에 저장한 무작위 값(CustomId)으로 PlayFab에 로그인한다. 처음이면 계정이 자동으로 만들어진다.
///                 같은 브라우저는 같은 사용자다. "다른 사용자로 시작"은 이 값을 새로 만드는 것이다.
///   Photon 접속   접속할 때마다 PlayFab에서 Photon용 토큰을 받아 인증한다. Photon이 보는 사용자 ID는 PlayFab ID가 된다.
///                 (Photon이 토큰을 PlayFab에 확인하므로 남의 ID를 흉내 낼 수 없다)
///
/// 나중에 이메일과 비밀번호로 올릴 때(AddUsernamePassword)도 같은 계정을 이어서 쓰므로, 이 구조는 그대로 둔다.
/// </summary>
public class PlayFabIdentity
{
    private const string CustomIdKey = "PlayFab.CustomId";
    private const float RequestTimeoutSeconds = 4f;      // 요청 한 번의 제한 시간. 처음 연결은 인증서 확인 때문에 조금 걸릴 수 있다
    private const float SessionMaxAgeSeconds = 6 * 3600f; // 이보다 오래된 로그인은 새로 한다

    private readonly Action<string> log;
    private readonly string[] commandLineArgs;

    private string sessionTicket;
    private float loggedInAt;
    private Task<bool> loginTask;

    /// <summary>PlayFab이 이 사용자를 알아보는 값. (저장돼 있다. 개발용으로 -userSlot을 주면 뒤에 번호가 붙는다)</summary>
    public string CustomId { get; private set; }

    /// <summary>PlayFab이 붙인 사용자 ID. 로그인한 뒤에만 있다</summary>
    public string PlayFabId { get; private set; }

    /// <summary>이번 로그인으로 계정이 새로 만들어졌는지</summary>
    public bool NewlyCreated { get; private set; }

    public bool IsLoggedIn => !string.IsNullOrEmpty(sessionTicket);

    /// <summary>마지막 실패 이유 (화면과 로그에 보이는 문장)</summary>
    public string LastError { get; private set; }

    /// <summary>로그인 상태나 사용자가 바뀌었을 때 불린다. (테스트 창 표시용)</summary>
    public event Action Changed;

    /// <summary>
    /// 다른 계정으로 바뀌었을 때(이메일로 로그인, 다른 사용자로 시작) 불린다.
    /// 이전 계정의 닉네임, 전적, Photon 접속을 이 계정에서 다시 받아야 한다.
    /// </summary>
    public event Action AccountChanged;

    /// <summary>이 계정에 이메일이 연결돼 있는지. RefreshAccountInfoAsync나 이메일 가입, 로그인 뒤에 정해진다</summary>
    public bool HasEmail { get; private set; }

    /// <summary>이 계정에 연결된 이메일 (연결돼 있지 않으면 null)</summary>
    public string Email { get; private set; }

    public PlayFabIdentity(Action<string> log, string[] commandLineArgs = null)
    {
        this.log = log ?? (_ => { });
        this.commandLineArgs = commandLineArgs ?? ReadCommandLine();
        LoadCustomId();
    }

    // ───────────── 사용자 값 ─────────────

    private void LoadCustomId()
    {
        string saved = PlayerPrefs.GetString(CustomIdKey, "");
        if (string.IsNullOrEmpty(saved))
        {
            saved = NewCustomId();
            PlayerPrefs.SetString(CustomIdKey, saved);
            PlayerPrefs.Save();
        }

        // 개발용: 같은 PC에서 빌드를 여러 개 띄우면 저장 공간을 같이 써서 모두 같은 사용자가 된다.
        // -userSlot 2 처럼 번호를 주면 그 실행만 다른 사용자로 만든다. (저장하지 않는다)
        string slot = ReadSlot();
        CustomId = slot == null ? saved : saved + "-slot" + slot;
    }

    private string ReadSlot()
    {
        for (int i = 0; i < commandLineArgs.Length - 1; i++)
        {
            if (string.Equals(commandLineArgs[i], "-userSlot", StringComparison.OrdinalIgnoreCase))
                return commandLineArgs[i + 1];
        }
        return null;
    }

    private static string[] ReadCommandLine()
    {
        try { return Environment.GetCommandLineArgs(); }
        catch { return new string[0]; }
    }

    private static string NewCustomId() => Guid.NewGuid().ToString("N");

    /// <summary>다른 사용자로 시작한다. 사용자 값을 새로 만들어 저장하고, 로그인 상태를 비운다.</summary>
    public void ResetIdentity()
    {
        string fresh = NewCustomId();
        PlayerPrefs.SetString(CustomIdKey, fresh);
        PlayerPrefs.Save();

        string slot = ReadSlot();
        CustomId = slot == null ? fresh : fresh + "-slot" + slot;

        sessionTicket = null;
        PlayFabId = null;
        NewlyCreated = false;
        LastError = null;
        HasEmail = false;
        Email = null;
        log("다른 사용자로 시작합니다. 다음 접속부터 새 PlayFab 계정을 씁니다");
        Changed?.Invoke();
        AccountChanged?.Invoke();
    }

    // ───────────── 로그인 ─────────────

    /// <summary>PlayFab에 로그인되어 있게 한다. 이미 되어 있으면 그대로, 아니면 로그인한다. (동시에 여러 번 부르면 한 번만 로그인한다)</summary>
    public Task<bool> EnsureLoggedIn()
    {
        if (IsLoggedIn && Time.realtimeSinceStartup - loggedInAt < SessionMaxAgeSeconds)
            return Task.FromResult(true);

        if (loginTask == null || loginTask.IsCompleted)
            loginTask = LoginAsync();
        return loginTask;
    }

    private async Task<bool> LoginAsync()
    {
        try
        {
            var body = new { TitleId = PlayFabConfig.TitleId, CustomId = CustomId, CreateAccount = true };
            PlayFabRest.Result result = await PlayFabRest.PostAsync("/Client/LoginWithCustomID", body, null, RequestTimeoutSeconds);

            if (!result.Ok)
            {
                Fail($"PlayFab 로그인 실패: {result.ErrorCode} {result.ErrorMessage}{Hint(result.ErrorCode, null)}");
                return false;
            }

            if (!AcceptLogin(result.Data))
            {
                Fail("PlayFab 로그인 응답에 세션 정보가 없습니다");
                return false;
            }

            log($"PlayFab 로그인 완료: {PlayFabId}{(NewlyCreated ? " (새 계정)" : "")}");
            Changed?.Invoke();
            return true;
        }
        catch (Exception e)
        {
            Fail("PlayFab 로그인 중 오류: " + e.Message);
            return false;
        }
    }

    // 로그인 응답의 세션 티켓과 PlayFab ID를 받아들인다. 응답에 없으면 false (이전 세션은 지운다)
    private bool AcceptLogin(Newtonsoft.Json.Linq.JObject data)
    {
        sessionTicket = (string)data["SessionTicket"];
        PlayFabId = (string)data["PlayFabId"];
        NewlyCreated = (bool?)data["NewlyCreated"] ?? false;
        loggedInAt = Time.realtimeSinceStartup;
        LastError = null;

        if (string.IsNullOrEmpty(sessionTicket) || string.IsNullOrEmpty(PlayFabId))
        {
            sessionTicket = null;
            return false;
        }
        return true;
    }

    // ───────────── Photon 인증 ─────────────

    /// <summary>
    /// Photon에 접속할 때 낼 인증 값을 만든다. PlayFab에서 Photon용 토큰을 새로 받아서 쓴다. (접속마다 새로 받는다)
    /// 실패하면 null을 돌려주고 LastError에 이유를 남긴다.
    /// </summary>
    public async Task<AuthenticationValues> CreatePhotonAuthAsync(string photonAppId)
    {
        try
        {
            if (!await EnsureLoggedIn()) return null;

            string appIdPrefix = AppIdPrefix(photonAppId);
            PlayFabRest.Result result = await RequestPhotonToken(photonAppId);

            // 로그인한 지 오래돼 세션이 끝났을 수 있다. 연결 문제가 아니면 한 번만 다시 로그인해서 다시 받아 본다.
            if (!result.Ok && IsSessionProblem(result))
            {
                log("PlayFab 세션이 끝난 것 같아 다시 로그인합니다");
                sessionTicket = null;
                if (!await EnsureLoggedIn()) return null;
                result = await RequestPhotonToken(photonAppId);
            }

            if (!result.Ok)
            {
                Fail($"Photon 토큰 요청 실패: {result.ErrorCode} {result.ErrorMessage}{Hint(result.ErrorCode, appIdPrefix)}");
                return null;
            }

            string token = (string)result.Data["PhotonCustomAuthenticationToken"];
            if (string.IsNullOrEmpty(token))
            {
                Fail("Photon 토큰이 비어 있습니다");
                return null;
            }

            // Photon이 PlayFab에 확인하는 값이다. "username"에는 사용자 이름이 아니라 PlayFab ID를 넣는다. (PlayFab의 안내)
            var auth = new AuthenticationValues { AuthType = CustomAuthenticationType.Custom };
            auth.AddAuthParameter("username", PlayFabId);
            auth.AddAuthParameter("token", token);
            return auth;
        }
        catch (Exception e)
        {
            Fail("Photon 인증 값을 만드는 중 오류: " + e.Message);
            return null;
        }
    }

    // 보낸 AppId는 전부 보여 주지 않고 앞 8자리만 보여 준다. (PlayFab 애드온에 넣은 값과 맞는지 눈으로 비교하는 용도)
    private static string AppIdPrefix(string appId) =>
        string.IsNullOrEmpty(appId) ? "(비어 있음)" : appId.Substring(0, Math.Min(8, appId.Length));

    // 자주 나는 오류는 무엇을 확인해야 하는지 같이 알려 준다
    private static string Hint(string errorCode, string appIdPrefix)
    {
        switch (errorCode)
        {
            case "InvalidTitleId":
                return $" → Title ID({PlayFabConfig.TitleId})가 맞는지 확인하세요";
            case "PlayerCreationDisabled":
                return " → PlayFab Game Manager의 Settings > API Features에서 'Disable Player Creation' 아래의 Custom ID(LoginWithCustomID) 항목 체크를 풀고 저장하세요. 반영에 몇 분 걸릴 수 있습니다";
            case "PhotonNotEnabledForTitle":
                return " → PlayFab 타이틀의 Add-ons에서 Photon 애드온이 설치되지 않았습니다";
            case "PhotonApplicationNotFound":
            case "PhotonApplicationNotAssociatedWithTitle":
                return $" → PlayFab Photon 애드온에 넣은 Realtime App ID가 이 앱이 보낸 Fusion AppId(앞 8자리 {appIdPrefix})와 같은지 확인하세요";
            case "TIMEOUT":
            case "NETWORK":
                return " → PlayFab에 연결하지 못했습니다. 인터넷, VPN, 방화벽을 확인하세요";
            default:
                return "";
        }
    }

    /// <summary>
    /// 로그인한 사용자로 PlayFab API를 부른다. 로그인이 안 돼 있으면 먼저 로그인하고,
    /// 세션이 끝났다고 거절당하면(연결 문제가 아니면) 한 번만 다시 로그인해서 다시 부른다.
    /// </summary>
    public async Task<PlayFabRest.Result> CallAsync(string path, object body)
    {
        if (!await EnsureLoggedIn()) return PlayFabRest.Result.Fail("NOT_LOGGED_IN", LastError ?? "PlayFab에 로그인하지 못했습니다");

        PlayFabRest.Result result = await PlayFabRest.PostAsync(path, body, sessionTicket, RequestTimeoutSeconds);
        if (!result.Ok && LooksLikeExpiredSession(result.ErrorCode))
        {
            // 호출한 내용이 잘못된 것이 아니라 세션이 끝난 경우에만 다시 로그인한다
            log("PlayFab 세션이 끝난 것 같아 다시 로그인합니다");
            sessionTicket = null;
            if (!await EnsureLoggedIn()) return PlayFabRest.Result.Fail("NOT_LOGGED_IN", LastError ?? "PlayFab에 로그인하지 못했습니다");
            result = await PlayFabRest.PostAsync(path, body, sessionTicket, RequestTimeoutSeconds);
        }
        return result;
    }

    private static bool LooksLikeExpiredSession(string errorCode) =>
        errorCode == "NotAuthenticated" || errorCode == "InvalidSessionTicket" || errorCode == "ExpiredAuthToken";

    private Task<PlayFabRest.Result> RequestPhotonToken(string photonAppId)
    {
        return PlayFabRest.PostAsync("/Client/GetPhotonAuthenticationToken", new { PhotonApplicationId = photonAppId }, sessionTicket, RequestTimeoutSeconds);
    }

    // 연결이 안 되거나(NETWORK, TIMEOUT) 응답을 못 읽은 경우가 아니면, PlayFab이 우리 세션을 거절한 것으로 본다
    private static bool IsSessionProblem(PlayFabRest.Result result)
    {
        return result.ErrorCode != "NETWORK" && result.ErrorCode != "TIMEOUT" && result.ErrorCode != "BAD_RESPONSE";
    }

    private void Fail(string message)
    {
        LastError = message;
        log(message);
        Changed?.Invoke();
    }

    // ───────────── 이메일 계정 ─────────────

    /// <summary>
    /// 이 계정에 이메일이 연결돼 있는지 서버에서 확인한다. (화면에 "가입하기"를 보여 줄지 정하려고 로비가 뜰 때 부른다)
    /// 확인하지 못해도 게임에는 영향이 없다.
    /// </summary>
    public async Task<bool> RefreshAccountInfoAsync()
    {
        try
        {
            PlayFabRest.Result result = await CallAsync("/Client/GetAccountInfo", new { });
            if (!result.Ok) return false;

            string email = (string)result.Data["AccountInfo"]?["PrivateInfo"]?["Email"];
            bool has = !string.IsNullOrEmpty(email);
            if (has != HasEmail || email != Email)
            {
                HasEmail = has;
                Email = has ? email : null;
                Changed?.Invoke();
            }
            return true;
        }
        catch (Exception e)
        {
            log("계정 정보를 확인하는 중 오류: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// 지금 쓰는 계정에 이메일과 비밀번호를 붙인다. (익명 계정을 이메일 계정으로 올린다. 닉네임과 전적은 그대로 이어진다)
    /// 로그인 이름은 PlayFab이 요구해서 계정 ID로 자동으로 정한다. 사용자는 이메일로 로그인한다.
    /// </summary>
    public async Task<AccountResult> RegisterEmailAsync(string email, string password)
    {
        string problem = AccountRules.CheckEmail(email) ?? AccountRules.CheckPassword(password);
        if (problem != null) return AccountResult.Invalid(problem);

        try
        {
            if (!await EnsureLoggedIn()) return AccountResult.Fail("NOT_LOGGED_IN", AccountRules.MessageFor("NOT_LOGGED_IN"));

            string trimmed = email.Trim();
            PlayFabRest.Result result = await CallAsync("/Client/AddUsernamePassword", new { Username = PlayFabId, Email = trimmed, Password = password });
            if (!result.Ok)
            {
                log($"이메일 가입 실패: {result.ErrorCode}");
                return AccountResult.Fail(result.ErrorCode, AccountRules.MessageFor(result.ErrorCode));
            }

            HasEmail = true;
            Email = trimmed;
            log("이메일 계정으로 가입했습니다");
            Changed?.Invoke();
            return AccountResult.Success();
        }
        catch (Exception e)
        {
            log("이메일 가입 중 오류: " + e.Message);
            return AccountResult.Fail("ERROR", "처리하지 못했어요");
        }
    }

    /// <summary>
    /// 이메일과 비밀번호로 로그인한다. 성공하면 이 기기가 그 계정으로 바뀌고, 이 기기의 사용자 값을 그 계정에 연결해서
    /// 다음부터는 이메일을 다시 넣지 않아도 자동으로 그 계정으로 로그인된다.
    /// (이 기기에서 쓰던 익명 계정은 더 이상 이 기기와 연결되지 않는다)
    /// 실패하면 지금 계정과 세션은 그대로다.
    /// </summary>
    public async Task<AccountResult> LoginWithEmailAsync(string email, string password)
    {
        string problem = AccountRules.CheckEmail(email) ?? AccountRules.CheckPassword(password);
        if (problem != null) return AccountResult.Invalid(problem);

        try
        {
            string trimmed = email.Trim();
            PlayFabRest.Result result = await PlayFabRest.PostAsync("/Client/LoginWithEmailAddress",
                new { TitleId = PlayFabConfig.TitleId, Email = trimmed, Password = password }, null, RequestTimeoutSeconds);

            if (!result.Ok)
            {
                log($"이메일 로그인 실패: {result.ErrorCode}");
                return AccountResult.Fail(result.ErrorCode, AccountRules.MessageFor(result.ErrorCode));
            }

            // 성공한 응답만 받아들인다. 응답이 이상하면 지금 세션을 그대로 둔다.
            string oldTicket = sessionTicket, oldId = PlayFabId; bool oldNew = NewlyCreated; float oldAt = loggedInAt;
            if (!AcceptLogin(result.Data))
            {
                sessionTicket = oldTicket; PlayFabId = oldId; NewlyCreated = oldNew; loggedInAt = oldAt;
                return AccountResult.Fail("BAD_RESPONSE", "로그인 응답을 읽지 못했어요");
            }

            HasEmail = true;
            Email = trimmed;
            log($"이메일로 로그인했습니다: {PlayFabId}");

            // 이 기기의 사용자 값을 이 계정에 연결한다. (이미 다른 계정에 연결돼 있으면 ForceLink로 옮긴다)
            PlayFabRest.Result link = await CallAsync("/Client/LinkCustomID", new { CustomId = CustomId, ForceLink = true });
            string warning = null;
            if (!link.Ok)
            {
                log($"이 기기를 계정에 연결하지 못했습니다: {link.ErrorCode}");
                warning = "이 기기에서 자동 로그인을 연결하지 못했어요. 다음에 실행하면 다시 로그인해야 할 수 있어요";
            }

            Changed?.Invoke();
            AccountChanged?.Invoke();
            return AccountResult.Success(warning);
        }
        catch (Exception e)
        {
            log("이메일 로그인 중 오류: " + e.Message);
            return AccountResult.Fail("ERROR", "처리하지 못했어요");
        }
    }

    /// <summary>비밀번호를 다시 정할 수 있는 링크를 이메일로 보낸다. (로그인하지 않은 상태에서도 쓴다)</summary>
    public async Task<AccountResult> RequestPasswordResetAsync(string email)
    {
        string problem = AccountRules.CheckEmail(email);
        if (problem != null) return AccountResult.Invalid(problem);

        try
        {
            PlayFabRest.Result result = await PlayFabRest.PostAsync("/Client/SendAccountRecoveryEmail",
                new { TitleId = PlayFabConfig.TitleId, Email = email.Trim() }, null, RequestTimeoutSeconds);

            if (!result.Ok)
            {
                log($"비밀번호 재설정 메일 실패: {result.ErrorCode} {result.ErrorMessage}");
                return AccountResult.Fail(result.ErrorCode, AccountRules.MessageFor(result.ErrorCode));
            }

            return AccountResult.Success("비밀번호를 다시 정하는 메일을 보냈어요. 메일함을 확인해 주세요");
        }
        catch (Exception e)
        {
            log("비밀번호 재설정 중 오류: " + e.Message);
            return AccountResult.Fail("ERROR", "처리하지 못했어요");
        }
    }
}