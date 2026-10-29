using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

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

    [SerializeField]private string gameSceneName = "BoardLayout 2";

    private System.Random random; // 선턴 정하기나 랜덤 주사위 값을 계산할 때 사용합니다.

    private const int IslandTurns = 3; // 무인도 영업정지 턴 수

    private long salaryAmount = 100000;   // TODO: 월급 금액 확정 필요
    private long taxAmount = 100000;      // 세무조사 벌금 
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

    private void Log(string message)
    {
        Debug.Log($"[Game] {message}");
    }

    // 로그용 이름: "1P[123]", "2P(봇)[456]"처럼 순서와 봇 여부를 보여줍니다.
    private string P(long playerId)
    {
        int order = gameState.PlayerOrder.IndexOf(playerId) + 1;
        string nickname = gameState.PlayerStates[order - 1].Nickname;
        return $"[{order}P]{nickname}";
    }

    private string Won(long amount)
    {
        return $"{amount:N0}원";
    }

    private string CityName(int propertyId)
    {
        PropertyData data = PropertyManager.Instance.GetData(propertyId);
        return data != null ? data.CityName : $"땅{propertyId}";
    }

    private string TileName(int position)
    {
        TileData tile = BoardManager.Instance.GetTileData(position);
        if (tile == null) return $"{position}번";

        if (tile.Type == TileType.PROPERTY)
            return $"{CityName(tile.PropertyId)}({position}번)";

        return $"{tile.Type}({position}번)";
    }

    // 파산하지 않은 플레이어가 1명 이하면 게임 종료
    private bool IsGameOver()
    {
        return GameRules.IsGameOver(gameState);
    }



    // 최종 순위를 FinalRank 순으로 로그에 남깁니다.
    private void LogRanking()
    {
        var ranked = new List<PlayerState>(gameState.PlayerStates);
        ranked.Sort((a, b) => a.FinalRank.CompareTo(b.FinalRank));
        foreach (PlayerState player in ranked)
        {
            long totalAsset = gameData.Properties.GetTotalAsset(player.PlayerId, player.Money, gameState.PropertyStates);
            string result = player.IsBankrupt ? "파산" : (player.FinalRank == 1 ? "승리" : "패배");
            Log($"  {player.FinalRank}위 {P(player.PlayerId)}: 총자산 {Won(totalAsset)} (현금 {Won(player.Money)}) - {result}");
        }
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
        LogRanking();

        // 게임 결과 창을 띄웁니다. (총자산 순으로 순위 표시)
        if (UIManager.Instance != null)
            UIManager.Instance.ShowGameResultPopup(ReturnToRoom);
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
        if (UIManager.Instance != null)
            UIManager.Instance.ShowTurn(playerId);
        PlayerManager.Instance.SetCurrentTurn(playerId);

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
            if (UIManager.Instance != null)
            {
                UIManager.Instance.ShowChooseDestinationPopup();
            }
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
            if (UIManager.Instance != null)
            {
                UIManager.Instance.ShowRollDicePopup();
            }
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
        if (UIManager.Instance != null)
        {
            UIManager.Instance.ShowDiceResult(dice1, dice2,
                () => ContinueAfterDiceResult(playerId, dice1, dice2));
            return;
        }

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
            SpecialTileManager.Instance.PlayIslandEscapeFailed(playerId, outcome.IslandTurnsRemaining); // 탈출 실패 연출
            HandleTurnChanged(GetNextPlayerId()); // 강제로 턴을 넘깁니다.
            return;
        }

        // 더블로 무인도에서 탈출했습니다. (추가 턴은 없습니다)
        if (outcome.EscapedIsland)
        {
            Log($"[무인도 탈출] {P(playerId)}: 더블로 탈출했습니다!");
            SpecialTileManager.Instance.PlayIslandEscaped(playerId); // 탈출 연출
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
            EconomyManager.NotifyMoneyChanged(playerId, before, player.Money);
            Log($"[월급] {P(playerId)}: 출발 지점을 지나 월급 {Won(salary)} 지급 (현금 {Won(player.Money)})");
        }

        // 2) 말 이동 연출 → 끝나면 도착 처리
        isMoving = true;
        PlayerManager.Instance.MoveBySteps(
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
            EconomyManager.NotifyMoneyChanged(playerId, before, player.Money);
            Log($"[월급] {P(playerId)}: 출발 지점을 지나 월급 {Won(salary)} 지급 (현금 {Won(player.Money)})");
        }

        // 2) 타일을 따라 이동하는 연출 → 끝나면 도착 처리
        isMoving = true;
        PlayerManager.Instance.MoveAlongTiles(
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

    #region ProcessArrival
    private void ProcessArrival(long playerId, int toPosition)
    {
        PlayerState player = GetPlayerState(playerId);
        TileData arrivalTile = BoardManager.Instance.GetTileData(toPosition);
        OnPlayerArrived?.Invoke(playerId, toPosition);

        Log($"[도착] {P(playerId)} → {TileName(toPosition)} (현금 {Won(player.Money)})");

        switch (arrivalTile.Type)
        {
            case TileType.START: // 시작지점에 도착
                Log("  출발 칸: 특별한 효과 없음");
                ProcessEndTurn(); // 턴을 종료합니다. 
                break;

            case TileType.ISLAND: // 무인도에 도착
                player.IslandTurnsRemaining = IslandTurns;
                Log("  무인도: 턴이 넘어갑니다.");
                HandleTurnChanged(GetNextPlayerId()); // 강제로 턴을 넘깁니다.
                break;

            case TileType.CHARITY: // 기부금수령(푸드 페스티벌)에 도착
                HandleWelfareFundReceived(playerId, gameState.WelfareFund); // 쌓인 적립금을 모두 받습니다.
                ProcessEndTurn();
                break;

            case TileType.DONATION: // 기부금납부(세무조사)에 도착
                HandleDonationPaid(playerId, taxAmount); // 벌금을 내고 적립금에 쌓습니다.
                ProcessEndTurn();
                break;

            case TileType.WORLD_TRAVEL: // 자유여행에 도착
                // 강제로 턴을 넘깁니다.
                Log("  세계여행 칸: 턴이 넘어갑니다.");
                HandleTurnChanged(GetNextPlayerId());
                break;

            case TileType.GOLDEN_KEY: // 황금열쇠에 도착
                // 황금 열쇠 카드 드로우 (턴 처리는 각 CardEffect 실행 메서드에서 진행하므로 ProcessEndTurn 호출하지 않음)
                Log("  황금열쇠 카드를 뽑습니다.");
                DrawCard();
                break;

            case TileType.PROPERTY: // 자산 유형의 타일에 도착
                int propertyId = arrivalTile.PropertyId;
                PropertyState propertyState = GetPropertyState(propertyId);
                PropertyData propertyData = PropertyManager.Instance.GetData(propertyId);

                if (propertyState == null || propertyData == null)
                {
                    Debug.LogError($"[GameManager] 땅 데이터 없음: propertyId {propertyId}");
                    ProcessEndTurn();
                    break;
                }

                if (propertyState.OwnerId == null) // 주인 없는 땅인 경우
                {
                    long landPrice = PropertyManager.Instance.GetLandPrice(propertyState.PropertyId);
                    bool canAffordToPurchase = player.Money >= landPrice;
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
                }
                else if (propertyState.OwnerId.Value == playerId) // 본인 소유의 땅인 경우
                {
                    bool canBuild = propertyData.CanBuild;

                    // 건설 불가 자산 Case 처리
                    if (!canBuild)
                    {
                        Log($"  내 땅: {CityName(propertyId)} 현재 BuildingLevel : {BuildingLevel.Land} / 건설 불가능 - 건설 불가 칸");
                        HandleBuildPrompt(playerId, propertyId, false);
                        return;
                    }

                    // 이미 최고 레벨인 Case 처리
                    BuildingLevel currentLevel = propertyState.BuildingLevel;
                    bool isMaxLevel = currentLevel == BuildingLevel.Hotel;
                    if (isMaxLevel)
                    {
                        Log($"  내 땅: {CityName(propertyId)} 현재 BuildingLevel : {currentLevel} / 건설 불가능 - 이미 최고 단계");
                        HandleBuildPrompt(playerId, propertyId, false);
                        return;
                    }

                    // 돈이 부족한 Case 처리
                    long buildCost = PropertyManager.Instance.GetBuildCost(propertyId, (BuildingLevel)(currentLevel + 1));
                    if (player.Money < buildCost)
                    {
                        Log($"  내 땅: {CityName(propertyId)} 현재 BuildingLevel : {currentLevel}) / 건설 불가능 - 현금 부족 (건설 비용 : {Won(buildCost)})");
                        HandleBuildPrompt(playerId, propertyId, false);
                        return;
                    }

                    // 건설 가능한 Case 처리
                    Log($"  내 땅: {CityName(propertyId)} 현재 BuildingLevel : {currentLevel}) 건설 가능 (비용 {Won(buildCost)})");
                    HandleBuildPrompt(playerId, propertyId, true);
                }
                else // 타인 소유의 땅인 경우
                {
                    long ownerId = propertyState.OwnerId.Value;
                    long toll = PropertyManager.Instance.GetToll(propertyState);
                    Log($"  남의 땅: {CityName(propertyId)} (주인 {P(ownerId)}, 통행료 {Won(toll)})");
                    if (player.Money >= toll) // 통행료 납부 가능하면
                    {
                        HandleTollPaid(playerId, ownerId, toll); // 통행료 납부 처리. GameState를 갱신합니다.

                        long acquireValue = PropertyManager.Instance.GetAcquireValue(propertyState);
                        if (player.Money >= acquireValue) // 납부하고도 인수할 돈이 있다면 
                        {
                            Log($"  인수 가능 (인수가 {Won(acquireValue)})");
                            HandleAcquirePropertyPrompt(playerId, propertyId, acquireValue); // 인수 선택지 UI를 표시합니다. ( 호출에 필요한 매개변수는 GameState 와 Data를 조회하여 얻습니다. )
                        }
                        else
                        {
                            Log($"  인수 불가 (인수가 {Won(acquireValue)}, 현금 {Won(player.Money)})");
                            ProcessEndTurn();
                        }
                    }
                    else if (player.Money + PropertyManager.Instance.GetTotalSellValue(playerId, gameState.PropertyStates) >= toll) // 통행료 납부 불가지만, 자산을 팔면 납부 가능하면
                    {
                        Log($"  현금 부족: 자산을 팔아서 통행료를 내야 합니다.");
                        HandleSellPropertiesPrompt(playerId, ownerId, toll); // 자산 매각 선택지 UI를 표시합니다. ( 호출에 필요한 매개변수는 GameState 와 Data를 조회하여 얻습니다. )
                    }
                    else
                    {
                        Log($"  현금과 자산을 모두 팔아도 통행료를 낼 수 없습니다. 파산!");
                        HandleBankruptcy(playerId, ownerId); // 재산 현금화 -> 수납자 지급 -> 파산 처리. GameState를 갱신합니다.                  
                        HandleTurnChanged(GetNextPlayerId()); // 강제로 턴을 넘깁니다.
                    }
                }

                break;
        }
        
    }
    #endregion

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
        PlayerManager.Instance.MoveToTile(playerId, toPosition, () =>
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

    public void ProcessBankruptcy(long playerId)
    {
        // playerId에 해당하는 GameState.PlayerStates의 PlayerState.IsBankrupt 값을 true로 갱신합니다.
        var player = GetPlayerState(playerId);
        if (player == null || player.IsBankrupt) return; // 이미 파산한 플레이어는 다시 처리하지 않습니다.

        // 최종 등수를 정합니다. 파산하는 시점에 남아 있던 인원 수가 등수입니다. (예: 4명 중 첫 파산 → 4위)
        int aliveCount = 0;
        foreach (var p in gameState.PlayerStates)
        {
            if (!p.IsBankrupt) aliveCount++;
        }
        player.FinalRank = aliveCount;

        player.IsBankrupt = true;
        Log($"[파산] {P(playerId)}가 파산했습니다. (최종 {player.FinalRank}위)");

        // 파산 연출을 재생합니다. (GameState를 먼저 갱신한 뒤 부르므로 UI는 IsBankrupt를 그대로 읽으면 됨)
        if (UIManager.Instance != null)
            UIManager.Instance.PlayBankruptEffect(playerId);
        OnPlayerBankrupt?.Invoke(playerId);

        // 남은 플레이어 수가 1이라면 게임 종료 함수를 호출합니다. (팀전의 경우 조건이 바뀔 수 있음.)
        // 파산 연출 뒤에 판정해서, 결과 창이 파산 연출보다 먼저 뜨지 않게 합니다.
        int remaining = 0;
        foreach (var p in gameState.PlayerStates)
        {
            if (!p.IsBankrupt) remaining++;
        }
        if (remaining <= 1)
        {
            // 게임 종료 함수를 호출합니다.
            EndGame("남은 플레이어가 1명입니다.");
        }
    }

    #region Card Effect
    // ────────────────────────── 황금 열쇠 CardEffect 실행 ──────────────────────────
    // HandleCardDrawn → ExecuteCardEffect에서 카드의 EffectType을 확인한 뒤 호출합니다.
    // 대상은 현재 턴 플레이어(gameState.CurrentPlayerId)이며, 게임 상태 변경 후 다음 흐름(턴 종료, 도착 칸 처리)까지 진행합니다.

    // 카드의 EffectType을 확인하고 해당 CardEffect 실행 메서드를 호출합니다.
    private void ExecuteCardEffect(long playerId, CardData card)
    {
        switch (card.effectType)
        {
            case CardEffectType.Bonus:
                ExecuteBonusEffect(playerId, card.amount);
                break;

            case CardEffectType.Penalty:
                ExecutePenaltyEffect(playerId, card.amount, card.penaltyToFestivalPool);
                break;

            case CardEffectType.MoveTo:
                ExecuteMoveToEffect(playerId, card.targetTileId);
                break;

            case CardEffectType.MoveBy:
                ExecuteMoveByEffect(playerId, card.steps);
                break;

            case CardEffectType.GoToInspection:
                ExecuteGoToInspectionEffect(playerId);
                break;

            default:
                Debug.LogError($"[GameManager] 처리하지 않은 CardEffectType: {card.effectType}");
                ProcessEndTurn(); // 턴이 멈추지 않도록 종료
                break;
        }
    }

    // CardEffect: Bonus - 은행에서 돈을 받는다
    private void ExecuteBonusEffect(long playerId, int amount)
    {
        PlayerState player = GetPlayerState(playerId);
        if (player == null)
        {
            ProcessEndTurn();
            return;
        }

        long before = player.Money;
        player.Money += amount;
        EconomyManager.NotifyMoneyChanged(playerId, before, player.Money);

        // 재화 획득 연출을 재생합니다.

        ProcessEndTurn();
    }

    // CardEffect: Penalty - 은행 또는 기부금(WelfareFund)에 돈을 낸다
    private void ExecutePenaltyEffect(long playerId, int amount, bool toWelfareFund)
    {
        PlayerState player = GetPlayerState(playerId);
        if (player == null)
        {
            ProcessEndTurn();
            return;
        }

        // 현금 한도 안에서만 냅니다. (매각/파산 없음)
        long paid = DeductMoneyWithinBalance(player, amount);
        if (toWelfareFund)
            gameState.WelfareFund += paid;
        Log($"[카드:벌금] {P(playerId)}: 벌금 {Won(amount)} 중 {Won(paid)} 납부 (현금 {Won(player.Money)})");

        // 재화 손실 연출을 재생합니다.

        ProcessEndTurn();
    }

    // CardEffect: MoveTo - 지정한 칸으로 앞으로 이동한다 (출발지를 지나면 월급)
    private void ExecuteMoveToEffect(long playerId, int targetTileId)
    {
        PlayerState player = GetPlayerState(playerId);
        if (player == null)
        {
            ProcessEndTurn();
            return;
        }

        int fromPosition = player.Position;
        int toPosition = targetTileId;
        bool shouldReceiveSalary = toPosition < fromPosition; // RollDice와 같은 규칙: toPosition < fromPosition 이면 출발지 통과

        Log($"[카드:이동] {P(playerId)}: {TileName(toPosition)}(으)로 이동");

        // 이동이 끝나면 MovePlayerToDestination이 알아서 ProcessArrival을 호출합니다.
        MovePlayerToDestination(playerId, fromPosition, toPosition, shouldReceiveSalary);
    }

    // CardEffect: MoveBy - N칸 이동한다 (음수면 뒤로, 뒤로 갈 때는 월급 없음)
    private void ExecuteMoveByEffect(long playerId, int steps)
    {
        PlayerState player = GetPlayerState(playerId);
        int boardSize = BoardManager.Instance.BoardData.Tiles.Count;
        if (player == null || boardSize <= 0)
        {
            Debug.LogError($"[GameManager] MoveBy 카드 실패: 플레이어 또는 보드 데이터 없음 (보드 칸 수 {boardSize})");
            ProcessEndTurn();
            return;
        }

        int fromPosition = player.Position;
        int toPosition = ((fromPosition + steps) % boardSize + boardSize) % boardSize;
        bool shouldReceiveSalary = steps > 0 && fromPosition + steps >= boardSize;

        Log($"[카드:이동] {P(playerId)}: {steps}칸 이동 ({TileName(fromPosition)} → {TileName(toPosition)})");

        // 이동이 끝나면 두 함수 모두 알아서 ProcessArrival을 호출합니다.
        if (steps > 0)
            HandlePlayerMoved(playerId, fromPosition, toPosition, shouldReceiveSalary);
        else
            MovePlayerDirectly(playerId, toPosition);   // 뒤로 이동
    }

    // CardEffect: GoToInspection - 무인도로 바로 이동한다 (월급 없음, 더블이어도 추가 턴 없음)
    private void ExecuteGoToInspectionEffect(long playerId)
    {
        PlayerState player = GetPlayerState(playerId);
        int islandPosition = FindIslandTileIndex();
        if (player == null || islandPosition < 0)
        {
            Debug.LogError($"[GameManager] 무인도 카드 실패: 플레이어 또는 무인도 칸 없음");
            ProcessEndTurn();
            return;
        }

        Log($"[카드:무인도] {P(playerId)}: 무인도로 이동합니다.");

        // 순간이동 (월급 없음) → 도착 처리에서 영업정지 설정 + 추가 턴 없이 턴 넘김
        MovePlayerDirectly(playerId, islandPosition);
    }

    // 보드에서 무인도(ISLAND) 칸 번호를 찾는다. 없으면 -1
    private int FindIslandTileIndex()
    {
        return GameRules.FindIslandTileIndex(gameData);
    }
    #endregion


    // ────────────────────────── 토지 구매 ──────────────────────────
    public void HandlePurchasePropertyPrompt(long playerId, int propertyId, bool isPurchasable)
    {
        // 자신의 차례인 경우에만 선택 가능한 UI를 표시합니다.
        if (IsLocalPlayerTurn())
        {
            if (UIManager.Instance != null)
            {
                UIManager.Instance.ShowPurchasePropertyPopup(playerId, propertyId, isPurchasable);
            }
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

        long amount = PropertyManager.Instance.GetLandPrice(propertyId);

        // GameState를 갱신합니다. (플레이어 현금 차감, 자산 주인 갱신)
        player.Money -= amount;
        SetOwnerId(property, playerId);
        SetBuildingLevel(property, BuildingLevel.Land); // 새로 산 땅은 건물 없음
        Log($"[구매] {P(playerId)}: {CityName(propertyId)} 구매 (-{Won(amount)}, 남은 현금 {Won(player.Money)})");

        // 자산 구매 연출을 재생합니다.
        OnPropertyPurchasedSuccessfully?.Invoke(playerId);
        if (UIManager.Instance != null)
            UIManager.Instance.PlayMoneyChange(playerId, -amount, "땅 구매");

        // 턴을 종료합니다.
        ProcessEndTurn();
    }


    // ────────────────────────── 건물 건설 ──────────────────────────
    public void HandleBuildPrompt(long playerId, int propertyId, bool isBuildable)
    {
        // 자신의 차례인 경우에만 건설할 것인지 선택 가능한 UI를 표시합니다.
        if (IsLocalPlayerTurn())
        {
            if (UIManager.Instance != null)
            {
                UIManager.Instance.ShowBuildPopup(playerId, propertyId, isBuildable);
            }
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

        var propertyData = PropertyManager.Instance.GetData(propertyId);

        long amount = propertyData.GetBuildCost((BuildingLevel)(propertyState.BuildingLevel + 1));

        // GameState를 갱신합니다. (플레이어 현금 차감, 건설 레벨 갱신)
        playerState.Money -= amount; //건설비 차감
        SetBuildingLevel(propertyState, propertyState.BuildingLevel + 1); // 건설 레벨 증가
        // 건설 연출을 재생합니다.
        OnBuildingBuiltSuccessfully?.Invoke(playerId);
        if (UIManager.Instance != null)
            UIManager.Instance.PlayMoneyChange(playerId, -amount, "건설");

        // 턴을 종료합니다.
        ProcessEndTurn();
    }


    // ────────────────────────── 자산 인수 ──────────────────────────
    public void HandleAcquirePropertyPrompt(long playerId, int propertyId, long amount)
    {
        // 자신의 차례인 경우에만 인수할 것인지 선택 가능한 UI를 표시합니다.
        if (IsLocalPlayerTurn())
        {
            if (UIManager.Instance != null)
            {
                UIManager.Instance.ShowAcquirePropertyPopup(playerId, propertyId);
            }
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

        // GameState를 갱신합니다. (인수자 현금 차감, 인수당하는 사람 현금 증가, 자산 주인 갱신)
        var acquirer = GetPlayerState(playerId); //인수하는 사람
        var property = GetPropertyState(propertyId); //인수할 땅
        var owner = property != null && property.OwnerId.HasValue ? GetPlayerState(property.OwnerId.Value) : null; //인수당하는 사람
        if (acquirer == null || property == null || owner == null) return;

        long amount = property != null ? PropertyManager.Instance.GetAcquireValue(property) : 0; //인수가 조회

        acquirer.Money -= amount; //인수자 돈 차감
        owner.Money += amount; //소유자 돈 증가
        SetOwnerId(property, playerId); //소유권 이전

        // 인수 연출을 재생합니다.
        if (UIManager.Instance != null)
            UIManager.Instance.PlayMoneyTransfer(playerId, owner.PlayerId, amount, "인수", "인수 대금");

        // 턴을 종료합니다.
        ProcessEndTurn();
    }


    // ────────────────────────── 자산 매각 ──────────────────────────
    public void HandleSellPropertiesPrompt(long payerId, long receiverId, long requiredAmount)
    {
        // 자신의 차례인 경우에만 청산할 자산들을 선택 가능한 UI를 표시합니다.
        if (IsLocalPlayerTurn())
        {
            if (UIManager.Instance != null)
            {
                UIManager.Instance.ShowSellPropertiesPopup(payerId, receiverId, requiredAmount);
            }
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

        int landedPropertyId = BoardManager.Instance.GetTileData(payer.Position).PropertyId;
        PropertyData landedPropertyData = PropertyManager.Instance.GetData(landedPropertyId);
        PropertyState landedPropertyState = GetPropertyState(landedPropertyId);

        long receiverId = GetPropertyState(landedPropertyId).OwnerId.Value;

        var receiver = GetPlayerState(receiverId);
        if (payer == null || receiver == null) return;

        long toll = PropertyManager.Instance.GetToll(landedPropertyState);

        // 매각가 총합 계산
        long totalAmount = 0;
        foreach (var id in propertyIds)
        {
            var property = GetPropertyState(id);
            if (property == null || property.OwnerId != payerId) continue; // 소유자가 맞는지 확인

            totalAmount += PropertyManager.Instance.GetSellValue(property);
        }

        // 매각 결과가 유효한지 확인
        if (payer.Money + totalAmount < toll)
        {
            Debug.LogWarning($"서버에서 승인한 자산 매각 처리 결과 {payer}.Money + {totalAmount}가 지불해야할 돈 {toll}보다 부족합니다. ");
            return;
        }

        // 매각된 자산을 환원
        foreach (var id in propertyIds)
        {
            var property = GetPropertyState(id);
            if (property == null || property.OwnerId != payerId) continue; // 소유자가 맞는지 확인

            SetBuildingLevel(property, BuildingLevel.Land); // 건설 레벨 초기화
            SetOwnerId(property, null); // 주인 없는 땅으로
        }

        // PlayerState 갱신
        payer.Money = payer.Money + totalAmount - toll; // 판 돈 받고 통행료 냄
        receiver.Money += toll; //통행료 전액 지급

        // 자산 청산 및 재화 이동 연출을 재생합니다.
        if (UIManager.Instance != null)
            UIManager.Instance.PlayMoneyTransfer(payerId, receiverId, toll, "통행료", "통행료 수입");

        // 턴을 종료합니다.
        ProcessEndTurn();
    }

    /// <summary>
    /// 통행료를 낼 수 없을 때 호출. 납부자의 현금 전부와 모든 땅(매각가로 현금화)을 수납자에게 주고 파산 처리한다.
    /// </summary>
    public void HandleBankruptcy(long payerId, long receiverId)
    {
        // GameState를 갱신합니다. (납부자 재산 현금화, 수납자 현금 증가, 납부자 파산)
        var payer = GetPlayerState(payerId); // 파산하는 사람
        var receiver = GetPlayerState(receiverId); // 통행료 받는 사람
        if (payer == null || receiver == null) return;

        long liquidated = 0; //땅을 전부 판 금액
        foreach (var state in gameState.PropertyStates)
        {
            if (state.OwnerId != payerId) continue;

            liquidated += PropertyManager.Instance.GetSellValue(state); // 매각가 합산
            SetOwnerId(state, null); // 주인 없는 땅으로
            SetBuildingLevel(state, BuildingLevel.Land); // 건설 레벨 초기화
        }

        receiver.Money += payer.Money + liquidated; // 납부자 현금 전액 + 땅 매각가 합계
        payer.Money = 0; // 납부자 현금 0

        ProcessBankruptcy(payerId); // 납부자 파산 처리 (GameState 갱신 → 파산 연출 → 게임 종료 판정)

        // 턴 넘기기는 ProcessArrival에서 처리 (HandleTurnChanged(GetNextPlayerId()) 호출)
    }
    public void HandleTollPaid(long payerId, long receiverId, long amount)
    {
        // GameState를 갱신합니다.
        var payer = GetPlayerState(payerId); // 통행료 내는 사람
        var receiver = GetPlayerState(receiverId); // 통행료 받는 사람
        if (payer == null || receiver == null) return;

        payer.Money -= amount; // 통행료 차감
        receiver.Money += amount; // 통행료 수령

        // 재화 이동 연출을 재생합니다.
        if (UIManager.Instance != null)
            UIManager.Instance.PlayTollEffect(payerId, receiverId, amount);
    }

    public void HandleDonationPaid(long playerId, long amount)
    {
        // GameState를 갱신합니다. (현금 감소, 적립금 증가)
        PlayerState player = GetPlayerState(playerId);
        if (player == null) return;

        // 현금 한도 안에서만 냅니다. (매각/파산 없음)
        long paid = DeductMoneyWithinBalance(player, amount);
        gameState.WelfareFund += paid;
        Log($"  [세무조사] {P(playerId)}: 벌금 {Won(amount)} 중 {Won(paid)} 납부 (현금 {Won(player.Money)}, 적립금 {Won(gameState.WelfareFund)})");

        // 재화 손실 연출을 재생합니다.
        SpecialTileManager.Instance.PlayTaxPaid(playerId, paid);
    }

    /// <summary>
    /// 현재 현금 한도 안에서만 돈을 뺀다. (카드 벌금, 세무조사처럼 매각/파산 없이 걷는 돈)
    /// 실제로 낸 금액을 반환한다. (예: 벌금 1000, 현금 500 → 500 납부, 현금 0)
    /// </summary>
    private long DeductMoneyWithinBalance(PlayerState player, long amount)
    {
        long paid = Math.Min(amount, Math.Max(0, player.Money));
        long before = player.Money;
        player.Money -= paid;
        EconomyManager.NotifyMoneyChanged(player.PlayerId, before, player.Money);
        return paid;
    }

    public void HandleWelfareFundReceived(long playerId, long amount)
    {
        // GameState를 갱신합니다. (현금 증가, 적립금 감소)
        PlayerState player = GetPlayerState(playerId);
        if (player == null) return;

        if (amount <= 0)
        {
            Log($"  [푸드 페스티벌] {P(playerId)}: 쌓인 적립금이 없습니다.");
            return;
        }

        long before = player.Money;
        player.Money += amount;
        gameState.WelfareFund -= amount;
        EconomyManager.NotifyMoneyChanged(playerId, before, player.Money);
        Log($"  [푸드 페스티벌] {P(playerId)}: 적립금 {Won(amount)} 획득 (현금 {Won(player.Money)})");

        // 재화 획득 연출을 재생합니다.
        SpecialTileManager.Instance.PlayWelfareFundReceived(playerId, amount);
    }

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
        
        // CardManager에서 CardId로 카드 데이터를 조회합니다.
        CardData card = CardManager.Instance.FindCard(cardId);
        if (card == null)
        {
            ProcessEndTurn(); // 카드 조회 실패 시 턴이 멈추지 않도록 종료
            return;
        }

        Log($"[황금열쇠] {P(playerId)}: '{card.cardName}' 카드를 뽑았습니다.");

        // 카드 연출을 재생합니다. 연출이 끝나면 EffectType에 맞는 CardEffect 실행 메서드를 호출합니다.
        // 내 카드면 확인을 눌렀을 때, 다른 플레이어 카드면 자동으로 닫힐 때 실행됩니다. (전원 자동 진행 테스트에서는 모두 자동으로 닫힘)
        UIManager.Instance.ShowCardDrawPopup(playerId, card.id, true, () => ExecuteCardEffect(playerId, card));
    }

    private void SetOwnerId(PropertyState state, long? playerId)
    {
        state.OwnerId = playerId;

        int propertyId = state.PropertyId;
        BoardManager.Instance.UpdatePropertyTileColor(propertyId, playerId);
    }

    private void SetBuildingLevel(PropertyState state, BuildingLevel buildingLevel)
    {
        state.BuildingLevel = buildingLevel;

        int propertyId = state.PropertyId;
        BoardManager.Instance.UpdatePropertyBuildingVisual(propertyId, buildingLevel);
    }

    public void SyncGameState(GameState serverGameState)
    {
        Debug.Log("after OnSync?.Invoke");
        // gameState 동기화. 덮어씀
        gameState = serverGameState;
        gameData = GameDataFactory.FromScene(); // 규칙이 읽는 보드, 땅 가격표, 카드

        salaryAmount = gameState.Config.salaryAmount;
        taxAmount = gameState.Config.taxAmount;
        startMoney = gameState.Config.startMoney;
        maxRound = gameState.Config.maxRound;
    
        // player의 색상 지정. gameState.playerStates 의 순서대로 배정
        playerColorMap = new Dictionary<long, Color>();
        for (int i = 0; i < gameState.PlayerStates.Count; i++)
        {
            PlayerState playerState = gameState.PlayerStates[i];
            playerColorMap.Add(playerState.PlayerId, UIPalette.PlayerColor(i));
        }

        // 이 화면을 조작하는 사람 플레이어를 지정하고, 플레이어 정보 카드를 GameState에 맞춰 채웁니다. UI의 값들을 GameState에 맞게 갱신합니다. 
        if (UIManager.Instance != null)
        {
            UIManager.Instance.SetLocalPlayer(LocalPlayer.Id);
            UIManager.Instance.SetMaxRound(maxRound); // 최대 라운드는 GameManager가 관리하고 UI에 알려줍니다.
            UIManager.Instance.InitPlayers();
            
            // UI 리프레시 함수 호출
            UIManager.Instance.RefreshUIFromGameState();
        }

        // Board 리프레시 함수 호출
        BoardManager.Instance.RefreshBoard();
        
        PlayerManager.Instance.Initialize(gameState.PlayerStates.ConvertAll(state => state.PlayerId));
        // PlayerPawn 리프레시 함수 호출
        PlayerManager.Instance.RefreshPawns(gameState.PlayerStates);

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
        if (UIManager.Instance != null)
            UIManager.Instance.ShowTurn(playerId);

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
        if (UIManager.Instance != null)
            UIManager.Instance.CancelDiceRolling();
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

        UIManager.Instance.CloseSelectionPopups();
        
        // 내가 보낸 요청이 아닌데 알림이 왔다면 호스트가 대신 진행한 것이다.
        // 굴리기 버튼이 남아 있으면 눌러도 거절당하므로 잠근다. (다음 내 차례에 ShowRollDicePopup이 다시 연다)
        if (!sendAndWaiting)
            UIManager.Instance.LockDiceRoll();
            
        isAfk = !sendAndWaiting;
        sendAndWaiting = false;
    }
}