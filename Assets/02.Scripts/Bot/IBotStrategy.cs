using System;
using System.Collections.Generic;

/// <summary>
/// 봇의 결정만 담당합니다. GameState를 읽기만 하고 절대 직접 바꾸지 않습니다.
/// 상태 변경은 항상 GameManager(서버에서는 GameService)가 처리합니다.
/// Unity 기능(MonoBehaviour, Instance 등)을 쓰지 않아서 Java 서버로 그대로 옮길 수 있습니다.
/// </summary>
public interface IBotStrategy
{
    /// <summary>빈 땅을 살지 결정합니다.</summary>
    bool ShouldPurchase(GameState state, long botId, int propertyId, long price);

    /// <summary>내 땅에 다음 단계 건물을 지을지 결정합니다.</summary>
    bool ShouldBuild(GameState state, long botId, int propertyId, long cost);

    /// <summary>통행료를 낸 뒤 남의 땅을 인수할지 결정합니다.</summary>
    bool ShouldAcquire(GameState state, long botId, int propertyId, long price);

    /// <summary>현금이 부족할 때 팔 땅을 고릅니다. 선택한 땅의 propertyId 목록을 돌려줍니다.</summary>
    /// <param name="getSellValue">땅의 매각가 계산 함수 (가격 규칙은 바깥에서 주입)</param>
    List<int> ChooseSellProperties(GameState state, long botId, long requiredAmount,
                                   Func<PropertyState, long> getSellValue);

    /// <summary>세계여행 칸에서 이동할 목적지 칸 번호를 고릅니다.</summary>
    int ChooseTravelDestination(GameState state, long botId);

    /// <summary>통행료 2배 카드로 고를 내 땅을 고릅니다. 따로 고를 이유가 없으면 0 (호출하는 쪽이 평소 규칙으로 고름)</summary>
    int ChooseTollDoubleProperty(GameState state, long botId);
}