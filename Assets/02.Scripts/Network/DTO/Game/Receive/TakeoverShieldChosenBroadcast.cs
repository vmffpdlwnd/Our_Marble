using System;

// 인수 방지권을 쓸지 고른 결과를 모두에게 알린다. 받은 쪽은 같은 규칙으로 인수를 막거나(카드를 냄) 그대로 인수한다
[Serializable]
public class TakeoverShieldChosenBroadcast
{
    public long playerId; // 땅 주인 (고른 사람)
    public bool used;     // 인수 방지권을 써서 인수를 막았다
}