using System;

/// <summary>카드 효과를 반영한 뒤 이어서 할 일</summary>
public enum CardOutcomeKind
{
    EndTurn,      // 턴 종료 (돈 받기, 벌금, 또는 효과를 못 쓴 경우)
    Move,         // To 칸으로 이동. ReceivesSalary이면 월급을 받는다
    MoveDirectly, // To 칸으로 순간이동 (월급 없음)
}

/// <summary>카드 효과를 쓰지 못한 이유. None이면 정상</summary>
public enum CardProblem
{
    None,
    NoBoard,       // 보드 데이터가 없어 이동할 수 없음
    NoIslandTile,  // 무인도 칸이 없음
    UnknownEffect, // 처리하지 않는 카드 종류
}

/// <summary>카드 효과 결과. 돈과 적립금은 규칙이 이미 반영해 두었다.</summary>
public class CardOutcome
{
    public CardOutcomeKind Kind;
    public int To;
    public bool ReceivesSalary;
    public bool IsPenalty;     // 벌금 카드였는지
    public long PenaltyPaid;   // 실제로 낸 벌금 (현금 한도 안에서)
    public CardProblem Problem;
}

/// <summary>
/// 황금열쇠 카드 효과 규칙. 화면(Unity 오브젝트)을 모른다.
/// 어떤 카드를 뽑을지(무작위)와 그 뒤의 이동, 도착 처리는 부른 쪽이 한다.
/// 각 함수 위에 짝이 되는 GameManager 함수를 적어 두었다. 규칙을 바꿀 때는 양쪽을 함께 고친다.
/// </summary>
public static class CardRules
{
    // GameManager.ExecuteCardEffect
    /// <summary>뽑은 카드의 효과를 반영하고, 이어서 할 일을 돌려준다.</summary>
    public static CardOutcome ApplyCard(GameState state, GameData data, PlayerState player, CardData card)
    {
        switch (card.effectType)
        {
            case CardEffectType.Bonus: // 은행에서 돈을 받는다
                player.Money += card.amount;
                return new CardOutcome { Kind = CardOutcomeKind.EndTurn };

            case CardEffectType.Penalty: // 은행 또는 적립금에 돈을 낸다. 현금 한도 안에서만 낸다. (매각, 파산 없음)
                long paid = DeductMoneyWithinBalance(player, card.amount);
                if (card.penaltyToFestivalPool) state.WelfareFund += paid;
                return new CardOutcome { Kind = CardOutcomeKind.EndTurn, IsPenalty = true, PenaltyPaid = paid };

            case CardEffectType.MoveTo: // 지정한 칸으로 앞으로 이동한다. 출발 지점을 지나면 월급
                int target = card.targetTileId;
                return new CardOutcome { Kind = CardOutcomeKind.Move, To = target, ReceivesSalary = target < player.Position };

            case CardEffectType.MoveBy: // N칸 이동한다. 음수면 뒤로 가고, 뒤로 갈 때는 월급이 없다
                return MoveBy(data, player, card.steps);

            case CardEffectType.GoToInspection: // 무인도로 바로 이동한다. 월급 없음, 더블이어도 추가 턴 없음
                int island = GameRules.FindIslandTileIndex(data);
                if (island < 0) return new CardOutcome { Kind = CardOutcomeKind.EndTurn, Problem = CardProblem.NoIslandTile };
                return new CardOutcome { Kind = CardOutcomeKind.MoveDirectly, To = island };

            default:
                // 턴이 멈추지 않도록 종료한다
                return new CardOutcome { Kind = CardOutcomeKind.EndTurn, Problem = CardProblem.UnknownEffect };
        }
    }

    // GameManager.ExecuteMoveByEffect
    private static CardOutcome MoveBy(GameData data, PlayerState player, int steps)
    {
        int boardSize = data.TileCount;
        if (boardSize <= 0) return new CardOutcome { Kind = CardOutcomeKind.EndTurn, Problem = CardProblem.NoBoard };

        int from = player.Position;
        int to = ((from + steps) % boardSize + boardSize) % boardSize;
        bool shouldReceiveSalary = steps > 0 && from + steps >= boardSize;

        if (steps > 0)
            return new CardOutcome { Kind = CardOutcomeKind.Move, To = to, ReceivesSalary = shouldReceiveSalary };

        return new CardOutcome { Kind = CardOutcomeKind.MoveDirectly, To = to };
    }

    // GameManager.DeductMoneyWithinBalance (현금 한도 안에서만 뺀다. 실제로 뺀 금액을 돌려준다)
    private static long DeductMoneyWithinBalance(PlayerState player, long amount)
    {
        long paid = Math.Min(amount, Math.Max(0, player.Money));
        player.Money -= paid;
        return paid;
    }
}