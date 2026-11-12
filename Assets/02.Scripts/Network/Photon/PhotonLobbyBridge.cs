using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fusion;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 게임의 로비와 대기방 화면(LobbyManager)을 Photon에 이어 주는 쪽.
/// 화면은 예전처럼 LobbyNetwork로 요청을 보내고 알림을 받기만 하고, 그 뒤에서 이 클래스가 Photon으로 바꿔 준다.
///
/// 역할 나누기:
///   로비(방 밖)   Photon 방 목록이 곧 로비의 방 목록이다. 방 만들기와 입장은 Photon 방(세션)을 시작하는 일로 바뀐다.
///   방 안         방 하나를 가진 LocalLobbyHost를 방장(마스터)의 PC가 돌린다. 참가자의 요청은 방장에게 가고, 호스트의 알림은 Photon으로 나간다.
///
/// playerId: 사람은 Photon이 붙여 주는 번호(PlayerRef.PlayerId)를 그대로 쓴다. 봇은 1001부터라서 겹치지 않는다.
/// 방 번호(roomId): Photon 세션 이름, 즉 방 코드(8자리)다.
/// </summary>
public class PhotonLobbyBridge : IHostRequestReceiver
{
    // 방 목록에 보일 값을 Photon 방 속성에 담는 이름
    private const string KeyName = "name";
    private const string KeyState = "state";
    private const string KeyHost = "host";
    private const string KeyMap = "map";
    private const string KeyCount = "count";
    private const string KeyMax = "max";
    private const string KeyMembers = "members"; // 참가자 닉네임 명단 (RoomMemberCodec). 방 목록에서 방을 눌렀을 때 이름을 보여 준다
    private const string KeyHosted = "hosted";   // 1이면 방장 PC가 로비 호스트(서버 역할)를 돌리고 있는 방. 게임 로비에서 만든 방이다

    private const string StateWaiting = "waiting";
    private const string StatePlaying = "playing";

    private const long OfflinePlayerId = 1;      // Photon을 쓰지 않을 때 이 PC 플레이어의 번호 (TitleManager와 같다)
    private const float LeaveWaitSeconds = 3f;   // 방을 나갈 때 방장의 답을 기다리는 시간
    private const float AutoRejoinSeconds = 30f;       // 게임 중 끊겼을 때 자동으로 다시 들어가기를 시도하는 전체 시간
    private const float AutoRejoinRetrySeconds = 3f;   // 시도 사이의 간격
    private const float RejoinAnswerSeconds = 6f;      // 방장이 돌아오는 것을 받아 주기를 기다리는 시간
    private const float AutoLobbyRetrySeconds = 3f;  // 로비 연결이 끊겨 있을 때 스스로 다시 연결하는 간격
    private const float RoomListWaitSeconds = 2f;  // 로비에 방금 다시 연결했을 때, 고른 방이 목록에 나타나기를 기다리는 시간
    private const float LobbyConnectSeconds = 8f;  // 로비에 연결되기를 기다리는 시간. 넘으면 혼자 하기로 전환한다 (화면의 10초 제한보다 먼저)
    private const float LobbyAttemptSeconds = 12f;       // 로비 연결을 한 번 시도하고 기다리는 시간 (다시 시도할 때)
    private const float LobbyFirstConnectSeconds = 12f;  // 처음 연결할 때, 혼자 하기로 넘어가기까지 기다리는 전체 시간
    private const float LobbyGiveUpSeconds = 30f;        // 연결됐다가 끊긴 뒤, 혼자 하기로 넘어가기까지 다시 연결을 시도하는 전체 시간
    private const float LobbyRetryGapSeconds = 3f;       // 로비 연결 재시도 사이의 간격
    private const float HandshakeSeconds = 2.5f; // 방에 들어온 직후 방장의 퐁을 기다리는 시간. 퐁이 없어도 요청은 보낸다 (옛 버전 방장은 퐁을 못 준다)
    private const float MasterLeaveFlushSeconds = 0.5f; // 방장이 나갈 때, 방금 보낸 알림이 Photon으로 나갈 시간을 준다
    private const float AnswerWarnSeconds = 5f;  // 입장 요청을 보낸 뒤 이 시간 안에 답이 없으면 방에서 나온다 (화면의 10초 제한보다 먼저)

    private const string LeaveRoomJson = "{\"type\":\"LEAVE_ROOM\"}";

    private readonly PhotonConnection connection;

    private LocalLobbyHost host;                 // 내가 방장일 때만 있다. 방 하나를 가진 호스트
    private RoomSnapshot lastSnapshot;           // 마지막으로 받은 방 정보. 방장이 바뀔 때 이어받는 데 쓴다
    private bool busy;                           // 방을 만들거나 들어가는 중
    private bool leaving;                        // 방을 나가는 중 (연결을 끊고 로비로 돌아갈 때까지 계속 true)
    private bool finishing;                      // 나가기 마무리(FinishLeave)가 실행 중
    private int? pendingPortrait;                // 방에 들어가기 전에 고른 초상화
    private bool awaitingAnswer;                 // 방 만들기나 입장 요청을 보내고 방장의 답을 기다리는 중
    private readonly Queue<string> queued = new Queue<string>();   // 만드는 중에 들어온 요청 (방 설정 요청 등)
    private readonly HashSet<int> knownIds = new HashSet<int>();
    private string lastExcludedLog = "";
    private string lastListedLog = "";           // 로비 목록에 올린 방을 로그로 남긴 마지막 내용
    private readonly HashSet<int> rejoiningRefs = new HashSet<int>(); // 게임 중인 방에 돌아온 연결. 곧 오는 입장 요청을 자기 자리로 돌려보낸다
    private string lastRegistryJson;             // 방장이 나눠 준 사람 기록. 내가 방장을 이어받을 때 쓴다
    private bool lastStatusPlaying;              // 직전 방 정보가 게임 중이었는지 (게임이 끝나는 순간을 알려고)

    private bool refreshingLobby;   // 로비에 연결하는 중 (재시도 포함). 다른 연결 시도가 겹치지 않게 한다
    private bool everOnline;        // 이번에 로비를 Photon에 연결한 뒤로 한 번이라도 연결됐는지

    // 끊겨서 같은 방에 자동으로 다시 들어가는 중인지, 그리고 방장의 답
    private bool autoRejoining;
    private bool rejoiningWaitingRoom;   // 다시 들어가는 방이 게임 중이 아니라 대기방이다 (방장이 받아 주면 화면에 방을 다시 그리게 전달한다)
    private enum RejoinAnswer { Pending, Accepted, Rejected, Failed }
    private RejoinAnswer rejoinAnswer;

    private const string LastGameRoomKey = "Photon.LastGameRoom";

    /// <summary>
    /// 내가 하던 게임 방의 코드. 게임이 시작되면 기억하고, 게임이 끝나거나 일부러 방을 나가면 지운다.
    /// 앱을 껐다 켜도 남아 있어서, 로비 목록에 그 방이 "이어하기"로 보인다.
    /// </summary>
    private string LastGameRoom
    {
        get => PlayerPrefs.GetString(LastGameRoomKey, "");
        set
        {
            string next = value ?? "";
            if (PlayerPrefs.GetString(LastGameRoomKey, "") == next) return;

            PlayerPrefs.SetString(LastGameRoomKey, next);
            PlayerPrefs.Save();
        }
    }         // 방 목록에서 제외된 방을 로그로 남긴 마지막 내용 (같은 내용을 반복해 남기지 않으려고)

    // 요청을 보낸 사람의 연결. 접속자 목록(ActivePlayers)이 늦게 갱신돼도 그 사람에게 답을 보낼 수 있게 요청에서 알아 둔다.
    private readonly Dictionary<int, PlayerRef> seenRefs = new Dictionary<int, PlayerRef>();

    /// <summary>게임 로비를 Photon에 이어 둔 상태인지</summary>
    public bool IsActive { get; private set; }

    public PhotonLobbyBridge(PhotonConnection connection)
    {
        this.connection = connection;
    }

    // ───────────── 켜고 끄기 ─────────────

    /// <summary>게임 로비의 요청을 Photon으로 보내도록 바꾼다. 로비 화면에 들어가기 전에 켠다.</summary>
    public void Enable()
    {
        if (IsActive) return;
        IsActive = true;
        everOnline = false;

        LobbyNetwork.Instance.Host = this;
        connection.GameBridge.Enable(); // 게임 진행도 같이 Photon으로 보낸다
        connection.Messenger.RequestReceived += OnRequestReceived;
        connection.Messenger.NotifyReceived += OnNotifyReceived;
        connection.SessionsChanged += OnSessionsChanged;
        connection.PlayersChanged += OnPlayersChanged;
        connection.MasterChanged += OnMasterChanged;
        connection.ConnectionLost += OnConnectionLost;
        Log("게임 로비를 Photon에 연결했습니다");
    }

    /// <summary>원래대로 이 PC 안의 호스트를 쓰도록 되돌린다. 방 안이었다면 나간다.</summary>
    public async Task Disable()
    {
        if (!IsActive) return;
        IsActive = false;

        connection.Messenger.RequestReceived -= OnRequestReceived;
        connection.Messenger.NotifyReceived -= OnNotifyReceived;
        connection.SessionsChanged -= OnSessionsChanged;
        connection.PlayersChanged -= OnPlayersChanged;
        connection.MasterChanged -= OnMasterChanged;
        connection.ConnectionLost -= OnConnectionLost;
        if (LobbyNetwork.Instance.Host == this) LobbyNetwork.Instance.Host = null;
        connection.GameBridge.Disable();

        ClearRoomState();
        LocalPlayer.Set(OfflinePlayerId, LocalPlayer.Nickname);
        await connection.Disconnect();
        Log("게임 로비를 Photon에서 분리했습니다");
    }

    // ───────────── 화면의 요청 받기 (LobbyNetwork가 부른다) ─────────────

    /// <param name="senderId">화면이 적은 내 번호. Photon에서는 쓰지 않는다. (보낸 사람은 연결로 정한다)</param>
    public void OnRequest(long senderId, string json)
    {
        string type = ReadType(json);
        switch (type)
        {
            case "ROOM_LIST_REQUEST": Run(RefreshRoomList(), "방 목록"); return;
            case "CREATE_ROOM": Run(CreateRoomFlow(json), "방 만들기"); return;
            case "ENTER_ROOM": Run(EnterRoomFlow(json), "방 입장"); return;
            case "LEAVE_ROOM": Run(LeaveRoomFlow(json), "방 나가기"); return;
        }

        if (type == "SET_PORTRAIT") RememberPortrait(json);
        ForwardToMaster(type, json);
    }

    // 방 안이면 방장에게 보낸다. 방을 만들거나 들어가는 중이면 끝날 때까지 기다린다.
    private void ForwardToMaster(string type, string json)
    {
        if (busy) { queued.Enqueue(json); return; }

        if (connection.IsInSession)
        {
            connection.Messenger.SendRequest(PhotonMessenger.DomainLobby, json);
            return;
        }

        // 방 밖에서 고른 초상화는 기억해 두었다가 방에 들어갈 때 알린다. 그 밖의 요청은 방이 있어야 한다.
        if (type == "SET_PORTRAIT") return;
        DeliverError("NOT_IN_ROOM", "방에 들어가 있지 않아요");
    }

    private void RememberPortrait(string json)
    {
        try { pendingPortrait = JsonConvert.DeserializeObject<SetPortraitRequest>(json).portraitId; }
        catch (Exception e) { Log("초상화 요청을 읽지 못했습니다: " + e.Message); }
    }

    // ───────────── 방 목록 (방 밖) ─────────────

    private float nextLobbyAttemptAt;

    /// <summary>
    /// 로비 화면에 있는데 로비 연결이 없으면 스스로 연결한다. (끊긴 뒤 사용자가 아무것도 누르지 않아도 "연결 중"에서 "연결됨"으로 돌아가게)
    /// 연결하지 못하면 RefreshRoomList가 혼자 하기로 전환한다. 매 프레임 불린다.
    /// </summary>
    public void Tick()
    {
        if (!IsActive || connection.OfflineMode) return;
        if (connection.Current != PhotonConnection.ConnectionState.Disconnected) return;
        if (busy || leaving || finishing || autoRejoining || refreshingLobby || lastSnapshot != null) return; // 방 안이거나 방에 드나드는 중, 이미 연결을 시도하는 중이면 그쪽이 맡는다
        if (SceneManager.GetActiveScene().path != SceneFlow.LobbyScenePath) return;
        if (Time.unscaledTime < nextLobbyAttemptAt) return;

        nextLobbyAttemptAt = Time.unscaledTime + AutoLobbyRetrySeconds;
        Log("로비에 연결돼 있지 않아 연결합니다");
        Run(RefreshRoomList(), "로비 연결");
    }

    private async Task RefreshRoomList()
    {
        if (connection.IsInSession || busy || leaving) return; // 방 안이거나 방을 만들고 들어가고 나오는 중에는 목록을 보내지 않는다

        // 연결이 바뀌는 도중이면 오류를 띄우지 않고 건너뛴다. 로비에 다시 들어가면 방 목록이 알아서 화면에 간다.
        if (connection.Current == PhotonConnection.ConnectionState.StartingSession || connection.Current == PhotonConnection.ConnectionState.InSession) return;

        if (refreshingLobby) return; // 이미 로비에 연결하는 중이다. 연결되면 그 쪽이 방 목록을 화면에 보낸다.

        refreshingLobby = true;
        try
        {
            // 연결되기까지 시간이 걸리거나 한 번 실패해도 곧바로 혼자 하기로 넘기지 않고, 정해 둔 시간 동안 다시 해 본다.
            if (!await EnsureLobbyWithRetry(RejoinRules.LobbyGiveUpSeconds(everOnline, LobbyFirstConnectSeconds, LobbyGiveUpSeconds)))
            {
                await FallbackToLocal("로비에 연결하지 못했습니다");
                return;
            }
        }
        finally { refreshingLobby = false; }

        SendRoomListToScreen();
    }

    // 로비에 연결한다. 한 번에 안 되면 정리하고 다시 해 본다. giveUpSeconds 안에 연결되면 true.
    // 웹에서 탭이 뒤로 갔다 돌아온 직후처럼 잠깐 느린 것을 곧바로 "연결 불가"로 보지 않으려고 쓴다.
    private async Task<bool> EnsureLobbyWithRetry(float giveUpSeconds)
    {
        RejoinResult result = await PhotonRejoinLoop.RunAsync(
            async attempt =>
            {
                if (attempt > 1) Log($"로비에 다시 연결해 봅니다 ({attempt}번째)");
                return await EnsureLobby(LobbyAttemptSeconds) ? RejoinAttempt.Success : RejoinAttempt.Retry;
            },
            () => Time.unscaledTime, WaitUntil, () => IsActive, giveUpSeconds, LobbyRetryGapSeconds);

        if (result != RejoinResult.Success) return false;

        everOnline = true;
        return true;
    }

    // 로비에 있게 만든다. 이미 있으면 그대로, 들어가는 중이면 기다리고, 끊겨 있으면 들어간다.
    // 연결이 오래 걸리면 포기하고 false를 돌려준다. (인터넷이 없을 때 화면이 10초 뒤 "서버 응답이 없어요"만 띄우지 않게)
    private async Task<bool> EnsureLobby(float waitSeconds = LobbyConnectSeconds)
    {
        float until = Time.unscaledTime + waitSeconds;

        // 웹(WebGL)에서는 Task.Delay가 동작하지 않아서 프레임마다 확인한다
        while (connection.Current == PhotonConnection.ConnectionState.JoiningLobby && Time.unscaledTime < until)
            await Task.Yield();

        if (connection.Current == PhotonConnection.ConnectionState.InLobby) return true;
        if (connection.Current != PhotonConnection.ConnectionState.Disconnected) return false;

        Task<bool> join = connection.JoinLobby();
        while (!join.IsCompleted && Time.unscaledTime < until)
            await Task.Yield();

        if (join.IsCompleted) return join.Result;

        Log($"로비 연결이 {waitSeconds:0}초 안에 되지 않았습니다");
        _ = connection.Disconnect(); // 늦게 연결되더라도 쓰지 않는다
        return false;
    }

    // Photon에 연결하지 못했을 때, 이 PC 안의 호스트를 쓰는 혼자 하기로 전환한다. (v1.0.1과 같은 동작)
    // 화면이 이미 보낸 요청(방 만들기 등)은 이 PC의 호스트에게 다시 보내고, 전환했다는 안내는 그 뒤에 보낸다.
    // (안내를 먼저 보내면 화면이 "입장 중" 상태를 끝내 버려서 뒤따르는 방 입장 응답과 겹친다)
    private async Task FallbackToLocal(string reason, List<string> replay = null)
    {
        Log($"온라인에 연결하지 못해 이 PC에서 혼자 하기로 전환합니다 ({reason})");

        connection.OfflineMode = true; // 타이틀로 돌아갈 때까지 다시 시도하지 않는다
        await Disable();               // 이 PC 안의 호스트를 쓰도록 되돌린다. (내 번호도 원래대로)

        if (replay != null && replay.Count > 0)
        {
            foreach (string json in replay)
                LocalLobbyHost.Instance.OnRequest(LocalPlayer.Id, json);
        }
        else
        {
            LobbyNetwork.Instance.SendRoomListRequest();
        }

        LobbyNetwork.Instance.ReceiveLobbyMessage(LobbyMessageType.LOBBY_ERROR, ErrorJson("OFFLINE_MODE", "온라인에 연결하지 못해 혼자 하기로 전환했어요"));
    }

    private void OnSessionsChanged()
    {
        // 방 밖에서 로비에 있는 동안은 목록이 바뀔 때마다 화면에 밀어 준다
        if (IsActive && connection.Current == PhotonConnection.ConnectionState.InLobby) SendRoomListToScreen();
    }

    // 방 목록에 올리지 않은 방과 그 이유를 로그로 남긴다. (방이 안 보일 때 이유를 바로 알 수 있게. 내용이 바뀔 때만 남긴다)
    private void LogExcludedSessions()
    {
        var text = new StringBuilder();
        foreach (SessionInfo info in connection.Sessions)
        {
            if (IsListable(info)) continue;
            text.Append($"{PhotonConnection.FormatCode(info.Name)}(열림 {info.IsOpen}, 호스트 표시 {IntProp(info, KeyHosted, 0)}, 상태 {StringProp(info, KeyState, "?")}, 인원 {info.PlayerCount}/{info.MaxPlayers}) ");
        }

        string now = text.ToString();
        if (now == lastExcludedLog) return;

        lastExcludedLog = now;
        if (now.Length > 0) Log("방 목록에서 제외된 방: " + now);
    }

    // 로비 목록에 올린 방과 그 인원을 로그로 남긴다. (내용이 바뀔 때만) 방장이 바꾼 인원이 로비에 반영되는지 볼 수 있다.
    private void LogListedRooms(List<RoomSummary> list)
    {
        string now = string.Join(" ", list.Select(r => $"{PhotonConnection.FormatCode(r.roomId)}({r.playerCount}/{r.maxPlayers}명, 맵 {r.mapId})"));
        if (now == lastListedLog) return;

        lastListedLog = now;
        Log("방 목록: " + (now.Length > 0 ? now : "없음"));
    }

    /// <summary>방 목록에 보일 인원. 게임이 정한 값(현재 인원/최대 인원)이고, 없으면 Photon이 세는 값이다.</summary>
    public static string DescribeCapacity(SessionInfo info) =>
        $"{IntProp(info, KeyCount, info.PlayerCount)}/{IntProp(info, KeyMax, info.MaxPlayers)}";

    private void SendRoomListToScreen()
    {
        LogExcludedSessions();

        List<RoomSummary> list = connection.Sessions.Where(IsListable).Select(ToSummary).ToList();
        LogListedRooms(list);
        string json = JsonConvert.SerializeObject(new RoomListResponse { type = "ROOM_LIST", rooms = list });
        LobbyNetwork.Instance.ReceiveLobbyMessage(LobbyMessageType.ROOM_LIST, json);
    }

    // 가득 찬 방(Photon이 닫아 둠), 호스트가 없는 방은 목록에 올리지 않는다.
    // 호스트가 없는 방은 들어가도 요청에 답해 줄 PC가 없다. (테스트 창의 만들기로 만든 방, 로비 연결을 켜지 않은 PC가 만든 방)
    // 게임 중인 방은 남에게는 보이지 않고, 내가 하던 게임(마지막 게임 방)만 "이어하기"로 보인다.
    // 게임 중에도 방은 열어 둔다. (닫힌 방에는 돌아오는 사람도 못 들어온다. 참가자가 아닌 사람은 방장이 들여보내지 않는다)
    private bool IsListable(SessionInfo info)
    {
        if (!info.IsOpen || IntProp(info, KeyHosted, 0) != 1) return false;

        string state = StringProp(info, KeyState, StateWaiting);
        if (state == StateWaiting) return true;

        string mine = LastGameRoom;
        return state == StatePlaying && !string.IsNullOrEmpty(mine) && info.Name == mine;
    }

    private static RoomSummary ToSummary(SessionInfo info) => new RoomSummary
    {
        roomId = info.Name,
        roomName = (StringProp(info, KeyState, StateWaiting) == StatePlaying ? "[이어하기] " : "") + StringProp(info, KeyName, ""),
        hostNickname = (StringProp(info, KeyState, StateWaiting) == StatePlaying ? "[이어하기] " : "") + StringProp(info, KeyHost, ""),
        memberNicknames = DecodeMembers(info, out List<bool> memberBots),
        memberBots = memberBots,
        mapId = IntProp(info, KeyMap, 0),
        playerCount = IntProp(info, KeyCount, info.PlayerCount),
        maxPlayers = IntProp(info, KeyMax, PhotonConnection.MaxHumansLimit),
    };

    // 방 속성의 참가자 명단을 꺼낸다. 명단이 없는 방(이름 표시가 없던 옛 빌드가 만든 방)은 빈 목록이고, 화면이 예전 방식으로 보여 준다.
    private static List<string> DecodeMembers(SessionInfo info, out List<bool> bots)
    {
        RoomMemberCodec.Decode(StringProp(info, KeyMembers, ""), out List<string> names, out bots);
        return names;
    }

    private static string StringProp(SessionInfo info, string key, string fallback)
    {
        if (info.Properties != null && info.Properties.TryGetValue(key, out SessionProperty property) && property.IsString)
            return (string)property;
        return fallback;
    }

    private static int IntProp(SessionInfo info, string key, int fallback)
    {
        if (info.Properties != null && info.Properties.TryGetValue(key, out SessionProperty property) && property.IsInt)
            return (int)property;
        return fallback;
    }

    // ───────────── 방 만들기, 입장, 나가기 (화면 쪽) ─────────────

    private async Task CreateRoomFlow(string json)
    {
        if (busy || !await RecoverIfStale()) { DeliverError("ALREADY_IN_ROOM", "이미 방에 들어가 있어요"); return; }

        busy = true;
        try
        {
            // 화면은 방 만들기 요청 바로 뒤에 방 설정(인원, 맵)을 보낸다. 한 프레임 기다려서 그 요청을 받아 둔다.
            await Task.Yield();

            if (!await EnsureLobby())
            {
                var replay = new List<string> { json };
                replay.AddRange(queued);
                queued.Clear();
                await FallbackToLocal("로비에 연결하지 못했습니다", replay);
                return;
            }

            // 방 이름은 방장이 정한 이름이고, 비우면 만든 사람의 이름으로 정한다. (호스트도 같은 요청으로 같은 이름을 저장한다)
            // 방 목록에 보일 속성(방장, 인원, 호스트 표시 등)은 방을 만들 때 함께 정해야 다른 PC의 방 목록에 보인다.
            // 화면이 고른 인원과 맵을 처음부터 반영한다. (Photon 방의 최대 인원은 만든 뒤에 못 바꿔서 4로 두고, 실제 자리는 게임 규칙이 막는다)
            string roomName = null;
            try { roomName = RoomNames.Sanitize(JsonConvert.DeserializeObject<CreateRoomRequest>(json)?.roomName); }
            catch (Exception e) { Log("방 만들기 요청의 이름을 읽지 못해 기본 이름을 씁니다: " + e.Message); }

            if (!await connection.CreateRoom(roomName ?? RoomNames.Default(LocalPlayer.Nickname), PhotonConnection.MaxHumansLimit, InitialRoomProperties(PeekRoomConfig())))
            {
                DeliverError("CREATE_FAILED", "방을 만들지 못했어요");
                return;
            }

            // 방을 만든 사람이 방장이다. 호스트를 시작하고 평소처럼 요청을 보낸다. (내 요청은 네트워크를 타지 않고 내 호스트로 간다)
            OnSessionJoined();
            StartHost();
            SendHello();
            connection.Messenger.SendRequest(PhotonMessenger.DomainLobby, json);
            Log("방 만들기 요청을 호스트로 보냈습니다");
            FlushQueue();
        }
        finally
        {
            busy = false;
            if (!connection.IsInSession) queued.Clear(); // 실패했으면 그 방을 향한 요청도 버린다
        }
    }

    private async Task EnterRoomFlow(string json)
    {
        if (busy || !await RecoverIfStale()) { DeliverError("ALREADY_IN_ROOM", "이미 방에 들어가 있어요"); return; }

        busy = true;
        try
        {
            string roomId;
            try { roomId = JsonConvert.DeserializeObject<EnterRoomRequest>(json).roomId; }
            catch (Exception e) { Log("입장 요청을 읽지 못했습니다: " + e.Message); DeliverError("BAD_REQUEST", "요청을 처리하지 못했어요"); return; }

            if (!await EnsureLobby())
            {
                await FallbackToLocal("로비에 연결하지 못했습니다");
                return;
            }

            // 로비에 방금 다시 연결했다면 방 목록이 오기까지 잠깐 걸린다. 고른 방이 목록에 나타날 때까지 잠깐 기다린다.
            float listUntil = Time.unscaledTime + RoomListWaitSeconds;
            while (!connection.Sessions.Any(session => session.Name == roomId) && Time.unscaledTime < listUntil)
                await Task.Yield();

            SessionInfo target = connection.Sessions.FirstOrDefault(session => session.Name == roomId);
            if (target != null && IntProp(target, KeyHosted, 0) != 1)
            {
                Log("호스트가 없는 방이라서 들어가지 않습니다: " + roomId);
                DeliverError("NO_HOST", "이 방은 게임 로비에서 만든 방이 아니라서 들어갈 수 없어요");
                return;
            }

            // 방 목록에 있는 방은 목록의 정보로 들어가고, 목록에 없는 방은 코드로 들어간다.
            // (방 코드를 직접 입력해서 입장할 때, 방 목록이 아직 오지 않았거나 목록에 보이지 않는 방)
            bool joined = target != null ? await connection.JoinRoom(roomId) : await connection.JoinRoomByCode(roomId);
            if (!joined)
            {
                bool noSuchCode = target == null && connection.LastJoinRoomMissing;
                DeliverError("ROOM_NOT_FOUND", noSuchCode ? "그 코드의 방이 없어요. 코드를 다시 확인해 주세요" : "방이 사라졌거나 들어갈 수 없어요");
                return;
            }

            OnSessionJoined();

            // 방에 막 들어온 직후에는 방장과 서로 연결되기 전이라서, 지금 요청을 보내면 방장에게 닿지 않을 수 있다.
            // 방장이 답(퐁)을 줄 때까지 확인한 뒤에 요청을 보낸다.
            Log("방장과 연결을 확인하는 중...");
            float handshakeStart = Time.unscaledTime;
            bool answered = await connection.Messenger.WaitForMaster(HandshakeSeconds);
            if (!connection.IsInSession)
            {
                Log("방장과 연결을 확인하는 중에 연결이 끊어졌습니다");
                DeliverError("HOST_NO_RESPONSE", "방장 PC와 연결하지 못했어요. 잠시 후 다시 시도해 주세요");
                return;
            }
            Log(answered
                ? $"방장과 연결이 확인되었습니다 ({Time.unscaledTime - handshakeStart:0.0}초)"
                : $"방장의 퐁이 {HandshakeSeconds}초 안에 오지 않았지만 요청을 보냅니다. (방장 PC가 같은 코드 버전({PhotonConnection.CodeRevision})으로 빌드됐는지 확인하세요)");

            SendHello();
            connection.Messenger.SendRequest(PhotonMessenger.DomainLobby, json); // 방장의 호스트가 나를 방에 넣는다
            Log($"방 입장 요청을 방장({connection.MasterPlayerRef.PlayerId}번)에게 보냈습니다");
            FlushQueue();

            awaitingAnswer = true;
            Run(WatchAnswer(), "방장의 답 확인");
        }
        finally
        {
            busy = false;
            if (!connection.IsInSession) queued.Clear();
        }
    }

    private async Task LeaveRoomFlow(string json)
    {
        if (busy) { DeliverError("NOT_IN_ROOM", "방에 들어가 있지 않아요"); return; }

        if (!connection.IsInSession)
        {
            // 연결은 이미 없는데 화면은 방에 있다고 생각한다. 방장에게 알릴 길이 없으니 이쪽에서 정리하고 화면을 로비로 보낸다.
            if (lastSnapshot != null || host != null) await HandleRoomConnectionLost(allowRejoin: false); // 나가려는 것이므로 다시 들어가지 않는다
            else DeliverError("NOT_IN_ROOM", "방에 들어가 있지 않아요");
            return;
        }

        leaving = true;
        connection.Messenger.SendRequest(PhotonMessenger.DomainLobby, json); // 방장이 ROOM_LEFT를 돌려주고 남은 사람들에게 알린다

        // ROOM_LEFT가 오면 OnNotifyReceived가 정리한다. 오지 않을 때를 대비해 시간 제한을 둔다.
        float until = Time.unscaledTime + LeaveWaitSeconds;
        while (leaving && Time.unscaledTime < until) await Task.Yield();

        if (leaving && !finishing) await FinishLeave();
    }

    // 방에서 나온 뒤: 연결을 끊고 다시 로비로 돌아가 방 목록을 본다.
    // 끝날 때까지 leaving을 true로 두어서, 그 사이에 화면이 보내는 방 목록 요청이 오류를 띄우지 않게 한다.
    private async Task FinishLeave()
    {
        if (!leaving || finishing) return;
        finishing = true;

        try
        {
            // 방장이 나가면서 보낸 "새 방장" 알림이 끊기기 전에 나가도록 잠깐 기다린다. (바로 끊으면 남은 사람이 못 받을 수 있다)
            if (host != null)
            {
                float until = Time.unscaledTime + MasterLeaveFlushSeconds;
                while (Time.unscaledTime < until) await Task.Yield();
            }

            ClearRoomState();
            LocalPlayer.Set(0, LocalPlayer.Nickname);

            await connection.Disconnect();
            if (IsActive) await connection.JoinLobby();
        }
        finally
        {
            leaving = false;
            finishing = false;
        }
    }

    // 입장 요청을 보낸 뒤 방장의 답이 오는지 지켜본다.
    // 늦으면 화면의 기본 문구("서버 응답이 없어요")가 뜨기 전에 이유를 알려 주고, 응답 없는 방에서 나온다. (연결만 방에 남지 않게)
    private async Task WatchAnswer()
    {
        float until = Time.unscaledTime + AnswerWarnSeconds;
        while (awaitingAnswer && Time.unscaledTime < until) await Task.Yield();

        if (!awaitingAnswer) return;
        awaitingAnswer = false;

        Log($"방장의 답이 {AnswerWarnSeconds}초 넘게 오지 않습니다. 방장 PC에 로비 호스트(서버 역할)가 없거나 응답하지 않는 것입니다. 방에서 나옵니다");
        DeliverError("HOST_NO_RESPONSE", "방장 PC가 응답하지 않아요. 방장이 게임 로비에서 만든 방인지 확인해 주세요");

        leaving = true;
        await FinishLeave();
    }

    // 화면은 방에 없는데 Photon 연결만 방 안에 남아 있는 어긋난 상태를 정리한다. (방 입장 답이 오지 않아 화면이 포기한 경우 등)
    // 정말 방에 있으면(방 정보를 받은 적이 있으면) false를 돌려준다.
    private async Task<bool> RecoverIfStale()
    {
        if (!connection.IsInSession) return true;
        if (lastSnapshot != null) return false;

        Log("화면은 방에 없는데 연결이 방 안에 남아 있어서 정리합니다");
        ClearRoomState();
        LocalPlayer.Set(0, LocalPlayer.Nickname);
        await connection.Disconnect();
        return true;
    }

    private void OnSessionJoined()
    {
        // 방에 들어온 뒤부터 내 번호는 Photon이 붙여 준 번호다
        everOnline = true;
        LocalPlayer.Set(connection.LocalPlayerRef.PlayerId, LocalPlayer.Nickname);
        knownIds.Clear();
        foreach (PlayerRef player in connection.Players) knownIds.Add(player.PlayerId);
    }

    // 닉네임과 초상화를 방장에게 먼저 알린다. 요청 JSON에는 없는 값이라서, 입장 요청보다 앞서 보낸다. (보내는 순서가 지켜진다)
    private void SendHello()
    {
        var hello = new JObject { ["type"] = "PLAYER_HELLO", ["nickname"] = LocalPlayer.Nickname };
        if (pendingPortrait.HasValue) hello["portraitId"] = pendingPortrait.Value;

        connection.Messenger.SendRequest(PhotonMessenger.DomainLobby, hello.ToString(Formatting.None));
    }

    private void FlushQueue()
    {
        while (queued.Count > 0)
            connection.Messenger.SendRequest(PhotonMessenger.DomainLobby, queued.Dequeue());
    }

    private void ClearRoomState()
    {
        if (host != null) host.RoomUpdated -= OnHostRoomUpdated;
        if (host != null) LocalLobbyHost.Reset();

        connection.GameBridge.Reset();
        connection.Registry.Clear();
        host = null;
        lastSnapshot = null;
        lastRegistryJson = null;
        lastStatusPlaying = false;
        rejoiningRefs.Clear();
        busy = false;
        awaitingAnswer = false;
        queued.Clear();
        knownIds.Clear();
        seenRefs.Clear();
    }

    // ───────────── 방장 쪽: 요청을 받아 호스트에게 넘기기 ─────────────

    private void StartHost()
    {
        host = new LocalLobbyHost(Deliver) { RoomIdFactory = () => connection.RoomCode };
        host.RoomUpdated += OnHostRoomUpdated;
        LocalLobbyHost.Use(host); // 게임 호스트(HostGameService)도 같은 호스트를 본다
    }

    private void OnRequestReceived(PlayerRef sender, int domain, string json)
    {
        if (!IsActive || domain != PhotonMessenger.DomainLobby) return;

        seenRefs[sender.PlayerId] = sender;
        string type = ReadType(json);
        Log($"요청 받음: {type} (보낸 사람 {sender.PlayerId}번)");

        if (host == null)
        {
            // 방장이 아니거나, 아직 호스트가 없다. (방이 만들어지기 전에 온 요청 등)
            Log($"로비 요청을 받았지만 호스트가 없습니다 (보낸 사람 {sender.PlayerId}번)");
            DeliverTo(sender, LobbyMessageType.LOBBY_ERROR, ErrorJson("ROOM_NOT_FOUND", "방이 사라졌어요"));
            return;
        }

        // 방과 게임은 사람을 게임 번호로 구분한다. 처음 온 사람은 연결 번호와 같고, 끊겼다 돌아온 사람은 예전 번호다.
        long senderId = ResolveSender(sender);

        if (type == "PLAYER_HELLO")
        {
            try
            {
                JObject hello = JObject.Parse(json);
                host.RegisterPlayer(senderId, (string)hello["nickname"], (int?)hello["portraitId"]);
            }
            catch (Exception e) { Log("PLAYER_HELLO를 읽지 못했습니다: " + e.Message); }
            return;
        }

        // 게임 중인 방으로 돌아온 사람: 처음부터 입장시키지 않고, 자기 자리로 다시 들여보낸다
        if (type == "ENTER_ROOM" && rejoiningRefs.Remove(sender.PlayerId))
        {
            host.RejoinPlayingRoom(senderId);
            return;
        }

        host.OnRequest(senderId, json);
    }

    // 연결 번호의 게임 번호를 정한다. 처음 보는 연결이면 계정(PlayFab ID)으로 예전에 이 게임에 있던 사람인지 확인한다.
    private long ResolveSender(PlayerRef sender)
    {
        PlayerRegistry registry = connection.Registry;
        if (registry.TryGetGameId(sender.PlayerId, out long known)) return known;

        string account = connection.GetUserId(sender);
        long gameId = registry.Resolve(sender.PlayerId, account, id => host != null && host.IsMemberOfPlayingRoom(id), out bool returning);

        if (returning)
        {
            rejoiningRefs.Add(sender.PlayerId);
            Log($"게임 중이던 {gameId}번 자리로 돌아온 사람입니다 (연결 {sender.PlayerId}번, 계정 {account})");
        }
        else
        {
            Log($"새 연결을 기록했습니다: 연결 {sender.PlayerId}번 = 게임 {gameId}번 (계정 {(string.IsNullOrEmpty(account) ? "알 수 없음" : account)})");
        }

        // 그 사람에게 자기 게임 번호를 알린다. 화면이 입장 응답을 받기 전에 번호를 맞춰 두려는 것이다.
        DeliverRaw(sender, "ASSIGN_ID", JsonConvert.SerializeObject(new { playerId = gameId }));
        BroadcastRegistry();
        return gameId;
    }

    // 방장이 바뀌어도 새 방장이 사람 기록을 이어받을 수 있게, 기록이 바뀔 때마다 모두에게 나눠 준다.
    private void BroadcastRegistry()
    {
        string json = connection.Registry.Export();
        foreach (int refId in connection.Registry.ConnectedRefs())
            DeliverRaw(connection.FindPlayerRef(refId), "REGISTRY", json);
    }

    // 호스트가 보내는 알림을 Photon으로 내보낸다. 봇(1001번부터)이나 이미 나간 사람에게는 보내지 않는다.
    private void Deliver(long playerId, LobbyMessageType type, string json)
    {
        // 이 PC의 호스트가 게임을 시작시켰다. 이 PC의 게임 호스트가 새 게임을 처음부터 만들어도 된다는 표시다.
        if (type == LobbyMessageType.GAME_STARTED) connection.GameBridge.OnLocalHostStartedGame();

        if (!TryFindPlayer(playerId, out PlayerRef target))
        {
            if (playerId < 1000) Log($"알림을 보낼 사람을 찾지 못했습니다: {type} → {playerId}번"); // 1000번 이상은 봇이라 받을 필요가 없다
            return;
        }

        Log($"알림 보냄: {type} → {playerId}번");
        DeliverTo(target, type, json);
    }

    // 로비 알림 종류에 없는 방 안의 내부 메시지(ASSIGN_ID, REGISTRY)를 보낸다
    private void DeliverRaw(PlayerRef target, string typeName, string json)
    {
        connection.Messenger.SendNotify(target, PhotonMessenger.DomainLobby, typeName, json);
    }

    private void DeliverTo(PlayerRef target, LobbyMessageType type, string json)
    {
        connection.Messenger.SendNotify(target, PhotonMessenger.DomainLobby, type.ToString(), json);
    }

    private bool TryFindPlayer(long playerId, out PlayerRef found)
    {
        // 게임 번호의 사람이 지금 어느 연결로 들어와 있는지는 사람 기록이 안다
        if (connection.Registry.TryGetRef(playerId, out int refId))
        {
            found = connection.FindPlayerRef(refId);
            return true;
        }

        foreach (PlayerRef player in connection.Players)
        {
            if (player.PlayerId == playerId) { found = player; return true; }
        }

        // 접속자 목록에 아직 없어도, 방금 요청을 보낸 사람이면 그 연결로 보낸다
        return seenRefs.TryGetValue((int)playerId, out found);
    }

    // 방을 만들 때 Photon에 함께 넘길 방 목록용 속성. 이후 방 정보가 바뀔 때 OnHostRoomUpdated가 같은 이름으로 값을 갱신한다.
    // (Photon은 방 목록에 방을 만들 때 정한 속성 이름만 보여 준다. 나중에 새 이름을 더해도 다른 PC에는 안 보인다)
    // 보관 중인 요청에서 화면이 보낸 방 설정(인원, 맵, 봇 수)을 찾는다. 없으면 null.
    private RoomConfigRequest PeekRoomConfig()
    {
        foreach (string json in queued)
        {
            if (ReadType(json) != "ROOM_CONFIG_REQUEST") continue;
            try { return JsonConvert.DeserializeObject<RoomConfigRequest>(json); }
            catch (Exception e) { Log("방 설정 요청을 읽지 못했습니다: " + e.Message); }
        }
        return null;
    }

    private static Dictionary<string, SessionProperty> InitialRoomProperties(RoomConfigRequest config)
    {
        int max = config != null ? config.maxPlayers : PhotonConnection.MaxHumansLimit;
        int bots = config != null ? config.botCount : 0;
        int map = config != null && config.config != null ? config.config.mapId : 0;

        return new Dictionary<string, SessionProperty>
        {
            [KeyHost] = LocalPlayer.Nickname,
            [KeyMembers] = RoomMemberCodec.Encode(new[] { LocalPlayer.Nickname }), // 나중에 갱신할 속성은 만들 때 이름을 정해 두어야 목록에 보인다
            [KeyMap] = map,
            [KeyCount] = 1 + bots,
            [KeyMax] = max,
            [KeyHosted] = 1,
        };
    }

    // 방의 상태가 바뀔 때마다 방 목록에 보일 값(방장 이름, 인원, 상태)과 열림 여부를 Photon 방에 반영한다
    private void OnHostRoomUpdated(RoomSnapshot room) => ApplyRoomToSession(room, false);

    private void ApplyRoomToSession(RoomSnapshot room, bool forceOpen)
    {
        lastSnapshot = room;

        int humans = room.Players.Count(p => !p.bot);
        bool waiting = room.status == RoomStatus.WAITING;
        bool gameJustFinished = waiting && lastStatusPlaying;
        lastStatusPlaying = !waiting;
        PlayerSnapshot hostPlayer = room.Players.FirstOrDefault(p => p.playerId == room.hostId);

        // 방장이 바뀌어도 방 이름은 그대로다. (방 이름은 방을 만들 때 방 정보에 저장돼 있다)
        // 참가자 명단은 방장이 맨 앞, 그다음 다른 사람, 봇은 맨 뒤다.
        List<PlayerSnapshot> ordered = room.Players.Where(p => p.playerId == room.hostId)
            .Concat(room.Players.Where(p => p.playerId != room.hostId && !p.bot))
            .Concat(room.Players.Where(p => p.playerId != room.hostId && p.bot))
            .ToList();

        var properties = new Dictionary<string, SessionProperty>
        {
            [KeyName] = RoomNames.Resolve(room.roomName, hostPlayer?.nickname),
            [KeyMembers] = RoomMemberCodec.Encode(ordered.Select(p => p.nickname).ToList(), ordered.Select(p => p.bot).ToList()),
            [KeyState] = waiting ? StateWaiting : StatePlaying,
            [KeyHost] = hostPlayer != null ? hostPlayer.nickname : "",
            [KeyMap] = room.mapId,
            [KeyCount] = room.Players.Count,
            [KeyMax] = room.maxPlayers,
            [KeyHosted] = 1,
        };
        connection.UpdateRoomProperties(properties);
        Log($"방 속성 갱신: 사람 {humans}명, 전체 {room.Players.Count}/{room.maxPlayers}명, 맵 {room.mapId}, 상태 {(waiting ? "대기" : "게임 중")}");

        // 대기 중에는 들어올 수 있는 사람 수(최대 인원에서 봇을 뺀 만큼)가 다 차면 닫는다.
        // 게임 중에는 닫지 않는다. 닫힌 방에는 연결이 끊겼다 돌아오는 참가자도 못 들어온다. (참가자가 아닌 사람은 방장이 들여보내지 않는다)
        bool open = !waiting || humans < room.maxPlayers - room.botCount;
        connection.SetRoomOpen(open, forceOpen || !waiting); // 게임 중에는 지금 값이 열림으로 보여도 다시 열어 둔다. (돌아오는 사람이 못 들어오면 안 된다)
        if (forceOpen) Log($"방 정보를 Photon 방에 반영했습니다 (사람 {humans}명, 자리 {room.maxPlayers - room.botCount}개, 상태 {(waiting ? "대기" : "게임 중")}, 열림 {open})");

        // 게임 중에는 연결이 끊긴 사람의 자리도 남겨 두었다. 게임이 끝나면 돌아오지 않은 사람은 방에서 뺀다.
        if (gameJustFinished && host != null && connection.IsMaster) RemoveDisconnectedMembers(room);
    }

    private void RemoveDisconnectedMembers(RoomSnapshot room)
    {
        foreach (PlayerSnapshot member in room.Players.Where(p => !p.bot).ToList())
        {
            if (member.playerId == LocalPlayer.Id) continue;
            if (connection.Registry.TryGetRef(member.playerId, out int refId) && connection.IsPlayerPresent(refId)) continue;
            if (!host.IsMember(member.playerId)) continue;

            Log($"게임 중 연결이 끊긴 {member.playerId}번을 방에서 뺍니다");
            host.OnRequest(member.playerId, LeaveRoomJson);
        }
    }

    // ───────────── 타이틀 화면으로 돌아갈 때 ─────────────

    /// <summary>
    /// 타이틀 화면으로 돌아갈 때 방과 연결을 정리한다. 방에 그대로 남아 있으면 다른 사람들이 내가 나간 것을 알 수 없고,
    /// 내가 방장이면 게임을 이어받을 새 방장도 정해지지 않는다. 이미 연결이 없으면 아무것도 하지 않는다.
    /// </summary>
    public async Task LeaveForLogin()
    {
        if (!IsActive || connection.Current == PhotonConnection.ConnectionState.Disconnected) return;

        Log("타이틀로 돌아가서 방과 연결을 정리합니다");
        ClearRoomState();
        await connection.Disconnect();
    }

    // ───────────── 게임 화면에서 나가기 ─────────────

    /// <summary>
    /// 게임 화면에서 나가기를 눌렀을 때 방과 연결을 정리한다. (SceneFlow.LeaveGameToLobby가 부른다)
    /// 방장에게 나간다고 알리지 않고 연결만 끊는다. 방장은 연결이 끊긴 사람처럼 자리를 남기고 AFK로 대신 진행하고,
    /// 내가 방장이었다면 남은 사람 중 한 명이 이어받는다. (기획서 "사람 구분과 재접속", "방장 승계")
    /// keepResume이면 방 코드를 남겨 로비에서 이어하기로 돌아올 수 있고, 아니면 지운다. (파산한 사람은 돌아올 수 없다)
    /// </summary>
    public async Task LeaveGame(bool keepResume)
    {
        if (!IsActive) return;

        string roomCode = lastSnapshot != null ? lastSnapshot.roomId : null;
        if (!keepResume) LastGameRoom = null;
        else if (!string.IsNullOrEmpty(roomCode)) LastGameRoom = roomCode;

        Log(keepResume ? "게임에서 나갑니다. 이어하기로 돌아올 수 있게 방 코드를 남깁니다" : "게임에서 나갑니다. 이 게임에는 다시 들어오지 않습니다");

        leaving = true; // 끊기는 동안 OnConnectionLost가 끊김으로 보고 다시 들어가지 않게 한다
        try
        {
            ClearRoomState();
            LocalPlayer.Set(0, LocalPlayer.Nickname);
            if (connection.IsInSession) await connection.Disconnect();
        }
        finally
        {
            leaving = false;
        }
    }

    // ───────────── 연결이 예기치 않게 끊겼을 때 ─────────────

    private void OnConnectionLost(PhotonConnection.ConnectionState previous, ShutdownReason reason)
    {
        if (!IsActive || leaving || finishing || autoRejoining) return; // 우리가 나가는 중이거나 이미 다시 들어가는 중이면 이미 처리하고 있다

        if (previous == PhotonConnection.ConnectionState.InLobby)
        {
            // 방 밖이면 화면에 남은 방 목록이 낡았을 뿐이다. 다음에 무언가 누를 때 EnsureLobby가 다시 연결하고, 안 되면 혼자 하기로 전환한다.
            Log($"로비 연결이 끊어졌습니다 ({reason}). 다음 요청 때 다시 연결합니다");
            return;
        }

        Log($"방 연결이 끊어졌습니다 ({reason})");
        Run(HandleRoomConnectionLost(), "연결 끊김 처리");
    }

    // 방 안에서 연결이 끊겼을 때: 방 정보를 정리하고 화면을 방에서 내보낸다.
    // allowRejoin: 대기방이면 같은 방에 다시 들어가 보기. 사용자가 일부러 나가려는 경우(연결이 이미 없는 채 나가기를 누름)는 false다.
    private async Task HandleRoomConnectionLost(bool allowRejoin = true)
    {
        bool inGame = SceneManager.GetActiveScene().path == SceneFlow.GameScenePath;
        string roomCode = lastSnapshot != null ? lastSnapshot.roomId : LastGameRoom; // 정리하기 전에 방 코드를 기억해 둔다
        bool roomWasPlaying = lastSnapshot != null && lastSnapshot.status == RoomStatus.PLAYING;

        ClearRoomState();

        if (inGame && !string.IsNullOrEmpty(roomCode))
        {
            // 게임 중에 끊기면 게임 화면에 그대로 머물면서 자동으로 다시 들어가 본다.
            // 들어가면 현재 게임 상태를 받아 이어지고, 안 되면 로비로 보낸다. (내 번호는 그대로 둔다. 게임 화면이 이 번호로 나를 찾는다)
            await AutoRejoinFlow(roomCode);
            return;
        }

        LocalPlayer.Set(0, LocalPlayer.Nickname);

        if (inGame)
        {
            // 돌아갈 방 코드를 모르면 로비로 보낸다.
            Log("게임 중 연결이 끊겼지만 돌아갈 방을 몰라 로비로 돌아갑니다");
            LobbyNetwork.Instance.ReceiveLobbyMessage(LobbyMessageType.LOBBY_ERROR, ErrorJson("CONNECTION_LOST", "연결이 끊어졌어요"));
            SceneFlow.ToLobby();
            return;
        }

        // 대기방에서 끊겼다 (웹에서 탭이 뒤로 갔다 돌아온 경우 등). 로비로 나오지 않고 같은 방에 다시 들어가 본다.
        // 방이 이미 사라졌거나 들어갈 수 없으면 아래처럼 로비로 나온다.
        if (allowRejoin && RejoinRules.ShouldRejoinWaitingRoom(inGame, !string.IsNullOrEmpty(roomCode), roomWasPlaying))
        {
            await WaitingRoomRejoinFlow(roomCode);
            return;
        }

        // 화면이 방에서 나오게 하고 안내한 뒤, 로비에 다시 연결해서 방 목록을 본다.
        await LeaveToLobbyAfterLoss("연결이 끊어져 방에서 나왔어요");
    }

    // 화면을 방에서 내보내고 안내한 뒤 로비에 다시 연결한다.
    private async Task LeaveToLobbyAfterLoss(string message)
    {
        LobbyNetwork.Instance.ReceiveLobbyMessage(LobbyMessageType.ROOM_LEFT, JsonConvert.SerializeObject(new RoomLeftResponse { type = "ROOM_LEFT" }));
        LobbyNetwork.Instance.ReceiveLobbyMessage(LobbyMessageType.LOBBY_ERROR, ErrorJson("CONNECTION_LOST", message));
        await RefreshRoomList();
    }

    // ───────────── 게임 중 끊겼을 때 자동으로 다시 들어가기 ─────────────

    private async Task AutoRejoinFlow(string roomCode)
    {
        autoRejoining = true;
        PhotonReconnectOverlay.Show("연결이 끊어졌어요. 다시 연결하는 중...", "Reconnecting...");

        RejoinResult result;
        try
        {
            result = await PhotonRejoinLoop.RunAsync(
                attempt => TryRejoinOnce(roomCode, attempt),
                () => Time.unscaledTime,
                WaitUntil,
                () => IsActive && autoRejoining,
                AutoRejoinSeconds, AutoRejoinRetrySeconds);
        }
        finally
        {
            autoRejoining = false;
            PhotonReconnectOverlay.Hide();
        }

        if (result == RejoinResult.Success) { Log("자동으로 다시 들어갔습니다"); return; }
        if (result == RejoinResult.Cancelled) return;

        // 돌아가지 못했다: 로비로 보낸다. 방이 아직 있다면 방 목록의 이어하기로 다시 들어갈 수 있다.
        if (result == RejoinResult.RoomGone) LastGameRoom = null; // 게임이 끝났거나 방이 사라졌다
        Log($"자동으로 다시 들어가지 못해 로비로 돌아갑니다 ({result})");

        LocalPlayer.Set(0, LocalPlayer.Nickname);
        string message = result == RejoinResult.RoomGone
            ? "게임이 이미 끝났거나 방이 사라졌어요"
            : "다시 연결하지 못했어요. 목록의 '이어하기' 방을 누르면 게임으로 돌아가요";
        LobbyNetwork.Instance.ReceiveLobbyMessage(LobbyMessageType.LOBBY_ERROR, ErrorJson("CONNECTION_LOST", message));
        SceneFlow.ToLobby();
    }

    private async Task<RejoinAttempt> TryRejoinOnce(string roomCode, int attempt)
    {
        PhotonReconnectOverlay.SetText($"연결이 끊어졌어요. 다시 연결하는 중... ({attempt})", $"Reconnecting... ({attempt})");
        Log($"자동 재연결 시도 {attempt}번 ({PhotonConnection.FormatCode(roomCode)})");

        // 방에 코드로 바로 들어간다. (PlayFab 인증은 접속 안에서 새로 받는다)
        if (!await connection.JoinRoomByCode(roomCode))
            return connection.LastJoinRoomMissing ? RejoinAttempt.RoomGone : RejoinAttempt.Retry;

        // 내 게임 번호는 건드리지 않는다. 방장이 예전 번호를 다시 알려 준다. (ASSIGN_ID)
        knownIds.Clear();
        foreach (PlayerRef player in connection.Players) knownIds.Add(player.PlayerId);
        rejoinAnswer = RejoinAnswer.Pending;

        // 방에 막 들어온 직후에는 방장과 서로 연결되기 전이라서, 방장이 답을 줄 때까지 확인한 뒤에 요청을 보낸다.
        await connection.Messenger.WaitForMaster(HandshakeSeconds);
        if (!connection.IsInSession) return RejoinAttempt.Retry;

        SendHello();
        connection.Messenger.SendRequest(PhotonMessenger.DomainLobby, JsonConvert.SerializeObject(new EnterRoomRequest { type = "ENTER_ROOM", roomId = roomCode }));

        float until = Time.unscaledTime + RejoinAnswerSeconds;
        while (rejoinAnswer == RejoinAnswer.Pending && connection.IsInSession && Time.unscaledTime < until)
            await Task.Yield();

        if (rejoinAnswer == RejoinAnswer.Accepted) return RejoinAttempt.Success;

        // 못 돌아갔다. 이 방의 참가자가 아니거나 게임이 끝난 경우는 포기하고, 그 밖에는 연결을 정리하고 다시 해 본다.
        Log(rejoinAnswer == RejoinAnswer.Rejected ? "방장이 돌아오는 것을 받아 주지 않았습니다" : "방장의 답을 받지 못했습니다");
        await connection.Disconnect();
        return rejoinAnswer == RejoinAnswer.Rejected ? RejoinAttempt.RoomGone : RejoinAttempt.Retry;
    }

    // ───────────── 대기방에서 끊겼을 때 같은 방에 다시 들어가기 ─────────────

    // 화면은 방에 그대로 둔 채 어두운 안내를 덮고, 같은 방에 다시 들어가 본다. (게임 중 재연결과 같은 반복)
    // 들어가면 방장이 보내 주는 방 정보로 화면이 방을 다시 그리고, 안 되면 로비로 나온다.
    // 끊긴 사람은 새 연결이라 방장에게는 새로 들어온 사람이다. (방장이었다면 방에 남은 사람 중 한 명이 방장을 이어받았고 나는 게스트로 들어간다)
    private async Task WaitingRoomRejoinFlow(string roomCode)
    {
        autoRejoining = true;
        rejoiningWaitingRoom = true;
        PhotonReconnectOverlay.Show("연결이 끊어졌어요. 다시 연결하는 중...", "Reconnecting...");

        RejoinResult result;
        try
        {
            result = await PhotonRejoinLoop.RunAsync(
                attempt => TryRejoinWaitingRoomOnce(roomCode, attempt),
                () => Time.unscaledTime,
                WaitUntil,
                () => IsActive && autoRejoining,
                AutoRejoinSeconds, AutoRejoinRetrySeconds);
        }
        finally
        {
            autoRejoining = false;
            rejoiningWaitingRoom = false;
            PhotonReconnectOverlay.Hide();
        }

        if (result == RejoinResult.Success) { Log("대기방에 다시 들어갔습니다"); return; }
        if (result == RejoinResult.Cancelled) return;

        Log($"대기방에 다시 들어가지 못해 로비로 돌아갑니다 ({result})");
        LocalPlayer.Set(0, LocalPlayer.Nickname);
        await LeaveToLobbyAfterLoss(result == RejoinResult.RoomGone ? "방이 없어졌거나 게임이 시작돼서 로비로 나왔어요" : "연결이 끊어져 방에서 나왔어요");
    }

    private async Task<RejoinAttempt> TryRejoinWaitingRoomOnce(string roomCode, int attempt)
    {
        PhotonReconnectOverlay.SetText($"연결이 끊어졌어요. 다시 연결하는 중... ({attempt})", $"Reconnecting... ({attempt})");
        Log($"대기방 다시 들어가기 {attempt}번 ({PhotonConnection.FormatCode(roomCode)})");

        if (!await connection.JoinRoomByCode(roomCode))
            return connection.LastJoinRoomMissing ? RejoinAttempt.RoomGone : RejoinAttempt.Retry;

        // 들어갔는데 나 혼자면 방장 PC가 이미 나간 방이다. 요청에 답해 줄 사람이 없으니 포기한다.
        if (connection.IsMaster)
        {
            Log("다시 들어간 방에 나 혼자라서 돌아가지 않습니다 (방이 사라진 것으로 봅니다)");
            await connection.Disconnect();
            return RejoinAttempt.RoomGone;
        }

        OnSessionJoined();
        rejoinAnswer = RejoinAnswer.Pending;
        rejoinErrorCode = null;

        await connection.Messenger.WaitForMaster(HandshakeSeconds);
        if (!connection.IsInSession) return RejoinAttempt.Retry;

        SendHello();
        connection.Messenger.SendRequest(PhotonMessenger.DomainLobby, JsonConvert.SerializeObject(new EnterRoomRequest { type = "ENTER_ROOM", roomId = roomCode }));

        float until = Time.unscaledTime + RejoinAnswerSeconds;
        while (rejoinAnswer == RejoinAnswer.Pending && connection.IsInSession && Time.unscaledTime < until)
            await Task.Yield();

        if (rejoinAnswer == RejoinAnswer.Accepted) return RejoinAttempt.Success;

        Log(rejoinAnswer == RejoinAnswer.Pending ? "방장의 답을 받지 못했습니다" : $"방장이 받아 주지 않았습니다 ({rejoinErrorCode})");
        RejoinAttempt verdict = rejoinAnswer == RejoinAnswer.Pending ? RejoinAttempt.Retry : RejoinRules.ClassifyEnterError(rejoinErrorCode);
        await connection.Disconnect();
        return verdict;
    }

    private string rejoinErrorCode; // 대기방에 다시 들어갈 때 방장이 알려 준 오류 이름

    private static string ErrorCodeOf(string json)
    {
        try { return (string)JObject.Parse(json)["code"]; }
        catch { return null; }
    }

    private static async Task WaitUntil(float time)
    {
        while (Time.unscaledTime < time) await Task.Yield();
    }

    // 다시 들어가서 방장의 허락(게임 시작 알림)을 받으면, 게임 화면이 현재 게임 상태를 요청해서 따라잡게 한다.
    private void ResyncGameAfterRejoin()
    {
        GameNetwork network = UnityEngine.Object.FindFirstObjectByType<GameNetwork>();
        if (network != null) network.SendGameStateRequest();
    }

    // 방장의 오류가 "해 봐야 소용없다"는 뜻인지. (돌아올 수 있는 게임이 없거나 이 방의 참가자가 아닌 경우)
    private static bool IsDefiniteRejection(string json)
    {
        try { return (string)JObject.Parse(json)["code"] == "NOT_REJOINABLE"; }
        catch { return false; }
    }

    // ───────────── 방장이 바뀌거나 사람이 나갈 때 ─────────────

    private void OnPlayersChanged()
    {
        if (!IsActive || !connection.IsInSession) { knownIds.Clear(); return; }

        var now = new HashSet<int>(connection.Players.Select(p => p.PlayerId));

        // 인사 없이 연결이 끊긴 사람은 방에서 나간 것으로 처리한다
        if (host != null && connection.IsMaster)
        {
            foreach (int refId in knownIds)
            {
                if (now.Contains(refId)) continue;

                long gameId = connection.Registry.GameIdOfRef(refId);

                // 그 자리는 이미 새 연결이 이어받았다. (같은 사람이 다시 들어온 뒤에야 예전 연결이 사라지는 경우)
                // 이때 나간 것으로 처리하면 방금 돌아온 사람이 AFK가 된다.
                if (connection.Registry.TryGetRef(gameId, out int currentRef) && currentRef != refId) continue;

                connection.Registry.Release(refId); // 연결 번호만 지우고 게임 번호와 계정은 남겨서, 돌아오면 같은 자리로 붙인다

                // 게임 중이면 자리를 남겨 둔다. (돌아올 수 있다) 대기 중이면 방에서 내보낸다.
                if (host.IsMember(gameId) && !host.IsMemberOfPlayingRoom(gameId)) host.OnRequest(gameId, LeaveRoomJson);
                connection.GameBridge.OnPlayerGone(gameId); // 게임 중이면 그 사람의 차례는 기다리지 않고 자동으로 진행한다
            }
        }

        foreach (int id in knownIds)
            if (!now.Contains(id)) seenRefs.Remove(id);

        knownIds.Clear();
        knownIds.UnionWith(now);
    }

    // 방장이 나가서 내가 새 방장이 되면, 마지막으로 받은 방 정보로 호스트를 이어받는다
    private void OnMasterChanged()
    {
        if (!IsActive || !connection.IsInSession || leaving) return;
        if (!connection.IsMaster || host != null || lastSnapshot == null) return;

        Log("방장이 되었습니다. 마지막으로 받은 방 정보로 호스트를 이어받습니다");
        StartHost();
        host.RestoreRoom(lastSnapshot);
        connection.Registry.Import(lastRegistryJson, connection.IsPlayerPresent); // 방장이 나눠 준 사람 기록으로 연결 번호와 게임 번호를 되살린다
        RemoveMembersWhoLeft();
        ApplyRoomToSession(lastSnapshot, true); // 이전 방장의 마지막 변경이 반영됐는지 알 수 없어서, 열림 여부를 다시 맞춘다

        // 게임이 진행 중이었다면 게임도 이어받는다. (내 화면이 가진 상태로 게임 호스트를 시작한다)
        if (lastSnapshot.status == RoomStatus.PLAYING)
            connection.GameBridge.BeginTakeOver(lastSnapshot, connection.PreviousMasterId);
    }

    // 이어받은 방 정보에는 방금 나간 전 방장 같은 사람이 아직 들어 있을 수 있다. (나가기 직전의 방 정보를 못 받은 경우)
    // 그대로 두면 빈자리가 없는 방으로 보여서 방이 닫히고, 나간 사람이 다시 들어올 수 없다.
    // Photon에 이미 없는 사람을 방에서 뺀다.
    private void RemoveMembersWhoLeft()
    {
        // 게임 중에는 나간 사람의 자리도 남겨 둔다. (돌아올 수 있고, 게임이 끝날 때 정리한다)
        if (lastSnapshot.status == RoomStatus.PLAYING) return;

        foreach (PlayerSnapshot member in lastSnapshot.Players.ToList())
        {
            if (member.bot) continue;
            if (member.playerId == connection.LocalPlayerRef.PlayerId) continue;
            if (connection.IsPlayerPresent((int)member.playerId)) continue;
            if (!host.IsMember(member.playerId)) continue;

            Log($"이미 나간 {member.playerId}번을 방에서 뺍니다");
            host.OnRequest(member.playerId, LeaveRoomJson);
        }
    }

    // ───────────── 알림 받기 (방장 호스트 → 화면) ─────────────

    private void OnNotifyReceived(PlayerRef sender, int domain, string typeName, string json)
    {
        if (!IsActive || domain != PhotonMessenger.DomainLobby) return;

        // 방 안의 내부 메시지: 방장이 정해 준 내 게임 번호, 방장이 나눠 준 사람 기록
        if (typeName == "ASSIGN_ID") { HandleAssignId(json); return; }
        if (typeName == "REGISTRY") { lastRegistryJson = json; return; }

        if (!Enum.TryParse(typeName, out LobbyMessageType type))
        {
            Log("알 수 없는 로비 알림입니다: " + typeName);
            return;
        }

        Log($"알림 받음: {type} (보낸 사람 {sender.PlayerId}번)");
        if (type == LobbyMessageType.ROOM_ENTERED || type == LobbyMessageType.LOBBY_ERROR) awaitingAnswer = false;

        if (type == LobbyMessageType.ROOM_UPDATED)
        {
            try { lastSnapshot = JsonConvert.DeserializeObject<RoomUpdatedBroadcast>(json).room; }
            catch (Exception e) { Log("방 정보를 읽지 못했습니다: " + e.Message); }

            // 게임이 시작되면 이 방을 기억하고(끊겼다 돌아올 때 이어하기로 보인다), 게임이 끝나면 지운다
            if (lastSnapshot != null)
            {
                if (lastSnapshot.status == RoomStatus.PLAYING) LastGameRoom = lastSnapshot.roomId;
                else if (LastGameRoom == lastSnapshot.roomId) LastGameRoom = null;
            }
        }

        if (autoRejoining && rejoiningWaitingRoom)
        {
            // 대기방으로 돌아가는 중이다. 방장이 받아 주면(ROOM_ENTERED) 화면이 방을 다시 그리도록 전달하고,
            // 그 뒤에 오는 방 정보(ROOM_UPDATED)도 전달한다. 오류면 이유로 계속 해 볼지 정한다.
            if (type == LobbyMessageType.ROOM_ENTERED)
            {
                rejoinAnswer = RejoinAnswer.Accepted;
                LobbyNetwork.Instance.ReceiveLobbyMessage(type, json);
            }
            else if (type == LobbyMessageType.ROOM_UPDATED)
            {
                if (rejoinAnswer == RejoinAnswer.Accepted) LobbyNetwork.Instance.ReceiveLobbyMessage(type, json);
            }
            else if (type == LobbyMessageType.LOBBY_ERROR)
            {
                rejoinErrorCode = ErrorCodeOf(json);
                rejoinAnswer = RejoinAnswer.Failed;
            }
            return;
        }

        if (autoRejoining)
        {
            // 게임 화면에서 다시 들어가는 중이다. 방장의 답으로 결과를 정하고 로비 화면에는 전달하지 않는다.
            // (전달하면 나중에 로비에 들어갔을 때 낡은 입장, 게임 시작 알림이 처리돼서 게임 화면으로 다시 넘어간다)
            if (type == LobbyMessageType.GAME_STARTED)
            {
                rejoinAnswer = RejoinAnswer.Accepted;
                ResyncGameAfterRejoin();
            }
            else if (type == LobbyMessageType.LOBBY_ERROR)
            {
                rejoinAnswer = IsDefiniteRejection(json) ? RejoinAnswer.Rejected : RejoinAnswer.Failed;
            }
            return;
        }

        if (type == LobbyMessageType.GAME_STARTED) connection.GameBridge.ResetForNewGame(); // 새 게임: 이전 게임의 보관 중인 것을 버린다

        LobbyNetwork.Instance.ReceiveLobbyMessage(type, json);

        if (type == LobbyMessageType.ROOM_LEFT)
        {
            if (leaving) LastGameRoom = null; // 일부러 나간 방은 이어하기로 보이지 않는다
            Run(FinishLeave(), "방 나가기 마무리");
        }
    }

    // 방장이 정해 준 내 게임 번호. 처음 들어온 사람은 연결 번호와 같고, 끊겼다 돌아온 사람은 예전 번호를 다시 받는다.
    private void HandleAssignId(string json)
    {
        try
        {
            long id = (long)JObject.Parse(json)["playerId"];
            if (id == LocalPlayer.Id) return;

            LocalPlayer.Set(id, LocalPlayer.Nickname);
            Log($"내 게임 번호는 {id}번입니다");
        }
        catch (Exception e) { Log("게임 번호를 읽지 못했습니다: " + e.Message); }
    }

    // ───────────── 도우미 ─────────────

    private void DeliverError(string code, string message)
    {
        LobbyNetwork.Instance.ReceiveLobbyMessage(LobbyMessageType.LOBBY_ERROR, ErrorJson(code, message));
    }

    private static string ErrorJson(string code, string message)
    {
        return JsonConvert.SerializeObject(new LobbyErrorResponse { type = "LOBBY_ERROR", code = code, message = message });
    }

    private static string ReadType(string json)
    {
        try { return (string)JObject.Parse(json)["type"]; }
        catch { return ""; }
    }

    // 기다리지 않는 작업이 중간에 오류를 내도 로그로 남게 한다
    private async void Run(Task task, string name)
    {
        try { await task; }
        catch (Exception e)
        {
            Log($"{name} 중 오류: {e.Message}");
            Debug.LogError(e);
            busy = false;
        }
    }

    private void Log(string message) => connection.WriteLog("[로비] " + message);
}