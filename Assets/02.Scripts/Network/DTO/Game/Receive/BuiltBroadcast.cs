using System;

[Serializable]
public class BuiltBroadcast
{
    public long playerId;
    public int propertyId;
    public bool isAccept;
    public bool usedBuildDiscount; // 건설 할인권을 썼다. 받은 쪽도 같은 규칙(TradeRules.Build)으로 할인한다
}