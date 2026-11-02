/// <summary>로비 화면의 연결 표시가 보여 줄 상태</summary>
public enum LobbyLink
{
    Online,     // Photon에 연결됨 (서버 연결됨, 초록)
    Connecting, // 로비에 연결하는 중이거나 끊겨서 다시 연결하는 중 (노랑)
    Offline,    // Photon을 쓰지 않음. 이 PC 안의 호스트로 혼자 하기만 됨 (빨강)
}

/// <summary>
/// 로비 화면의 "서버 연결됨" 표시를 정하는 쪽. Photon 연결 상태를 화면이 이해하는 세 가지로 바꿔 준다.
/// (표시 자체는 LobbyManager가 한다)
/// </summary>
public static class LobbyConnectionStatus
{
    /// <summary>
    /// bridgeActive: 게임 로비를 Photon에 연결해 쓰는 중인지. (AppId가 없거나, 연결에 실패해 혼자 하기로 전환됐거나, 테스트 창에서 껐으면 false)
    /// connected: Photon 로비나 방에 연결돼 있는지.
    /// 연결해 쓰는 중인데 아직 연결되지 않았다면 연결하는 중으로 본다. (끊기면 로비 쪽이 스스로 다시 연결한다)
    /// </summary>
    public static LobbyLink Resolve(bool bridgeActive, bool connected)
    {
        if (!bridgeActive) return LobbyLink.Offline;
        return connected ? LobbyLink.Online : LobbyLink.Connecting;
    }

    public static LobbyLink Current
    {
        get
        {
            PhotonConnection photon = PhotonConnection.Instance;
            PhotonConnection.ConnectionState state = photon.Current;
            bool connected = state == PhotonConnection.ConnectionState.InLobby
                || state == PhotonConnection.ConnectionState.StartingSession
                || state == PhotonConnection.ConnectionState.InSession;
            return Resolve(photon.LobbyBridge.IsActive, connected);
        }
    }
}