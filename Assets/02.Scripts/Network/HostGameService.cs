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
/// 그래서 이 파일의 진행 순서는 GameManager의 같은 이름 함수와 맞춰 두었고, 각 함수 위에 짝이 되는 GameManager 함수를 적어 두었다.
/// 규칙을 바꿀 때는 양쪽을 함께 고친다.
/// </summary>
public class HostGameService : IHostRequestReceiver
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

        // 턴 순서는 방의 자리 순서다. (방장이 첫 차례, 봇은 맨 뒤) 무작위 선턴은 자동 진행 작업에서 넣는다.
        List<PlayerState> players = room.Players
            .Select(p => new PlayerState(p.playerId)
            {
                Nickname = p.nickname,
                Money = config.startMoney,
                IsBot = p.bot,
                PortraitId = p.portraitId,
            })
            .ToList();

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

        switch (type)
        {
            case "GAME_STATE_REQUEST": HandleGameStateRequest(senderId); break;
            case "ROLL_DICE": HandleRollDice(senderId); break;
            case "PURCHASE_PROPERTY": HandlePurchaseProperty(senderId, JsonConvert.DeserializeObject<PropertyDecisionRequest>(json)); break;
            case "BUILD": HandleBuild(senderId, JsonConvert.DeserializeObject<PropertyDecisionRequest>(json)); break;

            // 아래 요청은 이어지는 작업에서 처리한다
            case "CHOOSE_DESTINATION":
            case "DRAW_CARD":
            case "ACQUIRE_PROPERTY":
            case "SELL_PROPERTIES":
                Debug.LogWarning($"[HostGameService] 아직 처리하지 않는 요청입니다: {type}");
                SendError(senderId, "NOT_IMPLEMENTED", "아직 지원하지 않는 요청이에요");
                break;

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

    // GameManager.HandlePropertyPurchased
    // 빈 땅에 도착해 구매 단계(PROPERTY_PURCHASED)에 있을 때, 구매 또는 거절을 받는다.
    private void HandlePurchaseProperty(long senderId, PropertyDecisionRequest request)
    {
        if (!Validate(senderId, GameMessageType.PROPERTY_PURCHASED, out PlayerState player, request.propertyId)) return;
        if (!ValidateOnProperty(senderId, player, request.propertyId)) return;

        PropertyState property = GetProperty(request.propertyId);
        long price = PropertyManager.Instance.GetLandPrice(request.propertyId);

        // 8. 기능별 검증. 거절은 확인할 것이 없다. (살 돈이 없는 사람은 거절만 보낼 수 있다)
        if (request.isAccept)
        {
            if (property.OwnerId != null) { SendError(senderId, "ALREADY_OWNED", "이미 주인이 있는 땅이에요"); return; }
            if (player.Money < price) { SendError(senderId, "NOT_ENOUGH_MONEY", "현금이 부족해요"); return; }
        }

        state.WaitingType = null;
        Broadcast(GameMessageType.PROPERTY_PURCHASED,
            new PropertyPurchasedBroadcast { playerId = senderId, propertyId = request.propertyId, isAccept = request.isAccept });

        if (request.isAccept)
        {
            player.Money -= price;
            property.OwnerId = senderId;
            property.BuildingLevel = BuildingLevel.Land; // 새로 산 땅은 건물 없음
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
        long cost = 0;

        // 8. 기능별 검증. 거절은 확인할 것이 없다. (건설할 수 없는 땅에서도 거절은 보낼 수 있다)
        if (request.isAccept)
        {
            PropertyData data = PropertyManager.Instance.GetData(request.propertyId);

            if (property.OwnerId != senderId) { SendError(senderId, "NOT_OWNER", "내 땅이 아니에요"); return; }
            if (data == null || !data.CanBuild) { SendError(senderId, "CANNOT_BUILD", "건설할 수 없는 땅이에요"); return; }
            if (property.BuildingLevel >= BuildingLevel.Hotel) { SendError(senderId, "MAX_LEVEL", "이미 최고 단계예요"); return; }

            cost = PropertyManager.Instance.GetBuildCost(request.propertyId, property.BuildingLevel + 1);
            if (player.Money < cost) { SendError(senderId, "NOT_ENOUGH_MONEY", "현금이 부족해요"); return; }
        }

        state.WaitingType = null;
        Broadcast(GameMessageType.BUILT,
            new BuiltBroadcast { playerId = senderId, propertyId = request.propertyId, isAccept = request.isAccept });

        if (request.isAccept)
        {
            player.Money -= cost;
            property.BuildingLevel = property.BuildingLevel + 1;
            Debug.Log($"[HostGameService] 건설: {player.Nickname} {request.propertyId}번 땅 → {property.BuildingLevel} (-{cost}, 현금 {player.Money})");
        }
        else
        {
            Debug.Log($"[HostGameService] 건설 안 함: {player.Nickname} {request.propertyId}번 땅");
        }

        ProcessEndTurn();
    }

    // ───────────── 검증 (기획서 5장 "검증 순서") ─────────────

    // 1. 진행 중인 게임이 있는가  2. (땅 번호가 있다면) 존재하는 땅인가  3. 요청자의 차례인가  4. 파산하지 않았는가  5. 요청이 현재 단계에 맞는가
    // (7번은 ValidateOnProperty, 8번은 각 요청 처리에서 하고, 6번 제한 시간은 자동 진행 작업에서 넣는다)
    private bool Validate(long senderId, GameMessageType expectedPhase, out PlayerState player, int? propertyId = null)
    {
        player = GetPlayer(senderId);

        if (state.IsGameOver) { SendError(senderId, "GAME_OVER", "이미 끝난 게임이에요"); return false; }
        if (propertyId.HasValue && GetProperty(propertyId.Value) == null) { SendError(senderId, "NO_SUCH_PROPERTY", "존재하지 않는 땅이에요"); return false; }
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

    // ───────────── 게임 진행 (GameManager와 같은 순서) ─────────────

    // GameManager.HandleTurnChanged
    // 새 차례를 시작한다. 게임이 끝났는지 먼저 확인하고, 끝나지 않았으면 어떤 입력을 기다릴지 정한다.
    private void StartTurn(long playerId)
    {
        if (state.IsGameOver) return;

        if (IsGameOver())
        {
            EndGame("남은 플레이어가 1명입니다.");
            return;
        }

        int nextRound = GetNextRound(playerId);
        if (nextRound > state.Config.maxRound)
        {
            EndGame($"최대 라운드({state.Config.maxRound}라운드)에 도달했습니다.");
            return;
        }

        state.CurrentPlayerId = playerId;
        state.TurnNumber += 1;
        state.RoundNumber = nextRound;
        state.IsDouble = false;
        state.ConsecutiveDoubleCount = 0;

        PlayerState player = GetPlayer(playerId);
        Debug.Log($"[HostGameService] ===== {state.RoundNumber}/{state.Config.maxRound}라운드 ({state.TurnNumber}번째 턴): {player.Nickname} 차례 =====");

        // 세계여행 칸에서 시작하는 차례는 주사위 대신 목적지 선택으로 시작한다
        TileData tile = BoardManager.Instance.GetTileData(player.Position);
        state.WaitingType = tile != null && tile.Type == TileType.WORLD_TRAVEL
            ? GameMessageType.DESTINATION_CHOSEN
            : GameMessageType.DICE_ROLLED;
    }

    // GameManager.ContinueAfterDiceResult
    private void ApplyDiceResult(PlayerState player, int dice1, int dice2)
    {
        if (dice1 == dice2)
        {
            state.IsDouble = true;
            state.ConsecutiveDoubleCount++;
        }
        else
        {
            state.IsDouble = false;
            state.ConsecutiveDoubleCount = 0;
        }

        TileData currentTile = BoardManager.Instance.GetTileData(player.Position);
        bool onIslandTile = currentTile != null && currentTile.Type == TileType.ISLAND;
        bool islandTurnsRemaining = player.IslandTurnsRemaining > 0;

        // 무인도에서 영업정지 중인데 더블이 아니면 이동 없이 턴이 넘어간다
        if (onIslandTile && islandTurnsRemaining && !state.IsDouble)
        {
            player.IslandTurnsRemaining -= 1;
            Debug.Log($"[HostGameService] 무인도 탈출 실패: {player.Nickname} (남은 {player.IslandTurnsRemaining}턴)");
            StartTurn(GetNextPlayerId());
            return;
        }

        // 더블이면 영업정지가 풀리고 그 더블로 이동한다. 추가 턴은 없다.
        // 남은 턴이 0이 된 뒤의 더블은 여기에 걸리지 않고 일반 더블로 처리된다.
        if (onIslandTile && islandTurnsRemaining && state.IsDouble)
        {
            player.IslandTurnsRemaining = 0;
            state.IsDouble = false;
            Debug.Log($"[HostGameService] 무인도 탈출: {player.Nickname}");
        }

        // 3번째 연속 더블이면 이동 없이 무인도로 간다. (월급 없음)
        if (state.ConsecutiveDoubleCount >= 3)
        {
            int islandPosition = FindIslandTileIndex();
            if (islandPosition >= 0)
            {
                Debug.Log($"[HostGameService] 3연속 더블: {player.Nickname} 무인도로 이동");
                state.IsDouble = false;
                state.ConsecutiveDoubleCount = 0;
                MovePlayerDirectly(player, islandPosition);
                return;
            }
        }

        int tileCount = BoardManager.Instance.TileCount;
        int from = player.Position;
        int to = (from + dice1 + dice2) % tileCount;
        bool shouldReceiveSalary = to < from; // 출발 지점을 지나침
        MovePlayer(player, to, shouldReceiveSalary);
    }

    // GameManager.HandlePlayerMoved
    private void MovePlayer(PlayerState player, int to, bool shouldReceiveSalary)
    {
        player.Position = to;

        if (shouldReceiveSalary)
        {
            player.Money += state.Config.salaryAmount;
            Debug.Log($"[HostGameService] 월급: {player.Nickname} +{state.Config.salaryAmount} (현금 {player.Money})");
        }

        ProcessArrival(player, to);
    }

    // GameManager.MovePlayerDirectly (월급 없는 순간이동)
    private void MovePlayerDirectly(PlayerState player, int to)
    {
        player.Position = to;
        ProcessArrival(player, to);
    }

    // GameManager.ProcessArrival
    // 도착한 칸에 따라 처리하고, 다음에 기다릴 입력(WaitingType)을 정한다.
    private void ProcessArrival(PlayerState player, int position)
    {
        TileData tile = BoardManager.Instance.GetTileData(position);
        if (tile == null)
        {
            Debug.LogError($"[HostGameService] 도착한 칸 {position}의 데이터가 없습니다.");
            ProcessEndTurn();
            return;
        }

        Debug.Log($"[HostGameService] 도착: {player.Nickname} → {position}번 칸 ({tile.Type}, 현금 {player.Money})");

        switch (tile.Type)
        {
            case TileType.START:
                ProcessEndTurn();
                break;

            case TileType.ISLAND:
                player.IslandTurnsRemaining = IslandTurns;
                StartTurn(GetNextPlayerId()); // 강제로 턴을 넘긴다
                break;

            case TileType.CHARITY:
                ReceiveWelfareFund(player);
                ProcessEndTurn();
                break;

            case TileType.DONATION:
                PayDonation(player);
                ProcessEndTurn();
                break;

            case TileType.WORLD_TRAVEL:
                StartTurn(GetNextPlayerId()); // 도착 후에는 더블이어도 턴을 넘긴다
                break;

            case TileType.GOLDEN_KEY:
                state.WaitingType = GameMessageType.CARD_DRAWN; // 카드 뽑기 요청은 이어지는 작업에서 처리한다
                break;

            case TileType.PROPERTY:
                ProcessPropertyArrival(player, tile.PropertyId);
                break;
        }
    }

    // GameManager.ProcessArrival의 PROPERTY 부분
    // 땅에 도착했을 때 어떤 입력을 기다릴지 정한다.
    private void ProcessPropertyArrival(PlayerState player, int propertyId)
    {
        PropertyState property = GetProperty(propertyId);
        PropertyData data = PropertyManager.Instance.GetData(propertyId);

        if (property == null || data == null)
        {
            Debug.LogError($"[HostGameService] 땅 데이터 없음: propertyId {propertyId}");
            ProcessEndTurn();
            return;
        }

        if (property.OwnerId == null)
        {
            // 살 돈이 없어도 구매 단계로 들어간다. (선택창에서 거절만 보낼 수 있다)
            state.WaitingType = GameMessageType.PROPERTY_PURCHASED;
        }
        else if (property.OwnerId.Value == player.PlayerId)
        {
            // 건설할 수 없는 땅이어도 건설 단계로 들어간다. (선택창에서 거절만 보낼 수 있다)
            state.WaitingType = GameMessageType.BUILT;
        }
        else
        {
            // 남의 땅: 통행료, 인수, 매각, 파산은 이어지는 작업에서 처리한다
            Debug.LogWarning("[HostGameService] 남의 땅 도착 처리(통행료)는 아직 구현되지 않았습니다.");
        }
    }

    // GameManager.ProcessEndTurn
    private void ProcessEndTurn()
    {
        if (IsGameOver()) return;

        if (state.IsDouble)
            state.WaitingType = GameMessageType.DICE_ROLLED; // 더블이면 같은 사람이 한 번 더 굴린다
        else
            StartTurn(GetNextPlayerId());
    }

    // GameManager.HandleDonationPaid (세무조사: 현금 한도 안에서만 걷고, 걷은 금액은 적립금에 쌓는다)
    private void PayDonation(PlayerState player)
    {
        long paid = Math.Min(state.Config.taxAmount, Math.Max(0, player.Money));
        player.Money -= paid;
        state.WelfareFund += paid;
        Debug.Log($"[HostGameService] 세무조사: {player.Nickname} {paid} 납부 (적립금 {state.WelfareFund})");
    }

    // GameManager.HandleWelfareFundReceived (적립금 칸: 쌓인 금액을 모두 받는다)
    private void ReceiveWelfareFund(PlayerState player)
    {
        long amount = state.WelfareFund;
        if (amount <= 0) return;

        player.Money += amount;
        state.WelfareFund -= amount;
        Debug.Log($"[HostGameService] 적립금: {player.Nickname} +{amount}");
    }

    // ───────────── 게임 종료 ─────────────

    // GameManager.IsGameOver
    private bool IsGameOver()
    {
        return state.PlayerStates.Count(p => !p.IsBankrupt) <= 1;
    }

    // GameManager.EndGame
    private void EndGame(string reason)
    {
        if (state.IsGameOver) return;

        state.IsGameOver = true;
        state.WaitingType = null;
        Debug.Log($"[HostGameService] 게임 종료: {reason}");

        AssignSurvivorRanks();

        // 방을 대기 상태로 되돌린다. 방 모두에게 가는 ROOM_UPDATED는 로비로 돌아갈 때까지 큐에 남아 있다가 처리된다.
        LocalLobbyHost.Instance.FinishGame(roomId);
    }

    // GameManager.AssignSurvivorRanks (살아남은 플레이어에게 총자산 → 현금 → 원래 순서로 1위부터 부여)
    private void AssignSurvivorRanks()
    {
        List<PlayerState> ranked = PlayerRanking.Rank(state.PlayerStates, state.PropertyStates);
        int rank = 1;
        foreach (PlayerState player in ranked)
        {
            if (player.IsBankrupt) continue;
            player.FinalRank = rank++;
        }
    }

    // ───────────── 순서 계산 ─────────────

    // GameManager.GetNextRound
    // nextPlayerId의 차례가 몇 번째 라운드인지. 턴 순서가 처음으로 돌아오면 1 증가한다.
    private int GetNextRound(long nextPlayerId)
    {
        int prevIndex = state.PlayerOrder.IndexOf(state.CurrentPlayerId);
        int nextIndex = state.PlayerOrder.IndexOf(nextPlayerId);
        return nextIndex < prevIndex ? state.RoundNumber + 1 : state.RoundNumber;
    }

    // GameManager.GetNextPlayerId (파산한 플레이어는 건너뛴다)
    private long GetNextPlayerId()
    {
        List<long> order = state.PlayerOrder;
        int currentIndex = order.IndexOf(state.CurrentPlayerId);
        for (int i = 1; i <= order.Count; i++)
        {
            long candidateId = order[(currentIndex + i) % order.Count];
            PlayerState candidate = GetPlayer(candidateId);
            if (candidate != null && !candidate.IsBankrupt) return candidateId;
        }
        return state.CurrentPlayerId; // 모두 파산한 경우 (게임 종료 상황)
    }

    // GameManager.FindIslandTileIndex
    private int FindIslandTileIndex()
    {
        int tileCount = BoardManager.Instance.TileCount;
        for (int i = 0; i < tileCount; i++)
        {
            TileData tile = BoardManager.Instance.GetTileData(i);
            if (tile != null && tile.Type == TileType.ISLAND) return i;
        }
        return -1;
    }

    private PlayerState GetPlayer(long playerId) => state.PlayerStates.Find(p => p.PlayerId == playerId);

    private PropertyState GetProperty(int propertyId) => state.PropertyStates.Find(p => p.PropertyId == propertyId);

    // ───────────── 알림 보내기 ─────────────

    // 게임에 참가한 사람 모두에게 (봇 제외, 파산한 사람도 지켜볼 수 있게 포함)
    private void Broadcast(GameMessageType type, object message)
    {
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