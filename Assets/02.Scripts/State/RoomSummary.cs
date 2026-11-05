using System;
using System.Collections.Generic;

[Serializable]
public class RoomSummary
{
    public string roomId;
    public string roomName;                   // 방 이름. 없으면(옛 방) 방장 닉네임으로 대신 만든다
    public string hostNickname;
    public List<string> memberNicknames;      // 참가자 닉네임. 방장이 맨 앞, 봇은 맨 뒤. 없으면(옛 방) 방장만 알 수 있다
    public List<bool> memberBots;             // memberNicknames와 같은 순서로 봇이면 true
    public int mapId;
    public int playerCount;
    public int maxPlayers;
}