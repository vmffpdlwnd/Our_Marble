/// <summary>
/// 방 이름을 정하는 규칙. 방 이름은 방을 만들 때 한 번 정해서 방 정보에 저장하고, 방장이 바뀌어도 그대로 둔다.
/// 이름이 없는 방(이름 저장이 없던 옛 빌드가 만든 방 등)만 방장 닉네임으로 대신 만든다.
/// </summary>
public static class RoomNames
{
    /// <summary>방 이름의 최대 글자 수. 방 목록에 올라가는 방 속성(500바이트 제한)과 목록 한 줄의 길이를 함께 고려한 값이다.</summary>
    public const int MaxLength = 12;

    /// <summary>
    /// 사람이 입력한 방 이름을 정리한다. 앞뒤 공백과 제어 글자를 빼고 최대 길이로 자른다.
    /// 아무것도 남지 않으면 null이다. (이름을 비우면 기본 이름 "[만든 사람]의 방"을 쓴다)
    /// </summary>
    public static string Sanitize(string input)
    {
        if (string.IsNullOrEmpty(input)) return null;

        var text = new System.Text.StringBuilder(input.Length);
        foreach (char c in input)
            if (!char.IsControl(c)) text.Append(c); // 줄바꿈, 탭, 방 속성 구분 글자 같은 보이지 않는 글자는 뺀다

        string name = text.ToString().Trim();
        if (name.Length > MaxLength)
        {
            int cut = MaxLength;
            if (char.IsHighSurrogate(name[cut - 1])) cut--; // 이모지 같은 글자(두 칸)의 가운데에서 자르지 않는다
            name = name.Substring(0, cut).TrimEnd();
        }

        return name.Length == 0 ? null : name;
    }

    /// <summary>방을 만든 사람의 닉네임으로 만드는 기본 이름. 예: 철수의 방</summary>
    public static string Default(string hostNickname) =>
        string.IsNullOrEmpty(hostNickname) ? "이름 없는 방" : $"{hostNickname}의 방";

    /// <summary>저장된 방 이름이 있으면 그것을, 없으면 방장 닉네임으로 만든 기본 이름을 돌려준다.</summary>
    public static string Resolve(string roomName, string hostNickname) =>
        string.IsNullOrWhiteSpace(roomName) ? Default(hostNickname) : roomName;
}