using System.Collections.Generic;
using System.Linq;

/// <summary>땅 거래 요청이 거절되는 이유. None이면 거절할 이유가 없다.</summary>
public enum TradeError
{
    None,
    AlreadyOwned,      // 이미 주인이 있는 땅
    NotEnoughMoney,    // 현금이 부족 (구매, 건설, 인수)
    NotOwner,          // 내 땅이 아님 (건설)
    CannotBuild,       // 건설할 수 없는 땅
    MaxLevel,          // 이미 최고 단계
    NotOthersProperty, // 다른 사람의 땅이 아님 (인수)
    NotOnProperty,     // 통행료를 낼 땅 위에 있지 않음 (매각)
    DuplicateProperty, // 같은 땅이 두 번 들어 있음 (매각)
    SellNotOwner,      // 내 땅이 아닌 땅이 들어 있음 (매각)
    SellNotEnough,     // 판 돈을 합쳐도 통행료가 모자람 (매각)
}

/// <summary>땅에 도착했을 때의 결과. 어떤 입력을 기다릴지(WaitingType)는 규칙이 이미 정해 두었다.</summary>
public enum PropertyArrivalKind
{
    MissingData,      // 땅 데이터가 없음. 턴을 종료해야 한다
    Purchase,         // 빈 땅: 구매 대기
    Build,            // 내 땅: 건설 대기
    TollPaidAcquire,  // 통행료를 냈고, 인수할 돈이 있어 인수 대기
    TollPaidEnd,      // 통행료를 냈고, 인수할 돈은 없음. 턴을 종료해야 한다
    Sell,             // 현금이 모자라 땅을 팔아야 함: 매각 대기
    Bankrupt,         // 전부 팔아도 모자라 파산해야 한다
}

/// <summary>땅 도착 결과. 남의 땅일 때(TollPaid*, Sell, Bankrupt)만 Owner와 Toll이 채워진다.</summary>
public class PropertyArrival
{
    public PropertyArrivalKind Kind;
    public PlayerState Owner; // 통행료를 받을 사람
    public long Toll;         // 통행료
}

/// <summary>매각 요청을 확인한 결과. Error가 None일 때 Receiver, Toll, TotalAmount로 매각을 진행한다.</summary>
public class SellPlan
{
    public TradeError Error;
    public PlayerState Receiver; // 통행료를 받을 사람
    public long Toll;            // 낼 통행료
    public long TotalAmount;     // 팔 땅의 매각가 합계
}

/// <summary>파산 처리의 결과</summary>
public enum BankruptResult
{
    AlreadyBankrupt, // 이미 파산한 플레이어라 등수와 표시는 바꾸지 않음
    Bankrupted,      // 파산 처리됨
    LastPlayerLeft,  // 파산 처리됐고 남은 플레이어가 1명 이하라 게임이 끝나야 함
}

/// <summary>
/// 땅 거래 규칙: 구매, 건설, 인수, 매각, 통행료, 파산.
/// 화면(Unity 오브젝트)을 모른다. 요청이 가능한지 확인(Check...)하는 일과 실제로 상태를 바꾸는 일을 나누어 두었다.
/// 요청을 누가 보냈는지, 단계가 맞는지, 말이 그 땅 위에 있는지 같은 요청 검증은 부른 쪽(호스트)이 먼저 한다.
/// 각 함수 위에 짝이 되는 GameManager 함수를 적어 두었다. 규칙을 바꿀 때는 양쪽을 함께 고친다.
/// </summary>
public static class TradeRules
{
    // ───────────── 구매 ─────────────

    // GameManager.HandlePropertyPurchased
    // 거절은 확인할 것이 없다. (살 돈이 없는 사람은 거절만 보낼 수 있다)
    public static TradeError CheckPurchase(GameData data, PlayerState player, PropertyState property, bool isAccept)
    {
        if (!isAccept) return TradeError.None;

        if (property.OwnerId != null) return TradeError.AlreadyOwned;
        if (player.Money < data.Properties.GetLandPrice(property.PropertyId)) return TradeError.NotEnoughMoney;

        return TradeError.None;
    }

    /// <summary>땅을 산다. 낸 땅값을 돌려준다.</summary>
    public static long Purchase(GameData data, PlayerState player, PropertyState property)
    {
        long price = data.Properties.GetLandPrice(property.PropertyId);

        player.Money -= price;
        property.OwnerId = player.PlayerId;
        property.BuildingLevel = BuildingLevel.Land; // 새로 산 땅은 건물 없음
        return price;
    }

    // ───────────── 건설 ─────────────

    // GameManager.HandleBuilt
    // 거절은 확인할 것이 없다. (건설할 수 없는 땅에서도 거절은 보낼 수 있다)
    public static TradeError CheckBuild(GameData data, PlayerState player, PropertyState property, bool isAccept)
    {
        if (!isAccept) return TradeError.None;

        PropertyData propertyData = data.Properties.GetData(property.PropertyId);

        if (property.OwnerId != player.PlayerId) return TradeError.NotOwner;
        if (propertyData == null || !propertyData.CanBuild) return TradeError.CannotBuild;
        if (property.BuildingLevel >= BuildingLevel.Hotel) return TradeError.MaxLevel;

        long cost = data.Properties.GetBuildCost(property.PropertyId, property.BuildingLevel + 1);
        if (player.Money < cost) return TradeError.NotEnoughMoney;

        return TradeError.None;
    }

    /// <summary>건물을 한 단계 올린다. 낸 건설비를 돌려준다.</summary>
    public static long Build(GameData data, PlayerState player, PropertyState property)
    {
        long cost = data.Properties.GetBuildCost(property.PropertyId, property.BuildingLevel + 1);

        player.Money -= cost;
        property.BuildingLevel = property.BuildingLevel + 1;
        return cost;
    }

    // ───────────── 인수 ─────────────

    // GameManager.HandlePropertyAcquired
    // 거절은 확인할 것이 없다.
    public static TradeError CheckAcquire(GameState state, GameData data, PlayerState player, PropertyState property, bool isAccept)
    {
        if (!isAccept) return TradeError.None;

        PlayerState owner = property.OwnerId.HasValue ? GameRules.FindPlayer(state, property.OwnerId.Value) : null;
        if (owner == null || owner.PlayerId == player.PlayerId) return TradeError.NotOthersProperty;

        if (player.Money < data.Properties.GetAcquireValue(property)) return TradeError.NotEnoughMoney;

        return TradeError.None;
    }

    /// <summary>땅을 인수한다. 인수자에게서 빼고 원래 주인에게 준 금액을 돌려준다. 주인만 바뀌고 건물 단계는 그대로다.</summary>
    public static long Acquire(GameData data, PlayerState player, PlayerState owner, PropertyState property)
    {
        long amount = data.Properties.GetAcquireValue(property);

        player.Money -= amount;   // 인수자 현금 차감
        owner.Money += amount;    // 원래 주인 현금 증가
        property.OwnerId = player.PlayerId;
        return amount;
    }

    // ───────────── 매각 ─────────────

    // GameManager.HandlePropertiesSold
    /// <summary>
    /// 통행료를 낼 현금이 모자라 매각 단계에 있을 때의 요청을 확인한다.
    /// 통행료를 낼 땅 위에 있어야 하고(그 땅의 주인이 통행료를 받는다), 같은 땅을 두 번 팔 수 없고,
    /// 모두 내 땅이어야 하며, 판 돈을 합쳐 통행료를 낼 수 있어야 한다.
    /// </summary>
    public static SellPlan CheckSell(GameState state, GameData data, PlayerState player, List<int> propertyIds)
    {
        var plan = new SellPlan();

        TileData tile = data.GetTile(player.Position);
        PropertyState landed = tile != null && tile.Type == TileType.PROPERTY ? FindProperty(state, tile.PropertyId) : null;
        if (landed == null || !landed.OwnerId.HasValue || landed.OwnerId.Value == player.PlayerId)
        {
            plan.Error = TradeError.NotOnProperty;
            return plan;
        }

        plan.Receiver = GameRules.FindPlayer(state, landed.OwnerId.Value);
        plan.Toll = data.Properties.GetToll(landed);

        if (propertyIds.Distinct().Count() != propertyIds.Count)
        {
            plan.Error = TradeError.DuplicateProperty;
            return plan;
        }

        foreach (int id in propertyIds)
        {
            PropertyState property = FindProperty(state, id);
            if (property.OwnerId != player.PlayerId)
            {
                plan.Error = TradeError.SellNotOwner;
                return plan;
            }
            plan.TotalAmount += data.Properties.GetSellValue(property);
        }

        if (player.Money + plan.TotalAmount < plan.Toll)
        {
            plan.Error = TradeError.SellNotEnough;
            return plan;
        }

        return plan;
    }

    /// <summary>
    /// 확인을 통과한 매각을 진행한다. 판 땅은 주인 없음 + 땅 단계로 돌아가고, 판 돈을 받아 통행료를 낸다.
    /// </summary>
    public static void Sell(GameState state, PlayerState player, SellPlan plan, List<int> propertyIds)
    {
        foreach (int id in propertyIds)
        {
            PropertyState property = FindProperty(state, id);
            property.BuildingLevel = BuildingLevel.Land;
            property.OwnerId = null;
        }

        player.Money = player.Money + plan.TotalAmount - plan.Toll;
        plan.Receiver.Money += plan.Toll;
    }

    // ───────────── 땅에 도착 ─────────────

    // GameManager.ProcessArrival의 PROPERTY 부분
    /// <summary>
    /// 땅에 도착했을 때 어떤 입력을 기다릴지(WaitingType)를 정한다.
    /// 남의 땅이면 통행료를 이체하고, 그 결과(인수 대기, 매각 대기, 파산)를 돌려준다.
    /// 파산은 여기서 처리하지 않고 알리기만 한다. (Bankrupt를 부르는 일은 부른 쪽이 한다)
    /// </summary>
    public static PropertyArrival ArriveAtProperty(GameState state, GameData data, PlayerState player, int propertyId)
    {
        PropertyState property = FindProperty(state, propertyId);
        PropertyData propertyData = data.Properties.GetData(propertyId);

        if (property == null || propertyData == null)
            return new PropertyArrival { Kind = PropertyArrivalKind.MissingData };

        if (property.OwnerId == null)
        {
            // 살 돈이 없어도 구매 단계로 들어간다. (선택창에서 거절만 보낼 수 있다)
            state.WaitingType = GameMessageType.PROPERTY_PURCHASED;
            return new PropertyArrival { Kind = PropertyArrivalKind.Purchase };
        }

        if (property.OwnerId.Value == player.PlayerId)
        {
            // 건설할 수 없는 땅이어도 건설 단계로 들어간다. (선택창에서 거절만 보낼 수 있다)
            state.WaitingType = GameMessageType.BUILT;
            return new PropertyArrival { Kind = PropertyArrivalKind.Build };
        }

        // 남의 땅: 통행료를 낸다
        PlayerState owner = GameRules.FindPlayer(state, property.OwnerId.Value);
        long toll = data.Properties.GetToll(property);

        if (player.Money >= toll)
        {
            PayToll(player, owner, toll);

            // 통행료를 내고도 인수할 돈이 있으면 인수를 선택할 수 있다
            long acquireValue = data.Properties.GetAcquireValue(property);
            if (player.Money >= acquireValue)
            {
                state.WaitingType = GameMessageType.PROPERTY_ACQUIRED;
                return new PropertyArrival { Kind = PropertyArrivalKind.TollPaidAcquire, Owner = owner, Toll = toll };
            }

            return new PropertyArrival { Kind = PropertyArrivalKind.TollPaidEnd, Owner = owner, Toll = toll };
        }

        if (player.Money + data.Properties.GetTotalSellValue(player.PlayerId, state.PropertyStates) >= toll)
        {
            // 현금이 모자라지만 땅을 팔면 낼 수 있다. 팔 땅을 고르는 단계로 들어간다.
            state.WaitingType = GameMessageType.PROPERTIES_SOLD;
            return new PropertyArrival { Kind = PropertyArrivalKind.Sell, Owner = owner, Toll = toll };
        }

        // 전부 팔아도 모자라면 파산한다.
        return new PropertyArrival { Kind = PropertyArrivalKind.Bankrupt, Owner = owner, Toll = toll };
    }

    // GameManager.HandleTollPaid (통행료는 알림 없이 양쪽이 같은 규칙으로 반영한다)
    private static void PayToll(PlayerState payer, PlayerState receiver, long amount)
    {
        payer.Money -= amount;
        receiver.Money += amount;
    }

    // ───────────── 파산 ─────────────

    // GameManager.HandleBankruptcy, ProcessBankruptcy
    /// <summary>
    /// 파산: 가진 땅을 매각가로 현금화하고, 현금과 함께 통행료를 받을 사람에게 전부 넘긴다. 땅은 주인 없음 + 땅 단계로 돌아간다.
    /// 최종 등수는 파산하는 시점에 남아 있던 인원 수다. (예: 4명 중 첫 파산은 4위)
    /// 남은 플레이어가 1명 이하가 되면 게임을 끝내야 한다고 알린다. (끝내는 일은 부른 쪽이 한다)
    /// </summary>
    public static BankruptResult Bankrupt(GameState state, GameData data, PlayerState payer, PlayerState receiver)
    {
        long liquidated = 0;
        foreach (PropertyState property in state.PropertyStates)
        {
            if (property.OwnerId != payer.PlayerId) continue;

            liquidated += data.Properties.GetSellValue(property);
            property.OwnerId = null;
            property.BuildingLevel = BuildingLevel.Land;
        }

        receiver.Money += payer.Money + liquidated;
        payer.Money = 0;

        if (payer.IsBankrupt) return BankruptResult.AlreadyBankrupt; // 이미 파산한 플레이어는 다시 처리하지 않는다

        payer.FinalRank = state.PlayerStates.Count(p => !p.IsBankrupt);
        payer.IsBankrupt = true;

        return state.PlayerStates.Count(p => !p.IsBankrupt) <= 1 ? BankruptResult.LastPlayerLeft : BankruptResult.Bankrupted;
    }

    private static PropertyState FindProperty(GameState state, int propertyId)
    {
        return state.PropertyStates.Find(p => p.PropertyId == propertyId);
    }
}