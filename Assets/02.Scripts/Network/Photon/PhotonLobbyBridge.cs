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
    private const string KeyHosted = "hosted";   // 1이면 방장 PC가 로비 호스트(서버 역할)를 돌리고 있는 방. 게임 로비에서 만든 방이다

    private const string StateWaiting = "waiting";
    private const string StatePlaying = "playing";

    private const long OfflinePlayerId = 1;      // Photon을 쓰지 않을 때 이 PC 플레이어의 번호 (TitleManager와 같다)
    private const float LeaveWaitSeconds = 3f;   // 방을 나갈 때 방장의 답을 기다리는 시간
    private const float RoomListWaitSeconds = 2f;  // 로비에 방금 다시 연결했을 때, 고른 방이 목록에 나타나기를 기다리는 시간
    private const float LobbyConnectSeconds = 8f;  // 로비에 연결되기를 기다리는 시간. 넘으면 혼자 하기로 전환한다 (화면의 10초 제한보다 먼저)
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
    private string lastListedLog = "";           // 로비 목록에 올린 방을 로그로 남긴 마지막 내용         // 방 목록에서 제외된 방을 로그로 남긴 마지막 내용 (같은 내용을 반복해 남기지 않으려고)

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

    private async Task RefreshRoomList()
    {
        if (connection.IsInSession || busy || leaving) return; // 방 안이거나 방을 만들고 들어가고 나오는 중에는 목록을 보내지 않는다

        // 연결이 바뀌는 도중이면 오류를 띄우지 않고 건너뛴다. 로비에 다시 들어가면 방 목록이 알아서 화면에 간다.
        if (connection.Current == PhotonConnection.ConnectionState.StartingSession || connection.Current == PhotonConnection.ConnectionState.InSession) return;

        if (!await EnsureLobby())
        {
            await FallbackToLocal("로비에 연결하지 못했습니다");
            return;
        }
        SendRoomListToScreen();
    }

    // 로비에 있게 만든다. 이미 있으면 그대로, 들어가는 중이면 기다리고, 끊겨 있으면 들어간다.
    // 연결이 오래 걸리면 포기하고 false를 돌려준다. (인터넷이 없을 때 화면이 10초 뒤 "서버 응답이 없어요"만 띄우지 않게)
    private async Task<bool> EnsureLobby()
    {
        float until = Time.unscaledTime + LobbyConnectSeconds;

        // 웹(WebGL)에서는 Task.Delay가 동작하지 않아서 프레임마다 확인한다
        while (connection.Current == PhotonConnection.ConnectionState.JoiningLobby && Time.unscaledTime < until)
            await Task.Yield();

        if (connection.Current == PhotonConnection.ConnectionState.InLobby) return true;
        if (connection.Current != PhotonConnection.ConnectionState.Disconnected) return false;

        Task<bool> join = connection.JoinLobby();
        while (!join.IsCompleted && Time.unscaledTime < until)
            await Task.Yield();

        if (join.IsCompleted) return join.Result;

        Log($"로비 연결이 {LobbyConnectSeconds}초 안에 되지 않았습니다");
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

    // 게임이 시작된 방, 가득 찬 방(Photon이 닫아 둠), 호스트가 없는 방은 목록에 올리지 않는다.
    // 호스트가 없는 방은 들어가도 요청에 답해 줄 PC가 없다. (테스트 창의 만들기로 만든 방, 로비 연결을 켜지 않은 PC가 만든 방)
    private static bool IsListable(SessionInfo info) =>
        info.IsOpen
        && IntProp(info, KeyHosted, 0) == 1
        && StringProp(info, KeyState, StateWaiting) == StateWaiting;

    private static RoomSummary ToSummary(SessionInfo info) => new RoomSummary
    {
        roomId = info.Name,
        hostNickname = StringProp(info, KeyHost, ""),
        mapId = IntProp(info, KeyMap, 0),
        playerCount = IntProp(info, KeyCount, info.PlayerCount),
        maxPlayers = IntProp(info, KeyMax, PhotonConnection.MaxHumansLimit),
    };

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

            // 방 이름은 화면이 정하지 않으므로 만든 사람의 이름으로 정한다.
            // 방 목록에 보일 속성(방장, 인원, 호스트 표시 등)은 방을 만들 때 함께 정해야 다른 PC의 방 목록에 보인다.
            // 화면이 고른 인원과 맵을 처음부터 반영한다. (Photon 방의 최대 인원은 만든 뒤에 못 바꿔서 4로 두고, 실제 자리는 게임 규칙이 막는다)
            if (!await connection.CreateRoom($"{LocalPlayer.Nickname}의 방", PhotonConnection.MaxHumansLimit, InitialRoomProperties(PeekRoomConfig())))
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

            if (!await connection.JoinRoom(roomId))
            {
                DeliverError("ROOM_NOT_FOUND", "방이 사라졌거나 들어갈 수 없어요");
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
            if (lastSnapshot != null || host != null) await HandleRoomConnectionLost();
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
        host = null;
        lastSnapshot = null;
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
        Log($"요청 받음: {ReadType(json)} (보낸 사람 {sender.PlayerId}번)");

        if (host == null)
        {
            // 방장이 아니거나, 아직 호스트가 없다. (방이 만들어지기 전에 온 요청 등)
            Log($"로비 요청을 받았지만 호스트가 없습니다 (보낸 사람 {sender.PlayerId}번)");
            DeliverTo(sender, LobbyMessageType.LOBBY_ERROR, ErrorJson("ROOM_NOT_FOUND", "방이 사라졌어요"));
            return;
        }

        long senderId = sender.PlayerId;

        if (ReadType(json) == "PLAYER_HELLO")
        {
            try
            {
                JObject hello = JObject.Parse(json);
                host.RegisterPlayer(senderId, (string)hello["nickname"], (int?)hello["portraitId"]);
            }
            catch (Exception e) { Log("PLAYER_HELLO를 읽지 못했습니다: " + e.Message); }
            return;
        }

        host.OnRequest(senderId, json);
    }

    // 호스트가 보내는 알림을 Photon으로 내보낸다. 봇(1001번부터)이나 이미 나간 사람에게는 보내지 않는다.
    private void Deliver(long playerId, LobbyMessageType type, string json)
    {
        if (!TryFindPlayer(playerId, out PlayerRef target))
        {
            if (playerId < 1000) Log($"알림을 보낼 사람을 찾지 못했습니다: {type} → {playerId}번"); // 1000번 이상은 봇이라 받을 필요가 없다
            return;
        }

        Log($"알림 보냄: {type} → {playerId}번");
        DeliverTo(target, type, json);
    }

    private void DeliverTo(PlayerRef target, LobbyMessageType type, string json)
    {
        connection.Messenger.SendNotify(target, PhotonMessenger.DomainLobby, type.ToString(), json);
    }

    private bool TryFindPlayer(long playerId, out PlayerRef found)
    {
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
        PlayerSnapshot hostPlayer = room.Players.FirstOrDefault(p => p.playerId == room.hostId);

        var properties = new Dictionary<string, SessionProperty>
        {
            [KeyName] = hostPlayer != null ? hostPlayer.nickname + "의 방" : room.roomId,
            [KeyState] = waiting ? StateWaiting : StatePlaying,
            [KeyHost] = hostPlayer != null ? hostPlayer.nickname : "",
            [KeyMap] = room.mapId,
            [KeyCount] = room.Players.Count,
            [KeyMax] = room.maxPlayers,
            [KeyHosted] = 1,
        };
        connection.UpdateRoomProperties(properties);
        Log($"방 속성 갱신: 사람 {humans}명, 전체 {room.Players.Count}/{room.maxPlayers}명, 맵 {room.mapId}, 상태 {(waiting ? "대기" : "게임 중")}");

        // 들어올 수 있는 사람 수는 최대 인원에서 봇을 뺀 만큼이다. 다 찼거나 게임이 시작되면 닫는다.
        bool open = waiting && humans < room.maxPlayers - room.botCount;
        connection.SetRoomOpen(open, forceOpen);
        if (forceOpen) Log($"방 정보를 Photon 방에 반영했습니다 (사람 {humans}명, 자리 {room.maxPlayers - room.botCount}개, 상태 {(waiting ? "대기" : "게임 중")}, 열림 {open})");
    }

    // ───────────── 연결이 예기치 않게 끊겼을 때 ─────────────

    private void OnConnectionLost(PhotonConnection.ConnectionState previous, ShutdownReason reason)
    {
        if (!IsActive || leaving || finishing) return; // 우리가 나가는 중이면 이미 정리하고 있다

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
    private async Task HandleRoomConnectionLost()
    {
        bool inGame = SceneManager.GetActiveScene().path == SceneFlow.GameScenePath;

        ClearRoomState();
        LocalPlayer.Set(0, LocalPlayer.Nickname);

        if (inGame)
        {
            // 게임 중에 끊기면 이어하기는 아직 지원하지 않아서 타이틀로 보낸다. (재접속 작업에서 이어하기로 바꾼다)
            Log("게임 중 연결이 끊겨 타이틀로 돌아갑니다");
            SceneFlow.ToLogin("연결이 끊어져 게임에서 나왔어요");
            return;
        }

        // 대기방에서 끊겼다: 화면이 방에서 나오게 하고 안내한 뒤, 로비에 다시 연결해서 방 목록을 본다.
        LobbyNetwork.Instance.ReceiveLobbyMessage(LobbyMessageType.ROOM_LEFT, JsonConvert.SerializeObject(new RoomLeftResponse { type = "ROOM_LEFT" }));
        LobbyNetwork.Instance.ReceiveLobbyMessage(LobbyMessageType.LOBBY_ERROR, ErrorJson("CONNECTION_LOST", "연결이 끊어져 방에서 나왔어요"));
        await RefreshRoomList();
    }

    // ───────────── 방장이 바뀌거나 사람이 나갈 때 ─────────────

    private void OnPlayersChanged()
    {
        if (!IsActive || !connection.IsInSession) { knownIds.Clear(); return; }

        var now = new HashSet<int>(connection.Players.Select(p => p.PlayerId));

        // 인사 없이 연결이 끊긴 사람은 방에서 나간 것으로 처리한다
        if (host != null && connection.IsMaster)
        {
            foreach (int id in knownIds)
                if (!now.Contains(id) && host.IsMember(id)) host.OnRequest(id, LeaveRoomJson);
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
        RemoveMembersWhoLeft();
        ApplyRoomToSession(lastSnapshot, true); // 이전 방장의 마지막 변경이 반영됐는지 알 수 없어서, 열림 여부를 다시 맞춘다
    }

    // 이어받은 방 정보에는 방금 나간 전 방장 같은 사람이 아직 들어 있을 수 있다. (나가기 직전의 방 정보를 못 받은 경우)
    // 그대로 두면 빈자리가 없는 방으로 보여서 방이 닫히고, 나간 사람이 다시 들어올 수 없다.
    // Photon에 이미 없는 사람을 방에서 뺀다.
    private void RemoveMembersWhoLeft()
    {
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
        }

        if (type == LobbyMessageType.GAME_STARTED) connection.GameBridge.ResetForNewGame(); // 새 게임: 이전 게임의 보관 중인 것을 버린다

        LobbyNetwork.Instance.ReceiveLobbyMessage(type, json);

        if (type == LobbyMessageType.ROOM_LEFT) Run(FinishLeave(), "방 나가기 마무리");
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