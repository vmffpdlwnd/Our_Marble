using System;

/// <summary>파산 신청 알림. 받은 쪽은 같은 규칙(TradeRules.DeclareBankruptcy)으로 재산을 은행에 돌려주고 파산 처리한다.</summary>
[Serializable]
public class BankruptcyDeclaredBroadcast
{
    public long playerId;
}