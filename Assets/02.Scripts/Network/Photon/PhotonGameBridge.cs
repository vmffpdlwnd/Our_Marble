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

    private readonly PhotonConnection connection;

    private HostGameService gameHost;       // 내가 방장이고 게임 중일 때만 있다
    private int requestLogged;
    private readonly Queue<KeyValuePair<PlayerRef, string>> queuedRequests = new Queue<KeyValuePair<PlayerRef, string>>(); // 방장 쪽: 호스트가 생기기 전의 요청
    private readonly Queue<KeyValuePair<GameMessageType, string>> queuedNotifies = new Queue<KeyValuePair<GameMessageType, string>>(); // 받는 쪽: 게임 씬이 열리기 전의 알림
    private readonly Dictionary<int, PlayerRef> seenRefs = new Dictionary<int, PlayerRef>(); // 요청을 보낸 사람의 연결 (접속자 목록이 늦어도 답할 수 있게)

    public bool IsActive { get; private set; }

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
        queuedRequests.Clear();
        queuedNotifies.Clear();
        seenRefs.Clear();
    }

    /// <summary>새 게임이 시작될 때 이전 게임의 보관 중인 요청과 알림을 버린다.</summary>
    public void ResetForNewGame()
    {
        gameHost = null;
        requestLogged = 0;
        queuedRequests.Clear();
        queuedNotifies.Clear();
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

        if (gameHost == null || !gameHost.IsRunning) TryCreateHost();
        if (gameHost == null) return;

        while (queuedRequests.Count > 0)
        {
            KeyValuePair<PlayerRef, string> request = queuedRequests.Dequeue();
            if (requestLogged < RequestLogLimit)
            {
                requestLogged++;
                Log($"게임 요청을 호스트에 넘깁니다 (보낸 사람 {request.Key.PlayerId}번)");
            }
            gameHost.OnRequest(request.Key.PlayerId, request.Value);
        }
    }

    // 진행 중인 방이 있고 게임 씬이 열려 있으면 게임 호스트를 만든다.
    private void TryCreateHost()
    {
        if (!HostGameService.CanStart) return;

        RoomSnapshot room = LocalLobbyHost.Instance.GetPlayingRoom(LocalPlayer.Id);
        if (room == null) return; // 게임이 끝났거나 아직 시작 전이다

        gameHost = HostGameService.StartNew(room, Deliver);
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

    // ───────────── 매 프레임 ─────────────

    /// <summary>보관 중인 요청과 알림을 넘길 수 있는지 확인한다. 연결이 방 안일 때 매 프레임 부른다.</summary>
    public void Tick()
    {
        if (!IsActive) return;

        PumpRequests();
        PumpNotifies();
    }

    private void Log(string message) => connection.WriteLog("[게임] " + message);
}