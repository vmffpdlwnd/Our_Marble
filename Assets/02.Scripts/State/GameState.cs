using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

public class GameState
{
    public int TurnNumber { get; set; }   // 지금까지 진행된 차례 수 (플레이어 한 명의 차례마다 1 증가)
    public int RoundNumber { get; set; }  // 현재 라운드 수. 살아 있는 플레이어가 모두 한 번씩 하면 1 증가
    public long CurrentPlayerId { get; set; }
    
    public long WelfareFund { get; set; }
    
    [JsonProperty("phase")]
    public GameMessageType? WaitingType { get; set; }
    
    public bool IsDouble { get; set; }
    
    public int ConsecutiveDoubleCount { get; set; }
    
    public bool IsGameOver { get; set; }
    
    public List<long> PlayerOrder { get; set; }
    
    public List<PlayerState> PlayerStates { get; set; }
    
    public List<PropertyState> PropertyStates{ get; set; }
    
    public GameConfig Config { get; set; }

    // 방 정보. 재접속하면 로비의 방 정보(LobbyManager.CurrentRoom)가 없으므로, 상단바는 서버가 게임 상태에 담아 준 이 값으로 채운다.
    // 서버가 주지 않으면 없음(상단바는 플레이어 수로 대신 표시한다)
    public long? HostId { get; set; }
    public int? MaxPlayers { get; set; }
    public string RoomName { get; set; } // 방 이름. 재접속하거나 방장이 바뀌어도 상단바가 같은 이름을 보여 주려고 담는다 (없으면 방장 닉네임으로 대신 만든다)
}