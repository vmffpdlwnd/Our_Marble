using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

public class BoardManager : Singleton<BoardManager>
{
    // 게임 씬 전용 매니저입니다. 씬 오브젝트(말, 타일, 보드)를 참조하므로 게임 씬이 로드될 때마다 새로 만들어지게 합니다.
    protected override bool PersistAcrossScenes => false;

    [SerializeField] private BoardController boardController;

    [Tooltip("맵을 못 찾을 때 쓰는 기본 보드. (1맵 DefaultBoardInfo)")]
    [FormerlySerializedAs("boardData")]
    [SerializeField] private BoardData defaultBoard;

    [Tooltip("기본 보드 말고 고를 수 있는 보드. 방의 mapId와 BoardData의 mapId가 같은 것을 쓴다. (2맵 FranchiseBoardInfo)")]
    [SerializeField] private BoardData[] otherBoards;

    private BoardData boardData; // 이번 게임에 고른 보드. 처음 쓸 때 SelectBoard로 정한다

    public BoardController BoardController => boardController;
    public BoardData BoardData => boardData != null ? boardData : boardData = SelectBoard();
    
    /// <summary>보드의 전체 칸 수. (BoardData 기준)</summary>
    public int TileCount => BoardData != null ? BoardData.Tiles.Count : 0;
    
    private Dictionary<int, TileData> tilesByIndex;

    private void OnEnable() => GameManager.OnPlayerArrived += HandlePlayerArrived;
    private void OnDisable() => GameManager.OnPlayerArrived -= HandlePlayerArrived;

    private void LateUpdate() => RefreshTollDoubleMarks();

    /// <summary>
    /// 통행료 2배 표시를 GameState에 맞춘다. 주인이 2배를 붙인 땅(TollDoubleOwnerId == OwnerId)만 표시한다.
    /// 표시가 바뀌는 곳(카드로 고름, 통행료를 받음, 매각·인수로 주인이 바뀜, 재접속 동기화)이 여러 군데라 하나씩 부르면 빠뜨리기 쉬워서,
    /// 매 프레임 GameState와 맞춘다. 땅은 32칸 이하이고, 바뀐 칸만 다시 그린다. (BoardController.SetTollDoubleMark)
    /// </summary>
    private void RefreshTollDoubleMarks()
    {
        if (boardController == null || tilesByIndex == null) return;
        GameState state = GameManager.Instance != null ? GameManager.Instance.gameState : null;
        if (state == null || state.PropertyStates == null) return;

        foreach (var pair in tilesByIndex)
        {
            if (pair.Value == null || pair.Value.Type != TileType.PROPERTY) continue;
            PropertyState property = state.PropertyStates.Find(p => p.PropertyId == pair.Value.PropertyId);
            bool marked = property != null && property.OwnerId.HasValue && property.TollDoubleOwnerId == property.OwnerId;
            boardController.SetTollDoubleMark(pair.Key, marked);
        }
    }

    private void HandlePlayerArrived(long playerId, int tileIndex)
    {
        if (boardController == null || tileIndex < 0 || tileIndex >= boardController.BoardTiles.Count) return;
        boardController.BoardTiles[tileIndex]?.PlayAnimation();
    }










    public void RefreshBoard()
    {
        for (int i = 0; i < BoardData.Tiles.Count; i++)
        {
            TileData tileData = tilesByIndex[i];

            if (tileData.Type == TileType.PROPERTY)
            {
                int propertyId = tileData.PropertyId;
                PropertyState propertyState = GameManager.Instance.GetPropertyState(propertyId);
                long? ownerPlayerId = propertyState.OwnerId;

                if (ownerPlayerId == null) continue;

                BuildingLevel buildingLevel = propertyState.BuildingLevel;

                UpdatePropertyTileColor(propertyId, ownerPlayerId);
                UpdatePropertyBuildingVisual(propertyId, buildingLevel);
            }
        }
    }

    public void UpdatePropertyTileColor(int propertyId, long? playerId)
    {
         boardController.UpdatePropertyTileColor(propertyId, playerId);
    }

    public void UpdatePropertyBuildingVisual(int propertyId, BuildingLevel buildingLevel)
    {
        boardController.UpdatePropertyBuildingVisual(propertyId, buildingLevel);
    }




    protected override void OnAwake()
    {
        ChangeToInspectMode();
        
        if(tilesByIndex == null)
            RebuildLookup();
    }

    private void Start()
    {
        boardController.InitializeBoardTiles();
    }

    /// <summary>
    /// 방의 맵 번호(SceneFlow.GameMapId)와 같은 mapId의 보드를 고른다. 못 찾으면 기본 보드.
    /// 호스트, 참가자, 이어하기 모두 게임 시작 알림의 mapId로 게임 씬에 들어오므로 같은 보드가 골라진다.
    /// 방장이 바뀌어도 새 방장은 이미 고른 보드로 이어받는다.
    /// </summary>
    private BoardData SelectBoard()
    {
        int mapId = SceneFlow.GameMapId;
        if (defaultBoard != null && defaultBoard.MapId == mapId) return defaultBoard;

        if (otherBoards != null)
        {
            foreach (BoardData board in otherBoards)
            {
                if (board != null && board.MapId == mapId) return board;
            }
        }

        // 에디터에서 게임 씬을 바로 플레이하면 맵 번호가 0이다
        if (mapId != 0) Debug.LogWarning($"[BoardManager] mapId {mapId}의 보드가 없어 기본 보드를 씁니다.", this);
        return defaultBoard;
    }

    private void RebuildLookup()
    {
        tilesByIndex = new Dictionary<int, TileData>();
        BoardData boardData = BoardData;

        if (boardData == null)
        {
            Debug.LogError("[BoardManager] BoardData가 연결되지 않았습니다.", this);
            return;
        }
        
        if (boardData.Tiles == null || boardData.Tiles.Count == 0)
        {
            Debug.LogWarning($"[BoardManager] mapId: {boardData.MapId} BoardData.Tiles 초기화 안됨", this);
            return;
        }

        foreach (TileData data in boardData.Tiles)
        {
            if (data == null)
            {
                Debug.LogWarning($"[BoardManager] mapId: {boardData.MapId} BoardData.Tiles 원소 중 null값 존재", this);
                continue;
            }

            if (!tilesByIndex.TryAdd(data.Index, data))
            {
                Debug.LogWarning($"[BoardManager] mapId: {boardData.MapId} BoardData.Tiles 에서 TileData.index 중복: {data.Index})", this);
            }
        }
    }
    
    public TileData GetTileData(int index)
    {
        if (tilesByIndex == null)
        {
            RebuildLookup();
        }

        return tilesByIndex.TryGetValue(index, out TileData data)?  data : null;
    }
    
    
    
    // 타일 선택 기능과 관련된 BoardController측 함수 호출
    
    public void ChangeToSelectTravelMode()
    {
        boardController.ChangeToSelectTravelMode();
    }

    public void ChangeToSelectSellMode()
    {
        boardController.ChangeToSelectSellMode();
    }

    public void ChangeToInspectMode()
    {
        boardController.ChangeToInspectMode();
    }
}