using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// HostGameService의 땅과 경제 요청 처리: 구매, 건설, 인수, 매각, 땅 도착, 파산. (나뉜 파일 설명은 HostGameService.cs 참고)
/// 요청 검증(차례, 단계, 말 위치), 알림, 로그, 다음 단계로 이어가는 일은 여기서 하고,
/// 거래가 가능한지 확인하고 상태를 바꾸는 규칙은 TradeRules가 맡는다.
/// </summary>
public partial class HostGameService
{
    // ───────────── 요청 처리 ─────────────

    // GameManager.HandlePropertyPurchased
    // 빈 땅에 도착해 구매 단계(PROPERTY_PURCHASED)에 있을 때, 구매 또는 거절을 받는다.
    private void HandlePurchaseProperty(long senderId, PropertyDecisionRequest request)
    {
        if (!Validate(senderId, GameMessageType.PROPERTY_PURCHASED, out PlayerState player, request.propertyId)) return;
        if (!ValidateOnProperty(senderId, player, request.propertyId)) return;

        PropertyState property = GetProperty(request.propertyId);

        // 8. 기능별 검증. 거절은 확인할 것이 없다. (살 돈이 없는 사람은 거절만 보낼 수 있다)
        if (RejectTrade(senderId, TradeRules.CheckPurchase(gameData, player, property, request.isAccept))) return;

        state.WaitingType = null;
        Broadcast(GameMessageType.PROPERTY_PURCHASED,
            new PropertyPurchasedBroadcast { playerId = senderId, propertyId = request.propertyId, isAccept = request.isAccept });

        if (request.isAccept)
        {
            long price = TradeRules.Purchase(gameData, player, property);
            Debug.Log($"[HostGameService] 구매: {player.Nickname} {request.propertyId}번 땅 (-{price}, 현금 {player.Money})");
        }
        else
        {
            Debug.Log($"[HostGameService] 구매 안 함: {player.Nickname} {request.propertyId}번 땅");
        }

        ProcessEndTurn();
    }

    // GameManager.HandleBuilt
    // 내 땅에 도착해 건설 단계(BUILT)에 있을 때, 건설 또는 거절을 받는다.
    private void HandleBuild(long senderId, PropertyDecisionRequest request)
    {
        if (!Validate(senderId, GameMessageType.BUILT, out PlayerState player, request.propertyId)) return;
        if (!ValidateOnProperty(senderId, player, request.propertyId)) return;

        PropertyState property = GetProperty(request.propertyId);

        // 8. 기능별 검증. 거절은 확인할 것이 없다. (건설할 수 없는 땅에서도 거절은 보낼 수 있다)
        if (RejectTrade(senderId, TradeRules.CheckBuild(gameData, player, property, request.isAccept))) return;

        state.WaitingType = null;
        Broadcast(GameMessageType.BUILT,
            new BuiltBroadcast { playerId = senderId, propertyId = request.propertyId, isAccept = request.isAccept });

        if (request.isAccept)
        {
            long cost = TradeRules.Build(gameData, player, property);
            Debug.Log($"[HostGameService] 건설: {player.Nickname} {request.propertyId}번 땅 → {property.BuildingLevel} (-{cost}, 현금 {player.Money})");
        }
        else
        {
            Debug.Log($"[HostGameService] 건설 안 함: {player.Nickname} {request.propertyId}번 땅");
        }

        ProcessEndTurn();
    }

    // GameManager.HandlePropertyAcquired
    // 남의 땅에 통행료를 내고 인수 단계(PROPERTY_ACQUIRED)에 있을 때, 인수 또는 거절을 받는다.
    private void HandleAcquireProperty(long senderId, PropertyDecisionRequest request)
    {
        if (!Validate(senderId, GameMessageType.PROPERTY_ACQUIRED, out PlayerState player, request.propertyId)) return;
        if (!ValidateOnProperty(senderId, player, request.propertyId)) return;

        PropertyState property = GetProperty(request.propertyId);

        // 8. 기능별 검증. 거절은 확인할 것이 없다.
        if (RejectTrade(senderId, TradeRules.CheckAcquire(state, gameData, player, property, request.isAccept))) return;

        state.WaitingType = null;
        Broadcast(GameMessageType.PROPERTY_ACQUIRED,
            new PropertyAcquiredBroadcast { playerId = senderId, propertyId = request.propertyId, isAccept = request.isAccept });

        if (request.isAccept)
        {
            PlayerState owner = GetPlayer(property.OwnerId.Value);
            long amount = TradeRules.Acquire(gameData, player, owner, property);
            Debug.Log($"[HostGameService] 인수: {player.Nickname} {request.propertyId}번 땅 ← {owner.Nickname} (-{amount}, 현금 {player.Money})");
        }
        else
        {
            Debug.Log($"[HostGameService] 인수 안 함: {player.Nickname} {request.propertyId}번 땅");
        }

        ProcessEndTurn();
    }

    // GameManager.HandlePropertiesSold
    // 통행료를 낼 현금이 모자라 매각 단계(PROPERTIES_SOLD)에 있을 때, 팔 땅 목록을 받는다.
    // 판 돈으로 통행료를 낸 뒤에는 인수를 선택할 수 없고 턴이 끝난다.
    private void HandleSellProperties(long senderId, SellPropertiesRequest request)
    {
        List<int> propertyIds = request.propertyIds ?? new List<int>();

        if (!Validate(senderId, GameMessageType.PROPERTIES_SOLD, out PlayerState player, propertyIds: propertyIds)) return;

        // 7, 8. 통행료를 낼 땅 위에 있어야 하고, 팔 땅 목록이 올바르고, 판 돈을 합쳐 통행료를 낼 수 있어야 한다
        SellPlan plan = TradeRules.CheckSell(state, gameData, player, propertyIds);
        if (RejectTrade(senderId, plan.Error)) return;

        state.WaitingType = null;
        Broadcast(GameMessageType.PROPERTIES_SOLD, new PropertiesSoldBroadcast { playerId = senderId, propertyIds = propertyIds });

        TradeRules.Sell(state, player, plan, propertyIds);
        Debug.Log($"[HostGameService] 매각: {player.Nickname} 땅 {propertyIds.Count}개 (+{plan.TotalAmount}), 통행료 {plan.Toll} 납부 (현금 {player.Money})");

        ProcessEndTurn();
    }

    // 규칙이 알려 준 거래 거절 이유를 요청한 사람에게 보낸다. 거절 이유가 있으면 true.
    private bool RejectTrade(long senderId, TradeError error)
    {
        switch (error)
        {
            case TradeError.None: return false;
            case TradeError.AlreadyOwned: SendError(senderId, "ALREADY_OWNED", "이미 주인이 있는 땅이에요"); return true;
            case TradeError.NotEnoughMoney: SendError(senderId, "NOT_ENOUGH_MONEY", "현금이 부족해요"); return true;
            case TradeError.NotOwner: SendError(senderId, "NOT_OWNER", "내 땅이 아니에요"); return true;
            case TradeError.CannotBuild: SendError(senderId, "CANNOT_BUILD", "건설할 수 없는 땅이에요"); return true;
            case TradeError.MaxLevel: SendError(senderId, "MAX_LEVEL", "이미 최고 단계예요"); return true;
            case TradeError.NotOthersProperty: SendError(senderId, "NOT_OTHERS_PROPERTY", "다른 사람의 땅이 아니에요"); return true;
            case TradeError.NotOnProperty: SendError(senderId, "NOT_ON_PROPERTY", "통행료를 낼 땅 위에 있지 않아요"); return true;
            case TradeError.DuplicateProperty: SendError(senderId, "DUPLICATE_PROPERTY", "같은 땅이 두 번 들어 있어요"); return true;
            case TradeError.SellNotOwner: SendError(senderId, "NOT_OWNER", "내 땅이 아닌 땅이 들어 있어요"); return true;
            case TradeError.SellNotEnough: SendError(senderId, "NOT_ENOUGH_MONEY", "판 돈을 합쳐도 통행료가 모자라요"); return true;
            default: SendError(senderId, "BAD_REQUEST", "요청을 처리하지 못했어요"); return true;
        }
    }

    // ───────────── 도착, 통행료, 파산 ─────────────

    // GameManager.ProcessArrival의 PROPERTY 부분
    // 땅에 도착했을 때 어떤 입력을 기다릴지는 규칙(TradeRules.ArriveAtProperty)이 정하고, 여기서는 그 결과대로 이어서 진행한다.
    private void ProcessPropertyArrival(PlayerState player, int propertyId)
    {
        PropertyArrival arrival = TradeRules.ArriveAtProperty(state, gameData, player, propertyId);

        switch (arrival.Kind)
        {
            case PropertyArrivalKind.MissingData:
                Debug.LogError($"[HostGameService] 땅 데이터 없음: propertyId {propertyId}");
                ProcessEndTurn();
                return;

            case PropertyArrivalKind.Purchase:
            case PropertyArrivalKind.Build:
                return; // 구매 또는 건설 요청을 기다린다
        }

        // 남의 땅
        Debug.Log($"[HostGameService] 남의 땅: {propertyId}번 (주인 {arrival.Owner.Nickname}, 통행료 {arrival.Toll})");

        switch (arrival.Kind)
        {
            case PropertyArrivalKind.TollPaidAcquire:
                Debug.Log($"[HostGameService] 통행료: {player.Nickname} → {arrival.Owner.Nickname} {arrival.Toll}");
                break; // 인수 요청을 기다린다

            case PropertyArrivalKind.TollPaidEnd:
                Debug.Log($"[HostGameService] 통행료: {player.Nickname} → {arrival.Owner.Nickname} {arrival.Toll}");
                ProcessEndTurn();
                break;

            case PropertyArrivalKind.Sell:
                // 현금이 모자라지만 땅을 팔면 낼 수 있다. 팔 땅을 고르는 단계로 들어간다.
                Debug.Log($"[HostGameService] 현금 부족: {player.Nickname} 땅을 팔아서 통행료를 내야 합니다.");
                break;

            case PropertyArrivalKind.Bankrupt:
                // 전부 팔아도 모자라면 파산한다. 더블이어도 다음 플레이어로 넘어간다.
                Debug.Log($"[HostGameService] 현금과 땅을 모두 팔아도 통행료를 낼 수 없습니다: {player.Nickname} 파산");
                Bankrupt(player, arrival.Owner);
                StartTurn(GetNextPlayerId());
                break;
        }
    }

    // GameManager.HandleBankruptcy, ProcessBankruptcy
    // 재산을 넘기고 파산 처리하는 일은 규칙(TradeRules.Bankrupt)이 하고, 여기서는 로그와 게임 종료를 처리한다.
    private void Bankrupt(PlayerState payer, PlayerState receiver)
    {
        BankruptResult result = TradeRules.Bankrupt(state, gameData, payer, receiver);
        if (result == BankruptResult.AlreadyBankrupt) return;

        Debug.Log($"[HostGameService] 파산: {payer.Nickname} (최종 {payer.FinalRank}위)");

        if (result == BankruptResult.LastPlayerLeft)
            EndGame("남은 플레이어가 1명입니다.");
    }
}