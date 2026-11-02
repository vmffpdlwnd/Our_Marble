/// <summary>
/// 전적을 기록하고 보여 주는 글자를 만드는 짧은 도우미. 화면(UIManager, LobbyManager)이 부른다.
/// </summary>
public static class GameRecord
{
    /// <summary>
    /// 게임 결과를 기록한다. 결과 창이 뜰 때 한 번 부른다.
    /// 게임 로비를 Photon에 연결해 쓰는 중(PlayFab에 로그인된 상태)일 때만 기록하고, 혼자 하기에서는 아무것도 하지 않는다.
    /// </summary>
    public static void Report(bool won)
    {
        PhotonConnection photon = PhotonConnection.Instance;
        if (!photon.LobbyBridge.IsActive) return;

        _ = photon.Profile.ReportResultAsync(won, photon.GameBridge.GameSerial);
    }

    /// <summary>
    /// 로비에 보여 줄 전적 글자. 옛 로비와 같은 모양이다. 예: 28승 20패 (58%)
    /// 패는 플레이 횟수에서 승리 횟수를 뺀 값이다.
    /// </summary>
    public static string Describe(int games, int wins)
    {
        if (games < 0) games = 0;
        if (wins < 0) wins = 0;
        if (wins > games) wins = games;

        int losses = games - wins;
        int rate = games == 0 ? 0 : (int)System.Math.Round(wins * 100.0 / games, System.MidpointRounding.AwayFromZero);
        return $"{wins}승 {losses}패 <size=80%><color=#8C90A8>({rate}%)</color></size>";
    }
}