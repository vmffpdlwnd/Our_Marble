using System.Linq;

/// <summary>
/// 방 코드를 다루는 규칙. 방 코드는 Photon 세션 이름이고, 헷갈리는 글자(0, 1, I, L, O)를 뺀 31글자 중 8자리다.
/// 화면에는 ABCD-EFGH처럼 가운데에 하이픈을 넣어 보여 준다.
/// </summary>
public static class RoomCodeRules
{
    public const int CodeLength = 8;

    /// <summary>방 코드에 쓰는 글자. 0, 1, I, L, O는 숫자와 알파벳이 헷갈려서 쓰지 않는다.</summary>
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    /// <summary>사람이 입력한 코드를 정리한다. (공백과 하이픈 같은 글자를 빼고 대문자로)</summary>
    public static string Normalize(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        return new string(input.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    /// <summary>화면에 보일 모양: ABCD-EFGH</summary>
    public static string Format(string code)
    {
        return !string.IsNullOrEmpty(code) && code.Length == CodeLength ? code.Substring(0, 4) + "-" + code.Substring(4) : code;
    }

    /// <summary>입력한 코드에 문제가 있다면 무엇인지</summary>
    public enum Problem
    {
        None,
        Empty,        // 아무것도 입력하지 않음
        WrongLength,  // 8자리가 아님
        BadCharacter, // 방 코드에 쓰지 않는 글자가 있음 (0, 1, I, L, O, 한글 등)
    }

    /// <summary>입력한 글자가 방 코드로 쓸 수 있는지 확인한다. 쓸 수 있으면 정리된 코드를 code에 돌려준다.</summary>
    public static Problem Check(string input, out string code)
    {
        code = Normalize(input);
        if (code.Length == 0) return Problem.Empty;
        if (code.Any(c => Alphabet.IndexOf(c) < 0)) return Problem.BadCharacter;
        if (code.Length != CodeLength) return Problem.WrongLength;
        return Problem.None;
    }

    /// <summary>사용자에게 보여 줄 안내 문장. 문제가 없으면 null</summary>
    public static string MessageFor(Problem problem)
    {
        switch (problem)
        {
            case Problem.Empty: return "방 코드를 입력해 주세요";
            case Problem.WrongLength: return "방 코드는 8자리예요 (예: ABCD-EFGH)";
            case Problem.BadCharacter: return "코드에 쓸 수 없는 글자가 있어요 (0, 1, I, L, O는 쓰지 않아요)";
            default: return null;
        }
    }
}