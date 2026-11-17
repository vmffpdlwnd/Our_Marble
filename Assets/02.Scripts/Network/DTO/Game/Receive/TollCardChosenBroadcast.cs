using System;

// 통행료 카드를 고른 결과를 모두에게 알린다. 받은 쪽은 같은 규칙(CardRules.UseTollCard → TradeRules.PayTollOnArrival)으로 통행료를 낸다
[Serializable]
public class TollCardChosenBroadcast
{
    public long playerId;
    public CardEffectType? card; // 실제로 쓴 카드. 쓰지 않았으면 null
}