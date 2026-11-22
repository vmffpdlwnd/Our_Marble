using System;

[Serializable]
public class EnterRoomRequest
{
    public string type;
    public string roomId;
    public string password; // 방 목록에서 비밀번호 방에 들어갈 때 입력한 비밀번호 (v1.3)
    public bool byCode;     // 방 코드로 들어오면 true. 방 코드는 초대장으로 보고 비밀번호를 묻지 않는다 (클라이언트가 정하는 값이라 막는 장치는 아니다)
}