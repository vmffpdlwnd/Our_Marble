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
}