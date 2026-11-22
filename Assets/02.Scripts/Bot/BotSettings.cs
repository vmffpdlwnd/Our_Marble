using System;

/// <summary>
/// 봇 성향 목록입니다. 고르는 화면은 없고, 게임을 시작할 때 호스트가 봇마다 뽑은 숫자(PlayerState.BotNumber)의 끝자리로 정합니다.
/// 새 성향은 여기에 추가하고 BotSettings.For에 값을, BotSettings.PersonalityOf에 끝자리를 연결합니다.
/// </summary>
public enum BotPersonality
{
    Balanced,   // 기본형 (중간 성향)
    RealEstate, // 부동산형: 땅을 많이 사고 건설도 적극적
    Miser,      // 짠돌이: 현금을 많이 남기고 좋은 땅만 삼
    Aggressive, // 공격형: 사람 중 꼴찌를 노림 (노리는 판단은 BasicBotStrategy)
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

    // ── 공격형 ──
    public bool TargetsLastHuman;                    // 살아 있는 사람 중 꼴찌를 목표로 노림 (BasicBotStrategy.FindTarget)

    // ── 너프 ──
    public bool IsNerfed;                            // Nerfed()로 만든 값인지 (공격형은 너프 중에 노리기를 멈춤)

    // ───────────── 성향 숫자 ─────────────

    /// <summary>봇 숫자는 0 이상 이 값 미만에서 뽑습니다. 음수는 쓰지 않습니다. (음수 나머지와 절댓값 오버플로우를 피하려고)</summary>
    public const int BotNumberRange = 100;

    /// <summary>
    /// 봇 숫자의 끝자리로 성향을 정합니다. 숫자가 없으면(사람, 옛 게임 상태) 기본형입니다.
    ///   0 공격형 (10%) / 1, 2, 3 기본형 (30%) / 4, 5, 6 부동산형 (30%) / 7, 8, 9 짠돌이 (30%)
    /// 확률은 이 끝자리 매칭만 바꿔서 조절합니다.
    /// </summary>
    public static BotPersonality PersonalityOf(int? botNumber)
    {
        if (!botNumber.HasValue || botNumber.Value < 0) return BotPersonality.Balanced;

        switch (botNumber.Value % 10)
        {
            case 0: return BotPersonality.Aggressive;
            case 4:
            case 5:
            case 6: return BotPersonality.RealEstate;
            case 7:
            case 8:
            case 9: return BotPersonality.Miser;
            default: return BotPersonality.Balanced; // 1, 2, 3
        }
    }

    // ───────────── 성향별 값 ─────────────

    /// <summary>기본형: 지금까지 만든 판단 기준 그대로입니다.</summary>
    public static BotSettings Balanced()
    {
        return new BotSettings();
    }

    /// <summary>부동산형: 땅을 많이 사고 건설도 적극적입니다. 현금을 적게 남기고, 더 좋은 땅을 기다리지 않습니다.</summary>
    public static BotSettings RealEstate()
    {
        return new BotSettings
        {
            Name = "부동산형",
            MaxCashShortProbability = 0.3, // 기본 0.2
            GoodLandReturn = 0.6,          // 기본 0.75
            SafeCashReserve = 100000,      // 기본 200000
            MaxPaybackRounds = 14,         // 기본 10
            MaxMissedChance = 0.25,        // 기본 0.15
        };
    }

    /// <summary>짠돌이: 현금을 많이 남기고 좋은 땅만 삽니다. 오래 버팁니다.</summary>
    public static BotSettings Miser()
    {
        return new BotSettings
        {
            Name = "짠돌이",
            MaxCashShortProbability = 0.12, // 기본 0.2
            GoodLandReturn = 0.85,          // 기본 0.75
            SafeCashReserve = 350000,       // 기본 200000
            MaxPaybackRounds = 7,           // 기본 10
            MaxMissedChance = 0.1,          // 기본 0.15
        };
    }

    /// <summary>
    /// 공격형: 값은 기본형과 같고, 사람 중 꼴찌를 목표로 정해 그 사람이 밟을 땅을 노립니다. (BasicBotStrategy)
    /// 너프 중에는 노리기를 멈추고 값이 약해진 기본형처럼 움직입니다.
    /// </summary>
    public static BotSettings Aggressive()
    {
        return new BotSettings { Name = "공격형", TargetsLastHuman = true };
    }

    // ───────────── 너프 ─────────────

    // 너프 비율. 기본형의 너프 값(0.2 → 0.1, 200000 → 400000, 10 → 6, 0.75 → 0.9, 0.15 → 0.1)에서 가져왔다
    private const double NerfCashShortRatio = 0.5;
    private const long NerfReserveRatio = 2;
    private const double NerfPaybackRatio = 0.6;
    private const double NerfGoodLandAdd = 0.15;
    private const double NerfMissedChanceRatio = 2.0 / 3;
    private const double MaxGoodLandReturn = 1.0; // 좋은 땅 기준의 상한 (너무 높으면 아무 땅도 사지 않는 봇이 된다)

    /// <summary>
    /// 봇 너프(v1.3 순위 보정): 봇이 살아 있는 사람 중 가장 높은 사람보다 앞설 때 쓰는 약한 판단 기준입니다.
    /// 이 성향 값에 비율을 걸어 새 값을 만듭니다. 그래서 너프가 걸려도 성향은 남습니다.
    /// 항상 원래 성향 값에서 한 번만 부릅니다. (너프된 값에 다시 걸면 남길 현금이 2배, 4배로 불어나므로, 이미 너프된 값이면 그대로 돌려줍니다)
    /// </summary>
    public BotSettings Nerfed()
    {
        if (IsNerfed) return this;

        var nerfed = (BotSettings)MemberwiseClone();
        nerfed.Name = Name + " 너프";
        nerfed.IsNerfed = true;
        nerfed.MaxCashShortProbability = Clamp01(MaxCashShortProbability * NerfCashShortRatio);
        nerfed.SafeCashReserve = SafeCashReserve * NerfReserveRatio;
        nerfed.MaxPaybackRounds = MaxPaybackRounds * NerfPaybackRatio;
        nerfed.GoodLandReturn = Math.Min(GoodLandReturn + NerfGoodLandAdd, MaxGoodLandReturn);
        nerfed.MaxMissedChance = Clamp01(MaxMissedChance * NerfMissedChanceRatio);
        return nerfed;
    }

    private static double Clamp01(double value)
    {
        return Math.Max(0, Math.Min(1, value));
    }

    /// <summary>성향에 맞는 설정값을 만듭니다.</summary>
    public static BotSettings For(BotPersonality personality)
    {
        switch (personality)
        {
            case BotPersonality.RealEstate:
                return RealEstate();
            case BotPersonality.Miser:
                return Miser();
            case BotPersonality.Aggressive:
                return Aggressive();
            case BotPersonality.Balanced:
            default:
                return Balanced();
        }
    }
}