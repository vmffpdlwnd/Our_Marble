using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 게임 씬 전용 매니저. 호스트가 보낸 알림을 받아 게임 상태를 맞추고, 화면 연출로 이어서 진행한다.
/// 코드가 길어서 #region으로 구역을 나눴다. 위에서부터:
///   (구역 밖)  이벤트, 필드, 구독, 업데이트, 상태 동기화, 재접속 복구, 조회, 입력 보호, 로그
///   턴, 주사위, 이동, 월급, 무인도, 게임 종료, 세계여행
///   칸 도착 처리, 세무조사, 적립금
///   땅 거래(구매, 건설, 인수, 매각), 통행료, 파산
///   황금열쇠
/// </summary>
public class GameManager : Singleton<GameManager>
{
    public static event Action<long, int> OnPlayerArrived;
    public static event Action<long> OnPropertyPurchasedSuccessfully;
    public static event Action<long> OnBuildingBuiltSuccessfully;
    public static event Action<long> OnIslandEscapeFailed;
    public static event Action<long> OnPlayerBankrupt;

    // 게임 씬 전용 매니저입니다. 씬을 넘어 살아남으면 두 번째 게임에서 이미 파괴된 GameNetwork에 구독된 채로 남아
    // OnSync(SyncGameState)가 호출되지 않으므로, 게임 씬이 로드될 때마다 새로 만들어지게 합니다.
    protected override bool PersistAcrossScenes => false;

    // OnEnable에서 구독한 GameNetwork. OnDisable에서 같은 인스턴스에서 구독을 해제하기 위해 보관합니다.
    // (씬 언로드 중 GameNetwork.Instance를 다시 부르면 새 GameNetwork 오브젝트가 생겨 버립니다.)
    private GameNetwork subscribedNetwork;

    public GameState gameState;

    // 규칙(Rules/)이 읽는 보드, 땅 가격표, 카드. 게임 상태를 동기화할 때 게임 씬의 데이터로 만든다.
    private GameData gameData;

    // 화면에 보여 주는 일(연출, 결과 표시, 로그)은 이 클래스에 맡긴다.
    private readonly GameScreen screen = new GameScreen();

    [SerializeField]private string gameSceneName = "BoardLayout 2";

    private System.Random random; // 선턴 정하기나 랜덤 주사위 값을 계산할 때 사용합니다.


    private long startMoney = 500000;     // 초기 자금 (테스트할 때 인스펙터에서 늘려서 사용)
    private int maxRound = 30;     // 최대 라운드 (모든 플레이어가 한 번씩 하면 1라운드). 넘으면 게임 종료

    private bool isMoving;

    // 플레이어 색상 관련 필드
    private Dictionary<long, Color> playerColorMap;
    public Color GetPlayerColor(long playerId) => playerColorMap[playerId];

    public bool isAfk = false;
    private bool sendAndWaiting = false;
    
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name == gameSceneName)
        {
            Debug.Log("GameManager 초기화 작업 수행");
            gameState = null;
            GameNetwork.Instance.SendGameStateRequest();
        }
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;

        subscribedNetwork = GameNetwork.Instance;
        subscribedNetwork.OnBuilt += HandleBuilt;
        subscribedNetwork.OnCardDrawn += HandleCardDrawn;
        subscribedNetwork.OnDestinationChosen += HandleDestinationChosen;
        subscribedNetwork.OnDiceRolled += HandleDiceRolled;
        subscribedNetwork.OnPropertiesSold += HandlePropertiesSold;
        subscribedNetwork.OnPropertyAcquired += HandlePropertyAcquired;
        subscribedNetwork.OnPropertyPurchased += HandlePropertyPurchased;
        subscribedNetwork.OnGameError += HandleGameError;

        subscribedNetwork.OnSync += SyncGameState;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;

        if (subscribedNetwork == null) return; // 이미 파괴됐으면 해제할 구독도 없습니다.
        subscribedNetwork.OnBuilt -= HandleBuilt;
        subscribedNetwork.OnCardDrawn -= HandleCardDrawn;
        subscribedNetwork.OnDestinationChosen -= HandleDestinationChosen;
        subscribedNetwork.OnDiceRolled -= HandleDiceRolled;
        subscribedNetwork.OnPropertiesSold -= HandlePropertiesSold;
        subscribedNetwork.OnPropertyAcquired -= HandlePropertyAcquired;
        subscribedNetwork.OnPropertyPurchased -= HandlePropertyPurchased;
        subscribedNetwork.OnGameError -= HandleGameError;

        subscribedNetwork.OnSync -= SyncGameState;
        subscribedNetwork = null;
    }

    private void Update()
    {
        // WaitingType이 null이면 직전 메시지를 처리하는 중(카드 연출, 말 이동 등)이므로 다음 메시지를 꺼내지 않습니다.
        // 처리 흐름이 다음 Prompt 함수(HandleRollDicePrompt, DrawCard, HandlePurchasePropertyPrompt ...)에 도달해야 다시 값이 채워집니다.
        if (gameState == null || gameState.WaitingType == null) return;

        HostStateCheck.Run(gameState); // 개발용: 호스트와 상태가 같은지 비교 (에디터와 개발 빌드에서만 동작)

        GameMessageType waitingType = gameState.WaitingType.Value;
        GameNetwork.Instance.TryDequeueMessage(waitingType);
    }

    // ────────────────────────── 로그 / 봇 도우미 ──────────────────────────
    // UI가 없는 동안 진행 상황을 콘솔에서 볼 수 있도록 로그를 남깁니다.

    // 로그와 표시 이름은 GameScreen이 만든다. 부르는 곳이 많아 짧은 이름으로 전달만 한다.
    private void Log(string message) => screen.Log(message);
    private string P(long playerId) => screen.P(playerId);
    private string Won(long amount) => screen.Won(amount);
    private string CityName(int propertyId) => screen.CityName(propertyId);
    private string TileName(int position) => screen.TileName(position);

    public void SyncGameState(GameState serverGameState)
    {
        Debug.Log("after OnSync?.Invoke");
        // gameState 동기화. 덮어씀
        gameState = serverGameState;
        gameData = GameDataFactory.FromScene(); // 규칙이 읽는 보드, 땅 가격표, 카드
        screen.Bind(gameState, gameData);

        startMoney = gameState.Config.startMoney;
        maxRound = gameState.Config.maxRound;
    
        // player의 색상 지정. gameState.playerStates 의 순서대로 배정
        playerColorMap = new Dictionary<long, Color>();
        for (int i = 0; i < gameState.PlayerStates.Count; i++)
        {
            PlayerState playerState = gameState.PlayerStates[i];
            playerColorMap.Add(playerState.PlayerId, UIPalette.PlayerColor(i));
        }

        // 이 화면을 조작하는 사람 플레이어를 지정하고, 플레이어 정보 카드, 보드, 말을 GameState에 맞춰 채웁니다. 
        screen.InitializeScreen(LocalPlayer.Id, maxRound);

        if (gameState.TurnNumber == 1)
        {
            Debug.Log("SyncGameState - 최초 SyncGameState 이므로 StartGame을 호출합니다.");
            StartGame();
        }
        else
        {
            Debug.Log($"SyncGameState - 진행 중인 게임에 합류합니다. (서버 대기 phase: {gameState.WaitingType})");
            RestorePromptFromSync();
        }
    }

    /// <summary>
    /// 재접속(GAME_STATE_SYNC) 후, 서버가 기다리고 있는 입력(phase)에 맞는 선택창을 다시 띄웁니다.
    /// 각 Prompt 함수가 내 차례인지 확인하므로 남의 차례면 선택창 없이 대기 상태(WaitingType)만 맞춰집니다.
    /// 서버의 마감시간은 알 수 없지만, 이미 지났다면 서버의 자동 진행 브로드캐스트를 받으면서 선택창이 닫힙니다.
    /// </summary>
    private void RestorePromptFromSync()
    {
        if (gameState.IsGameOver || gameState.WaitingType == null) return;

        sendAndWaiting = false;
        long playerId = gameState.CurrentPlayerId;
        PlayerState player = GetPlayerState(playerId);
        if (player == null) return;

        // 이번 차례가 끝나기 전에 끊겼다면 'OO의 턴' 표시를 다시 띄웁니다.
        screen.ShowTurn(playerId);

        switch (gameState.WaitingType.Value)
        {
            case GameMessageType.DICE_ROLLED:
                HandleRollDicePrompt();
                return;

            case GameMessageType.DESTINATION_CHOSEN:
                HandleChooseDestinationPrompt();
                return;

            case GameMessageType.CARD_DRAWN:
                DrawCard(); // 원래 흐름처럼 내 차례면 카드 뽑기 요청을 바로 보냅니다.
                return;
        }

        // 나머지는 모두 도착한 땅에 대한 선택이므로, 현재 위치의 땅 정보로 매개변수를 다시 계산합니다.
        TileData tile = BoardManager.Instance.GetTileData(player.Position);
        PropertyState propertyState = tile != null && tile.Type == TileType.PROPERTY ? GetPropertyState(tile.PropertyId) : null;
        if (propertyState == null)
        {
            Debug.LogError($"[GameManager] 재접속 선택창 복구 실패: {P(playerId)}의 위치 {player.Position}가 땅이 아닙니다. (phase {gameState.WaitingType})");
            return;
        }
        int propertyId = propertyState.PropertyId;

        switch (gameState.WaitingType.Value)
        {
            case GameMessageType.PROPERTY_PURCHASED:
            {
                bool canAfford = player.Money >= PropertyManager.Instance.GetLandPrice(propertyId);
                HandlePurchasePropertyPrompt(playerId, propertyId, canAfford);
                break;
            }
            case GameMessageType.BUILT:
            {
                PropertyData data = PropertyManager.Instance.GetData(propertyId);
                BuildingLevel level = propertyState.BuildingLevel;
                bool canBuild = data != null && data.CanBuild && level != BuildingLevel.Hotel
                                && player.Money >= PropertyManager.Instance.GetBuildCost(propertyId, (BuildingLevel)(level + 1));
                HandleBuildPrompt(playerId, propertyId, canBuild);
                break;
            }
            case GameMessageType.PROPERTY_ACQUIRED:
                // 통행료는 서버에서 이미 낸 상태로 동기화되었습니다.
                HandleAcquirePropertyPrompt(playerId, propertyId, PropertyManager.Instance.GetAcquireValue(propertyState));
                break;

            case GameMessageType.PROPERTIES_SOLD:
                if (propertyState.OwnerId == null) break;
                HandleSellPropertiesPrompt(playerId, propertyState.OwnerId.Value, PropertyManager.Instance.GetToll(propertyState));
                break;
        }
    }

    private bool IsLocalPlayerTurn()
    {
        return gameState.CurrentPlayerId == LocalPlayer.Id;
    }

    /// <summary>
    /// GameState.PlayerStates에서 playerId가 같은 플레이어 상태를 찾는다.
    /// 구매, 건설, 통행료, 인수, 매각 처리에서 돈을 바꿀 대상을 찾을 때 쓴다.
    /// </summary>
    /// 
    /// 플레이어 상태, 없으면 null. (호출한 쪽에서 null 확인 필요)
    public PlayerState GetPlayerState(long playerId)
    {
        PlayerState playerState = gameState.PlayerStates.Find(p => p.PlayerId == playerId);

        if (playerState == null)
            Debug.LogError($"GameState - PlayerState not found: {playerId}");

        return playerState;
    }
    /// <summary>
    /// GameState.PropertyStates에서 propertyId가 같은 땅 상태(소유자, 건물 단계)를 찾는다.
    /// 구매, 건설, 인수, 매각 처리에서 소유자와 단계를 바꿀 대상을 찾을 때 쓴다.
    /// </summary>
    /// 
    /// 땅 상태, 없으면 null. (호출한 쪽에서 null 확인 필요)

    public PropertyState GetPropertyState(long propertyId)
    {
        PropertyState propertyState = gameState.PropertyStates.Find(p => p.PropertyId == propertyId);

        if (propertyState == null)
            Debug.LogError($"GameState - PropertyState not found: {propertyId}");

        return propertyState;
    }

        // 호스트가 내 요청을 거절했다. (예: 내가 누르기 직전에 호스트가 대신 진행해서 이미 단계가 넘어간 경우)
    // 결과를 기다리며 구르던 주사위가 그대로 남지 않게 멈춘다.
    private void HandleGameError(string code, string message)
    {
        sendAndWaiting = false;
        screen.CancelDiceRolling();
    }

    private void HandleRoutine()
    {
        // 브로드캐스트 메시지를 하나 받았으면, 그 결과 처리(연출, 이동, 도착 처리)가 끝나 다음 Prompt에 도달할 때까지
        // 큐에서 다음 메시지를 꺼내지 않도록 대기 상태를 비웁니다.
        // (이게 없으면 카드 연출/말 이동 중에 다음 메시지가 먼저 처리되어 CurrentPlayerId가 미리 바뀌고,
        //  상대방이 도착한 땅의 구매창이 로컬 플레이어에게 뜨는 문제가 생깁니다.)
        if (gameState != null)
            gameState.WaitingType = null;

        if (!IsLocalPlayerTurn()) return;

        screen.CloseSelectionPopups();
        
        // 내가 보낸 요청이 아닌데 알림이 왔다면 호스트가 대신 진행한 것이다.
        // 굴리기 버튼이 남아 있으면 눌러도 거절당하므로 잠근다. (다음 내 차례에 ShowRollDicePopup이 다시 연다)
        if (!sendAndWaiting)
            screen.LockDiceRoll();
            
        isAfk = !sendAndWaiting;
        sendAndWaiting = false;
    }

    #region 턴, 주사위, 이동, 월급, 무인도, 게임 종료, 세계여행
    // 파산하지 않은 플레이어가 1명 이하면 게임 종료
    private bool IsGameOver()
    {
        return GameRules.IsGameOver(gameState);
    }

    /// <summary>
    /// 게임을 종료합니다. (남은 플레이어 1명 이하, 최대 라운드 도달)
    /// 결과 창은 한 번만 띄우고, 이후에는 턴을 진행하지 않습니다.
    /// </summary>
    private void EndGame(string reason)
    {
        // 게임 종료 상태로 바꾸고 생존자 최종 등수를 확정하는 일은 규칙(GameRules.EndGame)이 한다. (파산자는 이미 정해짐)
        if (!GameRules.EndGame(gameState, gameData)) return;

        Log($"[게임 종료] {reason}");
        screen.LogRanking();

        // 게임 결과 창을 띄웁니다. (총자산 순으로 순위 표시)
        screen.ShowGameResultPopup(ReturnToRoom);
    }

    private void ReturnToRoom()
    {
        if (SceneFlow.ReturnToRoom()) return;
    }

    // ────────────────────────── 게임 진행 ──────────────────────────

    void StartGame()
    {
        Log($"게임 시작!");
        
        // 첫 번째 순서부터 턴 시작
        HandleTurnChanged(gameState.PlayerOrder[0]);
    }

    public void HandleTurnChanged(long playerId)
    {
        // 게임 종료 판정(새 턴을 시작하기 전), 차례와 라운드 갱신, 더블 초기화, 어떤 입력을 기다릴지는 규칙(GameRules.StartTurn)이 한다.
        switch (GameRules.StartTurn(gameState, gameData, playerId))
        {
            case TurnStartResult.AlreadyOver:
                return;

            case TurnStartResult.LastPlayerLeft:
                EndGame("남은 플레이어가 1명입니다.");
                return;

            case TurnStartResult.RoundLimit:
                EndGame($"최대 라운드({maxRound}라운드)에 도달했습니다.");
                return;
        }

        // 'OO의 턴'이라는 UI를 표시합니다.
        PlayerState playerState = GetPlayerState(playerId);
        Log($"===== {gameState.RoundNumber}/{maxRound}라운드 ({gameState.TurnNumber}번째 턴): {P(playerId)} 차례 (현금 {Won(playerState.Money)}, 위치 {TileName(playerState.Position)}) =====");
        screen.ShowTurn(playerId);
        screen.SetCurrentTurn(playerId);

        // 새 차례가 온 플레이어의 위치가 자유여행인 경우
        TileData currentTile = gameData.GetTile(playerState.Position);
        if (currentTile != null && currentTile.Type == TileType.WORLD_TRAVEL)
        {
            // 주사위를 굴리는 창 대신, 원하는 타일을 선택하는 창을 띄웁니다.
            HandleChooseDestinationPrompt();
        }
        else
        {
            HandleRollDicePrompt();
        }
    }

    public void HandleChooseDestinationPrompt()
    {
        // 다른 Prompt 함수들처럼 HandleRoutine을 부르지 않습니다.
        // HandleRoutine은 브로드캐스트를 받았을 때 쓰는 함수라서, 요청을 보내기 전인 여기서 부르면
        // isAfk = !sendAndWaiting 이 true가 되어 잠수용 짧은 제한시간(afkSelectSeconds)으로 여행지 선택창이 금방 닫힙니다.

        // 자신의 차례인 경우에만 자유 여행할 타일을 선택하는 UI를 표시합니다.
        if (IsLocalPlayerTurn())
        {
            screen.ShowChooseDestinationPopup();
        }

        gameState.WaitingType = GameMessageType.DESTINATION_CHOSEN;
    }

    /// <summary>
    /// 화면에서 자유 이동할 위치를 누르면 이 함수가 호출됩니다.
    /// </summary>
    public void ChooseDestination(int destinationPosition)
    {
        sendAndWaiting = true;
        GameNetwork.Instance.SendChooseDestinationRequest(destinationPosition);
    }
    
    public void HandleDestinationChosen(long playerId, int destinationPosition)
    {
        HandleRoutine();
        
        var player = GetPlayerState(playerId);

        int fromPosition = player.Position;
        int toPosition = destinationPosition;
        bool shouldReceiveSalary = toPosition < fromPosition;   // 출발 지점을 지나침

        Log($"[세계여행] {P(playerId)}: {TileName(fromPosition)} → {TileName(toPosition)}");

        MovePlayerToDestination(playerId, fromPosition, toPosition, shouldReceiveSalary);
    }

    public void HandleRollDicePrompt()
    {
        if (IsLocalPlayerTurn())
        {
            screen.ShowRollDicePopup();
        }

        // broadcast메시지 수신 대기 상태에 들어갑니다.
        gameState.WaitingType = GameMessageType.DICE_ROLLED;
    }

    [ContextMenu("RollDiceTest")]   // 플레이 모드에서 테스트용
    private void RollDiceTest() => RollDice();

    /// <summary>
    /// 화면의 '주사위 굴리기' 버튼을 누르면 이 함수가 호출됩니다.
    /// </summary>
    public void RollDice()
    {
        sendAndWaiting = true;
        GameNetwork.Instance.SendRollDiceRequest();
    }

    public void HandleDiceRolled(long playerId, int dice1, int dice2)
    {
        HandleRoutine();

        Log($"[주사위] {P(playerId)}: {dice1}+{dice2}={dice1 + dice2}{(dice1 == dice2 ? " (더블!)" : "")}");
        // 화면이 있으면 주사위 결과를 보여 주고, 연출이 끝난 뒤 이어서 진행합니다.
        if (screen.ShowDiceResult(dice1, dice2, () => ContinueAfterDiceResult(playerId, dice1, dice2)))
            return;

        ContinueAfterDiceResult(playerId, dice1, dice2);
    }

    /// <summary>주사위 결과 UI가 완전히 닫힌 뒤 실제 판정과 말 이동을 시작합니다.</summary>
    private void ContinueAfterDiceResult(long playerId, int dice1, int dice2)
    {
        PlayerState player = GetPlayerState(playerId);
        if (player == null)
        {
            Debug.LogError($"[GameManager] 주사위 이동 실패: 플레이어 {playerId}를 찾을 수 없습니다.");
            return;
        }

        // 말이 이동 중이면 새 주사위 결과를 처리하지 않습니다. (정상 흐름에서는 걸리지 않는 보호 장치)
        if (isMoving) return;

        // 더블 횟수, 무인도 탈출 판정, 3연속 더블, 이동할 칸과 월급 여부는 규칙(GameRules.ApplyDice)이 정합니다.
        // 규칙은 상태만 바꾸고, 연출과 이어서 진행하는 일은 아래에서 합니다.
        DiceOutcome outcome = GameRules.ApplyDice(gameState, gameData, player, dice1, dice2);

        // 무인도에서 영업정지 중인데 더블이 아니면: 이동 없이 턴이 넘어갑니다.
        if (outcome.Kind == DiceMoveKind.IslandStay)
        {
            Log($"[무인도] {P(playerId)}: 탈출 실패 (남은 영업정지 {outcome.IslandTurnsRemaining}턴)");
            OnIslandEscapeFailed?.Invoke(playerId);
            screen.PlayIslandEscapeFailed(playerId, outcome.IslandTurnsRemaining); // 탈출 실패 연출
            HandleTurnChanged(GetNextPlayerId()); // 강제로 턴을 넘깁니다.
            return;
        }

        // 더블로 무인도에서 탈출했습니다. (추가 턴은 없습니다)
        if (outcome.EscapedIsland)
        {
            Log($"[무인도 탈출] {P(playerId)}: 더블로 탈출했습니다!");
            screen.PlayIslandEscaped(playerId); // 탈출 연출
        }

        // 3연속 더블이면 이동하지 않고 무인도로 보낸다.
        if (outcome.Kind == DiceMoveKind.DirectToIsland)
        {
            Log($"[3연속 더블] {P(playerId)}: 무인도로 이동합니다.");
            MovePlayerDirectly(playerId, outcome.To); // 순간이동 (월급 없음) → 도착 처리에서 영업정지 설정 + 턴 넘김
            return;
        }

        int fromPosition = player.Position;
        Log($"[이동] {P(playerId)}: {TileName(fromPosition)} → {TileName(outcome.To)}");

        HandlePlayerMoved(playerId, fromPosition, outcome.To, outcome.ReceivesSalary);
    }

    public void HandlePlayerMoved(long playerId, int fromPosition, int toPosition, bool shouldReceiveSalary)
    {
        PlayerState player = GetPlayerState(playerId);
        if (player == null) return;

        int tileCount = BoardManager.Instance.TileCount;
        int steps = (toPosition - fromPosition + tileCount) % tileCount;

        // 1) GameState는 즉시 갱신 (이동과 월급 지급은 규칙이 한다)
        long before = player.Money;
        long salary = GameRules.MoveTo(gameState, player, toPosition, shouldReceiveSalary);
        if (shouldReceiveSalary)
        {
            screen.NotifyMoneyChanged(playerId, before, player.Money);
            Log($"[월급] {P(playerId)}: 출발 지점을 지나 월급 {Won(salary)} 지급 (현금 {Won(player.Money)})");
        }

        // 2) 말 이동 연출 → 끝나면 도착 처리
        isMoving = true;
        screen.MoveBySteps(
            playerId, fromPosition, steps,
            onTileReached: tileIndex =>
            {
                if (shouldReceiveSalary && tileIndex == 0)
                {
                    // 월급 획득 연출
                }
            },
            onCompleted: () =>
            {
                isMoving = false;
                ProcessArrival(playerId, toPosition);
            });
    }

    /// <summary>
    /// 월급 수령이 가능한 지정 위치로 이동합니다. (세계여행, 황금열쇠 지정 위치 이동)
    /// 무인도 강제 이동처럼 직선으로 가지 않고, 주사위 이동처럼 한 칸씩 뛰지도 않고, 타일을 따라 시계방향으로 빠르게 이동합니다.
    /// 출발 지점을 지나면 월급을 받고, 이동이 끝나면 도착 처리를 합니다.
    /// </summary>
    private void MovePlayerToDestination(long playerId, int fromPosition, int toPosition, bool shouldReceiveSalary)
    {
        PlayerState player = GetPlayerState(playerId);
        if (player == null) return;

        // 1) GameState는 즉시 갱신 (이동과 월급 지급은 규칙이 한다)
        long before = player.Money;
        long salary = GameRules.MoveTo(gameState, player, toPosition, shouldReceiveSalary);
        if (shouldReceiveSalary)
        {
            screen.NotifyMoneyChanged(playerId, before, player.Money);
            Log($"[월급] {P(playerId)}: 출발 지점을 지나 월급 {Won(salary)} 지급 (현금 {Won(player.Money)})");
        }

        // 2) 타일을 따라 이동하는 연출 → 끝나면 도착 처리
        isMoving = true;
        screen.MoveAlongTiles(
            playerId, fromPosition, toPosition,
            onTileReached: tileIndex =>
            {
                if (shouldReceiveSalary && tileIndex == 0)
                {
                    // 월급 획득 연출
                }
            },
            onCompleted: () =>
            {
                isMoving = false;
                ProcessArrival(playerId, toPosition);
            });
    }

    /// <summary>
    /// 말이 목적지로 직접 이동합니다. (자유여행, 뒤로 이동 카드, 무인도행 등) 월급은 없으며, 이동이 끝나면 도착 처리를 합니다.
    /// </summary>
    private void MovePlayerDirectly(long playerId, int toPosition)
    {
        PlayerState player = GetPlayerState(playerId);
        if (player == null) return;

        Log($"[직접 이동] {P(playerId)}: {TileName(player.Position)} → {TileName(toPosition)}");

        GameRules.MoveTo(gameState, player, toPosition, false); // 월급 없는 순간이동
        isMoving = true;
        screen.MoveToTile(playerId, toPosition, () =>
        {
            isMoving = false;
            ProcessArrival(playerId, toPosition);
        });
    }

    /// <summary>
    /// 턴을 종료하는 함수입니다. 무조건 턴을 넘기는게 아닙니다! 추가턴 진행 조건(isDouble == true)를 만족하면 추가턴을 진행합니다.
    /// </summary>
    private void ProcessEndTurn()
    {
        // 게임이 끝났는지, 더블이라 추가 턴인지, 다음 사람에게 넘길지는 규칙(GameRules.EndTurn)이 정합니다.
        switch (GameRules.EndTurn(gameState))
        {
            case EndTurnResult.GameOver:
                Log("게임 종료: 남은 플레이어가 1명 이하입니다.");
                return;

            case EndTurnResult.RollAgain:
                // isDouble인 경우 추가턴을 진행합니다.
                HandleRollDicePrompt();
                return;

            default:
                HandleTurnChanged(GetNextPlayerId());
                return;
        }
    }

    private long GetNextPlayerId()
    {
        return GameRules.GetNextPlayerId(gameState);
    }
    #endregion

    #region 칸 도착 처리, 세무조사, 적립금
    #region ProcessArrival
    private void ProcessArrival(long playerId, int toPosition)
    {
        PlayerState player = GetPlayerState(playerId);
        OnPlayerArrived?.Invoke(playerId, toPosition);

        Log($"[도착] {P(playerId)} → {TileName(toPosition)} (현금 {Won(player.Money)})");

        // 칸 종류별 처리(출발, 무인도, 세무조사, 적립금, 세계여행, 황금열쇠)는 규칙(ArrivalRules.ArriveAtTile)이 하고,
        // 규칙은 상태만 바꿉니다. 연출과 이어서 진행하는 일은 아래에서 합니다.
        TileArrival arrival = ArrivalRules.ArriveAtTile(gameState, gameData, player, toPosition);
        if (arrival.Kind == TileArrivalKind.MissingTile)
        {
            Debug.LogError($"[GameManager] 도착한 칸 {toPosition}의 데이터가 없습니다.");
            ProcessEndTurn();
            return;
        }

        TileData arrivalTile = arrival.Tile;

        switch (arrival.Kind)
        {
            case TileArrivalKind.EndTurn: // 시작지점에 도착
                Log("  출발 칸: 특별한 효과 없음");
                ProcessEndTurn(); // 턴을 종료합니다. 
                break;

            case TileArrivalKind.NextTurn: // 무인도(영업정지 설정은 규칙이 함) 또는 자유여행에 도착
                // 강제로 턴을 넘깁니다.
                Log(arrivalTile.Type == TileType.ISLAND ? "  무인도: 턴이 넘어갑니다." : "  세계여행 칸: 턴이 넘어갑니다.");
                HandleTurnChanged(GetNextPlayerId());
                break;

            case TileArrivalKind.Welfare: // 기부금수령(푸드 페스티벌)에 도착
                screen.ShowWelfareFundReceived(player, arrival.MoneyBefore, arrival.Amount); // 쌓인 적립금을 모두 받았습니다. (규칙이 반영)
                ProcessEndTurn();
                break;

            case TileArrivalKind.Donation: // 기부금납부(세무조사)에 도착
                screen.ShowDonationPaid(player, arrival.MoneyBefore, arrival.Amount); // 벌금을 내고 적립금에 쌓았습니다. (규칙이 반영)
                ProcessEndTurn();
                break;

            case TileArrivalKind.WaitCard: // 황금열쇠에 도착
                // 황금 열쇠 카드 드로우 (턴 처리는 각 CardEffect 실행 메서드에서 진행하므로 ProcessEndTurn 호출하지 않음)
                Log("  황금열쇠 카드를 뽑습니다.");
                DrawCard();
                break;

            case TileArrivalKind.Property: // 자산 유형의 타일에 도착
                int propertyId = arrivalTile.PropertyId;
                PropertyState propertyState = GetPropertyState(propertyId);

                // 땅에 도착했을 때 어떤 입력을 기다릴지, 남의 땅이면 통행료 이체까지는 규칙(TradeRules.ArriveAtProperty)이 합니다.
                // 규칙은 상태만 바꾸고, 로그와 선택창, 연출은 결과(Kind)대로 아래에서 합니다.
                PropertyArrival propertyArrival = TradeRules.ArriveAtProperty(gameState, gameData, player, propertyId);

                switch (propertyArrival.Kind)
                {
                    case PropertyArrivalKind.MissingData:
                        Debug.LogError($"[GameManager] 땅 데이터 없음: propertyId {propertyId}");
                        ProcessEndTurn();
                        break;

                    case PropertyArrivalKind.Purchase: // 주인 없는 땅인 경우
                    {
                        long landPrice = gameData.Properties.GetLandPrice(propertyState.PropertyId);
                        bool canAffordToPurchase = TradeRules.CheckPurchase(gameData, player, propertyState, true) == TradeError.None;
                        if (canAffordToPurchase) // 땅 구매할 돈이 충분하면
                        {
                            Log($"  빈 땅: {CityName(propertyId)} 구매 가능 (땅값 {Won(landPrice)})");
                            HandlePurchasePropertyPrompt(playerId, propertyId, true);
                        }
                        else // 땅 구매할 돈이 충분하지 않으면
                        {
                            Log($"  빈 땅: {CityName(propertyId)} 구매 불가능 (땅값 {Won(landPrice)})");
                            HandlePurchasePropertyPrompt(playerId, propertyId, false);
                        }
                        break;
                    }

                    case PropertyArrivalKind.Build: // 본인 소유의 땅인 경우
                    {
                        BuildingLevel currentLevel = propertyState.BuildingLevel;
                        TradeError buildError = TradeRules.CheckBuild(gameData, player, propertyState, true);
                        switch (buildError)
                        {
                            // 건설 불가 자산 Case 처리
                            case TradeError.CannotBuild:
                                Log($"  내 땅: {CityName(propertyId)} 현재 BuildingLevel : {BuildingLevel.Land} / 건설 불가능 - 건설 불가 칸");
                                HandleBuildPrompt(playerId, propertyId, false);
                                break;

                            // 이미 최고 레벨인 Case 처리
                            case TradeError.MaxLevel:
                                Log($"  내 땅: {CityName(propertyId)} 현재 BuildingLevel : {currentLevel} / 건설 불가능 - 이미 최고 단계");
                                HandleBuildPrompt(playerId, propertyId, false);
                                break;

                            // 돈이 부족한 Case 처리
                            case TradeError.NotEnoughMoney:
                                Log($"  내 땅: {CityName(propertyId)} 현재 BuildingLevel : {currentLevel}) / 건설 불가능 - 현금 부족 (건설 비용 : {Won(gameData.Properties.GetBuildCost(propertyId, (BuildingLevel)(currentLevel + 1)))})");
                                HandleBuildPrompt(playerId, propertyId, false);
                                break;

                            // 건설 가능한 Case 처리
                            default:
                                Log($"  내 땅: {CityName(propertyId)} 현재 BuildingLevel : {currentLevel}) 건설 가능 (비용 {Won(gameData.Properties.GetBuildCost(propertyId, (BuildingLevel)(currentLevel + 1)))})");
                                HandleBuildPrompt(playerId, propertyId, true);
                                break;
                        }
                        break;
                    }

                    default: // 타인 소유의 땅인 경우 (통행료는 규칙이 이미 이체했습니다)
                    {
                        long ownerId = propertyArrival.Owner.PlayerId;
                        long toll = propertyArrival.Toll;
                        Log($"  남의 땅: {CityName(propertyId)} (주인 {P(ownerId)}, 통행료 {Won(toll)})");

                        switch (propertyArrival.Kind)
                        {
                            case PropertyArrivalKind.TollPaidAcquire: // 통행료 납부하고도 인수할 돈이 있다면
                            {
                                screen.ShowTollPaid(playerId, ownerId, toll); // 통행료 이동 연출을 재생합니다.

                                long acquireValue = gameData.Properties.GetAcquireValue(propertyState);
                                Log($"  인수 가능 (인수가 {Won(acquireValue)})");
                                HandleAcquirePropertyPrompt(playerId, propertyId, acquireValue); // 인수 선택지 UI를 표시합니다.
                                break;
                            }

                            case PropertyArrivalKind.TollPaidEnd: // 통행료는 냈지만 인수할 돈은 없다면
                            {
                                screen.ShowTollPaid(playerId, ownerId, toll); // 통행료 이동 연출을 재생합니다.

                                long acquireValue = gameData.Properties.GetAcquireValue(propertyState);
                                Log($"  인수 불가 (인수가 {Won(acquireValue)}, 현금 {Won(player.Money)})");
                                ProcessEndTurn();
                                break;
                            }

                            case PropertyArrivalKind.Sell: // 통행료 납부 불가지만, 자산을 팔면 납부
                                Log($"  현금 부족: 자산을 팔아서 통행료를 내야 합니다.");
                                HandleSellPropertiesPrompt(playerId, ownerId, toll); // 자산 매각 선택지 UI를 표시합니다.
                                break;

                            case PropertyArrivalKind.Bankrupt: // 전부 팔아도 모자라면 파산
                                Log($"  현금과 자산을 모두 팔아도 통행료를 낼 수 없습니다. 파산!");
                                HandleBankruptcy(playerId, ownerId); // 재산 현금화 -> 수납자 지급 -> 파산 처리. GameState를 갱신합니다.
                                HandleTurnChanged(GetNextPlayerId()); // 강제로 턴을 넘깁니다.
                                break;
                        }
                        break;
                    }
                }
                break;
        }
        
    }
    #endregion
    #endregion

    #region 땅 거래, 통행료, 파산
    public void ProcessBankruptcy(long playerId)
    {
        // playerId에 해당하는 GameState.PlayerStates의 PlayerState.IsBankrupt 값을 true로 갱신합니다.
        var player = GetPlayerState(playerId);
        if (player == null) return;

        // 최종 등수를 정하고 파산 표시를 하는 일은 규칙(TradeRules.MarkBankrupt)이 합니다. (이미 파산한 플레이어는 다시 처리하지 않습니다.)
        AfterBankrupt(player, TradeRules.MarkBankrupt(gameState, player));
    }

    // 파산 뒤처리: 화면 표시(GameScreen), 파산 이벤트, 남은 플레이어가 1명이면 게임 종료. GameState는 규칙이 이미 갱신했습니다.
    private void AfterBankrupt(PlayerState player, BankruptResult result)
    {
        if (result == BankruptResult.AlreadyBankrupt) return;

        screen.ShowBankrupt(player); // 파산 로그와 연출
        OnPlayerBankrupt?.Invoke(player.PlayerId);

        // 남은 플레이어 수가 1이라면 게임 종료 함수를 호출합니다. (팀전의 경우 조건이 바뀔 수 있음.)
        // 파산 연출 뒤에 판정해서, 결과 창이 파산 연출보다 먼저 뜨지 않게 합니다.
        if (result == BankruptResult.LastPlayerLeft)
        {
            // 게임 종료 함수를 호출합니다.
            EndGame("남은 플레이어가 1명입니다.");
        }
    }


    // ────────────────────────── 토지 구매 ──────────────────────────
    public void HandlePurchasePropertyPrompt(long playerId, int propertyId, bool isPurchasable)
    {
        // 자신의 차례인 경우에만 선택 가능한 UI를 표시합니다.
        if (IsLocalPlayerTurn())
        {
            screen.ShowPurchasePropertyPopup(playerId, propertyId, isPurchasable);
        }
        
        // broadcast메시지 수신 대기 상태에 들어갑니다.
        gameState.WaitingType = GameMessageType.PROPERTY_PURCHASED;
    }

    /// <summary>
    /// 화면의 '땅 사기' 버튼을 누르면 이 함수가 호출됩니다.
    /// </summary>
    public void PurchaseProperty(long playerId, int propertyId, bool isAccept = true)
    {
        sendAndWaiting = true;
        GameNetwork.Instance.SendPurchasePropertyRequest(propertyId, isAccept);
    }

    /// <summary>
    /// 화면의 '땅 사지 않기' 버튼을 누르면 이 함수가 호출됩니다.
    /// </summary>
    public void DeclinePropertyPurchase(long playerId, int propertyId) // 거절 함수를 분리한 이유는, chatGPT한테 물어본 결과 bool매개변수를 사용하여 수락/거절을 표현하기보다 함수 자체를 분리하는것을 추천했기 때문입니다.
    {
        PurchaseProperty(playerId, propertyId, false);
    }

    public void HandlePropertyPurchased(long playerId, int propertyId, bool isAccept)
    {
        HandleRoutine();

        if (!isAccept)
        {
            Log($"[구매 안 함] {P(playerId)}: {CityName(propertyId)}");
            ProcessEndTurn();
            return;
        }

        var player = GetPlayerState(playerId); //사는 사람
        var property = GetPropertyState(propertyId); //사는 땅
        if (player == null || property == null) return;

        // GameState를 갱신합니다. (플레이어 현금 차감, 자산 주인 갱신, 새로 산 땅은 건물 없음) 규칙(TradeRules.Purchase)이 합니다.
        long amount = TradeRules.Purchase(gameData, player, property);
        screen.UpdatePropertyOwner(property);
        screen.UpdatePropertyBuilding(property);
        Log($"[구매] {P(playerId)}: {CityName(propertyId)} 구매 (-{Won(amount)}, 남은 현금 {Won(player.Money)})");

        // 자산 구매 연출을 재생합니다.
        OnPropertyPurchasedSuccessfully?.Invoke(playerId);
        screen.PlayMoneyChange(playerId, -amount, "땅 구매");

        // 턴을 종료합니다.
        ProcessEndTurn();
    }


    // ────────────────────────── 건물 건설 ──────────────────────────
    public void HandleBuildPrompt(long playerId, int propertyId, bool isBuildable)
    {
        // 자신의 차례인 경우에만 건설할 것인지 선택 가능한 UI를 표시합니다.
        if (IsLocalPlayerTurn())
        {
            screen.ShowBuildPopup(playerId, propertyId, isBuildable);
        }
        
        // broadcast메시지 수신 대기 상태에 들어갑니다.
        gameState.WaitingType = GameMessageType.BUILT;
    }

    /// <summary>
    /// 화면의 '건설하기' 버튼을 누르면 이 함수가 호출됩니다.
    /// </summary>
    public void Build(long playerId, int propertyId, bool isAccept = true)
    {
        sendAndWaiting = true;
        GameNetwork.Instance.SendBuildRequest(propertyId, isAccept);
    }

    /// <summary>
    /// 화면의 '건설하지 않기' 버튼을 누르면 이 함수가 호출됩니다.
    /// </summary>
    public void DeclineBuild(long playerId, int propertyId)
    {
        Build(playerId, propertyId, false);
    }

    public void HandleBuilt(long playerId, int propertyId, bool isAccept)
    {
        HandleRoutine();

        if (!isAccept)
        {
            Log($"[건설 안 함] {P(playerId)}: {CityName(propertyId)}");
            ProcessEndTurn();
            return;
        }

        var playerState = GetPlayerState(playerId);
        var propertyState = GetPropertyState(propertyId);
        if (playerState == null || propertyState == null) return;

        // GameState를 갱신합니다. (플레이어 현금 차감, 건설 레벨 증가) 규칙(TradeRules.Build)이 합니다.
        long amount = TradeRules.Build(gameData, playerState, propertyState);
        screen.UpdatePropertyBuilding(propertyState);

        // 건설 연출을 재생합니다.
        OnBuildingBuiltSuccessfully?.Invoke(playerId);
        screen.PlayMoneyChange(playerId, -amount, "건설");

        // 턴을 종료합니다.
        ProcessEndTurn();
    }


    // ────────────────────────── 자산 인수 ──────────────────────────
    public void HandleAcquirePropertyPrompt(long playerId, int propertyId, long amount)
    {
        // 자신의 차례인 경우에만 인수할 것인지 선택 가능한 UI를 표시합니다.
        if (IsLocalPlayerTurn())
        {
            screen.ShowAcquirePropertyPopup(playerId, propertyId);
        }

        // broadcast메시지 수신 대기 상태에 들어갑니다.
        gameState.WaitingType = GameMessageType.PROPERTY_ACQUIRED;
    }

    /// <summary>
    /// 화면의 '인수하기' 버튼을 누르면 이 함수가 호출됩니다.
    /// </summary>
    public void AcquireProperty(long playerId, int propertyId, bool isAccept = true)
    {
        sendAndWaiting = true;
        GameNetwork.Instance.SendAcquirePropertyRequest(propertyId, isAccept);
    }

    /// <summary>
    /// 화면의 '인수하지 않기' 버튼을 누르면 이 함수가 호출됩니다.
    /// </summary>
    public void DeclineAcquireProperty(long playerId, int propertyId)
    {
        AcquireProperty(playerId, propertyId, false);
    }

    public void HandlePropertyAcquired(long playerId, int propertyId, bool isAccept)
    {
        HandleRoutine();

        if (!isAccept)
        {
            Log($"[인수 안 함] {P(playerId)}: {CityName(propertyId)}");
            ProcessEndTurn();
            return;
        }

        var acquirer = GetPlayerState(playerId); //인수하는 사람
        var property = GetPropertyState(propertyId); //인수할 땅
        var owner = property != null && property.OwnerId.HasValue ? GetPlayerState(property.OwnerId.Value) : null; //인수당하는 사람
        if (acquirer == null || property == null || owner == null) return;

        // GameState를 갱신합니다. (인수자 현금 차감, 인수당하는 사람 현금 증가, 자산 주인 갱신) 규칙(TradeRules.Acquire)이 합니다.
        long amount = TradeRules.Acquire(gameData, acquirer, owner, property);
        screen.UpdatePropertyOwner(property); //소유권 이전

        // 인수 연출을 재생합니다.
        screen.PlayMoneyTransfer(playerId, owner.PlayerId, amount, "인수", "인수 대금");

        // 턴을 종료합니다.
        ProcessEndTurn();
    }


    // ────────────────────────── 자산 매각 ──────────────────────────
    public void HandleSellPropertiesPrompt(long payerId, long receiverId, long requiredAmount)
    {
        // 자신의 차례인 경우에만 청산할 자산들을 선택 가능한 UI를 표시합니다.
        if (IsLocalPlayerTurn())
        {
            screen.ShowSellPropertiesPopup(payerId, receiverId, requiredAmount);
        }

        // broadcast메시지 수신 대기 상태에 들어갑니다.
        gameState.WaitingType = GameMessageType.PROPERTIES_SOLD;
    }

    /// <summary>
    /// 화면의 '선택 완료' 버튼을 누르면 이 함수가 호출됩니다.
    /// </summary>
    public void SellProperties(long payerId, List<int> propertyIds)
    {
        sendAndWaiting = true;
        GameNetwork.Instance.SendSellPropertiesRequest(propertyIds);
    }

    public void HandlePropertiesSold(long payerId, List<int> propertyIds)
    {
        HandleRoutine();

        var payer = GetPlayerState(payerId);
        if (payer == null) return;

        // 통행료를 낼 땅 위에 있는지, 팔 땅이 모두 내 땅인지, 판 돈으로 통행료를 낼 수 있는지는 규칙(TradeRules.CheckSell)이 확인합니다.
        SellPlan plan = TradeRules.CheckSell(gameState, gameData, payer, propertyIds);
        if (plan.Error != TradeError.None)
        {
            Debug.LogWarning($"서버에서 승인한 자산 매각 처리 결과를 반영할 수 없습니다. ({plan.Error}) 판 돈 {plan.TotalAmount}, 지불해야할 돈 {plan.Toll}");
            return;
        }

        // 매각된 자산을 환원하고, 판 돈을 받아 통행료를 냅니다. 규칙(TradeRules.Sell)이 합니다.
        TradeRules.Sell(gameState, payer, plan, propertyIds);
        foreach (var id in propertyIds)
        {
            var property = GetPropertyState(id);
            if (property == null) continue;

            screen.UpdatePropertyBuilding(property); // 건설 레벨 초기화
            screen.UpdatePropertyOwner(property); // 주인 없는 땅으로
        }

        // 자산 청산 및 재화 이동 연출을 재생합니다.
        screen.PlayMoneyTransfer(payerId, plan.Receiver.PlayerId, plan.Toll, "통행료", "통행료 수입");

        // 턴을 종료합니다.
        ProcessEndTurn();
    }

    /// <summary>
    /// 통행료를 낼 수 없을 때 호출. 납부자의 현금 전부와 모든 땅(매각가로 현금화)을 수납자에게 주고 파산 처리한다.
    /// </summary>
    public void HandleBankruptcy(long payerId, long receiverId)
    {
        var payer = GetPlayerState(payerId); // 파산하는 사람
        var receiver = GetPlayerState(receiverId); // 통행료 받는 사람
        if (payer == null || receiver == null) return;

        // 재산이 정리되는 땅 목록을 미리 기억해 둡니다. (규칙이 상태를 바꾼 뒤 보드에 반영하려고)
        var ownedIds = gameState.PropertyStates.FindAll(p => p.OwnerId == payerId).ConvertAll(p => p.PropertyId);

        // GameState를 갱신합니다. (납부자 재산 현금화, 수납자 현금 증가, 납부자 파산) 규칙(TradeRules.Bankrupt)이 합니다.
        BankruptResult result = TradeRules.Bankrupt(gameState, gameData, payer, receiver);
        foreach (var id in ownedIds)
        {
            var property = GetPropertyState(id);
            if (property == null) continue;

            screen.UpdatePropertyOwner(property); // 주인 없는 땅으로
            screen.UpdatePropertyBuilding(property); // 건설 레벨 초기화
        }

        AfterBankrupt(payer, result); // 납부자 파산 처리 (파산 연출 → 게임 종료 판정)

        // 턴 넘기기는 ProcessArrival에서 처리 (HandleTurnChanged(GetNextPlayerId()) 호출)
    }
    // 통행료를 이체하고 연출을 재생합니다. 이체는 규칙(TradeRules.PayToll)이 합니다.
    // 땅 도착 처리에서는 규칙(TradeRules.ArriveAtProperty)이 이미 이체하므로 ShowTollPaid만 부릅니다. (개발용 패널은 이 함수를 직접 부릅니다)
    public void HandleTollPaid(long payerId, long receiverId, long amount)
    {
        var payer = GetPlayerState(payerId); // 통행료 내는 사람
        var receiver = GetPlayerState(receiverId); // 통행료 받는 사람
        if (payer == null || receiver == null) return;

        TradeRules.PayToll(payer, receiver, amount);
        screen.ShowTollPaid(payerId, receiverId, amount);
    }
    #endregion

    #region 황금열쇠
    #region Card Effect
    // ────────────────────────── 황금 열쇠 CardEffect 실행 ──────────────────────────
    // HandleCardDrawn → ExecuteCardEffect에서 카드의 EffectType을 확인한 뒤 호출합니다.
    // 대상은 현재 턴 플레이어(gameState.CurrentPlayerId)이며, 게임 상태 변경 후 다음 흐름(턴 종료, 도착 칸 처리)까지 진행합니다.

    // 카드 효과를 규칙(CardRules)으로 반영하고, 카드 종류에 맞게 로그와 연출을 이어서 진행합니다.
    private void ExecuteCardEffect(long playerId, CardData card)
    {
        PlayerState player = GetPlayerState(playerId);
        if (player == null)
        {
            ProcessEndTurn();
            return;
        }

        // 돈 받기, 벌금, 이동할 칸 계산은 규칙(CardRules.ApplyCard)이 하고, 규칙은 상태만 바꿉니다.
        // 아래에서는 돈 변화 알림과 로그, 말 이동 연출, 다음 흐름(턴 종료, 도착 칸 처리)을 이어서 진행합니다.
        long before = player.Money;
        int fromPosition = player.Position;
        CardOutcome outcome = CardRules.ApplyCard(gameState, gameData, player, card);

        switch (outcome.Problem)
        {
            case CardProblem.NoBoard:
                Debug.LogError($"[GameManager] MoveBy 카드 실패: 보드 데이터 없음 (보드 칸 수 {gameData.TileCount})");
                break;

            case CardProblem.NoIslandTile:
                Debug.LogError($"[GameManager] 무인도 카드 실패: 무인도 칸 없음");
                break;

            case CardProblem.UnknownEffect:
                Debug.LogError($"[GameManager] 처리하지 않은 CardEffectType: {card.effectType}");
                break;
        }

        switch (card.effectType)
        {
            case CardEffectType.Bonus: // 은행에서 돈을 받는다
                screen.NotifyMoneyChanged(playerId, before, player.Money);
                // 재화 획득 연출을 재생합니다.
                break;

            case CardEffectType.Penalty: // 은행 또는 기부금(WelfareFund)에 돈을 낸다 (현금 한도 안에서만, 매각/파산 없음)
                screen.NotifyMoneyChanged(playerId, before, player.Money);
                Log($"[카드:벌금] {P(playerId)}: 벌금 {Won(card.amount)} 중 {Won(outcome.PenaltyPaid)} 납부 (현금 {Won(player.Money)})");
                // 재화 손실 연출을 재생합니다.
                break;

            case CardEffectType.MoveTo: // 지정한 칸으로 앞으로 이동한다 (출발지를 지나면 월급)
                Log($"[카드:이동] {P(playerId)}: {TileName(outcome.To)}(으)로 이동");
                break;

            case CardEffectType.MoveBy: // N칸 이동한다 (음수면 뒤로, 뒤로 갈 때는 월급 없음)
                if (outcome.Problem == CardProblem.None)
                    Log($"[카드:이동] {P(playerId)}: {card.steps}칸 이동 ({TileName(fromPosition)} → {TileName(outcome.To)})");
                break;

            case CardEffectType.GoToInspection: // 무인도로 바로 이동한다 (월급 없음, 더블이어도 추가 턴 없음)
                if (outcome.Problem == CardProblem.None)
                    Log($"[카드:무인도] {P(playerId)}: 무인도로 이동합니다.");
                break;
        }

        switch (outcome.Kind)
        {
            case CardOutcomeKind.Move:
                // 이동이 끝나면 두 함수 모두 알아서 ProcessArrival을 호출합니다.
                if (card.effectType == CardEffectType.MoveTo)
                    MovePlayerToDestination(playerId, fromPosition, outcome.To, outcome.ReceivesSalary);
                else
                    HandlePlayerMoved(playerId, fromPosition, outcome.To, outcome.ReceivesSalary);
                break;

            case CardOutcomeKind.MoveDirectly:
                // 뒤로 이동하거나 무인도로 순간이동 (월급 없음) → 도착 처리에서 영업정지 설정 + 추가 턴 없이 턴 넘김
                MovePlayerDirectly(playerId, outcome.To);
                break;

            default:
                ProcessEndTurn(); // 돈 받기, 벌금, 효과를 못 쓴 경우: 턴 종료 (턴이 멈추지 않도록)
                break;
        }
    }

    #endregion

    public void DrawCard()
    {
        // 자신의 차례인 경우에만 요청을 보냅니다.
        if (IsLocalPlayerTurn())
        {
            sendAndWaiting = true;
            GameNetwork.Instance.SendDrawCardRequest();
        }

        // broadcast메시지 수신 대기 상태에 들어갑니다.
        gameState.WaitingType = GameMessageType.CARD_DRAWN;
    }

    public void HandleCardDrawn(long playerId, int cardId)
    {
        HandleRoutine();
        
        // GameData에서 CardId로 카드 데이터를 조회합니다.
        CardData card = gameData.FindCard(cardId);
        if (card == null)
        {
            ProcessEndTurn(); // 카드 조회 실패 시 턴이 멈추지 않도록 종료
            return;
        }

        Log($"[황금열쇠] {P(playerId)}: '{card.cardName}' 카드를 뽑았습니다.");

        // 카드 연출을 재생합니다. 연출이 끝나면 EffectType에 맞는 CardEffect 실행 메서드를 호출합니다.
        // 내 카드면 확인을 눌렀을 때, 다른 플레이어 카드면 자동으로 닫힐 때 실행됩니다. (전원 자동 진행 테스트에서는 모두 자동으로 닫힘)
        screen.ShowCardDrawPopup(playerId, card.id, true, () => ExecuteCardEffect(playerId, card));
    }
    #endregion
}