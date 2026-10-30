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

    // 카드 효과를 규칙(CardRules)으로 반영하고, 카드 종류에 맞게 로그와 연출을 이어서 진행합니다.
    private void ExecuteCardEffect(long playerId, CardData card)
    {
        PlayerState player = GetPlayerState(playerId);
        if (player == null)
        {
            ProcessEndTurn();
            return;
        }

        // 돈 받기, 벌금, 이동할 칸 계산은 규칙(CardRules.ApplyCard)이 하고, 규칙은 상태만 바꿉니다.
        // 아래에서는 돈 변화 알림과 로그, 말 이동 연출, 다음 흐름(턴 종료, 도착 칸 처리)을 이어서 진행합니다.
        long before = player.Money;
        int fromPosition = player.Position;
        CardOutcome outcome = CardRules.ApplyCard(gameState, gameData, player, card);

        switch (outcome.Problem)
        {
            case CardProblem.NoBoard:
                Debug.LogError($"[GameManager] MoveBy 카드 실패: 보드 데이터 없음 (보드 칸 수 {gameData.TileCount})");
                break;

            case CardProblem.NoIslandTile:
                Debug.LogError($"[GameManager] 무인도 카드 실패: 무인도 칸 없음");
                break;

            case CardProblem.UnknownEffect:
                Debug.LogError($"[GameManager] 처리하지 않은 CardEffectType: {card.effectType}");
                break;
        }

        switch (card.effectType)
        {
            case CardEffectType.Bonus: // 은행에서 돈을 받는다
                screen.NotifyMoneyChanged(playerId, before, player.Money);
                // 재화 획득 연출을 재생합니다.
                break;

            case CardEffectType.Penalty: // 은행 또는 기부금(WelfareFund)에 돈을 낸다 (현금 한도 안에서만, 매각/파산 없음)
                screen.NotifyMoneyChanged(playerId, before, player.Money);
                Log($"[카드:벌금] {P(playerId)}: 벌금 {Won(card.amount)} 중 {Won(outcome.PenaltyPaid)} 납부 (현금 {Won(player.Money)})");
                // 재화 손실 연출을 재생합니다.
                break;

            case CardEffectType.MoveTo: // 지정한 칸으로 앞으로 이동한다 (출발지를 지나면 월급)
                Log($"[카드:이동] {P(playerId)}: {TileName(outcome.To)}(으)로 이동");
                break;

            case CardEffectType.MoveBy: // N칸 이동한다 (음수면 뒤로, 뒤로 갈 때는 월급 없음)
                if (outcome.Problem == CardProblem.None)
                    Log($"[카드:이동] {P(playerId)}: {card.steps}칸 이동 ({TileName(fromPosition)} → {TileName(outcome.To)})");
                break;

            case CardEffectType.GoToInspection: // 무인도로 바로 이동한다 (월급 없음, 더블이어도 추가 턴 없음)
                if (outcome.Problem == CardProblem.None)
                    Log($"[카드:무인도] {P(playerId)}: 무인도로 이동합니다.");
                break;
        }

        switch (outcome.Kind)
        {
            case CardOutcomeKind.Move:
                // 이동이 끝나면 두 함수 모두 알아서 ProcessArrival을 호출합니다.
                if (card.effectType == CardEffectType.MoveTo)
                    MovePlayerToDestination(playerId, fromPosition, outcome.To, outcome.ReceivesSalary);
                else
                    HandlePlayerMoved(playerId, fromPosition, outcome.To, outcome.ReceivesSalary);
                break;

            case CardOutcomeKind.MoveDirectly:
                // 뒤로 이동하거나 무인도로 순간이동 (월급 없음) → 도착 처리에서 영업정지 설정 + 추가 턴 없이 턴 넘김
                MovePlayerDirectly(playerId, outcome.To);
                break;

            default:
                ProcessEndTurn(); // 돈 받기, 벌금, 효과를 못 쓴 경우: 턴 종료 (턴이 멈추지 않도록)
                break;
        }
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
        
        // GameData에서 CardId로 카드 데이터를 조회합니다.
        CardData card = gameData.FindCard(cardId);
        if (card == null)
        {
            ProcessEndTurn(); // 카드 조회 실패 시 턴이 멈추지 않도록 종료
            return;
        }

        Log($"[황금열쇠] {P(playerId)}: '{card.cardName}' 카드를 뽑았습니다.");

        // 카드 연출을 재생합니다. 연출이 끝나면 EffectType에 맞는 CardEffect 실행 메서드를 호출합니다.
        // 내 카드면 확인을 눌렀을 때, 다른 플레이어 카드면 자동으로 닫힐 때 실행됩니다. (전원 자동 진행 테스트에서는 모두 자동으로 닫힘)
        screen.ShowCardDrawPopup(playerId, card.id, true, () => ExecuteCardEffect(playerId, card));
    }
}