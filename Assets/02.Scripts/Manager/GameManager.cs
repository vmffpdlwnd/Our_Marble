using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 게임 씬 전용 매니저. 호스트가 보낸 알림을 받아 게임 상태를 맞추고, 화면 연출로 이어서 진행한다.
/// 코드가 길어서 역할별로 나눈 partial class다.
///   GameManager.cs          구독, 업데이트, 상태 동기화, 재접속 복구, 조회, 입력 보호, 로그
///   GameManager.Flow.cs     턴, 주사위, 이동, 월급, 무인도, 게임 종료, 세계여행
///   GameManager.Arrival.cs  칸 도착 처리, 세무조사, 적립금
///   GameManager.Trade.cs    땅 거래(구매, 건설, 인수, 매각), 통행료, 파산
///   GameManager.Card.cs     황금열쇠
/// </summary>
public partial class GameManager : Singleton<GameManager>
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

    public void SyncGameState(GameState serverGameState)
    {
        Debug.Log("after OnSync?.Invoke");
        // gameState 동기화. 덮어씀
        gameState = serverGameState;
        gameData = GameDataFactory.FromScene(); // 규칙이 읽는 보드, 땅 가격표, 카드

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