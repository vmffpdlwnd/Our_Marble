using System;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 로비 씬의 대기방 화면. 방에 들어오면 열리고, 방장이 시작하면 게임 씬으로 넘어간다.
/// 왼쪽은 플레이어(2×2로 놓인 자리 4개), 오른쪽은 방장이 정하는 게임 설정이다.
/// 자리는 최대 인원과 상관없이 항상 4개를 보여 주고, 최대 인원 밖의 빈 자리는 흐리게 "닫힘"으로 보인다.
///
/// 게임 설정(맵, 최대 인원, 시작 자금, 최대 라운드, 월급, 봇 수)은 바로 적용되지 않는다.
/// 방장이 값을 바꾸면 이 화면의 "초안"만 바뀌고, "설정 적용" 버튼을 눌러야 SettingsApplied로 한꺼번에 알린다.
/// 서버에 적용된 값(applied)은 SetSettings로 받는다. 자리와 인원 표시는 적용된 값 기준이고, 적용하지 않은 변경이 있으면 시작할 수 없다.
/// 방장이 아닌 플레이어는 적용된 값을 보기만 하고(조작 화살표는 보이지 않는다) 준비하기를 누른다.
/// 바꾼 설정을 적용하지 않고 되돌리려면 "변경 취소"를 누른다.
///
/// 이 화면은 받은 값을 보여 주고 알리기만 하고, 방 정보(RoomInfo)를 갱신하는 일은 받는 쪽(LobbyManager)이 한다.
/// 이벤트: SettingsApplied, ReadyToggled, LeaveRequested. 시작은 Show의 콜백으로 알린다. Canvas_RoomSetup에 붙는다.
/// </summary>
public class RoomSetupView : UIView
{
    public override string PanelPath => "RoomSetupPopup/Window";
    public override string DimPath => "RoomSetupPopup/Dim";

    /// <summary>슬롯 1칸 정보. IsEmpty면 빈 자리로 표시.</summary>
    public struct Slot
    {
        public bool IsEmpty;
        public string Name;
        public Sprite Portrait;
        public int ColorIndex;
        public bool IsHost;
        public bool IsBot;   // 봇이면 준비 대신 "봇" 표시를 단다
        public bool IsReady;
    }

    /// <summary>방장이 정하는 게임 설정 한 묶음. 적용 버튼을 누르면 이 묶음 전체가 한 번에 전달된다.</summary>
    public struct Settings
    {
        public string Map;      // 맵 이름 (SetMapChoices로 넘긴 목록 중 하나)
        public int MaxPlayers;  // 최대 인원 (2~4)
        public long StartMoney; // 시작 자금
        public int MaxRound;    // 최대 라운드
        public long Salary;     // 출발 지점을 지날 때 받는 월급
        public int BotCount;    // 봇 수. 봇은 맨 뒤에 붙고 맨 뒤에서 빠진다
    }

    private static readonly Color EmptySlotBg = new Color32(250, 251, 254, 255);     // 빈 자리: 밝은 면 + 또렷한 테두리
    private static readonly Color EmptySlotStroke = new Color32(188, 196, 214, 255);
    private static readonly Color LockedSlotBg = new Color32(226, 229, 237, 255);    // 닫힌 자리: 채워진 회색 면, 테두리 없음
    private static readonly Color BotBadgeBg = new Color32(228, 218, 248, 255);   // 봇 표시: 플레이어 색(분홍/파랑/초록/노랑)에 없는 보라
    private static readonly Color BotBadgeText = new Color32(116, 84, 186, 255);

    // 방장이 고를 수 있는 값. 화살표로 한 칸씩 넘긴다.
    private static readonly long[] StartMoneyChoices = { 200000, 300000, 500000, 1000000, 2000000 };
    private static readonly int[] MaxRoundChoices = { 10, 15, 20, 30, 40 };
    private static readonly long[] SalaryChoices = { 50000, 100000, 150000, 200000, 300000 };

    private const string UnappliedHint = "변경한 설정을 적용할까요?";
    private const float LockedSlotAlpha = 0.45f; // 최대 인원 밖의 빈 자리를 흐리게 보이는 정도

    [Serializable]
    private class SlotRefs
    {
        public Image background;
        public Image stroke;
        public Image portraitImage;
        public Image portraitRing;
        public GameObject hostBadge;
        public Image readyBadge;
        public TMP_Text readyText;
        public TMP_Text nicknameText;
        public RectTransform bubble;   // 초상화 위 말풍선
        public TMP_Text bubbleText;
        [NonSerialized] public Sequence bubbleTween;
    }

    [SerializeField] private Image mapImage;
    [SerializeField] private TMP_Text mapNameText;
    [SerializeField] private Button prevMapButton;
    [SerializeField] private Button nextMapButton;
    [SerializeField] private Toggle[] maxPlayersToggles = new Toggle[3]; // 2명, 3명, 4명
    [SerializeField] private TMP_Text playerCountText;
    [SerializeField] private SlotRefs[] slots = new SlotRefs[4];
    [SerializeField] private Button startButton;
    [SerializeField] private Button readyButton;
    [SerializeField] private TMP_Text readyLabel;
    [SerializeField] private Button closeButton;
    [SerializeField] private Button applyButton;
    [SerializeField] private Button cancelButton;

    // 오른쪽 위 방 코드 칩. 씬에 없고 SetRoomCode가 처음 불릴 때 "취소" 버튼을 복제해서 만든다
    private Button codeChip;
    private TMP_Text codeChipLabel;
    private string shownCode;
    private Action<string> onCodeClick;
    [SerializeField] private TMP_Text unappliedHintText;    // 적용하지 않은 변경이 있을 때 취소/적용 버튼 위에 뜨는 안내
    [SerializeField] private TMP_Text subtitleText;
    [SerializeField] private Button moneyPrevButton;
    [SerializeField] private Button moneyNextButton;
    [SerializeField] private TMP_Text moneyValueText;
    [SerializeField] private Button roundPrevButton;
    [SerializeField] private Button roundNextButton;
    [SerializeField] private TMP_Text roundValueText;
    [SerializeField] private Button salaryPrevButton;
    [SerializeField] private Button salaryNextButton;
    [SerializeField] private TMP_Text salaryValueText;
    [SerializeField] private Button botPrevButton;
    [SerializeField] private Button botNextButton;
    [SerializeField] private TMP_Text botValueText;
    [SerializeField] private GameObject settingsPanel;      // 게임 설정 탭의 내용 (맵, 모드, 최대 인원, 게임 설정)
    [SerializeField] private Toggle settingsTab;
    [SerializeField] private Toggle chatTab;
    [SerializeField] private GameObject chatTabDot;         // 게임 설정 탭을 보는 중에 새 채팅이 오면 켜진다
    [SerializeField] private GameObject chatPanel;
    [SerializeField] private ScrollRect chatScroll;
    [SerializeField] private RectTransform chatContent;
    [SerializeField] private RectTransform chatLineTemplate; // 채팅 한 줄의 견본 (꺼 둔다). Bubble/Name, Bubble/Body를 가진다
    [SerializeField] private GameObject chatEmptyText;      // 메시지가 없을 때 보이는 안내
    [SerializeField] private TMP_InputField chatInput;
    [SerializeField] private Button chatSendButton;

    /// <summary>방장이 "설정 적용"을 누르면 호출. 초안 전체가 넘어온다. 받는 쪽이 방 정보를 맞춘 뒤 SetSettings로 적용된 값을 돌려준다.</summary>
    public event Action<Settings> SettingsApplied;

    /// <summary>방장이 아닌 플레이어가 준비하기/준비 취소를 누르면 호출. true면 준비 완료.</summary>
    public event Action<bool> ReadyToggled;

    /// <summary>오른쪽 아래 나가기 버튼을 눌렀다. 방에서 나가겠다는 뜻이라, 창을 닫는 건 받는 쪽이 정한다.</summary>
    public event Action LeaveRequested;

    private bool isHost = true;
    private Settings applied;                // 서버에 적용된 설정
    private Settings draft;                  // 방장이 바꾸는 중인 설정 (적용 전)
    private string[] mapNames = Array.Empty<string>();
    private bool[] mapAvailable = Array.Empty<bool>(); // 고를 수 있는 맵. 비어 있으면 모두 고를 수 있다
    private Sprite[] mapPreviews = Array.Empty<Sprite>();

    public override void Bind()
    {
        const string w = "RoomSetupPopup/Window/";
        const string sp = w + "SettingsPanel/";
        mapImage = Find<Image>(sp + "MapSection/MapPreview/MapImage");
        mapNameText = Find<TMP_Text>(sp + "MapSection/MapSelector/MapNameText");
        prevMapButton = Find<Button>(sp + "MapSection/MapSelector/PrevMapButton");
        nextMapButton = Find<Button>(sp + "MapSection/MapSelector/NextMapButton");
        if (maxPlayersToggles == null || maxPlayersToggles.Length != 3) maxPlayersToggles = new Toggle[3]; // 이미 저장된 프리팹에는 배열이 없다
        for (int i = 0; i < maxPlayersToggles.Length; i++)
            maxPlayersToggles[i] = Find<Toggle>($"{sp}MaxPlayersSection/MaxPlayersToggle/Max{i + 2}Toggle");
        playerCountText = Find<TMP_Text>(w + "PlayerCountText");
        for (int i = 0; i < 4; i++)
        {
            string s = $"{w}PlayerSlots/PlayerSlot_{i + 1}/";
            slots[i] = new SlotRefs
            {
                background = Find<Image>(s.TrimEnd('/')),
                stroke = Find<Image>(s + "Stroke"),
                portraitImage = Find<Image>(s + "Portrait/PortraitMask/PortraitImage"),
                portraitRing = Find<Image>(s + "Portrait/Ring"),
                hostBadge = FindObject(s + "Portrait/HostBadge"),
                readyBadge = Find<Image>(s + "Portrait/ReadyBadge"),
                readyText = Find<TMP_Text>(s + "Portrait/ReadyBadge/Text"),
                nicknameText = Find<TMP_Text>(s + "NicknameText"),
                bubble = Find<RectTransform>(s + "Portrait/ChatBubble"),
                bubbleText = Find<TMP_Text>(s + "Portrait/ChatBubble/Text"),
            };
        }
        startButton = Find<Button>(w + "StartButton");
        readyButton = Find<Button>(w + "ReadyButton");
        readyLabel = Find<TMP_Text>(w + "ReadyButton/Label");
        closeButton = Find<Button>(w + "CloseButton");
        applyButton = Find<Button>(w + "ApplyButton");
        cancelButton = Find<Button>(w + "CancelButton");
        unappliedHintText = Find<TMP_Text>(w + "UnappliedHintText");
        subtitleText = Find<TMP_Text>(w + "SubtitleText");

        settingsPanel = FindObject(w + "SettingsPanel");
        settingsTab = Find<Toggle>(w + "TabBar/SettingsTab");
        chatTab = Find<Toggle>(w + "TabBar/ChatTab");
        chatTabDot = FindObject(w + "TabBar/ChatTab/Dot");
        chatPanel = FindObject(w + "ChatPanel");
        chatScroll = Find<ScrollRect>(w + "ChatPanel/Scroll");
        chatContent = Find<RectTransform>(w + "ChatPanel/Scroll/Viewport/Content");
        chatLineTemplate = Find<RectTransform>(w + "ChatPanel/Scroll/Viewport/Content/LineTemplate");
        chatEmptyText = FindObject(w + "ChatPanel/Scroll/EmptyText");
        chatInput = Find<TMP_InputField>(w + "ChatPanel/InputRow/Input");
        chatSendButton = Find<Button>(w + "ChatPanel/InputRow/SendButton");

        const string g = sp + "GameSettingsSection/Card/";
        moneyPrevButton = Find<Button>(g + "MoneyRow/Selector/PrevButton");
        moneyNextButton = Find<Button>(g + "MoneyRow/Selector/NextButton");
        moneyValueText = Find<TMP_Text>(g + "MoneyRow/Selector/ValueText");
        roundPrevButton = Find<Button>(g + "RoundRow/Selector/PrevButton");
        roundNextButton = Find<Button>(g + "RoundRow/Selector/NextButton");
        roundValueText = Find<TMP_Text>(g + "RoundRow/Selector/ValueText");
        salaryPrevButton = Find<Button>(g + "SalaryRow/Selector/PrevButton");
        salaryNextButton = Find<Button>(g + "SalaryRow/Selector/NextButton");
        salaryValueText = Find<TMP_Text>(g + "SalaryRow/Selector/ValueText");
        botPrevButton = Find<Button>(g + "BotRow/Selector/PrevButton");
        botNextButton = Find<Button>(g + "BotRow/Selector/NextButton");
        botValueText = Find<TMP_Text>(g + "BotRow/Selector/ValueText");
    }

    private void Awake()
    {
        SetOnClick(closeButton, () => LeaveRequested?.Invoke());
        SetOnClick(readyButton, OnReadyClicked);
        SetOnClick(applyButton, OnApplyClicked);
        SetOnClick(cancelButton, DiscardDraft);
        for (int i = 0; i < maxPlayersToggles.Length; i++)
        {
            int value = i + 2;
            if (maxPlayersToggles[i] != null) maxPlayersToggles[i].onValueChanged.AddListener(isOn => OnMaxPlayersToggled(value, isOn));
        }
        SetOnClick(prevMapButton, () => StepMap(-1));
        SetOnClick(nextMapButton, () => StepMap(1));
        SetOnClick(moneyPrevButton, () => StepStartMoney(-1));
        SetOnClick(moneyNextButton, () => StepStartMoney(1));
        SetOnClick(roundPrevButton, () => StepMaxRound(-1));
        SetOnClick(roundNextButton, () => StepMaxRound(1));
        SetOnClick(salaryPrevButton, () => StepSalary(-1));
        SetOnClick(salaryNextButton, () => StepSalary(1));
        SetOnClick(botPrevButton, () => StepBots(-1));
        SetOnClick(botNextButton, () => StepBots(1));
        InitChat();
        RefreshAll();
    }

    private void OnEnable()
    {
        ChatService.MessageReceived += OnChatMessage;
        RebuildChatLog(); // 닫혀 있던 동안 온 메시지까지 다시 그린다
    }

    private void OnDisable()
    {
        ChatService.MessageReceived -= OnChatMessage;
        foreach (var r in slots) HideBubble(r);
    }

    /// <summary>제목 옆 줄에 방 이름과 (있으면) 비밀번호를 보여 준다.</summary>
    /// <summary>
    /// 오른쪽 위에 "방 코드 ABCD-EFGH" 칩을 보여 준다. 누르면 onClick(코드)이 불린다.
    /// code가 비어 있으면(방 코드가 없는 혼자 하기) 칩을 숨긴다. 칩은 씬을 고치지 않고 "취소" 버튼을 복제해서 만든다.
    /// </summary>
    public void SetRoomCode(string code, Action<string> onClick)
    {
        if (string.IsNullOrEmpty(code))
        {
            if (codeChip != null) codeChip.gameObject.SetActive(false);
            return;
        }

        if (!EnsureCodeChip()) return;
        shownCode = code;
        onCodeClick = onClick;
        codeChipLabel.text = Loc.T("방 코드  {0}  (눌러서 복사)", code);
        codeChip.gameObject.SetActive(true);
    }

    private bool EnsureCodeChip()
    {
        if (codeChip != null) return true;
        if (cancelButton == null) return false;

        Transform window = transform.Find("RoomSetupPopup/Window");
        if (window == null) return false;

        GameObject copy = Instantiate(cancelButton.gameObject, window);
        copy.name = "RoomCodeChip";
        copy.SetActive(true);
        copy.transform.SetAsLastSibling();

        var rect = (RectTransform)copy.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.anchoredPosition = new Vector2(-60f, -24f); // 제목 줄의 오른쪽 끝 (구분선과 같은 여백)
        rect.sizeDelta = new Vector2(480f, 56f);

        codeChip = copy.GetComponent<Button>();
        if (codeChip == null) return false;
        codeChip.onClick.RemoveAllListeners();
        codeChip.onClick.AddListener(() => onCodeClick?.Invoke(shownCode));
        codeChip.interactable = true; // 복제한 "취소" 버튼이 눌러지지 않는 상태였어도 칩은 항상 눌린다

        codeChipLabel = copy.GetComponentInChildren<TMP_Text>(true);
        if (codeChipLabel == null) return false;
        codeChipLabel.enableAutoSizing = true;
        codeChipLabel.fontSizeMax = 22f;
        codeChipLabel.fontSizeMin = 16f;
        return true;
    }

    public void SetRoomInfo(string roomName, string password = null)
    {
        if (subtitleText == null) subtitleText = Find<TMP_Text>("RoomSetupPopup/Window/SubtitleText"); // 이미 만들어진 프리팹에는 연결돼 있지 않을 수 있다
        if (subtitleText == null) return;
        string text = roomName;
        if (!string.IsNullOrEmpty(password)) text += Loc.T("  ·  비밀번호 {0}", password);
        subtitleText.text = text;
    }

    /// <summary>onStart: 방장이 "게임 시작"을 눌렀을 때.</summary>
    public void Show(Action onStart)
    {
        legacyMode = false;
        SetOnClick(startButton, onStart);
        Open();
    }

    // ───────────── 기존 호출 호환 (LobbyManager) ─────────────
    // 서버와 연결된 LobbyManager는 예전 방식(항목마다 따로 설정하는 SetMode, SetMap, SetMaxPlayers와 5개 인자 Show)으로 이 화면을 쓴다.
    // 그 코드를 고치지 않아도 되도록 예전 함수와 이벤트를 남겨 두고, 새 설정 묶음(applied, draft)으로 이어 준다.
    // 새 코드는 SetMapChoices, SetSettings, SettingsApplied, 인자 1개 Show를 쓴다.

    /// <summary>(호환) 방장이 "설정 적용"으로 최대 인원을 바꾸면 호출. 서버가 받아들이면 SetMaxPlayers로 새 값이 돌아온다.</summary>
    public event Action<int> MaxPlayersChanged;

    private bool legacyMode;          // 예전 Show로 열었는지
    private Action legacyPrevMap;
    private Action legacyNextMap;

    /// <summary>(호환) onPrevMap / onNextMap: 방장이 맵 화살표를 누르면 호출. 맵은 서버가 바꿔서 SetMap으로 돌려준다.</summary>
    public void Show(Action onPrevMap, Action onNextMap, Action onStart)
    {
        Show(onStart);
        legacyMode = true; // 인자 1개 Show가 끄므로 그 뒤에 켠다
        legacyPrevMap = onPrevMap;
        legacyNextMap = onNextMap;
        RefreshAll();
    }

    /// <summary>(호환) 맵을 이름과 그림으로 보여 준다. 맵 목록이 없으면 이 맵 하나만 있는 것으로 본다.</summary>
    public void SetMap(string mapName, Sprite preview)
    {
        if (legacyMode || mapNames.Length == 0)
        {
            mapNames = new[] { mapName };
            mapPreviews = new[] { preview };
        }
        PatchApplied(s => { s.Map = mapName; return s; });
    }

    /// <summary>(호환) 서버에 적용된 최대 인원을 보여 준다.</summary>
    public void SetMaxPlayers(int max) => PatchApplied(s => { s.MaxPlayers = max; return s; });

    /// <summary>(호환) 맵 화살표는 SetHost를 따른다. 따로 켜고 끌 필요가 없다.</summary>
    public void SetMapSelectable(bool selectable) { }

    // 적용된 값 하나만 바꾼다. 방장이 바꾸던 초안은 그대로 둔다.
    private void PatchApplied(Func<Settings, Settings> patch)
    {
        var next = patch(applied);
        // 예전 호출은 시작 자금, 최대 라운드, 월급을 모른다. 처음에는 기본값에서 시작한다.
        if (next.StartMoney == 0) next.StartMoney = 500000;
        if (next.MaxRound == 0) next.MaxRound = 30;
        if (next.Salary == 0) next.Salary = 100000;
        SetSettings(next, true);
    }

    // 예전 호출에서의 "설정 적용": 서버가 아는 항목(최대 인원)은 이벤트로 알리고,
    // 서버가 모르는 항목(시작 자금, 최대 라운드, 월급, 봇)은 화면에서만 적용된 것으로 한다. (안 그러면 시작 버튼이 계속 잠긴다)
    private void ApplyLegacy()
    {
        var wanted = draft;
        var next = applied;
        next.StartMoney = wanted.StartMoney;
        next.MaxRound = wanted.MaxRound;
        next.Salary = wanted.Salary;
        next.BotCount = wanted.BotCount;
        bool maxChanged = wanted.MaxPlayers != applied.MaxPlayers;

        SetSettings(next, true);
        if (maxChanged) MaxPlayersChanged?.Invoke(wanted.MaxPlayers);
        RefreshAll();
    }

    // ───────────── 게임 설정 (초안과 적용) ─────────────

    /// <summary>고를 수 있는 맵의 이름과 미리보기 그림. 이름과 그림은 같은 순서다. 그림이 null이면 이름만 보여 준다.</summary>
    public void SetMapChoices(IList<string> names, IList<Sprite> previews, IList<bool> available = null)
    {
        mapNames = new string[names.Count];
        names.CopyTo(mapNames, 0);
        mapAvailable = new bool[mapNames.Length];
        for (int i = 0; i < mapAvailable.Length; i++) mapAvailable[i] = available == null || (i < available.Count && available[i]);
        mapPreviews = new Sprite[mapNames.Length];
        for (int i = 0; i < mapNames.Length; i++) mapPreviews[i] = previews != null && i < previews.Count ? previews[i] : null;
        RefreshAll();
    }

    /// <summary>
    /// 서버에 적용된 설정을 받는다. 목록에 없는 값은 가장 가까운 값으로 맞춘다.
    /// 자리와 인원 표시는 이 값을 따른다.
    /// keepDraft가 false면 방장의 초안도 이 값으로 되돌린다. (처음 열 때, 적용이 끝났을 때)
    /// true면 방장이 바꾸던 항목은 그대로 두고, 바꾸지 않은 항목만 새 값을 따라간다. (방 안에서 다른 변화가 서버에서 올 때)
    /// </summary>
    public void SetSettings(Settings settings, bool keepDraft = false)
    {
        settings.MaxPlayers = Mathf.Clamp(settings.MaxPlayers, 2, 4);
        settings.StartMoney = StartMoneyChoices[ClosestIndex(StartMoneyChoices, settings.StartMoney)];
        settings.MaxRound = MaxRoundChoices[ClosestIndex(MaxRoundChoices, settings.MaxRound)];
        settings.Salary = SalaryChoices[ClosestIndex(SalaryChoices, settings.Salary)];
        settings.BotCount = Mathf.Max(0, settings.BotCount);
        if (Array.IndexOf(mapNames, settings.Map) < 0 && mapNames.Length > 0) settings.Map = mapNames[0];

        var previous = applied;
        applied = settings;
        draft = keepDraft ? MergeDraft(previous, draft, settings) : settings;

        // 최대 인원이 바뀌면 닫힌 자리 표시가 달라지므로 자리를 다시 그린다
        for (int i = 0; i < slotData.Length; i++) SetSlot(i, slotData[i]);
        RefreshAll();
    }

    /// <summary>
    /// 이 화면의 플레이어가 방장인지. 방장만 게임 설정을 바꾸고 적용하고 게임을 시작할 수 있고,
    /// 방장이 아니면 시작 버튼 자리에 준비하기 버튼이 보인다.
    /// </summary>
    public void SetHost(bool host)
    {
        isHost = host;
        RefreshAll();
    }

    // 새로 적용된 값으로 바꾸되, 방장이 이미 바꿔 둔 항목(이전 적용값과 다른 항목)은 초안을 그대로 둔다
    private static Settings MergeDraft(Settings previous, Settings draft, Settings next)
    {
        if (draft.Map == previous.Map) draft.Map = next.Map;
        if (draft.MaxPlayers == previous.MaxPlayers) draft.MaxPlayers = next.MaxPlayers;
        if (draft.StartMoney == previous.StartMoney) draft.StartMoney = next.StartMoney;
        if (draft.MaxRound == previous.MaxRound) draft.MaxRound = next.MaxRound;
        if (draft.Salary == previous.Salary) draft.Salary = next.Salary;
        if (draft.BotCount == previous.BotCount) draft.BotCount = next.BotCount;
        return draft;
    }

    /// <summary>방장이 바꾸던 설정(적용 전)을 모두 버리고 적용된 값으로 되돌린다.</summary>
    private void DiscardDraft()
    {
        if (!isHost || SameSettings(applied, draft)) return;
        draft = applied;
        RefreshAll();
    }

    private void OnApplyClicked()
    {
        if (!isHost || SameSettings(applied, draft)) return;
        // SettingsApplied를 받는 쪽이 있으면 설정 묶음 전체를 넘긴다. 없을 때만 예전 방식(최대 인원만 이벤트)으로 처리한다
        if (SettingsApplied == null && legacyMode) { ApplyLegacy(); return; }
        SettingsApplied?.Invoke(draft);
    }

    private void OnMaxPlayersToggled(int value, bool isOn)
    {
        if (!isOn || value == draft.MaxPlayers) return;
        draft.MaxPlayers = value;
        RefreshAll();
    }

    private bool IsMapAvailable(int index) => index >= 0 && index < mapAvailable.Length && mapAvailable[index];

    private int AvailableMapCount()
    {
        int n = 0;
        foreach (bool a in mapAvailable) if (a) n++;
        return n;
    }

    private void StepMap(int step)
    {
        if (legacyMode) { if (isHost) (step < 0 ? legacyPrevMap : legacyNextMap)?.Invoke(); return; } // 맵은 서버가 바꿔서 돌려준다
        if (!isHost || mapNames.Length == 0) return;
        int index = Mathf.Max(0, Array.IndexOf(mapNames, draft.Map));
        for (int k = 1; k <= mapNames.Length; k++)
        {
            int next = ((index + step * k) % mapNames.Length + mapNames.Length) % mapNames.Length;
            if (!IsMapAvailable(next)) continue; // 아직 없는 맵은 건너뛴다
            draft.Map = mapNames[next];
            break;
        }
        RefreshAll();
    }

    private void StepStartMoney(int step)
    {
        if (!isHost) return;
        draft.StartMoney = StartMoneyChoices[Mathf.Clamp(ClosestIndex(StartMoneyChoices, draft.StartMoney) + step, 0, StartMoneyChoices.Length - 1)];
        RefreshAll();
    }

    private void StepMaxRound(int step)
    {
        if (!isHost) return;
        draft.MaxRound = MaxRoundChoices[Mathf.Clamp(ClosestIndex(MaxRoundChoices, draft.MaxRound) + step, 0, MaxRoundChoices.Length - 1)];
        RefreshAll();
    }

    private void StepSalary(int step)
    {
        if (!isHost) return;
        draft.Salary = SalaryChoices[Mathf.Clamp(ClosestIndex(SalaryChoices, draft.Salary) + step, 0, SalaryChoices.Length - 1)];
        RefreshAll();
    }

    // 봇 수를 한 명씩 늘리거나 줄인다. 사람과 봇을 합쳐 (초안의) 최대 인원을 넘을 수 없다.
    private void StepBots(int step)
    {
        if (!isHost) return;
        CountPlayers(out int humans, out _);
        draft.BotCount = Mathf.Clamp(draft.BotCount + step, 0, Mathf.Max(0, draft.MaxPlayers - humans));
        RefreshAll();
    }

    private static int ClosestIndex<T>(T[] choices, T value) where T : IComparable<T>
    {
        int best = 0;
        for (int i = 0; i < choices.Length; i++)
            if (choices[i].CompareTo(value) <= 0) best = i;
        return best;
    }

    private static bool SameSettings(Settings a, Settings b) =>
        a.Map == b.Map && a.MaxPlayers == b.MaxPlayers && a.StartMoney == b.StartMoney
        && a.MaxRound == b.MaxRound && a.Salary == b.Salary && a.BotCount == b.BotCount;

    // 사람 수와 봇 수를 센다 (적용된 자리 기준)
    private void CountPlayers(out int humans, out int bots)
    {
        humans = bots = 0;
        foreach (var slot in slotData)
        {
            if (slot.IsEmpty) continue;
            if (slot.IsBot) bots++; else humans++;
        }
    }

    // ───────────── 화면 갱신 ─────────────

    /// <summary>
    /// 화면 전체를 현재 상태(적용된 값, 초안, 자리, 방장 여부)에 맞춘다.
    /// 자리와 인원 표시는 적용된 값, 게임 설정 컨트롤은 초안, 적용 버튼과 시작 버튼은 둘의 차이를 따른다.
    /// </summary>
    private void RefreshAll()
    {
        CountPlayers(out int humans, out int bots);

        // 초안의 최대 인원이 줄면 봇도 같이 줄여서, 사람과 봇의 합이 최대 인원을 넘지 않게 한다
        draft.BotCount = Mathf.Clamp(draft.BotCount, 0, Mathf.Max(0, draft.MaxPlayers - humans));

        // 적용된 값: 인원 수 표시와 자리
        if (playerCountText != null) playerCountText.text = $"{humans + bots} / {applied.MaxPlayers}";

        // 초안: 맵
        int mapIndex = Array.IndexOf(mapNames, draft.Map);
        if (mapNameText != null) mapNameText.text = Loc.T(draft.Map);
        if (mapImage != null && mapIndex >= 0 && mapPreviews[mapIndex] != null)
        {
            mapImage.sprite = mapPreviews[mapIndex];
            mapImage.color = Color.white;
        }
        // 맵 화살표는 방장에게만 보인다. 참가자는 정해진 맵의 이름과 그림만 본다
        bool canStepMap = isHost && (AvailableMapCount() > 1 || legacyMode); // 고를 수 있는 맵이 하나뿐이면 화살표를 잠근다
        SetActive(prevMapButton != null ? prevMapButton.gameObject : null, isHost);
        SetActive(nextMapButton != null ? nextMapButton.gameObject : null, isHost);
        if (prevMapButton != null) prevMapButton.interactable = canStepMap;
        if (nextMapButton != null) nextMapButton.interactable = canStepMap;

        // 초안: 최대 인원
        for (int i = 0; i < maxPlayersToggles.Length; i++)
        {
            var toggle = maxPlayersToggles[i];
            if (toggle == null) continue;
            int value = i + 2;
            toggle.SetIsOnWithoutNotify(value == draft.MaxPlayers);
            toggle.interactable = isHost && value >= humans; // 지금 들어와 있는 사람보다 적게는 못 줄인다
        }

        // 초안: 시작 자금, 최대 라운드, 월급, 봇
        if (moneyValueText != null) moneyValueText.text = Loc.T("{0}원", UIPalette.Money(draft.StartMoney));
        if (roundValueText != null) roundValueText.text = Loc.T("{0}라운드", draft.MaxRound);
        if (salaryValueText != null) salaryValueText.text = Loc.T("{0}원", UIPalette.Money(draft.Salary));
        if (botValueText != null) botValueText.text = Loc.T("{0}명", draft.BotCount);
        SetStepper(moneyPrevButton, moneyNextButton, ClosestIndex(StartMoneyChoices, draft.StartMoney), StartMoneyChoices.Length);
        SetStepper(roundPrevButton, roundNextButton, ClosestIndex(MaxRoundChoices, draft.MaxRound), MaxRoundChoices.Length);
        SetStepper(salaryPrevButton, salaryNextButton, ClosestIndex(SalaryChoices, draft.Salary), SalaryChoices.Length);
        SetStepper(botPrevButton, botNextButton, draft.BotCount, Mathf.Max(0, draft.MaxPlayers - humans) + 1);

        // 적용 버튼과 시작 버튼
        bool dirty = !SameSettings(applied, draft);
        SetActive(applyButton != null ? applyButton.gameObject : null, isHost && !chatTabOpen);
        if (applyButton != null) applyButton.interactable = isHost && dirty;
        SetActive(cancelButton != null ? cancelButton.gameObject : null, isHost && !chatTabOpen);
        if (cancelButton != null) cancelButton.interactable = isHost && dirty;
        SetActive(startButton != null ? startButton.gameObject : null, isHost);
        SetActive(readyButton != null ? readyButton.gameObject : null, !isHost);
        RefreshHint(dirty);
        if (startButton != null) startButton.interactable = CanStart() && !dirty;
    }

    // 값을 한 칸씩 넘기는 화살표 한 쌍: index가 처음이면 이전을, 마지막(count - 1)이면 다음을 끈다. 방장이 아니면 화살표를 숨긴다.
    private void SetStepper(Button prev, Button next, int index, int count)
    {
        SetActive(prev != null ? prev.gameObject : null, isHost);
        SetActive(next != null ? next.gameObject : null, isHost);
        if (prev != null) prev.interactable = isHost && index > 0;
        if (next != null) next.interactable = isHost && index < count - 1;
    }

    // 적용하지 않은 변경이 있으면 취소/적용 버튼 위에 시작할 수 없다는 안내를 보여 준다. (왼쪽 아래의 기본 안내는 그대로 둔다)
    private void RefreshHint(bool dirty)
    {
        if (unappliedHintText == null) return;
        unappliedHintText.text = Loc.T(UnappliedHint);
        unappliedHintText.gameObject.SetActive(isHost && dirty && !chatTabOpen);
    }

    // ───────────── 자리 ─────────────

    /// <summary>자리 1칸을 그린다. 방장, 준비 상태에 따라 색과 배지가 달라진다.</summary>
    public void SetSlot(int index, Slot slot)
    {
        if (index < 0 || index >= slots.Length || slots[index] == null) return;
        slotData[index] = slot;
        var r = slots[index];

        // 빈 자리는 회색이고, 최대 인원 밖의 빈 자리(잠긴 자리)는 더 흐리게 해서 쓸 수 없다는 것을 보여 준다.
        bool locked = slot.IsEmpty && index >= applied.MaxPlayers;
        Color bgColor, strokeColor;
        if (slot.IsEmpty)
        {
            if (locked)
            {
                // 닫힌 자리: 채워진 회색 면에 테두리 없이 흐리게. 누가 들어올 수 없다는 느낌
                bgColor = LockedSlotBg;
                strokeColor = LockedSlotBg;
            }
            else
            {
                // 빈 자리: 밝은 면에 또렷한 회색 테두리. 사람이 들어올 수 있는 자리
                bgColor = EmptySlotBg;
                strokeColor = EmptySlotStroke;
            }
        }
        else
        {
            // 게임에서 쓰는 플레이어 색(자리 순서)을 옅게 깔아 누가 어떤 색인지 보이게 한다
            bgColor = UIPalette.PlayerLightColor(slot.ColorIndex);
            strokeColor = UIPalette.PlayerColor(slot.ColorIndex);
        }

        if (r.background != null) r.background.color = bgColor;
        if (r.stroke != null) r.stroke.color = strokeColor;
        // 초상화 테두리는 플레이어 색
        if (r.portraitRing != null)
            r.portraitRing.color = slot.IsEmpty ? bgColor : UIPalette.PlayerColor(slot.ColorIndex);
        SetPortrait(r.portraitImage, slot.IsEmpty ? null : slot.Portrait, Color.white);
        if (r.nicknameText != null)
        {
            r.nicknameText.text = slot.IsEmpty ? Loc.T(locked ? "닫힘" : "빈 자리") : Loc.TName(slot.Name);
            var textColor = slot.IsEmpty ? UIPalette.InkSub : UIPalette.Ink;
            if (locked) textColor.a = LockedSlotAlpha;
            r.nicknameText.color = textColor;
        }

        SetActive(r.hostBadge, !slot.IsEmpty && slot.IsHost);

        // 방장은 준비 표시가 없다. 봇은 항상 준비된 상태라 준비 대신 "봇" 표시를 단다
        bool showReady = !slot.IsEmpty && !slot.IsHost;
        if (r.readyBadge != null)
        {
            r.readyBadge.gameObject.SetActive(showReady);
            r.readyBadge.color = slot.IsBot ? BotBadgeBg : slot.IsReady ? UIPalette.ReadyBg : UIPalette.Neutral;
        }
        if (r.readyText != null)
        {
            r.readyText.text = Loc.T(slot.IsBot ? "봇" : slot.IsReady ? "준비 완료" : "대기 중");
            r.readyText.color = slot.IsBot ? BotBadgeText : slot.IsReady ? UIPalette.ReadyText : UIPalette.InkSub;
        }

        RefreshAll();
    }

    private bool localReady;

    /// <summary>이 화면의 플레이어가 준비한 상태인지 보여준다. (준비하기 ↔ 준비 취소)</summary>
    public void SetReady(bool ready)
    {
        localReady = ready;
        // 회색은 비활성처럼 보이므로 두 상태 모두 또렷한 색을 쓴다: 준비하기는 파랑, 준비 취소는 노랑
        if (readyLabel != null) readyLabel.text = Loc.T(ready ? "준비 취소" : "준비하기");
        if (readyButton != null && readyButton.targetGraphic is Image image)
            image.color = ready ? new Color32(255, 208, 130, 255) : new Color32(91, 155, 235, 255);
        if (readyLabel != null) readyLabel.color = ready ? new Color32(150, 100, 30, 255) : Color.white;
    }

    private void OnReadyClicked()
    {
        SetReady(!localReady);
        ReadyToggled?.Invoke(localReady);
    }

    /// <summary>
    /// 시작할 수 있는 자리 상태인지. 방장만 시작할 수 있고, 2명 이상이며, 방장 외 모든 플레이어가 준비를 마쳤어야 한다.
    /// (적용하지 않은 설정 변경이 없어야 하는 조건은 RefreshAll이 더한다)
    /// </summary>
    private bool CanStart()
    {
        int total = 0;
        bool allReady = true;
        foreach (var slot in slotData)
        {
            if (slot.IsEmpty) continue;
            total++;
            if (!slot.IsHost && !slot.IsReady) allReady = false;
        }
        return isHost && total >= 2 && allReady;
    }

    // ───────────── 탭 (게임 설정 / 채팅) ─────────────

    private bool chatTabOpen;

    private void InitChat()
    {
        if (chatLineTemplate != null) chatLineTemplate.gameObject.SetActive(false);
        if (settingsTab != null) settingsTab.onValueChanged.AddListener(on => { if (on) SetTab(false); });
        if (chatTab != null) chatTab.onValueChanged.AddListener(on => { if (on) SetTab(true); });
        SetOnClick(chatSendButton, SubmitChat);
        if (chatInput != null)
        {
            chatInput.characterLimit = ChatService.MaxLength;
            chatInput.onSubmit.AddListener(_ => SubmitChat());
            WebIMEInput.Attach(chatInput, keepOpenOnSubmit: true); // 엔터로 보낸 뒤에도 계속 입력
        }

        // 말풍선은 이웃 자리 위에도 보여야 하므로 따로 정렬하는 캔버스를 단다
        var rootCanvas = GetComponentInParent<Canvas>();
        foreach (var r in slots)
        {
            if (r == null || r.bubble == null) continue;
            var canvas = r.bubble.GetComponent<Canvas>();
            if (canvas == null) canvas = r.bubble.gameObject.AddComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = rootCanvas != null ? rootCanvas.sortingOrder + 1 : 1;
            if (r.bubble.GetComponent<CanvasGroup>() == null) r.bubble.gameObject.AddComponent<CanvasGroup>();
            if (r.bubbleText != null) r.bubbleText.richText = false; // 다른 사람이 보낸 <size> 같은 태그가 말풍선을 망치지 않게
            HideBubble(r);
        }
        SetTab(false);
    }

    /// <summary>
    /// 오른쪽 영역을 게임 설정 또는 채팅으로 바꾼다. 채팅을 열면 새 채팅 표시가 사라진다.
    /// 게임 설정 탭의 값(초안)은 탭을 오가도 그대로 남는다.
    /// </summary>
    public void SetTab(bool chat)
    {
        chatTabOpen = chat;
        SetActive(settingsPanel, !chat);
        SetActive(chatPanel, chat);
        if (settingsTab != null) settingsTab.SetIsOnWithoutNotify(!chat);
        if (chatTab != null) chatTab.SetIsOnWithoutNotify(chat);
        StyleTab(settingsTab, !chat);
        StyleTab(chatTab, chat);
        if (chat)
        {
            SetActive(chatTabDot, false);
            ScrollChatToEnd();
        }
        RefreshAll();
    }

    private static void StyleTab(Toggle tab, bool on)
    {
        if (tab == null) return;
        var label = tab.GetComponentInChildren<TMP_Text>();
        if (label != null) label.color = on ? UIPalette.Ink : UIPalette.InkSub; // 선택된 쪽의 흰 알약은 토글이 보여 준다
    }

    // ───────────── 채팅 ─────────────

    private void SubmitChat()
    {
        if (chatInput == null) return;
        string text = chatInput.text;
        chatInput.text = string.Empty;
        chatInput.ActivateInputField(); // 엔터로 보낸 뒤에도 계속 입력할 수 있게 한다
        if (string.IsNullOrWhiteSpace(text)) return;

        var me = slotData[localSlot];
        ChatService.Send(me.IsEmpty || string.IsNullOrEmpty(me.Name) ? Loc.T("나") : me.Name, me.ColorIndex, text);
    }

    private void OnChatMessage(ChatMessage message)
    {
        AddChatLine(message);
        ShowBubble(message);
        if (!chatTabOpen && !message.IsLocal) SetActive(chatTabDot, true);
    }

    private void RebuildChatLog()
    {
        if (chatContent == null || chatLineTemplate == null) return;
        for (int i = chatContent.childCount - 1; i >= 0; i--)
        {
            var child = chatContent.GetChild(i);
            if (child != chatLineTemplate) Destroy(child.gameObject);
        }
        foreach (var message in ChatService.History) AddChatLine(message, false);
        SetActive(chatEmptyText, ChatService.History.Count == 0);
        ScrollChatToEnd();
    }

    private const float ChatBubbleMaxWidth = 520f;
    private const float ChatBubblePaddingX = 40f;
    private static readonly Color ChatOtherBg = new Color32(238, 241, 247, 255);

    // 내 메시지는 오른쪽에 민트색, 다른 사람 메시지는 왼쪽에 회색 말풍선으로 쌓는다. 말풍선 위에 플레이어 색 점, 닉네임, 보낸 시각(00시00분)이 붙는다.
    private void AddChatLine(ChatMessage message, bool scroll = true)
    {
        if (chatContent == null || chatLineTemplate == null) return;
        var row = Instantiate(chatLineTemplate, chatContent);
        row.gameObject.SetActive(true);
        row.GetComponent<HorizontalLayoutGroup>().childAlignment = message.IsLocal ? TextAnchor.UpperRight : TextAnchor.UpperLeft;

        var bubble = row.Find("Bubble");
        bubble.GetComponent<Image>().color = message.IsLocal ? UIPalette.ReadyBg : ChatOtherBg;
        var nameText = bubble.Find("Name").GetComponent<TMP_Text>();
        var bodyText = bubble.Find("Body").GetComponent<TMP_Text>();
        string time = Loc.T("{0}시{1}분", message.Time.ToString("HH"), message.Time.ToString("mm"));
        nameText.text = $"<color={UIPalette.Hex(UIPalette.PlayerColor(message.ColorIndex))}>●</color> {Escape(message.Sender)}  <size=80%>{time}</size>";
        bodyText.text = message.Text;
        bodyText.richText = false;

        // 글 길이에 맞춰 말풍선 너비를 정한다 (길면 최대 너비에서 줄바꿈)
        float width = bodyText.GetPreferredValues(message.Text, ChatBubbleMaxWidth, 0f).x;
        width = Mathf.Max(width, nameText.GetPreferredValues(nameText.text).x);
        bubble.GetComponent<LayoutElement>().preferredWidth = Mathf.Ceil(Mathf.Min(width, ChatBubbleMaxWidth)) + ChatBubblePaddingX;

        SetActive(chatEmptyText, false);
        if (scroll) ScrollChatToEnd();
    }

    // 사용자가 쓴 글의 '<'가 서식으로 읽히지 않게 한다 (뒤에 보이지 않는 글자를 붙인다)
    private static string Escape(string text) => text?.Replace("<", "<​");

    private void ScrollChatToEnd()
    {
        if (chatScroll == null || !isActiveAndEnabled) return;
        Canvas.ForceUpdateCanvases();
        chatScroll.verticalNormalizedPosition = 0f;
    }

    // ───────────── 말풍선 ─────────────

    private const float BubbleMaxTextWidth = 190f;
    private const float BubbleHoldSeconds = 4f;
    private static readonly Vector2 BubblePadding = new Vector2(28f, 20f);

    // 보낸 사람의 닉네임과 같은 자리의 초상화 위에 말풍선을 띄운다. 봇 자리는 말하지 않는다.
    private void ShowBubble(ChatMessage message)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            var data = slotData[i];
            var r = slots[i];
            if (data.IsEmpty || data.IsBot || data.Name != message.Sender || r == null || r.bubble == null || r.bubbleText == null) continue;

            // 켜진 뒤에 글을 넣고 크기를 잰다. 꺼진 채로 재면 처음 한 번은 글 크기가 틀리게 나온다
            var group = r.bubble.GetComponent<CanvasGroup>();
            r.bubbleTween?.Kill();
            r.bubble.gameObject.SetActive(true);
            r.bubbleText.text = message.Text;
            r.bubbleText.ForceMeshUpdate();
            var textSize = r.bubbleText.GetPreferredValues(message.Text, BubbleMaxTextWidth, 0f);
            r.bubble.sizeDelta = new Vector2(Mathf.Min(textSize.x, BubbleMaxTextWidth), textSize.y) + BubblePadding;
            group.alpha = 1f;
            r.bubble.localScale = Vector3.one * 0.6f;
            r.bubbleTween = DOTween.Sequence().SetUpdate(true).SetLink(r.bubble.gameObject)
                .Append(r.bubble.DOScale(1f, 0.22f).SetEase(Ease.OutBack))
                .AppendInterval(BubbleHoldSeconds)
                .Append(group.DOFade(0f, 0.3f))
                .OnComplete(() => r.bubble.gameObject.SetActive(false));
            return;
        }
    }

    private static void HideBubble(SlotRefs r)
    {
        if (r == null || r.bubble == null) return;
        r.bubbleTween?.Kill();
        r.bubble.gameObject.SetActive(false);
    }

    // ───────────── 자리 정보 ─────────────

    // 아직 SetSlot 하지 않은 자리는 빈 자리로 센다
    private readonly Slot[] slotData =
    {
        new Slot { IsEmpty = true }, new Slot { IsEmpty = true },
        new Slot { IsEmpty = true }, new Slot { IsEmpty = true },
    };
    private int localSlot;

    /// <summary>이 화면의 플레이어가 몇 번째 슬롯인지(0~3). 채팅을 보낼 때 이 슬롯의 이름과 색을 쓴다.</summary>
    public void SetLocalPlayer(int slotIndex) => localSlot = Mathf.Clamp(slotIndex, 0, slotData.Length - 1);
}