using System;

// 내 땅을 남이 인수하려 할 때, 땅 주인이 인수 방지권을 쓸지 고른 결과를 호스트에 보낸다 (차례가 아닌 사람이 보내는 요청)
[Serializable]
public class ChooseTakeoverShieldRequest
{
    public string type;
    public bool use;
}