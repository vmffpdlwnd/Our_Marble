using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Fusion;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

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
    private const float AnswerWarnSeconds = 6f;  // 방 만들기와 입장 요청을 보낸 뒤 이 시간 안에 답이 없으면 로그로 알린다

    private const string LeaveRoomJson = "{\"type\":\"LEAVE_ROOM\"}";

    private readonly PhotonConnection connection;

    private LocalLobbyHost host;                 // 내가 방장일 때만 있다. 방 하나를 가진 호스트
    private RoomSnapshot lastSnapshot;           // 마지막으로 받은 방 정보. 방장이 바뀔 때 이어받는 데 쓴다
    private bool busy;                           // 방을 만들거나 들어가는 중
    private bool leaving;                        // 방을 나가는 중
    private int? pendingPortrait;                // 방에 들어가기 전에 고른 초상화
    private bool awaitingAnswer;                 // 방 만들기나 입장 요청을 보내고 방장의 답을 기다리는 중
    private readonly Queue<string> queued = new Queue<string>();   // 만드는 중에 들어온 요청 (방 설정 요청 등)
    private readonly HashSet<int> knownIds = new HashSet<int>();

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
        connection.Messenger.RequestReceived += OnRequestReceived;
        connection.Messenger.NotifyReceived += OnNotifyReceived;
        connection.SessionsChanged += OnSessionsChanged;
        connection.PlayersChanged += OnPlayersChanged;
        connection.MasterChanged += OnMasterChanged;
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
        if (LobbyNetwork.Instance.Host == this) LobbyNetwork.Instance.Host = null;

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
        if (connection.IsInSession || busy) return; // 방 안이거나 방을 만들고 들어가는 중에는 목록이 필요 없다

        if (!await EnsureLobby())
        {
            DeliverError("CONNECT_FAILED", "Photon에 연결하지 못했어요");
            return;
        }
        SendRoomListToScreen();
    }

    // 로비에 있게 만든다. 이미 있으면 그대로, 들어가는 중이면 기다리고, 끊겨 있으면 들어간다.
    private async Task<bool> EnsureLobby()
    {
        // 웹(WebGL)에서는 Task.Delay가 동작하지 않아서 프레임마다 확인한다
        while (connection.Current == PhotonConnection.ConnectionState.JoiningLobby)
            await Task.Yield();

        if (connection.Current == PhotonConnection.ConnectionState.InLobby) return true;
        if (connection.Current == PhotonConnection.ConnectionState.Disconnected) return await connection.JoinLobby();
        return false;
    }

    private void OnSessionsChanged()
    {
        // 방 밖에서 로비에 있는 동안은 목록이 바뀔 때마다 화면에 밀어 준다
        if (IsActive && connection.Current == PhotonConnection.ConnectionState.InLobby) SendRoomListToScreen();
    }

    private void SendRoomListToScreen()
    {
        List<RoomSummary> list = connection.Sessions.Where(IsListable).Select(ToSummary).ToList();
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
            if (!await EnsureLobby()) { DeliverError("CONNECT_FAILED", "Photon에 연결하지 못했어요"); return; }

            // 방 이름은 화면이 정하지 않으므로 만든 사람의 이름으로 정한다
            if (!await connection.CreateRoom($"{LocalPlayer.Nickname}의 방", PhotonConnection.MaxHumansLimit))
            {
                DeliverError("CREATE_FAILED", "방을 만들지 못했어요");
                return;
            }

            // 방을 만든 사람이 방장이다. 호스트를 시작하고 평소처럼 요청을 보낸다. (내 요청은 네트워크를 타지 않고 내 호스트로 간다)
            OnSessionJoined();
            StartHost();
            PublishHostedMark();
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

            if (!await EnsureLobby()) { DeliverError("CONNECT_FAILED", "Photon에 연결하지 못했어요"); return; }

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
        if (!connection.IsInSession || busy) { DeliverError("NOT_IN_ROOM", "방에 들어가 있지 않아요"); return; }

        leaving = true;
        connection.Messenger.SendRequest(PhotonMessenger.DomainLobby, json); // 방장이 ROOM_LEFT를 돌려주고 남은 사람들에게 알린다

        // ROOM_LEFT가 오면 OnNotifyReceived가 정리한다. 오지 않을 때를 대비해 시간 제한을 둔다.
        float until = Time.unscaledTime + LeaveWaitSeconds;
        while (leaving && Time.unscaledTime < until) await Task.Yield();

        if (leaving) await FinishLeave();
    }

    // 방에서 나온 뒤: 연결을 끊고 다시 로비로 돌아가 방 목록을 본다
    private async Task FinishLeave()
    {
        if (!leaving) return;
        leaving = false;

        ClearRoomState();
        LocalPlayer.Set(0, LocalPlayer.Nickname);

        await connection.Disconnect();
        if (IsActive) await connection.JoinLobby();
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

    // 방을 만든 직후, 첫 방 정보가 나오기 전에도 이 방에 호스트가 있다고 표시한다. (다른 PC의 방 목록에 바로 보이게)
    private void PublishHostedMark()
    {
        connection.UpdateRoomProperties(new Dictionary<string, SessionProperty>
        {
            [KeyName] = LocalPlayer.Nickname + "의 방",
            [KeyState] = StateWaiting,
            [KeyHost] = LocalPlayer.Nickname,
            [KeyMap] = 0,
            [KeyCount] = 1,
            [KeyMax] = PhotonConnection.MaxHumansLimit,
            [KeyHosted] = 1,
        });
    }

    // 방의 상태가 바뀔 때마다 방 목록에 보일 값(방장 이름, 인원, 상태)과 열림 여부를 Photon 방에 반영한다
    private void OnHostRoomUpdated(RoomSnapshot room)
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

        // 들어올 수 있는 사람 수는 최대 인원에서 봇을 뺀 만큼이다. 다 찼거나 게임이 시작되면 닫는다.
        connection.SetRoomOpen(waiting && humans < room.maxPlayers - room.botCount);
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
        OnHostRoomUpdated(lastSnapshot);
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