using System;

/// <summary>
/// 시간 규칙: 입력을 기다리는 시간과 호스트가 대신 진행하는 시간.
/// 호스트(HostGameService)와 화면(UIManager)이 같은 값을 쓰도록 한 곳에 둔다.
///
/// 화면의 선택창은 호스트가 대신 진행하는 시간보다 먼저 닫혀야 한다. 호스트가 먼저 움직이면 화면과 겹쳐 입력이 거절된다.
/// 그 사이의 여유는 연출이 늦어지는 시간과 네트워크 지연을 덮기 위한 것이다.
///
///   사람(일반)  호스트: 방 설정의 제한 시간(기본 60초)   화면: 호스트보다 10초 먼저 (50초)
///   사람(AFK)   호스트: 8초                            화면: 호스트보다 3초 먼저 (5초)
///   봇          호스트: 6초
/// </summary>
public static class TimeRules
{
    public const float BotDelaySeconds = 6f;         // 봇 차례: 호스트가 이 시간 뒤에 진행한다
    public const float AfkDelaySeconds = 8f;         // AFK인 사람: 호스트가 이 시간 뒤에 대신 진행한다
    public const float ScreenMarginSeconds = 10f;    // 일반 선택창은 호스트가 대신 진행하는 시간보다 이만큼 먼저 닫힌다
    public const float AfkScreenMarginSeconds = 3f;  // AFK 선택창은 호스트가 대신 진행하는 시간보다 이만큼 먼저 닫힌다

    private const float DefaultLimitSeconds = 60f;   // 방 설정이 없을 때의 제한 시간
    private const float NoLimitScreenSeconds = 30f;  // 제한 시간이 없는 방에서 선택창이 닫히는 시간
    private const float MinScreenSeconds = 1f;       // 선택창이 닫히는 시간의 최소값

    /// <summary>
    /// 호스트가 입력을 기다렸다가 대신 진행하기까지의 시간(초).
    /// 사람의 제한 시간이 0 이하이면 제한이 없으므로 무한대를 돌려준다.
    /// </summary>
    public static float HostWaitSeconds(GameConfig config, bool isBot, bool isAfk)
    {
        if (isBot) return BotDelaySeconds;
        if (isAfk) return AfkDelaySeconds;

        float limit = config != null ? config.limitTimeSeconds : DefaultLimitSeconds;
        return limit > 0 ? limit : float.PositiveInfinity;
    }

    /// <summary>
    /// 화면의 선택창이 닫히기까지의 시간(초). 호스트가 대신 진행하는 시간보다 먼저 닫히도록 여유(margin)만큼 줄인다.
    /// </summary>
    public static float ScreenSeconds(GameConfig config, bool isAfk)
    {
        float hostWait = HostWaitSeconds(config, false, isAfk);
        if (float.IsPositiveInfinity(hostWait)) return NoLimitScreenSeconds;

        float margin = isAfk ? AfkScreenMarginSeconds : ScreenMarginSeconds;
        return Math.Max(MinScreenSeconds, hostWait - margin);
    }
}