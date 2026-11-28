using System;
using System.Collections.Generic;

/// <summary>주사위를 실제로 반영하지 않고 미리 본 결과. 만들 때 상태(GameState, PlayerState)는 바꾸지 않는다.</summary>
public class RollPreview
{
    public int Dice1;
    public int Dice2;
    public DiceMoveKind Kind;   // GameRules.ApplyDice가 돌려줄 Kind와 같다
    public int To;              // 도착 칸 (IslandStay면 지금 칸)
    public int Score;           // 총점: 도착 칸 점수 + 월급 + 한 번 더 굴림. 클수록 좋다 (v1.3.2 점수제, BalanceRules 맨 위의 점수 표)
    public long Toll;           // 남의 땅이면 통행료(통행료 카드 할인 전), 아니면 0
    public bool ReceivesSalary; // 출발 지점을 지나 월급을 받는지
    public bool RollsAgain;     // 이동한 뒤 한 번 더 굴리는지 (무인도 탈출 더블과 3연속 더블은 아님)

    public bool IsDouble => Dice1 == Dice2;
}

/// <summary>순위 보정에서 두 후보 중 하나를 고른 이유. 로그에 남겨 "왜 이게 골라졌는지"를 확인한다. (v1.3.2)</summary>
public enum RollPickReason
{
    Compared,          // 결과를 비교해 더 나쁜 쪽(1등) 또는 더 좋은 쪽(꼴찌)
    SameFirst,         // 결과가 같아 먼저 굴린 쪽
    IslandFail,        // 무인도 영업정지: 탈출 실패(더블이 아닌) 쪽 (1등)
    IslandEscape,      // 무인도 영업정지: 탈출하는(더블인) 쪽 (꼴찌)
    IslandBothFail,    // 무인도 영업정지: 둘 다 탈출 실패라 먼저 굴린 쪽
    IslandRerollLimit, // 무인도 영업정지: 다시 굴려도 계속 둘 다 더블이라 나중 쪽 (1등, 거의 일어나지 않는다)
}

/// <summary>
/// 순위 보정에서 두 번 굴린 결과. 고른 쪽만 쓰지만, 로그에 두 후보와 이유를 같이 남기려고 모두 돌려준다. (v1.3.2)
/// </summary>
public class RollChoice
{
    public RollPreview First;     // 먼저 굴린 쪽 [1]
    public RollPreview Second;    // 나중에 굴린 쪽 [2]
    public RollPreview Picked;    // 고른 쪽 (First나 Second와 같은 객체)
    public RollPickReason Reason;
    public bool IslandLocked;     // 무인도 영업정지 중의 탈출 굴림인지
    public int Rerolls;           // 1등 무인도 굴림에서 둘 다 더블이라 다시 굴린 횟수

    public bool PickedFirst => Picked == First;
}

/// <summary>
/// 지금의 순위 보정 상태. 1등과 꼴찌는 살아 있는 사람 중 RankingRules 순서로 한 명씩 정해진다.
/// 꺼져 있어도 1등과 꼴찌는 채워 둔다. (F1 개발용 창이 보여 준다) 패널티와 보너스를 줄지는 IsLeader, IsLast로 묻는다.
/// </summary>
public class RankBalance
{
    public PlayerState Leader; // 1등. 살아 있는 사람이 2명 미만이면 null
    public PlayerState Last;   // 꼴찌. 살아 있는 사람이 2명 미만이면 null
    public long LeaderAsset;   // 1등의 총자산 (현금 + 가진 땅의 투자금)
    public long LastAsset;     // 꼴찌의 총자산
    public bool Active;        // 1등과 꼴찌의 차이가 커서 패널티와 보너스를 켜는지

    /// <summary>켜져 있고 이 사람이 1등인가 (1등 패널티 대상)</summary>
    public bool IsLeader(long playerId) => Active && Leader != null && Leader.PlayerId == playerId;

    /// <summary>켜져 있고 이 사람이 꼴찌인가 (꼴찌 보너스 대상)</summary>
    public bool IsLast(long playerId) => Active && Last != null && Last.PlayerId == playerId;
}

/// <summary>
/// 순위 보정 규칙 (v1.3). 순위 보정을 켜는 조건(1등과 꼴찌의 보유액 차이)과,
/// 1등 패널티와 꼴찌 보너스에서 주사위를 두 번 굴려 한쪽을 고를 때의 판정, 황금열쇠 뽑기 가중치를 맡는다.
/// 숫자 크기가 아니라 결과로 판단한다. v1.3.2부터 결과와 카드를 점수로 매겨 비교한다. (기획서 "순위 보정과 뉴비 옵션")
/// 판정은 호스트만 하고, 고른 주사위만 알림으로 보내므로 클라이언트와 어긋나지 않는다. 화면을 모른다.
/// </summary>
public static class BalanceRules
{
    // ───────────── 점수 (v1.3.2 점수제) ─────────────
    // 주사위 결과와 황금열쇠 카드를 점수로 비교한다. 플레이해 보고 숫자만 고치면 되게 여기에 모아 둔다.
    // 돈이 오가는 것(통행료, 월급, 기부금, 카드 금액)은 1점 = PointWon원으로 바꾸고, 돈이 아닌 것은 아래 고정 점수다.

    public const long PointWon = 10000;          // 1점이 몇 원인지
    public const int WorldTravelScore = 30;      // 세계여행 칸: 다음 차례에 원하는 칸으로 간다
    public const int BuyableLandScore = 20;      // 빈 땅인데 살 돈이 있다 (없으면 0)
    public const int BuildableLandScore = 15;    // 내 땅인데 지을 수 있다 (최고 단계이거나 돈이 없으면 0)
    public const int IslandScore = -40;          // 무인도 칸, 무인도에 남음, 3연속 더블, 무인도 카드
    public const int RollAgainScore = 10;        // 더블로 한 번 더 굴린다
    public const int TollDoubleScore = 10;       // 통행료 2배 카드 (내 땅이 없으면 0)
    public const int TollFreeScore = 15;         // 통행료 면제권
    public const int TollHalfScore = 8;          // 통행료 반값권
    public const int BuildDiscountScore = 8;     // 건설 할인권
    public const int IslandEscapeScore = 5;      // 무인도 탈출권 (갇혀야 쓸모 있다)
    public const int TakeoverShieldScore = 5;    // 인수 방지권 (내 땅이 없으면 0)
    public const int NestedGoldenKeyScore = 0;   // 카드로 옮겨 간 칸이 또 황금열쇠면 (기대 점수를 끝없이 계산하지 않게 0으로 본다)

    // 황금열쇠 뽑기 가중치. 평소에는 모든 카드가 CardBaseWeight로 같은 확률이다.
    // 꼴찌와 뉴비는 CardBaseWeight + 점수/2, 1등은 CardBaseWeight - 점수/2이고, CardMinWeight보다 작아지지 않는다.
    public const int CardBaseWeight = 20;
    public const int CardMinWeight = 5;

    // ───────────── 켜는 조건 ─────────────

    /// <summary>
    /// 꼴찌 총자산이 1등의 이 비율(%) 미만이면 순위 보정을 켠다. 인원과 상관없이 같은 값이다. (2인 게임도)
    /// 차이가 작을 때까지 켜면 앞선 사람이 이유 없이 손해를 보므로, 벌어졌을 때만 켠다. 값은 플레이해 보고 고친다.
    /// </summary>
    public const int ActivePercent = 70;

    /// <summary>
    /// 지금의 1등, 꼴찌와 순위 보정을 켜는지 계산한다. 상태는 바꾸지 않는다.
    /// 순위는 결과 창의 등수와 같은 기준(RankingRules)이라 1등과 꼴찌는 한 명씩만 정해진다. 파산한 사람은 뺀다.
    /// 사람, 봇, AFK 모두 같은 규칙을 받는다. 턴마다 자산이 바뀌므로 쓰는 순간에 다시 부른다.
    /// </summary>
    public static RankBalance GetRankBalance(GameState state, PropertyRules propertyRules)
    {
        var balance = new RankBalance();

        List<PlayerState> ranked = RankingRules.Rank(state.PlayerStates, state.PropertyStates, propertyRules);
        ranked.RemoveAll(p => p.IsBankrupt);
        if (ranked.Count < 2) return balance;

        balance.Leader = ranked[0];
        balance.Last = ranked[ranked.Count - 1];
        balance.LeaderAsset = propertyRules.GetTotalAsset(balance.Leader.PlayerId, balance.Leader.Money, state.PropertyStates);
        balance.LastAsset = propertyRules.GetTotalAsset(balance.Last.PlayerId, balance.Last.Money, state.PropertyStates);

        // 1등의 총자산이 0 이하면 비율을 잴 수 없으므로 켜지 않는다
        balance.Active = balance.LeaderAsset > 0 && balance.LastAsset * 100 < balance.LeaderAsset * ActivePercent;
        return balance;
    }

    // ───────────── 봇 너프 ─────────────

    /// <summary>
    /// 봇이 살아 있는 사람 중 가장 높은 사람보다 앞서는가. 앞서면 봇의 판단을 약하게 한다. (BotSettings.Nerfed)
    /// 순위 목록의 순서가 아니라 총자산(RankingRules와 같은 기준)으로 비교해서, 봇이 **더 많을 때만** 앞선 것으로 본다.
    /// (순위 목록은 동점이면 선턴 순서를 따르므로, 모두 시작 자금이 같은 게임 시작 때 선턴이 앞선 봇이 너프되는 일을 막는다)
    /// 1등 패널티와 상관없는 별도 장치라, 1등과 꼴찌의 차이(Active)와 상관없이 본다.
    /// AFK는 사람이므로 사람 쪽에 든다. 파산한 봇이거나 살아 있는 사람이 없으면 false.
    /// </summary>
    public static bool IsBotAheadOfHumans(GameState state, PropertyRules propertyRules, long botId)
    {
        PlayerState bot = state.PlayerStates.Find(p => p.PlayerId == botId);
        if (bot == null || bot.IsBankrupt) return false;

        long botAsset = propertyRules.GetTotalAsset(bot.PlayerId, bot.Money, state.PropertyStates);
        bool anyHuman = false;
        long bestHumanAsset = long.MinValue;
        foreach (PlayerState player in state.PlayerStates)
        {
            if (player.IsBot || player.IsBankrupt) continue;
            anyHuman = true;
            bestHumanAsset = Math.Max(bestHumanAsset, propertyRules.GetTotalAsset(player.PlayerId, player.Money, state.PropertyStates));
        }
        return anyHuman && botAsset > bestHumanAsset;
    }

    // ───────────── 주사위 결과 판정 ─────────────

    /// <summary>
    /// 주사위 결과를 상태를 바꾸지 않고 미리 본다.
    /// GameRules.ApplyDice와 같은 순서(무인도 탈출 실패 → 무인도 탈출 → 3연속 더블 → 이동)로 판단한다. ApplyDice를 고치면 여기도 같이 고친다.
    /// 무인도 탈출권은 굴리기 전에 이미 써서 IslandTurnsRemaining이 0이 된 상태로 부른다. (HostGameService.HandleRollDice와 같은 순서)
    /// balance는 황금열쇠 칸의 기대 점수를 낼 때 쓴다. (1등과 꼴찌는 카드 가중치가 달라서 같은 칸도 점수가 다르다)
    /// </summary>
    public static RollPreview PreviewRoll(GameState state, GameData data, PlayerState player, RankBalance balance, int dice1, int dice2)
    {
        var preview = new RollPreview { Dice1 = dice1, Dice2 = dice2, To = player.Position };

        bool isDouble = dice1 == dice2;
        int doubleCount = isDouble ? state.ConsecutiveDoubleCount + 1 : 0;

        bool islandLocked = IsIslandLocked(data, player);

        // 무인도에서 더블이 아니면 이동 없이 턴이 넘어간다
        if (islandLocked && !isDouble)
        {
            preview.Kind = DiceMoveKind.IslandStay;
            preview.Score = IslandScore;
            return preview;
        }

        // 3번째 연속 더블이면 무인도로 바로 간다 (월급 없음). 뉴비는 무인도 대신 평소대로 이동하고 턴이 끝난다
        bool newbieThirdDouble = doubleCount >= 3 && player.IsNewbie;
        if (doubleCount >= 3 && !player.IsNewbie)
        {
            int islandPosition = GameRules.FindIslandTileIndex(data);
            if (islandPosition >= 0)
            {
                preview.Kind = DiceMoveKind.DirectToIsland;
                preview.To = islandPosition;
                preview.Score = IslandScore;
                return preview;
            }
        }

        int tileCount = data.TileCount;
        if (tileCount <= 0)
        {
            // 보드가 없으면 비교할 것이 없다. (ApplyDice도 이 상태에서는 진행할 수 없다)
            preview.Kind = DiceMoveKind.Move;
            preview.Score = 0;
            return preview;
        }

        int to = (player.Position + dice1 + dice2) % tileCount;
        preview.Kind = DiceMoveKind.Move;
        preview.To = to;
        preview.ReceivesSalary = to < player.Position;
        preview.RollsAgain = isDouble && !islandLocked && !newbieThirdDouble; // 더블로 무인도를 탈출하거나 뉴비의 세 번째 더블이면 추가 턴이 없다
        preview.Score = TileScore(state, data, player, balance, to, false, out preview.Toll)
                      + (preview.ReceivesSalary ? SalaryScore(state, data, player) : 0)
                      + (preview.RollsAgain ? RollAgainScore : 0);
        return preview;
    }

    // 무인도 칸에서 영업정지 중인가. (GameRules.ApplyDice의 onIslandTile && islandTurnsRemaining)
    private static bool IsIslandLocked(GameData data, PlayerState player)
    {
        TileData currentTile = data.GetTile(player.Position);
        return currentTile != null && currentTile.Type == TileType.ISLAND && player.IslandTurnsRemaining > 0;
    }

    /// <summary>
    /// 두 결과를 총점(RollPreview.Score)으로 비교한다. a가 좋으면 양수, b가 좋으면 음수, 같으면 0. (v1.3.2 점수제)
    /// 총점 = 도착 칸 점수(TileScore) + 월급(SalaryScore) + 한 번 더 굴림(RollAgainScore)
    /// </summary>
    public static int CompareRoll(RollPreview a, RollPreview b)
    {
        return a.Score.CompareTo(b.Score);
    }

    /// <summary>좋은 쪽을 고른다. 같으면 first (먼저 굴린 쪽). 꼴찌 보너스에서 쓴다.</summary>
    public static RollPreview PickBetter(RollPreview first, RollPreview second)
    {
        return CompareRoll(second, first) > 0 ? second : first;
    }

    /// <summary>나쁜 쪽을 고른다. 같으면 first (먼저 굴린 쪽). 1등 패널티에서 쓴다.</summary>
    public static RollPreview PickWorse(RollPreview first, RollPreview second)
    {
        return CompareRoll(second, first) < 0 ? second : first;
    }

    // ───────────── 1등 패널티 ─────────────

    // 무인도 탈출 굴림에서 둘 다 더블이라 다시 굴리는 최대 횟수. (둘 다 더블일 확률은 1/36이라 거의 닿지 않지만 무한히 돌지 않게 막는다)
    private const int IslandRerollLimit = 10;

    /// <summary>
    /// 1등 패널티: 주사위를 두 번 굴려 나쁜 결과(PickWorse)를 고른다. rollPair는 주사위 두 개를 굴리는 호스트의 난수다. (RollPair)
    /// 무인도 영업정지 중이면 결과 비교 대신 더블이 아닌 쪽(탈출 실패)을 쓰고, 둘 다 더블이면 다시 두 번 굴린다.
    /// (결과 비교로는 탈출한 더블이 남의 땅에 서면 탈출 실패와 같은 나쁨이 되어, 탈출이 골라질 수 있기 때문이다)
    /// 무인도 탈출권은 굴리기 전에 이미 써서 영업정지가 풀린 상태로 부르므로, 탈출권을 쓰면 평소처럼 결과를 비교한다.
    /// 두 후보와 고른 이유를 같이 돌려준다. 쓰는 주사위는 Picked다. (v1.3.2)
    /// </summary>
    public static RollChoice RollForLeader(GameState state, GameData data, PlayerState player, RankBalance balance, Func<(int, int)> rollPair)
    {
        RollPreview first = Preview(state, data, player, balance, rollPair());
        RollPreview second = Preview(state, data, player, balance, rollPair());

        if (!IsIslandLocked(data, player)) return Choose(first, second, PickWorse(first, second));

        int rerolls = 0;
        for (; rerolls < IslandRerollLimit && first.IsDouble && second.IsDouble; rerolls++)
        {
            first = Preview(state, data, player, balance, rollPair());
            second = Preview(state, data, player, balance, rollPair());
        }

        var choice = new RollChoice { First = first, Second = second, IslandLocked = true, Rerolls = rerolls };
        if (!first.IsDouble && !second.IsDouble) { choice.Picked = first; choice.Reason = RollPickReason.IslandBothFail; }
        else if (first.IsDouble && second.IsDouble) { choice.Picked = second; choice.Reason = RollPickReason.IslandRerollLimit; }
        else { choice.Picked = !first.IsDouble ? first : second; choice.Reason = RollPickReason.IslandFail; }
        return choice;
    }

    // ───────────── 꼴찌 보너스 ─────────────

    /// <summary>
    /// 꼴찌 보너스: 주사위를 두 번 굴려 좋은 결과(PickBetter)를 고른다. rollPair는 주사위 두 개를 굴리는 호스트의 난수다. (RollPair)
    /// 결과가 같으면 더블을 고르므로 더블 확률도 오른다. (한 번 굴림 약 17% → 약 31%. 뉴비의 더블 확률 증가는 따로 겹친다)
    /// 무인도 영업정지 중이면 1등 패널티와 반대로 더블인 쪽(탈출)을 쓴다. 둘 다 더블이 아니면 비교할 것 없이 먼저 굴린 쪽(탈출 실패)이다.
    /// 두 후보와 고른 이유를 같이 돌려준다. 쓰는 주사위는 Picked다. (v1.3.2)
    /// </summary>
    public static RollChoice RollForLast(GameState state, GameData data, PlayerState player, RankBalance balance, Func<(int, int)> rollPair)
    {
        RollPreview first = Preview(state, data, player, balance, rollPair());
        RollPreview second = Preview(state, data, player, balance, rollPair());

        if (!IsIslandLocked(data, player)) return Choose(first, second, PickBetter(first, second));

        RollChoice choice;
        if (first.IsDouble && second.IsDouble) choice = Choose(first, second, PickBetter(first, second)); // 둘 다 탈출이면 도착이 좋은 쪽
        else if (first.IsDouble || second.IsDouble) choice = new RollChoice { First = first, Second = second, Picked = second.IsDouble ? second : first, Reason = RollPickReason.IslandEscape };
        else choice = new RollChoice { First = first, Second = second, Picked = first, Reason = RollPickReason.IslandBothFail };
        choice.IslandLocked = true;
        return choice;
    }

    // 결과를 비교해 고른 경우의 이유를 채운다. 결과가 같으면 PickBetter, PickWorse가 먼저 굴린 쪽을 고른다
    private static RollChoice Choose(RollPreview first, RollPreview second, RollPreview picked)
    {
        return new RollChoice
        {
            First = first,
            Second = second,
            Picked = picked,
            Reason = CompareRoll(first, second) == 0 ? RollPickReason.SameFirst : RollPickReason.Compared,
        };
    }

    private static RollPreview Preview(GameState state, GameData data, PlayerState player, RankBalance balance, (int dice1, int dice2) dice)
    {
        return PreviewRoll(state, data, player, balance, dice.dice1, dice.dice2);
    }

    // ───────────── 뉴비 주사위 ─────────────

    /// <summary>뉴비 옵션의 한 번 굴림 더블 확률. (보통은 1/6, 약 17%)</summary>
    public const double NewbieDoubleChance = 0.25;

    /// <summary>
    /// 주사위 두 개를 굴린다. newbie면 더블이 NewbieDoubleChance 확률로 나온다. (뉴비 옵션, 1등이면 1등 패널티가 먼저라 newbie=false로 부른다)
    /// 더블이 아닐 때는 첫 주사위를 뺀 다섯 눈 중에서 둘째를 골라, 더블이 아닌 조합끼리는 같은 확률이 되게 한다.
    /// </summary>
    public static (int, int) RollPair(Random random, bool newbie)
    {
        if (!newbie) return (random.Next(1, 7), random.Next(1, 7));

        int first = random.Next(1, 7);
        if (random.NextDouble() < NewbieDoubleChance) return (first, first);

        int second = random.Next(1, 6);  // 1~5
        if (second >= first) second++;   // first를 건너뛴다
        return (first, second);
    }

    // ───────────── 황금열쇠 ─────────────

    /// <summary>
    /// 카드 한 장의 점수 (v1.3.2 점수제). from은 카드를 뽑는 칸(황금열쇠 칸)이다. 이동 카드는 그 칸에서 옮겨 갈 칸의 점수로 본다.
    /// 카드 에셋마다 적지 않고 효과 종류와 금액으로 정해서, 새 카드를 넣어도 따로 점수를 적지 않아도 되게 한다.
    /// 상황에 따라 바뀐다: 같은 종류의 보관형을 이미 들고 있으면(버려짐) 0, 내 땅이 없으면 통행료 2배와 인수 방지권은 0.
    /// </summary>
    public static int CardScore(GameState state, GameData data, PlayerState player, CardData card, int from)
    {
        if (card == null) return 0;
        if (CardRules.IsHeldType(card.effectType) && CardRules.FindHeldCard(data, player, card.effectType) != null) return 0;

        bool ownsLand = state.PropertyStates.Exists(p => p.OwnerId == player.PlayerId);
        switch (card.effectType)
        {
            case CardEffectType.Bonus: return Points(card.amount);
            case CardEffectType.Penalty: return -Points(card.amount);
            case CardEffectType.GoToIsland: return IslandScore;
            case CardEffectType.CollectFromAll: return CardRules.OtherActivePlayers(state, player).Count * Points(card.amount);
            case CardEffectType.PayToAll: return -CardRules.OtherActivePlayers(state, player).Count * Points(card.amount);
            case CardEffectType.RepairCost: return -CardRules.CountBuildings(state, player.PlayerId) * Points(card.amount);
            case CardEffectType.SwapPosition: return 0; // 상대가 무작위라 미리 알 수 없다
            case CardEffectType.TollDouble: return ownsLand ? TollDoubleScore : 0;

            case CardEffectType.MoveTo: return MoveToScore(state, data, player, from, card.targetTileType);
            case CardEffectType.MoveBy: return MoveByScore(state, data, player, from, card.steps);

            case CardEffectType.TollFree: return TollFreeScore;
            case CardEffectType.TollHalf: return TollHalfScore;
            case CardEffectType.IslandEscape: return IslandEscapeScore;
            case CardEffectType.TakeoverShield: return ownsLand ? TakeoverShieldScore : 0;
            case CardEffectType.BuildDiscount: return BuildDiscountScore;
            case CardEffectType.SalaryDouble: return Points(state.Config != null ? state.Config.salaryAmount : 0);

            default: return 0;
        }
    }

    // MoveTo 카드: from 다음부터 한 바퀴를 돌며 처음 나오는 그 종류의 칸. 출발 칸을 지나거나 도착하면 월급 (CardRules.MoveToTileType과 같은 계산)
    private static int MoveToScore(GameState state, GameData data, PlayerState player, int from, TileType type)
    {
        int boardSize = data.TileCount;
        for (int step = 1; step <= boardSize; step++)
        {
            int to = (from + step) % boardSize;
            TileData tile = data.GetTile(to);
            if (tile == null || tile.Type != type) continue;

            bool salary = from + step >= boardSize;
            return TileScore(state, data, player, null, to, true, out _) + (salary ? SalaryScore(state, data, player) : 0);
        }
        return 0; // 그 종류의 칸이 없으면 효과 없이 끝난다
    }

    // MoveBy 카드: 앞으로 가면 출발 칸을 지날 때 월급, 뒤로 가면 순간이동이라 월급 없음 (CardRules.MoveBy와 같은 계산)
    private static int MoveByScore(GameState state, GameData data, PlayerState player, int from, int steps)
    {
        int boardSize = data.TileCount;
        if (boardSize <= 0) return 0;

        int to = ((from + steps) % boardSize + boardSize) % boardSize;
        bool salary = steps > 0 && from + steps >= boardSize;
        return TileScore(state, data, player, null, to, true, out _) + (salary ? SalaryScore(state, data, player) : 0);
    }

    /// <summary>
    /// from 칸(황금열쇠 칸)에서 뽑을 카드의 기대 점수. 이 사람에게 적용되는 뽑기 가중치(CardWeights)로 평균을 낸다.
    /// 주사위 결과 판정에서 황금열쇠 칸의 점수로 쓴다.
    /// </summary>
    public static int ExpectedCardScore(GameState state, GameData data, RankBalance balance, PlayerState player, int from)
    {
        IReadOnlyList<int> cardIds = data.CardIds;
        if (cardIds == null || cardIds.Count == 0) return 0;

        int[] scores = CardScores(state, data, player, from);
        int[] weights = CardWeights(data, balance, player, scores);
        long weighted = 0, total = 0;
        for (int i = 0; i < cardIds.Count; i++)
        {
            weighted += (long)weights[i] * scores[i];
            total += weights[i];
        }
        return total > 0 ? (int)Math.Round((double)weighted / total) : 0;
    }

    /// <summary>무인도로 보내는 카드인가. 꼴찌에게는 나오지 않는다.</summary>
    public static bool SendsToIsland(CardData card)
    {
        return card.effectType == CardEffectType.GoToIsland
            || (card.effectType == CardEffectType.MoveTo && card.targetTileType == TileType.ISLAND);
    }

    /// <summary>
    /// 황금열쇠를 뽑는다. 순위 보정이 꺼져 있거나 1등, 꼴찌가 아니면 모든 카드가 같은 확률이다. (전과 같음)
    /// 1등은 점수가 낮은 카드가, 꼴찌와 뉴비는 점수가 높은 카드가 잘 나오고 꼴찌와 뉴비에게는 무인도 카드가 빠진다. (CardWeights)
    /// 점수는 뽑는 사람의 지금 칸(황금열쇠 칸) 기준이다. (CardScore)
    /// 뉴비가 1등이면 1등 패널티가 먼저라 뉴비 혜택을 받지 않는다. 꼴찌이면서 뉴비여도 혜택은 한 번만 받는다.
    /// randomBelow(n)은 0 이상 n 미만의 수를 주는 호스트의 난수다. 덱이 비어 있으면 0.
    /// </summary>
    public static int PickCardId(GameState state, GameData data, RankBalance balance, PlayerState player, Func<int, int> randomBelow)
    {
        IReadOnlyList<int> cardIds = data.CardIds;
        if (cardIds == null || cardIds.Count == 0) return 0;

        int[] weights = CardWeights(data, balance, player, CardScores(state, data, player, player.Position));
        int total = 0;
        foreach (int weight in weights) total += weight;

        // 모든 카드가 빠진 덱(무인도 카드만 있음 등)이면 평소처럼 고른다
        if (total <= 0) return cardIds[randomBelow(cardIds.Count)];

        int roll = randomBelow(total);
        for (int i = 0; i < cardIds.Count; i++)
        {
            if (roll < weights[i]) return cardIds[i];
            roll -= weights[i];
        }
        return cardIds[cardIds.Count - 1];
    }

    // 덱의 카드마다 점수 (data.CardIds 순서)
    private static int[] CardScores(GameState state, GameData data, PlayerState player, int from)
    {
        IReadOnlyList<int> cardIds = data.CardIds;
        var scores = new int[cardIds.Count];
        for (int i = 0; i < cardIds.Count; i++) scores[i] = CardScore(state, data, player, data.FindCard(cardIds[i]), from);
        return scores;
    }

    // 덱의 카드마다 뽑기 가중치 (data.CardIds 순서). 0이면 나오지 않는다.
    // 1등은 점수가 낮을수록, 꼴찌와 뉴비(isHelped)는 높을수록 가중치가 크다. 그 밖의 사람은 모두 같다.
    private static int[] CardWeights(GameData data, RankBalance balance, PlayerState player, int[] scores)
    {
        bool isLeader = balance != null && balance.IsLeader(player.PlayerId);
        bool isHelped = !isLeader && ((balance != null && balance.IsLast(player.PlayerId)) || player.IsNewbie);

        IReadOnlyList<int> cardIds = data.CardIds;
        var weights = new int[cardIds.Count];
        for (int i = 0; i < cardIds.Count; i++)
        {
            CardData card = data.FindCard(cardIds[i]);
            if (card != null && isHelped && SendsToIsland(card)) weights[i] = 0;
            else if (card != null && isLeader) weights[i] = Math.Max(CardMinWeight, CardBaseWeight - scores[i] / 2);
            else if (card != null && isHelped) weights[i] = Math.Max(CardMinWeight, CardBaseWeight + scores[i] / 2);
            else weights[i] = CardBaseWeight;
        }
        return weights;
    }

    // ───────────── 뉴비 제한 ─────────────

    /// <summary>
    /// 뉴비 제한: 매각과 세계여행 목적지는 뉴비가 고르지 않고 호스트가 고른다. 지금 입력 단계가 그런 단계인가.
    /// 호스트는 이 단계에서 뉴비를 봇처럼 기다렸다가 대신 진행하고, AFK로 표시하지 않는다. 1등이어도 제한은 그대로다.
    /// </summary>
    public static bool IsNewbieAutoPhase(PlayerState player, GameMessageType? waitingType)
    {
        if (player == null || !player.IsNewbie || player.IsBot) return false;
        return waitingType == GameMessageType.PROPERTIES_SOLD || waitingType == GameMessageType.DESTINATION_CHOSEN;
    }

    /// <summary>
    /// 도착 칸의 점수 (v1.3.2 점수제). 남의 땅이면 통행료도 알려 준다. (ArrivalRules.ArriveAtTile, TradeRules.ArriveAtProperty와 같은 칸 구분)
    /// nested면 황금열쇠 카드로 옮겨 간 칸이라, 그 칸이 또 황금열쇠여도 기대 점수를 다시 계산하지 않는다. (balance는 nested가 아닐 때만 쓴다)
    /// </summary>
    public static int TileScore(GameState state, GameData data, PlayerState player, RankBalance balance, int position, bool nested, out long toll)
    {
        toll = 0;
        TileData tile = data.GetTile(position);
        if (tile == null) return 0;

        switch (tile.Type)
        {
            case TileType.ISLAND: return IslandScore;
            case TileType.DONATION: return -Points(state.Config != null ? state.Config.taxAmount : 0);
            case TileType.CHARITY: return Points(state.WelfareFund); // 쌓인 적립금 (없으면 0)
            case TileType.WORLD_TRAVEL: return WorldTravelScore;
            case TileType.GOLDEN_KEY: return nested ? NestedGoldenKeyScore : ExpectedCardScore(state, data, balance, player, position);

            case TileType.PROPERTY:
                PropertyState property = state.PropertyStates.Find(p => p.PropertyId == tile.PropertyId);
                if (property == null) return 0;
                if (!property.OwnerId.HasValue) // 빈 땅: 살 돈이 있어야 의미가 있다
                    return TradeRules.CheckPurchase(data, player, property, true) == TradeError.None ? BuyableLandScore : 0;
                if (property.OwnerId.Value == player.PlayerId) // 내 땅: 지을 수 있어야 의미가 있다 (건설 할인권을 들고 있으면 할인한 값으로 본다)
                    return TradeRules.CheckBuild(data, player, property, true, useDiscount: true) == TradeError.None ? BuildableLandScore : 0;

                toll = data.Properties.GetToll(property, state.PropertyStates);
                return -Points(toll);

            default:
                return 0; // 출발 칸에 멈춤
        }
    }

    // 월급의 점수. 월급 2배 카드를 들고 있으면 2배로 본다 (GameRules의 월급과 같다)
    private static int SalaryScore(GameState state, GameData data, PlayerState player)
    {
        long salary = state.Config != null ? state.Config.salaryAmount : 0;
        if (CardRules.FindHeldCard(data, player, CardEffectType.SalaryDouble) != null) salary *= 2;
        return Points(salary);
    }

    // 돈을 점수로. 1점 = PointWon원. 남는 금액은 올려서, 2,000원 통행료처럼 작은 금액도 0점(아무 일 없음)이 되지 않게 한다
    private static int Points(long won) => won <= 0 ? 0 : (int)((won + PointWon - 1) / PointWon);
}