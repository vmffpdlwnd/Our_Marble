/// <summary>
/// 방 이름을 정하는 규칙. 방 이름은 방을 만들 때 한 번 정해서 방 정보에 저장하고, 방장이 바뀌어도 그대로 둔다.
/// 이름이 없는 방(이름 저장이 없던 옛 빌드가 만든 방 등)만 방장 닉네임으로 대신 만든다.
/// </summary>
public static class RoomNames
{
    /// <summary>방을 만든 사람의 닉네임으로 만드는 기본 이름. 예: 철수의 방</summary>
    public static string Default(string hostNickname) =>
        string.IsNullOrEmpty(hostNickname) ? "이름 없는 방" : $"{hostNickname}의 방";

    /// <summary>저장된 방 이름이 있으면 그것을, 없으면 방장 닉네임으로 만든 기본 이름을 돌려준다.</summary>
    public static string Resolve(string roomName, string hostNickname) =>
        string.IsNullOrWhiteSpace(roomName) ? Default(hostNickname) : roomName;
}