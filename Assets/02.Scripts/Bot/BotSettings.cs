/// <summary>
/// 봇 성향 목록입니다. 인스펙터의 PlayerSetup에서 봇마다 고릅니다.
/// 새 성향은 여기에 추가하고 BotSettings.For에 값을 연결합니다.
/// </summary>
public enum BotPersonality
{
    Balanced,   // 기본형 (중간 성향)
}

/// <summary>
/// 봇 성향마다 달라지는 판단 기준값입니다.
/// BasicBotStrategy는 이 값만 읽어서 판단하므로, 성향을 추가할 때 판단 로직은 건드리지 않습니다.
/// Unity 기능을 쓰지 않는 순수 클래스라 서버(Java)로 그대로 옮길 수 있습니다.
/// </summary>
public class BotSettings
{
    public string Name = "기본형";                   // 로그에 표시할 성향 이름

    // ── 통행료 위험 ──
    public double MaxCashShortProbability = 0.2;     // 다음 턴 현금 부족 확률이 이보다 크면 돈을 쓰지 않음

    // ── 구매 ──
    public double GoodLandReturn = 0.75;             // 최대 통행료 ÷ 총 투자금이 이 이상이면 좋은 땅
    public long SafeCashReserve = 200000;            // 보통 땅 구매·회수가 느린 건설은 이만큼 현금이 남을 때만 진행

    // ── 건설·인수 ──
    public double MaxPaybackRounds = 10;             // 투자 비용을 이 라운드 안에 통행료로 회수할 수 있으면 건설·인수

    // ── 기회비용 ──
    public double BetterLandMargin = 0.2;            // 구매 시 지금 땅보다 수익 비율이 이만큼 높아야 "더 좋은 땅"
    public double MaxMissedChance = 0.15;            // 더 좋은 땅을 놓칠 확률이 이보다 크면 돈을 아낌

    // ── 막판 ──
    public int EndGameRounds = 3;                    // 남은 라운드가 이 이하면 막판 (구매·건설 기준 완화)

    // ── 세계여행 ──
    public long WelfareFundWorthTrip = 200000;       // 적립금이 이 이상이면 세계여행 최우선 목적지

    /// <summary>기본형: 지금까지 만든 판단 기준 그대로입니다.</summary>
    public static BotSettings Balanced()
    {
        return new BotSettings();
    }

    /// <summary>
    /// 봇 너프(v1.3 순위 보정): 봇이 살아 있는 사람 중 가장 높은 사람보다 앞설 때 쓰는 약한 판단 기준입니다.
    /// 현금을 더 아끼고(위험 허용 낮춤, 남길 현금 늘림), 좋은 땅 기준과 회수 기간을 엄격하게 해서 구매·건설·인수를 덜 합니다.
    /// 판단 로직은 같고 값만 다릅니다. 값은 플레이해 보고 고칩니다.
    /// </summary>
    public static BotSettings Nerfed()
    {
        return new BotSettings
        {
            Name = "너프",
            MaxCashShortProbability = 0.1, // 기본 0.2
            GoodLandReturn = 0.9,          // 기본 0.75
            SafeCashReserve = 400000,      // 기본 200000
            MaxPaybackRounds = 6,          // 기본 10
            MaxMissedChance = 0.1,         // 기본 0.15
        };
    }

    /// <summary>성향에 맞는 설정값을 만듭니다.</summary>
    public static BotSettings For(BotPersonality personality)
    {
        switch (personality)
        {
            case BotPersonality.Balanced:
            default:
                return Balanced();
        }
    }
}