using System;

[Serializable]
public class CreateRoomRequest
{
    public string type;
    public string roomName; // 방장이 정한 방 이름. 비어 있으면(옛 빌드 포함) 만든 사람의 이름으로 정한다
}