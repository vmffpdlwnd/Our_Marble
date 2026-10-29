using UnityEngine;

/// <summary>
/// GameManager의 황금열쇠. (나뉜 파일 설명은 GameManager.cs 참고)
/// </summary>
public partial class GameManager
{
    #region Card Effect
    // ────────────────────────── 황금 열쇠 CardEffect 실행 ──────────────────────────
    // HandleCardDrawn → ExecuteCardEffect에서 카드의 EffectType을 확인한 뒤 호출합니다.
    // 대상은 현재 턴 플레이어(gameState.CurrentPlayerId)이며, 게임 상태 변경 후 다음 흐름(턴 종료, 도착 칸 처리)까지 진행합니다.

    // 카드의 EffectType을 확인하고 해당 CardEffect 실행 메서드를 호출합니다.
    private void ExecuteCardEffect(long playerId, CardData card)
    {
        switch (card.effectType)
        {
            case CardEffectType.Bonus:
                ExecuteBonusEffect(playerId, card.amount);
                break;

            case CardEffectType.Penalty:
                ExecutePenaltyEffect(playerId, card.amount, card.penaltyToFestivalPool);
                break;

            case CardEffectType.MoveTo:
                ExecuteMoveToEffect(playerId, card.targetTileId);
                break;

            case CardEffectType.MoveBy:
                ExecuteMoveByEffect(playerId, card.steps);
                break;

            case CardEffectType.GoToInspection:
                ExecuteGoToInspectionEffect(playerId);
                break;

            default:
                Debug.LogError($"[GameManager] 처리하지 않은 CardEffectType: {card.effectType}");
                ProcessEndTurn(); // 턴이 멈추지 않도록 종료
                break;
        }
    }

    // CardEffect: Bonus - 은행에서 돈을 받는다
    private void ExecuteBonusEffect(long playerId, int amount)
    {
        PlayerState player = GetPlayerState(playerId);
        if (player == null)
        {
            ProcessEndTurn();
            return;
        }

        long before = player.Money;
        player.Money += amount;
        EconomyManager.NotifyMoneyChanged(playerId, before, player.Money);

        // 재화 획득 연출을 재생합니다.

        ProcessEndTurn();
    }

    // CardEffect: Penalty - 은행 또는 기부금(WelfareFund)에 돈을 낸다
    private void ExecutePenaltyEffect(long playerId, int amount, bool toWelfareFund)
    {
        PlayerState player = GetPlayerState(playerId);
        if (player == null)
        {
            ProcessEndTurn();
            return;
        }

        // 현금 한도 안에서만 냅니다. (매각/파산 없음)
        long paid = DeductMoneyWithinBalance(player, amount);
        if (toWelfareFund)
            gameState.WelfareFund += paid;
        Log($"[카드:벌금] {P(playerId)}: 벌금 {Won(amount)} 중 {Won(paid)} 납부 (현금 {Won(player.Money)})");

        // 재화 손실 연출을 재생합니다.

        ProcessEndTurn();
    }

    // CardEffect: MoveTo - 지정한 칸으로 앞으로 이동한다 (출발지를 지나면 월급)
    private void ExecuteMoveToEffect(long playerId, int targetTileId)
    {
        PlayerState player = GetPlayerState(playerId);
        if (player == null)
        {
            ProcessEndTurn();
            return;
        }

        int fromPosition = player.Position;
        int toPosition = targetTileId;
        bool shouldReceiveSalary = toPosition < fromPosition; // RollDice와 같은 규칙: toPosition < fromPosition 이면 출발지 통과

        Log($"[카드:이동] {P(playerId)}: {TileName(toPosition)}(으)로 이동");

        // 이동이 끝나면 MovePlayerToDestination이 알아서 ProcessArrival을 호출합니다.
        MovePlayerToDestination(playerId, fromPosition, toPosition, shouldReceiveSalary);
    }

    // CardEffect: MoveBy - N칸 이동한다 (음수면 뒤로, 뒤로 갈 때는 월급 없음)
    private void ExecuteMoveByEffect(long playerId, int steps)
    {
        PlayerState player = GetPlayerState(playerId);
        int boardSize = BoardManager.Instance.BoardData.Tiles.Count;
        if (player == null || boardSize <= 0)
        {
            Debug.LogError($"[GameManager] MoveBy 카드 실패: 플레이어 또는 보드 데이터 없음 (보드 칸 수 {boardSize})");
            ProcessEndTurn();
            return;
        }

        int fromPosition = player.Position;
        int toPosition = ((fromPosition + steps) % boardSize + boardSize) % boardSize;
        bool shouldReceiveSalary = steps > 0 && fromPosition + steps >= boardSize;

        Log($"[카드:이동] {P(playerId)}: {steps}칸 이동 ({TileName(fromPosition)} → {TileName(toPosition)})");

        // 이동이 끝나면 두 함수 모두 알아서 ProcessArrival을 호출합니다.
        if (steps > 0)
            HandlePlayerMoved(playerId, fromPosition, toPosition, shouldReceiveSalary);
        else
            MovePlayerDirectly(playerId, toPosition);   // 뒤로 이동
    }

    // CardEffect: GoToInspection - 무인도로 바로 이동한다 (월급 없음, 더블이어도 추가 턴 없음)
    private void ExecuteGoToInspectionEffect(long playerId)
    {
        PlayerState player = GetPlayerState(playerId);
        int islandPosition = FindIslandTileIndex();
        if (player == null || islandPosition < 0)
        {
            Debug.LogError($"[GameManager] 무인도 카드 실패: 플레이어 또는 무인도 칸 없음");
            ProcessEndTurn();
            return;
        }

        Log($"[카드:무인도] {P(playerId)}: 무인도로 이동합니다.");

        // 순간이동 (월급 없음) → 도착 처리에서 영업정지 설정 + 추가 턴 없이 턴 넘김
        MovePlayerDirectly(playerId, islandPosition);
    }

    // 보드에서 무인도(ISLAND) 칸 번호를 찾는다. 없으면 -1
    private int FindIslandTileIndex()
    {
        return GameRules.FindIslandTileIndex(gameData);
    }
    #endregion

    public void DrawCard()
    {
        // 자신의 차례인 경우에만 요청을 보냅니다.
        if (IsLocalPlayerTurn())
        {
            sendAndWaiting = true;
            GameNetwork.Instance.SendDrawCardRequest();
        }

        // broadcast메시지 수신 대기 상태에 들어갑니다.
        gameState.WaitingType = GameMessageType.CARD_DRAWN;
    }

    public void HandleCardDrawn(long playerId, int cardId)
    {
        HandleRoutine();
        
        // CardManager에서 CardId로 카드 데이터를 조회합니다.
        CardData card = CardManager.Instance.FindCard(cardId);
        if (card == null)
        {
            ProcessEndTurn(); // 카드 조회 실패 시 턴이 멈추지 않도록 종료
            return;
        }

        Log($"[황금열쇠] {P(playerId)}: '{card.cardName}' 카드를 뽑았습니다.");

        // 카드 연출을 재생합니다. 연출이 끝나면 EffectType에 맞는 CardEffect 실행 메서드를 호출합니다.
        // 내 카드면 확인을 눌렀을 때, 다른 플레이어 카드면 자동으로 닫힐 때 실행됩니다. (전원 자동 진행 테스트에서는 모두 자동으로 닫힘)
        UIManager.Instance.ShowCardDrawPopup(playerId, card.id, true, () => ExecuteCardEffect(playerId, card));
    }
}