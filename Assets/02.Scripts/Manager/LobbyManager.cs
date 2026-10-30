using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static UIPalette;

/// <summary>
/// 로비 씬 매니저. 서버(LobbyNetwork)가 보내 주는 RoomSummary / RoomSnapshot으로 화면을 그린다.
/// - 방 목록: RoomSummary 목록 (OnRoomList)
/// - 방 안(방 설정 화면): RoomSnapshot (OnRoomEntered + OnRoomUpdated)
/// 클라이언트는 요청만 보내고, 화면은 서버 응답이 올 때만 바꾼다.
/// 방장이 시작하면(OnGameStarted) 게임 씬으로 넘어가고, 게임이 끝나면 같은 방으로 돌아와 방 설정을 다시 연다. (씬 이동은 SceneFlow가 맡는다)
/// UI_LobbyScene의 "UI" 루트를 찾아 이름으로 연결한다.
/// </summary>
public class LobbyManager : MonoBehaviour
{
    // ───────────── 상수 ─────────────

    // mapId → 맵 이름. 서버의 mapId와 같은 순서여야 한다
    private static readonly string[] Maps = { "클래식 월드", "도시 탐험", "해변 마을" };

    // 지금 고를 수 있는 맵 (Maps와 같은 순서). 아직 만들지 않은 맵은 false로 두면 어느 화면에서도 선택되지 않는다.
    private static readonly bool[] MapAvailable = { false, true, false };

    private static int FirstAvailableMap()
    {
        for (int i = 0; i < MapAvailable.Length; i++) if (MapAvailable[i]) return i;
        return 0;
    }

    // from에서 step(+1/-1) 방향으로 고를 수 있는 다음 맵. 고를 수 있는 맵이 하나뿐이면 그 맵.
    private static int NextAvailableMap(int from, int step)
    {
        for (int k = 1; k <= Maps.Length; k++)
        {
            int i = ((from + step * k) % Maps.Length + Maps.Length) % Maps.Length;
            if (MapAvailable[i]) return i;
        }
        return from;
    }

    private static int AvailableMapCount()
    {
        int n = 0;
        foreach (bool a in MapAvailable) if (a) n++;
        return n;
    }

    private static readonly Color RowStroke = new Color32(220, 225, 236, 255);
    private static readonly Color SoloRowBg = new Color32(240, 250, 245, 255);
    private static readonly Color SoloRowStroke = new Color32(196, 232, 214, 255);
    private static readonly Color SoloRowStrokeOn = new Color32(92, 201, 154, 255);
    private static readonly Color DotEmpty = new Color32(226, 229, 238, 255);

    [Header("연결")]
    [SerializeField] private Transform uiRoot;

    [Tooltip("선택한 방 행의 두꺼운 테두리 (UI_RoundedStroke_Thick).")]
    [SerializeField] private Sprite selectedStrokeSprite;

    [Tooltip("맵 초상화. mapId 순서(클래식 월드, 도시 탐험, 해변 마을).")]
    [SerializeField] private Sprite[] mapSprites = new Sprite[3];

    [Header("서버")]
    [Tooltip("요청을 보낸 뒤 이 시간(초) 안에 응답이 없으면 로딩을 닫고 알린다.")]
    [SerializeField] private float requestTimeout = 10f;
    [Tooltip("방 목록 새로고침 버튼을 누른 뒤 다시 누를 수 있기까지 기다리는 시간(초).")]
    [SerializeField] private float refreshCooldown = 10f;

    private static LobbyNetwork Net => LobbyNetwork.Instance;

    private static long MyPlayerId => LocalPlayer.Id;

    // ───────────── 상태 ─────────────

    private enum ConnectionState { Connected, Reconnecting, Disconnected }
    private ConnectionState connection = ConnectionState.Connected;

    // 응답을 기다리는 요청. 기다리는 동안 다른 요청과 입력을 막는다
    private enum Pending { None, RoomList, Enter, Leave, Start, Config }
    private Pending pending;
    private Coroutine timeoutRoutine;
    private bool refreshToast; // 사용자가 새로고침을 눌러 받은 목록이면 토스트를 띄운다

    // 새로고침 쿨타임: 이 시각이 지나야 다시 누를 수 있다. 기다리는 동안 버튼에 남은 시간을 보여 준다
    private float refreshReadyAt;
    private int shownCooldown = -1; // 버튼에 지금 보이는 남은 초 (0이면 평소 글자)
    private TMP_Text refreshLabel;
    private bool Busy => pending != Pending.None;
    private bool CanRequest => !Busy && connection == ConnectionState.Connected;

    private readonly List<RoomSummary> rooms = new List<RoomSummary>();
    private readonly Dictionary<string, RowView> rows = new Dictionary<string, RowView>();
    private string selectedId;
    private RoomSummary Selected => selectedId == null ? null : rooms.FirstOrDefault(r => r.roomId == selectedId);

    // OnRoomEntered를 받았고 아직 OnRoomLeft를 받지 않았다
    private bool inRoom;
    // 스냅샷을 방 설정 화면에 적용하는 중이면 true. 그 사이 화면이 쏘는 이벤트를 서버로 다시 보내지 않는다
    private bool applyingSnapshot;

    // ───────────── 방 정보 (방 설정과 게임 씬이 함께 읽는다) ─────────────

    /// <summary>지금 들어가 있는 방의 최신 스냅샷. 로비를 거치지 않고 게임 씬만 실행하면 null.</summary>
    public static RoomSnapshot CurrentRoom { get; private set; }

    /// <summary>
    /// GAME_STARTED로 들어간 방의 id. 재접속으로 게임에 바로 들어가면 방 스냅샷(CurrentRoom)이 없으므로,
    /// 게임이 끝나 돌아왔을 때 이 id로 "방 안에 있다"는 것을 알고 서버의 ROOM_UPDATED를 받아 대기방을 연다.
    /// </summary>
    private static string PlayingRoomId;

    /// <summary>방 안에 있는지. 방 정보(CurrentRoom)를 받았거나, 재접속으로 게임에 바로 들어와 방 번호만 아는 경우도 true.</summary>
    public static bool HasRoom => CurrentRoom != null || PlayingRoomId != null;

    // 게임을 마치고 돌아왔는데 방 스냅샷이 없을 때(재접속 후 게임), 서버의 ROOM_UPDATED를 기다리는 최대 시간(초)
    private const float RoomSnapshotWaitSeconds = 5f;

    // 에디터에서 도메인 리로드 없이 플레이를 다시 시작해도 이전 플레이의 방 정보가 남지 않게 한다
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        CurrentRoom = null;
        PlayingRoomId = null;
    }

    // ───────────── UI 참조 ─────────────

    private class RowView
    {
        public GameObject Go;
        public Button Row, Join;
        public GameObject Lock;
        public Image Bg, Stroke, Thumb, ThumbStroke, StatusChip, JoinImage;
        public TMP_Text Number, Name, Info, StatusText, Count, JoinLabel;
        public Image[] Dots;
    }

    private class SlotView
    {
        public GameObject Go;
        public Image Bg, Stroke, Portrait, Ring;
        public GameObject Host;
        public TMP_Text Nick;
    }

    // 상단바 / 방 목록 위 버튼
    private Button settingsButton, exitButton, refreshButton;

    // 방 설정 화면
    private ExitConfirmView exitConfirm;
    private RoomSetupView roomSetup;

    // 방 목록
    private Toggle joinableOnlyToggle;
    private TMP_Text joinableOnlyLabel;
    private RectTransform rowContent;
    private GameObject rowTemplate;
    private Sprite normalStrokeSprite;

    // 오른쪽 열
    private GameObject profileCard, playerListCard, detailCard;
    private TMP_Text nicknameText, marbleText;
    private Button profilePortraitButton;
    private Image profilePortraitImage;
    private PortraitPickerView portraitPicker;
    private Button quickStartButton, createRoomButton;
    private GameObject detailLockChip;
    private TMP_Text detailTitle, detailStatusText, detailMap, detailCount;
    private Image detailStatusChip;
    private Button detailClose, detailJoin;
    private Image detailJoinImage;
    private TMP_Text detailJoinLabel;
    private GameObject detailProgress;
    private readonly SlotView[] slots = new SlotView[4];

    // 방 만들기 팝업
    private PopupView createPopup;
    private TMP_Text mapNameText;
    private Image mapPreviewImage;
    private Button prevMapButton, nextMapButton, createCancel, createConfirm, createClose;
    private Toggle[] maxToggles;
    private int mapIndex;

    // 설정 팝업
    private SettingsView settingsView;

    // 연결 상태 / 빈 화면 / 토스트 / 로딩
    private Image connectionDot;
    private TMP_Text connectionText;
    private CanvasGroup listGroup, sideGroup;
    private GameObject emptyState;
    private ToastView toast;
    private LoadingView loading;

    // ───────────── 초기화 ─────────────

    private void Reset()
    {
        var ui = GameObject.Find("UI");
        if (ui != null) uiRoot = ui.transform;
    }

    private void Awake()
    {
        if (uiRoot == null) Reset();
        if (uiRoot == null) { Debug.LogError("[Lobby] 'UI' 루트를 찾을 수 없습니다.", this); enabled = false; return; }

        // 게임을 마치고 같은 방으로 돌아온 경우만 방을 유지한다
        if (!SceneFlow.IsReturningToRoom)
        {
            CurrentRoom = null;
            PlayingRoomId = null;
            ChatService.Clear(); // 지난 방의 채팅이 다음 방에 남지 않게 한다
        }
        // 재접속으로 게임에 들어갔다 돌아온 경우 CurrentRoom은 없지만 PlayingRoomId는 있다. 이때도 방 안으로 본다.
        inRoom = CurrentRoom != null || PlayingRoomId != null;
        
        Debug.Log($"inRoom = {inRoom}");
        
        Bind();
        WireEvents();
        RefreshProfile();
        ShowProfile(false);
        
        if (inRoom)
            SetConnectionTrue();
        else if (LocalPlayer.IsSet)
            SetConnection(ConnectionState.Connected, false); // 게임에서 나가기로 돌아온 경우: 이미 닉네임이 정해져 있고 CONNECTED가 다시 오지 않는다
        else
            SetConnection(ConnectionState.Disconnected, false);
        
        SyncAll();
    }

    private void Start()
    {
        // 게임이 끝나 같은 방으로 돌아왔다면 방 설정을 다시 띄운다 (준비 해제 등은 서버가 스냅샷으로 알려 준다)
        bool returning = SceneFlow.ConsumeReopenRoomSetup();
        if (returning && CurrentRoom != null)
        {
            OpenRoomSetup(CurrentRoom);
            RequestRoomList(false); // 방 설정 뒤의 목록은 조용히 받아 둔다
        }
        else if (returning && inRoom)
        {
            // 재접속 후 게임을 마치고 돌아왔다: 방 스냅샷이 없으므로 서버가 보내는 ROOM_UPDATED로 대기방을 연다 (HandleRoomUpdated)
            RequestRoomList(false);
            StartCoroutine(WaitRoomSnapshotRoutine());
        }
        else
        {
            RequestRoomList(true);
        }
    }

    // ROOM_UPDATED가 끝내 오지 않으면(방이 사라졌거나 서버에서 내보낸 경우) 방 밖 상태로 되돌린다
    private IEnumerator WaitRoomSnapshotRoutine()
    {
        yield return new WaitForSecondsRealtime(RoomSnapshotWaitSeconds);
        if (CurrentRoom != null) yield break;

        Debug.LogWarning($"[Lobby] 방({PlayingRoomId}) 정보를 받지 못해 로비 목록으로 돌아갑니다.");
        inRoom = false;
        PlayingRoomId = null;
        ShowToast("방 정보를 불러오지 못했어요");
        RequestRoomList(false);
        SyncAll();
    }
    
    private void OnEnable()
    {
        if (Net == null) { Debug.LogError("[Lobby] LobbyNetwork.Instance가 없습니다.", this); return; }
        Net.OnRoomEntered += HandleRoomEntered;
        Net.OnRoomLeft += HandleRoomLeft;
        Net.OnRoomList += HandleRoomList;
        Net.OnRoomUpdated += HandleRoomUpdated;
        Net.OnGameStarted += HandleGameStarted;
        Net.OnLobbyError += HandleLobbyError;
        Net.OnChatBroadcast += HandleChatBroadcast;
        ChatService.SendRequested = SendChat;
        
        Net.OnConnected += HandleConnected;
        PortraitLibrary.Changed += HandlePortraitChanged;
    }

    private void OnDisable()
    {
        if (Net == null) return;
        Net.OnRoomEntered -= HandleRoomEntered;
        Net.OnRoomLeft -= HandleRoomLeft;
        Net.OnRoomList -= HandleRoomList;
        Net.OnRoomUpdated -= HandleRoomUpdated;
        Net.OnGameStarted -= HandleGameStarted;
        Net.OnLobbyError -= HandleLobbyError;
        Net.OnChatBroadcast -= HandleChatBroadcast;
        if (ChatService.SendRequested == (System.Action<ChatMessage>)SendChat) ChatService.SendRequested = null;
        
        Net.OnConnected -= HandleConnected;
        PortraitLibrary.Changed -= HandlePortraitChanged;
    }

    private static T Get<T>(Transform root, string path) where T : Component
    {
        var t = root.Find(path);
        if (t == null) { Debug.LogWarning($"[Lobby] '{root.name}/{path}' 없음"); return null; }
        var c = t.GetComponent<T>();
        if (c == null) Debug.LogWarning($"[Lobby] '{root.name}/{path}'에 {typeof(T).Name} 없음");
        return c;
    }

    private static void Hide(Transform root, string path)
    {
        var t = root.Find(path);
        if (t != null) t.gameObject.SetActive(false);
    }

    private void Bind()
    {
        var lobby = uiRoot.Find("Canvas_Lobby");

        settingsButton = Get<Button>(lobby, "Header/Buttons/SettingsButton");
        exitButton = Get<Button>(lobby, "Header/Buttons/ExitButton");
        refreshButton = Get<Button>(lobby, "RoomListPanel/RefreshButton");
        connectionDot = Get<Image>(lobby, "Header/Brand/ConnectionChip/Dot");
        connectionText = Get<TMP_Text>(lobby, "Header/Brand/ConnectionChip/ConnectionText");
        listGroup = UIFx.EnsureGroup(lobby.Find("RoomListPanel").gameObject);
        sideGroup = UIFx.EnsureGroup(lobby.Find("SidePanel").gameObject);
        emptyState = lobby.Find("RoomListPanel/EmptyState").gameObject;

        toast = uiRoot.Find("Canvas_Toast").GetComponent<ToastView>();
        loading = uiRoot.Find("Canvas_Loading").GetComponent<LoadingView>();

        settingsView = uiRoot.Find("Canvas_Settings").GetComponent<SettingsView>();
        var exitCanvas = uiRoot.Find("Canvas_ExitConfirm");
        if (exitCanvas != null) exitConfirm = exitCanvas.GetComponent<ExitConfirmView>();
        var roomSetupCanvas = uiRoot.Find("Canvas_RoomSetup");
        if (roomSetupCanvas != null) roomSetup = roomSetupCanvas.GetComponent<RoomSetupView>();

        var list = lobby.Find("RoomListPanel");
        joinableOnlyToggle = Get<Toggle>(list, "JoinableOnlyToggle");
        joinableOnlyLabel = Get<TMP_Text>(list, "JoinableOnlyToggle/Label");
        rowContent = (RectTransform)list.Find("RoomScroll/Viewport/Content");

        // 첫 행을 복제용 틀로 쓰고 나머지 샘플 행은 치운다
        rowTemplate = rowContent.Find("RoomRow_1").gameObject;
        foreach (Transform child in rowContent.Cast<Transform>().ToList())
        {
            if (child.gameObject == rowTemplate) continue;
            child.SetParent(null);
            Destroy(child.gameObject);
        }
        rowTemplate.SetActive(false);
        normalStrokeSprite = rowTemplate.transform.Find("Stroke").GetComponent<Image>().sprite;

        var side = lobby.Find("SidePanel");
        profileCard = side.Find("ProfileCard").gameObject;
        nicknameText = Get<TMP_Text>(side, "ProfileCard/NicknameText");
        marbleText = Get<TMP_Text>(side, "ProfileCard/MarbleChip/AmountText");
        profilePortraitButton = Get<Button>(side, "ProfileCard/Portrait");
        profilePortraitImage = Get<Image>(side, "ProfileCard/Portrait/PortraitMask/PortraitImage");
        var pickerCanvas = uiRoot.Find("Canvas_PortraitPicker");
        if (pickerCanvas != null) portraitPicker = pickerCanvas.GetComponent<PortraitPickerView>();
        // 접속자 목록은 서버가 아직 보내 주지 않아서 숨긴다
        playerListCard = side.Find("PlayerListCard").gameObject;
        quickStartButton = Get<Button>(side, "QuickStartButton");
        createRoomButton = Get<Button>(side, "CreateRoomButton");

        // 방 상세 카드
        detailCard = side.Find("RoomDetailCard").gameObject;
        var d = detailCard.transform;
        detailTitle = Get<TMP_Text>(d, "TitleText");
        detailClose = Get<Button>(d, "CloseButton");
        detailLockChip = d.Find("ChipRow/LockChip").gameObject;
        detailStatusChip = Get<Image>(d, "ChipRow/StatusChip");
        detailStatusText = Get<TMP_Text>(d, "ChipRow/StatusChip/Text");
        detailMap = Get<TMP_Text>(d, "MapInfo/ValueText");
        detailCount = Get<TMP_Text>(d, "PlayerCountText");
        detailProgress = d.Find("GameProgress").gameObject;
        detailJoin = Get<Button>(d, "JoinButton");
        detailJoinImage = detailJoin.GetComponent<Image>();
        detailJoinLabel = Get<TMP_Text>(d, "JoinButton/Label");
        for (int i = 0; i < slots.Length; i++)
        {
            var s = d.Find($"PlayerSlots/PlayerSlot_{i + 1}");
            slots[i] = new SlotView
            {
                Go = s.gameObject,
                Bg = s.GetComponent<Image>(),
                Stroke = Get<Image>(s, "Stroke"),
                Portrait = Get<Image>(s, "Portrait/PortraitMask/PortraitImage"),
                Ring = Get<Image>(s, "Portrait/Ring"),
                Host = s.Find("Portrait/HostBadge").gameObject,
                Nick = Get<TMP_Text>(s, "NicknameText"),
            };
        }

        // 방 만들기 팝업: 서버로 보낼 수 있는 값은 맵과 최대 인원뿐이라 이름/비밀번호 칸은 숨긴다
        createPopup = uiRoot.Find("Canvas_CreateRoom").GetComponent<PopupView>();
        var cw = createPopup.Window;
        Hide(cw, "NameSection");
        Hide(cw, "PasswordSection");
        mapNameText = Get<TMP_Text>(cw, "MapSection/MapNameText");
        mapPreviewImage = Get<Image>(cw, "MapSection/MapPreview/MapImage");
        prevMapButton = Get<Button>(cw, "MapSection/PrevMapButton");
        nextMapButton = Get<Button>(cw, "MapSection/NextMapButton");
        maxToggles = cw.Find("MaxPlayersSection/MaxPlayersToggle").GetComponentsInChildren<Toggle>(true);
        createCancel = Get<Button>(cw, "CancelButton");
        createConfirm = Get<Button>(cw, "CreateButton");
        createClose = Get<Button>(cw, "CloseButton");
    }

    private void WireEvents()
    {
        // 상단바
        settingsButton.onClick.AddListener(settingsView.Open);
        exitButton.onClick.AddListener(ExitToLogin);
        refreshButton.onClick.AddListener(OnRefreshClicked);
        refreshLabel = refreshButton.GetComponentInChildren<TMP_Text>();
        if (refreshLabel != null)
        {
            // "새로고침 (10초)"가 버튼 안에 한 줄로 들어가도록 글자가 길어지면 줄여서 보인다
            refreshLabel.enableAutoSizing = true;
            refreshLabel.fontSizeMax = refreshLabel.fontSize;
            refreshLabel.fontSizeMin = 14;
            refreshLabel.textWrappingMode = TextWrappingModes.NoWrap;
        }

        // 필터
        joinableOnlyToggle.onValueChanged.AddListener(isOn =>
        {
            UpdateJoinableToggleVisual(isOn);
            SyncRows();
        });
        UpdateJoinableToggleVisual(joinableOnlyToggle.isOn);

        // 오른쪽 열
        quickStartButton.onClick.AddListener(QuickStart);
        createRoomButton.onClick.AddListener(OpenCreatePopup);
        if (profilePortraitButton != null) profilePortraitButton.onClick.AddListener(OpenPortraitPicker);
        detailClose.onClick.AddListener(Deselect);
        detailJoin.onClick.AddListener(() => { if (selectedId != null) TryJoin(selectedId); });

        // 방 만들기
        prevMapButton.onClick.AddListener(() => ChangeMap(-1));
        nextMapButton.onClick.AddListener(() => ChangeMap(1));
        createCancel.onClick.AddListener(createPopup.Close);
        createClose.onClick.AddListener(createPopup.Close);
        createConfirm.onClick.AddListener(ConfirmCreate);
    }

    private void Update()
    {
        // 서버 메시지는 매 프레임 처리한다 (Escape 처리보다 먼저)
        if (Net != null) Net.TryDequeueMessage();

        UpdateRefreshButton();

        if (!Input.GetKeyDown(KeyCode.Escape) || Busy) return;
        if (settingsView.IsOpen) return; // 설정 창은 닫기 버튼으로만 닫는다
        if (roomSetup != null && roomSetup.IsOpen) return; // 방 안에서는 X 버튼으로만 나간다
        if (portraitPicker != null && portraitPicker.IsOpen) portraitPicker.Close();
        else if (createPopup.IsOpen) createPopup.Close();
        else if (selectedId != null) Deselect();
    }

    // ───────────── 서버 요청 ─────────────
    // TODO: LobbyNetwork의 실제 메서드 이름/인자에 맞춘다

    // 새로고침 버튼. 서버 요청이 실제로 나가는 경우에만 쿨타임을 시작한다 (요청 중이거나 연결이 없으면 아무 일도 없다)
    private void OnRefreshClicked()
    {
        if (!CanRequest || Time.unscaledTime < refreshReadyAt) return;
        refreshReadyAt = Time.unscaledTime + refreshCooldown;
        RequestRoomList(true);
        UpdateRefreshButton();
    }

    // 쿨타임 동안 버튼을 누를 수 없게 하고 남은 초를 보여 준다. 끝나면 평소 모습으로 돌아온다. (초가 바뀔 때만 글자를 바꾼다)
    private void UpdateRefreshButton()
    {
        if (refreshButton == null) return;
        int remaining = Mathf.Max(0, Mathf.CeilToInt(refreshReadyAt - Time.unscaledTime));
        if (remaining == shownCooldown) return;
        shownCooldown = remaining;

        refreshButton.interactable = remaining == 0;
        if (refreshLabel == null) return;
        refreshLabel.text = remaining > 0 ? $"새로고침 ({remaining}초)" : "새로고침";
        var color = refreshLabel.color;
        color.a = remaining > 0 ? 0.55f : 1f;
        refreshLabel.color = color;
    }

    private void RequestRoomList(bool userTriggered)
    {
        if (Net == null || connection != ConnectionState.Connected) return;
        if (userTriggered)
        {
            if (Busy) return;
            BeginRequest(Pending.RoomList, ""); // 문구 없이 점만
        }
        refreshToast = userTriggered;
        Net.SendRoomListRequest();
    }

    private void RequestJoin(string roomId)
    {
        if (!CanRequest || string.IsNullOrEmpty(roomId)) return;
        Debug.Log($"[Lobby] 입장 요청: {roomId}");
        BeginRequest(Pending.Enter);
        Net.SendEnterRoomRequest(roomId);
    }

    private void RequestCreate(int mapId, int maxPlayers)
    {
        if (!CanRequest) return;
        Debug.Log($"[Lobby] 방 생성 요청: 맵 {mapId}, {maxPlayers}명");
        Debug.Log("[Lobby] A");
        BeginRequest(Pending.Enter);
        Debug.Log("[Lobby] B");
        Net.SendCreateRoomRequest();

        GameConfig config = new GameConfig();
        config.mapId = mapId;
        Net.SendRoomConfigRequest(config,  maxPlayers, 0);
    }

    private void RequestLeave()
    {
        if (Busy || CurrentRoom == null) return;
        BeginRequest(Pending.Leave);
        Net.SendLeaveRoomRequest();
    }

    private void RequestStartGame()
    {
        if (!CanRequest || CurrentRoom == null || !IsHost(CurrentRoom)) return;
        BeginRequest(Pending.Start);
        Net.SendStartGameRequest();
    }
    
    // 방장이 방 설정 화면에서 "설정 적용"을 눌렀을 때 바뀐 설정을 서버에 보낸다.
    // 서버가 받아들이면 새 설정이 담긴 ROOM_UPDATED가 오고(HandleRoomUpdated), 거절하면 LOBBY_ERROR가 온다.
    private void RequestRoomConfig(GameConfig config, int maxPlayers, int botCount)
    {
        var room = CurrentRoom;
        if (!CanRequest || room == null || !IsHost(room) || config == null) return;

        // 값이 서버가 받을 수 있는 범위인지 먼저 확인한다
        int humans = room.Players != null ? room.Players.Count(p => !p.bot) : 1; // 지금 들어와 있는 사람 수 (봇은 목록에 있어도 사람으로 세지 않는다)
        if (maxPlayers < 2 || maxPlayers > 4 || maxPlayers < humans)
        {
            ShowToast(maxPlayers < humans ? "지금 들어와 있는 인원보다 적게는 줄일 수 없어요" : "최대 인원은 2~4명이에요");
            return;
        }
        if (botCount < 0 || humans + botCount > maxPlayers)
        {
            ShowToast("사람과 봇을 합쳐 최대 인원을 넘을 수 없어요");
            return;
        }
        if (config.startMoney <= 0 || config.salaryAmount < 0 || config.maxRound <= 0)
        {
            ShowToast("게임 설정 값이 올바르지 않아요");
            return;
        }

        Debug.Log($"[Lobby] 방 설정 요청: 맵 {config.mapId}, 최대 {maxPlayers}명, 봇 {botCount}명, 시작 자금 {config.startMoney}, 라운드 {config.maxRound}, 월급 {config.salaryAmount}");
        BeginRequest(Pending.Config);
        Net.SendRoomConfigRequest(config, maxPlayers, botCount);
    }

    private void BeginRequest(Pending kind, string loadingMessage = null)
    {
        pending = kind;
        ShowLoading(true, loadingMessage);
        if (timeoutRoutine != null) StopCoroutine(timeoutRoutine);
        timeoutRoutine = StartCoroutine(TimeoutRoutine());
    }

    private void EndRequest()
    {
        if (timeoutRoutine != null) { StopCoroutine(timeoutRoutine); timeoutRoutine = null; }
        if (pending == Pending.None) return;
        pending = Pending.None;
        ShowLoading(false);
    }

    private IEnumerator TimeoutRoutine()
    {
        yield return new WaitForSecondsRealtime(requestTimeout);
        timeoutRoutine = null;
        if (pending == Pending.None) yield break;
        EndRequest();
        ShowToast("서버 응답이 없어요. 잠시 후 다시 시도해 주세요");
    }

    // ───────────── 서버 응답 ─────────────

    public void HandleConnected(string localNickname)
    {
        SetConnectionTrue();
        RefreshNickname(localNickname);
    }

    public void HandleRoomList(List<RoomSummary> roomSummaries)
    {
        rooms.Clear();
        if (roomSummaries != null) rooms.AddRange(roomSummaries.Where(r => r != null));

        if (pending == Pending.RoomList) EndRequest();
        SyncAll();
        if (refreshToast) ShowToast("방 목록을 새로 불러왔어요");
        refreshToast = false;
    }

    // 입장(또는 방 만들기)이 받아들여졌다. 방 정보는 OnRoomUpdated로 온다 (순서가 바뀌어 와도 둘 다 받으면 연다)
    public void HandleRoomEntered()
    {
        inRoom = true;
        TryOpenRoomSetup();
    }

    public void HandleRoomUpdated(RoomSnapshot snapshot)
    {
        if (snapshot == null) return;

        // 입장 중도, 방 안도 아니면 받을 이유가 없다
        if (!inRoom && pending != Pending.Enter) return;
        // 방 안에 있는데 다른 방의 스냅샷이면 무시한다
        if (inRoom && CurrentRoom != null && CurrentRoom.roomId != snapshot.roomId) return;
        // 재접속 후 게임에서 돌아와 스냅샷을 기다리는 중인데 다른 방의 스냅샷이면 무시한다
        if (inRoom && CurrentRoom == null && PlayingRoomId != null && PlayingRoomId != snapshot.roomId) return;

        CurrentRoom = snapshot;
        PlayingRoomId = null; // 이제 CurrentRoom이 방 정보를 가진다

        // 내가 보낸 설정 요청에 대한 응답이다: 로딩을 닫고, 방장의 초안도 서버가 적용한 값으로 맞춘다
        if (pending == Pending.Config)
        {
            EndRequest();
            if (roomSetup != null && roomSetup.IsOpen)
            {
                ApplyRoomSnapshot(snapshot, keepDraft: false);
                ShowToast("설정을 적용했어요");
                return;
            }
        }

        if (roomSetup != null && roomSetup.IsOpen) ApplyRoomSnapshot(snapshot);
        else TryOpenRoomSetup();
    }

    public void HandleRoomLeft()
    {
        bool requested = pending == Pending.Leave;
        EndRequest();
        inRoom = false;
        CurrentRoom = null;
        ChatService.Clear(); // 나간 방의 채팅은 지운다
        PlayingRoomId = null;
        if (roomSetup != null && roomSetup.IsOpen) roomSetup.Close();
        if (!requested) ShowToast("방에서 나왔어요");
        RequestRoomList(false);
        SyncAll();
    }

    public void HandleGameStarted(string roomId)
    {
        //if (CurrentRoom == null || CurrentRoom.roomId != roomId) return;

        // 재접속으로 바로 게임에 들어가는 경우 CurrentRoom이 없으므로, 게임이 끝나 돌아올 방을 id로 기억해 둔다
        PlayingRoomId = roomId;

        // 씬이 넘어갈 때까지 로딩을 띄워 둔다 (방장이 아닌 사람도)
        if (timeoutRoutine != null) { StopCoroutine(timeoutRoutine); timeoutRoutine = null; }
        pending = Pending.Start;
        ShowLoading(true);

        Debug.Log($"[Lobby] 게임 씬으로 이동 (방 {roomId})");
        if (!SceneFlow.ToGame())
        {
            EndRequest();
            ShowToast("게임 화면을 불러오지 못했어요");
        }
    }

    // 채팅: 보낸 글은 서버를 거쳐 CHAT_BROADCAST로 (나를 포함해) 방 모두에게 돌아온다. 그때 화면에 보인다.
    private void SendChat(ChatMessage message)
    {
        if (CurrentRoom == null) return;
        Net.SendChatRequest(message.Text);
    }

    private void HandleChatBroadcast(long playerId, string content)
    {
        var players = CurrentRoom?.Players;
        int index = players != null ? players.FindIndex(p => p.playerId == playerId) : -1;
        ChatService.Receive(new ChatMessage
        {
            Sender = index >= 0 ? players[index].nickname : "알 수 없음",
            ColorIndex = Mathf.Max(0, index), // 색은 자리 순서로 정한다 (ApplyRoomSnapshot과 같다)
            Text = content,
            IsLocal = playerId == MyPlayerId,
        });
    }

    public void HandleLobbyError(string code, string message)
    {
        Debug.LogWarning($"[Lobby] 서버 오류 {code}: {message}");
        bool wasEntering = pending == Pending.Enter;
        EndRequest();
        ShowToast(string.IsNullOrEmpty(message) ? "요청을 처리하지 못했어요" : message);

        // 입장 실패면 방이 사라졌거나 찼을 수 있으니 목록을 다시 받는다
        if (wasEntering) RequestRoomList(false);
    }
    
    // ───────────── 연결 상태 ─────────────

    // TODO: LobbyNetwork의 연결 상태 이벤트에 연결한다
    private void SetConnection(ConnectionState state, bool notify)
    {
        var previous = connection;
        connection = state;

        bool online = state == ConnectionState.Connected;
        connectionDot.color = state == ConnectionState.Connected ? new Color32(140, 220, 184, 255)
            : state == ConnectionState.Reconnecting ? UIPalette.Gold : UIPalette.Red;
        connectionText.text = state == ConnectionState.Connected ? "서버 연결됨"
            : state == ConnectionState.Reconnecting ? "재연결 중..." : "연결 끊김";

        // 연결이 없으면 방 목록과 오른쪽 버튼을 흐리게 하고 누를 수 없게 한다
        listGroup.interactable = online;
        sideGroup.interactable = online;
        listGroup.alpha = online ? 1f : 0.55f;
        sideGroup.alpha = online ? 1f : 0.55f;
        if (!online)
        {
            EndRequest();
            createPopup.Close();
        }

        if (!notify) return;
        if (state == ConnectionState.Disconnected) ShowToast("서버와 연결이 끊겼어요");
        else if (state == ConnectionState.Connected && previous != ConnectionState.Connected)
        {
            ShowToast("서버에 다시 연결됐어요");
            RequestRoomList(false);
        }
    }
    
    private void SetConnectionTrue()
    {
        SetConnection(ConnectionState.Connected, true);
    }

    // ───────────── 토스트 / 로딩 ─────────────

    private void ShowToast(string message) => toast.Show(message);

    private void ShowLoading(bool show, string message = null) => loading.Show(show, message);

    // ───────────── 공통 도우미 ─────────────

    private static bool IsFull(RoomSummary room) => room.playerCount >= room.maxPlayers;

    private static string MapName(int mapId) => mapId >= 0 && mapId < Maps.Length ? Maps[mapId] : $"맵 {mapId}";

    private Sprite MapSprite(int mapId) =>
        mapSprites != null && mapId >= 0 && mapId < mapSprites.Length ? mapSprites[mapId] : null;

    // 서버 데이터에 방 이름이 없어서 방장 닉네임으로 만든다
    public static string RoomTitle(string hostNickname) =>
        string.IsNullOrEmpty(hostNickname) ? "이름 없는 방" : $"{hostNickname}의 방";

    private static bool IsHost(RoomSnapshot room) => room.hostId == MyPlayerId;

    /// <summary>못 들어가는 이유. 들어갈 수 있으면 null.</summary>
    private static string JoinBlockedMessage(RoomSummary room) => IsFull(room) ? "방이 가득 찼어요" : null;

    // ───────────── 나가기 ─────────────

    private void ExitToLogin()
    {
        if (Busy) return;
        
        if (exitConfirm != null)
        {
            exitConfirm.Show(LoadLoginScene, "로그인 화면으로 돌아갈까요?");
        }
        else
        {
            LoadLoginScene();
        }
    }
    
    private void LoadLoginScene()
    {
        if (Busy) return;
        if (SceneFlow.ToLogin()) return;
        ShowToast("로그인 화면이 아직 없어요");
    }

    // ───────────── 방 입장 ─────────────

    /// <summary>목록·상세에서 입장. 가득 찬 방은 미리 막고, 나머지 검증은 서버가 한다.</summary>
    private void TryJoin(string roomId)
    {
        if (!CanRequest) return;
        var room = rooms.FirstOrDefault(r => r.roomId == roomId);
        if (room == null) { ShowToast("방이 사라졌어요"); SyncAll(); return; }
        string blocked = JoinBlockedMessage(room);
        if (blocked != null) { ShowToast(blocked); return; }
        RequestJoin(roomId);
    }

    private void QuickStart()
    {
        var target = rooms
            .Where(r => !IsFull(r))
            .OrderByDescending(r => r.playerCount)
            .FirstOrDefault();
        if (target != null) { RequestJoin(target.roomId); return; }

        Debug.Log("[Lobby] 들어갈 방이 없어 새 방을 만듭니다.");
        RequestCreate(FirstAvailableMap(), 4); // 빠른 시작으로 만드는 방도 고를 수 있는 맵으로
    }

    // ───────────── 방 설정 (방 안, 게임 시작 전) ─────────────

    private void TryOpenRoomSetup()
    {
        if (!inRoom || CurrentRoom == null) return;
        if (roomSetup != null && roomSetup.IsOpen) { ApplyRoomSnapshot(CurrentRoom); return; }
        if (pending == Pending.Enter) EndRequest();
        Debug.Log($"[Lobby] 방 입장: {CurrentRoom.roomId}, 방장 {IsHost(CurrentRoom)}");
        OpenRoomSetup(CurrentRoom);
    }

    private void OpenRoomSetup(RoomSnapshot room)
    {
        if (roomSetup == null) { Debug.LogError("[Lobby] Canvas_RoomSetup이 로비 씬에 없습니다."); return; }

        ApplyRoomSnapshot(room, keepDraft: false); // 방을 처음 열 때는 방장의 초안도 서버 값에서 시작한다

        roomSetup.SettingsApplied -= OnSettingsApplied;
        roomSetup.SettingsApplied += OnSettingsApplied;
        roomSetup.ReadyToggled -= OnLocalReadyToggled;
        roomSetup.ReadyToggled += OnLocalReadyToggled;
        roomSetup.LeaveRequested -= OnLeaveRequested;
        roomSetup.LeaveRequested += OnLeaveRequested;

        roomSetup.Show(RequestStartGame);
    }

    /// <summary>
    /// 스냅샷을 방 설정 화면에 그대로 반영한다. 방 안에서 받는 모든 OnRoomUpdated가 여기로 온다.
    /// keepDraft: 방장이 바꾸던 설정(적용 전)을 그대로 둘지. 방을 처음 열 때와 설정 적용 응답을 받았을 때만 false다.
    /// </summary>
    private void ApplyRoomSnapshot(RoomSnapshot room, bool keepDraft = true)
    {
        var players = room.Players ?? new List<PlayerSnapshot>();
        int local = players.FindIndex(p => p.playerId == MyPlayerId);
        bool isHost = IsHost(room);
        var host = players.FirstOrDefault(p => p.playerId == room.hostId);

        applyingSnapshot = true;
        try
        {
            roomSetup.SetRoomInfo(RoomTitle(host?.nickname));
            roomSetup.SetMapChoices(Maps, mapSprites, MapAvailable);
            roomSetup.SetLocalPlayer(Mathf.Max(0, local));
            roomSetup.SetHost(isHost);

            for (int i = 0; i < 4; i++)
            {
                if (i >= players.Count) { roomSetup.SetSlot(i, new RoomSetupView.Slot { IsEmpty = true }); continue; }
                var p = players[i];
                bool pHost = p.playerId == room.hostId;
                roomSetup.SetSlot(i, new RoomSetupView.Slot
                {
                    IsEmpty = false,
                    Name = p.nickname,
                    ColorIndex = i, // 서버 데이터에 색이 없어서 자리 순서로 정한다
                    // 서버가 준 초상화. 없으면(봇, 고르지 않은 사람) 내 것은 내가 고른 그림, 다른 사람은 자리 색의 기본 그림
                    Portrait = p.bot ? PortraitLibrary.Bot // 봇은 전용 로봇 초상화
                             : p.portraitId.HasValue ? PortraitLibrary.For(p.portraitId, i)
                             : i == local ? PortraitLibrary.Selected : PortraitLibrary.Default(i),
                    IsHost = pHost,
                    IsBot = p.bot,
                    IsReady = pHost || p.ready || p.bot, // 봇은 항상 준비된 상태
                });
            }

            // 서버에 적용된 설정. 자리를 채운 뒤에 넣어야 사람 수를 기준으로 맞춰진다.
            var config = room.config;
            roomSetup.SetSettings(new RoomSetupView.Settings
            {
                Map = MapName(room.mapId),
                MaxPlayers = room.maxPlayers,
                StartMoney = config != null && config.startMoney > 0 ? config.startMoney : 500000,
                MaxRound = config != null && config.maxRound > 0 ? config.maxRound : 30,
                Salary = config != null && config.salaryAmount > 0 ? config.salaryAmount : 100000,
                // 서버는 봇 수를 따로 주지 않고 players 목록에 봇을 넣어 보낸다. 목록의 봇을 세어 쓴다. (안 그러면 적용 뒤 0명으로 돌아간다)
                BotCount = Mathf.Max(room.botCount, players.Count(p => p.bot)),
            }, keepDraft);
            roomSetup.SetReady(isHost || (local >= 0 && players[local].ready));
        }
        finally
        {
            applyingSnapshot = false;
        }
    }

    // 방장이 "설정 적용"을 눌렀을 때. 방장이 바꾼 맵, 인원, 시작 자금, 최대 라운드, 월급, 봇 수 전체가 한 번에 넘어온다.
    // 화면은 서버가 새 스냅샷을 보내면 적용된 값으로 바뀐다 (HandleRoomUpdated).
    private void OnSettingsApplied(RoomSetupView.Settings settings)
    {
        var room = CurrentRoom;
        if (applyingSnapshot || room == null || !IsHost(room)) return;

        // 서버가 가진 나머지 설정(세금, 무인도 턴, 제한 시간)은 지금 값을 그대로 보낸다
        var current = room.config;
        var config = new GameConfig
        {
            startMoney = settings.StartMoney,
            salaryAmount = settings.Salary,
            taxAmount = current != null ? current.taxAmount : 0,
            maxRound = settings.MaxRound,
            islandTurns = current != null ? current.islandTurns : 0,
            mapId = Mathf.Max(0, Array.IndexOf(Maps, settings.Map)),
            limitTimeSeconds = current != null ? current.limitTimeSeconds : 0,
        };
        RequestRoomConfig(config, settings.MaxPlayers, settings.BotCount);
    }

    private void OnLocalReadyToggled(bool ready)
    {
        if (applyingSnapshot || CurrentRoom == null) return;
        Net.SendSetReadyRequest(ready);
    }

    // X 버튼: 방에서 나갈지 묻고, 확인하면 나가기 요청을 보낸다. 화면은 OnRoomLeft를 받으면 닫는다
    private void OnLeaveRequested()
    {
        if (Busy) return;
        if (exitConfirm != null) exitConfirm.Show(RequestLeave, "방에서 나갈까요?");
        else RequestLeave();
    }

    // ───────────── 선택 / 오른쪽 열 전환 ─────────────

    private void Select(string roomId)
    {
        bool wasShowingDetail = detailCard.activeSelf;
        selectedId = roomId;
        RefreshDetail();
        UpdateRowHighlights();
        ShowDetail(!wasShowingDetail);
    }

    private void Deselect()
    {
        if (selectedId == null) return;
        selectedId = null;
        UpdateRowHighlights();
        ShowProfile(true);
    }

    private void ShowDetail(bool animate)
    {
        profileCard.SetActive(false);
        playerListCard.SetActive(false);
        detailCard.SetActive(true);
        if (animate) Fade(detailCard);
    }

    private void ShowProfile(bool animate)
    {
        detailCard.SetActive(false);
        profileCard.SetActive(true);
        playerListCard.SetActive(false); // 접속자 목록 데이터가 생기면 다시 켠다
        if (animate) Fade(profileCard);
    }

    private static void Fade(GameObject go)
    {
        var group = UIFx.EnsureGroup(go);
        group.DOKill();
        group.alpha = 0f;
        group.DOFade(1f, 0.2f).SetUpdate(true).SetLink(go);
    }

    // ───────────── 방 목록 ─────────────

    private void SyncAll()
    {
        // 보고 있던 방이 사라졌으면 내 정보로 돌아간다
        if (selectedId != null && Selected == null) selectedId = null;
        if (selectedId == null && detailCard.activeSelf) ShowProfile(true);
        SyncRows();
        if (selectedId != null) RefreshDetail();
    }

    private void UpdateJoinableToggleVisual(bool isOn)
    {
        joinableOnlyToggle.targetGraphic.color = isOn ? ReadyBg : Neutral;
        joinableOnlyLabel.color = isOn ? ReadyText : UIPalette.Ink;
    }

    private bool IsListed(RoomSummary room) => !joinableOnlyToggle.isOn || !IsFull(room);

    private void SyncRows()
    {
        var visible = rooms.Where(IsListed).ToList();
        var visibleIds = new HashSet<string>(visible.Select(r => r.roomId));

        foreach (var goneId in rows.Keys.Where(id => !visibleIds.Contains(id)).ToList())
        {
            rows[goneId].Go.SetActive(false); // Destroy는 프레임 끝에 처리되므로 먼저 숨긴다
            Destroy(rows[goneId].Go);
            rows.Remove(goneId);
        }

        for (int i = 0; i < visible.Count; i++)
        {
            var room = visible[i];
            if (!rows.TryGetValue(room.roomId, out var row))
            {
                row = CreateRow(room.roomId);
                rows[room.roomId] = row;
            }
            row.Go.transform.SetSiblingIndex(i + 1); // 0번은 복제용 틀
            UpdateRow(row, room, i);
        }

        emptyState.SetActive(visible.Count == 0);
        UpdateRowHighlights();
    }

    private RowView CreateRow(string roomId)
    {
        var go = Instantiate(rowTemplate, rowContent);
        go.name = $"RoomRow_{roomId}";
        go.SetActive(true);
        var t = go.transform;

        var row = new RowView
        {
            Go = go,
            Row = go.GetComponent<Button>(),
            Bg = go.GetComponent<Image>(),
            Stroke = Get<Image>(t, "Stroke"),
            Thumb = Get<Image>(t, "Thumb"),
            ThumbStroke = Get<Image>(t, "Thumb/Stroke"),
            Lock = t.Find("Thumb/LockBadge").gameObject,
            Number = Get<TMP_Text>(t, "Thumb/NumberText"),
            Name = Get<TMP_Text>(t, "NameText"),
            Info = Get<TMP_Text>(t, "InfoText"),
            Count = Get<TMP_Text>(t, "PlayerCountText"),
            StatusChip = Get<Image>(t, "StatusChip"),
            StatusText = Get<TMP_Text>(t, "StatusChip/Text"),
            Join = Get<Button>(t, "JoinButton"),
            JoinImage = Get<Image>(t, "JoinButton"),
            JoinLabel = Get<TMP_Text>(t, "JoinButton/Label"),
            Dots = new Image[4],
        };
        for (int i = 0; i < 4; i++) row.Dots[i] = Get<Image>(t, $"PlayerDots/Dot_{i + 1}");

        row.Row.onClick.RemoveAllListeners();
        row.Row.onClick.AddListener(() => Select(roomId));
        row.Join.onClick.RemoveAllListeners();
        row.Join.onClick.AddListener(() => TryJoin(roomId));
        return row;
    }

    private void UpdateRow(RowView row, RoomSummary room, int index)
    {
        row.Bg.color = SoloRowBg;
        row.Thumb.color = Color.white;
        row.ThumbStroke.color = RowStroke;
        row.Lock.SetActive(false);                 // RoomSummary에 비밀번호 정보가 없다
        row.Number.text = (index + 1).ToString("00");
        row.Name.text = RoomTitle(room.hostNickname);
        row.Info.text = $"{MapName(room.mapId)} · 방장 {room.hostNickname}";

        row.Count.text = $"{room.playerCount} / {room.maxPlayers}";
        for (int i = 0; i < row.Dots.Length; i++)
        {
            row.Dots[i].gameObject.SetActive(i < room.maxPlayers);
            row.Dots[i].color = i < room.playerCount ? UIPalette.PlayerColor(i) : DotEmpty;
        }

        bool full = IsFull(room);
        ApplyStatus(full, row.StatusChip, row.StatusText);

        bool canJoin = !full;
        row.Join.interactable = canJoin;
        row.JoinImage.color = canJoin ? Mint : Neutral;
        row.JoinLabel.text = canJoin ? "참가" : "입장 불가";
        row.JoinLabel.fontSize = canJoin ? 24 : 18;
        row.JoinLabel.color = canJoin ? Color.white : UIPalette.InkSub;
    }

    private static void ApplyStatus(bool full, Image chip, TMP_Text text)
    {
        chip.color = full ? RedBg : ReadyBg;
        text.text = full ? "가득 참" : "대기 중";
        text.color = full ? RedText : ReadyText;
    }

    private void UpdateRowHighlights()
    {
        foreach (var pair in rows)
        {
            bool on = pair.Key == selectedId;
            var stroke = pair.Value.Stroke;
            stroke.color = on ? SoloRowStrokeOn : SoloRowStroke;
            if (selectedStrokeSprite != null && normalStrokeSprite != null)
                stroke.sprite = on ? selectedStrokeSprite : normalStrokeSprite;
        }
    }

    // ───────────── 방 상세 ─────────────

    private void RefreshDetail()
    {
        var room = Selected;
        if (room == null) return;

        detailTitle.text = RoomTitle(room.hostNickname);
        detailLockChip.SetActive(false);
        bool full = IsFull(room);
        ApplyStatus(full, detailStatusChip, detailStatusText);
        detailMap.text = MapName(room.mapId);
        detailCount.text = $"{room.playerCount} / {room.maxPlayers}";

        // RoomSummary에는 참가자 명단이 없어서 방장 이름과 인원 수만 보여 준다
        for (int i = 0; i < slots.Length; i++)
        {
            var s = slots[i];
            s.Go.SetActive(i < room.maxPlayers);
            if (i >= room.maxPlayers) continue;
            if (i >= room.playerCount) { SetEmptySlot(s); continue; }

            Color color = UIPalette.PlayerColor(i);
            s.Bg.color = UIPalette.PlayerLightColor(i);
            s.Stroke.color = color;
            s.Ring.color = color;
            s.Portrait.color = color;
            s.Host.SetActive(i == 0);
            s.Nick.text = i == 0 ? room.hostNickname : "참가자";
            s.Nick.color = UIPalette.Ink;
        }

        detailProgress.SetActive(false); // 목록에는 대기 중인 방 정보만 온다

        bool canJoin = !full;
        detailJoin.interactable = canJoin;
        detailJoinImage.color = canJoin ? Mint : Neutral;
        detailJoinLabel.text = canJoin ? "참가하기" : "입장 불가";
        detailJoinLabel.color = canJoin ? Color.white : UIPalette.InkSub;
    }

    private static void SetEmptySlot(SlotView s)
    {
        s.Bg.color = Neutral;
        s.Stroke.color = Neutral;
        s.Ring.color = Neutral;
        s.Portrait.color = Neutral;
        s.Host.SetActive(false);
        s.Nick.text = "빈 자리";
        s.Nick.color = UIPalette.InkSub;
    }

    // ───────────── 방 만들기 ─────────────

    private void OpenCreatePopup()
    {
        mapIndex = FirstAvailableMap();
        ShowMap();
        for (int i = 0; i < maxToggles.Length; i++) maxToggles[i].SetIsOnWithoutNotify(i == maxToggles.Length - 1);
        createPopup.Open();
    }

    private void ChangeMap(int step)
    {
        mapIndex = NextAvailableMap(mapIndex, step); // 아직 없는 맵은 건너뛴다
        ShowMap();
    }

    private void ShowMap()
    {
        mapNameText.text = Maps[mapIndex];
        if (mapPreviewImage != null) mapPreviewImage.sprite = MapSprite(mapIndex);
        // 고를 수 있는 맵이 하나뿐이면 화살표를 눌러도 바뀌는 게 없으므로 잠가 둔다
        bool canChange = AvailableMapCount() > 1;
        if (prevMapButton != null) prevMapButton.interactable = canChange;
        if (nextMapButton != null) nextMapButton.interactable = canChange;
    }

    private void ConfirmCreate()
    {
        int max = 2 + Mathf.Max(0, Array.FindIndex(maxToggles, t => t.isOn)); // 최대 인원 토글은 2명부터
        RequestCreate(mapIndex, max);
        createPopup.Close();
    }

    // ───────────── 내 정보 ─────────────

    private void OpenPortraitPicker()
    {
        if (portraitPicker == null || Busy || !CanRequest) return;
        portraitPicker.Show(PortraitLibrary.SelectedId, ChoosePortrait);
    }

    // 선택창에서 초상화를 골랐다: 이 기기에 적용하고 서버에 저장을 요청한다. (방 안이면 서버가 ROOM_UPDATED로 다른 사람에게도 알린다)
    private void ChoosePortrait(int id)
    {
        PortraitLibrary.Select(id);
        if (CanRequest) LobbyNetwork.Instance.SendSetPortraitRequest(id);
    }

    // 내가 초상화를 골랐다: 내 정보와 (방 안이라면) 대기방의 내 자리에 바로 보인다
    private void HandlePortraitChanged()
    {
        RefreshProfilePortrait();
        if (roomSetup != null && roomSetup.IsOpen && CurrentRoom != null) ApplyRoomSnapshot(CurrentRoom);
    }

    private void RefreshProfilePortrait()
    {
        if (profilePortraitImage == null) return;
        var portrait = PortraitLibrary.Selected;
        profilePortraitImage.sprite = portrait;
        profilePortraitImage.color = portrait != null ? Color.white : UIPalette.PlayerColor(0);
    }

    // TODO: 보유 마블을 채운다
    private void RefreshProfile()
    {
        marbleText.text = "-";
        RefreshProfilePortrait();
        
        string nickname = LocalPlayer.Nickname;
        if (!string.IsNullOrEmpty(nickname)) RefreshNickname(nickname);
    }
    
    private void RefreshNickname(string localNickname)
    {
        Debug.Log($"로비화면 닉네임 업데이트 -> {localNickname}");
        nicknameText.text = localNickname;
    }
}