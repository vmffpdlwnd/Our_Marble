using System;
using System.Collections.Generic;

/// <summary>
/// 기본 봇 전략입니다. 판단 기준값은 BotSettings에서 읽으므로, 성향마다 같은 로직을 다른 값으로 씁니다.
/// 돈을 쓰고 난 뒤 다음 턴에 통행료를 현금으로 못 낼 위험이 크면 구매·건설·인수를 하지 않고,
/// 땅은 수익 비율이 좋은 땅 위주로 삽니다.
/// 매각할 때는 상대가 밟을 확률까지 고려해 기대 통행료 손실이 적은 땅부터 팝니다.
/// 건설·인수는 늘어나는 통행료로 비용을 회수하는 기간(라운드)을 따져 결정합니다.
/// 돈을 쓰면 다음 턴에 만날 더 좋은 땅을 못 사게 될 확률이 크면 이번에는 아낍니다.
/// 막판에는 구매·건설을 적극적으로 하고, 남은 라운드 안에 회수 못 할 인수는 하지 않습니다.
/// 공격형(BotSettings.TargetsLast)은 자기와 뉴비를 뺀 살아 있는 사람과 봇 중 꼴찌를 목표로 정하고, 목표가 다음 턴에 밟을 수 있는 땅을 먼저 노립니다.
/// </summary>
public class BasicBotStrategy : IBotStrategy
{
    // 성향과 무관한 게임 규칙 값
    private const int DefaultTravelDestination = 16;       // 기부금수령 칸 (갈 곳이 없을 때 기본 목적지)
    private const double AverageHitRate = 1.0 / 7;         // 주사위 평균 7칸 → 한 번 굴릴 때 특정 칸에 도착할 평균 확률
    private const int TargetTravelTier = 0;                // 공격형: 목표의 길목에 있는 세계여행 목적지 순위 (가장 먼저)
    private const double TargetKeepFactor = 3;             // 공격형: 매각할 때 목표의 길목에 있는 땅을 지키는 정도 (기대 손실에 곱함)

    private readonly TileData[] tileByIndex;                  // 칸 번호 → 칸 정보
    private readonly Func<PropertyState, long> getToll;       // 통행료 계산
    private readonly Func<int, PropertyData> getPropertyData; // 가격표 조회 (땅값, 건설비, 통행료, 건설 가능 여부)
    private readonly BotSettings settings;                    // 성향별 판단 기준값
    private readonly int maxRound;                            // 최대 라운드 (0이면 제한 없음)
    private readonly Action<string> log;                      // 판단 근거 로그 (없으면 출력 안 함)
    private readonly Func<GameState, List<PlayerState>> rankPlayers; // 순위 순서의 플레이어 목록 (공격형의 목표 찾기, 없으면 노리지 않음)

    public BasicBotStrategy(IReadOnlyList<TileData> tiles,
                            Func<PropertyState, long> getToll,
                            Func<int, PropertyData> getPropertyData,
                            BotSettings settings = null,
                            int maxRound = 0,
                            Action<string> log = null,
                            Func<GameState, List<PlayerState>> rankPlayers = null)
    {
        // 리스트 순서와 칸 번호가 달라도 안전하도록 Index 기준으로 다시 배치합니다.
        tileByIndex = new TileData[tiles.Count];
        foreach (TileData tile in tiles)
        {
            if (tile != null && tile.Index >= 0 && tile.Index < tileByIndex.Length)
                tileByIndex[tile.Index] = tile;
        }

        this.getToll = getToll;
        this.getPropertyData = getPropertyData;
        this.settings = settings ?? BotSettings.Balanced();
        this.maxRound = maxRound;
        this.log = log;
        this.rankPlayers = rankPlayers;
    }

    #region 판단 (IBotStrategy)

    public bool ShouldPurchase(GameState state, long botId, int propertyId, long price)
    {
        // 1) 통행료 위험이 크면 어떤 땅이든 사지 않습니다.
        if (!CanAffordSafely(state, botId, price)) return false;

        PropertyData data = getPropertyData(propertyId);
        PlayerState bot = state.PlayerStates.Find(p => p.PlayerId == botId);
        if (data == null || bot == null) return true;

        // 공격형: 목표가 다음 턴에 밟을 수 있는 땅이면 가치와 기회비용을 따지지 않고 삽니다. (통행료 위험은 위에서 이미 봄)
        PlayerState target = FindTarget(state, botId);
        if (IsOnTargetPath(target, propertyId))
        {
            Log(botId, $"{data.CityName} 목표 {target.Nickname}의 길목 (다음 턴 도착 {TargetHitChance(target, propertyId):P1}) → 구매");
            return true;
        }

        // 2) 좋은 땅은 사고, 보통 땅은 현금이 넉넉히 남을 때만 삽니다.
        double landReturn = GetMaxTollReturn(data);
        long cashAfter = bot.Money - price;
        bool isGoodLand = landReturn >= settings.GoodLandReturn;
        bool buy = IsWorthBuying(state, data, cashAfter);

        // 3) 사면 다음 턴에 만날 더 좋은 땅을 못 사게 될 확률이 크면 아낍니다.
        double missed = buy ? MissedBetterLandChance(state, bot, price, landReturn + settings.BetterLandMargin) : 0;
        if (missed > settings.MaxMissedChance) buy = false;

        Log(botId, $"{data.CityName} 수익 비율 {landReturn:F2}" +
                   $"({(isGoodLand ? "좋은 땅" : "보통 땅")}), 남는 현금 {cashAfter:N0}, " +
                   $"더 좋은 땅 놓칠 확률 {missed:P1} → {(buy ? "구매" : "포기")}");
        return buy;
    }

    public bool ShouldBuild(GameState state, long botId, int propertyId, long cost)
    {
        // 1) 통행료 위험이 크면 짓지 않습니다.
        if (!CanAffordSafely(state, botId, cost)) return false;

        PropertyState property = state.PropertyStates.Find(p => p.PropertyId == propertyId);
        PropertyData data = getPropertyData(propertyId);
        PlayerState bot = state.PlayerStates.Find(p => p.PlayerId == botId);
        if (property == null || data == null || bot == null) return true;
        if (!data.CanBuild || property.BuildingLevel >= BuildingLevel.Hotel) return false;

        // 공격형: 목표가 다음 턴에 밟을 수 있는 땅이면 회수 기간과 기회비용을 따지지 않고 짓습니다.
        PlayerState target = FindTarget(state, botId);
        if (IsOnTargetPath(target, propertyId))
        {
            Log(botId, $"{data.CityName} 목표 {target.Nickname}의 길목 (다음 턴 도착 {TargetHitChance(target, propertyId):P1}) → 건설");
            return true;
        }

        // 2) 통행료 상승분으로 건설비를 빨리 회수할 수 있거나, 현금이 넉넉하면 짓습니다.
        BuildingLevel next = property.BuildingLevel + 1;
        long tollGain = data.GetToll(next) - data.GetToll(property.BuildingLevel);
        double payback = PaybackRounds(state, botId, propertyId, cost, tollGain);
        long cashAfter = bot.Money - cost;
        bool build = payback <= settings.MaxPaybackRounds || cashAfter >= settings.SafeCashReserve || IsEndGame(state);

        // 3) 지으면 다음 턴에 만날 좋은 땅을 못 사게 될 확률이 크면 아낍니다.
        double missed = build ? MissedBetterLandChance(state, bot, cost, settings.GoodLandReturn) : 0;
        if (missed > settings.MaxMissedChance) build = false;

        Log(botId, $"{data.CityName} {next} 건설, 통행료 +{tollGain:N0}, " +
                   $"회수 {FormatPayback(payback)}, 남는 현금 {cashAfter:N0}, " +
                   $"좋은 땅 놓칠 확률 {missed:P1} → {(build ? "건설" : "포기")}");
        return build;
    }

    public bool ShouldAcquire(GameState state, long botId, int propertyId, long price)
    {
        // 1) 통행료 위험이 크면 인수하지 않습니다.
        if (!CanAffordSafely(state, botId, price)) return false;

        PropertyState property = state.PropertyStates.Find(p => p.PropertyId == propertyId);
        PropertyData data = getPropertyData(propertyId);
        PlayerState bot = state.PlayerStates.Find(p => p.PlayerId == botId);
        if (property == null || data == null || bot == null) return true;

        // 공격형: 목표가 다음 턴에 밟을 수 있는 땅이면 회수 기간과 기회비용을 따지지 않고 인수합니다.
        PlayerState target = FindTarget(state, botId);
        if (IsOnTargetPath(target, propertyId))
        {
            Log(botId, $"{data.CityName} 목표 {target.Nickname}의 길목 (다음 턴 도착 {TargetHitChance(target, propertyId):P1}) → 인수");
            return true;
        }

        // 2) 인수가가 비싸므로, 통행료 수입으로 빨리 회수할 수 있는 땅만 인수합니다.
        long toll = getToll(property);
        double payback = PaybackRounds(state, botId, propertyId, price, toll);
        bool acquire = payback <= Math.Min(settings.MaxPaybackRounds, RemainingRounds(state));

        // 3) 인수하면 다음 턴에 만날 좋은 땅을 못 사게 될 확률이 크면 아낍니다.
        double missed = acquire ? MissedBetterLandChance(state, bot, price, settings.GoodLandReturn) : 0;
        if (missed > settings.MaxMissedChance) acquire = false;

        Log(botId, $"{data.CityName} 인수가 {price:N0}, 통행료 {toll:N0}, " +
                   $"회수 {FormatPayback(payback)}, 좋은 땅 놓칠 확률 {missed:P1} → {(acquire ? "인수" : "포기")}");
        return acquire;
    }

    /// <summary>
    /// 매각가 1원당 잃는 기대 통행료가 적은 땅부터 담아 부족분을 채웁니다.
    /// 그 뒤 지킬 가치가 큰 땅부터, 빼도 부족분이 채워지면 다시 빼서 수익이 좋은 땅을 최대한 지킵니다.
    /// </summary>
    public List<int> ChooseSellProperties(GameState state, long botId, long requiredAmount,
                                          Func<PropertyState, long> getSellValue)
    {
        long shortage = requiredAmount - GetMoney(state, botId);

        // 공격형은 목표의 길목에 있는 땅을 지킬 가치가 크다고 보고 마지막까지 남깁니다.
        PlayerState target = FindTarget(state, botId);
        Func<PropertyState, double> lossPerWon = p => TollLossPerWon(state, botId, p, getSellValue)
                                                       * (IsOnTargetPath(target, p.PropertyId) ? TargetKeepFactor : 1);

        List<PropertyState> owned = state.PropertyStates.FindAll(p => p.OwnerId == botId);
        owned.Sort((a, b) => lossPerWon(a).CompareTo(lossPerWon(b)));

        List<PropertyState> selected = new List<PropertyState>();
        long sum = 0;
        foreach (PropertyState p in owned)
        {
            if (sum >= shortage) break;
            selected.Add(p);
            sum += getSellValue(p);
        }

        // 지킬 가치가 큰 땅(기대 통행료 손실이 큰 땅)부터, 빼도 부족분을 채운다면 매각 목록에서 제외합니다.
        selected.Sort((a, b) => lossPerWon(b).CompareTo(lossPerWon(a)));
        for (int i = 0; i < selected.Count;)
        {
            long value = getSellValue(selected[i]);
            if (sum - value >= shortage)
            {
                sum -= value;
                selected.RemoveAt(i);
            }
            else
            {
                i++;
            }
        }

        Log(botId, $"부족분 {shortage:N0} → 매각 {selected.Count}개 (매각가 합 {sum:N0})");
        return selected.ConvertAll(p => p.PropertyId);
    }

    /// <summary>
    /// 보드의 모든 칸을 평가해서 우선순위(tier)가 가장 높고, 같은 순위에서는 점수가 가장 높은 칸으로 갑니다.
    /// 갈 만한 칸이 없으면 기본 목적지로 갑니다.
    /// </summary>
    public int ChooseTravelDestination(GameState state, long botId)
    {
        PlayerState bot = state.PlayerStates.Find(p => p.PlayerId == botId);
        if (bot == null || tileByIndex.Length == 0) return DefaultTravelDestination;

        int bestPosition = -1;
        int bestTier = int.MaxValue;
        double bestScore = double.MinValue;
        PlayerState target = FindTarget(state, botId);

        for (int position = 0; position < tileByIndex.Length; position++)
        {
            if (position == bot.Position) continue;
            if (!TryScoreDestination(state, bot, position, out int tier, out double score)) continue;

            // 공격형: 살 만한 빈 땅이나 지을 내 땅이 목표의 길목에 있으면 가장 먼저 갑니다. (같은 순위에서는 목표가 밟을 확률로 비교)
            if ((tier == 2 || tier == 3) && TargetHitChanceAt(target, position) > 0)
            {
                tier = TargetTravelTier;
                score = TargetHitChanceAt(target, position);
            }

            if (tier < bestTier || (tier == bestTier && score > bestScore))
            {
                bestPosition = position;
                bestTier = tier;
                bestScore = score;
            }
        }

        if (bestPosition < 0) bestPosition = DefaultTravelDestination;
        Log(botId, $"세계여행 목적지 {bestPosition}번 (순위 {bestTier}, 점수 {bestScore:F2})");
        return bestPosition;
    }

    /// <summary>
    /// 공격형: 목표가 다음 턴에 밟을 확률 × 통행료가 가장 큰 내 땅을 고릅니다. 공격형이 아니거나 그런 땅이 없으면 0. (호출하는 쪽이 평소 규칙으로 고름)
    /// </summary>
    public int ChooseTollDoubleProperty(GameState state, long botId)
    {
        PlayerState target = FindTarget(state, botId);
        if (target == null) return 0;

        int best = 0;
        double bestValue = 0;
        foreach (PropertyState property in state.PropertyStates)
        {
            if (property.OwnerId != botId) continue;
            double value = getToll(property) * TargetHitChance(target, property.PropertyId);
            if (value > bestValue) { best = property.PropertyId; bestValue = value; }
        }

        if (best != 0) Log(botId, $"통행료 2배: 목표 {target.Nickname}의 길목 땅 {best}번 (기대 통행료 {bestValue:N0})");
        return best;
    }

    #endregion

    #region 공격형 (목표)

    /// <summary>
    /// 공격형의 목표: 자기와 뉴비를 뺀, 살아 있는 사람과 봇 중 꼴찌. 공격형이 아니거나, 너프 중이거나(앞선 봇은 노리기를 멈춤), 노릴 사람이 없으면 null.
    /// 봇도 목표가 됩니다(공격형끼리도). 꼴찌가 봇이면 사람은 공격받지 않아서, "사람을 괴롭히는 AI"가 아니라 "꼴찌를 무너뜨리는 AI"가 됩니다.
    /// 자기가 꼴찌면 자기를 빼므로 바로 위 한 명을 노려 위로 올라갑니다. 뉴비 옵션을 켠 사람은 목표에서 빼고, 그다음으로 낮은 사람을 노립니다.
    /// UIDebugPanel.FindTarget(F1 목표 표시)과 같은 기준입니다. 바꾸면 같이 고칩니다.
    /// </summary>
    private PlayerState FindTarget(GameState state, long botId)
    {
        if (!settings.TargetsLast || settings.IsNerfed || rankPlayers == null) return null;

        List<PlayerState> ranked = rankPlayers(state);
        for (int i = ranked.Count - 1; i >= 0; i--)
        {
            PlayerState player = ranked[i];
            if (player.IsBankrupt || player.IsNewbie || player.PlayerId == botId) continue; // 뉴비 옵션을 켠 사람은 보호하고, 자기는 노리지 않는다
            return player;
        }
        return null;
    }

    /// <summary>목표가 다음 주사위 한 번으로 position 칸에 도착할 확률. 목표가 없으면 0.</summary>
    private double TargetHitChanceAt(PlayerState target, int position)
    {
        if (target == null || position < 0 || tileByIndex.Length == 0) return 0;

        int distance = (position - target.Position + tileByIndex.Length) % tileByIndex.Length;
        return NextRollHitProbability(target, distance);
    }

    /// <summary>목표가 다음 주사위 한 번으로 이 땅에 도착할 확률. 목표가 없으면 0.</summary>
    private double TargetHitChance(PlayerState target, int propertyId)
    {
        return TargetHitChanceAt(target, PositionOf(propertyId));
    }

    /// <summary>목표가 다음 턴에 밟을 수 있는 땅(앞쪽 2~12칸)인가.</summary>
    private bool IsOnTargetPath(PlayerState target, int propertyId)
    {
        return TargetHitChance(target, propertyId) > 0;
    }

    #endregion

    #region 통행료 위험 (내가 낼 돈)

    /// <summary>
    /// cost를 낼 현금이 있고, 낸 뒤 다음 턴에 통행료를 현금으로 못 낼 확률이 기준 이하인지 확인합니다.
    /// </summary>
    private bool CanAffordSafely(GameState state, long botId, long cost)
    {
        PlayerState bot = state.PlayerStates.Find(p => p.PlayerId == botId);
        if (bot == null || bot.Money < cost) return false;

        long cashAfter = bot.Money - cost;
        double risk = CashShortProbability(state, botId, bot.Position, cashAfter);
        bool safe = risk <= settings.MaxCashShortProbability;

        Log(botId, $"비용 {cost:N0}, 남는 현금 {cashAfter:N0}, " +
                   $"다음 턴 현금 부족 확률 {risk:P1} → {(safe ? "진행" : "포기")}");
        return safe;
    }

    /// <summary>
    /// position 칸에서 cost를 쓰고 난 뒤, 다음 턴 현금 부족 확률이 기준 이하인지 확인합니다. (로그 없음)
    /// </summary>
    private bool IsSafeToSpendAt(GameState state, PlayerState bot, int position, long cost)
    {
        if (bot.Money < cost) return false;
        return CashShortProbability(state, bot.PlayerId, position, bot.Money - cost) <= settings.MaxCashShortProbability;
    }

    /// <summary>
    /// 다음 주사위(합 2~12)로 도착할 칸 중, 통행료가 cash보다 비싼 남의 땅에 도착할 확률입니다.
    /// </summary>
    private double CashShortProbability(GameState state, long botId, int position, long cash)
    {
        if (tileByIndex.Length == 0) return 0;

        double probability = 0;
        for (int sum = 2; sum <= 12; sum++)
        {
            int target = (position + sum) % tileByIndex.Length;
            if (GetOpponentToll(state, botId, target) > cash)
                probability += DiceSumProbability(sum);
        }
        return probability;
    }

    /// <summary>
    /// position 칸이 다른 플레이어 소유 땅이면 통행료를, 아니면 0을 돌려줍니다.
    /// </summary>
    private long GetOpponentToll(GameState state, long botId, int position)
    {
        TileData tile = tileByIndex[position];
        if (tile == null || tile.Type != TileType.PROPERTY) return 0;

        PropertyState property = state.PropertyStates.Find(p => p.PropertyId == tile.PropertyId);
        if (property == null || property.OwnerId == null || property.OwnerId == botId) return 0;

        return getToll(property);
    }

    #endregion

    #region 땅 가치 (구매·기회비용)

    /// <summary>
    /// 땅의 수익 비율 = 최대 통행료 ÷ 그 통행료까지 드는 총 투자금.
    /// 건설 가능한 땅은 호텔 기준, 건설 불가 땅은 땅 통행료 기준입니다.
    /// </summary>
    private static double GetMaxTollReturn(PropertyData data)
    {
        if (!data.CanBuild)
            return data.LandPrice > 0 ? (double)data.GetToll(BuildingLevel.Land) / data.LandPrice : 0;

        long invested = data.LandPrice
                        + data.GetBuildCost(BuildingLevel.Villa)
                        + data.GetBuildCost(BuildingLevel.Building)
                        + data.GetBuildCost(BuildingLevel.Hotel);
        return invested > 0 ? (double)data.GetToll(BuildingLevel.Hotel) / invested : 0;
    }

    /// <summary>
    /// 구매 규칙: 좋은 땅이면 사고, 보통 땅은 사고 나서 현금이 넉넉히 남을 때만 삽니다.
    /// 막판에는 땅값이 그대로 총자산에 잡혀 손해가 없으므로 보통 땅도 삽니다.
    /// </summary>
    private bool IsWorthBuying(GameState state, PropertyData data, long cashAfter)
    {
        return GetMaxTollReturn(data) >= settings.GoodLandReturn
               || cashAfter >= settings.SafeCashReserve
               || IsEndGame(state);
    }

    /// <summary>
    /// cost를 쓰면 다음 턴에 도착할 수 있는 더 좋은 빈 땅을 못 사게 되는 확률입니다.
    /// "더 좋은 땅"은 맵 위치(칸 번호)와 무관하게, 수익 비율(GetMaxTollReturn)이 minReturn 이상인 땅입니다.
    ///   - 구매: minReturn = 지금 땅의 수익 비율 + BetterLandMargin(0.2)
    ///   - 건설·인수: minReturn = GoodLandReturn(0.75)
    /// 지금 현금으로는 살 수 있는데, cost를 쓰고 나면 못 사게 되는 땅만 셉니다.
    /// </summary>
    private double MissedBetterLandChance(GameState state, PlayerState bot, long cost, double minReturn)
    {
        if (tileByIndex.Length == 0) return 0;

        long cashAfter = bot.Money - cost;
        double chance = 0;
        for (int sum = 2; sum <= 12; sum++)
        {
            int targetPosition = (bot.Position + sum) % tileByIndex.Length;
            TileData tile = tileByIndex[targetPosition];
            if (tile == null || tile.Type != TileType.PROPERTY) continue;

            PropertyState target = state.PropertyStates.Find(p => p.PropertyId == tile.PropertyId);
            PropertyData data = getPropertyData(tile.PropertyId);
            if (target == null || data == null || target.OwnerId != null) continue;
            if (GetMaxTollReturn(data) < minReturn) continue;

            if (bot.Money >= data.LandPrice && cashAfter < data.LandPrice)
                chance += DiceSumProbability(sum) * StillEmptyChance(state, bot.PlayerId, targetPosition);
        }
        return chance;
    }

    /// <summary>
    /// 내 다음 차례까지 position 칸의 빈 땅이 남아 있을 확률입니다.
    /// 상대가 먼저 밟으면 사 간다고 보고, 모든 상대가 다음 주사위로 그 칸을 안 밟을 확률을 곱합니다.
    /// </summary>
    private double StillEmptyChance(GameState state, long botId, int position)
    {
        double stillEmpty = 1;
        foreach (PlayerState opponent in state.PlayerStates)
        {
            if (opponent.PlayerId == botId || opponent.IsBankrupt) continue;

            int distance = (position - opponent.Position + tileByIndex.Length) % tileByIndex.Length;
            stillEmpty *= 1 - NextRollHitProbability(opponent, distance);
        }
        return stillEmpty;
    }

    #endregion

    #region 통행료 수입 (상대가 밟을 확률)

    /// <summary>
    /// 살아 있는 상대들이 이 땅에 도착할 확률의 합입니다. (장기 평균 + 다음 주사위 기준 실제 확률)
    /// </summary>
    private double OpponentHitRate(GameState state, long botId, int propertyId)
    {
        int tilePosition = PositionOf(propertyId);
        if (tilePosition < 0) return 0;

        double rate = 0;
        foreach (PlayerState opponent in state.PlayerStates)
        {
            if (opponent.PlayerId == botId || opponent.IsBankrupt) continue;

            int distance = (tilePosition - opponent.Position + tileByIndex.Length) % tileByIndex.Length;
            rate += AverageHitRate + NextRollHitProbability(opponent, distance);
        }
        return rate;
    }

    /// <summary>
    /// 상대가 다음 주사위 한 번으로 distance칸 앞에 도착할 확률입니다.
    /// 무인도에 갇힌 상대는 더블이 나와야만 움직이므로, 짝수 거리(2~12)만 각각 1/36입니다.
    /// </summary>
    private static double NextRollHitProbability(PlayerState opponent, int distance)
    {
        if (distance < 2 || distance > 12) return 0;

        if (opponent.IslandTurnsRemaining > 0)
            return distance % 2 == 0 ? 1.0 / 36 : 0;

        return DiceSumProbability(distance);
    }

    /// <summary>
    /// 투자 비용을 통행료로 회수하는 데 걸리는 라운드 수입니다. (건설·인수)
    /// 한 라운드 기대 수입 = 통행료 × 상대들이 한 라운드에 이 땅을 밟을 확률의 합
    /// </summary>
    private double PaybackRounds(GameState state, long botId, int propertyId, long cost, long toll)
    {
        double incomePerRound = toll * OpponentHitRate(state, botId, propertyId);
        return incomePerRound > 0 ? cost / incomePerRound : double.MaxValue;
    }

    /// <summary>
    /// 이 땅을 팔 때 매각가 1원당 잃는 기대 통행료입니다. 값이 작을수록 팔아도 손해가 적은 땅입니다. (매각)
    /// 기대 통행료 = 통행료 × (상대별 장기 평균 도착 확률 + 다음 턴 실제 도착 확률)
    /// </summary>
    private double TollLossPerWon(GameState state, long botId, PropertyState property,
                                  Func<PropertyState, long> getSellValue)
    {
        long sellValue = getSellValue(property);
        if (sellValue <= 0) return double.MaxValue; // 팔아도 돈이 안 되는 땅은 맨 뒤로

        double hitRate = OpponentHitRate(state, botId, property.PropertyId);
        return getToll(property) * hitRate / sellValue;
    }

    #endregion

    #region 세계여행 목적지 평가

    /// <summary>
    /// 목적지 후보 칸의 우선순위(tier, 작을수록 좋음)와 같은 순위 안에서 비교할 점수를 구합니다.
    /// 가면 손해인 칸은 false를 돌려 후보에서 뺍니다.
    /// </summary>
    private bool TryScoreDestination(GameState state, PlayerState bot, int position, out int tier, out double score)
    {
        tier = 5;
        score = 0;

        TileData tile = tileByIndex[position];
        if (tile == null) return false;

        switch (tile.Type)
        {
            case TileType.CHARITY:
                if (state.WelfareFund <= 0) return true;                       // 받을 게 없으면 중립
                tier = state.WelfareFund >= settings.WelfareFundWorthTrip ? 1 : 4;
                score = state.WelfareFund;
                return true;

            case TileType.GOLDEN_KEY:
            case TileType.START:
                return true;                                                    // 중립 (세계여행은 월급 없음)

            case TileType.PROPERTY:
                return TryScorePropertyDestination(state, bot, position, tile.PropertyId, out tier, out score);

            default:
                return false;                                                   // 무인도, 세무조사, 세계여행
        }
    }

    /// <summary>
    /// 땅 칸 평가: 살 만한 빈 땅(2순위), 건물을 올릴 내 땅(3순위), 그 외 빈 땅·내 땅(중립), 남의 땅(제외).
    /// </summary>
    private bool TryScorePropertyDestination(GameState state, PlayerState bot, int position, int propertyId,
                                             out int tier, out double score)
    {
        tier = 5;
        score = 0;

        PropertyState property = state.PropertyStates.Find(p => p.PropertyId == propertyId);
        PropertyData data = getPropertyData(propertyId);
        if (property == null || data == null) return false;

        // 빈 땅: 구매 규칙을 통과하면 수익 비율로 비교
        if (property.OwnerId == null)
        {
            long price = data.LandPrice;
            if (IsSafeToSpendAt(state, bot, position, price) && IsWorthBuying(state, data, bot.Money - price))
            {
                tier = 2;
                score = GetMaxTollReturn(data);
            }
            return true;
        }

        // 내 땅: 다음 단계를 지을 수 있으면 통행료 상승폭으로 비교
        if (property.OwnerId == bot.PlayerId)
        {
            if (data.CanBuild && property.BuildingLevel < BuildingLevel.Hotel)
            {
                BuildingLevel next = property.BuildingLevel + 1;
                long cost = data.GetBuildCost(next);
                if (IsSafeToSpendAt(state, bot, position, cost))
                {
                    tier = 3;
                    score = data.GetToll(next) - data.GetToll(property.BuildingLevel);
                }
            }
            return true;
        }

        return false; // 남의 땅은 통행료만 내므로 제외
    }

    #endregion

    #region 공용

    /// <summary>
    /// 주사위 두 개의 합이 sum일 확률. (7이 6/36으로 가장 높고, 2와 12가 1/36으로 가장 낮음)
    /// </summary>
    private static double DiceSumProbability(int sum)
    {
        return (6 - Math.Abs(sum - 7)) / 36.0;
    }

    /// <summary>땅의 칸 번호. 보드에 없으면 -1.</summary>
    private int PositionOf(int propertyId)
    {
        return Array.FindIndex(tileByIndex, t => t != null && t.Type == TileType.PROPERTY && t.PropertyId == propertyId);
    }

    private static long GetMoney(GameState state, long botId)
    {
        PlayerState player = state.PlayerStates.Find(p => p.PlayerId == botId);
        return player != null ? player.Money : 0;
    }

    private static string FormatPayback(double payback)
    {
        return payback == double.MaxValue ? "불가" : $"{payback:F1}라운드";
    }

    /// <summary>
    /// 이번 라운드 이후 남은 라운드 수입니다. 최대 라운드가 없으면 제한 없음으로 봅니다.
    /// </summary>
    private int RemainingRounds(GameState state)
    {
        return maxRound > 0 ? Math.Max(0, maxRound - state.RoundNumber) : int.MaxValue;
    }

    private bool IsEndGame(GameState state)
    {
        return RemainingRounds(state) <= settings.EndGameRounds;
    }

    /// <summary>
    /// 판단 근거 로그. 성향 이름을 붙여서 어떤 성향의 봇이 판단했는지 보이게 합니다.
    /// </summary>
    private void Log(long botId, string message)
    {
        log?.Invoke($"[봇 판단:{settings.Name}] {botId}: {message}");
    }

    #endregion
}