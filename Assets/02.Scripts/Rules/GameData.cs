using System.Collections.Generic;

/// <summary>
/// 규칙이 읽는 게임 데이터: 보드 칸, 땅 가격표(PropertyRules), 황금열쇠 카드. 읽기만 한다.
/// 규칙이 BoardManager, PropertyManager, CardManager 같은 씬 오브젝트를 직접 부르지 않고 이것만 받아 쓰게 하려고 만들었다.
/// 호스트와 클라이언트는 각자 씬의 데이터로 GameDataFactory가 만든 GameData를 쓴다.
///
/// 조회 방식은 기존 매니저와 같다.
///   칸: TileData.Index로 찾는다. (같은 번호가 있으면 먼저 나온 칸)   (BoardManager.GetTileData)
///   카드: 같은 id가 있으면 먼저 나온 카드. 카드 id 목록은 중복 없이 덱 순서대로   (CardManager.FindCard, GetCardIds)
/// </summary>
public class GameData
{
    private readonly Dictionary<int, TileData> tilesByIndex = new Dictionary<int, TileData>();
    private readonly Dictionary<int, CardData> cardsById = new Dictionary<int, CardData>();
    private readonly List<int> cardIds = new List<int>();

    /// <summary>보드의 모든 칸. (BoardData 기준)</summary>
    public IReadOnlyList<TileData> Tiles { get; }

    /// <summary>땅 가격표와 계산 규칙</summary>
    public PropertyRules Properties { get; }

    /// <param name="tiles">보드의 칸 목록. 없으면 빈 보드</param>
    /// <param name="properties">땅 가격표와 계산 규칙</param>
    /// <param name="cards">덱에 등록된 카드 목록. 없으면 카드 없음</param>
    public GameData(IReadOnlyList<TileData> tiles, PropertyRules properties, IReadOnlyList<CardData> cards)
    {
        Tiles = tiles ?? new List<TileData>();
        Properties = properties;

        foreach (TileData tile in Tiles)
        {
            if (tile == null) continue;
            tilesByIndex.TryAdd(tile.Index, tile);
        }

        if (cards == null) return;
        foreach (CardData card in cards)
        {
            if (card == null) continue;
            if (cardsById.TryAdd(card.id, card)) cardIds.Add(card.id);
        }
    }

    /// <summary>보드의 전체 칸 수. 보드가 없으면 0.</summary>
    public int TileCount => Tiles.Count;

    /// <summary>번호로 칸을 찾는다. 없으면 null.</summary>
    public TileData GetTile(int index)
    {
        return tilesByIndex.TryGetValue(index, out TileData tile) ? tile : null;
    }

    /// <summary>덱에 등록된 카드 id 목록. (같은 id는 한 번만, 덱 순서대로)</summary>
    public IReadOnlyList<int> CardIds => cardIds;

    /// <summary>카드 id로 카드를 찾는다. 없으면 null.</summary>
    public CardData FindCard(int cardId)
    {
        return cardsById.TryGetValue(cardId, out CardData card) ? card : null;
    }
}