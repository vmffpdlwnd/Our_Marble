using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// 로비 요청을 받아 방을 관리하고 응답을 돌려주는 호스트 쪽 코드. 예전에 서버가 하던 일이다.
/// 요청은 JSON 문자열로 들어오고(OnRequest), 응답은 받는 사람마다 JSON 문자열로 나간다. (네트워크로 주고받을 때와 같은 모양)
/// Unity 오브젝트가 아니라서 씬이 바뀌어도 방 정보가 유지된다.
/// 여러 사람이 보내는 요청을 다룰 수 있게 만들었지만, 지금은 이 PC의 플레이어 한 명만 요청을 보낸다.
/// </summary>
public class LocalLobbyHost : IHostRequestReceiver
{
    private const int BotIdBase = 1000;   // 봇의 playerId는 1001부터. 사람의 id와 겹치지 않게 한다
    private const int MinPlayers = 2;
    private const int MaxPlayersLimit = 4;

    private class Member
    {
        public long Id;
        public string Nickname;
        public bool Ready;
        public bool Bot;
        public int? PortraitId;
    }

    private class Room
    {
        public string Id;
        public string Name; // 방 이름. 방을 만들 때 정하고 방장이 바뀌어도 그대로 둔다
        public long HostId;
        public RoomStatus Status;
        public GameConfig Config;
        public int MaxPlayers;
        public List<Member> Members = new List<Member>(); // 사람이 먼저, 봇은 맨 뒤
    }

    private readonly Action<long, LobbyMessageType, string> deliver;
    private readonly List<Room> rooms = new List<Room>();
    private readonly Dictionary<long, string> roomOfPlayer = new Dictionary<long, string>();
    private readonly Dictionary<long, string> nicknames = new Dictionary<long, string>();
    private readonly Dictionary<long, int> portraits = new Dictionary<long, int>();
    private int nextRoomNumber = 1;

    /// <summary>방 번호를 정하는 방법. 정하지 않으면 "room-1", "room-2"처럼 센다. (Photon에서는 방 코드를 쓴다)</summary>
    public Func<string> RoomIdFactory;

    /// <summary>방의 상태가 바뀌어 전원에게 알릴 때마다 불린다. (Photon 방 목록에 보일 값을 갱신하는 데 쓴다)</summary>
    public event Action<RoomSnapshot> RoomUpdated;

    /// <param name="deliver">응답을 받는 사람에게 보낸다. (playerId, 응답 종류, 응답 JSON)</param>
    public LocalLobbyHost(Action<long, LobbyMessageType, string> deliver)
    {
        this.deliver = deliver;
    }

    // ───────────── 이 PC의 호스트 ─────────────

    private static LocalLobbyHost instance;

    /// <summary>이 PC에서 돌아가는 호스트. 처음 쓸 때 만든다.</summary>
    public static LocalLobbyHost Instance => instance ?? (instance = new LocalLobbyHost(DeliverToLocalPlayer));

    /// <summary>방 정보를 모두 버린다. 타이틀로 돌아갈 때 부른다.</summary>
    public static void Reset() => instance = null;

    /// <summary>이 PC의 호스트를 직접 만든 것으로 바꾼다. (Photon에서 방장이 된 PC가 쓴다)</summary>
    public static void Use(LocalLobbyHost host) => instance = host;

    // 에디터에서 도메인 리로드 없이 플레이를 다시 시작해도 이전 플레이의 방이 남지 않게 한다
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => instance = null;

    // 이 PC에서 플레이하는 사람에게만 전달한다. (봇은 받을 필요가 없다)
    private static void DeliverToLocalPlayer(long playerId, LobbyMessageType type, string json)
    {
        if (playerId == LocalPlayer.Id) LobbyNetwork.Instance.ReceiveLobbyMessage(type, json);
    }

    // ───────────── 요청 받기 ─────────────

    public void OnRequest(long senderId, string json)
    {
        // 이 PC의 플레이어는 닉네임을 바꿔서 다시 시작할 수 있으므로 요청마다 최신 값을 기억해 둔다
        if (senderId == LocalPlayer.Id && LocalPlayer.IsSet) nicknames[senderId] = LocalPlayer.Nickname;

        string type;
        try
        {
            type = (string)JObject.Parse(json)["type"];
        }
        catch (Exception e)
        {
            Debug.LogError($"[LocalLobbyHost] 요청을 읽지 못했습니다: {json}\n{e.Message}");
            SendError(senderId, "BAD_REQUEST", "요청을 처리하지 못했어요");
            return;
        }

        Debug.Log($"[LocalLobbyHost] 요청 수신: {type} (보낸 사람 {senderId})");

        switch (type)
        {
            case "ROOM_LIST_REQUEST": HandleRoomList(senderId); break;
            case "CREATE_ROOM": HandleCreateRoom(senderId, JsonConvert.DeserializeObject<CreateRoomRequest>(json)); break;
            case "ENTER_ROOM": HandleEnterRoom(senderId, JsonConvert.DeserializeObject<EnterRoomRequest>(json)); break;
            case "LEAVE_ROOM": HandleLeaveRoom(senderId); break;
            case "SET_READY": HandleSetReady(senderId, JsonConvert.DeserializeObject<SetReadyRequest>(json)); break;
            case "SET_PORTRAIT": HandleSetPortrait(senderId, JsonConvert.DeserializeObject<SetPortraitRequest>(json)); break;
            case "ROOM_CONFIG_REQUEST": HandleRoomConfig(senderId, JsonConvert.DeserializeObject<RoomConfigRequest>(json)); break;
            case "START_GAME": HandleStartGame(senderId); break;
            case "CHAT_SEND": HandleChat(senderId, JsonConvert.DeserializeObject<ChatSendRequest>(json)); break;
            default:
                Debug.LogWarning($"[LocalLobbyHost] 알 수 없는 요청입니다: {type}");
                SendError(senderId, "UNKNOWN_REQUEST", "요청을 처리하지 못했어요");
                break;
        }
    }

    // ───────────── 요청 처리 ─────────────

    private void HandleRoomList(long senderId)
    {
        // 게임이 시작된 방은 들어갈 수 없으므로 목록에 올리지 않는다
        List<RoomSummary> list = rooms
            .Where(r => r.Status == RoomStatus.WAITING)
            .Select(ToSummary)
            .ToList();

        Send(senderId, LobbyMessageType.ROOM_LIST, new RoomListResponse { type = "ROOM_LIST", rooms = list });
    }

    private void HandleCreateRoom(long senderId, CreateRoomRequest request)
    {
        if (roomOfPlayer.ContainsKey(senderId)) { SendError(senderId, "ALREADY_IN_ROOM", "이미 방에 들어가 있어요"); return; }

        Room room = new Room
        {
            Id = RoomIdFactory != null ? RoomIdFactory() : "room-" + nextRoomNumber++,
            Name = RoomNames.Sanitize(request?.roomName) ?? RoomNames.Default(NicknameOf(senderId)), // 방장이 정한 이름, 비우면 만든 사람의 닉네임으로 정한다. 방장이 바뀌어도 바뀌지 않는다
            HostId = senderId,
            Status = RoomStatus.WAITING,
            Config = new GameConfig(),
            MaxPlayers = MaxPlayersLimit, // 방을 만든 직후 바로 오는 방 설정 요청이 실제 값으로 바꾼다
        };
        rooms.Add(room);
        AddHuman(room, senderId);

        Debug.Log($"[LocalLobbyHost] 방 생성: {room.Id} (방장 {NicknameOf(senderId)})");

        Send(senderId, LobbyMessageType.ROOM_ENTERED, new RoomEnteredResponse { type = "ROOM_ENTERED" });
        BroadcastRoomUpdated(room);
    }

    private void HandleEnterRoom(long senderId, EnterRoomRequest request)
    {
        if (roomOfPlayer.ContainsKey(senderId)) { SendError(senderId, "ALREADY_IN_ROOM", "이미 방에 들어가 있어요"); return; }

        Room room = rooms.FirstOrDefault(r => r.Id == request.roomId);
        if (room == null) { SendError(senderId, "ROOM_NOT_FOUND", "방이 사라졌어요"); return; }
        if (room.Status != RoomStatus.WAITING) { SendError(senderId, "ROOM_PLAYING", "이미 게임이 시작된 방이에요"); return; }
        if (room.Members.Count >= room.MaxPlayers) { SendError(senderId, "ROOM_FULL", "방이 가득 찼어요"); return; }

        AddHuman(room, senderId);

        Debug.Log($"[LocalLobbyHost] 방 입장: {room.Id} ({NicknameOf(senderId)})");

        Send(senderId, LobbyMessageType.ROOM_ENTERED, new RoomEnteredResponse { type = "ROOM_ENTERED" });
        BroadcastRoomUpdated(room);
    }

    private void HandleLeaveRoom(long senderId)
    {
        Room room = RoomOf(senderId);
        if (room == null) { SendError(senderId, "NOT_IN_ROOM", "방에 들어가 있지 않아요"); return; }

        room.Members.RemoveAll(m => m.Id == senderId);
        roomOfPlayer.Remove(senderId);
        Send(senderId, LobbyMessageType.ROOM_LEFT, new RoomLeftResponse { type = "ROOM_LEFT" });

        Member nextHost = room.Members.FirstOrDefault(m => !m.Bot);
        if (nextHost == null)
        {
            // 사람이 한 명도 남지 않으면 방을 없앤다 (봇만 남은 방은 의미가 없다)
            rooms.Remove(room);
            Debug.Log($"[LocalLobbyHost] 방 삭제: {room.Id}");
            return;
        }

        if (room.HostId == senderId) room.HostId = nextHost.Id; // 방장이 나가면 가장 먼저 들어온 사람이 이어받는다
        BroadcastRoomUpdated(room);
    }

    private void HandleSetReady(long senderId, SetReadyRequest request)
    {
        Room room = RoomOf(senderId);
        if (room == null) { SendError(senderId, "NOT_IN_ROOM", "방에 들어가 있지 않아요"); return; }
        if (room.Status != RoomStatus.WAITING) return;

        MemberOf(room, senderId).Ready = request.ready;
        BroadcastRoomUpdated(room);
    }

    private void HandleSetPortrait(long senderId, SetPortraitRequest request)
    {
        portraits[senderId] = request.portraitId; // 방 밖에서 골라도 기억해 두었다가 방에 들어갈 때 쓴다

        Room room = RoomOf(senderId);
        if (room == null) return;

        MemberOf(room, senderId).PortraitId = request.portraitId;
        BroadcastRoomUpdated(room);
    }

    private void HandleRoomConfig(long senderId, RoomConfigRequest request)
    {
        Room room = RoomOf(senderId);
        if (room == null) { SendError(senderId, "NOT_IN_ROOM", "방에 들어가 있지 않아요"); return; }
        if (room.HostId != senderId) { SendError(senderId, "NOT_HOST", "방장만 설정을 바꿀 수 있어요"); return; }
        if (room.Status != RoomStatus.WAITING) { SendError(senderId, "ROOM_PLAYING", "게임 중에는 설정을 바꿀 수 없어요"); return; }

        GameConfig config = request.config;
        int humans = room.Members.Count(m => !m.Bot);

        // 화면(LobbyManager.RequestRoomConfig)이 먼저 확인하는 범위와 같다. 그래도 호스트가 한 번 더 확인한다.
        if (request.maxPlayers < MinPlayers || request.maxPlayers > MaxPlayersLimit)
        { SendError(senderId, "INVALID_CONFIG", "최대 인원은 2~4명이에요"); return; }
        if (request.maxPlayers < humans)
        { SendError(senderId, "INVALID_CONFIG", "지금 들어와 있는 인원보다 적게는 줄일 수 없어요"); return; }
        if (request.botCount < 0 || humans + request.botCount > request.maxPlayers)
        { SendError(senderId, "INVALID_CONFIG", "사람과 봇을 합쳐 최대 인원을 넘을 수 없어요"); return; }
        if (config == null || config.startMoney <= 0 || config.salaryAmount < 0 || config.maxRound <= 0)
        { SendError(senderId, "INVALID_CONFIG", "게임 설정 값이 올바르지 않아요"); return; }

        room.Config = config;
        room.MaxPlayers = request.maxPlayers;
        AdjustBots(room, request.botCount);

        BroadcastRoomUpdated(room);
    }

    private void HandleStartGame(long senderId)
    {
        Room room = RoomOf(senderId);
        if (room == null) { SendError(senderId, "NOT_IN_ROOM", "방에 들어가 있지 않아요"); return; }
        if (room.HostId != senderId) { SendError(senderId, "NOT_HOST", "방장만 게임을 시작할 수 있어요"); return; }
        if (room.Status != RoomStatus.WAITING) { SendError(senderId, "ROOM_PLAYING", "이미 게임이 시작됐어요"); return; }

        if (room.Members.Count < MinPlayers)
        { SendError(senderId, "NOT_ENOUGH_PLAYERS", "게임을 시작하려면 2명 이상이 필요해요. 방 설정에서 봇을 추가할 수 있어요"); return; }
        if (room.Members.Any(m => !m.Bot && m.Id != room.HostId && !m.Ready))
        { SendError(senderId, "NOT_ALL_READY", "모든 참가자가 준비해야 시작할 수 있어요"); return; }

        room.Status = RoomStatus.PLAYING;
        Debug.Log($"[LocalLobbyHost] 게임 시작: {room.Id} ({room.Members.Count}명)");

        BroadcastRoomUpdated(room);
        Broadcast(room, LobbyMessageType.GAME_STARTED, new GameStartedBroadcast { type = "GAME_STARTED", roomId = room.Id });
    }

    private void HandleChat(long senderId, ChatSendRequest request)
    {
        Room room = RoomOf(senderId);
        if (room == null) { SendError(senderId, "NOT_IN_ROOM", "방에 들어가 있지 않아요"); return; }

        string content = request.content == null ? "" : request.content.Trim();
        if (content.Length == 0) return;
        if (content.Length > ChatService.MaxLength) content = content.Substring(0, ChatService.MaxLength);

        // 보낸 사람을 포함해 방 모두에게 돌려준다. 보낸 글은 이 응답을 받아야 화면에 보인다.
        Broadcast(room, LobbyMessageType.CHAT_BROADCAST, new ChatBroadcast { type = "CHAT_BROADCAST", playerId = senderId, content = content });
    }

    // ───────────── 게임 쪽에서 쓰는 것 (게임 호스트 작업에서 연결한다) ─────────────

    /// <summary>방의 현재 모습. 게임을 시작할 때 참가자 목록과 설정을 읽는 데 쓴다. 없으면 null.</summary>
    public RoomSnapshot GetSnapshot(string roomId)
    {
        Room room = rooms.FirstOrDefault(r => r.Id == roomId);
        return room == null ? null : ToSnapshot(room);
    }

    /// <summary>플레이어가 들어가 있는 방이 게임 중이면 그 방의 모습. 아니면 null. 게임 씬이 열릴 때 게임을 만드는 데 쓴다.</summary>
    public RoomSnapshot GetPlayingRoom(long playerId)
    {
        Room room = RoomOf(playerId);
        return room != null && room.Status == RoomStatus.PLAYING ? ToSnapshot(room) : null;
    }

    /// <summary>게임이 끝났다. 대기방으로 되돌리고(준비 해제) 방 모두에게 알린다.</summary>
    public void FinishGame(string roomId)
    {
        Room room = rooms.FirstOrDefault(r => r.Id == roomId);
        if (room == null) return;

        room.Status = RoomStatus.WAITING;
        foreach (Member m in room.Members) m.Ready = m.Bot;
        BroadcastRoomUpdated(room);
    }

    // ───────────── 게임 중인 방으로 돌아오기 ─────────────

    /// <summary>이 사람이 게임 중인 방의 참가자인지. (연결이 끊겼다 돌아온 사람이 자기 자리로 돌아올 수 있는지 가리는 데 쓴다)</summary>
    public bool IsMemberOfPlayingRoom(long playerId)
    {
        Room room = RoomOf(playerId);
        return room != null && room.Status == RoomStatus.PLAYING;
    }

    /// <summary>
    /// 게임 중인 방의 참가자가 돌아왔을 때, 그 사람에게만 입장과 게임 시작을 다시 알린다.
    /// 화면은 이 알림으로 방에 들어와 게임 화면으로 가고, 게임 화면이 현재 게임 상태를 요청해서 따라잡는다.
    /// </summary>
    public bool RejoinPlayingRoom(long playerId)
    {
        Room room = RoomOf(playerId);
        if (room == null || room.Status != RoomStatus.PLAYING)
        {
            // 해 봐야 소용없는 경우다. 화면이 다시 시도하지 않고 포기하도록 다른 오류 이름으로 알린다.
            SendError(playerId, "NOT_REJOINABLE", "돌아갈 수 있는 게임이 없어요");
            return false;
        }

        // 파산한 사람은 관전하다 나가면 다시 들어올 수 없다. 같은 오류 이름이라 화면은 다시 시도하지 않고 이어하기도 지운다.
        HostGameService game = HostGameService.Current;
        if (game != null && game.IsRunning && game.IsBankrupt(playerId))
        {
            SendError(playerId, "NOT_REJOINABLE", "파산해서 이 게임에 다시 들어올 수 없어요");
            return false;
        }

        Debug.Log($"[LocalLobbyHost] 게임 중인 방으로 돌아옴: {room.Id} ({NicknameOf(playerId)})");
        Send(playerId, LobbyMessageType.ROOM_ENTERED, new RoomEnteredResponse { type = "ROOM_ENTERED" });
        Send(playerId, LobbyMessageType.ROOM_UPDATED, new RoomUpdatedBroadcast { type = "ROOM_UPDATED", room = ToSnapshot(room) });
        Send(playerId, LobbyMessageType.GAME_STARTED, new GameStartedBroadcast { type = "GAME_STARTED", roomId = room.Id });
        return true;
    }

    // ───────────── 다른 PC에서 온 사람 ─────────────

    /// <summary>다른 PC에서 온 사람의 닉네임과 초상화를 기억한다. 요청 JSON에는 이 값이 없어서 입장 전에 따로 알려 받는다.</summary>
    public void RegisterPlayer(long playerId, string nickname, int? portraitId)
    {
        if (!string.IsNullOrWhiteSpace(nickname)) nicknames[playerId] = nickname;
        if (portraitId.HasValue) portraits[playerId] = portraitId.Value;
    }

    /// <summary>이 사람이 방에 들어 있는지</summary>
    public bool IsMember(long playerId) => roomOfPlayer.ContainsKey(playerId);

    // ───────────── 방 이어받기 (방장이 바뀔 때) ─────────────

    /// <summary>
    /// 이미 있는 방 정보로 방을 되살린다. 방장이 바뀌어서 새 방장이 자기가 가진 방 정보로 로비 호스트를 시작할 때 쓴다.
    /// 같은 번호의 방이 이미 있으면 그 방을 이 정보로 바꾼다. 받은 방 설정은 복사해서 쓴다.
    /// </summary>
    public void RestoreRoom(RoomSnapshot snapshot)
    {
        Room room = new Room
        {
            Id = snapshot.roomId,
            // 이름 없이 저장된 방(옛 빌드가 만든 방)은 지금 방장의 닉네임으로 이름을 만들고, 그다음부터는 그 이름을 유지한다
            Name = !string.IsNullOrWhiteSpace(snapshot.roomName)
                ? snapshot.roomName
                : RoomNames.Default(snapshot.Players?.FirstOrDefault(p => p.playerId == snapshot.hostId)?.nickname),
            HostId = snapshot.hostId,
            Status = snapshot.status,
            Config = snapshot.config == null ? new GameConfig() : JsonConvert.DeserializeObject<GameConfig>(JsonConvert.SerializeObject(snapshot.config)),
            MaxPlayers = snapshot.maxPlayers,
            Members = snapshot.Players.Select(p => new Member
            {
                Id = p.playerId,
                Nickname = p.nickname,
                Ready = p.ready,
                Bot = p.bot,
                PortraitId = p.portraitId,
            }).ToList(),
        };

        // 같은 방이 이미 있으면 그 방과 그 방 사람들의 기록을 지우고 새 정보로 채운다
        rooms.RemoveAll(r => r.Id == room.Id);
        foreach (long oldMemberId in roomOfPlayer.Where(pair => pair.Value == room.Id).Select(pair => pair.Key).ToList())
            roomOfPlayer.Remove(oldMemberId);

        rooms.Add(room);
        foreach (Member member in room.Members.Where(m => !m.Bot))
        {
            roomOfPlayer[member.Id] = room.Id;
            nicknames[member.Id] = member.Nickname;
            if (member.PortraitId.HasValue) portraits[member.Id] = member.PortraitId.Value;
        }

        // 새 방을 만들 때 이 방과 번호가 겹치지 않게 한다. ("room-3"이면 다음은 4부터)
        if (room.Id.StartsWith("room-") && int.TryParse(room.Id.Substring(5), out int number) && number >= nextRoomNumber)
            nextRoomNumber = number + 1;
    }

    // ───────────── 방 도우미 ─────────────

    private Room RoomOf(long playerId)
    {
        return roomOfPlayer.TryGetValue(playerId, out string roomId) ? rooms.FirstOrDefault(r => r.Id == roomId) : null;
    }

    private static Member MemberOf(Room room, long playerId) => room.Members.First(m => m.Id == playerId);

    private string NicknameOf(long playerId) =>
        nicknames.TryGetValue(playerId, out string nickname) ? nickname : $"플레이어 {playerId}";

    private void AddHuman(Room room, long playerId)
    {
        Member member = new Member
        {
            Id = playerId,
            Nickname = NicknameOf(playerId),
            PortraitId = portraits.TryGetValue(playerId, out int portraitId) ? portraitId : (int?)null,
        };

        // 사람은 봇보다 앞에 들어간다 (봇은 맨 뒤에 붙어 있다)
        int firstBot = room.Members.FindIndex(m => m.Bot);
        if (firstBot < 0) room.Members.Add(member);
        else room.Members.Insert(firstBot, member);

        roomOfPlayer[playerId] = room.Id;
    }

    // 봇 수를 맞춘다. 봇은 맨 뒤에 붙이고 맨 뒤에서 뺀다. 최대 인원을 넘지 않고, 봇은 항상 준비된 상태다.
    private static void AdjustBots(Room room, int botCount)
    {
        // 줄이기: 맨 뒤의 봇부터 뺀다. 최대 인원보다 많이 들어와 있어도 봇을 먼저 뺀다.
        while (room.Members.Any(m => m.Bot) && (room.Members.Count(m => m.Bot) > botCount || room.Members.Count > room.MaxPlayers))
        {
            int last = room.Members.FindLastIndex(m => m.Bot);
            room.Members.RemoveAt(last);
        }

        // 늘리기: 아직 쓰지 않은 가장 작은 번호로 "봇 1", "봇 2"처럼 붙인다
        while (room.Members.Count(m => m.Bot) < botCount && room.Members.Count < room.MaxPlayers)
        {
            int number = 1;
            while (room.Members.Any(m => m.Nickname == $"봇 {number}")) number++;

            room.Members.Add(new Member { Id = BotIdBase + number, Nickname = $"봇 {number}", Ready = true, Bot = true });
        }
    }

    // ───────────── 응답 만들기와 보내기 ─────────────

    private static RoomSummary ToSummary(Room room)
    {
        Member host = room.Members.FirstOrDefault(m => m.Id == room.HostId);

        // 참가자 명단: 방장이 맨 앞, 그다음 다른 사람, 봇은 맨 뒤
        List<Member> ordered = room.Members.Where(m => m.Id == room.HostId)
            .Concat(room.Members.Where(m => m.Id != room.HostId && !m.Bot))
            .Concat(room.Members.Where(m => m.Id != room.HostId && m.Bot))
            .ToList();

        return new RoomSummary
        {
            roomId = room.Id,
            roomName = RoomNames.Resolve(room.Name, host?.Nickname),
            hostNickname = host != null ? host.Nickname : "",
            memberNicknames = ordered.Select(m => m.Nickname).ToList(),
            memberBots = ordered.Select(m => m.Bot).ToList(),
            mapId = room.Config.mapId,
            playerCount = room.Members.Count,
            maxPlayers = room.MaxPlayers,
        };
    }

    private static RoomSnapshot ToSnapshot(Room room)
    {
        return new RoomSnapshot
        {
            roomId = room.Id,
            roomName = RoomNames.Resolve(room.Name, room.Members.FirstOrDefault(m => m.Id == room.HostId)?.Nickname),
            hostId = room.HostId,
            mapId = room.Config.mapId,
            status = room.Status,
            config = room.Config,
            Players = room.Members.Select(m => new PlayerSnapshot
            {
                playerId = m.Id,
                nickname = m.Nickname,
                ready = m.Ready || m.Bot,
                bot = m.Bot,
                portraitId = m.PortraitId,
            }).ToList(),
            maxPlayers = room.MaxPlayers,
            botCount = room.Members.Count(m => m.Bot),
        };
    }

    private void BroadcastRoomUpdated(Room room)
    {
        RoomSnapshot snapshot = ToSnapshot(room);
        Broadcast(room, LobbyMessageType.ROOM_UPDATED, new RoomUpdatedBroadcast { type = "ROOM_UPDATED", room = snapshot });
        RoomUpdated?.Invoke(snapshot);
    }

    // 방에 있는 사람 모두에게 (봇 제외)
    private void Broadcast(Room room, LobbyMessageType type, object response)
    {
        string json = JsonConvert.SerializeObject(response);
        foreach (Member member in room.Members.Where(m => !m.Bot).ToList())
            deliver(member.Id, type, json);
    }

    private void Send(long playerId, LobbyMessageType type, object response)
    {
        deliver(playerId, type, JsonConvert.SerializeObject(response));
    }

    private void SendError(long playerId, string code, string message)
    {
        Debug.LogWarning($"[LocalLobbyHost] 요청 거절 {code}: {message} (보낸 사람 {playerId})");
        Send(playerId, LobbyMessageType.LOBBY_ERROR, new LobbyErrorResponse { type = "LOBBY_ERROR", code = code, message = message });
    }
}