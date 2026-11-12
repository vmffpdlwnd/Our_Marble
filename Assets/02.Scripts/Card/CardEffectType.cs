public enum CardEffectType
{
    Bonus,              // 돈 받는 카드
    Penalty,            // 돈 내는 카드
    MoveTo,             // 지정한 칸으로 이동 (예. 본사(출발지)로 이동)
    MoveBy,             // 앞/뒤로 N칸 이동
    GoToIsland,         // 무인도 이동

    // 추후 통행료 면제 또는 매수 방지 카드 등 
}
