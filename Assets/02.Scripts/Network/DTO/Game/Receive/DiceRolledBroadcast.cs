using System;

[Serializable]
public class DiceRolledBroadcast
{
    public long playerId;
    public int dice1;
    public int dice2;
    public bool usedIslandEscape; // 굴리기 전에 무인도 탈출권을 썼다. 받은 쪽도 같은 규칙(CardRules.UseIslandEscape)을 먼저 적용한다
}