using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 로비 화면에 들어가기 전에 Photon 연결을 스스로 켠다. (F2 창의 "게임 로비 연결 켜기"를 사람이 누르지 않아도 되게 한다)
///
///   AppId가 없으면      켜지 않는다. 혼자 하기(봇과)만 된다.
///   로비 화면이 뜨면    Photon 연결을 켠다. (로비 화면이 첫 방 목록 요청을 보내기 전에 켜진다)
///   타이틀 화면이 뜨면  "오프라인으로 전환됨" 표시를 풀어서, 다음 입장 때 다시 온라인을 시도한다.
///
/// Photon에 연결하지 못하면 PhotonLobbyBridge가 스스로 혼자 하기로 전환한다. (전환되면 이번 접속 동안은 다시 시도하지 않는다)
/// 개발할 때 F2 창에서 연결을 끄면 그 뒤로는 자동으로 켜지 않는다.
/// </summary>
public static class PhotonAutoStart
{
    private static bool noAppIdLogged;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Init()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single); // 첫 씬은 이미 떠 있어서 한 번 직접 확인한다
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.path == SceneFlow.LoginScenePath)
        {
            // 타이틀로 돌아오면 방과 연결을 정리하고, 다음 입장 때 다시 온라인을 시도한다
            PhotonConnection connection = PhotonConnection.Instance;
            connection.OfflineMode = false;
            _ = connection.LobbyBridge.LeaveForLogin();
            return;
        }

        if (scene.path == SceneFlow.LobbyScenePath) EnsureOnline();
    }

    /// <summary>Photon 연결이 켜져 있게 한다. 켤 수 없거나 켜면 안 되는 때는 아무것도 하지 않는다.</summary>
    public static void EnsureOnline()
    {
        PhotonConnection connection = PhotonConnection.Instance;
        if (connection.LobbyBridge.IsActive) return;
        if (connection.OfflineMode || connection.AutoStartDisabled) return;

        if (!connection.HasAppId)
        {
            if (!noAppIdLogged)
            {
                noAppIdLogged = true;
                connection.WriteLog("Photon AppId가 없어 온라인을 켜지 않습니다. 혼자 하기만 됩니다");
            }
            return;
        }

        connection.LobbyBridge.Enable();

        // PlayFab 로그인을 미리 해 둔다. 방 목록 요청이 올 때쯤이면 끝나 있어서 Photon 연결이 그만큼 빨라진다.
        // (실패하면 연결을 시도할 때 다시 로그인한다)
        _ = connection.Identity.EnsureLoggedIn();
    }
}