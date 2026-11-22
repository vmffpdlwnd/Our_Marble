using System;

/// <summary>
/// 방장이 대기방에서 방 이름과 비밀번호를 바꾼다 (v1.3). 방장만, 대기 중인 방에서만 된다.
/// 이름을 비우면 방장 닉네임으로 만든 기본 이름, 비밀번호를 비우면 비밀번호 없는 방이 된다.
/// </summary>
[Serializable]
public class SetRoomInfoRequest
{
    public string type;
    public string roomName;
    public string password;
}