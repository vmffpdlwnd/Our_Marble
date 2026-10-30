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
/// 코드가 길어서 #region으로 구역을 나눴다. 위에서부터:
///   (구역 밖)  생성, 요청 받기, 검증, 알림 보내기
///   진행: 주사위, 이동, 도착, 턴 전환, 카드, 세계여행, 종료
///   땅과 경제: 구매, 건설, 인수, 매각, 통행료, 파산
///   자동 진행: 봇 차례, 제한 시간, AFK (시간은 HostAutoPlayRunner가 흘려 준다)
///   개발용 조회: 호스트와 클라이언트의 상태를 비교하는 HostStateCheck가 읽는다
/// </summary>
public class HostGameService : IHostRequestReceiver
{
    private const int IslandTurns = GameRules.IslandTurns; // GameManager.IslandTurns와 같아야 한다 (무인도 영업정지 턴 수)

    private readonly Action<long, GameMessageType, string> deliver;
    private readonly string roomId;
    private readonly System.Random random = new System.Random();
    private readonly GameState state;
    private readonly GameData gameData; // 규칙이 읽는 보드, 땅 가격표, 카드

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

    /// <summary>
    /// 이미 진행 중인 게임 상태를 이어받아 호스트를 시작한다. 방장이 바뀔 때 새 방장이 자기가 가진 게임 상태로 호스트 역할을 이어받는 데 쓴다.
    /// 받은 상태는 복사해서 쓴다. (새 방장의 클라이언트가 가진 상태와 같은 객체를 쓰면 같은 변화가 두 번 반영된다)
    /// 호스트만 알던 값(AFK 표시, 제한 시간이 얼마나 흘렀는지)은 비어 있는 상태로 시작한다. 이어받는 방법은 Photon 연결 때 정한다.
    /// </summary>
    /// <param name="existing">이어받을 게임 상태. 입력을 기다리는 때(WaitingType이 있을 때)의 상태여야 한다</param>
    /// <param name="roomId">이어받는 방의 번호</param>
    /// <param name="hostId">새 방장의 playerId. 게임 상태에 적힌 방장도 이 값으로 바꾼다</param>
    /// <param name="broadcastCount">지금까지 클라이언트가 처리한 진행 알림 수. 개발용 상태 비교(HostStateCheck)가 이 값으로 둘을 맞춰 본다</param>
    /// <param name="deliver">알림을 보내는 쪽. 정하지 않으면 이 PC의 플레이어에게만 전달한다</param>
    public static HostGameService TakeOver(GameState existing, string roomId, long hostId, int broadcastCount, Action<long, GameMessageType, string> deliver = null)
    {
        HostGameService host = new HostGameService(existing, roomId, hostId, broadcastCount, deliver ?? DeliverToLocalClient);
        current = host;
        HostAutoPlayRunner.Create(host); // 봇 차례와 제한 시간을 돌린다
        return host;
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
        gameData = GameDataFactory.FromScene();
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

    // 이미 진행 중인 게임 상태로 호스트를 만든다. (TakeOver가 부른다)
    private HostGameService(GameState existing, string roomId, long hostId, int broadcastCount, Action<long, GameMessageType, string> deliver)
    {
        this.deliver = deliver;
        this.roomId = roomId;
        gameData = GameDataFactory.FromScene();

        // 게임 상태를 주고받을 때와 같은 방식으로 복사한다
        state = JsonConvert.DeserializeObject<GameState>(JsonConvert.SerializeObject(existing));
        state.HostId = hostId;

        started = true;                 // 첫 차례는 이미 시작된 게임이다
        actionSerial = broadcastCount;  // 클라이언트가 처리한 알림 수에 맞춘다

        Debug.Log($"[HostGameService] 게임 이어받기: 방 {roomId}, {state.PlayerStates.Count}명, {state.TurnNumber}번째 턴, 대기 단계 {state.WaitingType}");
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

    #region 진행: 주사위, 이동, 도착, 턴 전환, 카드, 세계여행, 종료
    // ───────────── 요청 처리 ─────────────

    private void HandleRollDice(long senderId)
    {
        if (!Validate(senderId, GameMessageType.DICE_ROLLED, out PlayerState player)) return;

        int dice1 = random.Next(1, 7);
        int dice2 = random.Next(1, 7);
        Debug.Log($"[HostGameService] 주사위: {player.Nickname} {dice1}+{dice2}{(dice1 == dice2 ? " (더블)" : "")}");

        // 요청 하나가 흐름을 다음 입력 단계까지 끝까지 처리한다. 그 사이에는 입력을 받지 않는다.
        state.WaitingType = null;
        Broadcast(GameMessageType.DICE_ROLLED, new DiceRolledBroadcast { playerId = senderId, dice1 = dice1, dice2 = dice2 });

        ApplyDiceResult(player, dice1, dice2);
    }

    // GameManager.HandleCardDrawn
    // 황금열쇠 칸에 도착해 카드 단계(CARD_DRAWN)에 있을 때, 카드를 뽑는다. 카드는 매번 전체 중 무작위다.
    private void HandleDrawCard(long senderId)
    {
        if (!Validate(senderId, GameMessageType.CARD_DRAWN, out PlayerState player)) return;

        // 뽑을 카드가 없으면 턴이 멈추지 않도록 알림 없이 턴을 종료한다. (원본 서버와 같다)
        IReadOnlyList<int> cardIds = gameData.CardIds;
        CardData card = null;
        int cardId = 0;
        if (cardIds != null && cardIds.Count > 0)
        {
            cardId = cardIds[random.Next(cardIds.Count)];
            card = gameData.FindCard(cardId);
        }

        if (card == null)
        {
            Debug.LogWarning("[HostGameService] 뽑을 카드가 없어 턴을 종료합니다.");
            state.WaitingType = null;
            ProcessEndTurn();
            return;
        }
        
        state.WaitingType = null;
        Broadcast(GameMessageType.CARD_DRAWN, new CardDrawnBroadcast { playerId = senderId, cardId = cardId });
        Debug.Log($"[HostGameService] 황금열쇠: {player.Nickname} '{card.cardName}' ({card.effectType})");

        ExecuteCardEffect(player, card);
    }

    // GameManager.HandleDestinationChosen
    // 세계여행 칸에서 차례를 시작해 목적지 단계(DESTINATION_CHOSEN)에 있을 때, 목적지를 받는다.
    private void HandleChooseDestination(long senderId, ChooseDestinationRequest request)
    {
        if (!Validate(senderId, GameMessageType.DESTINATION_CHOSEN, out PlayerState player)) return;

        // 8. 기능별 검증: 보드 안의 칸이어야 하고, 세계여행 칸 자체는 목적지로 고를 수 없다
        int to = request.destinationPosition;
        DestinationError destinationError = ArrivalRules.CheckDestination(gameData, to);
        if (destinationError == DestinationError.Invalid) { SendError(senderId, "INVALID_DESTINATION", "갈 수 없는 칸이에요"); return; }
        if (destinationError == DestinationError.WorldTravelTile) { SendError(senderId, "INVALID_DESTINATION", "세계여행 칸은 목적지로 고를 수 없어요"); return; }

        state.WaitingType = null;
        Broadcast(GameMessageType.DESTINATION_CHOSEN, new DestinationChosenBroadcast { playerId = senderId, destinationPosition = to });

        int from = player.Position;
        Debug.Log($"[HostGameService] 세계여행: {player.Nickname} {from}번 → {to}번 칸");

        MovePlayer(player, to, to < from); // 출발 지점을 지나면 월급
    }

    // ───────────── 게임 진행 (GameManager와 같은 순서) ─────────────

    // GameManager.HandleTurnChanged
    // 새 차례를 시작한다. 차례와 대기 단계는 규칙(GameRules.StartTurn)이 정하고, 여기서는 게임이 끝나는 경우를 처리한다.
    private void StartTurn(long playerId)
    {
        switch (GameRules.StartTurn(state, gameData, playerId))
        {
            case TurnStartResult.AlreadyOver:
                return;

            case TurnStartResult.LastPlayerLeft:
                EndGame("남은 플레이어가 1명입니다.");
                return;

            case TurnStartResult.RoundLimit:
                EndGame($"최대 라운드({state.Config.maxRound}라운드)에 도달했습니다.");
                return;
        }

        PlayerState player = GetPlayer(playerId);
        Debug.Log($"[HostGameService] ===== {state.RoundNumber}/{state.Config.maxRound}라운드 ({state.TurnNumber}번째 턴): {player.Nickname} 차례 =====");
    }

    // GameManager.ContinueAfterDiceResult
    // 더블 횟수, 무인도 남은 턴 같은 상태 변경과 이동 방법은 규칙(GameRules.ApplyDice)이 정하고, 여기서는 그 결과대로 이어서 진행한다.
    private void ApplyDiceResult(PlayerState player, int dice1, int dice2)
    {
        DiceOutcome outcome = GameRules.ApplyDice(state, gameData, player, dice1, dice2);

        switch (outcome.Kind)
        {
            case DiceMoveKind.IslandStay:
                Debug.Log($"[HostGameService] 무인도 탈출 실패: {player.Nickname} (남은 {outcome.IslandTurnsRemaining}턴)");
                StartTurn(GetNextPlayerId());
                return;

            case DiceMoveKind.DirectToIsland:
                if (outcome.EscapedIsland) Debug.Log($"[HostGameService] 무인도 탈출: {player.Nickname}");
                Debug.Log($"[HostGameService] 3연속 더블: {player.Nickname} 무인도로 이동");
                MovePlayerDirectly(player, outcome.To);
                return;

            default:
                if (outcome.EscapedIsland) Debug.Log($"[HostGameService] 무인도 탈출: {player.Nickname}");
                MovePlayer(player, outcome.To, outcome.ReceivesSalary);
                return;
        }
    }

    // GameManager.HandlePlayerMoved
    private void MovePlayer(PlayerState player, int to, bool shouldReceiveSalary)
    {
        long salary = GameRules.MoveTo(state, player, to, shouldReceiveSalary);

        if (shouldReceiveSalary)
            Debug.Log($"[HostGameService] 월급: {player.Nickname} +{salary} (현금 {player.Money})");

        ProcessArrival(player, to);
    }

    // GameManager.MovePlayerDirectly (월급 없는 순간이동)
    private void MovePlayerDirectly(PlayerState player, int to)
    {
        GameRules.MoveTo(state, player, to, false);
        ProcessArrival(player, to);
    }

    // GameManager.ProcessArrival
    // 도착한 칸에 따라 처리한다. 칸 종류별 처리와 다음에 기다릴 입력(WaitingType)은 규칙(ArrivalRules)이 정하고, 여기서는 그 결과대로 이어서 진행한다.
    private void ProcessArrival(PlayerState player, int position)
    {
        TileArrival arrival = ArrivalRules.ArriveAtTile(state, gameData, player, position);

        if (arrival.Kind == TileArrivalKind.MissingTile)
        {
            Debug.LogError($"[HostGameService] 도착한 칸 {position}의 데이터가 없습니다.");
            ProcessEndTurn();
            return;
        }

        Debug.Log($"[HostGameService] 도착: {player.Nickname} → {position}번 칸 ({arrival.Tile.Type}, 현금 {arrival.MoneyBefore})");

        switch (arrival.Kind)
        {
            case TileArrivalKind.EndTurn:
                ProcessEndTurn();
                break;

            case TileArrivalKind.NextTurn:
                StartTurn(GetNextPlayerId()); // 강제로 턴을 넘긴다. 도착 후에는 더블이어도 턴을 넘긴다
                break;

            case TileArrivalKind.Welfare:
                if (arrival.Amount > 0)
                    Debug.Log($"[HostGameService] 적립금: {player.Nickname} +{arrival.Amount}");
                ProcessEndTurn();
                break;

            case TileArrivalKind.Donation:
                Debug.Log($"[HostGameService] 세무조사: {player.Nickname} {arrival.Amount} 납부 (적립금 {state.WelfareFund})");
                ProcessEndTurn();
                break;

            case TileArrivalKind.WaitCard:
                break; // 카드 뽑기 요청을 기다린다

            case TileArrivalKind.Property:
                ProcessPropertyArrival(player, arrival.Tile.PropertyId);
                break;
        }
    }

    // GameManager.ProcessEndTurn
    private void ProcessEndTurn()
    {
        if (GameRules.EndTurn(state) == EndTurnResult.NextTurn)
            StartTurn(GetNextPlayerId());
    }


    // ───────────── 황금열쇠 효과 ─────────────

    // GameManager.ExecuteCardEffect
    // 뽑은 카드의 효과 반영은 규칙(CardRules)이 하고, 여기서는 로그를 남기고 그 결과대로 이어서 진행한다. (턴 종료 또는 도착 칸 처리까지)
    private void ExecuteCardEffect(PlayerState player, CardData card)
    {
        CardOutcome outcome = CardRules.ApplyCard(state, gameData, player, card);

        if (outcome.IsPenalty)
            Debug.Log($"[HostGameService] 카드 벌금: {player.Nickname} {card.amount} 중 {outcome.PenaltyPaid} 납부 (현금 {player.Money})");

        switch (outcome.Problem)
        {
            case CardProblem.NoBoard:
                Debug.LogError("[HostGameService] MoveBy 카드 실패: 보드 데이터 없음");
                break;

            case CardProblem.NoIslandTile:
                Debug.LogError("[HostGameService] 무인도 카드 실패: 무인도 칸 없음");
                break;

            case CardProblem.UnknownEffect:
                Debug.LogError($"[HostGameService] 처리하지 않은 CardEffectType: {card.effectType}");
                break;
        }

        switch (outcome.Kind)
        {
            case CardOutcomeKind.Move:
                MovePlayer(player, outcome.To, outcome.ReceivesSalary);
                break;

            case CardOutcomeKind.MoveDirectly:
                MovePlayerDirectly(player, outcome.To);
                break;

            default:
                ProcessEndTurn(); // 턴이 멈추지 않도록 종료
                break;
        }
    }

    // ───────────── 게임 종료와 순서 계산 ─────────────

    // GameManager.IsGameOver
    private bool IsGameOver()
    {
        return GameRules.IsGameOver(state);
    }

    // GameManager.EndGame
    // 상태를 끝으로 바꾸고 등수를 주는 일은 규칙(GameRules.EndGame)이 하고, 여기서는 방을 대기 상태로 되돌린다.
    private void EndGame(string reason)
    {
        if (!GameRules.EndGame(state, gameData)) return;

        Debug.Log($"[HostGameService] 게임 종료: {reason}");

        // 방을 대기 상태로 되돌린다. 방 모두에게 가는 ROOM_UPDATED는 로비로 돌아갈 때까지 큐에 남아 있다가 처리된다.
        LocalLobbyHost.Instance.FinishGame(roomId);
    }

    // GameManager.GetNextPlayerId (파산한 플레이어는 건너뛴다)
    private long GetNextPlayerId()
    {
        return GameRules.GetNextPlayerId(state);
    }

    // GameManager.FindIslandTileIndex
    private int FindIslandTileIndex()
    {
        return GameRules.FindIslandTileIndex(gameData);
    }
    #endregion

    #region 땅과 경제: 구매, 건설, 인수, 매각, 통행료, 파산
    // 요청 검증(차례, 단계, 말 위치), 알림, 로그, 다음 단계로 이어가는 일은 여기서 하고,
    // 거래가 가능한지 확인하고 상태를 바꾸는 규칙은 TradeRules가 맡는다.

    // ───────────── 요청 처리 ─────────────

    // GameManager.HandlePropertyPurchased
    // 빈 땅에 도착해 구매 단계(PROPERTY_PURCHASED)에 있을 때, 구매 또는 거절을 받는다.
    private void HandlePurchaseProperty(long senderId, PropertyDecisionRequest request)
    {
        if (!Validate(senderId, GameMessageType.PROPERTY_PURCHASED, out PlayerState player, request.propertyId)) return;
        if (!ValidateOnProperty(senderId, player, request.propertyId)) return;

        PropertyState property = GetProperty(request.propertyId);

        // 8. 기능별 검증. 거절은 확인할 것이 없다. (살 돈이 없는 사람은 거절만 보낼 수 있다)
        if (RejectTrade(senderId, TradeRules.CheckPurchase(gameData, player, property, request.isAccept))) return;

        state.WaitingType = null;
        Broadcast(GameMessageType.PROPERTY_PURCHASED,
            new PropertyPurchasedBroadcast { playerId = senderId, propertyId = request.propertyId, isAccept = request.isAccept });

        if (request.isAccept)
        {
            long price = TradeRules.Purchase(gameData, player, property);
            Debug.Log($"[HostGameService] 구매: {player.Nickname} {request.propertyId}번 땅 (-{price}, 현금 {player.Money})");
        }
        else
        {
            Debug.Log($"[HostGameService] 구매 안 함: {player.Nickname} {request.propertyId}번 땅");
        }

        ProcessEndTurn();
    }

    // GameManager.HandleBuilt
    // 내 땅에 도착해 건설 단계(BUILT)에 있을 때, 건설 또는 거절을 받는다.
    private void HandleBuild(long senderId, PropertyDecisionRequest request)
    {
        if (!Validate(senderId, GameMessageType.BUILT, out PlayerState player, request.propertyId)) return;
        if (!ValidateOnProperty(senderId, player, request.propertyId)) return;

        PropertyState property = GetProperty(request.propertyId);

        // 8. 기능별 검증. 거절은 확인할 것이 없다. (건설할 수 없는 땅에서도 거절은 보낼 수 있다)
        if (RejectTrade(senderId, TradeRules.CheckBuild(gameData, player, property, request.isAccept))) return;

        state.WaitingType = null;
        Broadcast(GameMessageType.BUILT,
            new BuiltBroadcast { playerId = senderId, propertyId = request.propertyId, isAccept = request.isAccept });

        if (request.isAccept)
        {
            long cost = TradeRules.Build(gameData, player, property);
            Debug.Log($"[HostGameService] 건설: {player.Nickname} {request.propertyId}번 땅 → {property.BuildingLevel} (-{cost}, 현금 {player.Money})");
        }
        else
        {
            Debug.Log($"[HostGameService] 건설 안 함: {player.Nickname} {request.propertyId}번 땅");
        }

        ProcessEndTurn();
    }

    // GameManager.HandlePropertyAcquired
    // 남의 땅에 통행료를 내고 인수 단계(PROPERTY_ACQUIRED)에 있을 때, 인수 또는 거절을 받는다.
    private void HandleAcquireProperty(long senderId, PropertyDecisionRequest request)
    {
        if (!Validate(senderId, GameMessageType.PROPERTY_ACQUIRED, out PlayerState player, request.propertyId)) return;
        if (!ValidateOnProperty(senderId, player, request.propertyId)) return;

        PropertyState property = GetProperty(request.propertyId);

        // 8. 기능별 검증. 거절은 확인할 것이 없다.
        if (RejectTrade(senderId, TradeRules.CheckAcquire(state, gameData, player, property, request.isAccept))) return;

        state.WaitingType = null;
        Broadcast(GameMessageType.PROPERTY_ACQUIRED,
            new PropertyAcquiredBroadcast { playerId = senderId, propertyId = request.propertyId, isAccept = request.isAccept });

        if (request.isAccept)
        {
            PlayerState owner = GetPlayer(property.OwnerId.Value);
            long amount = TradeRules.Acquire(gameData, player, owner, property);
            Debug.Log($"[HostGameService] 인수: {player.Nickname} {request.propertyId}번 땅 ← {owner.Nickname} (-{amount}, 현금 {player.Money})");
        }
        else
        {
            Debug.Log($"[HostGameService] 인수 안 함: {player.Nickname} {request.propertyId}번 땅");
        }

        ProcessEndTurn();
    }

    // GameManager.HandlePropertiesSold
    // 통행료를 낼 현금이 모자라 매각 단계(PROPERTIES_SOLD)에 있을 때, 팔 땅 목록을 받는다.
    // 판 돈으로 통행료를 낸 뒤에는 인수를 선택할 수 없고 턴이 끝난다.
    private void HandleSellProperties(long senderId, SellPropertiesRequest request)
    {
        List<int> propertyIds = request.propertyIds ?? new List<int>();

        if (!Validate(senderId, GameMessageType.PROPERTIES_SOLD, out PlayerState player, propertyIds: propertyIds)) return;

        // 7, 8. 통행료를 낼 땅 위에 있어야 하고, 팔 땅 목록이 올바르고, 판 돈을 합쳐 통행료를 낼 수 있어야 한다
        SellPlan plan = TradeRules.CheckSell(state, gameData, player, propertyIds);
        if (RejectTrade(senderId, plan.Error)) return;

        state.WaitingType = null;
        Broadcast(GameMessageType.PROPERTIES_SOLD, new PropertiesSoldBroadcast { playerId = senderId, propertyIds = propertyIds });

        TradeRules.Sell(state, player, plan, propertyIds);
        Debug.Log($"[HostGameService] 매각: {player.Nickname} 땅 {propertyIds.Count}개 (+{plan.TotalAmount}), 통행료 {plan.Toll} 납부 (현금 {player.Money})");

        ProcessEndTurn();
    }

    // 규칙이 알려 준 거래 거절 이유를 요청한 사람에게 보낸다. 거절 이유가 있으면 true.
    private bool RejectTrade(long senderId, TradeError error)
    {
        switch (error)
        {
            case TradeError.None: return false;
            case TradeError.AlreadyOwned: SendError(senderId, "ALREADY_OWNED", "이미 주인이 있는 땅이에요"); return true;
            case TradeError.NotEnoughMoney: SendError(senderId, "NOT_ENOUGH_MONEY", "현금이 부족해요"); return true;
            case TradeError.NotOwner: SendError(senderId, "NOT_OWNER", "내 땅이 아니에요"); return true;
            case TradeError.CannotBuild: SendError(senderId, "CANNOT_BUILD", "건설할 수 없는 땅이에요"); return true;
            case TradeError.MaxLevel: SendError(senderId, "MAX_LEVEL", "이미 최고 단계예요"); return true;
            case TradeError.NotOthersProperty: SendError(senderId, "NOT_OTHERS_PROPERTY", "다른 사람의 땅이 아니에요"); return true;
            case TradeError.NotOnProperty: SendError(senderId, "NOT_ON_PROPERTY", "통행료를 낼 땅 위에 있지 않아요"); return true;
            case TradeError.DuplicateProperty: SendError(senderId, "DUPLICATE_PROPERTY", "같은 땅이 두 번 들어 있어요"); return true;
            case TradeError.SellNotOwner: SendError(senderId, "NOT_OWNER", "내 땅이 아닌 땅이 들어 있어요"); return true;
            case TradeError.SellNotEnough: SendError(senderId, "NOT_ENOUGH_MONEY", "판 돈을 합쳐도 통행료가 모자라요"); return true;
            default: SendError(senderId, "BAD_REQUEST", "요청을 처리하지 못했어요"); return true;
        }
    }

    // ───────────── 도착, 통행료, 파산 ─────────────

    // GameManager.ProcessArrival의 PROPERTY 부분
    // 땅에 도착했을 때 어떤 입력을 기다릴지는 규칙(TradeRules.ArriveAtProperty)이 정하고, 여기서는 그 결과대로 이어서 진행한다.
    private void ProcessPropertyArrival(PlayerState player, int propertyId)
    {
        PropertyArrival arrival = TradeRules.ArriveAtProperty(state, gameData, player, propertyId);

        switch (arrival.Kind)
        {
            case PropertyArrivalKind.MissingData:
                Debug.LogError($"[HostGameService] 땅 데이터 없음: propertyId {propertyId}");
                ProcessEndTurn();
                return;

            case PropertyArrivalKind.Purchase:
            case PropertyArrivalKind.Build:
                return; // 구매 또는 건설 요청을 기다린다
        }

        // 남의 땅
        Debug.Log($"[HostGameService] 남의 땅: {propertyId}번 (주인 {arrival.Owner.Nickname}, 통행료 {arrival.Toll})");

        switch (arrival.Kind)
        {
            case PropertyArrivalKind.TollPaidAcquire:
                Debug.Log($"[HostGameService] 통행료: {player.Nickname} → {arrival.Owner.Nickname} {arrival.Toll}");
                break; // 인수 요청을 기다린다

            case PropertyArrivalKind.TollPaidEnd:
                Debug.Log($"[HostGameService] 통행료: {player.Nickname} → {arrival.Owner.Nickname} {arrival.Toll}");
                ProcessEndTurn();
                break;

            case PropertyArrivalKind.Sell:
                // 현금이 모자라지만 땅을 팔면 낼 수 있다. 팔 땅을 고르는 단계로 들어간다.
                Debug.Log($"[HostGameService] 현금 부족: {player.Nickname} 땅을 팔아서 통행료를 내야 합니다.");
                break;

            case PropertyArrivalKind.Bankrupt:
                // 전부 팔아도 모자라면 파산한다. 더블이어도 다음 플레이어로 넘어간다.
                Debug.Log($"[HostGameService] 현금과 땅을 모두 팔아도 통행료를 낼 수 없습니다: {player.Nickname} 파산");
                Bankrupt(player, arrival.Owner);
                StartTurn(GetNextPlayerId());
                break;
        }
    }

    // GameManager.HandleBankruptcy, ProcessBankruptcy
    // 재산을 넘기고 파산 처리하는 일은 규칙(TradeRules.Bankrupt)이 하고, 여기서는 로그와 게임 종료를 처리한다.
    private void Bankrupt(PlayerState payer, PlayerState receiver)
    {
        BankruptResult result = TradeRules.Bankrupt(state, gameData, payer, receiver);
        if (result == BankruptResult.AlreadyBankrupt) return;

        Debug.Log($"[HostGameService] 파산: {payer.Nickname} (최종 {payer.FinalRank}위)");

        if (result == BankruptResult.LastPlayerLeft)
            EndGame("남은 플레이어가 1명입니다.");
    }
    #endregion

    #region 자동 진행: 봇 차례, 제한 시간, AFK
    // 시간은 HostAutoPlayRunner가 매 프레임 Tick으로 흘려 준다. (기획서 7장)
    //
    // 대신 진행(제한 시간 초과, AFK)과 봇은 같은 전략이 판단하고, 사람과 같은 처리 함수(Handle...)를 호출한다. (원본 서버와 같다)
    // 거절도 같은 경로로 호출해야 턴이 멈추지 않는다.

    // 기다리는 시간(봇, AFK, 사람의 제한 시간)은 시간 규칙(TimeRules)이 정한다.
    // 화면의 선택창은 이보다 먼저 닫혀야 한다. 호스트가 먼저 움직이면 화면과 겹쳐 입력이 거절된다.

    private readonly HashSet<long> afkPlayers = new HashSet<long>(); // 제한 시간을 넘긴 사람. 요청을 보내거나 재접속하면 풀린다
    private IBotStrategy botStrategy;

    // 지금 입력 단계를 얼마나 기다렸는지. 아래 세 값 중 하나라도 바뀌면 새 입력 단계로 보고 0부터 다시 센다.
    private float waited;
    private int waitTurn = -1;
    private int waitAction = -1;
    private GameMessageType? waitPhase;

    // 요청이 처리되어 알림이 나갈 때마다 올라간다. (더블처럼 같은 사람이 같은 단계를 다시 맡아도 새로 세기 위한 표시)
    private int actionSerial;

    /// <summary>이 호스트가 아직 진행 중인가. 끝났거나 다른 게임으로 바뀌었으면 false</summary>
    public bool IsRunning => current == this && !state.IsGameOver;

    // 봇 전략은 처음 필요할 때 만든다. (보드와 땅 가격표가 있는 게임 씬에서 만들어야 한다)
    private IBotStrategy Strategy
    {
        get
        {
            if (botStrategy == null)
            {
                botStrategy = new BasicBotStrategy(
                    BoardManager.Instance.BoardData.Tiles,
                    PropertyManager.Instance.GetToll,
                    PropertyManager.Instance.GetData,
                    BotSettings.Balanced(),
                    state.Config.maxRound,
                    message => Debug.Log($"[Bot] {message}"));
            }
            return botStrategy;
        }
    }

    // ───────────── 시간 ─────────────

    public void Tick(float deltaTime)
    {
        if (!started || state.IsGameOver || state.WaitingType == null) return;

        PlayerState actor = GetPlayer(state.CurrentPlayerId);
        if (actor == null || actor.IsBankrupt) return;

        // 새 입력 단계가 시작되면 기다린 시간을 0부터 센다
        if (waitTurn != state.TurnNumber || waitAction != actionSerial || waitPhase != state.WaitingType)
        {
            waitTurn = state.TurnNumber;
            waitAction = actionSerial;
            waitPhase = state.WaitingType;
            waited = 0f;
        }

        waited += deltaTime;
        if (waited < WaitLimit(actor)) return;

        AutoAct(actor);
    }

    // 이 차례를 몇 초 기다렸다가 대신 진행할지. 사람의 제한 시간이 0 이하면 제한이 없다.
    private float WaitLimit(PlayerState actor)
    {
        return TimeRules.HostWaitSeconds(state.Config, actor.IsBot, afkPlayers.Contains(actor.PlayerId));
    }

    // ───────────── 대신 진행 ─────────────

    // 봇이거나 제한 시간을 넘긴 차례를 대신 진행한다.
    private void AutoAct(PlayerState actor)
    {
        long id = actor.PlayerId;
        bool isBot = actor.IsBot;

        if (!isBot && afkPlayers.Add(id))
            Debug.Log($"[HostGameService] 제한 시간 초과: {actor.Nickname} AFK로 표시하고 대신 진행합니다.");

        // 대신 진행이 검증에 걸려 알림 없이 끝나더라도 매 프레임 되풀이하지 않게, 다시 처음부터 기다린다
        waited = 0f;

        TileData tile = BoardManager.Instance.GetTileData(actor.Position);
        int propertyId = tile != null ? tile.PropertyId : 0;

        switch (state.WaitingType)
        {
            case GameMessageType.DICE_ROLLED:
                HandleRollDice(id);
                break;

            case GameMessageType.DESTINATION_CHOSEN:
                HandleChooseDestination(id, new ChooseDestinationRequest { type = "CHOOSE_DESTINATION", destinationPosition = ChooseDestination(actor) });
                break;

            // 구매, 건설, 인수는 봇과 제한 시간을 넘긴 사람 모두 같은 전략이 판단한다
            case GameMessageType.PROPERTY_PURCHASED:
                HandlePurchaseProperty(id, Decision("PURCHASE_PROPERTY", propertyId, WantsToPurchase(actor, propertyId)));
                break;

            case GameMessageType.BUILT:
                HandleBuild(id, Decision("BUILD", propertyId, WantsToBuild(actor, propertyId)));
                break;

            case GameMessageType.PROPERTY_ACQUIRED:
                HandleAcquireProperty(id, Decision("ACQUIRE_PROPERTY", propertyId, WantsToAcquire(actor, propertyId)));
                break;

            case GameMessageType.PROPERTIES_SOLD:
                HandleSellProperties(id, new SellPropertiesRequest { type = "SELL_PROPERTIES", propertyIds = ChooseSellProperties(actor) });
                break;

            case GameMessageType.CARD_DRAWN:
                HandleDrawCard(id);
                break;
        }
    }

    private static PropertyDecisionRequest Decision(string type, int propertyId, bool isAccept)
    {
        return new PropertyDecisionRequest { type = type, propertyId = propertyId, isAccept = isAccept };
    }

    // ───────────── 봇의 판단 ─────────────
    // 현금이 모자라 요청이 거절되어 턴이 멈추는 일이 없도록, 전략에 묻기 전에 현금과 조건을 먼저 확인한다.

    private bool WantsToPurchase(PlayerState actor, int propertyId)
    {
        PropertyState property = GetProperty(propertyId);
        if (property == null || property.OwnerId != null) return false;

        long price = PropertyManager.Instance.GetLandPrice(propertyId);
        return actor.Money >= price && Strategy.ShouldPurchase(state, actor.PlayerId, propertyId, price);
    }

    private bool WantsToBuild(PlayerState actor, int propertyId)
    {
        PropertyState property = GetProperty(propertyId);
        PropertyData data = PropertyManager.Instance.GetData(propertyId);
        if (property == null || data == null || !data.CanBuild) return false;
        if (property.OwnerId != actor.PlayerId || property.BuildingLevel >= BuildingLevel.Hotel) return false;

        long cost = PropertyManager.Instance.GetBuildCost(propertyId, property.BuildingLevel + 1);
        return actor.Money >= cost && Strategy.ShouldBuild(state, actor.PlayerId, propertyId, cost);
    }

    private bool WantsToAcquire(PlayerState actor, int propertyId)
    {
        PropertyState property = GetProperty(propertyId);
        if (property == null || !property.OwnerId.HasValue || property.OwnerId.Value == actor.PlayerId) return false;

        long price = PropertyManager.Instance.GetAcquireValue(property);
        return actor.Money >= price && Strategy.ShouldAcquire(state, actor.PlayerId, propertyId, price);
    }

    // 통행료를 낼 땅 목록. 봇과 제한 시간을 넘긴 사람 모두 전략이 고른다.
    private List<int> ChooseSellProperties(PlayerState actor)
    {
        TileData tile = BoardManager.Instance.GetTileData(actor.Position);
        PropertyState landed = tile != null && tile.Type == TileType.PROPERTY ? GetProperty(tile.PropertyId) : null;
        if (landed == null) return new List<int>();

        long toll = PropertyManager.Instance.GetToll(landed);
        return Strategy.ChooseSellProperties(state, actor.PlayerId, toll, PropertyManager.Instance.GetSellValue);
    }

    // 세계여행 목적지. 전략이 고른 곳이 갈 수 없는 칸이면 갈 수 있는 첫 칸으로 간다.
    private int ChooseDestination(PlayerState actor)
    {
        int destination = Strategy.ChooseTravelDestination(state, actor.PlayerId);
        if (IsValidDestination(destination)) return destination;

        int tileCount = BoardManager.Instance.TileCount;
        for (int i = 0; i < tileCount; i++)
        {
            if (i != actor.Position && IsValidDestination(i)) return i;
        }
        return destination;
    }

    // 보드 안의 칸이고 세계여행 칸 자체가 아니면 목적지로 고를 수 있다
    private bool IsValidDestination(int position)
    {
        return ArrivalRules.CheckDestination(gameData, position) == DestinationError.None;
    }
    #endregion

    #region 개발용 조회
    // 읽기만 하고 상태를 바꾸지 않는다.

    /// <summary>이 PC에서 진행 중인 게임 호스트. 없으면 null. (다른 PC가 호스트인 참가자라면 null)</summary>
    public static HostGameService Current => current;

    /// <summary>호스트가 가진 게임 상태</summary>
    public GameState DebugState => state;

    /// <summary>호스트가 지금까지 참가자들에게 보낸 진행 알림(주사위, 구매, 건설 등)의 수</summary>
    public int BroadcastCountForDebug => actionSerial;
    #endregion
}