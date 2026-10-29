/// <summary>새 차례를 시작한 결과</summary>
public enum TurnStartResult
{
    AlreadyOver,    // 이미 끝난 게임이라 아무것도 하지 않음
    LastPlayerLeft, // 남은 플레이어가 1명 이하라 게임이 끝남
    RoundLimit,     // 다음 라운드가 최대 라운드를 넘어 게임이 끝남
    Started,        // 차례가 시작됨. WaitingType이 정해져 있다
}

/// <summary>턴을 끝낸 결과</summary>
public enum EndTurnResult
{
    GameOver, // 게임이 끝난 상태라 아무것도 하지 않음
    RollAgain, // 더블이라 같은 사람이 한 번 더 굴림. WaitingType이 정해져 있다
    NextTurn, // 다음 사람에게 차례를 넘겨야 함
}

/// <summary>주사위 결과로 어떻게 되는지</summary>
public enum DiceMoveKind
{
    IslandStay,     // 무인도에서 탈출 실패. 이동 없이 턴이 넘어감
    DirectToIsland, // 3연속 더블로 무인도로 바로 이동 (월급 없음)
    Move,           // 주사위 수만큼 이동
}

/// <summary>주사위 결과 규칙이 돌려주는 값. 상태(더블 횟수, 무인도 남은 턴 등)는 규칙이 이미 바꿔 두었다.</summary>
public class DiceOutcome
{
    public DiceMoveKind Kind;
    public int To;                   // 이동할 칸 (DirectToIsland, Move)
    public bool ReceivesSalary;      // 출발 지점을 지나 월급을 받는지 (Move)
    public bool EscapedIsland;       // 더블로 무인도에서 탈출했는지
    public int IslandTurnsRemaining; // 탈출에 실패한 뒤 남은 영업정지 턴 (IslandStay)
}