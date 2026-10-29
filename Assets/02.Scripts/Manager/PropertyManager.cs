using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 보드의 모든 도시 칸(PropertyTile)을 땅 번호(propertyId)로 들고, 찾아주고, 비주얼 갱신을 전달한다.
/// 가격표 데이터는 PropertyManager가 관리하고, 가격 계산 규칙은 PropertyRules가 맡는다.
/// 상태 변경은 GameManager가 GameState에서 한다.
/// </summary>
public class PropertyManager : Singleton<PropertyManager>
{
    // 게임 씬 전용 매니저입니다. 씬 오브젝트(말, 타일, 보드)를 참조하므로 게임 씬이 로드될 때마다 새로 만들어지게 합니다.
    protected override bool PersistAcrossScenes => false;

    private readonly Dictionary<int, PropertyTile> tiles = new Dictionary<int, PropertyTile>();
    public IEnumerable<PropertyTile> AllTiles => tiles.Values;


    [Header("JSON 데이터가 있다면 자동으로 채워집니다.")]
    [SerializeField] private List<PropertyData> properties = new();
    private Dictionary<int, PropertyData> propertiesById;

    // ───────────── JSON 불러오기 (에디터용) ─────────────

    [Header("JSON Import")]
    [SerializeField] private TextAsset propertiesJson;

    [System.Serializable]
    private class PropertyDataListWrapper
    {
        public List<PropertyData> properties;
    }

    /// <summary>propertiesJson의 내용을 properties 리스트에 채웁니다. (에디터에서 우클릭 메뉴로 실행)</summary>
    [ContextMenu("Load Properties From JSON")]
    private void LoadPropertiesFromJson()
    {
        Debug.Log("[PropertyManager] Load Properties From JSON...");
        
        if (propertiesJson == null)
        {
            Debug.LogError("[PropertyManager] propertiesJson이 연결되지 않았습니다.", this);
            return;
        }

        var wrapper = JsonUtility.FromJson<PropertyDataListWrapper>(propertiesJson.text);
        if (wrapper == null || wrapper.properties == null || wrapper.properties.Count == 0)
        {
            Debug.LogError("[PropertyManager] JSON 파싱 실패 또는 데이터가 비어 있습니다.", this);
            return;
        }

        properties = wrapper.properties;
        propertiesById = null;   // 조회용 캐시 초기화

#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif

        Debug.Log($"[PropertyManager] JSON에서 {properties.Count}개 로드 완료", this);
    }
    
    protected override void OnAwake()
    {
        LoadPropertiesFromJson();
        
        RegisterAllTiles();
    }

    // ───────────── PropertyTile 등록 ─────────────

    /// <summary>씬에 있는 PropertyTile을 땅 번호로 등록.</summary>
    private void RegisterAllTiles()
    {
        tiles.Clear();

        foreach (var tile in FindObjectsByType<PropertyTile>(FindObjectsSortMode.None))
        {
            if (!tiles.TryAdd(tile.PropertyId, tile))
            {
                Debug.LogWarning($"[PropertyManager] 땅 번호 중복: {tile.PropertyId} ({tile.name})");
            }
        }

        Debug.Log($"[PropertyManager] 칸 {tiles.Count}개 등록");
    }

    // ───────────── PropertyTile 조회 ─────────────

    public bool TryGetTile(int propertyId, out PropertyTile tile)
    {
        return tiles.TryGetValue(propertyId, out tile);
    }

    /// <summary>칸 정보. 칸 클릭 시 UI가 사용. 땅이 아니면 null.</summary>
    public TileInfo GetTileInfo(PropertyState state)
    {
        PropertyData data = GetData(state.PropertyId);
        if(data == null) return null;

        return new TileInfo
        {
            CityName = data.CityName,
            OwnerId = state.OwnerId,
            Level = state.BuildingLevel,
            CurrentToll = state.OwnerId.HasValue ? data.GetToll(state.BuildingLevel) : 0,
            LandPrice = data.LandPrice,
            BuildCosts = new[]
            {
                data.GetBuildCost(BuildingLevel.Villa),
                data.GetBuildCost(BuildingLevel.Building),
                data.GetBuildCost(BuildingLevel.Hotel)
            }
        };
    }

    // ───────────── 비주얼 갱신 ─────────────

    /// <summary>GameState가 바뀐 뒤 GameManager가 호출. 해당 칸 비주얼 갱신.</summary>
    public void Refresh(PropertyState state, Color ownerColor)
    {
        if (!tiles.TryGetValue(state.PropertyId, out var tile))
        {
            Debug.LogWarning($"[PropertyManager] {state.PropertyId}번 땅 없음");
            return;
        }
        tile.Refresh(state, ownerColor);
    }

    // ───────────── 부동산 계산 ─────────────
    // 계산 규칙은 PropertyRules에 있다. 아래는 기존 호출을 그대로 받아 PropertyRules로 넘기는 입구다.

    private PropertyRules rules;

    /// <summary>부동산 계산 규칙. 이 매니저가 가진 가격표를 읽는다.</summary>
    public PropertyRules Rules => rules ?? (rules = new PropertyRules(GetData, message => Debug.LogWarning(message)));

    /// <summary>땅값. PurchaseProperty의 amount.</summary>
    public long GetLandPrice(int propertyId) => Rules.GetLandPrice(propertyId);

    public long GetBuildCost(int propertyId, BuildingLevel level) => Rules.GetBuildCost(propertyId, level);

    /// <summary>통행료. 주인 없는 땅이면 0.</summary>
    public long GetToll(PropertyState state) => Rules.GetToll(state);

    /// <summary>투자금 = 땅값 + 지금 단계까지 지은 건물 비용 합계. 가격표가 없는 땅이면 0.</summary>
    public long GetInvestedAmount(PropertyState state) => Rules.GetInvestedAmount(state);

    /// <summary>땅 하나의 매각가: 투자금 x 50%. 가격표가 없는 땅이면 0.</summary>
    public long GetSellValue(PropertyState state) => Rules.GetSellValue(state);

    /// <summary>총자산 = 현금 + 가진 땅의 투자금. 매각가가 아니라 투자금 기준이다.</summary>
    public long GetTotalAsset(long playerId, long cash, List<PropertyState> propertyStates) => Rules.GetTotalAsset(playerId, cash, propertyStates);

    /// <summary>플레이어가 가진 땅을 전부 팔면 받는 금액의 합계. 가진 땅이 없으면 0.</summary>
    public long GetTotalSellValue(long playerId, List<PropertyState> propertyStates) => Rules.GetTotalSellValue(playerId, propertyStates);

    /// <summary>땅 하나의 인수가: 투자금 x 200%. 가격표가 없는 땅이면 0.</summary>
    public long GetAcquireValue(PropertyState state) => Rules.GetAcquireValue(state);

    // ───────────── PropertyData 조회 ─────────────

    private void RebuildLookup()
    {
        propertiesById = new Dictionary<int, PropertyData>();

        if (properties == null || properties.Count == 0)
        {
            Debug.LogWarning($"[PropertyManager] properties 초기화 안됨", this);
            return;
        }

        foreach (PropertyData data in properties)
        {
            if (data == null)
            {
                Debug.LogWarning($"[PropertyManager] properties의 원소 중 null값 존재", this);
                continue;
            }

            if (!propertiesById.TryAdd(data.Id, data))
            {
                Debug.LogWarning($"[PropertyTable] propertyId 중복: {data.Id} ({data.CityName})", this);
            }
        }
    }

    public PropertyData GetData(int id)
    {
        if (propertiesById == null)
        {
            RebuildLookup();
        }

        return propertiesById.TryGetValue(id, out PropertyData data)?  data : null;
    }

    public List<PropertyData> GetAllDataByMapId(int mapId)
    {
        List<PropertyData> mapProperties = new List<PropertyData>();
        foreach (PropertyData property in properties) 
        {
            if(property.MapId != mapId) continue;

            mapProperties.Add(property);
        }
        return mapProperties;
    }

    /// <summary>부족분(shortage)을 채울 땅을 골라 propertyId 목록으로 돌려줍니다. 고르기만 하고 실제 매각은 하지 않습니다.</summary>
    public List<int> AutoChooseSellProperties(long ownerId, long shortage,
                                              List<PropertyState> propertyStates, SellSelectionMode mode)
    {
        return Rules.AutoChooseSellProperties(ownerId, shortage, propertyStates, mode);
    }
}