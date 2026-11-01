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
    private const float RequestTimeoutSeconds = 3f;      // 요청 한 번의 제한 시간. Photon 연결 제한(7초) 안에 끝나야 한다
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
        log("다른 사용자로 시작합니다. 다음 접속부터 새 PlayFab 계정을 씁니다");
        Changed?.Invoke();
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
                Fail($"PlayFab 로그인 실패: {result.ErrorCode} {result.ErrorMessage}");
                return false;
            }

            sessionTicket = (string)result.Data["SessionTicket"];
            PlayFabId = (string)result.Data["PlayFabId"];
            NewlyCreated = (bool?)result.Data["NewlyCreated"] ?? false;
            loggedInAt = Time.realtimeSinceStartup;
            LastError = null;

            if (string.IsNullOrEmpty(sessionTicket) || string.IsNullOrEmpty(PlayFabId))
            {
                sessionTicket = null;
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
                Fail($"Photon 토큰 요청 실패: {result.ErrorCode} {result.ErrorMessage}");
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
}