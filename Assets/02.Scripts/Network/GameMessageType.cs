using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

[JsonConverter(typeof(StringEnumConverter))]
public enum GameMessageType
{
    [EnumMember(Value = "AWAITING_ROLL")]
    DICE_ROLLED,
    
    [EnumMember(Value = "AWAITING_DESTINATION")]
    DESTINATION_CHOSEN,
    
    [EnumMember(Value = "AWAITING_PURCHASE")]
    PROPERTY_PURCHASED,
    
    [EnumMember(Value = "AWAITING_BUILD")]
    BUILT,
    
    [EnumMember(Value = "AWAITING_ACQUIRE")]
    PROPERTY_ACQUIRED,
    
    [EnumMember(Value = "AWAITING_DRAW_CARD")] // 서버 TurnPhase.AWAITING_DRAW_CARD와 이름이 같아야 GAME_STATE_SYNC의 phase를 읽을 수 있다
    CARD_DRAWN,
    
    [EnumMember(Value = "AWAITING_SELL")]
    PROPERTIES_SOLD,
      
    // 파산 신청 알림. 기다리는 단계(phase)가 아니라서 EnumMember가 없다. (어느 입력 단계에서나 올 수 있다)
    BANKRUPTCY_DECLARED,

    GAME_STATE_SYNC,
    
    GAME_ERROR
}