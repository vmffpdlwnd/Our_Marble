using UnityEngine;
using UnityEngine.Serialization;

// 에디터에서 카드 한 장
[CreateAssetMenu(menuName = "Card/Card Data", fileName = "CardSO")]
public class CardData : ScriptableObject
{
    [Header("기본 정보")]
    public int id;
    public string cardName;
    [TextArea] public string description;
    public Sprite icon; // 클라이언트 표시 아이콘

    [Header("효과")]
    public CardEffectType effectType;
    [Min(0)] public int amount;
    public int targetTileId;
    public int steps;
    // 벌금을 적립금(GameState.WelfareFund)에 넣을지. 옛 이름의 에셋 값을 그대로 읽는다
    [FormerlySerializedAs("penaltyToFestivalPool")] public bool penaltyToWelfareFund;
}
