using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Fusion;
using Fusion.Photon.Realtime;
using Fusion.Sockets;
using UnityEngine;

/// <summary>
/// Photon Fusion(Shared 모드) 접속을 맡는 클래스.
/// 로비(방 목록 보기) → 방(Photon 세션) 만들기와 입장 → 나가기까지의 연결만 다룬다.
/// 게임 메시지를 주고받는 일은 아직 없다. (다음 단계)
///
/// 우리 구분과 Photon의 대응:
///   로비    = Photon의 세션 로비 (방 목록을 보는 곳, 세션이 아님)
///   방      = Photon 세션 하나. 대기방과 인게임이 같은 세션이다
///   방장    = Photon의 마스터 클라이언트 (처음엔 세션을 만든 사람, 나가면 들어온 순서대로 다음 사람)
/// </summary>
public class PhotonConnection : Singleton<PhotonConnection>, INetworkRunnerCallbacks
{
    // 지역은 코드에서 고정한다. (PhotonAppSettings는 저장소에 올리지 않아서 거기에 의존하지 않는다)
    public const string Region = "kr";

    // 이 값이 다른 빌드끼리는 서로의 방이 보이지 않는다. 방 규칙이 바뀌어 호환되지 않으면 올린다.
    public const string NetworkVersion = "1";

    // 한 방에 들어올 수 있는 사람(봇 제외)의 한계. NetworkProjectConfig의 Player Count와 같게 둔다.
    public const int MaxHumansLimit = 4;

    // 방 코드: 헷갈리는 글자(0, O, 1, I, L)를 뺀 31자에서 고른 무작위 8자리. (약 8천억 가지라서 겹칠 일이 사실상 없다)
    // Photon 세션 이름이 곧 방 코드다. 사람이 보는 방 이름은 방 속성(name)에 따로 둔다.
    public const int CodeLength = 8;
    private const string CodeAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public enum ConnectionState { Disconnected, JoiningLobby, InLobby, StartingSession, InSession }

    private enum StartResult { Ok, Failed, WrongRoom }

    private NetworkRunner runner;
    private List<SessionInfo> sessions = new List<SessionInfo>();
    private int lastMasterId = int.MinValue;

    // 접속자 변화는 Fusion 콜백에 기대지 않고 ActivePlayers를 지켜봐서 알아낸다.
    // (Shared 모드에서는 OnPlayerJoined가 모든 사람에게 오지 않을 수 있다고 공식 문서와 샘플이 안내한다)
    private readonly HashSet<int> knownPlayers = new HashSet<int>();
    private readonly HashSet<int> nowPlayers = new HashSet<int>();
    private float nextPlayerCheckTime;

    public ConnectionState Current { get; private set; } = ConnectionState.Disconnected;

    /// <summary>로비에서 받은 방 목록 (로비에 있을 때만 갱신된다)</summary>
    public IReadOnlyList<SessionInfo> Sessions => sessions;

    /// <summary>방 안에서 메시지를 주고받는 쪽</summary>
    public PhotonMessenger Messenger { get; private set; }

    public event Action StateChanged;
    public event Action SessionsChanged;
    public event Action PlayersChanged;
    public event Action MasterChanged;
    public event Action<string> LogAdded;

    protected override void OnAwake()
    {
        Messenger = new PhotonMessenger(this);
    }

    // ───────────── 상태 조회 ─────────────

    /// <summary>Photon AppId가 들어 있는지 (Fusion Hub에서 입력)</summary>
    public bool HasAppId =>
        PhotonAppSettings.TryGetGlobal(out PhotonAppSettings global) && !string.IsNullOrEmpty(global.AppSettings.AppIdFusion);

    public bool IsInSession => runner != null && runner.IsRunning && Current == ConnectionState.InSession;

    /// <summary>내가 방장(마스터 클라이언트)인지</summary>
    public bool IsMaster => IsInSession && runner.IsSharedModeMasterClient;

    public PlayerRef LocalPlayerRef => runner != null ? runner.LocalPlayer : default(PlayerRef);

    public PlayerRef MasterPlayerRef => IsInSession ? runner.GetMasterClient() : default(PlayerRef);

    /// <summary>방에 접속해 있는 사람들 (봇은 Photon에 접속한 게 아니라서 들어 있지 않다)</summary>
    public IEnumerable<PlayerRef> Players => runner != null ? runner.ActivePlayers : Enumerable.Empty<PlayerRef>();

    /// <summary>Photon이 세는 지금 방의 접속자 수. 목록(Players)과 다르면 Fusion 쪽 갱신이 늦은 것이다</summary>
    public int RoomPlayerCount
    {
        get
        {
            if (!IsInSession) return 0;
            SessionInfo info = runner.SessionInfo;
            return info != null && info.IsValid ? info.PlayerCount : 0;
        }
    }

    /// <summary>지금 들어 있는 방의 코드 (8자리). 방 안에 없으면 빈 문자열</summary>
    public string RoomCode => SessionName;

    /// <summary>새 방 코드를 만든다.</summary>
    public static string NewRoomCode()
    {
        var chars = new char[CodeLength];
        for (int i = 0; i < CodeLength; i++)
            chars[i] = CodeAlphabet[UnityEngine.Random.Range(0, CodeAlphabet.Length)];
        return new string(chars);
    }

    /// <summary>사람이 입력한 코드를 정리한다. (공백과 하이픈을 빼고 대문자로)</summary>
    public static string NormalizeCode(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        return new string(input.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    /// <summary>화면에 보일 모양: ABCD-EFGH</summary>
    public static string FormatCode(string code)
    {
        return !string.IsNullOrEmpty(code) && code.Length == CodeLength ? code.Substring(0, 4) + "-" + code.Substring(4) : code;
    }

    public string SessionName
    {
        get
        {
            if (!IsInSession) return "";
            SessionInfo info = runner.SessionInfo;
            return info != null && info.IsValid ? info.Name : "";
        }
    }

    // ───────────── 로비 ─────────────

    /// <summary>로비에 들어가 방 목록을 받기 시작한다. 이미 연결돼 있으면 끊고 새로 시작한다.</summary>
    public async Task<bool> JoinLobby()
    {
        if (!CheckReady()) return false;

        await ResetRunner();
        SetState(ConnectionState.JoiningLobby);
        Add($"로비 입장 시도 (지역 {Region})");

        StartGameResult result;
        try
        {
            result = await runner.JoinSessionLobby(SessionLobby.Shared, null, null, BuildAppSettings());
        }
        catch (Exception e)
        {
            Add("로비 입장 오류: " + e.Message);
            await Disconnect();
            return false;
        }

        if (!result.Ok)
        {
            Add($"로비 입장 실패: {result.ShutdownReason} {result.ErrorMessage}");
            await Disconnect();
            return false;
        }

        SetState(ConnectionState.InLobby);
        Add("로비 입장 완료. 방 목록을 기다립니다");
        return true;
    }

    // ───────────── 방 ─────────────

    /// <summary>
    /// 방을 만든다. 방 코드(8자리)가 곧 Photon 세션 이름이고, 사람이 보는 이름은 방 속성(name)에 넣는다.
    /// (Shared 모드는 같은 이름의 방이 있으면 새로 만들지 않고 들어가 버려서, 목록에 이미 있는 코드는 피한다)
    /// </summary>
    /// <param name="displayName">방 목록에 보일 이름. 비우면 방 코드를 쓴다</param>
    /// <param name="maxHumans">방에 들어올 수 있는 최대 사람 수 (봇 제외)</param>
    public async Task<bool> CreateRoom(string displayName, int maxHumans)
    {
        maxHumans = Math.Max(1, Math.Min(maxHumans, MaxHumansLimit));

        string code = NewRoomCode();
        for (int i = 0; i < 5 && sessions.Any(session => session.Name == code); i++)
            code = NewRoomCode();

        var properties = new Dictionary<string, SessionProperty>
        {
            ["name"] = string.IsNullOrWhiteSpace(displayName) ? FormatCode(code) : displayName,
            ["state"] = "waiting",
        };

        return await StartSession(code, maxHumans, properties, expectExisting: false, visible: true) == StartResult.Ok;
    }

    /// <summary>방 목록에서 고른 방에 들어간다.</summary>
    public async Task<bool> JoinRoom(string sessionName)
    {
        SessionInfo found = sessions.FirstOrDefault(s => s.Name == sessionName);
        if (found == null)
        {
            Add("방 목록에 없는 방입니다: " + sessionName);
            return false;
        }
        if (!found.IsOpen || found.PlayerCount >= found.MaxPlayers)
        {
            Add("들어갈 수 없는 방입니다 (닫혔거나 가득 참)");
            return false;
        }

        return await StartSession(sessionName, null, null, expectExisting: true, visible: true) == StartResult.Ok;
    }

    /// <summary>
    /// 방 코드로 들어간다. 방 목록에 없는 방(보이지 않는 방)도 코드를 알면 들어갈 수 있다.
    /// 없는 코드면 Shared 모드가 빈 방을 새로 만들어 버리는데, 그 방은 보이지 않게 만들고 바로 나온다.
    /// </summary>
    public async Task<bool> JoinRoomByCode(string input)
    {
        string code = NormalizeCode(input);
        if (code.Length != CodeLength)
        {
            Add($"방 코드는 {CodeLength}자리입니다");
            return false;
        }

        SessionInfo found = sessions.FirstOrDefault(s => s.Name == code);
        if (found != null && (!found.IsOpen || found.PlayerCount >= found.MaxPlayers))
        {
            Add("들어갈 수 없는 방입니다 (닫혔거나 가득 참)");
            return false;
        }

        StartResult result = await StartSession(code, null, null, expectExisting: true, visible: found != null);
        if (result == StartResult.WrongRoom) Add("그 코드의 방이 없습니다");
        return result == StartResult.Ok;
    }

    private async Task<StartResult> StartSession(string sessionName, int? maxHumans, Dictionary<string, SessionProperty> properties, bool expectExisting, bool visible)
    {
        if (!CheckReady()) return StartResult.Failed;
        if (Current == ConnectionState.StartingSession || Current == ConnectionState.InSession)
        {
            Add("이미 방에 들어가는 중이거나 방 안에 있습니다");
            return StartResult.Failed;
        }

        if (runner == null) await ResetRunner();

        PhotonRunnerSetup.Prepare(runner, out INetworkSceneManager sceneManager, out INetworkObjectProvider objectProvider);

        SetState(ConnectionState.StartingSession);
        Add($"{(expectExisting ? "방 입장" : "방 만들기")} 시도: {FormatCode(sessionName)}");

        var args = new StartGameArgs
        {
            GameMode = GameMode.Shared,
            SessionName = sessionName,
            PlayerCount = maxHumans,
            IsOpen = true,
            IsVisible = visible,
            SessionProperties = properties,
            CustomPhotonAppSettings = BuildAppSettings(),
            SceneManager = sceneManager,
            ObjectProvider = objectProvider,
        };

        StartGameResult result;
        try
        {
            result = await runner.StartGame(args);
        }
        catch (Exception e)
        {
            Add("방 시작 오류: " + e.Message);
            await Disconnect();
            return StartResult.Failed;
        }

        if (!result.Ok)
        {
            Add($"방 시작 실패: {result.ShutdownReason} {result.ErrorMessage}");
            await Disconnect();
            return StartResult.Failed;
        }

        lastMasterId = runner.GetMasterClient().PlayerId;
        RefreshKnownPlayers(announce: false);
        SetState(ConnectionState.InSession);
        Add($"방 안에 들어왔습니다: 코드 {FormatCode(runner.SessionInfo.Name)} (나 {runner.LocalPlayer.PlayerId}번, 방장 {lastMasterId}번)");

        // 들어가려던 방이 없었으면(코드가 틀렸거나 그 사이에 사라짐) 방금 새로 만들어진 방이다.
        // 그 방은 내가 방장이고 우리가 넣는 방 속성(state)이 없어서 알아볼 수 있다. 둘 다 맞을 때만 나가서, 정상 입장은 건드리지 않는다.
        if (expectExisting && IsEmptyNewRoom())
        {
            Add("들어가려던 방이 없어서 나갑니다 (코드가 틀렸거나 방이 사라짐)");
            await Disconnect();
            return StartResult.WrongRoom;
        }

        PlayersChanged?.Invoke();
        MasterChanged?.Invoke();
        return StartResult.Ok;
    }

    // 우리가 만든 방이면 항상 들어 있는 방 속성(state)이 없고, 내가 방장이면 방금 새로 만들어진 방이다
    private bool IsEmptyNewRoom()
    {
        SessionInfo info = runner.SessionInfo;
        bool hasOurProperties = info != null && info.Properties != null && info.Properties.ContainsKey("state");
        return runner.IsSharedModeMasterClient && !hasOurProperties;
    }

    /// <summary>방에서 나가 연결을 끊는다. (로비로 돌아가려면 이어서 JoinLobby를 부른다)</summary>
    public async Task Disconnect()
    {
        await ResetRunner(createNew: false);
        sessions.Clear();
        SetState(ConnectionState.Disconnected);
        SessionsChanged?.Invoke();
        PlayersChanged?.Invoke();
    }

    /// <summary>방을 닫으면 새로 들어올 수 없다. (게임이 시작되면 닫는다) 사람 수 제한과는 별개다.</summary>
    public bool SetRoomOpen(bool open)
    {
        if (!IsInSession) return false;

        SessionInfo info = runner.SessionInfo;
        if (info == null || !info.IsValid) return false;

        info.IsOpen = open;
        Add(open ? "방을 열었습니다" : "방을 닫았습니다 (새로 들어올 수 없음)");
        return true;
    }

    /// <summary>신뢰 데이터 하나를 한 사람에게 보낸다. (메시지 전송은 PhotonMessenger가 이 함수를 쓴다)</summary>
    public void SendReliable(PlayerRef target, ReliableKey key, byte[] data)
    {
        if (!IsInSession) throw new InvalidOperationException("방 안에 있지 않습니다");
        runner.SendReliableDataToPlayer(target, key, data);
    }

    /// <summary>접속 로그에 한 줄을 남긴다. (콘솔과 테스트 창에 보인다)</summary>
    public void WriteLog(string message) => Add(message);

    /// <summary>방 속성(이름, 상태 등)을 바꾼다. 방 목록에 보이는 값이다.</summary>
    public bool UpdateRoomProperties(Dictionary<string, SessionProperty> properties)
    {
        if (!IsInSession) return false;

        SessionInfo info = runner.SessionInfo;
        return info != null && info.IsValid && info.UpdateCustomProperties(properties);
    }

    // ───────────── 내부 ─────────────

    private bool CheckReady()
    {
        if (HasAppId) return true;

        Add("Photon AppId가 없습니다. Fusion Hub(Tools > Fusion > Realtime Settings)에서 AppId를 입력하세요");
        return false;
    }

    // 지역과 버전을 코드에서 정한 Photon 설정을 만든다
    private FusionAppSettings BuildAppSettings()
    {
        FusionAppSettings settings = PhotonAppSettings.Global.AppSettings.GetCopy();
        settings.UseNameServer = true;
        settings.AppVersion = NetworkVersion;
        settings.FixedRegion = Region;
        return settings;
    }

    // 이전 러너를 끝내고(있다면) 새 러너를 만든다
    private async Task ResetRunner(bool createNew = true)
    {
        if (runner != null)
        {
            NetworkRunner old = runner;
            runner = null;

            old.RemoveCallbacks(this); // 우리가 일부러 끝내는 거라 콜백으로 상태를 흔들지 않게 한다
            try { await old.Shutdown(); }
            catch (Exception e) { Add("연결 종료 중 오류: " + e.Message); }
        }

        lastMasterId = int.MinValue;
        knownPlayers.Clear();
        Messenger?.Reset();
        if (!createNew) return;

        var go = new GameObject("NetworkRunner");
        go.transform.SetParent(transform);

        runner = go.AddComponent<NetworkRunner>();
        runner.ProvideInput = false;  // 우리 게임은 Fusion 입력을 쓰지 않는다
        runner.AddCallbacks(this);
        sessions.Clear();
    }

    private void SetState(ConnectionState state)
    {
        if (Current == state) return;
        Current = state;
        StateChanged?.Invoke();
    }

    private void Add(string message)
    {
        Debug.Log("[Photon] " + message);
        LogAdded?.Invoke(message);
    }

    // 방장과 접속자가 바뀌었는지 지켜본다.
    // Photon이 방장을 바꿀 때 따로 알려 주는 콜백이 없고, 접속자 콜백도 모든 사람에게 오지 않을 수 있어서 직접 확인한다.
    private void Update()
    {
        if (!IsInSession) return;

        Messenger.Tick();

        int masterId = runner.GetMasterClient().PlayerId;
        if (masterId != lastMasterId)
        {
            lastMasterId = masterId;
            Add($"방장이 바뀌었습니다: {masterId}번 {(runner.IsSharedModeMasterClient ? "(나)" : "")}");
            MasterChanged?.Invoke();
        }

        // 접속자 목록은 0.2초마다 확인한다
        if (Time.unscaledTime >= nextPlayerCheckTime)
        {
            nextPlayerCheckTime = Time.unscaledTime + 0.2f;
            RefreshKnownPlayers(announce: true);
        }
    }

    // 지금 접속자를 알고 있던 목록과 비교해서 바뀌었으면 알린다
    private void RefreshKnownPlayers(bool announce)
    {
        nowPlayers.Clear();
        foreach (PlayerRef player in runner.ActivePlayers)
            nowPlayers.Add(player.PlayerId);

        if (nowPlayers.SetEquals(knownPlayers)) return;

        if (announce)
        {
            foreach (int id in nowPlayers)
                if (!knownPlayers.Contains(id)) Add($"{id}번이 들어왔습니다");
            foreach (int id in knownPlayers)
                if (!nowPlayers.Contains(id)) Add($"{id}번이 나갔습니다");
            Add($"현재 접속자 {nowPlayers.Count}명 (Photon 집계 {RoomPlayerCount}명)");
        }

        knownPlayers.Clear();
        knownPlayers.UnionWith(nowPlayers);
        Messenger.OnPlayersChanged(nowPlayers);
        PlayersChanged?.Invoke();
    }

    // ───────────── Fusion 콜백 ─────────────

    public void OnPlayerJoined(NetworkRunner r, PlayerRef player)
    {
        if (r != runner) return;
        Add($"[콜백] OnPlayerJoined {player.PlayerId}번"); // 이 콜백이 오는지 확인하려고 남긴 진단 로그. 접속자 변화 알림은 Update의 확인이 한다
    }

    public void OnPlayerLeft(NetworkRunner r, PlayerRef player)
    {
        if (r != runner) return;
        Add($"[콜백] OnPlayerLeft {player.PlayerId}번");
    }

    public void OnSessionListUpdated(NetworkRunner r, List<SessionInfo> sessionList)
    {
        if (r != runner) return;
        sessions = new List<SessionInfo>(sessionList);
        Add($"방 목록 갱신: {sessions.Count}개");
        SessionsChanged?.Invoke();
    }

    public void OnShutdown(NetworkRunner r, ShutdownReason reason)
    {
        if (r != runner) return; // 우리가 일부러 끝낸 이전 러너는 무시한다

        Add("연결이 끝났습니다: " + reason);
        runner = null;
        sessions.Clear();
        SetState(ConnectionState.Disconnected);
        SessionsChanged?.Invoke();
        PlayersChanged?.Invoke();
    }

    public void OnDisconnectedFromServer(NetworkRunner r, NetDisconnectReason reason)
    {
        if (r != runner) return;
        Add("서버와 연결이 끊어졌습니다: " + reason);
    }

    public void OnConnectedToServer(NetworkRunner r) { }
    public void OnConnectFailed(NetworkRunner r, NetAddress remoteAddress, NetConnectFailedReason reason)
    {
        if (r != runner) return;
        Add("연결 실패: " + reason);
    }

    public void OnConnectRequest(NetworkRunner r, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
    public void OnCustomAuthenticationResponse(NetworkRunner r, Dictionary<string, object> data) { }
    public void OnHostMigration(NetworkRunner r, HostMigrationToken hostMigrationToken) { }
    public void OnInput(NetworkRunner r, NetworkInput input) { }
    public void OnInputMissing(NetworkRunner r, PlayerRef player, NetworkInput input) { }
    public void OnObjectEnterAOI(NetworkRunner r, NetworkObject obj, PlayerRef player) { }
    public void OnObjectExitAOI(NetworkRunner r, NetworkObject obj, PlayerRef player) { }
    public void OnReliableDataProgress(NetworkRunner r, PlayerRef player, ReliableKey key, float progress) { }
    public void OnReliableDataReceived(NetworkRunner r, PlayerRef player, ReliableKey key, ReadOnlySpan<byte> data)
    {
        if (r != runner) return;
        Messenger.OnReliableData(player, key, data);
    }
    public void OnSceneLoadDone(NetworkRunner r) { }
    public void OnSceneLoadStart(NetworkRunner r) { }
    public void OnUserSimulationMessage(NetworkRunner r, SimulationMessagePtr message) { }
}