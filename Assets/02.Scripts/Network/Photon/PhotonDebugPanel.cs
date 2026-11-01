#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using Fusion;
using UnityEngine;

/// <summary>
/// [임시] Photon 접속 확인용 테스트 창. 에디터와 개발 빌드(Development Build)에서만 만들어지고, F2로 열고 닫는다.
/// 로비 화면을 건드리지 않고 연결(로비 입장, 방 만들기, 방 목록, 입장, 방장 확인, 나가기)이 되는지 보려고 둔 것이다.
/// 일반 빌드에는 들어가지 않는다.
/// </summary>
public class PhotonDebugPanel : MonoBehaviour
{
    private const int MaxLogLines = 40;

    private bool visible;
    private Rect windowRect = new Rect(12, 12, 480, 10);
    private string roomName = "테스트방";
    private string maxHumansText = "4";
    private Vector2 listScroll;
    private Vector2 logScroll;
    private readonly List<string> logs = new List<string>();

    // 첫 씬이 뜬 뒤 한 번만 스스로 만든다. 씬에 따로 붙일 필요가 없다.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        var go = new GameObject("PhotonDebugPanel");
        DontDestroyOnLoad(go);
        go.AddComponent<PhotonDebugPanel>();
    }

    private void Start()
    {
        PhotonConnection.Instance.LogAdded += AddLog;
    }

    private void OnDestroy()
    {
        // Instance(getter)는 없으면 새로 만들어 버리므로, 정리할 때는 건드리지 않는다
    }

    private void AddLog(string message)
    {
        logs.Add($"{Time.frameCount} {message}");
        if (logs.Count > MaxLogLines) logs.RemoveAt(0);
        logScroll.y = float.MaxValue;
    }

    private void OnGUI()
    {
        Event e = Event.current;
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.F2) { visible = !visible; e.Use(); }
        if (!visible) return;

        // 1080p 기준으로 글자 크기를 맞춘다
        float s = Mathf.Max(1f, Screen.height / 1080f * 1.25f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));

        windowRect.height = 10; // 내용에 맞춰 늘어난다
        windowRect = GUILayout.Window(98232, windowRect, DrawWindow, "Photon 테스트 (F2 닫기)");
    }

    private void DrawWindow(int id)
    {
        PhotonConnection conn = PhotonConnection.Instance;

        GUILayout.Label($"AppId: {(conn.HasAppId ? "있음" : "없음 (Fusion Hub에서 입력)")}   지역: {PhotonConnection.Region}   버전: {PhotonConnection.NetworkVersion}");
        GUILayout.Label($"상태: {conn.Current}");

        GUILayout.BeginHorizontal();
        GUI.enabled = conn.Current == PhotonConnection.ConnectionState.Disconnected || conn.Current == PhotonConnection.ConnectionState.InLobby;
        if (GUILayout.Button(conn.Current == PhotonConnection.ConnectionState.InLobby ? "로비 다시 입장" : "로비 입장"))
            _ = conn.JoinLobby();
        GUI.enabled = conn.Current != PhotonConnection.ConnectionState.Disconnected;
        if (GUILayout.Button("연결 끊기"))
            _ = conn.Disconnect();
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (conn.Current == PhotonConnection.ConnectionState.InLobby) DrawLobby(conn);
        if (conn.Current == PhotonConnection.ConnectionState.InSession) DrawSession(conn);

        GUILayout.Space(6);
        GUILayout.Label("로그");
        logScroll = GUILayout.BeginScrollView(logScroll, GUILayout.Height(150));
        foreach (string line in logs) GUILayout.Label(line);
        GUILayout.EndScrollView();

        GUI.DragWindow();
    }

    // 로비: 방 만들기와 방 목록
    private void DrawLobby(PhotonConnection conn)
    {
        GUILayout.Space(6);
        GUILayout.Label("방 만들기");
        GUILayout.BeginHorizontal();
        GUILayout.Label("이름", GUILayout.Width(40));
        roomName = GUILayout.TextField(roomName);
        GUILayout.Label("최대 사람", GUILayout.Width(70));
        maxHumansText = GUILayout.TextField(maxHumansText, GUILayout.Width(40));
        if (GUILayout.Button("만들기", GUILayout.Width(80)))
        {
            int.TryParse(maxHumansText, out int maxHumans);
            _ = conn.CreateRoom(roomName, maxHumans);
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        GUILayout.Label($"방 목록 ({conn.Sessions.Count}개)");
        listScroll = GUILayout.BeginScrollView(listScroll, GUILayout.Height(120));
        foreach (SessionInfo info in conn.Sessions)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{DisplayName(info)}   {info.PlayerCount}/{info.MaxPlayers}   {(info.IsOpen ? "열림" : "닫힘")}   [{info.Region}]");

            GUI.enabled = info.IsOpen && info.PlayerCount < info.MaxPlayers;
            if (GUILayout.Button("입장", GUILayout.Width(70)))
                _ = conn.JoinRoom(info.Name);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
    }

    // 방 안: 접속자, 방장, 방 열기와 닫기, 나가기
    private void DrawSession(PhotonConnection conn)
    {
        GUILayout.Space(6);
        GUILayout.Label($"방: {conn.SessionName}   나: {conn.LocalPlayerRef.PlayerId}번   {(conn.IsMaster ? "내가 방장" : "방장 아님")}");

        GUILayout.Label("접속자 (봇은 Photon 접속자가 아니라서 여기에 없음)");
        foreach (PlayerRef player in conn.Players)
        {
            string me = player == conn.LocalPlayerRef ? " (나)" : "";
            string master = player == conn.MasterPlayerRef ? " (방장)" : "";
            GUILayout.Label($"   {player.PlayerId}번{me}{master}");
        }

        GUILayout.BeginHorizontal();
        GUI.enabled = conn.IsMaster;
        if (GUILayout.Button("방 닫기 (새로 못 들어옴)")) conn.SetRoomOpen(false);
        if (GUILayout.Button("방 열기")) conn.SetRoomOpen(true);
        GUI.enabled = true;
        if (GUILayout.Button("방 나가기 (로비로)")) LeaveToLobby(conn);
        GUILayout.EndHorizontal();
    }

    private async void LeaveToLobby(PhotonConnection conn)
    {
        await conn.Disconnect();
        await conn.JoinLobby();
    }

    // 방 목록에 보일 이름: 만들 때 넣은 name 속성, 없으면 방 번호
    private static string DisplayName(SessionInfo info)
    {
        if (info.Properties != null && info.Properties.TryGetValue("name", out SessionProperty property) && property.IsString)
            return (string)property;
        return info.Name;
    }
}
#endif