using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 씬 이동을 한곳에서 맡는다. 로그인 → 로비 → 게임 → (같은 방으로) 로비 → 로그인.
/// 씬 경로와 이동 규칙은 여기에만 있고, 각 화면은 To...()만 부른다.
/// 이동에 성공하면 true, 씬 파일이 없거나(에디터) 빌드 설정에 없으면(빌드) false를 돌려준다. 실패했을 때의 안내는 부른 쪽이 정한다.
/// </summary>
public static class SceneFlow
{
    public const string LoginScenePath = "Assets/01.Scenes/Temp/UI_LoginScene.unity";
    public const string LobbyScenePath = "Assets/01.Scenes/Temp/UI_LobbyScene.unity";
    public const string GameScenePath = "Assets/01.Scenes/BoardLayout 2.unity";

    // 게임을 마치고 같은 방으로 돌아오는 중이면 true. 로비 씬이 읽고(ConsumeReopenRoomSetup) 지운다.
    private static bool reopenRoomSetup;

    // 로그인 화면이 열리자마자 토스트로 보여줄 안내. (예: 연결이 끊겨 돌아온 경우) 로그인 씬이 읽고(ConsumeLoginNotice) 지운다.
    private static string pendingLoginNotice;

    // 에디터에서 도메인 리로드 없이 플레이를 다시 시작해도 이전 플레이의 값이 남지 않게 한다
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        reopenRoomSetup = false;
        pendingLoginNotice = null;
    }

    /// <summary>로그인 화면으로 간다. (로비의 나가기, 연결 끊김) notice를 주면 로그인 화면에서 토스트로 띄운다.</summary>
    public static bool ToLogin(string notice = null)
    {
        LocalPlayer.Clear(); // 타이틀로 돌아가면 닉네임을 다시 정한다.
        LocalLobbyHost.Reset(); // 방 정보도 모두 버린다
        LobbyNetwork.Instance.ClearQueue(); // 이 접속에서 쌓인 로비 메시지는 다음 접속에 넘기지 않는다
        pendingLoginNotice = notice;
        if (Load(LoginScenePath, false)) return true;

        pendingLoginNotice = null;
        return false;
    }

    /// <summary>로그인 화면에 띄울 안내를 읽고 지운다. 없으면 null. 로그인 씬이 시작할 때 한 번 부른다.</summary>
    public static string ConsumeLoginNotice()
    {
        string notice = pendingLoginNotice;
        pendingLoginNotice = null;
        return notice;
    }

    /// <summary>로비로 간다. 로그인 성공 후, 또는 게임에서 방을 떠날 때. 방 정보는 로비가 비운다.</summary>
    public static bool ToLobby() => Load(LobbyScenePath, false);

    /// <summary>방 설정 화면에서 게임 씬으로 간다.</summary>
    public static bool ToGame() => Load(GameScenePath, false);

    /// <summary>게임이 끝나 같은 방을 유지한 채 로비로 돌아간다. 로비는 방 설정을 다시 연다.</summary>
    public static bool ReturnToRoom() => Load(LobbyScenePath, true);

    /// <summary>같은 방으로 돌아온 것인지 읽고 지운다. 로비 씬이 시작할 때 한 번 부른다.</summary>
    public static bool ConsumeReopenRoomSetup()
    {
        bool value = reopenRoomSetup;
        reopenRoomSetup = false;
        return value;
    }

    /// <summary>같은 방으로 돌아온 것인지 지우지 않고 본다. (로비의 방 정보를 비울지 정할 때)</summary>
    public static bool IsReturningToRoom => reopenRoomSetup;

    private static bool Load(string path, bool toRoom)
    {
        reopenRoomSetup = toRoom;
        if (TryLoadScene(path)) return true;

        reopenRoomSetup = false;
        Debug.LogError($"[SceneFlow] 씬을 불러올 수 없습니다: {path}");
        return false;
    }

    private static bool TryLoadScene(string path)
    {
        bool inBuild = SceneUtility.GetBuildIndexByScenePath(path) >= 0;
#if UNITY_EDITOR
        if (!inBuild)
        {
            // 빌드 설정에 없는 씬도 에디터 플레이 중에는 경로로 불러온다
            if (!System.IO.File.Exists(path)) return false;
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
                path, new LoadSceneParameters(LoadSceneMode.Single));
            return true;
        }
#else
        if (!inBuild) return false;
#endif
        SceneManager.LoadScene(path);
        return true;
    }
}
