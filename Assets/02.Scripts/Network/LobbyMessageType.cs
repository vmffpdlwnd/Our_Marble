using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

[JsonConverter(typeof(StringEnumConverter))]
public enum LobbyMessageType
{
    ROOM_ENTERED,
    
    ROOM_LEFT,
    
    ROOM_LIST,
    
    ROOM_UPDATED,
    
    GAME_STARTED,
    
    LOBBY_ERROR,
    
    CHAT_BROADCAST
}