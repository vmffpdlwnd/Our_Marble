using UnityEngine;

/// <summary>
/// 이 PC에서 플레이하는 사람의 정보. 서버 접속(WebSocketNetwork)이 들고 있던 내 playerId와 닉네임을 대신한다.
/// 타이틀 화면에서 닉네임을 정하면 Set()으로 채우고, 타이틀로 돌아가면 Clear()로 비운다.
/// </summary>
public static class LocalPlayer
{
    public static long Id { get; private set; }
    public static string Nickname { get; private set; }

    /// <summary>닉네임이 정해져 있는지. (게임을 할 준비가 된 상태)</summary>
    public static bool IsSet => !string.IsNullOrEmpty(Nickname);

    public static void Set(long id, string nickname)
    {
        Id = id;
        Nickname = nickname;
    }

    public static void Clear()
    {
        Id = 0;
        Nickname = null;
    }

    // 에디터에서 도메인 리로드 없이 플레이를 다시 시작해도 이전 플레이의 값이 남지 않게 한다
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => Clear();
}