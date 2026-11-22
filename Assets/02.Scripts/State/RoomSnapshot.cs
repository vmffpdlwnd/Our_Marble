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
    public string password; // 방 비밀번호 (v1.3). 방 안의 사람에게만 가는 정보라 담는다. 없으면 비밀번호 없는 방
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
    public bool newbie;     // 뉴비 옵션을 켰는지 (v1.3). 주지 않으면 false
}