using System;
using System.Collections.Generic;

/// <summary>
/// 부동산 계산 규칙: 땅값, 건설비, 통행료, 투자금, 매각가, 인수가, 총자산, 매각할 땅 자동 선택.
/// 화면(Unity 오브젝트)을 모른다. 땅 가격표는 만들 때 받은 조회 함수로 읽는다.
/// 호스트(HostGameService)와 클라이언트(GameManager)가 같은 계산을 쓰도록, PropertyManager가 이 클래스를 불러 쓴다.
/// </summary>
public class PropertyRules
{
    private const float SellRate = 0.5f; //매각가 = 투자금의 50%로 계산. 추후 밸런스 조정 필요.
    private const float AcquireRate = 2f; //인수가 = 투자금의 200%로 계산. 추후 밸런스 조정 필요.

    private readonly Func<int, PropertyData> getData;
    private readonly Action<string> logWarning;

    /// <param name="getData">땅 번호로 가격표를 찾는다. 없으면 null</param>
    /// <param name="logWarning">경고를 남기는 함수. 없으면 남기지 않는다</param>
    public PropertyRules(Func<int, PropertyData> getData, Action<string> logWarning = null)
    {
        this.getData = getData;
        this.logWarning = logWarning;
    }

    /// <summary>땅 번호로 가격표를 찾는다. 없으면 null.</summary>
    public PropertyData GetData(int id) => getData(id);

    // ───────────── 부동산 계산 ─────────────

    /// <summary>땅값. PurchaseProperty의 amount.</summary>
    public long GetLandPrice(int propertyId)
    {
        var data = GetData(propertyId);
        return data != null ? data.LandPrice : 0;
    }

    public long GetBuildCost(int propertyId, BuildingLevel level)
    {
        var data = GetData(propertyId);
        
        long cost = data != null ?  data.GetBuildCost(level) : 0;

        return cost;
    }

    /// <summary>통행료. 주인 없는 땅이면 0.</summary>
    public long GetToll(PropertyState state)
    {
        if (!state.OwnerId.HasValue) return 0;

        var data = GetData(state.PropertyId);
        if (data == null) return 0;

        return data.GetToll(state.BuildingLevel);
    }

    /// <summary>
    /// 투자금 = 땅값 + 지금 단계까지 지은 건물 비용 합계.
    /// 매각가(GetSellValue)와 인수가(GetAcquireValue) 계산에 사용됨.
    /// </summary>
    /// 투자금. 가격표가 없는 땅이면 0.
    public long GetInvestedAmount(PropertyState state)
    {
        var data = GetData(state.PropertyId);
        if (data == null) return 0;

        long invested = data.LandPrice;
        for (int lv = 1; lv <= (int)state.BuildingLevel; lv++)
        {
            invested += data.GetBuildCost((BuildingLevel)lv);
        }
        return invested;
    }

    ///<summary>땅 하나의 매각가: 투자금 x SellRate(50%).</summary>
    /// 사용처
    ///   - SellProperties: 선택한 땅의 매각가 합계 계산
    ///   - GetTotalSellValue: 가진 땅 전부의 매각가 합계
    ///   - 파산 처리: 모든 재산 현금화 (매각과 같은 기준이어야 분기 판단과 실제 금액이 일치)
    /// 
    /// 매각가. 가격표가 없는 땅이면 0.
    public long GetSellValue(PropertyState state)
    {
        return (long)(GetInvestedAmount(state) * SellRate);
    }
    
    /// <summary>
    /// 총자산 = 현금 + 가진 땅의 투자금(땅값 + 지은 건물 비용). 매각가가 아니라 투자금 기준이다.
    /// 사용처: 순위 계산(PlayerRanking), UI 총자산 표시, 게임 결과
    /// </summary>
    public long GetTotalAsset(long playerId, long cash, List<PropertyState> propertyStates)
    {
        long total = cash;
        foreach (var state in propertyStates)
        {
            if (state.OwnerId == playerId) total += GetInvestedAmount(state);
        }
        return total;
    }

    /// <summary>플레이어가 가진 땅을 전부 팔면 받는 금액의 합계</summary>
    /// 사용처
    ///   - ProcessArrival: 통행료를 현금으로 못 낼 때 분기 판단
    ///       현금 + GetTotalSellValue ≥ 통행료 → 매각 (HandleSellPropertiesPrompt)
    ///       현금 + GetTotalSellValue < 통행료 → 파산
    /// 실제로 땅을 팔지는 않고 금액만 계산한다.
    /// 
    /// 매각가 합계. 가진 땅이 없으면 0.
    public long GetTotalSellValue(long playerId, List<PropertyState> propertyStates)
    {
        long total = 0;
        foreach (var state in propertyStates)
        {
            if (state.OwnerId == playerId)  total += GetSellValue(state);
        }
        return total;
    }

    /// <summary>땅 하나의 인수가: 투자금 x AcquireRate(200%).</summary>
    /// 사용처
    ///   - ProcessArrival: 통행료를 낸 뒤 인수할 돈이 충분한지 판단
    ///   - HandleAcquirePropertyPrompt: 인수 팝업에 보여줄 금액
    ///   - AcquireProperty: 실제로 차감할 금액
    /// 
    /// 인수가. 가격표가 없는 땅이면 0.
    public long GetAcquireValue(PropertyState state)
    {
        return (long)(GetInvestedAmount(state) * AcquireRate);
    }

    #region 매각 자동 선택 (유저 매각 창)

    /// <summary>
    /// 부족분(shortage)을 채울 땅을 골라 propertyId 목록으로 돌려줍니다. 고르기만 하고 실제 매각은 하지 않습니다.
    /// 수익 지키기(ProtectIncome)는 상대 위치 계산이 필요해서 GameManager가 봇 전략으로 처리합니다.
    /// </summary>
    public List<int> AutoChooseSellProperties(long ownerId, long shortage,
                                              List<PropertyState> propertyStates, SellSelectionMode mode)
    {
        List<PropertyState> owned = propertyStates.FindAll(p => p.OwnerId == ownerId);
        if (shortage <= 0 || owned.Count == 0) return new List<int>();

        List<PropertyState> selected;
        switch (mode)
        {
            case SellSelectionMode.CheapestFirst:
                selected = TakeUntilCovered(SortBySellValue(owned, cheapestFirst: true), shortage);
                break;

            case SellSelectionMode.MostExpensiveFirst:
                selected = TakeUntilCovered(SortBySellValue(owned, cheapestFirst: false), shortage);
                break;

            case SellSelectionMode.FewestProperties:
                selected = FindFewestCombination(owned, shortage);
                break;

            case SellSelectionMode.ProtectTourist:
                selected = ChooseProtectingTourist(owned, shortage);
                break;

            default:
                logWarning?.Invoke($"[PropertyManager] {mode}는 GameManager에서 처리합니다. 싼 땅부터로 대신 고릅니다.");
                selected = TakeUntilCovered(SortBySellValue(owned, cheapestFirst: true), shortage);
                break;
        }

        return selected.ConvertAll(p => p.PropertyId);
    }

    /// <summary>
    /// 매각가 기준으로 정렬한 새 목록을 돌려줍니다.
    /// </summary>
    private List<PropertyState> SortBySellValue(List<PropertyState> properties, bool cheapestFirst)
    {
        List<PropertyState> sorted = new List<PropertyState>(properties);
        sorted.Sort((a, b) => cheapestFirst
            ? GetSellValue(a).CompareTo(GetSellValue(b))
            : GetSellValue(b).CompareTo(GetSellValue(a)));
        return sorted;
    }

    /// <summary>
    /// 정렬된 순서대로 담다가 매각가 합이 부족분 이상이 되면 멈춥니다.
    /// </summary>
    private List<PropertyState> TakeUntilCovered(List<PropertyState> ordered, long shortage)
    {
        List<PropertyState> selected = new List<PropertyState>();
        long sum = 0;
        foreach (PropertyState p in ordered)
        {
            if (sum >= shortage) break;
            selected.Add(p);
            sum += GetSellValue(p);
        }
        return selected;
    }

    /// <summary>
    /// 관광지 지키기: 관광지(건설 불가 땅)가 아닌 땅을 싼 것부터 팔고, 그래도 부족하면 관광지를 싼 것부터 추가합니다.
    /// </summary>
    private List<PropertyState> ChooseProtectingTourist(List<PropertyState> owned, long shortage)
    {
        List<PropertyState> normal = owned.FindAll(p => !IsTourist(p));
        List<PropertyState> tourist = owned.FindAll(p => IsTourist(p));

        List<PropertyState> ordered = SortBySellValue(normal, cheapestFirst: true);
        ordered.AddRange(SortBySellValue(tourist, cheapestFirst: true));
        return TakeUntilCovered(ordered, shortage);
    }

    private bool IsTourist(PropertyState property)
    {
        PropertyData data = GetData(property.PropertyId);
        return data != null && !data.CanBuild;
    }

    /// <summary>
    /// 적게 팔기: 부족분을 채우는 조합 중 땅 개수가 가장 적은 조합을 찾고, 개수가 같으면 매각가 합이 가장 작은 조합을 고릅니다.
    /// 1) 비싼 땅부터 담아서 필요한 최소 개수(k)를 구하고
    /// 2) k개짜리 조합만 살펴서 합이 가장 작은 조합을 찾습니다. (가망 없는 조합은 중간에 잘라냄)
    /// </summary>
    private List<PropertyState> FindFewestCombination(List<PropertyState> owned, long shortage)
    {
        List<PropertyState> sorted = SortBySellValue(owned, cheapestFirst: false);
        long[] values = sorted.ConvertAll(p => GetSellValue(p)).ToArray();

        // prefix[i] = 앞에서부터 i개의 매각가 합 (비싼 순이라 "남은 자리를 가장 비싸게 채운 합" 계산에 사용)
        long[] prefix = new long[values.Length + 1];
        for (int i = 0; i < values.Length; i++)
            prefix[i + 1] = prefix[i] + values[i];

        // 1) 최소 개수 k
        int k = 0;
        while (k < values.Length && prefix[k] < shortage) k++;
        if (prefix[k] < shortage) return sorted; // 전부 팔아도 부족하면 전부 (정상 흐름에서는 매각 창이 뜨지 않음)

        // 2) k개 조합 중 합이 부족분 이상이면서 가장 작은 조합
        List<int> best = new List<int>();
        for (int i = 0; i < k; i++) best.Add(i);
        long bestSum = prefix[k];
        List<int> current = new List<int>();

        void Search(int index, long sum)
        {
            int slotsLeft = k - current.Count;
            if (slotsLeft == 0)
            {
                if (sum >= shortage && sum < bestSum)
                {
                    bestSum = sum;
                    best = new List<int>(current);
                }
                return;
            }
            if (values.Length - index < slotsLeft) return;                       // 남은 땅이 자리보다 적음
            if (sum >= bestSum) return;                                           // 더 담으면 합만 커짐
            if (sum + (prefix[index + slotsLeft] - prefix[index]) < shortage) return; // 남은 자리를 가장 비싸게 채워도 부족

            current.Add(index);                       // 이 땅을 파는 경우
            Search(index + 1, sum + values[index]);
            current.RemoveAt(current.Count - 1);

            Search(index + 1, sum);                   // 이 땅을 안 파는 경우
        }

        Search(0, 0);
        return best.ConvertAll(i => sorted[i]);
    }

    #endregion
}