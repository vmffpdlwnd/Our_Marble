using System;

// 남의 땅에 도착해 통행료 면제권·반값권을 쓸지 고른 결과를 호스트에 보낸다
[Serializable]
public class ChooseTollCardRequest
{
    public string type;
    public CardEffectType? card; // TollFree 또는 TollHalf. 쓰지 않으면 null
}