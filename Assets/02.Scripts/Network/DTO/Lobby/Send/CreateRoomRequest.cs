using System;

[Serializable]
public class CreateRoomRequest
{
    public string type;
    public string roomName; // 방장이 정한 방 이름. 비어 있으면(옛 빌드 포함) 만든 사람의 이름으로 정한다
    public string password; // 방 비밀번호 (v1.3, 숫자 4자리). 비어 있으면 비밀번호 없는 방
}