using System.Collections.Generic;
using System.Text;

/// <summary>
/// 방에 있는 사람들의 닉네임을 Photon 방 속성(글자 하나) 안에 넣고 꺼내는 규칙.
/// 방 목록에서 방을 눌렀을 때 참가자 이름을 보여 주려고, 방장 PC가 방 정보가 바뀔 때마다 이 글자를 갱신한다.
/// Photon 방 속성은 전체 500바이트까지라서 이름은 방 목록에 올릴 때 짧게 줄인다. (방 안에서는 전체 닉네임이 보인다)
/// 순서는 방장이 맨 앞, 그다음 다른 사람, 봇은 맨 뒤다.
/// </summary>
public static class RoomMemberCodec
{
    /// <summary>이름과 이름 사이. 화면에서 칠 수 없는 글자라 닉네임과 섞이지 않는다</summary>
    public const char Separator = '\u001f';

    /// <summary>봇 이름 앞에 붙이는 표시</summary>
    public const char BotMark = '\u0002';

    /// <summary>방 목록에 올리는 이름의 최대 글자 수</summary>
    public const int MaxNameLength = 10;

    /// <summary>최대 글자 수보다 길면 앞부분만 남기고 끝을 ".."로 줄인다. (글꼴에 없는 … 대신 점 두 개)</summary>
    public static string Shorten(string name, int maxLength = MaxNameLength)
    {
        if (string.IsNullOrEmpty(name)) return "";
        name = Clean(name);
        if (maxLength < 3 || name.Length <= maxLength) return name;

        return name.Substring(0, maxLength - 2) + "..";
    }

    // 구분 글자와 표시 글자는 이름에서 뺀다. (닉네임 입력칸에서는 칠 수 없지만 값이 섞이는 것을 막는다)
    private static string Clean(string name) => name.Replace(Separator.ToString(), "").Replace(BotMark.ToString(), "");

    /// <summary>이름 목록을 글자 하나로 만든다. names와 bots는 같은 순서다. (bots가 없으면 모두 사람)</summary>
    public static string Encode(IList<string> names, IList<bool> bots = null)
    {
        if (names == null || names.Count == 0) return "";

        var text = new StringBuilder();
        for (int i = 0; i < names.Count; i++)
        {
            if (i > 0) text.Append(Separator);
            if (bots != null && i < bots.Count && bots[i]) text.Append(BotMark);
            text.Append(Shorten(names[i]));
        }
        return text.ToString();
    }

    /// <summary>글자 하나에서 이름 목록과 봇 여부를 꺼낸다. 비었거나 읽을 수 없으면 빈 목록이다.</summary>
    public static void Decode(string text, out List<string> names, out List<bool> bots)
    {
        names = new List<string>();
        bots = new List<bool>();
        if (string.IsNullOrEmpty(text)) return;

        foreach (string part in text.Split(Separator))
        {
            bool bot = part.Length > 0 && part[0] == BotMark;
            names.Add(bot ? part.Substring(1) : part);
            bots.Add(bot);
        }
    }
}