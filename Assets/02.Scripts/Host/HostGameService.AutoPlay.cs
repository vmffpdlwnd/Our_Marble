using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// HostGameService의 자동 진행: 봇 차례, 제한 시간 초과, AFK. (나뉜 파일 설명은 HostGameService.cs 참고)
/// 시간은 HostAutoPlayRunner가 매 프레임 Tick으로 흘려 준다. (기획서 7장)
///
/// 대신 진행(제한 시간 초과, AFK)과 봇은 같은 전략이 판단하고, 사람과 같은 처리 함수(Handle...)를 호출한다. (원본 서버와 같다)
/// 거절도 같은 경로로 호출해야 턴이 멈추지 않는다.
/// </summary>
public partial class HostGameService
{
    // 원본 서버의 BOT_DELAY_SECONDS, AFK_DELAY_SECONDS와 같다.
    // 화면의 선택창은 이보다 먼저 닫힌다. (AFK 5초, 일반 30초) 호스트가 먼저 움직이면 화면과 겹쳐 입력이 거절된다.
    private const float BotDelaySeconds = 6f; // 봇 차례: 6초 뒤 진행
    private const float AfkDelaySeconds = 6f; // AFK인 사람: 6초 뒤 대신 진행

    private readonly HashSet<long> afkPlayers = new HashSet<long>(); // 제한 시간을 넘긴 사람. 요청을 보내거나 재접속하면 풀린다
    private IBotStrategy botStrategy;

    // 지금 입력 단계를 얼마나 기다렸는지. 아래 세 값 중 하나라도 바뀌면 새 입력 단계로 보고 0부터 다시 센다.
    private float waited;
    private int waitTurn = -1;
    private int waitAction = -1;
    private GameMessageType? waitPhase;

    // 요청이 처리되어 알림이 나갈 때마다 올라간다. (더블처럼 같은 사람이 같은 단계를 다시 맡아도 새로 세기 위한 표시)
    private int actionSerial;

    /// <summary>이 호스트가 아직 진행 중인가. 끝났거나 다른 게임으로 바뀌었으면 false</summary>
    public bool IsRunning => current == this && !state.IsGameOver;

    // 봇 전략은 처음 필요할 때 만든다. (보드와 땅 가격표가 있는 게임 씬에서 만들어야 한다)
    private IBotStrategy Strategy
    {
        get
        {
            if (botStrategy == null)
            {
                botStrategy = new BasicBotStrategy(
                    BoardManager.Instance.BoardData.Tiles,
                    PropertyManager.Instance.GetToll,
                    PropertyManager.Instance.GetData,
                    BotSettings.Balanced(),
                    state.Config.maxRound,
                    message => Debug.Log($"[Bot] {message}"));
            }
            return botStrategy;
        }
    }

    // ───────────── 시간 ─────────────

    public void Tick(float deltaTime)
    {
        if (!started || state.IsGameOver || state.WaitingType == null) return;

        PlayerState actor = GetPlayer(state.CurrentPlayerId);
        if (actor == null || actor.IsBankrupt) return;

        // 새 입력 단계가 시작되면 기다린 시간을 0부터 센다
        if (waitTurn != state.TurnNumber || waitAction != actionSerial || waitPhase != state.WaitingType)
        {
            waitTurn = state.TurnNumber;
            waitAction = actionSerial;
            waitPhase = state.WaitingType;
            waited = 0f;
        }

        waited += deltaTime;
        if (waited < WaitLimit(actor)) return;

        AutoAct(actor);
    }

    // 이 차례를 몇 초 기다렸다가 대신 진행할지
    private float WaitLimit(PlayerState actor)
    {
        if (actor.IsBot) return BotDelaySeconds;
        if (afkPlayers.Contains(actor.PlayerId)) return AfkDelaySeconds;

        // 사람의 제한 시간. 0 이하면 제한이 없다.
        return state.Config.limitTimeSeconds > 0 ? state.Config.limitTimeSeconds : float.PositiveInfinity;
    }

    // ───────────── 대신 진행 ─────────────

    // 봇이거나 제한 시간을 넘긴 차례를 대신 진행한다.
    private void AutoAct(PlayerState actor)
    {
        long id = actor.PlayerId;
        bool isBot = actor.IsBot;

        if (!isBot && afkPlayers.Add(id))
            Debug.Log($"[HostGameService] 제한 시간 초과: {actor.Nickname} AFK로 표시하고 대신 진행합니다.");

        // 대신 진행이 검증에 걸려 알림 없이 끝나더라도 매 프레임 되풀이하지 않게, 다시 처음부터 기다린다
        waited = 0f;

        TileData tile = BoardManager.Instance.GetTileData(actor.Position);
        int propertyId = tile != null ? tile.PropertyId : 0;

        switch (state.WaitingType)
        {
            case GameMessageType.DICE_ROLLED:
                HandleRollDice(id);
                break;

            case GameMessageType.DESTINATION_CHOSEN:
                HandleChooseDestination(id, new ChooseDestinationRequest { type = "CHOOSE_DESTINATION", destinationPosition = ChooseDestination(actor) });
                break;

            // 구매, 건설, 인수는 봇과 제한 시간을 넘긴 사람 모두 같은 전략이 판단한다
            case GameMessageType.PROPERTY_PURCHASED:
                HandlePurchaseProperty(id, Decision("PURCHASE_PROPERTY", propertyId, WantsToPurchase(actor, propertyId)));
                break;

            case GameMessageType.BUILT:
                HandleBuild(id, Decision("BUILD", propertyId, WantsToBuild(actor, propertyId)));
                break;

            case GameMessageType.PROPERTY_ACQUIRED:
                HandleAcquireProperty(id, Decision("ACQUIRE_PROPERTY", propertyId, WantsToAcquire(actor, propertyId)));
                break;

            case GameMessageType.PROPERTIES_SOLD:
                HandleSellProperties(id, new SellPropertiesRequest { type = "SELL_PROPERTIES", propertyIds = ChooseSellProperties(actor) });
                break;

            case GameMessageType.CARD_DRAWN:
                HandleDrawCard(id);
                break;
        }
    }

    private static PropertyDecisionRequest Decision(string type, int propertyId, bool isAccept)
    {
        return new PropertyDecisionRequest { type = type, propertyId = propertyId, isAccept = isAccept };
    }

    // ───────────── 봇의 판단 ─────────────
    // 현금이 모자라 요청이 거절되어 턴이 멈추는 일이 없도록, 전략에 묻기 전에 현금과 조건을 먼저 확인한다.

    private bool WantsToPurchase(PlayerState actor, int propertyId)
    {
        PropertyState property = GetProperty(propertyId);
        if (property == null || property.OwnerId != null) return false;

        long price = PropertyManager.Instance.GetLandPrice(propertyId);
        return actor.Money >= price && Strategy.ShouldPurchase(state, actor.PlayerId, propertyId, price);
    }

    private bool WantsToBuild(PlayerState actor, int propertyId)
    {
        PropertyState property = GetProperty(propertyId);
        PropertyData data = PropertyManager.Instance.GetData(propertyId);
        if (property == null || data == null || !data.CanBuild) return false;
        if (property.OwnerId != actor.PlayerId || property.BuildingLevel >= BuildingLevel.Hotel) return false;

        long cost = PropertyManager.Instance.GetBuildCost(propertyId, property.BuildingLevel + 1);
        return actor.Money >= cost && Strategy.ShouldBuild(state, actor.PlayerId, propertyId, cost);
    }

    private bool WantsToAcquire(PlayerState actor, int propertyId)
    {
        PropertyState property = GetProperty(propertyId);
        if (property == null || !property.OwnerId.HasValue || property.OwnerId.Value == actor.PlayerId) return false;

        long price = PropertyManager.Instance.GetAcquireValue(property);
        return actor.Money >= price && Strategy.ShouldAcquire(state, actor.PlayerId, propertyId, price);
    }

    // 통행료를 낼 땅 목록. 봇과 제한 시간을 넘긴 사람 모두 전략이 고른다.
    private List<int> ChooseSellProperties(PlayerState actor)
    {
        TileData tile = BoardManager.Instance.GetTileData(actor.Position);
        PropertyState landed = tile != null && tile.Type == TileType.PROPERTY ? GetProperty(tile.PropertyId) : null;
        if (landed == null) return new List<int>();

        long toll = PropertyManager.Instance.GetToll(landed);
        return Strategy.ChooseSellProperties(state, actor.PlayerId, toll, PropertyManager.Instance.GetSellValue);
    }

    // 세계여행 목적지. 전략이 고른 곳이 갈 수 없는 칸이면 갈 수 있는 첫 칸으로 간다.
    private int ChooseDestination(PlayerState actor)
    {
        int destination = Strategy.ChooseTravelDestination(state, actor.PlayerId);
        if (IsValidDestination(destination)) return destination;

        int tileCount = BoardManager.Instance.TileCount;
        for (int i = 0; i < tileCount; i++)
        {
            if (i != actor.Position && IsValidDestination(i)) return i;
        }
        return destination;
    }

    // 보드 안의 칸이고 세계여행 칸 자체가 아니면 목적지로 고를 수 있다
    private bool IsValidDestination(int position)
    {
        return ArrivalRules.CheckDestination(gameData, position) == DestinationError.None;
    }
}