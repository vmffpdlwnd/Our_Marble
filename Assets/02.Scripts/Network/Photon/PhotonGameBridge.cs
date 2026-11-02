using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

/// <summary>
/// 게임 진행(주사위, 구매, 건설 등)을 Photon에 이어 주는 쪽. 로비를 이은 PhotonLobbyBridge와 같은 방식이다.
///
///   참가자 → 방장   GameNetwork가 보내는 요청을 방장에게 보낸다. (내가 방장이면 네트워크를 타지 않고 내 호스트로 간다)
///   방장 → 참가자   방장 PC의 게임 호스트(HostGameService)가 내보내는 알림을 Photon으로 보낸다.
///
/// 게임 호스트는 방장 PC에서만 돈다. 규칙, 봇, 제한 시간, AFK가 모두 방장 PC에서 처리된다.
/// 게임 호스트는 게임 씬의 보드가 있어야 만들 수 있어서, 방장 PC의 게임 씬이 열리기 전에 온 요청은 보관했다가 만든 뒤에 넘긴다.
/// 반대로 알림은 받는 PC의 게임 씬(GameNetwork)이 열리기 전에는 보관했다가 열린 뒤에 넘긴다.
/// </summary>
public class PhotonGameBridge : IHostRequestReceiver
{
    private const long BotIdBase = 1000; // 봇의 playerId는 1001부터 (LocalLobbyHost와 같다)
    private const int RequestLogLimit = 6; // 게임마다 처음 몇 개의 요청만 로그로 남긴다 (게임 중에는 요청이 많다)
    private const float TakeOverSettleSeconds = 8f;  // 방장을 이어받을 때, 내 화면이 받은 알림을 다 반영하기를 기다리는 시간
    private const float TakeOverGiveUpSeconds = 20f; // 이어받을 게임 화면이 이 시간 안에 준비되지 않으면 이어받기를 포기한다

    private readonly PhotonConnection connection;

    private HostGameService gameHost;       // 내가 방장이고 게임 중일 때만 있다
    private int requestLogged;

    // 새 게임을 이 PC가 처음부터 시작해도 되는지. 이 PC의 로비 호스트가 방금 게임을 시작시켰을 때만 true다.
    // (게임 도중에 방장을 이어받은 PC는 false라서 게임을 처음부터 새로 만들지 않고, 진행 중인 게임을 이어받는다)
    private bool freshGameAllowed;
    private bool waitLogged;

    // 방장이 바뀌어서 진행 중인 게임을 이어받아야 하는 방. 이어받기가 끝나거나 포기하면 null
    private RoomSnapshot takeOverRoom;
    private float takeOverSince;
    private readonly HashSet<long> pendingAfk = new HashSet<long>(); // 이어받기 전에 나간 사람들. 이어받은 뒤 AFK로 표시한다
    private readonly Queue<KeyValuePair<PlayerRef, string>> queuedRequests = new Queue<KeyValuePair<PlayerRef, string>>(); // 방장 쪽: 호스트가 생기기 전의 요청
    private readonly Queue<KeyValuePair<GameMessageType, string>> queuedNotifies = new Queue<KeyValuePair<GameMessageType, string>>(); // 받는 쪽: 게임 씬이 열리기 전의 알림
    private readonly Dictionary<int, PlayerRef> seenRefs = new Dictionary<int, PlayerRef>(); // 요청을 보낸 사람의 연결 (접속자 목록이 늦어도 답할 수 있게)

    public bool IsActive { get; private set; }

    /// <summary>이 PC에서 지금까지 시작을 알린 게임 수. 같은 게임의 결과를 두 번 기록하지 않으려고 쓴다. (게임에 다시 들어와도 늘지 않는다)</summary>
    public int GameSerial { get; private set; }

    public PhotonGameBridge(PhotonConnection connection)
    {
        this.connection = connection;
    }

    // ───────────── 켜고 끄기 ─────────────

    /// <summary>게임 요청을 Photon으로 보내도록 바꾼다. (로비 연결을 켤 때 같이 켜진다)</summary>
    public void Enable()
    {
        if (IsActive) return;
        IsActive = true;

        GameNetwork.DefaultHost = this;
        connection.Messenger.RequestReceived += OnRequestReceived;
        connection.Messenger.NotifyReceived += OnNotifyReceived;
    }

    public void Disable()
    {
        if (!IsActive) return;
        IsActive = false;

        connection.Messenger.RequestReceived -= OnRequestReceived;
        connection.Messenger.NotifyReceived -= OnNotifyReceived;
        if (GameNetwork.DefaultHost == this) GameNetwork.DefaultHost = null;
        Reset();
    }

    /// <summary>방을 나가거나 연결이 끝났을 때 게임 관련 기록을 모두 지운다.</summary>
    public void Reset()
    {
        if (gameHost != null) HostGameService.Reset();
        gameHost = null;
        requestLogged = 0;
        freshGameAllowed = false;
        takeOverRoom = null;
        waitLogged = false;
        pendingAfk.Clear();
        queuedRequests.Clear();
        queuedNotifies.Clear();
        seenRefs.Clear();
    }

    /// <summary>새 게임이 시작될 때 이전 게임의 보관 중인 요청과 알림을 버린다.</summary>
    public void ResetForNewGame()
    {
        GameSerial++;
        gameHost = null;
        requestLogged = 0;
        takeOverRoom = null;
        waitLogged = false;
        pendingAfk.Clear();
        queuedRequests.Clear();
        queuedNotifies.Clear();
    }

    /// <summary>이 PC의 로비 호스트가 게임을 시작시켰다. 이 PC가 새 게임 호스트를 만들어도 된다.</summary>
    public void OnLocalHostStartedGame() => freshGameAllowed = true;

    /// <summary>
    /// 방장이 바뀌어 내가 새 방장이 되었고 게임이 진행 중이면, 그 게임을 내 화면의 상태로 이어받을 준비를 한다.
    /// (내 화면이 받은 알림을 다 반영하고 입력을 기다리는 때에 이어받는다. Tick이 이어받는다)
    /// </summary>
    /// <param name="previousMasterId">나간 이전 방장의 번호. 그 사람의 차례는 AFK로 보고 자동으로 진행한다</param>
    public void BeginTakeOver(RoomSnapshot room, int previousMasterId)
    {
        if (!IsActive) return;

        gameHost = null;
        takeOverRoom = room;
        takeOverSince = Time.unscaledTime;
        if (previousMasterId > 0) pendingAfk.Add(previousMasterId);
        Log("방장이 바뀌어 진행 중인 게임을 이어받을 준비를 합니다");
    }

    /// <summary>방에 있던 사람이 나갔다. 게임 중이면 그 사람의 차례를 기다리지 않고 자동으로 진행하게 한다.</summary>
    public void OnPlayerGone(long playerId)
    {
        if (gameHost != null) gameHost.MarkAfk(playerId);
        else if (takeOverRoom != null) pendingAfk.Add(playerId);
    }

    // ───────────── 참가자 쪽: 요청을 방장에게 (GameNetwork가 부른다) ─────────────

    /// <param name="senderId">화면이 적은 내 번호. Photon에서는 쓰지 않는다. (보낸 사람은 연결로 정한다)</param>
    public void OnRequest(long senderId, string json)
    {
        if (!connection.IsInSession)
        {
            Log("방 안에 있지 않아 게임 요청을 보내지 못했습니다");
            return;
        }

        connection.Messenger.SendRequest(PhotonMessenger.DomainGame, json);
    }

    // ───────────── 방장 쪽: 요청을 게임 호스트에게 ─────────────

    private void OnRequestReceived(PlayerRef sender, int domain, string json)
    {
        if (!IsActive || domain != PhotonMessenger.DomainGame) return;
        if (!connection.IsMaster) return; // 방장이 아니면 처리할 곳이 없다

        seenRefs[sender.PlayerId] = sender;
        queuedRequests.Enqueue(new KeyValuePair<PlayerRef, string>(sender, json));
        PumpRequests();
    }

    // 호스트가 있으면 보관 중인 요청을 순서대로 넘긴다. 호스트를 아직 만들 수 없으면(방장 PC의 게임 씬이 열리기 전) 계속 보관한다.
    private void PumpRequests()
    {
        if (queuedRequests.Count == 0) return;

        // 게임 도중에 방장을 이어받는 중이면, 이어받기가 끝날 때까지 요청을 보관한다
        if (takeOverRoom != null) return;

        if (gameHost == null || !gameHost.IsRunning) TryCreateHost();
        if (gameHost == null)
        {
            if (!waitLogged)
            {
                waitLogged = true;
                Log("게임 호스트가 아직 없어 요청을 보관합니다");
            }
            return;
        }

        while (queuedRequests.Count > 0)
        {
            KeyValuePair<PlayerRef, string> request = queuedRequests.Dequeue();

            // 게임은 사람을 게임 번호로 구분한다. 연결이 끊겼다 돌아온 사람은 연결 번호가 바뀌어도 예전 게임 번호다.
            long gameId = connection.Registry.GameIdOfRef(request.Key.PlayerId);
            if (requestLogged < RequestLogLimit)
            {
                requestLogged++;
                Log($"게임 요청을 호스트에 넘깁니다 (보낸 사람 {gameId}번)");
            }
            gameHost.OnRequest(gameId, request.Value);
        }
    }

    // 진행 중인 방이 있고 게임 씬이 열려 있으면 게임 호스트를 만든다.
    private void TryCreateHost()
    {
        // 이 PC의 로비 호스트가 시작시킨 게임만 새로 만든다. 게임 도중에 이어받은 PC가 게임을 처음부터 새로 시작하면 안 된다.
        if (!freshGameAllowed || !HostGameService.CanStart) return;

        RoomSnapshot room = LocalLobbyHost.Instance.GetPlayingRoom(LocalPlayer.Id);
        if (room == null) return; // 게임이 끝났거나 아직 시작 전이다

        gameHost = HostGameService.StartNew(room, Deliver);
        freshGameAllowed = false;
        Log($"게임 호스트를 시작했습니다 (사람 {CountHumans(room)}명, 전체 {room.Players.Count}명)");
    }

    private static int CountHumans(RoomSnapshot room)
    {
        int count = 0;
        foreach (PlayerSnapshot player in room.Players) if (!player.bot) count++;
        return count;
    }

    // 게임 호스트가 내보내는 알림을 Photon으로 보낸다. 봇이나 이미 나간 사람에게는 보내지 않는다.
    private void Deliver(long playerId, GameMessageType type, string json)
    {
        if (playerId > BotIdBase) return;

        if (!TryFindPlayer(playerId, out PlayerRef target))
        {
            Log($"게임 알림을 보낼 사람을 찾지 못했습니다: {type} → {playerId}번");
            return;
        }

        connection.Messenger.SendNotify(target, PhotonMessenger.DomainGame, type.ToString(), json);
    }

    private bool TryFindPlayer(long playerId, out PlayerRef found)
    {
        // 게임 번호의 사람이 지금 어느 연결로 들어와 있는지는 방장의 기록이 안다
        if (connection.Registry.TryGetRef(playerId, out int refId))
        {
            found = connection.FindPlayerRef(refId);
            return true;
        }

        foreach (PlayerRef player in connection.Players)
        {
            if (player.PlayerId == playerId) { found = player; return true; }
        }
        return seenRefs.TryGetValue((int)playerId, out found);
    }

    // ───────────── 받는 쪽: 알림을 게임 화면(GameNetwork)에게 ─────────────

    private void OnNotifyReceived(PlayerRef sender, int domain, string typeName, string json)
    {
        if (!IsActive || domain != PhotonMessenger.DomainGame) return;

        if (!Enum.TryParse(typeName, out GameMessageType type))
        {
            Log("알 수 없는 게임 알림입니다: " + typeName);
            return;
        }

        queuedNotifies.Enqueue(new KeyValuePair<GameMessageType, string>(type, json));
        PumpNotifies();
    }

    // 게임 씬의 GameNetwork가 있을 때 보관 중인 알림을 순서대로 넘긴다.
    // GameNetwork.Instance는 없으면 새로 만들어 버려서, 있는지만 찾아본다.
    private void PumpNotifies()
    {
        if (queuedNotifies.Count == 0) return;

        GameNetwork network = UnityEngine.Object.FindFirstObjectByType<GameNetwork>();
        if (network == null) return; // 게임 씬이 열리기 전

        while (queuedNotifies.Count > 0)
        {
            KeyValuePair<GameMessageType, string> notify = queuedNotifies.Dequeue();
            network.ReceiveGameMessage(notify.Key, notify.Value);
        }
    }

    // ───────────── 게임 도중에 방장 이어받기 ─────────────

    // 내 화면이 가진 게임 상태를 복사해서 게임 호스트를 시작하고, 모든 화면을 그 상태에 다시 맞춘다.
    // 내 화면이 아직 받은 알림을 반영하는 중이면(쌓인 알림이 있거나 입력을 기다리는 상태가 아니면) 잠깐 기다린다.
    private void TryTakeOver()
    {
        if (takeOverRoom == null) return;

        // 없으면 새로 만들어 버리는 Instance 대신, 씬에 있는 것만 찾는다
        GameManager manager = UnityEngine.Object.FindFirstObjectByType<GameManager>();
        GameNetwork network = UnityEngine.Object.FindFirstObjectByType<GameNetwork>();
        float waited = Time.unscaledTime - takeOverSince;

        if (!HostGameService.CanStart || manager == null || manager.gameState == null || network == null)
        {
            if (waited >= TakeOverGiveUpSeconds)
            {
                Log("내 게임 화면이 준비되지 않아 게임을 이어받지 못했습니다");
                takeOverRoom = null;
            }
            return;
        }

        GameState clientState = manager.gameState;
        if (clientState.IsGameOver)
        {
            Log("게임이 이미 끝났으므로 이어받지 않습니다");
            takeOverRoom = null;
            return;
        }

        bool settled = network.PendingMessageCount == 0 && clientState.WaitingType != null;
        if (!settled && waited < TakeOverSettleSeconds) return;

        gameHost = HostGameService.TakeOver(clientState, takeOverRoom.roomId, LocalPlayer.Id, network.ProcessedBroadcastCount, Deliver);
        foreach (long id in pendingAfk) gameHost.MarkAfk(id);
        pendingAfk.Clear();
        gameHost.SyncAllHumans();

        Log($"게임을 이어받았습니다 ({clientState.TurnNumber}번째 턴, {(settled ? "입력 대기 중" : $"{waited:0.0}초 기다린 뒤")})");
        takeOverRoom = null;
    }

    // ───────────── 매 프레임 ─────────────

    /// <summary>보관 중인 요청과 알림을 넘길 수 있는지 확인한다. 연결이 방 안일 때 매 프레임 부른다.</summary>
    public void Tick()
    {
        if (!IsActive) return;

        TryTakeOver();
        PumpRequests();
        PumpNotifies();
    }

    private void Log(string message) => connection.WriteLog("[게임] " + message);
}