using System;
using System.Collections.Generic;

[Serializable]
public class RoomSnapshot
{
    public string roomId;
    public string roomName; // 방 이름. 방을 만들 때 정해서 방장이 바뀌어도 그대로다. 없으면(옛 방) 방장 닉네임으로 대신 만든다
    public long hostId;
    public int mapId;
    public RoomStatus status;
    public GameConfig config;
    public List<PlayerSnapshot> Players;
    public int maxPlayers;
    public int botCount;
}

public enum RoomStatus
{
    WAITING,
    PLAYING
}

public class PlayerSnapshot
{
    public long playerId;
    public String nickname;
    public bool ready;
    public bool bot; // 봇이면 true. 봇도 players 목록에 사람과 함께 들어 있다
    public int? portraitId; // 고른 초상화 번호. 서버가 주지 않았거나 고르지 않았으면 없음
}