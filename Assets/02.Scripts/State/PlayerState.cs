using System.Collections.Generic;
using UnityEngine;

public class PlayerState
{
    public long PlayerId { get; set; }
    public string Nickname { get; set; }
    public int Position { get; set; }
    public long Money { get; set; }
    
    // 무인도 탈출까지 남은 턴 수
    public int IslandTurnsRemaining { get; set; }
    
    // 황금열쇠에서 얻어 들고 있는 카드 id (통행료 면제 등 나중에 쓸 자리. 아직 쓰지 않음)
    // GameData.CardIds(덱에 있는 카드 목록)와 구분하려고 이름을 나눴다. 주고받는 JSON 키는 그대로 둔다
    [Newtonsoft.Json.JsonProperty("CardIds")]
    public List<int> HeldCardIds { get; set; }

    // 파산 여부
    public bool IsBankrupt { get; set; }

    // 봇이면 true. 서버가 주지 않으면 false (대기방 정보의 bot 값을 따로 본다)
    [Newtonsoft.Json.JsonProperty("bot")]
    public bool IsBot { get; set; }

    // 고른 초상화 번호. 서버가 주지 않았거나 고르지 않았으면 없음
    public int? PortraitId { get; set; }

    // 최종 등수. 0이면 아직 정해지지 않음
    // - 파산 시: 그 시점에 남아 있던 인원 수 (먼저 파산할수록 낮은 등수)
    // - 게임 종료 시: 살아남은 플레이어에게 1위부터 부여
    public int FinalRank { get; set; }

    public PlayerState(long playerId)
    {
        PlayerId = playerId;
        Nickname = "";
        Position = 0;
        Money = 0;
        IslandTurnsRemaining = 0;
        HeldCardIds = new List<int>();
        IsBankrupt = false;
        FinalRank = 0;
    }
}
