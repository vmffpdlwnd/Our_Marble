using System;

[Serializable]
public class CardDrawnBroadcast
{
    public long playerId;
    public int cardId;
    public long targetPlayerId; // 위치 바꾸기 카드에서 호스트가 고른 상대. 없으면 0
}