/// <summary>
/// 클라이언트의 요청을 받아 처리하는 쪽(호스트). 예전에는 서버가 하던 일이다.
/// 요청은 서버로 보낼 때와 같은 JSON 문자열로 들어온다. 받는 쪽이 같은 PC의 호스트든, Photon으로 이어진 방장이든 GameNetwork/LobbyNetwork는 이 모양만 안다.
/// </summary>
public interface IHostRequestReceiver
{
    /// <param name="senderId">요청을 보낸 플레이어의 playerId</param>
    /// <param name="json">요청 JSON. "type" 필드로 종류를 구분한다</param>
    void OnRequest(long senderId, string json);
}