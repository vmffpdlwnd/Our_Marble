using System.Collections.Generic;
using UnityEngine;

public class BoardController : MonoBehaviour
{
#if UNITY_EDITOR
    [Header("Board Layout")]
    [SerializeField, Min(1)] private int tilesPerSide = 7;
    [SerializeField] private Vector2 tileSpacing = new(0.8f, 0.45f);
    [SerializeField, Min(1f)] private float cornerSpacingMultiplier = 1.5f;

    [Header("Tile Prefabs")]
    [SerializeField] private GameObject cornerTilePrefab;
    [SerializeField] private GameObject leftTopTilePrefab;
    [SerializeField] private GameObject rightTopTilePrefab;
    [SerializeField] private GameObject rightBottomTilePrefab;
    [SerializeField] private GameObject leftBottomTilePrefab;
#endif

    [Header("Board Tiles")]
    [SerializeField] private List<BoardTile> boardTiles = new();
    
    [Header("Colors")]
    [SerializeField] private Color defaultColor = Color.gray8;
    [SerializeField] private Color defaultTextColor = Color.brown;
    [SerializeField] private Color cornerColor = Color.aliceBlue;
    [SerializeField] private Color goldenKeyColor = new Color32(255, 205, 64, 255);
    [SerializeField] private Color islandColor = new Color32(142, 205, 255, 255);
    [SerializeField] private Color worldTravelColor = new Color32(190, 160, 255, 255);
    [SerializeField] private Color taxColor = new Color32(255, 154, 154, 255);
    [SerializeField] private Color charityColor = new Color32(154, 255, 188, 255);
    public IReadOnlyList<BoardTile> BoardTiles => boardTiles;
    public int TileCount => boardTiles.Count;
    
    /// <summary>tileIndex번 타일의 월드 좌표 (말 이동용)</summary>
    public Vector3 GetTilePosition(int tileIndex)
    {
        if (tileIndex < 0 || tileIndex >= boardTiles.Count)
        {
            Debug.LogError($"BoardTile index is out of range: {tileIndex}");
            return Vector3.zero;
        }
        return boardTiles[tileIndex].transform.position;
    }
    
    public void SetBoardTileColor(int tileIndex, Color color, Color textColor)
    {
        boardTiles[tileIndex].SetSpriteColor(color);
        boardTiles[tileIndex].SetText(defaultTextColor);
    }
    
    [ContextMenu("SetDefaultColors")]
    public void SetDefaultColors()
    {
        for (int i = 0; i < boardTiles.Count; i++)
            SetBoardTileColor(i, i % 8 == 0 ? cornerColor : defaultColor, defaultTextColor);

    }
    
    /// <summary>
    /// 각 타일의 글자를 BoardData / 재산 데이터(JSON)에 맞게 표시합니다.
    /// 땅(PROPERTY)이면 도시 이름, 그 외에는 칸 종류 이름을 보여줍니다.
    /// </summary>
    public void InitializeBoardTiles()
    {
        for (int i = 0; i < boardTiles.Count; i++)
        {
            // 인덱스 설정
            boardTiles[i].Index = i;
            
            // 글자 설정
            TileData tileData = BoardManager.Instance.GetTileData(i);
            boardTiles[i].SetText(defaultTextColor, GetTileLabel(tileData));
            boardTiles[i].SetGoldKeyActive(tileData != null && tileData.Type == TileType.GOLDEN_KEY);
            boardTiles[i].SetIslandActive(tileData != null && tileData.Type == TileType.ISLAND);
            boardTiles[i].SetWorldTravelActive(tileData != null && tileData.Type == TileType.WORLD_TRAVEL);
            boardTiles[i].SetCharityActive(tileData != null && tileData.Type == TileType.CHARITY);

            PropertyData propertyData = tileData != null && tileData.Type == TileType.PROPERTY
                ? PropertyManager.Instance.GetData(tileData.PropertyId)
                : null;
            boardTiles[i].SetCanNotBuildActive(propertyData != null && !propertyData.CanBuild);
            
            // 색상 설정
            boardTiles[i].SetSpriteColor(GetTileColor(i, tileData));
        }
    }

    private Color GetTileColor(int index, TileData tileData)
    {
        if (tileData != null)
        {
            switch (tileData.Type)
            {
                case TileType.GOLDEN_KEY:    return goldenKeyColor;
                case TileType.ISLAND:       return islandColor;
                case TileType.WORLD_TRAVEL: return worldTravelColor;
                case TileType.DONATION:     return taxColor;
                case TileType.CHARITY:      return charityColor;
            }
        }

        return index % 8 == 0 ? cornerColor : defaultColor;
    }

    private string GetTileLabel(TileData tileData)
    {
        if (tileData == null)
            return "-";

        switch (tileData.Type)
        {
            case TileType.PROPERTY:
                PropertyData property = PropertyManager.Instance.GetData(tileData.PropertyId);
                return property != null ? Loc.TShort(property.CityName) : $"?{tileData.PropertyId}";
            case TileType.START:        return Loc.TShort("출발");
            case TileType.GOLDEN_KEY:   return Loc.TShort("황금열쇠");
            case TileType.ISLAND:       return Loc.TShort("무인도");
            case TileType.DONATION:     return Loc.TShort("기부금 납부");
            case TileType.CHARITY:      return Loc.TShort("기부금 수령");
            case TileType.WORLD_TRAVEL: return Loc.TShort("세계여행");
            default:                    return tileData.Type.ToString();
        }
    }
    
    
    public void UpdatePropertyTileColor(int propertyId, long? playerId)
    {
        PropertyData propertyData = PropertyManager.Instance.GetData(propertyId);
        
        int tileIndex = propertyData.BoardIndex;

        BoardTile boardTile = boardTiles[tileIndex];

        Color newColor;
        if (playerId == null) newColor = defaultColor;
        else newColor = GameManager.Instance.GetPlayerColor(playerId.Value);
        
        boardTile.SetSpriteColor(newColor);
    }
    
    public void UpdatePropertyBuildingVisual(int propertyId, BuildingLevel buildingLevel)
    {
        PropertyData propertyData = PropertyManager.Instance.GetData(propertyId);
        
        int tileIndex = propertyData.BoardIndex;

        BoardTile boardTile = boardTiles[tileIndex];

        boardTile.SetBuildingSpriteRenderers(buildingLevel);
    }
    
    
    
    
    // 타일 선택 기능
    
    private TileClickMode mode = TileClickMode.Inspect;
    
    public List<int> selectedIndexes = new List<int>();
    int maxSelectCount = 0;

    public void ChangeToSelectTravelMode()
    {
        mode = TileClickMode.SelectTravel;

        maxSelectCount = 1;
        selectedIndexes.Clear();
    }

    public void ChangeToSelectSellMode()
    {
        mode = TileClickMode.SelectSell;

        maxSelectCount = 32;
        selectedIndexes.Clear();
    }

    public void ChangeToInspectMode()
    {
        mode = TileClickMode.Inspect;
        
        ClearTileHighlights();
        
        maxSelectCount = 0;
        selectedIndexes.Clear();
    }

    public void OnBoardTileClicked(int tileIndex)
    {
        switch (mode)
        {
            case TileClickMode.Inspect:
                // 땅이면 땅 정보 창을 띄운다 (땅이 아닌 칸은 UIManager가 무시한다)
                if (UIManager.Instance != null) UIManager.Instance.OnBoardTileClicked(tileIndex);
                return;
            
            case TileClickMode.SelectTravel:
                // tileIndex가 여행 가능한 타일인지 확인 (자유여행칸으로 이동은 안됨.)
                if (tileIndex == 24) return;
                
                // selectedIndexes에 이미 있는 값인지 확인 
                if (selectedIndexes.Contains(tileIndex))
                {
                    RemoveFromSelectedIndexes(tileIndex);
                    UIManager.Instance.CheckTileValidForTravel(selectedIndexes);
                    return;
                }

                // 이미 선택된 타일 개수가 최대인지 확인
                if (selectedIndexes.Count >= maxSelectCount)
                {
                    int firxtIndex =  selectedIndexes[0];
                    RemoveFromSelectedIndexes(firxtIndex);
                    AddToSelectedIndexes(tileIndex);
                    UIManager.Instance.CheckTileValidForTravel(selectedIndexes);
                    return;
                }
                
                // selectedIndexes에 값 추가
                AddToSelectedIndexes(tileIndex);
                UIManager.Instance.CheckTileValidForTravel(selectedIndexes);
                return;
            
            case TileClickMode.SelectSell:
                // selectedIndexes에 이미 있는 값인지 확인 
                if (selectedIndexes.Contains(tileIndex))
                {
                    RemoveFromSelectedIndexes(tileIndex);
                    UIManager.Instance.CheckTilesValidForSell(selectedIndexes);
                    return;
                }
                
                // 이미 선택된 타일 개수가 최대인지 확인
                if (selectedIndexes.Count >= maxSelectCount) return;
                
                // tileIndex가 자산 타일인지 확인
                TileData tileDatadata = BoardManager.Instance.GetTileData(tileIndex);
                if (tileDatadata.Type != TileType.PROPERTY) return;
                
                // tileIndex가 현재 차례인 플레이어 소유인지 확인
                int propertyId = tileDatadata.PropertyId;
                PropertyState propertyState = GameManager.Instance.GetPropertyState(propertyId);
                if (propertyState.OwnerId != GameManager.Instance.gameState.CurrentPlayerId) return;
                
                // selectedIndexes에 값 추가
                AddToSelectedIndexes(tileIndex);
                UIManager.Instance.CheckTilesValidForSell(selectedIndexes);
                return;
        }
    }

    private void AddToSelectedIndexes(int index)
    {
        selectedIndexes.Add(index);
        boardTiles[index].SetTileHighlight(true);
    }

    private void RemoveFromSelectedIndexes(int index)
    {
        selectedIndexes.Remove(index);
        boardTiles[index].SetTileHighlight(false);
    }
    
    private void ClearTileHighlights()
    {
        foreach (BoardTile boardTile in boardTiles)
        {
            boardTile.SetTileHighlight(false);
        }
    }
}

enum TileClickMode
{
    Inspect,
    SelectTravel,
    SelectSell
}