public enum CardEffectType
{
    Bonus,              // 돈 받는 카드
    Penalty,            // 돈 내는 카드
    MoveTo,             // 지정한 칸으로 이동 (예. 본사(출발지)로 이동)
    MoveBy,             // 앞/뒤로 N칸 이동
    GoToIsland,         // 무인도 이동
    CollectFromAll,     // 다른 플레이어 모두에게서 amount씩 받기 (각자 현금 한도 안에서)
    PayToAll,           // 다른 플레이어 모두에게 amount씩 주기 (내 현금 한도 안에서)
    RepairCost,         // 내 건물 수 × amount 내기 (현금 한도 안에서, 은행 또는 적립금으로)

    // 추후 통행료 면제 또는 매수 방지 카드 등 
}