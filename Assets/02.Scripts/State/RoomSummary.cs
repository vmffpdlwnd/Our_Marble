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
    public bool locked;                       // 비밀번호 방이면 true (v1.3). 방 목록은 로비의 모두가 보므로 비밀번호 자체는 담지 않는다
}