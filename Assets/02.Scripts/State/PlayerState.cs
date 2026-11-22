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
    
    // 황금열쇠에서 얻어 들고 있는 보관형 카드 id (얻은 순서대로). 종류마다 1장이고, 쓰거나 파산하면 지운다 (CardRules.HoldCard, TradeRules.MarkBankrupt)
    // GameData.CardIds(덱에 있는 카드 목록)와 구분하려고 이름을 나눴다. 주고받는 JSON 키는 그대로 둔다
    [Newtonsoft.Json.JsonProperty("CardIds")]
    public List<int> HeldCardIds { get; set; }

    // 남의 땅에 도착해 쓰기로 한 통행료 카드 (통행료 면제권 TollFree 또는 반값권 TollHalf). 없으면 null.
    // 카드를 쓴 뒤 매각을 거쳐 통행료를 낼 때도 같은 할인이 적용되도록, 통행료를 다 낼 때까지 남겨 둔다 (TradeRules.TollToPay)
    public CardEffectType? TollCardInUse { get; set; }

    // 세계여행 칸에 도착해 다음 차례에 목적지를 고를 예정이면 true. 차례가 시작될 때 쓰고 지운다.
    // 칸 위치가 아니라 이 값으로 정해서, 위치 바꾸기처럼 도착 없이 세계여행 칸에 선 경우에는 세계여행을 하지 않는다
    public bool WorldTravelPending { get; set; }

    // 파산 여부
    public bool IsBankrupt { get; set; }

    // 봇이면 true. 서버가 주지 않으면 false (대기방 정보의 bot 값을 따로 본다)
    [Newtonsoft.Json.JsonProperty("bot")]
    public bool IsBot { get; set; }

    // 뉴비 옵션 (v1.3). 게임을 시작할 때 대기방 정보(PlayerSnapshot.newbie)에서 가져오고 게임 중에는 바뀌지 않는다
    // 혜택(황금열쇠, 더블 25%, 3연속 더블 무인도 면제)과 제한(매각, 세계여행 목적지는 호스트가 고름). BalanceRules, GameRules.ApplyDice
    public bool IsNewbie { get; set; }

    // 봇 성향 숫자 (0~99, v1.3). 게임을 시작할 때 호스트가 봇마다 뽑고, 끝자리로 성향을 정한다 (BotSettings.PersonalityOf)
    // 게임 상태에 넣어서 방장이 바뀌거나 재접속해도 같은 성향을 쓴다. 사람과 옛 게임 상태는 없음(기본형)
    public int? BotNumber { get; set; }

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
        WorldTravelPending = false;
        IsBankrupt = false;
        FinalRank = 0;
    }
}