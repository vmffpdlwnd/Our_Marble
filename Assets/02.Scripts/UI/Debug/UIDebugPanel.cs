using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;

/// <summary>
/// [임시] 게임 씬 UI 확인용 테스트 버튼 창. 에디터·개발 빌드에서만 그려지고, F1로 접거나 펼친다.
/// 확인이 끝나면 씬의 UIDebugPanel 오브젝트를 지우면 된다.
///
/// 하는 일
/// - GameManager(GameState)를 읽어 카드·차례·라운드를 채운다. 창과 연출은 GameManager가 UIManager를 직접 부르므로,
///   이 창은 상태를 만들어 보고(땅 주인, 현금, 파산) 창을 띄워 보는 일만 한다.
/// - 동기화 시험: 화면은 그대로 두고 GameState만 바꾼 뒤 UIManager.RefreshUIFromGameState로 화면을 맞춰 본다.
/// - 결과 창, 방(로비)으로 돌아가기 같은 씬 흐름을 바로 시험한다. (로비에서 들어온 방이 있어야 방으로 돌아온다)
/// - 보드 선택 시험: BoardManager 대신 칸 번호 목록을 UIManager.CheckTileValidForTravel / CheckTilesValidForSell에 넘긴다.
/// </summary>
public class UIDebugPanel : MonoBehaviour
{
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    // 로비를 거치지 않고 게임 씬만 실행했을 때 카드에 쓰는 이름. 로비에서 들어왔으면 방 안의 이름을 쓴다
    private static readonly string[] DefaultNames = { "나", "봇", "플레이어3", "플레이어4" };

    private int propertyCursor;
    private float speed = 1f;
    private bool expanded = true;
    private bool placed;
    private bool ready;
    private Rect windowRect = new Rect(16, 120, 460, 10);
    private Vector2 scroll;
    private string lastLog = "";

    private UIManager UI => UIManager.Instance;
    private GameManager Game => GameManager.Instance;
    private GameState State => Game != null ? Game.gameState : null;
    private List<PlayerState> Players => State.PlayerStates;

    // 이 화면의 플레이어 번호(PlayerStates 인덱스). GameManager가 UIManager에 알려 준 값을 따른다
    private int LocalPlayer => Mathf.Max(0, Players.FindIndex(p => p.PlayerId == UI.LocalPlayerId));

    private IEnumerator Start()
    {
        // GameManager.Start(StartGame: 초기 자금, 첫 턴)가 끝난 뒤에 읽는다
        yield return null;
        if (UI == null || Game == null) yield break;

        // 서버에서 게임 상태(GAME_STATE_SYNC)를 받기 전에는 플레이어 목록이 비어 있다. 받을 때까지 기다린다.
        while (State == null || State.PlayerStates == null || State.PlayerStates.Count == 0) yield return null;

        for (int i = 0; i < Players.Count; i++) UI.SetPlayerProfile(Players[i].PlayerId, NameOf(i));
        ready = true;

        FillHud(animate: false);
    }

    private void OnDisable()
    {
        DOTween.timeScale = 1f;
    }

    private void OnGUI()
    {
        if (UI == null) return;

        var e = Event.current;
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.F1) { expanded = !expanded; e.Use(); }

        // 1080p 기준으로 글자 크기를 맞춘다
        float s = Mathf.Max(1f, Screen.height / 1080f * 1.25f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));

        // 처음에는 왼쪽 플레이어 카드 두 장 사이 빈 공간에 둔다
        float viewHeight = Screen.height / s;
        if (!placed) { windowRect.y = viewHeight * 0.27f; placed = true; }
        windowRect.height = expanded ? Mathf.Max(120, viewHeight * 0.53f) : 10;
        windowRect = GUILayout.Window(98231, windowRect, DrawWindow, expanded ? "UI 테스트 (F1 접기)" : "UI 테스트 (F1)");
    }

    private void DrawWindow(int id)
    {
        if (!expanded)
        {
            if (GUILayout.Button("펼치기")) expanded = true;
            GUI.DragWindow();
            return;
        }

        if (Game == null)
        {
            GUILayout.Label("씬에 GameManager가 없습니다.");
            GUI.DragWindow();
            return;
        }
        if (!ready)
        {
            GUILayout.Label("GameManager 준비 중...");
            GUI.DragWindow();
            return;
        }

        GUILayout.Label($"연출 속도 x{speed:0.00}");
        float next = GUILayout.HorizontalSlider(speed, 0.1f, 1f);
        if (!Mathf.Approximately(next, speed)) { speed = next; DOTween.timeScale = speed; }
        GUILayout.Space(6);

        scroll = GUILayout.BeginScrollView(scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);

        DrawRoomSection();

        Header($"HUD (GameState 기준, {Players.Count}명)");
        if (GUILayout.Button("카드·차례 다시 채우기 (InitPlayers)")) { FillHud(animate: true); Log("카드·차례 다시 채우기"); }
        if (GUILayout.Button("다음 차례 (HandleTurnChanged)")) NextTurn();
        if (GUILayout.Button("통행료 주고받기 (HandleTollPaid)")) PayToll();
        if (GUILayout.Button("파산 켜기/끄기 (차례인 사람)")) ToggleBankrupt();

        Header("동기화 시험 (서버 상태만 바꾸기)");
        GUILayout.Label("화면은 그대로 두고 GameState만 바꿉니다. 바꾼 뒤 '화면 동기화'를 누르세요.");
        if (GUILayout.Button("내 돈 +100,000")) SilentChange(() => Players[LocalPlayer].Money += 100000, "내 돈 +100,000");
        if (GUILayout.Button("상대 돈 -100,000")) SilentChange(() => Players[NextPlayer(LocalPlayer)].Money -= 100000, "상대 돈 -100,000");
        if (GUILayout.Button("다음 차례로")) SilentChange(() => State.CurrentPlayerId = Players[NextPlayer(turnIndex)].PlayerId, "차례 변경");
        if (GUILayout.Button("라운드 +1")) SilentChange(() => State.RoundNumber += 1, "라운드 +1");
        if (GUILayout.Button("화면 동기화 (UIManager.RefreshUIFromGameState)")) { UI.RefreshUIFromGameState(); Log("화면 동기화"); }

        Header("주사위");
        if (GUILayout.Button("주사위 굴리기 창 열기 (버튼 → GameManager.RollDice)")) UI.ShowRollDicePopup();

        DrawBankruptSection();

        DrawPropertySection();

        Header("게임 종료 · 이동");
        if (GUILayout.Button("결과 창 띄우기 (UIManager.ShowGameResultPopup)")) { UI.ShowGameResultPopup(); Log("결과 창 (확인 또는 시간이 지나면 방으로 돌아간다)"); }
        Row(("방으로 돌아가기", () => SceneFlow.ReturnToRoom()), ("로비로 나가기", () => SceneFlow.ToLobby()));

        GUILayout.Space(4);
        if (GUILayout.Button("팝업 모두 닫기")) UI.CloseAllPopups();

        GUILayout.EndScrollView();

        if (!string.IsNullOrEmpty(lastLog)) GUILayout.Label(lastLog);
        GUI.DragWindow();
    }

    // ───────────── 방 정보 ─────────────

    // 로비에서 들어온 방(LobbyManager.CurrentRoom)을 보여 준다
    private void DrawRoomSection()
    {
        var room = LobbyManager.CurrentRoom;
        Header("방 정보");
        if (room == null)
        {
            GUILayout.Label("로비를 거치지 않고 실행했습니다. (방 정보 없음)");
            return;
        }
        // 이 클래스에 LocalPlayer라는 멤버(내 자리 번호)가 있어서, 정적 클래스 LocalPlayer는 global::로 구분한다
        int localIndex = room.Players.FindIndex(p => p.playerId == global::LocalPlayer.Id);
        GUILayout.Label($"{room.roomId} / 맵 {room.mapId} / {room.Players.Count}/{room.maxPlayers}명 (봇 {room.botCount})");
        GUILayout.Label($"내 자리 {localIndex + 1}번 ({(room.hostId == global::LocalPlayer.Id ? "방장" : "참가자")})");
    }

    // 카드에 쓰는 이름. 로비에서 들어왔으면 방 안의 이름, 아니면 기본 이름
    private static string NameOf(int index)
    {
        var room = LobbyManager.CurrentRoom;
        if (room != null && index < room.Players.Count) return room.Players[index].nickname;
        return index < DefaultNames.Length ? DefaultNames[index] : "플레이어" + (index + 1);
    }

    // ───────────── 상태 읽기 ─────────────

    // 지금 차례인 플레이어의 번호(PlayerStates 인덱스)
    private int turnIndex
    {
        get
        {
            int i = Players.FindIndex(p => p.PlayerId == State.CurrentPlayerId);
            return i < 0 ? 0 : i;
        }
    }

    private long Cash(int index) => Players[index].Money;

    // ───────────── HUD ─────────────

    // 카드 수·이름·돈·등수와 차례 표시를 GameState에 맞춰 처음부터 다시 채운다
    private void FillHud(bool animate)
    {
        UI.InitPlayers();
        UI.RefreshPlayerCards(animate);
        UI.ShowTurn(State.CurrentPlayerId);
    }

    // GameManager.HandleTurnChanged를 실제로 부른다. (다음 사람은 이 창이 정해서 넘긴다)
    private void NextTurn()
    {
        int next = NextPlayer(turnIndex);

        // 차례 표시, 주사위 창은 GameManager가 UIManager로 직접 띄운다
        Game.HandleTurnChanged(Players[next].PlayerId);
        Log($"{NameOf(next)} 차례");
    }

    // 이벤트도 화면 갱신도 없이 GameState만 바꾼다. (서버에서 상태가 바뀌었는데 화면은 모르는 경우를 흉내)
    private void SilentChange(System.Action change, string message)
    {
        change();
        Log($"서버 상태만 변경: {message}");
    }

    // GameState의 돈을 바꾸고 EconomyManager로 알린다 (돈 변화 표시는 이 경로 하나만 쓴다)
    private void ChangeMoney(int index, long amount, string reason)
    {
        PlayerState player = Players[index];
        long before = player.Money;
        player.Money += amount;
        UI.SetNextMoneyReason(player.PlayerId, reason);
        EconomyManager.NotifyMoneyChanged(player.PlayerId, before, player.Money);
    }

    private void PayToll()
    {
        int payer = turnIndex;
        int receiver = NextPlayer(payer);
        const long toll = 2000;
        if (receiver == payer || Cash(payer) < toll) { Log("통행료를 낼 돈이 부족합니다"); return; }

        Game.HandleTollPaid(Players[payer].PlayerId, Players[receiver].PlayerId, toll); // GameState 갱신
        Log($"통행료 {toll:N0}: {NameOf(payer)} → {NameOf(receiver)}");
    }

    private void ToggleBankrupt()
    {
        PlayerState player = Players[turnIndex];
        if (!player.IsBankrupt)
        {
            Game.ProcessBankruptcy(player.PlayerId); // GameState: IsBankrupt = true, FinalRank 기록 → 파산 연출까지 재생
            Log($"{NameOf(turnIndex)} 파산");
        }
        else
        {
            player.IsBankrupt = false;
            player.FinalRank = 0;
            UI.RefreshPlayerCards();
            Log($"{NameOf(turnIndex)} 파산 해제 (표시만 되돌립니다)");
        }
    }

    // ───────────── 파산 로직 시험 ─────────────
    // 파산하는 사람은 "나", 돈을 받는 사람은 다음 사람이다. 실제 GameManager의 파산 처리를 그대로 부른다.

    private void DrawBankruptSection()
    {
        Header("파산 로직 시험");
        for (int i = 0; i < Players.Count; i++)
        {
            PlayerState p = Players[i];
            int lands = 0; long sellValue = 0;
            foreach (PropertyState ps in State.PropertyStates)
                if (ps.OwnerId == p.PlayerId) { lands++; sellValue += PropertyManager.Instance.GetSellValue(ps); }
            GUILayout.Label($"{NameOf(i)}: 현금 {p.Money:N0} / 땅 {lands}개 (매각가 {sellValue:N0}) / {(p.IsBankrupt ? $"파산 {p.FinalRank}위" : "생존")}");
        }
        if (State.IsGameOver) GUILayout.Label("(게임 종료 상태)");

        Row(("내 땅 3개 만들기", () => GiveLands(Players[LocalPlayer].PlayerId, 3)),
            ("상대 땅 2개 만들기", () => GiveLands(Players[NextPlayer(LocalPlayer)].PlayerId, 2)));
        Row(("내 현금 0", () => SetMyMoney(0)), ("내 현금 1,000", () => SetMyMoney(1000)));

        RichLabel("<b>① 강제 파산</b> (통행료를 못 낼 때와 같은 처리: 재산을 상대에게 넘기고 다음 차례로)");
        if (GUILayout.Button("강제 파산 실행 (HandleBankruptcy → 다음 차례)")) RunBankrupt();

        if (GUILayout.Button("시험 상태 되돌리기 (파산·땅·게임 종료 초기화)")) ResetBankruptTest();
    }

    // 주인 없는 땅 중 처음 count개를 owner의 땅(1단계)으로 만든다
    private void GiveLands(long owner, int count)
    {
        foreach (PropertyData data in PropertyManager.Instance.GetAllDataByMapId(1))
        {
            if (count <= 0) break;
            if (!data.CanBuild) continue;
            PropertyState state = State.PropertyStates.Find(p => p.PropertyId == data.Id);
            if (state == null || state.OwnerId.HasValue) continue;
            state.OwnerId = owner;
            state.BuildingLevel = BuildingLevel.Villa;
            count--;
        }
        UI.RefreshPlayerCards();
        Log("땅을 나눠 줬습니다");
    }

    // payer(나)가 파산한다. 차례는 payer 것으로 맞춘 뒤 실제 처리 함수를 부른다.
    private void RunBankrupt()
    {
        int payerIndex = LocalPlayer;
        PlayerState payer = Players[payerIndex];
        PlayerState receiver = Players[NextPlayer(payerIndex)];
        if (payer.IsBankrupt || State.IsGameOver) { Log("이미 파산했거나 게임이 끝났습니다. '되돌리기'를 누르세요"); return; }
        if (receiver.PlayerId == payer.PlayerId) { Log("받을 사람이 없습니다"); return; }

        State.CurrentPlayerId = payer.PlayerId;
        UI.CloseAllPopups();
        Game.HandleBankruptcy(payer.PlayerId, receiver.PlayerId);
        if (!State.IsGameOver) Game.HandleTurnChanged(Players[NextPlayer(payerIndex)].PlayerId);
        Log($"{NameOf(payerIndex)} 강제 파산 → {NameOf(Players.FindIndex(p => p.PlayerId == receiver.PlayerId))}에게 재산 이전");
    }

    // 파산, 땅 주인, 게임 종료 상태를 처음으로 돌린다. (현금은 그대로)
    private void ResetBankruptTest()
    {
        foreach (PlayerState p in Players) { p.IsBankrupt = false; p.FinalRank = 0; }
        foreach (PropertyState ps in State.PropertyStates) { ps.OwnerId = null; ps.BuildingLevel = BuildingLevel.Land; }
        State.IsGameOver = false;
        UI.CloseAllPopups();
        UI.RefreshPlayerCards(false);
        UI.ShowTurn(State.CurrentPlayerId);
        Log("시험 상태를 되돌렸습니다 (현금은 그대로)");
    }

    // ───────────── 땅 (PropertyManager 데이터 + GameState) ─────────────

    private void DrawPropertySection()
    {
        List<PropertyData> all = PropertyManager.Instance.GetAllDataByMapId(1);
        Header("땅 / 실제 팝업 (GameManager 함수로 이어짐)");
        if (all == null || all.Count == 0) { GUILayout.Label("PropertyManager에 땅 데이터가 없습니다."); return; }

        propertyCursor = Mathf.Clamp(propertyCursor, 0, all.Count - 1);
        PropertyData data = all[propertyCursor];
        PropertyState state = Game.gameState.PropertyStates.Find(p => p.PropertyId == data.Id);
        long me = Players[LocalPlayer].PlayerId, other = Players[NextPlayer(LocalPlayer)].PlayerId;

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("◀")) propertyCursor = (propertyCursor + all.Count - 1) % all.Count;
        GUILayout.Label($"{data.CityName} ({data.Id})  {data.LandPrice:N0}", GUILayout.Width(180));
        if (GUILayout.Button("▶")) propertyCursor = (propertyCursor + 1) % all.Count;
        GUILayout.EndHorizontal();
        GUILayout.Label(state == null ? "상태 없음"
            : $"주인 {(state.OwnerId.HasValue ? NameOf(Players.FindIndex(p => p.PlayerId == state.OwnerId.Value)) : "없음")} / 단계 {state.BuildingLevel}");

        Row(("주인 없음", () => SetOwner(data.Id, null, 0)), ("내 땅", () => SetOwner(data.Id, me, 0)),
            ("상대 땅", () => SetOwner(data.Id, other, 0)));
        Row(("단계 +1", () => { if (state != null && state.BuildingLevel < BuildingLevel.Hotel) SetOwner(data.Id, state.OwnerId, (int)state.BuildingLevel + 1); }),
            ("내 돈 100,000", () => SetMyMoney(100000)), ("내 돈 100", () => SetMyMoney(100)));
        GUILayout.Label("아래 창의 버튼 → GameManager.Purchase/Build/Acquire 호출. 매각은 아래 '보드 선택 시험'에서");
        Row(("구매 창", () => UI.ShowPurchasePropertyPopup(me, data.Id, isPurchasable: true)),
            ("건설 창", () => UI.ShowBuildPopup(me, data.Id, isBuildable: true)));
        Row(("구매 창 (금액 부족)", () => UI.ShowPurchasePropertyPopup(me, data.Id, isPurchasable: false)),
            ("건설 창 (금액 부족)", () => UI.ShowBuildPopup(me, data.Id, isBuildable: false)));
        Row(("인수 창", () => UI.ShowAcquirePropertyPopup(me, data.Id)),
            ("타일 정보", () => UI.ShowTileInfoPopup(data.Id)));

        DrawBoardSelectSection(me, other);
    }

    // ───────────── 보드 선택 시험 (BoardManager 대신 칸 번호 목록을 넘긴다) ─────────────

    private int tileCursor;                       // 시험용으로 가리키는 칸 번호
    private readonly List<int> travelTiles = new(); // '보드에서 고른' 여행지 칸 번호들
    private readonly List<int> sellTiles = new();   // '보드에서 고른' 매각할 칸 번호들
    private static readonly long[] SellRequiredPresets = { 10000, 30000, 100000, 500000 };
    private int sellPresetIndex = 1;

    private void DrawBoardSelectSection(long me, long other)
    {
        Header("보드 선택 시험 (칸 번호 목록을 직접 넘김)");
        GUILayout.Label("창을 열면 UIManager가 보드 모드도 바꿉니다. (ChangeTo~)");

        int count = BoardManager.Instance.TileCount;
        if (count <= 0) { GUILayout.Label("보드 데이터가 없습니다."); return; }
        tileCursor = Mathf.Clamp(tileCursor, 0, count - 1);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("◀")) tileCursor = (tileCursor + count - 1) % count;
        GUILayout.Label($"{DescribeTile(tileCursor)}", GUILayout.Width(200));
        if (GUILayout.Button("▶")) tileCursor = (tileCursor + 1) % count;
        GUILayout.EndHorizontal();

        RichLabel("<b>세계여행</b> (칸 종류와 상관없이 하나만 고르면 완료 버튼이 켜집니다)");
        Row(("창 열기", () => { travelTiles.Clear(); UI.ShowChooseDestinationPopup(); }),
            ("창 닫기", () => UI.HideChooseDestinationPopup()));
        Row(("이 칸 선택/해제", () => { Toggle(travelTiles, tileCursor); UI.CheckTileValidForTravel(new List<int>(travelTiles)); }),
            ("모두 해제", () => { travelTiles.Clear(); UI.CheckTileValidForTravel(new List<int>()); }));
        GUILayout.Label($"고른 칸: {ListText(travelTiles)}");

        RichLabel("<b>매각</b> (내 땅만 합산, 현금 + 매각가 합 ≥ 필요 금액이면 완료 버튼이 켜집니다)");
        Row(($"필요 금액 {SellRequiredPresets[sellPresetIndex]:N0} (눌러서 변경)", () => sellPresetIndex = (sellPresetIndex + 1) % SellRequiredPresets.Length),
            ("창 열기", () => { sellTiles.Clear(); UI.ShowSellPropertiesPopup(me, other, SellRequiredPresets[sellPresetIndex]); }));
        Row(("이 칸 선택/해제", () => { Toggle(sellTiles, tileCursor); UI.CheckTilesValidForSell(new List<int>(sellTiles)); }),
            ("모두 해제", () => { sellTiles.Clear(); UI.CheckTilesValidForSell(new List<int>()); }));
        GUILayout.Label($"고른 칸: {ListText(sellTiles)}");

        RichLabel("<b>둘러보기</b>");
        if (GUILayout.Button("이 칸 클릭 (땅이면 정보창)")) UI.OnBoardTileClicked(tileCursor);
    }

    private static void Toggle(List<int> list, int value)
    {
        if (!list.Remove(value)) list.Add(value);
    }

    private static string ListText(List<int> list) => list.Count == 0 ? "없음" : string.Join(", ", list);

    // 칸 번호의 종류(땅이면 도시 이름)
    private static string DescribeTile(int index)
    {
        TileData tile = BoardManager.Instance.GetTileData(index);
        if (tile == null) return $"{index}번 (없음)";
        if (tile.Type != TileType.PROPERTY) return $"{index}번 {tile.Type}";
        PropertyData data = PropertyManager.Instance.GetData(tile.PropertyId);
        return $"{index}번 {(data != null ? data.CityName : "땅 " + tile.PropertyId)}";
    }

    private void SetOwner(int propertyId, long? owner, int level)
    {
        PropertyState state = Game.gameState.PropertyStates.Find(p => p.PropertyId == propertyId);
        if (state == null) return;
        state.OwnerId = owner;
        state.BuildingLevel = (BuildingLevel)level;
        UI.RefreshPlayerCards();
        Log($"땅 {propertyId}: 주인 {(owner.HasValue ? owner.Value.ToString() : "없음")}, 단계 {(BuildingLevel)level}");
    }

    private void SetMyMoney(long amount) => ChangeMoney(LocalPlayer, amount - Players[LocalPlayer].Money, "설정");

    // from 다음 차례의 (파산하지 않은) 플레이어. 다른 사람이 없으면 from의 다음 번호
    private int NextPlayer(int from)
    {
        for (int step = 1; step <= Players.Count; step++)
        {
            int i = (from + step) % Players.Count;
            if (!Players[i].IsBankrupt && i != from) return i;
        }
        return (from + 1) % Players.Count;
    }

    // ───────────── GUI 헬퍼 ─────────────

    private static GUIStyle richLabel;

    // 굵은 글씨 같은 리치 텍스트를 쓰는 라벨. 스타일은 한 번만 만든다
    private static void RichLabel(string text)
    {
        richLabel ??= new GUIStyle(GUI.skin.label) { richText = true };
        GUILayout.Label(text, richLabel);
    }

    private static void Header(string text)
    {
        GUILayout.Space(8);
        RichLabel($"<b>{text}</b>");
    }

    private static void Row(params (string label, System.Action action)[] buttons)
    {
        GUILayout.BeginHorizontal();
        foreach (var (label, action) in buttons)
            if (GUILayout.Button(label)) action();
        GUILayout.EndHorizontal();
    }

    private void Log(string message)
    {
        lastLog = message;
        Debug.Log($"[UIDebugPanel] {message}");
    }
#endif
}