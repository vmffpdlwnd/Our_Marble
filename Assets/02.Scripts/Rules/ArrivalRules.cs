using System;

/// <summary>칸에 도착했을 때의 결과. 어떤 입력을 기다릴지(WaitingType)가 필요하면 규칙이 이미 정해 두었다.</summary>
public enum TileArrivalKind
{
    None,        // 처리할 것이 없음 (알 수 없는 칸 종류)
    MissingTile, // 칸 데이터가 없음. 턴을 종료해야 한다
    EndTurn,     // 아무 일 없이 턴 종료 (출발 칸)
    NextTurn,    // 도착하면 더블이어도 강제로 다음 차례로 넘어감 (무인도, 세계여행 칸)
    WaitCard,    // 황금열쇠 칸: 카드 뽑기 요청을 기다린다
    Donation,    // 세무조사 칸: Amount를 냈다. 턴 종료
    Welfare,     // 적립금 칸: Amount를 받았다. (0이면 받을 것이 없었음) 턴 종료
    Property,    // 땅 칸: 땅 규칙(TradeRules.ArriveAtProperty)으로 이어간다
}

/// <summary>칸 도착 결과</summary>
public class TileArrival
{
    public TileArrivalKind Kind;
    public TileData Tile;
    public long MoneyBefore; // 도착한 순간의 현금 (세무조사·적립금을 반영하기 전)
    public long Amount;      // Donation: 낸 금액, Welfare: 받은 금액
}

/// <summary>세계여행 목적지를 고를 수 없는 이유</summary>
public enum DestinationError
{
    None,
    Invalid,         // 보드 안의 칸이 아님
    WorldTravelTile, // 세계여행 칸 자체는 목적지로 고를 수 없음
}

/// <summary>
/// 도착 규칙: 칸 종류별 도착 처리(시작, 무인도, 세무조사, 적립금, 세계여행, 황금열쇠, 땅)와 세계여행 목적지 확인.
/// 화면(Unity 오브젝트)을 모른다. 땅 칸의 구체적인 처리는 TradeRules가 맡는다.
/// 각 함수 위에 짝이 되는 GameManager 함수를 적어 두었다. 규칙을 바꿀 때는 양쪽을 함께 고친다.
/// </summary>
public static class ArrivalRules
{
    // GameManager.ProcessArrival
    /// <summary>
    /// 도착한 칸에 따라 처리한다. 이동은 이미 끝난 상태에서 부른다.
    /// 세무조사와 적립금은 여기서 돈을 옮기고, 그 금액을 결과에 담는다.
    /// 다음에 무엇을 할지(턴 종료, 다음 차례, 카드·땅 요청 대기)는 결과의 Kind에 담겨 있고 이어가는 일은 부른 쪽이 한다.
    /// </summary>
    public static TileArrival ArriveAtTile(GameState state, GameData data, PlayerState player, int position)
    {
        TileData tile = data.GetTile(position);
        if (tile == null) return new TileArrival { Kind = TileArrivalKind.MissingTile };

        var arrival = new TileArrival { Tile = tile, MoneyBefore = player.Money };

        switch (tile.Type)
        {
            case TileType.START:
                arrival.Kind = TileArrivalKind.EndTurn;
                break;

            case TileType.ISLAND:
                player.IslandTurnsRemaining = GameRules.IslandTurns;
                arrival.Kind = TileArrivalKind.NextTurn; // 강제로 턴을 넘긴다
                break;

            case TileType.CHARITY:
                arrival.Amount = ReceiveWelfareFund(state, player);
                arrival.Kind = TileArrivalKind.Welfare;
                break;

            case TileType.DONATION:
                arrival.Amount = PayDonation(state, player);
                arrival.Kind = TileArrivalKind.Donation;
                break;

            case TileType.WORLD_TRAVEL:
                arrival.Kind = TileArrivalKind.NextTurn; // 도착 후에는 더블이어도 턴을 넘긴다
                break;

            case TileType.GOLDEN_KEY:
                state.WaitingType = GameMessageType.CARD_DRAWN; // 카드 뽑기 요청을 기다린다
                arrival.Kind = TileArrivalKind.WaitCard;
                break;

            case TileType.PROPERTY:
                arrival.Kind = TileArrivalKind.Property;
                break;

            default:
                arrival.Kind = TileArrivalKind.None;
                break;
        }

        return arrival;
    }

    // GameManager.HandleDonationPaid (세무조사: 현금 한도 안에서만 걷고, 걷은 금액은 적립금에 쌓는다)
    private static long PayDonation(GameState state, PlayerState player)
    {
        long paid = Math.Min(state.Config.taxAmount, Math.Max(0, player.Money));
        player.Money -= paid;
        state.WelfareFund += paid;
        return paid;
    }

    // GameManager.HandleWelfareFundReceived (적립금 칸: 쌓인 금액을 모두 받는다)
    private static long ReceiveWelfareFund(GameState state, PlayerState player)
    {
        long amount = state.WelfareFund;
        if (amount <= 0) return 0;

        player.Money += amount;
        state.WelfareFund -= amount;
        return amount;
    }

    // GameManager.HandleDestinationChosen
    /// <summary>세계여행 목적지로 고를 수 있는지 확인한다. 보드 안의 칸이어야 하고, 세계여행 칸 자체는 고를 수 없다.</summary>
    public static DestinationError CheckDestination(GameData data, int position)
    {
        TileData tile = position >= 0 && position < data.TileCount ? data.GetTile(position) : null;

        if (tile == null) return DestinationError.Invalid;
        if (tile.Type == TileType.WORLD_TRAVEL) return DestinationError.WorldTravelTile;

        return DestinationError.None;
    }
}