using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// 게임을 진행하는 호스트 쪽 코드. 예전에 서버가 하던 일이다. (기획서 5장 "호스트 권위 방식")
/// 클라이언트의 요청(주사위 굴리기 등)을 받아 검증하고, 결과를 모두에게 알린 뒤, 흐름을 다음 입력 단계까지 끝까지 처리한다.
///
/// 클라이언트(GameManager)도 같은 규칙으로 도착 처리, 월급, 턴 전환 등을 계산해 자기 GameState를 갱신한다.
/// 그래서 진행 순서는 GameManager의 같은 이름 함수와 맞춰 두었고, 각 함수 위에 짝이 되는 GameManager 함수를 적어 두었다.
/// 규칙을 바꿀 때는 양쪽을 함께 고친다.
///
/// 파일이 길어서 역할별로 나누어 둔 partial class다.
///   HostGameService.cs          생성, 요청 받기, 검증, 알림 보내기
///   HostGameService.Property.cs 땅과 경제: 구매, 건설, 인수, 매각, 통행료, 파산
///   HostGameService.AutoPlay.cs 자동 진행: 봇 차례, 제한 시간, AFK (시간은 HostAutoPlayRunner가 흘려 준다)
///   HostGameService.Flow.cs     진행: 주사위, 이동, 도착, 턴 전환, 카드, 세계여행, 종료
/// </summary>
public partial class HostGameService : IHostRequestReceiver
{
    private const int IslandTurns = 3; // GameManager.IslandTurns와 같아야 한다 (무인도 영업정지 턴 수)

    private readonly Action<long, GameMessageType, string> deliver;
    private readonly string roomId;
    private readonly System.Random random = new System.Random();
    private readonly GameState state;

    private bool started; // 첫 GAME_STATE_REQUEST를 받아 첫 차례를 시작했는지

    // ───────────── 이 PC의 게임 호스트 ─────────────

    private static HostGameService current;

    /// <summary>
    /// 이 PC에서 진행 중인 게임의 호스트. 게임 씬이 처음 요청을 보낼 때 로비 호스트의 방 정보로 만든다.
    /// 시작한 게임이 없으면 null.
    /// </summary>
    public static HostGameService GetOrCreate()
    {
        if (current != null && !current.state.IsGameOver) return current;

        RoomSnapshot room = LocalLobbyHost.Instance.GetPlayingRoom(LocalPlayer.Id);
        if (room == null) return current; // 게임이 끝난 뒤 늦게 온 요청은 끝난 게임이 오류로 답한다

        if (BoardManager.Instance == null || BoardManager.Instance.BoardData == null)
        {
            Debug.LogError("[HostGameService] BoardManager에 BoardData가 없어 게임을 만들 수 없습니다.");
            return null;
        }

        current = new HostGameService(room, DeliverToLocalClient);
        HostAutoPlayRunner.Create(current); // 봇 차례와 제한 시간을 돌린다
        return current;
    }

    /// <summary>진행 중인 게임을 버린다. 타이틀로 돌아갈 때 부른다.</summary>
    public static void Reset() => current = null;

    // 에디터에서 도메인 리로드 없이 플레이를 다시 시작해도 이전 플레이의 게임이 남지 않게 한다
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => current = null;

    // 이 PC에서 플레이하는 사람에게만 전달한다. (봇은 받을 필요가 없다)
    // 서버가 답하던 때처럼 한 프레임 뒤에 전달한다. 게임 씬이 열리는 순간에 바로 전달하면, 씬의 오브젝트들이 Start를 마치기 전에
    // 게임 상태(GAME_STATE_SYNC)가 적용돼 버린다. 같은 프레임에 보낸 순서는 그대로 지켜진다.
    private static void DeliverToLocalClient(long playerId, GameMessageType type, string json)
    {
        if (playerId != LocalPlayer.Id) return;

        GameNetwork network = GameNetwork.Instance;
        network.StartCoroutine(DeliverNextFrame(network, type, json));
    }

    private static IEnumerator DeliverNextFrame(GameNetwork network, GameMessageType type, string json)
    {
        yield return null;
        if (network != null) network.ReceiveGameMessage(type, json);
    }

    // ───────────── 게임 만들기 ─────────────

    /// <param name="room">게임을 시작한 방. 참가자(사람과 봇)와 설정을 여기서 읽는다</param>
    /// <param name="deliver">알림을 받는 사람에게 보낸다. (playerId, 알림 종류, 알림 JSON)</param>
    public HostGameService(RoomSnapshot room, Action<long, GameMessageType, string> deliver)
    {
        this.deliver = deliver;
        roomId = room.roomId;

        GameConfig config = room.config ?? new GameConfig();

        List<PlayerState> players = room.Players
            .Select(p => new PlayerState(p.playerId)
            {
                Nickname = p.nickname,
                Money = config.startMoney,
                IsBot = p.bot,
                PortraitId = p.portraitId,
            })
        .ToList();  
        // 선턴을 무작위로 정한다. 턴 순서는 이 목록의 순서다.
        for (int i = players.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (players[i], players[j]) = (players[j], players[i]);
        } 

        // 이 보드에 있는 땅마다 주인 없는 땅 상태를 만든다
        List<PropertyState> properties = BoardManager.Instance.BoardData.Tiles
            .Where(t => t.Type == TileType.PROPERTY)
            .Select(t => new PropertyState(t.PropertyId))
            .ToList();

        // TurnNumber 1은 "아직 게임이 시작되지 않은 최초 상태"라는 표시다. (GameManager.SyncGameState가 이 값을 보고 StartGame을 부른다)
        state = new GameState
        {
            TurnNumber = 1,
            RoundNumber = 1,
            CurrentPlayerId = players[0].PlayerId,
            WelfareFund = 0,
            WaitingType = null,
            IsDouble = false,
            ConsecutiveDoubleCount = 0,
            IsGameOver = false,
            PlayerOrder = players.Select(p => p.PlayerId).ToList(),
            PlayerStates = players,
            PropertyStates = properties,
            Config = config,
            HostId = room.hostId,
            MaxPlayers = room.maxPlayers,
        };

        Debug.Log($"[HostGameService] 게임 생성: 방 {roomId}, {players.Count}명, 땅 {properties.Count}개, 시작 자금 {config.startMoney}");
    }

    // ───────────── 요청 받기 ─────────────

    public void OnRequest(long senderId, string json)
    {
        string type;
        try
        {
            type = (string)JObject.Parse(json)["type"];
        }
        catch (Exception e)
        {
            Debug.LogError($"[HostGameService] 요청을 읽지 못했습니다: {json}\n{e.Message}");
            SendError(senderId, "BAD_REQUEST", "요청을 처리하지 못했어요");
            return;
        }

        Debug.Log($"[HostGameService] 요청 수신: {type} (보낸 사람 {senderId})");

        afkPlayers.Remove(senderId); // 요청을 보내면 AFK가 풀린다. (재접속의 GAME_STATE_REQUEST도 같다)

        switch (type)
        {
            case "GAME_STATE_REQUEST": HandleGameStateRequest(senderId); break;
            case "ROLL_DICE": HandleRollDice(senderId); break;
            case "PURCHASE_PROPERTY": HandlePurchaseProperty(senderId, JsonConvert.DeserializeObject<PropertyDecisionRequest>(json)); break;
            case "BUILD": HandleBuild(senderId, JsonConvert.DeserializeObject<PropertyDecisionRequest>(json)); break;
            case "ACQUIRE_PROPERTY": HandleAcquireProperty(senderId, JsonConvert.DeserializeObject<PropertyDecisionRequest>(json)); break;
            case "SELL_PROPERTIES": HandleSellProperties(senderId, JsonConvert.DeserializeObject<SellPropertiesRequest>(json)); break;
            case "DRAW_CARD": HandleDrawCard(senderId); break;
            case "CHOOSE_DESTINATION": HandleChooseDestination(senderId, JsonConvert.DeserializeObject<ChooseDestinationRequest>(json)); break;

            default:
                Debug.LogWarning($"[HostGameService] 알 수 없는 요청입니다: {type}");
                SendError(senderId, "UNKNOWN_REQUEST", "요청을 처리하지 못했어요");
                break;
        }
    }

    // ───────────── 요청 처리 ─────────────

    // 게임 씬이 열리면 모든 클라이언트가 보낸다. (재접속도 같다)
    private void HandleGameStateRequest(long senderId)
    {
        SendState(senderId);

        // 최초 요청이면 상태를 보낸 뒤에 첫 차례를 시작한다.
        // (보낸 상태는 TurnNumber 1이라서 받은 쪽이 StartGame을 부르고, 호스트도 같은 시점에 첫 차례를 시작해 둘이 같은 상태가 된다)
        if (!started)
        {
            started = true;
            Debug.Log("[HostGameService] 게임 시작");
            StartTurn(state.PlayerOrder[0]);
        }
    }

    // ───────────── 검증 (기획서 5장 "검증 순서") ─────────────

    // 1. 진행 중인 게임이 있는가  2. (땅 번호가 있다면) 존재하는 땅인가  3. 요청자의 차례인가  4. 파산하지 않았는가  5. 요청이 현재 단계에 맞는가
    // (7번은 ValidateOnProperty, 8번은 각 요청 처리에서 하고, 6번 제한 시간은 자동 진행 작업에서 넣는다)
    private bool Validate(long senderId, GameMessageType expectedPhase, out PlayerState player, int? propertyId = null, IEnumerable<int> propertyIds = null)
    {
        player = GetPlayer(senderId);

        if (state.IsGameOver) { SendError(senderId, "GAME_OVER", "이미 끝난 게임이에요"); return false; }
        if (propertyId.HasValue && GetProperty(propertyId.Value) == null) { SendError(senderId, "NO_SUCH_PROPERTY", "존재하지 않는 땅이에요"); return false; }
        if (propertyIds != null && propertyIds.Any(id => GetProperty(id) == null)) { SendError(senderId, "NO_SUCH_PROPERTY", "존재하지 않는 땅이에요"); return false; }
        if (player == null) { SendError(senderId, "NOT_IN_GAME", "이 게임의 참가자가 아니에요"); return false; }
        if (state.CurrentPlayerId != senderId) { SendError(senderId, "NOT_YOUR_TURN", "내 차례가 아니에요"); return false; }
        if (player.IsBankrupt) { SendError(senderId, "BANKRUPT", "파산해서 요청할 수 없어요"); return false; }
        if (state.WaitingType != expectedPhase)
        {
            SendError(senderId, "INVALID_PHASE", "지금은 보낼 수 없는 요청이에요");
            return false;
        }

        return true;
    }

    // 7. (구매·건설·인수) 요청자의 말이 그 땅 위에 있는가
    private bool ValidateOnProperty(long senderId, PlayerState player, int propertyId)
    {
        TileData tile = BoardManager.Instance.GetTileData(player.Position);
        if (tile == null || tile.Type != TileType.PROPERTY || tile.PropertyId != propertyId)
        {
            SendError(senderId, "NOT_ON_PROPERTY", "그 땅 위에 있지 않아요");
            return false;
        }
        return true;
    }

    private PlayerState GetPlayer(long playerId) => state.PlayerStates.Find(p => p.PlayerId == playerId);

    private PropertyState GetProperty(int propertyId) => state.PropertyStates.Find(p => p.PropertyId == propertyId);

    // ───────────── 알림 보내기 ─────────────

    // 게임에 참가한 사람 모두에게 (봇 제외, 파산한 사람도 지켜볼 수 있게 포함)
    private void Broadcast(GameMessageType type, object message)
    {
        actionSerial++; // 요청이 처리됐다. 기다린 시간을 새로 세게 한다
        string json = JsonConvert.SerializeObject(message);
        foreach (PlayerState player in state.PlayerStates.Where(p => !p.IsBot).ToList())
            deliver(player.PlayerId, type, json);
    }

    // GAME_STATE_SYNC: 지금의 게임 상태 전체를 한 사람에게 보낸다
    private void SendState(long playerId)
    {
        deliver(playerId, GameMessageType.GAME_STATE_SYNC, JsonConvert.SerializeObject(new { gameState = state }));
    }

    // 검증에 실패하면 상태는 바뀌지 않고 요청한 사람에게만 오류를 보낸다
    private void SendError(long playerId, string code, string message)
    {
        Debug.LogWarning($"[HostGameService] 요청 거절 {code}: {message} (보낸 사람 {playerId})");
        deliver(playerId, GameMessageType.GAME_ERROR,
            JsonConvert.SerializeObject(new GameErrorResponse { type = "GAME_ERROR", code = code, message = message }));
    }
}