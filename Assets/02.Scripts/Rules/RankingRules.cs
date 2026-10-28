using System.Collections.Generic;

/// <summary>
/// 플레이어 순위 계산 규칙. 총자산 계산은 받은 PropertyRules를 쓴다. 화면을 모른다.
/// GameManager(게임 종료 로그), UIManager(등수, 결과 창), 호스트가 PlayerRanking을 거쳐 같은 기준을 쓴다.
/// 순위 기준
///   1. 파산하지 않은 플레이어가 앞
///   2. 둘 다 파산했으면 FinalRank가 작은 쪽이 앞 (나중에 파산한 쪽)
///   3. 총자산(현금 + 가진 땅의 투자금)이 많은 순
///   4. 총자산이 같으면 현금이 많은 순
///   5. 그래도 같으면 원래 순서(PlayerStates 순서) 유지
/// </summary>
public static class RankingRules
{
    /// <summary>
    /// 두 플레이어의 순위를 비교한다. a가 앞이면 음수, b가 앞이면 양수, 같으면 0.
    /// 파산 여부와 파산 등수는 GameState(PlayerState)에서 읽는다.
    /// </summary>
    public static int Compare(PlayerState a, PlayerState b, List<PropertyState> propertyStates, PropertyRules propertyRules)
    {
        if (a.IsBankrupt != b.IsBankrupt) return a.IsBankrupt ? 1 : -1;

        // 둘 다 파산: 나중에 파산한 쪽(FinalRank가 작은 쪽)이 앞. (파산하면 ProcessBankruptcy에서 FinalRank가 바로 정해짐)
        if (a.IsBankrupt)
            return a.FinalRank.CompareTo(b.FinalRank);

        long assetA = propertyRules.GetTotalAsset(a.PlayerId, a.Money, propertyStates);
        long assetB = propertyRules.GetTotalAsset(b.PlayerId, b.Money, propertyStates);
        if (assetA != assetB) return assetB.CompareTo(assetA);

        return b.Money.CompareTo(a.Money);
    }

    /// <summary>순위 순으로 정렬한 새 목록을 돌려준다. (원래 목록은 바꾸지 않음, 완전히 같으면 원래 순서 유지)</summary>
    public static List<PlayerState> Rank(List<PlayerState> players, List<PropertyState> propertyStates, PropertyRules propertyRules)
    {
        var order = new List<int>();
        for (int i = 0; i < players.Count; i++) order.Add(i);

        order.Sort((x, y) =>
        {
            int result = Compare(players[x], players[y], propertyStates, propertyRules);
            return result != 0 ? result : x.CompareTo(y);
        });

        var ranked = new List<PlayerState>();
        foreach (int index in order) ranked.Add(players[index]);
        return ranked;
    }
}