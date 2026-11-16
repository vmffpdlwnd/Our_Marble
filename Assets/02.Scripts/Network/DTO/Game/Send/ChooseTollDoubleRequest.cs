using System;

// 통행료 2배 카드로 고른 내 땅을 호스트에 보낸다
[Serializable]
public class ChooseTollDoubleRequest
{
    public string type;
    public int propertyId;
}