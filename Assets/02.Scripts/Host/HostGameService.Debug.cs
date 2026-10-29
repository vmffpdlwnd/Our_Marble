/// <summary>
/// HostGameService의 개발용 조회: 호스트와 클라이언트의 상태를 비교하는 HostStateCheck가 읽는다. (나뉜 파일 설명은 HostGameService.cs 참고)
/// 읽기만 하고 상태를 바꾸지 않는다.
/// </summary>
public partial class HostGameService
{
    /// <summary>이 PC에서 진행 중인 게임 호스트. 없으면 null. (다른 PC가 호스트인 참가자라면 null)</summary>
    public static HostGameService Current => current;

    /// <summary>호스트가 가진 게임 상태</summary>
    public GameState DebugState => state;

    /// <summary>호스트가 지금까지 참가자들에게 보낸 진행 알림(주사위, 구매, 건설 등)의 수</summary>
    public int BroadcastCountForDebug => actionSerial;
}