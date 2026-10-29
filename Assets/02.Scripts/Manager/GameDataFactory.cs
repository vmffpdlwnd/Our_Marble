using System.Collections.Generic;

/// <summary>
/// 게임 씬의 보드, 땅 가격표, 카드 덱으로 GameData를 만든다.
/// 규칙(Rules/)은 씬 오브젝트를 모르므로, 씬 오브젝트를 읽는 일은 여기서만 한다.
/// 게임 씬이 열린 뒤에 불러야 한다.
/// </summary>
public static class GameDataFactory
{
    public static GameData FromScene()
    {
        BoardData boardData = BoardManager.Instance.BoardData;
        IReadOnlyList<TileData> tiles = boardData != null ? boardData.Tiles : null;

        var cards = new List<CardData>();
        CardDeckData deck = CardManager.Instance.DeckData;
        if (deck != null)
        {
            foreach (CardDeckData.Entry entry in deck.entries)
            {
                if (entry.card != null) cards.Add(entry.card);
            }
        }

        return new GameData(tiles, PropertyManager.Instance.Rules, cards);
    }
}