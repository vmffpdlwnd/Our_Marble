using System.Collections.Generic;

/// <summary>
/// 순위 보정 후보 로그 (v1.3.2). 1등 패널티와 꼴찌 보너스에서 두 번 굴린 후보와 고른 쪽, 고른 이유를 한 줄로 만든다.
/// "왜 이게 골라졌는지"(다른 후보보다 나아서인지, 같은 결과라 먼저 굴린 쪽인지)를 확인하려는 것이다. (HostGameService.HandleRollDice)
/// 개발자가 보는 로그라 번역하지 않는다. 번역 점검(tools/check_translations.py)에서도 이 파일은 뺀다.
/// 예: 꼴찌 보너스: 파산왕 [1] 4+5 → 9번 무인도 (나쁨) / [2] 1+2 → 3번 서울 남의 땅 (나쁨, 통행료 40,000) → [1] 사용 (같음, 먼저 굴린 쪽)
/// </summary>
public static class BalanceRollLog
{
    /// <param name="title">"1등 패널티" 또는 "꼴찌 보너스"</param>
    /// <param name="leader">1등 패널티면 true. 결과를 비교해 고른 이유를 "더 나쁨"으로, 아니면 "더 좋음"으로 적는다</param>
    public static string Describe(string title, GameState state, GameData data, PlayerState player, RollChoice choice, bool leader)
    {
        string island = !choice.IslandLocked ? ""
            : choice.Rerolls > 0 ? $" 무인도 탈출 굴림(둘 다 더블이라 {choice.Rerolls}번 다시 굴림)" : " 무인도 탈출 굴림";
        return $"{title}: {player.Nickname}{island} [1] {DescribeRoll(state, data, player, choice.First)} / [2] {DescribeRoll(state, data, player, choice.Second)}"
             + $" → [{(choice.PickedFirst ? 1 : 2)}] 사용 ({ReasonText(choice.Reason, leader)})";
    }

    // 한 후보: 눈 → 도착 칸 (등급, 통행료, 월급, 더블)
    private static string DescribeRoll(GameState state, GameData data, PlayerState player, RollPreview roll)
    {
        var notes = new List<string> { roll.Grade == RollGrade.Good ? "좋음" : roll.Grade == RollGrade.Bad ? "나쁨" : "보통" };
        if (roll.Toll > 0) notes.Add($"통행료 {roll.Toll:N0}");
        if (roll.ReceivesSalary) notes.Add("월급");
        if (roll.IsDouble) notes.Add(roll.RollsAgain ? "더블, 한 번 더" : "더블");
        return $"{roll.Dice1}+{roll.Dice2} → {DescribeArrival(state, data, player, roll)} ({string.Join(", ", notes)})";
    }

    // 도착 칸: "번호 이름 종류". 땅은 굴리기 전 주인 기준으로 빈 땅, 내 땅, 남의 땅을 붙인다
    private static string DescribeArrival(GameState state, GameData data, PlayerState player, RollPreview roll)
    {
        if (roll.Kind == DiceMoveKind.IslandStay) return "무인도에 남음";
        if (roll.Kind == DiceMoveKind.DirectToIsland) return $"{roll.To}번 무인도 (3연속 더블)";

        TileData tile = data.GetTile(roll.To);
        if (tile == null) return $"{roll.To}번";

        switch (tile.Type)
        {
            case TileType.PROPERTY:
            {
                string name = PropertyManager.Instance != null ? PropertyManager.Instance.GetData(tile.PropertyId)?.CityName : null;
                PropertyState property = state.PropertyStates.Find(p => p.PropertyId == tile.PropertyId);
                string owner = property == null || !property.OwnerId.HasValue ? "빈 땅"
                             : property.OwnerId.Value == player.PlayerId ? "내 땅" : "남의 땅";
                return string.IsNullOrEmpty(name) ? $"{roll.To}번 {owner}" : $"{roll.To}번 {name} {owner}";
            }
            case TileType.START: return $"{roll.To}번 출발";
            case TileType.GOLDEN_KEY: return $"{roll.To}번 황금열쇠";
            case TileType.ISLAND: return $"{roll.To}번 무인도";
            case TileType.DONATION: return $"{roll.To}번 기부금 납부";
            case TileType.CHARITY: return $"{roll.To}번 기부금 수령";
            case TileType.WORLD_TRAVEL: return $"{roll.To}번 세계여행";
            default: return $"{roll.To}번";
        }
    }

    private static string ReasonText(RollPickReason reason, bool leader)
    {
        switch (reason)
        {
            case RollPickReason.Compared: return leader ? "더 나쁨" : "더 좋음";
            case RollPickReason.SameFirst: return "같음, 먼저 굴린 쪽";
            case RollPickReason.IslandFail: return "탈출 실패 쪽";
            case RollPickReason.IslandEscape: return "탈출하는 쪽";
            case RollPickReason.IslandBothFail: return "둘 다 탈출 실패, 먼저 굴린 쪽";
            case RollPickReason.IslandRerollLimit: return "다시 굴림 한도, 나중 쪽";
            default: return reason.ToString();
        }
    }
}