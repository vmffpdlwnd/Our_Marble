using System.Collections.Generic;

/// <summary>
/// 플레이어 순위 계산의 입구. 규칙은 RankingRules에 있고, 여기서는 게임 씬의 PropertyManager가 가진 PropertyRules를 넘겨 부른다.
/// 순위 기준은 RankingRules 참고.
/// </summary>
public static class PlayerRanking
{
    /// <summary>두 플레이어의 순위를 비교한다. a가 앞이면 음수, b가 앞이면 양수, 같으면 0.</summary>
    public static int Compare(PlayerState a, PlayerState b, List<PropertyState> propertyStates)
    {
        return RankingRules.Compare(a, b, propertyStates, PropertyManager.Instance.Rules);
    }

    /// <summary>순위 순으로 정렬한 새 목록을 돌려준다. (원래 목록은 바꾸지 않음, 완전히 같으면 원래 순서 유지)</summary>
    public static List<PlayerState> Rank(List<PlayerState> players, List<PropertyState> propertyStates)
    {
        return RankingRules.Rank(players, propertyStates, PropertyManager.Instance.Rules);
    }
}