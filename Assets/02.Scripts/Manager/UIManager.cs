using System;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 게임 화면 UI 진입점. GameManager는 여기서 창과 연출을 꺼내 쓴다.
/// 창을 띄우는 함수가 (playerId, propertyId 같은) 값만 받아 필요한 데이터를 스스로 조회하고,
/// 버튼을 누르면 주석에 적힌 GameManager 함수를 호출한다.
/// 뷰 참조는 메뉴 Tools > Our Marble > Game UI 연결 로 채운다.
/// </summary>
/// <summary>제한 시간이 있는 선택창의 종류. 시간이 끝났을 때 어떤 창이었는지 알려주는 데 쓴다.</summary>
public enum SelectionKind
{
    RollDice,          // 주사위 굴리기
    PurchaseOrBuild,   // 땅 구매 / 건설
    Takeover,          // 인수
    Sell,              // 매각
    Destination,       // 세계여행 목적지
    IslandEscape,      // 무인도 탈출 방법
}

public class UIManager : MonoBehaviour
{
    public static UIManager Instance { get; private set; }

    [Header("HUD")]
    [SerializeField] private PlayerInfoView[] playerInfos = new PlayerInfoView[4];
    [SerializeField] private TopBarView topBar;
    [SerializeField] private CurrentTurnView currentTurn;
    [SerializeField] private DiceRollView diceRoll;
    [SerializeField] private DiceResultView diceResult;

    [Header("팝업")]
    [SerializeField] private PurchasePopupView purchase;
    [SerializeField] private TakeoverPopupView takeover;
    [SerializeField] private SellPopupView sell;
    [SerializeField] private IslandEscapeView islandEscape;
    [SerializeField] private CardDrawView cardDraw;
    [SerializeField] private DestinationSelectView destinationSelect;
    [SerializeField] private TileInfoView tileInfo;
    [SerializeField] private GameResultView gameResult;
    [SerializeField] private ExitConfirmView exitConfirm;
    [SerializeField] private SettingsView settings;

    [Header("데이터")]
    [Tooltip("황금열쇠 카드의 이름·설명·아이콘을 찾는 덱. CardManager가 쓰는 덱과 같은 에셋을 연결한다.")]
    [SerializeField] private CardDeckData cardDeck;
    [Tooltip("다른 플레이어가 뽑은 황금열쇠 카드를 보여주는 시간(초). 카드가 뒤집힌 뒤부터 센다.")]
    [SerializeField] private float otherCardShowSeconds = 2f;
    [Tooltip("게임이 끝난 뒤 결과를 보여 주는 시간(초). 이 시간이 지나면 확인을 누르지 않아도 방(로비)으로 돌아간다.")]
    [SerializeField] private float resultSeconds = 15f;
    [Header("선택 제한시간")]
    // 선택창의 제한시간은 시간 규칙(TimeRules)이 정한다. 호스트가 대신 진행하는 시간보다 먼저 닫힌다.
    [Tooltip("시간이 끝난 뒤 버튼을 잠그고 창을 닫기까지 기다리는 시간(초)")]
    [SerializeField] private float timeoutCloseDelay = 0.3f;
    private int maxRound = 30; // 상단바에 '라운드 N / 최대'로 보여줄 최대 라운드. 값은 GameManager가 SetMaxRound로 알려주고, 그 전까지는 이 기본값을 쓴다.

    #region 밖에서 연결하는 곳

    /// <summary>
    /// 특수 칸(PropertyData.CanBuild == false, 예: 카페)의 효과 설명을 돌려주는 함수. propertyId를 받아 문장을 돌려줍니다.
    /// 지정하지 않거나 빈 문장이면 기본 안내가 나옵니다. (설명 데이터가 PropertyData에 생기면 여기서 읽으면 됩니다.)
    /// </summary>
    public Func<int, string> PropertyEffectDescription { get; set; }

    /// <summary>
    /// 선택 제한시간이 끝나 창이 닫혔을 때 (어떤 창이었는지). UIManager는 창을 잠그고 닫기만 하고,
    /// 시간이 끝난 뒤의 처리(예: 구매하지 않기, 자동 주사위)는 구독하는 쪽(GameManager)이 정한다.
    /// </summary>
    public event Action<SelectionKind> SelectionTimedOut;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        CloseAllPopups(immediate: true);

        if (diceRoll != null)
        {
            // 굴리기 버튼은 기본적으로 잠겨 있고, 내 턴에 ShowRollDicePopup()을 부르면 켜진다
            diceRoll.SetInteractable(false);
            diceRoll.SetGauge(0f);
            // 무인도 표시는 갇힌 플레이어의 주사위 UI를 열 때만 켠다
            diceRoll.SetIslandTurns(0);
        }
        // 주사위 결과창이 닫히면 게이지를 비운다 (두 뷰가 이 오브젝트와 같은 수명이라 구독을 해제하지 않는다)
        if (diceResult != null && diceRoll != null) diceResult.Closed += () => diceRoll.SetGauge(0f);

        // 선택창이 열리면 제한시간을 세기 시작한다 (각 Show 함수는 손대지 않는다)
        HookSelectionTimer(diceRoll, SelectionKind.RollDice);
        HookSelectionTimer(purchase, SelectionKind.PurchaseOrBuild);
        HookSelectionTimer(takeover, SelectionKind.Takeover);
        HookSelectionTimer(sell, SelectionKind.Sell);
        HookSelectionTimer(destinationSelect, SelectionKind.Destination);
        HookSelectionTimer(islandEscape, SelectionKind.IslandEscape);

        // 상단바: 로비에서 넘어온 방 정보 표시, 설정은 설정 창 열기/닫기, 나가기는 확인 후 로비로 돌아간다
        if (topBar != null)
        {
            RefreshTopBarRoom();
            topBar.SetPassword(null); // 서버 방 정보에는 비밀번호가 없다
            topBar.SetCallbacks(settings != null ? settings.Open : (Action)null, RequestExit);
        }
    }

    /// <summary>
    /// 상단바의 방 이름과 인원수를 맞춘다. 로비에서 들어왔으면 그 방 정보(LobbyManager.CurrentRoom)를,
    /// 재접속해서 방 정보가 없으면 서버가 게임 상태에 담아 준 방장(HostId)·최대 인원(MaxPlayers)을 쓴다.
    /// 둘 다 없으면 방 이름은 그대로 두고, 인원은 플레이어 수로 표시한다.
    /// </summary>
    private void RefreshTopBarRoom()
    {
        if (topBar == null) return;
        GameState state = State;
        var room = LobbyManager.CurrentRoom; // 로비를 거치지 않고 게임 씬만 실행하거나 재접속하면 null

        if (room != null)
        {
            var host = room.Players?.Find(p => p.playerId == room.hostId);
            topBar.SetRoom(LobbyManager.RoomTitle(host?.nickname));
            topBar.SetPlayerCount(room.Players != null ? room.Players.Count : 0, room.maxPlayers);
            return;
        }

        if (state == null || state.PlayerStates == null || state.PlayerStates.Count == 0) return;
        if (state.HostId.HasValue)
        {
            PlayerState host = GetPlayer(state.HostId.Value);
            topBar.SetRoom(LobbyManager.RoomTitle(host?.Nickname));
        }
        int count = state.PlayerStates.Count;
        topBar.SetPlayerCount(count, state.MaxPlayers ?? count);
    }
    #endregion

    #region 화면 동기화 (재접속 · 지연)

    /// <summary>
    /// 재접속하거나 지연으로 화면이 서버 상태와 어긋났을 때, 화면(UI)만 지금 GameState에 맞춘다.
    /// GameState는 읽기만 하고 바꾸지 않으며, 서버에서 상태를 받아 오는 일도 하지 않는다.
    /// 화면과 달라진 부분만 평소 변할 때와 같은 연출로 바뀐다:
    /// 현금이 달라졌으면 숫자가 굴러가며 돈 변화 표시("입금"/"출금")가 뜨고, 등수는 뒤집히고, 파산이면 도장이 찍히고,
    /// 차례가 달라졌으면 차례 표시와 카드 강조가 바뀌고, 라운드가 달라졌으면 숫자가 튄다. 달라진 게 없으면 아무 일도 없다.
    /// 서버 상태를 받아 GameState를 갱신한 쪽(GameManager 또는 네트워크)이 그 뒤에 이 함수를 부르면 된다.
    /// </summary>
    public void RefreshUIFromGameState()
    {
        GameState state = State;
        if (state == null) return;

        for (int i = 0; i < state.PlayerStates.Count; i++)
        {
            PlayerInfoView view = GetPlayerInfo(i);
            if (view == null) continue;
            PlayerState player = state.PlayerStates[i];
            // 코인 연출 중인 사람은 연출이 도착하는 순간 돈 표시를 직접 갱신하므로 건너뛴다 (같은 변화가 두 번 뜨지 않게)
            if (transferPlayers.Contains(player.PlayerId)) continue;

            // 현재 초상화시스템이 미구현되어있기 때문에 view.SetProfile함수 안쓰고 임시로 생성한 SetName함수 씁니다. TODO: 초상화시스템, 색상시스템 도입하고나서 SetProfile쓰도록 고치기
            view.SetName(player.Nickname);
            Debug.Log($"UIManager - RefreshUIFromGameState - view.SetName({player.Nickname});");
            
            long delta = player.Money - view.CashTarget;
            view.SetMoney(player.Money, TotalAssetOf(player.PlayerId, player.Money));
            if (delta != 0) view.ShowMoneyChange(TakeMoneyReason(player.PlayerId, delta), delta);
            view.SetBankrupt(player.IsBankrupt); // 새로 파산했으면 도장 연출
        }
        UpdateRanks(); // 등수가 달라진 카드만 뒤집힌다

        // 차례: 화면에 강조된 카드와 서버의 현재 차례가 다르면 차례 표시를 바꾼다
        int turnIndex = IndexOf(state.CurrentPlayerId);
        PlayerInfoView shownTurn = turnIndex >= 0 ? GetPlayerInfo(turnIndex) : null;
        if (shownTurn != null && !shownTurn.IsTurnShown) ShowTurn(state.CurrentPlayerId);

        RefreshTopBarRoom(); // 재접속해서 들어온 경우에도 방 이름과 인원이 서버 상태에 맞게 보인다
        if (topBar != null) topBar.SetRound(DisplayRound(), maxRound, animate: true);
    }

    #endregion

    #region 나가기 · 돈 변화 구독
    // 나가기 버튼: 로비로 돌아갈지 묻고, 확인하면 돌아간다
    private void RequestExit()
    {
        if (exitConfirm != null) exitConfirm.Show(() => SceneFlow.ToLobby());
        else SceneFlow.ToLobby();
    }

    // 돈이 바뀌면 EconomyManager가 알려준다. 돈 표시와 연출은 여기서 갱신한다.
    private void OnEnable() => EconomyManager.OnMoneyChanged += HandleMoneyChanged;
    private void OnDisable() => EconomyManager.OnMoneyChanged -= HandleMoneyChanged;
    #endregion

    #region 창 띄우기 (GameManager가 호출)

    // 자유여행으로 가고싶은 칸을 선택하는 창을 띄워주는 함수입니다.
    // 보드가 칸을 눌릴 때마다 CheckTileValidForTravel(고른 칸 번호들)을 부르고, 하나만 골라졌을 때 "여행지 선택 완료" 버튼이 켜집니다.
    // 완료 버튼을 누르면 GameManager로 목적지를 넘깁니다.
    public void ShowChooseDestinationPopup()
    {
        chooseDestinationMode = true;
        travelTileIndex = -1;
        if (diceRoll != null) diceRoll.SetInteractable(false); // 목적지를 고르는 동안에는 주사위를 굴릴 수 없다

        // 보드를 여행지 선택 모드로 바꾼다
        BoardManager.Instance.ChangeToSelectTravelMode();

        if (destinationSelect != null) destinationSelect.Show(CompleteDestination); // 완료 버튼은 꺼진 상태로 시작
    }

    /// <summary>
    /// 보드의 칸을 누를 때마다 BoardManager가 부르는 함수입니다. tileIndexes: 지금 고른 칸 번호들.
    /// 정확히 1개일 때만 "여행지 선택 완료" 버튼을 켭니다. (칸의 종류는 따로 보지 않습니다)
    /// </summary>
    public void CheckTileValidForTravel(List<int> tileIndexes)
    {
        bool valid = tileIndexes != null && tileIndexes.Count == 1;
        travelTileIndex = valid ? tileIndexes[0] : -1;
        if (destinationSelect != null) destinationSelect.SetCompleteInteractable(valid);
    }

    private int travelTileIndex = -1; // CheckTileValidForTravel이 마지막으로 확인한 여행지 칸

    // "여행지 선택 완료" 버튼
    private void CompleteDestination()
    {
        if (travelTileIndex < 0) return;
        int destination = travelTileIndex;
        chooseDestinationMode = false;
        travelTileIndex = -1;
        if (destinationSelect != null) destinationSelect.Close();

        // 보드를 다시 둘러보기 모드로 되돌린다
        BoardManager.Instance.ChangeToInspectMode();

        // 고른 목적지를 GameManager로 넘긴다
        if (Game != null) Game.ChooseDestination(destination);
    }

    /// <summary>목적지 선택 창이 떠 있고 칸을 고르기를 기다리는 중인지</summary>
    public bool IsChoosingDestination => chooseDestinationMode;

    // 목적지 선택 창을 선택 없이 닫는 함수입니다. (보드 선택이 구현되기 전의 임시 흐름에서 씁니다.)
    public void HideChooseDestinationPopup()
    {
        chooseDestinationMode = false;
        travelTileIndex = -1;
        if (destinationSelect != null) destinationSelect.Close();
        // BoardManager.Instance.ChangeToInspectMode(); // TODO: BoardManager 구현 후 주석 해제
    }

    // 주사위 굴리기 버튼이 있는 창을 띄워주는 함수입니다.
    // 굴리기 버튼을 누르면 GameManager.RollDice() 를 호출합니다.
    // 현재 차례 플레이어가 무인도에 갇혀 있으면 남은 턴도 함께 보여줍니다.
    public void ShowRollDicePopup()
    {
        if (diceRoll == null) return;

        PlayerState player = CurrentPlayer;
        int islandTurns = player != null ? player.IslandTurnsRemaining : 0;
        diceRoll.Show(power =>
        {
            // 버튼을 누르는 즉시 결과를 요청하고, 결과(ShowDiceResult)가 올 때까지 주사위는 구르는 상태로 둡니다.
            // 나중에는 Game.RollDice() 대신 서버 요청을 보내고, 응답을 받을 때 ShowDiceResult를 부르면 됩니다.
            if (diceResult != null) diceResult.StartRolling();
            if (Game != null) Game.RollDice();
        }, islandTurns);
    }

    // 빈 땅을 사거나 사지 않는 버튼이 있는 창을 띄워주는 함수입니다.
    // 사기 버튼을 누르면 GameManager.PurchaseProperty(playerId, propertyId) 를 호출합니다.
    // 사지않기 버튼을 누르면 GameManager.DeclinePropertyPurchase(playerId, propertyId) 를 호출합니다.
    // 이 창은 빈 땅 전용입니다. (내 땅의 건설은 GameManager가 ShowBuildPopup으로 따로 부릅니다.)
    // 땅 구매 창은 '건물(땅 구매)'만 고를 수 있고, 별 카드는 구매 뒤 건설 단계라 잠겨서 보입니다.
    // isPurchasable: 구매할 수 있는지 여부입니다. GameManager가 판단해서 넘겨주세요. (UIManager는 판단하지 않습니다)
    //   false이면 건물 카드가 "금액 부족"으로 잠기고, 창이 뜰 때 아무것도 선택되지 않은 상태로 시작하며,
    //   구매 버튼은 켜지지 않습니다. (사지않기는 누를 수 있습니다)
    public void ShowPurchasePropertyPopup(long playerId, long propertyId, bool isPurchasable)
    {
        if (purchase == null) return;

        int id = (int)propertyId;
        PropertyData data = PropertyManager.Instance.GetData(id);
        PlayerState player = GetPlayer(playerId);
        long landPrice = PropertyManager.Instance.GetLandPrice(id); // 구매 비용은 땅값 (PropertyManager에서 조회)

        var options = new List<PurchasePopupView.Option>
        {
            new PurchasePopupView.Option
            {
                TargetLevel = BuildingLevel.Land,
                Cost = landPrice,
                Toll = data != null ? data.GetToll(BuildingLevel.Land) : 0,
                Locked = !isPurchasable,
                LockReason = isPurchasable ? null : "금액 부족",
                ShowInfoWhenNone = !isPurchasable, // 구매할 수 없어도 이용료·구매 비용은 보여준다
            }
        };

        // 특수 칸(PropertyData.CanBuild == false)은 건물(땅)만 살 수 있고 별 건설이 없어서, 별 카드 대신 효과 설명을 보여준다
        bool special = data != null && !data.CanBuild;
        if (special)
        {
            purchase.Show(TileName(id), TileImageOf(id), options, player != null ? player.Money : 0,
                level => { if (Game != null) Game.PurchaseProperty(playerId, id); },
                () => { if (Game != null) Game.DeclinePropertyPurchase(playerId, id); },
                OwnerLabel(GetProperty(id)), SpecialDescription(id));
            return;
        }

        long buildCostSum = 0; // 별 1개, 2개, 3개까지 짓는 데 드는 누적 건설비 (참고용)
        for (int i = (int)BuildingLevel.Villa; i <= (int)BuildingLevel.Hotel; i++)
        {
            var level = (BuildingLevel)i;
            if (data != null) buildCostSum += data.GetBuildCost(level);

            options.Add(new PurchasePopupView.Option
            {
                TargetLevel = level,
                Cost = buildCostSum,
                Toll = data != null ? data.GetToll(level) : 0,
                Locked = true,
                LockReason = "다음 단계",
            });
        }

        purchase.Show(TileName(id), TileImageOf(id), options, player != null ? player.Money : 0,
            level => { if (Game != null) Game.PurchaseProperty(playerId, id); },
            () => { if (Game != null) Game.DeclinePropertyPurchase(playerId, id); },
            OwnerLabel(GetProperty(id)));
    }

    // 건설하기 & 건설하지않기 버튼이 있는 창을 띄워주는 함수입니다.
    // 건설하기 버튼을 누르면 GameManager.Build(playerId, propertyId) 를 호출합니다.
    // 건설하지않기 버튼을 누르면 GameManager.DeclineBuild(playerId, propertyId) 를 호출합니다.
    // 건물 → 별 1개 → 2개 → 3개 카드가 모두 보이고, 지금 지을 수 있는 다음 단계만 고를 수 있습니다.
    // isBuildable: 건설할 수 있는지 여부입니다. GameManager가 판단해서 넘겨주세요. (UIManager는 판단하지 않습니다)
    //   false이면 다음 단계 카드가 "금액 부족"으로 잠기고, 창이 뜰 때 아무것도 선택되지 않은 상태로 시작하며,
    //   건설 버튼은 켜지지 않습니다. (건설하지않기는 누를 수 있습니다)
    public void ShowBuildPopup(long playerId, int propertyId, bool isBuildable)
    {
        if (purchase == null) return;

        PropertyState state = GetProperty(propertyId);
        PropertyData data = PropertyManager.Instance.GetData(propertyId);
        PlayerState player = GetPlayer(playerId);
        if (state == null || data == null) return;

        // 특수 칸(PropertyData.CanBuild == false)은 별 건설이 없어서, 구매창처럼 특수 칸 모양(배지·효과 설명)으로 보여준다.
        // 지을 수 있는 단계가 없으니 건설 버튼은 꺼져 있고 "건설하지않기"만 누를 수 있다. 정보칸에는 현재 이용료를 보여준다.
        if (!data.CanBuild)
        {
            var specialOptions = new List<PurchasePopupView.Option>
            {
                new PurchasePopupView.Option
                {
                    TargetLevel = BuildingLevel.Land,
                    Toll = data.GetToll(state.BuildingLevel),
                    Locked = true,
                    LockReason = "보유 중",
                    ShowInfoWhenNone = true,
                }
            };
            purchase.Show(TileName(propertyId), TileImageOf(propertyId), specialOptions, player != null ? player.Money : 0,
                level => { if (Game != null) Game.Build(playerId, propertyId); },
                () => { if (Game != null) Game.DeclineBuild(playerId, propertyId); },
                OwnerLabel(state), SpecialDescription(propertyId));
            return;
        }

        int current = (int)state.BuildingLevel;
        var options = new List<PurchasePopupView.Option>();
        long pending = 0; // 다음 단계부터 해당 단계까지 더한 건설비

        for (int i = (int)BuildingLevel.Land; i <= (int)BuildingLevel.Hotel; i++)
        {
            var level = (BuildingLevel)i;
            var option = new PurchasePopupView.Option { TargetLevel = level, Toll = data.GetToll(level) };

            if (i <= current)
            {
                option.Locked = true;
                option.LockReason = "보유 중";
                option.Owned = true; // 별 단계 표시에 보유한 단계까지 채워 보여준다

                // 이미 최고 단계(호텔)라 더 지을 곳이 없어도, 정보창에는 호텔의 이용료를 보여준다. (구매 비용은 없다)
                if (level == BuildingLevel.Hotel)
                {
                    option.Cost = 0;
                    option.ShowInfoWhenNone = true;
                }
            }
            else
            {
                pending += data.GetBuildCost(level);
                option.Cost = pending;

                if (i > current + 1)
                {
                    option.Locked = true;
                    option.LockReason = "다음 단계";
                }
                else if (!isBuildable)
                {
                    option.Locked = true;
                    option.LockReason = "금액 부족";
                    option.ShowInfoWhenNone = true; // 지을 수 없어도 이용료·건설 비용은 보여준다
                }
            }
            options.Add(option);
        }

        purchase.Show(TileName(propertyId), TileImageOf(propertyId), options, player != null ? player.Money : 0,
            level => { if (Game != null) Game.Build(playerId, propertyId); },
            () => { if (Game != null) Game.DeclineBuild(playerId, propertyId); },
            OwnerLabel(state));
    }

    // 특수 칸(별 건설 없음)의 효과 설명. PropertyEffectDescription이 없거나 비어 있으면 기본 안내
    private string SpecialDescription(int propertyId)
    {
        string description = PropertyEffectDescription != null ? PropertyEffectDescription(propertyId) : null;
        return string.IsNullOrEmpty(description) ? "특수 칸이에요. 별 건설 없이 건물만 구매할 수 있어요." : description;
    }

    // 구매·건설 창의 타일 사진에 붙는 소유자 표시. 주인이 없으면 "빈 땅", 있으면 인수창처럼 "● 이름 님의 땅"
    private string OwnerLabel(PropertyState property)
    {
        if (property == null || !property.OwnerId.HasValue) return "빈 땅";
        long ownerId = property.OwnerId.Value;
        return $"<color={UIPalette.Hex(UIPalette.PlayerColor(ColorIndexOf(ownerId)))}>●</color> {NameOf(ownerId)} 님의 땅";
    }

    // 인수하기 & 인수하지않기 버튼이 있는 창을 띄워주는 함수입니다.
    // 인수하기 버튼을 누르면 GameManager.AcquireProperty(playerId, propertyId) 를 호출합니다.
    // 인수하지않기 버튼을 누르면 GameManager.DeclineAcquireProperty(playerId, propertyId) 를 호출합니다.
    public void ShowAcquirePropertyPopup(long playerId, int propertyId)
    {
        if (takeover == null) return;

        PropertyState state = GetProperty(propertyId);
        PlayerState player = GetPlayer(playerId);
        if (state == null) return;

        long ownerId = state.OwnerId ?? 0;
        long price = PropertyManager.Instance.GetAcquireValue(state);

        // 특수 칸(별 건설 없음)은 별 단계 대신 "특수 칸" 배지와 땅 효과 설명으로 보여준다
        takeover.Show(TileName(propertyId), TileImageOf(propertyId), state.BuildingLevel,
            NameOf(ownerId), ColorIndexOf(ownerId), price, player != null ? player.Money : 0,
            () => { if (Game != null) Game.AcquireProperty(playerId, propertyId); },
            () => { if (Game != null) Game.DeclineAcquireProperty(playerId, propertyId); },
            PropertyManager.Instance.GetData(propertyId) is { CanBuild: false } ? SpecialDescription(propertyId) : null);
    }

    // 매각할 자산들을 선택할 수 있는 창을 띄워주는 함수입니다.
    // 선택 완료 버튼을 누르면 GameManager.SellProperties(payerId, receiverId, List<int> propertyIds, long requiredAmount) 를 호출합니다.
    // 팔 땅은 보드에서 고릅니다. 보드가 칸을 눌릴 때마다 CheckTilesValidForSell(고른 칸 번호들)을 불러주세요.
    // 고른 땅의 매각가 합 + 현금이 requiredAmount보다 작으면 선택 완료 버튼이 활성화되지 않습니다.
    public void ShowSellPropertiesPopup(long payerId, long receiverId, long requiredAmount)
    {
        if (sell == null) return;

        ExitSellMode();
        sellSession++;
        sellMode = true;
        sellPayerId = payerId;
        sellReceiverId = receiverId;
        sellRequired = requiredAmount; // 필요한 금액을 보관해 두었다가 CheckTilesValidForSell에서 쓴다
        selectedSellPropertyIds.Clear();

        // 보드를 매각 자산 선택 모드로 바꾼다
        BoardManager.Instance.ChangeToSelectSellMode();

        PlayerState payer = GetPlayer(payerId);

        sell.Show(requiredAmount, payer != null ? payer.Money : 0, AutoSelectSell, CompleteSell);
        sell.SetCompleteInteractable(false); // 아무것도 고르지 않은 처음에는 꺼진 상태
    }

    #endregion

    #region 보드 칸 클릭

    /// <summary>
    /// 둘러보기 모드에서 보드의 칸을 눌렀을 때 불러주세요. 땅이면 땅 정보 창을 띄웁니다.
    /// (여행지·매각 자산 선택은 보드가 CheckTileValidForTravel / CheckTilesValidForSell로 알려줍니다.)
    /// </summary>
    public void OnBoardTileClicked(int tileIndex)
    {
        TileData tile = BoardManager.Instance.GetTileData(tileIndex);
        if (tile == null || tile.Type != TileType.PROPERTY) return;
        ShowTileInfoPopup(tile.PropertyId);
    }

    private bool chooseDestinationMode;

    #endregion

    #region 매각

    private bool sellMode;
    private int sellSession;            // 매각을 시작하거나 끝낼 때마다 늘려서, 늦게 도착한 시간 초과 처리가 이미 끝난 매각에 자동 매각을 걸지 못하게 한다
    private long sellPayerId;
    private long sellReceiverId;
    private long sellRequired;
    private readonly List<int> selectedSellPropertyIds = new(); // CheckTilesValidForSell이 마지막으로 구한, 팔 땅 번호들
    private long sellSelectedTotal; // 마지막으로 구한 선택 합계 (표시용)

    /// <summary>
    /// 보드의 칸을 누를 때마다 BoardManager가 부르는 함수입니다. tileIndexes: 지금 고른 칸 번호들.
    /// 고른 땅들의 매각가 합(PropertyManager.GetSellValue)을 구해 창의 부족한 금액을 갱신하고,
    /// 이 합 + 현금이 필요한 금액(requiredAmount)보다 작으면 "매각 완료" 버튼을 끄고 아니면 켭니다.
    /// 내 땅이 아니거나 땅이 아닌 칸은 무시합니다.
    /// </summary>
    public void CheckTilesValidForSell(List<int> tileIndexes)
    {
        if (!sellMode || sell == null) return;

        selectedSellPropertyIds.Clear();
        long total = 0;
        if (tileIndexes != null)
        {
            foreach (int tileIndex in tileIndexes)
            {
                TileData tile = BoardManager.Instance.GetTileData(tileIndex);
                if (tile == null || tile.Type != TileType.PROPERTY) continue;

                PropertyState state = GetProperty(tile.PropertyId);
                if (state == null || state.OwnerId != sellPayerId) continue;
                if (selectedSellPropertyIds.Contains(tile.PropertyId)) continue;

                selectedSellPropertyIds.Add(tile.PropertyId);
                total += PropertyManager.Instance.GetSellValue(state);
            }
        }

        sellSelectedTotal = total;
        sell.SetSelectedTotal(total); // 부족한 금액 갱신 + 현금 + 합계 >= 필요 금액이면 완료 버튼 켜짐
    }

    private const SellSelectionMode AutoSellMode = SellSelectionMode.CheapestFirst; // 자동 선택 방식: 매각가가 싼 땅부터 부족분이 찰 때까지

    // 자동 선택으로 고른 땅 번호들. 부족분(필요 금액 - 현금)을 채우는 땅을 PropertyManager의 자동 선택 규칙으로 고른다.
    private List<int> AutoChosenProperties()
    {
        if (State == null || PropertyManager.Instance == null) return new List<int>();
        PlayerState payer = GetPlayer(sellPayerId);
        long shortage = sellRequired - (payer != null ? payer.Money : 0);
        return PropertyManager.Instance.AutoChooseSellProperties(sellPayerId, shortage, State.PropertyStates, AutoSellMode);
    }

    // "자동 선택" 버튼: 부족한 금액을 채울 땅을 골라 보드에 선택 표시만 한다. 실제 매각은 "매각 완료"를 눌러야 한다.
    private void AutoSelectSell()
    {
        if (!sellMode || BoardManager.Instance == null || BoardManager.Instance.BoardController == null) return;
        BoardController board = BoardManager.Instance.BoardController;

        foreach (int index in new List<int>(board.selectedIndexes)) board.OnBoardTileClicked(index); // 지금 고른 땅을 모두 해제
        foreach (int propertyId in AutoChosenProperties())
        {
            PropertyData data = PropertyManager.Instance.GetData(propertyId);
            if (data != null) board.OnBoardTileClicked(data.BoardIndex); // 하나씩 선택으로 켠다 (합계와 완료 버튼은 CheckTilesValidForSell이 갱신)
        }
    }

    // 매각 창의 제한시간이 끝났다: 파산하지 않고, 자동 선택한 땅을 바로 판다.
    private void SellOnTimeout(int session)
    {
        if (session != sellSession) return; // 그 사이 매각이 끝났거나(서버 메시지로 창이 닫힘 등) 새로 시작됐다
        sellSession++;
        long payerId = sellPayerId;
        List<int> propertyIds = AutoChosenProperties();
        ExitSellMode();
        if (Game != null && propertyIds.Count > 0)
        {
            BoardManager.Instance.ChangeToInspectMode();
            Game.SellProperties(payerId, propertyIds);
        }
    }

    // "매각 완료" 버튼
    private void CompleteSell()
    {
        sellSession++;
        long payerId = sellPayerId, receiverId = sellReceiverId, required = sellRequired;
        var propertyIds = new List<int>(selectedSellPropertyIds); // 칸 번호가 아니라 땅 번호
        ExitSellMode(); // 보드도 둘러보기 모드로 되돌린다
        if (Game != null)
        {
            BoardManager.Instance.ChangeToInspectMode();
            Game.SellProperties(payerId, propertyIds);
        }
    }

    // 매각 모드를 끝내고 보드도 둘러보기 모드로 되돌린다
    private void ExitSellMode()
    {
        // if (sellMode) BoardManager.Instance.ChangeToInspectMode(); // TODO: BoardManager 구현 후 주석 해제
        selectedSellPropertyIds.Clear();
        sellMode = false;
        sellSelectedTotal = 0;
    }

    #endregion

    #region 턴 · 주사위

    // 'OO의 턴'이라는 UI를 표시합니다. GameManager.HandleTurnChanged에서 부릅니다.
    // 차례 표시, 플레이어 카드 강조, 상단바 라운드 수를 함께 바꾸고, 내 차례가 아니면 주사위 굴리기 버튼을 잠급니다.
    public void ShowTurn(long playerId)
    {
        int index = IndexOf(playerId);
        if (index < 0) return;

        SetTurn(index, NameOf(playerId), PortraitOf(playerId));
        if (topBar != null) topBar.SetRound(DisplayRound(), maxRound);
        if (playerId != LocalPlayerId && diceRoll != null) diceRoll.SetInteractable(false);
        // 다른 사람 차례가 되면 내 제한시간은 끝난다. (내 차례를 다시 표시하는 경우, 예: 화면 동기화는 진행 중인 제한시간을 끊지 않는다)
        if (playerId != LocalPlayerId) StopSelectionTimer();
    }

    // 호스트가 내 요청을 거절했을 때 부릅니다. 결과를 기다리며 구르던 주사위를 멈춥니다.
    public void CancelDiceRolling() { if (diceResult != null) diceResult.CancelRolling(); }

    // 주사위 굴림 연출을 재생합니다. GameManager.HandleDiceRolled에서 부릅니다.
    // 굴리기 버튼을 누른 뒤라면 이미 주사위가 구르는 중이므로, 결과를 받는 즉시 멈춥니다.
    // 눈이 흔들리다 멈추고 합계가 나오며, 같은 눈이면 '더블!'이 뜹니다. 1.5초 뒤 창이 스스로 닫힙니다.
    // 내가 무인도에 갇혀 있을 때 굴렸다면, 결과가 나온 뒤 더블이면 탈출하고 아니면 남은 턴이 1 줄어듭니다.
    // onCompleted는 결과를 충분히 보여주고 창까지 닫힌 뒤 호출됩니다.
    public void ShowDiceResult(int dice1, int dice2, Action onCompleted = null)
    {
        if (diceResult == null)
        {
            onCompleted?.Invoke();
            return;
        }

        bool mine = State != null && State.CurrentPlayerId == LocalPlayerId;
        diceResult.Show(dice1, dice2, () =>
        {
            if (mine && diceRoll != null) diceRoll.ResolveIslandRoll(dice1 == dice2);
        }, onCompleted);
    }

    // 호스트가 대신 굴렸을 때 부릅니다. 굴리기 버튼이 남아 있어도 눌리지 않게 잠급니다.
    public void LockDiceRoll()
    {
        if (diceRoll != null) diceRoll.LockRoll();
    }

    // 무인도에 갇혔을 때 탈출 방법을 고르는 창을 띄워주는 함수입니다.
    // 주사위 더블 / 비용 지불 / 카드 사용 중 누른 것에 해당하는 콜백을 호출합니다.
    // (GameManager에 탈출 함수가 아직 없어서 콜백으로 받습니다. 현금이 모자라면 비용 지불, 카드가 없으면 카드 사용이 잠깁니다.)
    public void ShowIslandEscapePopup(long playerId, long escapeCost, int escapeCardCount,
                                      Action onRollDouble, Action onPay, Action onUseCard)
    {
        if (islandEscape == null) return;

        PlayerState player = GetPlayer(playerId);
        int turns = player != null ? Mathf.Max(1, player.IslandTurnsRemaining) : 1;
        islandEscape.Show(turns, escapeCost, player != null ? player.Money : 0, escapeCardCount,
            onRollDouble, onPay, onUseCard);
    }

    #endregion

    #region 플레이어 정보 · 돈

    private readonly Dictionary<long, string> nextMoneyReason = new();
    private readonly HashSet<long> transferPlayers = new();   // 이동 연출이 돈 표시를 대신 처리하는 동안
    private readonly Dictionary<long, long> bankruptShortfall = new();

    private class Profile
    {
        public string Name;
        public Sprite Portrait;
    }
    private readonly Dictionary<long, Profile> profiles = new();
    private long localPlayerId;
    private bool hasLocalPlayer;

    /// <summary>이 화면을 보는 플레이어. 지정하지 않으면 첫 번째 플레이어입니다.</summary>
    public long LocalPlayerId
    {
        get
        {
            if (hasLocalPlayer) return localPlayerId;
            GameState state = State;
            return state != null && state.PlayerStates.Count > 0 ? state.PlayerStates[0].PlayerId : 0;
        }
    }

    public void SetLocalPlayer(long playerId)
    {
        localPlayerId = playerId;
        hasLocalPlayer = true;
    }

    /// <summary>플레이어 이름과 초상화. 지정하지 않으면 "플레이어 N"과 기본 초상화를 씁니다.</summary>
    public void SetPlayerProfile(long playerId, string playerName, Sprite portrait = null)
    {
        profiles[playerId] = new Profile { Name = playerName, Portrait = portrait };

        int index = IndexOf(playerId);
        PlayerInfoView view = GetPlayerInfo(index);
        if (view != null) view.SetProfile(NameOf(playerId), PortraitOf(playerId), index);
    }

    // 상단바에 보여줄 최대 라운드를 지정합니다. 게임 시작 시 GameManager가 부릅니다. (최대 라운드 값은 GameManager가 관리)
    public void SetMaxRound(int value)
    {
        maxRound = value;
        if (topBar != null) topBar.SetRound(DisplayRound(), maxRound);
    }

    /// <summary>
    /// 게임을 시작할 때 한 번 부릅니다. GameState의 플레이어 수에 맞춰 카드를 채웁니다.
    /// (플레이어 이름은 그 전에 SetPlayerProfile로 넣어주세요.)
    /// </summary>
    public void InitPlayers()
    {
        GameState state = State;
        if (state == null) return;

        if (topBar != null) topBar.SetRoundVisible(true); // 게임이 시작되면 상단바에 라운드를 보인다

        bankruptShortfall.Clear();
        transferPlayers.Clear();

        int count = state.PlayerStates.Count;
        SetPlayerCount(count);
        for (int i = 0; i < count; i++)
        {
            PlayerInfoView view = GetPlayerInfo(i);
            long id = state.PlayerStates[i].PlayerId;
            if (view != null) view.SetProfile(NameOf(id), PortraitOf(id), i);
        }
        RefreshPlayerCards(animate: false);

        if (topBar != null)
        {
            RefreshTopBarRoom();
            topBar.SetRound(DisplayRound(), maxRound);
        }
    }

    /// <summary>
    /// GameState를 다시 읽어 모든 플레이어 카드(돈, 총 자산, 등수, 파산)를 통째로 맞춥니다. 돈 변화 표시는 띄우지 않습니다.
    /// 게임 시작(InitPlayers)과 파산 연출에서 쓰는 카드 전용 갱신입니다. 재접속·지연으로 어긋난 화면을 맞출 때는
    /// 달라진 부분만 연출하고 차례와 라운드까지 맞추는 RefreshUIFromGameState를 쓰세요.
    /// </summary>
    public void RefreshPlayerCards(bool animate = true)
    {
        GameState state = State;
        if (state == null) return;

        for (int i = 0; i < state.PlayerStates.Count; i++)
        {
            PlayerInfoView view = GetPlayerInfo(i);
            if (view == null) continue;
            PlayerState player = state.PlayerStates[i];
            view.SetMoney(player.Money, TotalAssetOf(player.PlayerId, player.Money), animate);
            view.SetBankrupt(player.IsBankrupt, animate);
        }
        UpdateRanks();
    }

    // 돈이 바뀐 것을 카드에 반영하고, 바뀐 만큼 돈 변화 표시를 띄웁니다. (EconomyManager.OnMoneyChanged)
    private void HandleMoneyChanged(long playerId, long before, long after)
    {
        if (sellMode && playerId == sellPayerId && sell != null) sell.SetCash(after, sellSelectedTotal);

        PlayerInfoView view = GetPlayerInfo(IndexOf(playerId));
        if (view == null || transferPlayers.Contains(playerId)) return;

        view.SetMoney(after, TotalAssetOf(playerId, after));
        if (after != before) view.ShowMoneyChange(TakeMoneyReason(playerId, after - before), after - before);
        UpdateRanks();
    }

    /// <summary>다음 돈 변화 표시에 붙일 사유(예: "황금열쇠"). 지정하지 않으면 "입금"/"출금"으로 표시됩니다.</summary>
    public void SetNextMoneyReason(long playerId, string reason) => nextMoneyReason[playerId] = reason;

    private string TakeMoneyReason(long playerId, long delta)
    {
        if (nextMoneyReason.TryGetValue(playerId, out string reason))
        {
            nextMoneyReason.Remove(playerId);
            return reason;
        }
        return delta > 0 ? "입금" : "출금";
    }

    // 사유를 붙여 돈 변화 표시를 띄웁니다. GameManager의 땅 구매, 건설에서 부릅니다.
    // GameState를 먼저 갱신한 뒤 부릅니다. (EconomyManager.NotifyMoneyChanged를 이미 부르는 곳은 이 함수를 따로 부르지 않습니다.)
    public void PlayMoneyChange(long playerId, long amount, string reason)
    {
        PlayerInfoView view = GetPlayerInfo(IndexOf(playerId));
        PlayerState player = GetPlayer(playerId);
        if (view == null || player == null) return;

        view.SetMoney(player.Money, TotalAssetOf(playerId, player.Money));
        if (amount != 0) view.ShowMoneyChange(reason, amount);
        UpdateRanks();
    }

    // 통행료 지불 연출. PlayMoneyTransfer를 통행료 문구로 부르는 단축 함수입니다.
    public void PlayTollEffect(long payerId, long receiverId, long amount)
        => PlayMoneyTransfer(payerId, receiverId, amount, "통행료", "통행료 수입");

    /// <summary>
    /// 돈이 payer에서 receiver로 넘어가는 연출입니다. 내는 사람은 바로 줄어들고, 받는 사람은 코인이 도착할 때 늘어납니다.
    /// (인수, 매각 후 통행료 지불 등에도 씁니다. GameState를 먼저 갱신한 뒤 부릅니다.)
    /// </summary>
    public void PlayMoneyTransfer(long payerId, long receiverId, long amount, string payerReason, string receiverReason)
    {
        int from = IndexOf(payerId), to = IndexOf(receiverId);
        PlayerInfoView payerView = GetPlayerInfo(from), receiverView = GetPlayerInfo(to);
        PlayerState payer = GetPlayer(payerId), receiver = GetPlayer(receiverId);
        if (payerView == null || receiverView == null || payer == null || receiver == null) return;

        // 이 연출이 끝날 때까지 두 사람의 돈 표시는 여기서만 바꾼다 (같은 변화가 두 번 뜨지 않게)
        transferPlayers.Add(payerId);
        transferPlayers.Add(receiverId);

        payerView.SetMoney(payer.Money, TotalAssetOf(payerId, payer.Money));
        payerView.ShowMoneyChange(payerReason, -amount);

        void Finish()
        {
            transferPlayers.Remove(payerId);
            transferPlayers.Remove(receiverId);
        }

        PlayCoinFlight(from, to, () =>
        {
            receiverView.SetMoney(receiver.Money, TotalAssetOf(receiverId, receiver.Money));
            receiverView.ShowMoneyChange(receiverReason, amount);
            UpdateRanks();
            Finish();
        });
        // 코인 연출이 끊겨도 돈 표시가 잠겨 있지 않게 한다
        DOVirtual.DelayedCall(3f, Finish, ignoreTimeScale: true);
    }

    private const float BankruptResultDelay = 2.5f; // 파산 연출(도장)이 보인 뒤 결과 창이 뜨기까지
    private float lastBankruptEffectAt = -100f;     // 마지막으로 파산 연출을 재생한 시각
    private bool skipResultDelay;                   // 이미 기다렸으니 결과 창을 바로 띄운다

    // 파산 연출을 재생합니다. GameManager.ProcessBankruptcy가 GameState를 갱신한 뒤 부릅니다.
    // shortfall은 못 낸 금액으로, 게임 결과 창에 마이너스 자산으로 표시됩니다. (모르면 생략)
    public void PlayBankruptEffect(long playerId, long shortfall = 0)
    {
        if (shortfall > 0) bankruptShortfall[playerId] = shortfall;
        lastBankruptEffectAt = Time.unscaledTime;

        // GameState(IsBankrupt)가 이미 갱신된 뒤 호출되므로, 전체 갱신 한 번이면 됩니다.
        // 방금 파산한 플레이어의 카드만 파산 표시가 꺼짐 → 켜짐으로 바뀌면서 도장 연출이 재생됩니다.
        // (SetBankrupt를 따로 한 번 더 부르면, 두 번째 호출이 진행 중인 도장 연출을 멈추고 최종 모습으로 바꿔 버림)
        RefreshPlayerCards();
    }

    // 총 자산 = 현금 + 가진 땅의 투자금(땅값 + 지은 건물 비용). 계산은 PropertyManager.GetTotalAsset (게임 결과와 같은 기준)
    private long TotalAssetOf(long playerId, long cash)
    {
        GameState state = State;
        if (state == null) return cash;
        return PropertyManager.Instance.GetTotalAsset(playerId, cash, state.PropertyStates);
    }

    // 총 자산 순으로 등수를 매긴다. 파산한 사람은 맨 뒤.
    private void UpdateRanks()
    {
        GameState state = State;
        if (state == null) return;

        var order = new List<int>();
        for (int i = 0; i < state.PlayerStates.Count; i++) order.Add(i);
        order.Sort((a, b) =>
        {
            PlayerState pa = state.PlayerStates[a], pb = state.PlayerStates[b];
            int result = PlayerRanking.Compare(pa, pb, state.PropertyStates); // 파산(나중 파산 우선) → 총자산 → 현금 순
            return result != 0 ? result : a.CompareTo(b);
        });

        for (int rank = 0; rank < order.Count; rank++)
        {
            PlayerInfoView view = GetPlayerInfo(order[rank]);
            if (view != null) view.SetRank(rank + 1);
        }
    }

    // 상단바에 보여줄 라운드. GameManager가 계산한 GameState.RoundNumber(살아 있는 플레이어가 모두 한 번씩 하면 1라운드)를 그대로 쓴다.
    private int DisplayRound()
    {
        GameState state = State;
        if (state == null) return 1;
        return Mathf.Max(1, state.RoundNumber);
    }

    /// <summary>플레이어 순서(0~3)의 정보 카드.</summary>
    public PlayerInfoView GetPlayerInfo(int index)
    {
        if (index < 0 || index >= playerInfos.Length) return null;
        return playerInfos[index];
    }

    /// <summary>게임 인원(1~4)에 맞춰 플레이어 정보 카드를 보이거나 숨긴다. 앞에서부터 count장만 보인다.</summary>
    public void SetPlayerCount(int count)
    {
        for (int i = 0; i < playerInfos.Length; i++)
            if (playerInfos[i] != null) playerInfos[i].gameObject.SetActive(i < count);
    }

    /// <summary>차례 표시와 카드 강조를 함께 바꾼다. (카드 번호 기준. playerId 기준은 ShowTurn)</summary>
    public void SetTurn(int playerIndex, string playerName, Sprite portrait)
    {
        for (int i = 0; i < playerInfos.Length; i++)
            if (playerInfos[i] != null) playerInfos[i].SetTurn(i == playerIndex);
        if (currentTurn != null) currentTurn.Show(playerName, portrait, playerIndex);
    }

    #endregion

    #region 땅 정보 · 카드 · 게임 결과

    // 땅 정보 창을 띄웁니다. 보드의 땅을 눌렀을 때 OnBoardTileClicked가 부릅니다.
    // 주인, 지금 단계, 단계별 통행료(현재 단계만 굵게)를 보여줍니다.
    public void ShowTileInfoPopup(int propertyId)
    {
        if (tileInfo == null) return;

        PropertyData data = PropertyManager.Instance.GetData(propertyId);
        PropertyState state = GetProperty(propertyId);
        if (data == null) return;

        var tolls = new long[4];
        for (int i = 0; i < tolls.Length; i++) tolls[i] = data.GetToll((BuildingLevel)i);

        bool owned = state != null && state.OwnerId.HasValue;
        long ownerId = owned ? state.OwnerId.Value : 0;
        tileInfo.Show(data.CityName, TileImageOf(propertyId), owned ? NameOf(ownerId) : null, ColorIndexOf(ownerId),
            state != null ? state.BuildingLevel : BuildingLevel.Land, tolls,
            data.CanBuild ? null : SpecialDescription(propertyId));
    }

    // 황금열쇠 카드를 뽑는 연출입니다. GameManager.HandleCardDrawn이 카드 효과(ExecuteCardEffect)를 실행하기 전에 부릅니다.
    // 카드가 뒤집혀 이름·설명이 나옵니다.
    // - 이 화면의 플레이어(LocalPlayerId)가 뽑은 카드: 확인을 누르면 onConfirm을 호출합니다.
    // - 다른 플레이어가 뽑은 카드, 또는 forceAutoClose: 확인 버튼 없이 otherCardShowSeconds 동안 보여준 뒤 스스로 닫히고 onConfirm을 호출합니다.
    public void ShowCardDrawPopup(long playerId, int cardId, bool forceAutoClose, Action onConfirm)
    {
        if (cardDraw == null)
        {
            onConfirm?.Invoke();
            return;
        }

        CardData card = cardDeck != null ? cardDeck.FindById(cardId) : null;
        string cardName = card != null ? card.cardName : $"황금열쇠 {cardId}";
        string description = card != null ? card.description : "";
        Sprite icon = card != null ? card.icon : null;

        bool autoClose = forceAutoClose || playerId != LocalPlayerId; // 내 카드가 아니면 자동으로 닫힌다
        cardDraw.Show(cardName, description, icon, false, () =>
        {
            SetNextMoneyReason(playerId, "황금열쇠"); // 카드로 돈이 바뀌면 사유가 '황금열쇠'로 뜬다
            onConfirm?.Invoke();
        }, autoClose ? otherCardShowSeconds : 0f);
    }

    // 게임 결과 창을 띄우는 함수입니다. 게임이 끝났을 때 GameManager가 호출합니다.
    // 등수 순(파산한 사람은 맨 뒤)으로 승리/패배/파산과 최종 자산을 보여주고, 닫기를 누르면 onClose를 호출합니다. (결과 시간이 지나 자동으로 방으로 돌아갈 때는 호출되지 않습니다)
    public void ShowGameResultPopup(Action onClose = null)
    {
        GameState state = State;
        if (gameResult == null || state == null) return;

        // 게임이 끝났으니 결과 창이 뜰 때까지 서버 메시지를 더 처리하지 않는다 (말이 움직이는 등)
        state.WaitingType = null;

        // 파산 연출(도장)이 막 재생됐다면 그 연출이 보인 뒤에 결과 창을 띄운다
        float wait = lastBankruptEffectAt + BankruptResultDelay - Time.unscaledTime;
        if (!skipResultDelay && wait > 0f)
        {
            DOVirtual.DelayedCall(wait, () => { skipResultDelay = true; ShowGameResultPopup(onClose); skipResultDelay = false; }, ignoreTimeScale: true);
            return;
        }

        // 더 이상 굴릴 주사위도, 진행 중인 차례도 없다
        StopSelectionTimer();
        if (diceRoll != null) { diceRoll.SetInteractable(false); diceRoll.Close(); }
        if (diceResult != null) diceResult.Close();
        if (currentTurn != null) currentTurn.Close();

        // 순위: 파산 → 총자산 → 현금 순, 모두 같으면 원래 순서 (GameManager 종료 로그와 같은 기준)
        var order = new List<int>();
        for (int i = 0; i < state.PlayerStates.Count; i++) order.Add(i);
        order.Sort((x, y) =>
        {
            PlayerState px = state.PlayerStates[x], py = state.PlayerStates[y];
            int result = PlayerRanking.Compare(px, py, state.PropertyStates);
            return result != 0 ? result : x.CompareTo(y);
        });
        var ranked = new List<PlayerState>();
        foreach (int index in order) ranked.Add(state.PlayerStates[index]);

        var entries = new List<GameResultView.Entry>();
        for (int i = 0; i < ranked.Count; i++)
        {
            PlayerState player = ranked[i];
            bool bankrupt = player.IsBankrupt;
            long shortfall = bankruptShortfall.TryGetValue(player.PlayerId, out long value) ? value : 0;
            entries.Add(new GameResultView.Entry
            {
                Name = NameOf(player.PlayerId),
                Portrait = PortraitOf(player.PlayerId),
                ColorIndex = ColorIndexOf(player.PlayerId),
                FinalAsset = bankrupt ? -shortfall : TotalAssetOf(player.PlayerId, player.Money),
                IsWinner = i == 0 && !bankrupt,
                IsBankrupt = bankrupt,
            });
        }
        // 확인 버튼을 누르거나 정해진 시간이 지나면 방(로비)으로 돌아간다.
        gameResult.Show(entries, () =>
        {
            onClose?.Invoke();
            ReturnAfterResult();
        });
        if (resultCountdown != null) StopCoroutine(resultCountdown);
        resultCountdown = StartCoroutine(ResultCountdown());
    }

    private Coroutine resultCountdown;

    // 결과 창의 확인 버튼에 남은 시간을 보여 주고, 0이 되면 방으로 돌아간다
    private System.Collections.IEnumerator ResultCountdown()
    {
        for (int left = Mathf.CeilToInt(resultSeconds); left > 0; left--)
        {
            gameResult.SetCountdown(left);
            yield return new WaitForSecondsRealtime(1f);
        }
        resultCountdown = null;
        ReturnAfterResult();
    }

    // 결과 창의 확인 버튼을 눌렀거나 결과 시간이 끝났을 때: 같은 방을 유지한 채 로비 씬으로 돌아가 방 설정을 다시 연다.
    // 로비를 거치지 않고 게임 씬만 실행한 경우(방 정보 없음)에는 결과 창을 그대로 둔다.
    private void ReturnAfterResult()
    {
        if (resultCountdown != null) { StopCoroutine(resultCountdown); resultCountdown = null; }
        if (gameResult != null) gameResult.SetCountdown(0);
        if (LobbyManager.HasRoom) SceneFlow.ReturnToRoom(); // 서버 방 정보가 있을 때(재접속으로 들어온 경우 포함)
    }

    #endregion

    #region 코인 이동

    private const int CoinCount = 7;
    private const float CoinBurstSeconds = 0.14f;   // 금화에서 흩어져 나오는 시간
    private const float CoinFlightSeconds = 0.55f;  // 기본 비행 시간 (코인마다 조금씩 다름)
    private const float CoinStagger = 0.045f;
    private const int CoinLayerSortingOrder = 95;   // HUD(0~50) 위, 선택 팝업(100~) 아래
    private RectTransform coinLayer;

    /// <summary>
    /// 코인이 from 카드에서 to 카드로 날아간다. 첫 코인이 도착할 때 onArrived를 부른다. (카드 번호 기준)
    /// 돈 표시까지 함께 처리하려면 PlayMoneyTransfer를 쓴다.
    /// </summary>
    public void PlayCoinFlight(int fromIndex, int toIndex, Action onArrived = null)
    {
        var from = GetPlayerInfo(fromIndex);
        var to = GetPlayerInfo(toIndex);
        if (from == null || to == null || from.CashIcon == null || to.CashIcon == null)
        {
            onArrived?.Invoke();
            return;
        }

        var layer = GetCoinLayer(from);
        Vector2 start = layer.InverseTransformPoint(from.CashIcon.position);
        Vector2 end = layer.InverseTransformPoint(to.CashIcon.position);
        Vector2 bendDirection = BendTowardCenter(start, end);
        float distance = Vector2.Distance(start, end);
        bool arrivedOnce = false;

        // 보내는 쪽 금화가 살짝 움츠러들었다가 돌아온다
        PulseIcon(from.CashIcon, 0.82f, 0.1f);

        for (int i = 0; i < CoinCount; i++)
        {
            var rt = CreateCoin(from.CashIcon, layer);
            rt.anchoredPosition = start;
            rt.localScale = Vector3.zero;

            // 코인마다 흩어지는 방향, 휘는 정도, 비행 시간을 조금씩 다르게
            Vector2 burst = start + UnityEngine.Random.insideUnitCircle.normalized * UnityEngine.Random.Range(16f, 30f);
            Vector2 control = (burst + end) * 0.5f + bendDirection * distance * UnityEngine.Random.Range(0.18f, 0.34f);
            float flight = CoinFlightSeconds + UnityEngine.Random.Range(0f, 0.12f);
            float delay = i * CoinStagger + UnityEngine.Random.Range(0f, 0.02f);

            float t = 0f;
            DOTween.Sequence().SetUpdate(true).SetLink(rt.gameObject)
                .AppendInterval(delay)
                // 1) 금화에서 톡 튀어나와 주변으로 흩어진다
                .Append(rt.DOAnchorPos(burst, CoinBurstSeconds).SetEase(Ease.OutCubic))
                .Join(rt.DOScale(1f, CoinBurstSeconds).SetEase(Ease.OutBack))
                // 2) 화면 안쪽으로 휘는 곡선을 따라 가속했다 감속하며 날아간다
                .Append(DOTween.To(() => t, v =>
                {
                    t = v;
                    rt.anchoredPosition = Bezier(burst, control, end, v);
                    // 도착 직전에 빨려 들어가듯 작아진다
                    float s = v < 0.8f ? 1f : Mathf.Lerp(1f, 0.5f, (v - 0.8f) / 0.2f);
                    rt.localScale = new Vector3(s, s, 1f);
                }, 1f, flight).SetEase(Ease.InOutCubic))
                // 3) 도착: 받는 금화가 코인을 흡수하며 조금씩 부푼다
                .AppendCallback(() =>
                {
                    AbsorbPulse(to.CashIcon);
                    if (!arrivedOnce)
                    {
                        arrivedOnce = true;
                        onArrived?.Invoke();
                    }
                    UnityEngine.Object.Destroy(rt.gameObject);
                });
        }
    }

    private static RectTransform CreateCoin(RectTransform icon, RectTransform layer)
    {
        var coin = UnityEngine.Object.Instantiate(icon.gameObject, layer, false);
        coin.name = "Coin";
        var le = coin.GetComponent<LayoutElement>();
        if (le != null) le.ignoreLayout = true;
        foreach (var graphic in coin.GetComponentsInChildren<Graphic>()) graphic.raycastTarget = false;

        var rt = (RectTransform)coin.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = icon.rect.size;
        rt.localRotation = Quaternion.identity;
        return rt;
    }

    /// <summary>경로에 수직인 방향 중 화면 가운데 쪽. 코인이 화면 밖으로 솟지 않고 안쪽으로 휜다.</summary>
    private static Vector2 BendTowardCenter(Vector2 start, Vector2 end)
    {
        Vector2 dir = (end - start).normalized;
        var normal = new Vector2(-dir.y, dir.x);
        Vector2 mid = (start + end) * 0.5f;
        // 코인 레이어의 원점이 화면 가운데
        if (Vector2.Dot(normal, -mid) < 0f) normal = -normal;
        return normal;
    }

    private static Vector2 Bezier(Vector2 p0, Vector2 p1, Vector2 p2, float t)
    {
        float u = 1f - t;
        return u * u * p0 + 2f * u * t * p1 + t * t * p2;
    }

    // 코인이 닿을 때마다 조금씩 커졌다가 부드럽게 돌아온다 (매번 처음부터 튀지 않게 누적)
    private static void AbsorbPulse(RectTransform icon)
    {
        icon.DOKill();
        float current = icon.localScale.x;
        icon.localScale = Vector3.one * Mathf.Min(current + 0.08f, 1.35f);
        icon.DOScale(1f, 0.3f).SetEase(Ease.OutQuad).SetUpdate(true).SetLink(icon.gameObject);
    }

    private static void PulseIcon(RectTransform icon, float scale, float duration)
    {
        icon.DOKill();
        icon.localScale = Vector3.one;
        DOTween.Sequence().SetUpdate(true).SetLink(icon.gameObject)
            .Append(icon.DOScale(scale, duration).SetEase(Ease.OutQuad))
            .Append(icon.DOScale(1f, 0.28f).SetEase(Ease.OutBack));
    }

    // 플레이어 정보 캔버스 안에 코인 전용 하위 캔버스를 만든다.
    // 움직이는 동안 카드가 다시 그려지지 않게 하고, 정렬 순서를 따로 줘서 다른 HUD 위로 날아가게 한다.
    private RectTransform GetCoinLayer(PlayerInfoView anyCard)
    {
        if (coinLayer != null) return coinLayer;
        var go = new GameObject("CoinLayer", typeof(RectTransform));
        go.layer = anyCard.gameObject.layer;
        coinLayer = (RectTransform)go.transform;
        coinLayer.SetParent(anyCard.transform.parent, false);
        coinLayer.SetAsLastSibling();
        coinLayer.anchorMin = Vector2.zero;
        coinLayer.anchorMax = Vector2.one;
        coinLayer.offsetMin = coinLayer.offsetMax = Vector2.zero;
        var canvas = go.AddComponent<Canvas>();
        canvas.overrideSorting = true;
        canvas.sortingOrder = CoinLayerSortingOrder;
        return coinLayer;
    }

    #endregion

    #region 창 닫기
    /// <summary>
    /// 선택형 팝업과 일시적인 표시를 모두 닫는다. HUD(플레이어 정보, 상단바)는 그대로 둔다.
    /// immediate면 닫힘 연출 없이 바로 끈다.
    /// </summary>
    public void CloseAllPopups(bool immediate = false)
    {
        sellSession++;
        StopSelectionTimer();
        ExitSellMode();
        // 결과 창을 닫았는데 남은 시간이 지나 씬이 넘어가지 않게 한다
        if (resultCountdown != null) { StopCoroutine(resultCountdown); resultCountdown = null; }

        UIView[] popups =
        {
            diceResult, purchase, takeover, sell, islandEscape,
            cardDraw, destinationSelect, tileInfo, gameResult, exitConfirm
        };
        foreach (var popup in popups)
        {
            if (popup == null) continue;
            if (immediate) popup.CloseImmediate();
            else popup.Close();
        }
    }

    /// <summary>
    /// 지금 열려 있는 선택창(땅 구매·건설, 인수, 매각, 여행지 선택)만 닫는다. 선택 중이던 내용은 버리고, 아무것도 GameManager에 넘기지 않는다.
    /// 보드 선택 모드(여행지·매각)였다면 둘러보기 모드로 되돌린다. 주사위, 카드, 결과 창과 HUD는 그대로 둔다.
    /// immediate면 닫힘 연출 없이 바로 끈다.
    /// </summary>
    public void CloseSelectionPopups(bool immediate = false)
    {
        sellSession++;
        StopSelectionTimer();
        EndBoardSelection();

        UIView[] popups = { purchase, takeover, sell, destinationSelect };
        foreach (var popup in popups)
        {
            if (popup == null || !popup.gameObject.activeSelf) continue;
            if (immediate) popup.CloseImmediate();
            else popup.Close();
        }
    }

    // 여행지·매각 선택을 끝내고 보드를 둘러보기 모드로 되돌린다. 선택 중이 아니었다면 아무 일도 하지 않는다.
    private void EndBoardSelection()
    {
        bool boardSelecting = chooseDestinationMode || sellMode;

        chooseDestinationMode = false;
        travelTileIndex = -1;
        ExitSellMode();
        if (boardSelecting && BoardManager.Instance != null) BoardManager.Instance.ChangeToInspectMode();
    }
    #endregion

    #region 선택 제한시간

    private UIView timerWindow;        // 제한시간이 세어지고 있는 창. 없으면 null
    private TurnTimerView timerChip;   // 그 창 안에 들어 있는 시간 표시 칩. 없으면 null
    private SelectionKind timerKind;
    private float timerRemaining, timerTotal;
    private int timerEpoch;            // 제한시간을 새로 시작할 때마다 늘려서, 지난 시간 종료 처리가 새로 열린 창을 닫지 않게 한다

    // 잠수 중이면(GameManager.isAfk) 짧은 제한시간, 아니면 방 설정의 제한시간보다 먼저 닫히는 시간 (시간 규칙: TimeRules)
    private float CurrentSelectSeconds => TimeRules.ScreenSeconds(State != null ? State.Config : null, Game != null && Game.isAfk);

    private void HookSelectionTimer(UIView window, SelectionKind kind)
    {
        if (window != null) window.Opened += () => BeginSelectionTimer(window, kind);
    }

    private void BeginSelectionTimer(UIView window, SelectionKind kind)
    {
        timerEpoch++;
        timerWindow = window;
        timerKind = kind;
        timerTotal = timerRemaining = CurrentSelectSeconds;
        timerChip = window.GetComponentInChildren<TurnTimerView>(true);
        if (timerChip != null) timerChip.Begin(timerTotal);
    }

    /// <summary>세고 있던 제한시간을 시간 종료 처리 없이 멈추고 표시를 숨긴다.</summary>
    private void StopSelectionTimer()
    {
        timerWindow = null;
        if (timerChip != null) timerChip.Hide();
        timerChip = null;
    }

    private void Update()
    {
        if (timerWindow == null) return;

        // 창이 닫혔거나(선택을 마침), 주사위 버튼을 이미 눌렀으면 시간을 더 세지 않는다
        bool stillWaiting = timerWindow.IsOpen && (timerKind != SelectionKind.RollDice || diceRoll == null || diceRoll.CanRoll);
        if (!stillWaiting) { StopSelectionTimer(); return; }

        timerRemaining -= Time.unscaledDeltaTime;
        if (timerRemaining > 0f)
        {
            if (timerChip != null) timerChip.SetRemaining(timerRemaining, timerTotal);
            return;
        }
        OnSelectionTimeout();
    }

    // 제한시간 종료: 모든 선택 버튼을 잠그고, 잠시 뒤 창을 닫은 다음 알린다. (주사위 굴리기 UI는 닫지 않고 잠가만 둔다)
    private void OnSelectionTimeout()
    {
        UIView window = timerWindow;
        SelectionKind kind = timerKind;
        StopSelectionTimer();
        int epoch = timerEpoch; // 그 사이 새 제한시간이 시작되면 값이 달라진다

        int sellSessionAtTimeout = sellSession; // 지연 호출이 돌 때 이 매각이 그대로인지 확인하기 위해
        window.SetInputLocked(true);
        if (kind == SelectionKind.RollDice && diceRoll != null) diceRoll.LockRoll();
        EndBoardSelection(); // 보드에서 칸을 고르는 중이었다면 더 이상 고를 수 없게 한다

        DOVirtual.DelayedCall(timeoutCloseDelay, () =>
        {
            // 그 사이에 다른 선택이 시작됐다면 그 창을 닫지 않는다
            if (epoch != timerEpoch) return;
            // 주사위 굴리기 UI는 화면에 계속 두고 비활성화만 한다. (다음 내 차례에 ShowRollDicePopup이 다시 켠다)
            if (kind != SelectionKind.RollDice) window.Close();
            if (kind == SelectionKind.Sell) SellOnTimeout(sellSessionAtTimeout); // 매각을 마치지 못하고 시간이 끝나면 자동 선택한 땅을 판다
            SelectionTimedOut?.Invoke(kind);
        }, ignoreTimeScale: true);
    }

    #endregion

    #region 조회 도우미

    // 파괴된 Unity 객체도 진짜 null로 돌려줘서 "Game != null"만 확인하면 되게 한다
    private GameManager Game => GameManager.Instance != null ? GameManager.Instance : null;
    private GameState State => Game != null ? Game.gameState : null;

    private PlayerState GetPlayer(long playerId)
        => State != null ? State.PlayerStates.Find(p => p.PlayerId == playerId) : null;

    private PlayerState CurrentPlayer => State != null ? GetPlayer(State.CurrentPlayerId) : null;

    private PropertyState GetProperty(int propertyId)
        => State != null ? State.PropertyStates.Find(p => p.PropertyId == propertyId) : null;

    // 플레이어 카드 번호(0~3). GameState.PlayerStates의 순서와 같다. 없으면 -1
    private int IndexOf(long playerId)
        => State != null ? State.PlayerStates.FindIndex(p => p.PlayerId == playerId) : -1;

    private int ColorIndexOf(long playerId) => Mathf.Max(0, IndexOf(playerId));

    private string NameOf(long playerId)
    {
        if (profiles.TryGetValue(playerId, out Profile profile) && !string.IsNullOrEmpty(profile.Name)) return profile.Name;
        PlayerState player = GetPlayer(playerId); // 지정하지 않았으면 게임 상태(서버에서 받은 닉네임)를 쓴다
        if (player != null && !string.IsNullOrEmpty(player.Nickname)) return player.Nickname;
        int index = IndexOf(playerId);
        return index >= 0 ? $"플레이어 {index + 1}" : $"플레이어 {playerId}";
    }

    // 지정한 초상화가 없으면: 서버가 준 번호(게임 상태, 없으면 대기방 정보), 그것도 없으면 내 것은 내가 고른 그림, 다른 사람은 자리 색의 기본 그림
    private Sprite PortraitOf(long playerId)
    {
        if (profiles.TryGetValue(playerId, out Profile profile) && profile.Portrait != null) return profile.Portrait;

        // 봇은 전용 로봇 초상화 (게임 상태의 bot 값, 없으면 대기방 정보의 bot 값)
        PlayerState player = GetPlayer(playerId);
        PlayerSnapshot roomPlayer = LobbyManager.CurrentRoom?.Players?.Find(p => p.playerId == playerId);
        if ((player != null && player.IsBot) || (roomPlayer != null && roomPlayer.bot))
        {
            Sprite bot = PortraitLibrary.Bot;
            if (bot != null) return bot;
        }

        int? portraitId = player?.PortraitId ?? roomPlayer?.portraitId;
        if (portraitId.HasValue) return PortraitLibrary.For(portraitId, ColorIndexOf(playerId));

        return playerId == LocalPlayerId ? PortraitLibrary.Selected : PortraitLibrary.Default(ColorIndexOf(playerId));
    }

    private readonly Dictionary<int, Sprite> tileImages = new Dictionary<int, Sprite>();

    // 땅 그림: Resources/PropertyImages/property_땅번호. 없으면 null (창이 자리표시를 보여 준다)
    private Sprite TileImageOf(int propertyId)
    {
        if (!tileImages.TryGetValue(propertyId, out Sprite sprite))
        {
            sprite = Resources.Load<Sprite>($"PropertyImages/property_{propertyId}");
            tileImages[propertyId] = sprite;
        }
        return sprite;
    }

    private static string TileName(int propertyId)
    {
        PropertyData data = PropertyManager.Instance.GetData(propertyId);
        return data != null ? data.CityName : $"땅 {propertyId}";
    }

    #endregion
}