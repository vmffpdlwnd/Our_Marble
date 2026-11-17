using System;

[Serializable]
public class GameStartedBroadcast
{
    public string type;
    public string roomId;
    public int mapId; // 방의 맵 번호. 게임 씬이 이 번호의 보드를 쓴다 (없으면 0, 기본 보드)
}