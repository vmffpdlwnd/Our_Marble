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
    SwapPosition,       // 무작위 상대 1명과 위치만 바꾸기 (칸 효과 없음, 무인도만 예외)
    TollDouble,         // 내 땅 하나를 골라 다음 통행료 한 번을 2배로 받기

    // ── 보관형: 뽑으면 들고 있다가(PlayerState.HeldCardIds) 쓰는 때가 오면 쓴다. 모두 1회용, 종류마다 1장 ──
    // 효과는 순서 번호로 저장되므로 새 효과는 항상 맨 뒤에 추가한다
    TollFree,           // 통행료 면제권: 남의 땅 통행료 0원
    TollHalf,           // 통행료 반값권: 남의 땅 통행료 절반
    IslandEscape,       // 무인도 탈출권: 무인도에서 내 차례가 시작될 때 바로 풀려난다
    TakeoverShield,     // 인수 방지권: 남이 내 땅을 인수하려 할 때 막는다
    BuildDiscount,      // 건설 할인권: 건설비 할인
    SalaryDouble,       // 월급 2배: 월급을 받을 때 자동으로 2배
}