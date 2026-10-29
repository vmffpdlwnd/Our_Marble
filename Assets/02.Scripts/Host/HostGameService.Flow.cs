using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// HostGameService의 진행 규칙: 주사위, 이동, 도착 처리, 턴 전환, 황금열쇠, 세계여행, 게임 종료. (나뉜 파일 설명은 HostGameService.cs 참고)
/// </summary>
public partial class HostGameService
{
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
        List<int> cardIds = CardManager.Instance.GetCardIds();
        CardData card = null;
        int cardId = 0;
        if (cardIds != null && cardIds.Count > 0)
        {
            cardId = cardIds[random.Next(cardIds.Count)];
            card = CardManager.Instance.FindCard(cardId);
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
        TileData destination = to >= 0 && to < BoardManager.Instance.TileCount ? BoardManager.Instance.GetTileData(to) : null;
        if (destination == null) { SendError(senderId, "INVALID_DESTINATION", "갈 수 없는 칸이에요"); return; }
        if (destination.Type == TileType.WORLD_TRAVEL) { SendError(senderId, "INVALID_DESTINATION", "세계여행 칸은 목적지로 고를 수 없어요"); return; }

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
                state.WaitingType = GameMessageType.CARD_DRAWN; // 카드 뽑기 요청을 기다린다
                break;

            case TileType.PROPERTY:
                ProcessPropertyArrival(player, tile.PropertyId);
                break;
        }
    }

    // GameManager.ProcessEndTurn
    private void ProcessEndTurn()
    {
        if (GameRules.EndTurn(state) == EndTurnResult.NextTurn)
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

    // ───────────── 황금열쇠 효과 ─────────────

    // GameManager.ExecuteCardEffect
    // 뽑은 카드의 효과를 반영하고, 다음 흐름(턴 종료 또는 도착 칸 처리)까지 진행한다.
    private void ExecuteCardEffect(PlayerState player, CardData card)
    {
        switch (card.effectType)
        {
            case CardEffectType.Bonus: // 은행에서 돈을 받는다
                player.Money += card.amount;
                ProcessEndTurn();
                break;

            case CardEffectType.Penalty: // 은행 또는 적립금에 돈을 낸다. 현금 한도 안에서만 낸다. (매각, 파산 없음)
                long paid = DeductMoneyWithinBalance(player, card.amount);
                if (card.penaltyToFestivalPool) state.WelfareFund += paid;
                Debug.Log($"[HostGameService] 카드 벌금: {player.Nickname} {card.amount} 중 {paid} 납부 (현금 {player.Money})");
                ProcessEndTurn();
                break;

            case CardEffectType.MoveTo: // 지정한 칸으로 앞으로 이동한다. 출발 지점을 지나면 월급
                int target = card.targetTileId;
                MovePlayer(player, target, target < player.Position);
                break;

            case CardEffectType.MoveBy: // N칸 이동한다. 음수면 뒤로 가고, 뒤로 갈 때는 월급이 없다
                ExecuteMoveBy(player, card.steps);
                break;

            case CardEffectType.GoToInspection: // 무인도로 바로 이동한다. 월급 없음, 더블이어도 추가 턴 없음
                int island = FindIslandTileIndex();
                if (island < 0)
                {
                    Debug.LogError("[HostGameService] 무인도 카드 실패: 무인도 칸 없음");
                    ProcessEndTurn();
                }
                else
                {
                    MovePlayerDirectly(player, island);
                }
                break;

            default:
                Debug.LogError($"[HostGameService] 처리하지 않은 CardEffectType: {card.effectType}");
                ProcessEndTurn(); // 턴이 멈추지 않도록 종료
                break;
        }
    }

    // GameManager.ExecuteMoveByEffect
    private void ExecuteMoveBy(PlayerState player, int steps)
    {
        int boardSize = BoardManager.Instance.TileCount;
        if (boardSize <= 0)
        {
            Debug.LogError("[HostGameService] MoveBy 카드 실패: 보드 데이터 없음");
            ProcessEndTurn();
            return;
        }

        int from = player.Position;
        int to = ((from + steps) % boardSize + boardSize) % boardSize;
        bool shouldReceiveSalary = steps > 0 && from + steps >= boardSize;

        if (steps > 0)
            MovePlayer(player, to, shouldReceiveSalary);
        else
            MovePlayerDirectly(player, to);
    }

    // GameManager.DeductMoneyWithinBalance (현금 한도 안에서만 뺀다. 실제로 뺀 금액을 돌려준다)
    private long DeductMoneyWithinBalance(PlayerState player, long amount)
    {
        long paid = Math.Min(amount, Math.Max(0, player.Money));
        player.Money -= paid;
        return paid;
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
}