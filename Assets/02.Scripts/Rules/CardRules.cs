using System;
using System.Collections.Generic;

/// <summary>카드 효과를 반영한 뒤 이어서 할 일</summary>
public enum CardOutcomeKind
{
    EndTurn,      // 턴 종료 (돈 받기, 벌금, 또는 효과를 못 쓴 경우)
    Move,         // To 칸으로 이동. ReceivesSalary이면 월급을 받는다
    MoveDirectly, // To 칸으로 순간이동 (월급 없음)
    Swap,         // 위치 바꾸기. 상태는 이미 바뀌었고, 두 말을 옮긴 뒤 턴을 끝낸다 (도착 칸 처리 없음)
    ChooseProperty, // 통행료 2배. 땅 고르기 단계(TOLL_DOUBLE_CHOSEN)로 들어갔으니 고른 땅을 기다린다
}

/// <summary>카드 효과를 쓰지 못한 이유. None이면 정상</summary>
public enum CardProblem
{
    None,
    NoBoard,       // 보드 데이터가 없어 이동할 수 없음
    NoIslandTile,  // 무인도 칸이 없음
    NoTargetTile,  // 카드가 가리키는 종류의 칸이 보드에 없음 (MoveTo)
    NoSwapTarget,  // 위치를 바꿀 상대가 없음 (SwapPosition)
    NoOwnedProperty, // 고를 내 땅이 없음 (TollDouble)
    UnknownEffect, // 처리하지 않는 카드 종류
}

/// <summary>보관형 카드를 뽑았을 때 어떻게 됐는지. 보관형이 아니면 None</summary>
public enum HeldCardResult
{
    None,
    Kept,      // 새로 들었다
    Discarded, // 같은 종류를 이미 들고 있어서 버렸다
}

/// <summary>카드 효과 결과. 돈과 적립금은 규칙이 이미 반영해 두었다.</summary>
public class CardOutcome
{
    public CardOutcomeKind Kind;
    public int To;
    public bool ReceivesSalary;
    public bool IsPenalty;     // 벌금 카드였는지
    public long PenaltyPaid;   // 실제로 낸 벌금 (현금 한도 안에서). 수리비 카드도 여기에 담는다
    public long Transferred;   // 모두에게서 받기, 모두에게 주기에서 실제로 오간 합계
    public int BuildingCount;  // 수리비 카드에서 센 건물 수
    public long SwapTargetId;  // 위치 바꾸기 상대
    public int TargetTo;       // 위치 바꾸기 상대가 옮겨 간 칸 (뽑은 사람이 옮겨 간 칸은 To)
    public HeldCardResult Held; // 보관형 카드를 들었는지, 버렸는지
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
    /// <summary>뽑은 카드의 효과를 반영하고, 이어서 할 일을 돌려준다. targetPlayerId는 위치 바꾸기에서 호스트가 고른 상대(없으면 0).</summary>
    public static CardOutcome ApplyCard(GameState state, GameData data, PlayerState player, CardData card, long targetPlayerId = 0)
    {
        // 보관형은 효과 없이 들기만 하고 턴을 끝낸다. 쓰는 일은 쓰는 때(통행료, 무인도 등)의 규칙이 한다
        if (IsHeldType(card.effectType))
            return new CardOutcome { Kind = CardOutcomeKind.EndTurn, Held = HoldCard(data, player, card) };

        switch (card.effectType)
        {
            case CardEffectType.Bonus: // 은행에서 돈을 받는다
                player.Money += card.amount;
                return new CardOutcome { Kind = CardOutcomeKind.EndTurn };

            case CardEffectType.Penalty: // 은행 또는 적립금에 돈을 낸다. 현금 한도 안에서만 낸다. (매각, 파산 없음)
                long paid = DeductMoneyWithinBalance(player, card.amount);
                if (card.penaltyToWelfareFund) state.WelfareFund += paid;
                return new CardOutcome { Kind = CardOutcomeKind.EndTurn, IsPenalty = true, PenaltyPaid = paid };

            case CardEffectType.MoveTo: // 앞쪽에서 가장 가까운 지정 종류의 칸으로 이동한다. 출발 지점을 지나면 월급
                return MoveToTileType(data, player, card.targetTileType);

            case CardEffectType.MoveBy: // N칸 이동한다. 음수면 뒤로 가고, 뒤로 갈 때는 월급이 없다
                return MoveBy(data, player, card.steps);

            case CardEffectType.GoToIsland: // 무인도로 바로 이동한다. 월급 없음, 더블이어도 추가 턴 없음
                int island = GameRules.FindIslandTileIndex(data);
                if (island < 0) return new CardOutcome { Kind = CardOutcomeKind.EndTurn, Problem = CardProblem.NoIslandTile };
                return new CardOutcome { Kind = CardOutcomeKind.MoveDirectly, To = island };

            case CardEffectType.CollectFromAll: // 다른 플레이어 모두에게서 amount씩 받는다. 각자 현금 한도 안에서만 낸다 (매각, 파산 없음)
                long collected = 0;
                foreach (PlayerState other in OtherActivePlayers(state, player))
                    collected += DeductMoneyWithinBalance(other, card.amount);
                player.Money += collected;
                return new CardOutcome { Kind = CardOutcomeKind.EndTurn, Transferred = collected };

            case CardEffectType.PayToAll: // 다른 플레이어 모두에게 amount씩 준다. 내 현금 한도 안에서 차례 순서대로 준다 (매각, 파산 없음)
                long given = 0;
                foreach (PlayerState other in OtherActivePlayers(state, player))
                {
                    long gift = DeductMoneyWithinBalance(player, card.amount);
                    other.Money += gift;
                    given += gift;
                }
                return new CardOutcome { Kind = CardOutcomeKind.EndTurn, Transferred = given };

            case CardEffectType.RepairCost: // 내 건물 수 × amount를 은행 또는 적립금에 낸다. 현금 한도 안에서만 낸다 (매각, 파산 없음)
                int buildings = CountBuildings(state, player.PlayerId);
                long repair = DeductMoneyWithinBalance(player, (long)card.amount * buildings);
                if (card.penaltyToWelfareFund) state.WelfareFund += repair;
                return new CardOutcome { Kind = CardOutcomeKind.EndTurn, IsPenalty = true, PenaltyPaid = repair, BuildingCount = buildings };

            case CardEffectType.SwapPosition: // 상대와 위치만 바꾼다. 둘 다 칸 효과 없음 (무인도만 예외)
                return SwapPosition(state, data, player, targetPlayerId);

            case CardEffectType.TollDouble: // 내 땅 하나를 고르는 단계로 들어간다. 내 땅이 없으면 효과 없이 끝난다
                if (!state.PropertyStates.Exists(p => p.OwnerId == player.PlayerId))
                    return new CardOutcome { Kind = CardOutcomeKind.EndTurn, Problem = CardProblem.NoOwnedProperty };
                state.WaitingType = GameMessageType.TOLL_DOUBLE_CHOSEN;
                return new CardOutcome { Kind = CardOutcomeKind.ChooseProperty };

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

    // GameManager.ExecuteCardEffect (MoveTo)
    // 지금 칸 다음부터 한 바퀴를 돌며 처음 나오는 그 종류의 칸으로 간다. 출발 칸을 지나거나 출발 칸에 도착하면 월급
    private static CardOutcome MoveToTileType(GameData data, PlayerState player, TileType type)
    {
        int boardSize = data.TileCount;
        if (boardSize <= 0) return new CardOutcome { Kind = CardOutcomeKind.EndTurn, Problem = CardProblem.NoBoard };

        int from = player.Position;
        for (int step = 1; step <= boardSize; step++)
        {
            int to = (from + step) % boardSize;
            TileData tile = data.GetTile(to);
            if (tile == null || tile.Type != type) continue;

            return new CardOutcome { Kind = CardOutcomeKind.Move, To = to, ReceivesSalary = from + step >= boardSize };
        }

        return new CardOutcome { Kind = CardOutcomeKind.EndTurn, Problem = CardProblem.NoTargetTile };
    }

    // HostGameService.HandleDrawCard (호스트만 부른다. 고른 상대는 카드 알림에 실어 보내 모두 같은 상대와 바꾼다)
    /// <summary>위치 바꾸기 상대를 무작위로 고른다. 파산하지 않은 다른 플레이어 중에서 고르고, 없으면 0.</summary>
    public static long PickSwapTarget(GameState state, PlayerState player, Random random)
    {
        List<PlayerState> others = OtherActivePlayers(state, player);
        return others.Count > 0 ? others[random.Next(others.Count)].PlayerId : 0;
    }

    // GameManager.ExecuteCardEffect (SwapPosition)
    // 위치만 맞바꾼다. 이동이나 도착 처리를 거치지 않으므로 월급, 통행료, 구매, 기부금, 황금열쇠는 일어나지 않는다.
    // 세계여행 예정은 둘 다 사라진다. 무인도만 예외로, 상대가 무인도에 있었다면 뽑은 사람이 남은 턴을 넘겨받고(0이면 3) 상대는 풀려난다.
    private static CardOutcome SwapPosition(GameState state, GameData data, PlayerState player, long targetPlayerId)
    {
        PlayerState target = OtherActivePlayers(state, player).Find(p => p.PlayerId == targetPlayerId);
        if (target == null) return new CardOutcome { Kind = CardOutcomeKind.EndTurn, Problem = CardProblem.NoSwapTarget };

        int myPosition = player.Position;
        int targetPosition = target.Position;
        player.Position = targetPosition;
        target.Position = myPosition;

        player.WorldTravelPending = false;
        target.WorldTravelPending = false;

        TileData tile = data.GetTile(targetPosition);
        if (tile != null && tile.Type == TileType.ISLAND)
        {
            player.IslandTurnsRemaining = target.IslandTurnsRemaining > 0 ? target.IslandTurnsRemaining : GameRules.IslandTurns;
            target.IslandTurnsRemaining = 0;
            state.IsDouble = false; // 무인도에 갇혔으니 더블이어도 한 번 더 굴리지 않는다
        }

        return new CardOutcome { Kind = CardOutcomeKind.Swap, To = player.Position, SwapTargetId = target.PlayerId, TargetTo = target.Position };
    }

    // HostGameService.HandleChooseTollDouble
    /// <summary>통행료 2배를 붙일 수 있는 땅인지. 고르는 사람이 주인인 땅이어야 한다.</summary>
    public static bool CanChooseTollDouble(GameState state, PlayerState player, int propertyId)
    {
        PropertyState property = state.PropertyStates.Find(p => p.PropertyId == propertyId);
        return property != null && property.OwnerId == player.PlayerId;
    }

    // GameManager.HandleTollDoubleChosen
    /// <summary>고른 땅에 통행료 2배 표시를 붙인다. 다음 통행료 한 번이 2배가 되고, 통행료를 받으면 사라진다.</summary>
    public static void ApplyTollDouble(GameState state, PlayerState player, int propertyId)
    {
        PropertyState property = state.PropertyStates.Find(p => p.PropertyId == propertyId);
        if (property != null) property.TollDoubleOwnerId = player.PlayerId;
    }

    // HostGameService.AutoAct (봇과 AFK 대신 고르기)
    /// <summary>통행료가 가장 비싼 내 땅을 고른다. 없으면 0.</summary>
    public static int PickTollDoubleProperty(GameState state, GameData data, long playerId)
    {
        int best = 0;
        long bestToll = -1;
        foreach (PropertyState property in state.PropertyStates)
        {
            if (property.OwnerId != playerId) continue;
            long toll = data.Properties.GetToll(property, state.PropertyStates);
            if (toll > bestToll) { best = property.PropertyId; bestToll = toll; }
        }
        return best;
    }

    // ───────────── 보관형 ─────────────

    /// <summary>들고 있다가 쓰는 카드(보관형)인지.</summary>
    public static bool IsHeldType(CardEffectType type)
    {
        switch (type)
        {
            case CardEffectType.TollFree:
            case CardEffectType.TollHalf:
            case CardEffectType.IslandEscape:
            case CardEffectType.TakeoverShield:
            case CardEffectType.BuildDiscount:
            case CardEffectType.SalaryDouble:
                return true;
            default:
                return false;
        }
    }

    // GameManager.ExecuteCardEffect (보관형)
    // 종류마다 1장만 든다. 같은 종류를 이미 들고 있으면 새 카드는 버린다. (id가 아니라 효과 종류로 비교해서, 같은 효과의 카드가 여러 장 생겨도 1장만 든다)
    private static HeldCardResult HoldCard(GameData data, PlayerState player, CardData card)
    {
        player.HeldCardIds ??= new List<int>();
        if (FindHeldCard(data, player, card.effectType) != null) return HeldCardResult.Discarded;

        player.HeldCardIds.Add(card.id);
        return HeldCardResult.Kept;
    }

    // ───────────── 보관형 쓰기 ─────────────
    // 쓰는 일은 호스트와 클라이언트가 같은 함수로 한다. 호스트가 알림에 "썼다"를 실어 보내면 받은 쪽도 같은 함수를 부른다.

    /// <summary>무인도 탈출권을 지금 쓸 수 있는지. 들고 있고, 무인도에서 영업정지 중이어야 한다. (내 차례가 시작될 때 묻는다)</summary>
    public static bool CanUseIslandEscape(GameData data, PlayerState player)
    {
        return player != null && player.IslandTurnsRemaining > 0 && FindHeldCard(data, player, CardEffectType.IslandEscape) != null;
    }

    /// <summary>
    /// 무인도 탈출권을 쓴다. 카드를 내고 영업정지를 풀어서, 이어지는 주사위는 평소처럼 움직인다. (더블이면 한 번 더 굴린다)
    /// 쓸 수 없으면 아무것도 바꾸지 않고 false를 돌려준다.
    /// </summary>
    public static bool UseIslandEscape(GameData data, PlayerState player)
    {
        if (!CanUseIslandEscape(data, player)) return false;

        ConsumeHeldCard(data, player, CardEffectType.IslandEscape);
        player.IslandTurnsRemaining = 0;
        return true;
    }

    /// <summary>월급 2배 카드를 들고 있으면 쓴다(묻지 않음). 썼으면 true. 월급을 줄 때 GameRules.MoveTo가 부른다.</summary>
    public static bool UseSalaryDouble(GameData data, PlayerState player)
    {
        if (data == null || player == null || FindHeldCard(data, player, CardEffectType.SalaryDouble) == null) return false;
        ConsumeHeldCard(data, player, CardEffectType.SalaryDouble);
        return true;
    }

    // ── 건설 할인권 ──

    /// <summary>들고 있는 건설 할인권의 할인 비율(퍼센트, 카드의 amount). 없으면 0.</summary>
    public static int BuildDiscountPercent(GameData data, PlayerState player)
    {
        CardData card = data != null && player != null ? FindHeldCard(data, player, CardEffectType.BuildDiscount) : null;
        return card != null ? (int)System.Math.Min(100, System.Math.Max(0, card.amount)) : 0;
    }

    /// <summary>할인한 건설비. percent가 0이면 그대로.</summary>
    public static long DiscountedBuildCost(long cost, int percent) => cost * (100 - percent) / 100;

    /// <summary>건설 할인권을 쓴다. 들고 있으면 카드를 내고 할인 비율을 돌려준다. 없으면 0.</summary>
    public static int UseBuildDiscount(GameData data, PlayerState player)
    {
        int percent = BuildDiscountPercent(data, player);
        if (percent > 0) ConsumeHeldCard(data, player, CardEffectType.BuildDiscount);
        return percent;
    }

    // ── 통행료 면제권, 반값권 ──

    /// <summary>통행료 면제권이나 반값권을 하나라도 들고 있는지. (남의 땅에 도착했을 때 쓸지 묻는다)</summary>
    public static bool HasTollCard(GameData data, PlayerState player)
    {
        return FindHeldCard(data, player, CardEffectType.TollFree) != null || FindHeldCard(data, player, CardEffectType.TollHalf) != null;
    }

    /// <summary>
    /// 통행료 카드를 쓴다. card가 면제권이나 반값권이고 들고 있으면 카드를 내고, 통행료를 다 낼 때까지 PlayerState.TollCardInUse에 적어 둔다.
    /// 쓰지 않거나(null) 들고 있지 않으면 아무것도 바꾸지 않고 false.
    /// </summary>
    public static bool UseTollCard(GameData data, PlayerState player, CardEffectType? card)
    {
        if (player == null || !card.HasValue) return false;
        if (card.Value != CardEffectType.TollFree && card.Value != CardEffectType.TollHalf) return false;
        if (FindHeldCard(data, player, card.Value) == null) return false;

        ConsumeHeldCard(data, player, card.Value);
        player.TollCardInUse = card.Value;
        return true;
    }

    /// <summary>봇과 AFK가 쓸 통행료 카드. 면제권을 먼저, 없으면 반값권. 둘 다 없으면 null.</summary>
    public static CardEffectType? PickTollCard(GameData data, PlayerState player)
    {
        if (FindHeldCard(data, player, CardEffectType.TollFree) != null) return CardEffectType.TollFree;
        if (FindHeldCard(data, player, CardEffectType.TollHalf) != null) return CardEffectType.TollHalf;
        return null;
    }

    /// <summary>통행료 카드를 적용한 금액. 면제권은 0, 반값권은 절반, 없으면 그대로.</summary>
    public static long ApplyTollCard(long toll, CardEffectType? card)
    {
        if (card == CardEffectType.TollFree) return 0;
        if (card == CardEffectType.TollHalf) return toll / 2;
        return toll;
    }

    // ── 인수 방지권 ──

    /// <summary>땅 주인이 인수 방지권을 들고 있는지. 남이 인수를 고르면 주인에게 쓸지 묻는다. (주인 차례가 아니어도 묻는다)</summary>
    public static bool HasTakeoverShield(GameData data, PlayerState owner)
    {
        return owner != null && FindHeldCard(data, owner, CardEffectType.TakeoverShield) != null;
    }

    /// <summary>인수 방지권을 쓴다. 들고 있으면 카드를 내고 true. 인수는 일어나지 않는다. (인수하려던 사람은 통행료만 낸 채로 턴이 끝난다)</summary>
    public static bool UseTakeoverShield(GameData data, PlayerState owner)
    {
        if (!HasTakeoverShield(data, owner)) return false;
        ConsumeHeldCard(data, owner, CardEffectType.TakeoverShield);
        return true;
    }

    // 들고 있던 이 종류의 카드를 낸다 (보관형은 모두 1회용)
    private static void ConsumeHeldCard(GameData data, PlayerState player, CardEffectType type)
    {
        CardData held = FindHeldCard(data, player, type);
        if (held != null) player.HeldCardIds.Remove(held.id);
    }

    /// <summary>들고 있는 카드 중 이 종류의 카드. 없으면 null.</summary>
    public static CardData FindHeldCard(GameData data, PlayerState player, CardEffectType type)
    {
        if (player.HeldCardIds == null) return null;
        foreach (int id in player.HeldCardIds)
        {
            CardData held = data.FindCard(id);
            if (held != null && held.effectType == type) return held;
        }
        return null;
    }

    // 뽑은 사람을 뺀, 파산하지 않은 플레이어들 (차례 순서대로). 카드 점수(BalanceRules.CardScore)도 쓴다
    internal static List<PlayerState> OtherActivePlayers(GameState state, PlayerState player)
    {
        var others = new List<PlayerState>();
        foreach (long id in state.PlayerOrder)
        {
            if (id == player.PlayerId) continue;
            PlayerState other = state.PlayerStates.Find(p => p.PlayerId == id);
            if (other != null && !other.IsBankrupt) others.Add(other);
        }
        return others;
    }

    // 내 건물 수. 땅마다 건물 단계만큼 센다 (땅만 0, 별장 1, 빌딩 2, 호텔 3). 카드 점수(BalanceRules.CardScore)도 쓴다
    internal static int CountBuildings(GameState state, long ownerId)
    {
        int count = 0;
        foreach (PropertyState property in state.PropertyStates)
            if (property.OwnerId == ownerId) count += (int)property.BuildingLevel;
        return count;
    }

    // GameManager.DeductMoneyWithinBalance (현금 한도 안에서만 뺀다. 실제로 뺀 금액을 돌려준다)
    private static long DeductMoneyWithinBalance(PlayerState player, long amount)
    {
        long paid = Math.Min(amount, Math.Max(0, player.Money));
        player.Money -= paid;
        return paid;
    }
}