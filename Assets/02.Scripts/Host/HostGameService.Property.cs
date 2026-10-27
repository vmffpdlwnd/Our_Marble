using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// HostGameService의 땅과 경제 규칙: 구매, 건설, 인수, 매각, 통행료, 파산. (나뉜 파일 설명은 HostGameService.cs 참고)
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
        long price = PropertyManager.Instance.GetLandPrice(request.propertyId);

        // 8. 기능별 검증. 거절은 확인할 것이 없다. (살 돈이 없는 사람은 거절만 보낼 수 있다)
        if (request.isAccept)
        {
            if (property.OwnerId != null) { SendError(senderId, "ALREADY_OWNED", "이미 주인이 있는 땅이에요"); return; }
            if (player.Money < price) { SendError(senderId, "NOT_ENOUGH_MONEY", "현금이 부족해요"); return; }
        }

        state.WaitingType = null;
        Broadcast(GameMessageType.PROPERTY_PURCHASED,
            new PropertyPurchasedBroadcast { playerId = senderId, propertyId = request.propertyId, isAccept = request.isAccept });

        if (request.isAccept)
        {
            player.Money -= price;
            property.OwnerId = senderId;
            property.BuildingLevel = BuildingLevel.Land; // 새로 산 땅은 건물 없음
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
        long cost = 0;

        // 8. 기능별 검증. 거절은 확인할 것이 없다. (건설할 수 없는 땅에서도 거절은 보낼 수 있다)
        if (request.isAccept)
        {
            PropertyData data = PropertyManager.Instance.GetData(request.propertyId);

            if (property.OwnerId != senderId) { SendError(senderId, "NOT_OWNER", "내 땅이 아니에요"); return; }
            if (data == null || !data.CanBuild) { SendError(senderId, "CANNOT_BUILD", "건설할 수 없는 땅이에요"); return; }
            if (property.BuildingLevel >= BuildingLevel.Hotel) { SendError(senderId, "MAX_LEVEL", "이미 최고 단계예요"); return; }

            cost = PropertyManager.Instance.GetBuildCost(request.propertyId, property.BuildingLevel + 1);
            if (player.Money < cost) { SendError(senderId, "NOT_ENOUGH_MONEY", "현금이 부족해요"); return; }
        }

        state.WaitingType = null;
        Broadcast(GameMessageType.BUILT,
            new BuiltBroadcast { playerId = senderId, propertyId = request.propertyId, isAccept = request.isAccept });

        if (request.isAccept)
        {
            player.Money -= cost;
            property.BuildingLevel = property.BuildingLevel + 1;
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
        PlayerState owner = null;
        long amount = 0;

        // 8. 기능별 검증. 거절은 확인할 것이 없다.
        if (request.isAccept)
        {
            owner = property.OwnerId.HasValue ? GetPlayer(property.OwnerId.Value) : null;
            if (owner == null || owner.PlayerId == senderId) { SendError(senderId, "NOT_OTHERS_PROPERTY", "다른 사람의 땅이 아니에요"); return; }

            amount = PropertyManager.Instance.GetAcquireValue(property);
            if (player.Money < amount) { SendError(senderId, "NOT_ENOUGH_MONEY", "현금이 부족해요"); return; }
        }

        state.WaitingType = null;
        Broadcast(GameMessageType.PROPERTY_ACQUIRED,
            new PropertyAcquiredBroadcast { playerId = senderId, propertyId = request.propertyId, isAccept = request.isAccept });

        if (request.isAccept)
        {
            player.Money -= amount;   // 인수자 현금 차감
            owner.Money += amount;    // 원래 주인 현금 증가
            property.OwnerId = senderId; // 주인만 바뀌고 건물 단계는 그대로다
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

        // 7. 통행료를 낼 땅 위에 있어야 한다. 그 땅의 주인이 통행료를 받는다.
        TileData tile = BoardManager.Instance.GetTileData(player.Position);
        PropertyState landed = tile != null && tile.Type == TileType.PROPERTY ? GetProperty(tile.PropertyId) : null;
        if (landed == null || !landed.OwnerId.HasValue || landed.OwnerId.Value == senderId)
        {
            SendError(senderId, "NOT_ON_PROPERTY", "통행료를 낼 땅 위에 있지 않아요");
            return;
        }

        PlayerState receiver = GetPlayer(landed.OwnerId.Value);
        long toll = PropertyManager.Instance.GetToll(landed);

        // 8. 기능별 검증: 같은 땅을 두 번 팔 수 없고, 모두 내 땅이어야 하며, 판 돈을 합쳐 통행료를 낼 수 있어야 한다
        if (propertyIds.Distinct().Count() != propertyIds.Count) { SendError(senderId, "DUPLICATE_PROPERTY", "같은 땅이 두 번 들어 있어요"); return; }

        long totalAmount = 0;
        foreach (int id in propertyIds)
        {
            PropertyState property = GetProperty(id);
            if (property.OwnerId != senderId) { SendError(senderId, "NOT_OWNER", "내 땅이 아닌 땅이 들어 있어요"); return; }
            totalAmount += PropertyManager.Instance.GetSellValue(property);
        }

        if (player.Money + totalAmount < toll) { SendError(senderId, "NOT_ENOUGH_MONEY", "판 돈을 합쳐도 통행료가 모자라요"); return; }

        state.WaitingType = null;
        Broadcast(GameMessageType.PROPERTIES_SOLD, new PropertiesSoldBroadcast { playerId = senderId, propertyIds = propertyIds });

        // 판 땅은 주인 없음 + 땅 단계로 돌아가고, 판 돈을 받아 통행료를 낸다
        foreach (int id in propertyIds)
        {
            PropertyState property = GetProperty(id);
            property.BuildingLevel = BuildingLevel.Land;
            property.OwnerId = null;
        }

        player.Money = player.Money + totalAmount - toll;
        receiver.Money += toll;
        Debug.Log($"[HostGameService] 매각: {player.Nickname} 땅 {propertyIds.Count}개 (+{totalAmount}), 통행료 {toll} 납부 (현금 {player.Money})");

        ProcessEndTurn();
    }

    // ───────────── 도착, 통행료, 파산 ─────────────

    // GameManager.ProcessArrival의 PROPERTY 부분
    // 땅에 도착했을 때 어떤 입력을 기다릴지 정한다.
    private void ProcessPropertyArrival(PlayerState player, int propertyId)
    {
        PropertyState property = GetProperty(propertyId);
        PropertyData data = PropertyManager.Instance.GetData(propertyId);

        if (property == null || data == null)
        {
            Debug.LogError($"[HostGameService] 땅 데이터 없음: propertyId {propertyId}");
            ProcessEndTurn();
            return;
        }

        if (property.OwnerId == null)
        {
            // 살 돈이 없어도 구매 단계로 들어간다. (선택창에서 거절만 보낼 수 있다)
            state.WaitingType = GameMessageType.PROPERTY_PURCHASED;
        }
        else if (property.OwnerId.Value == player.PlayerId)
        {
            // 건설할 수 없는 땅이어도 건설 단계로 들어간다. (선택창에서 거절만 보낼 수 있다)
            state.WaitingType = GameMessageType.BUILT;
        }
        else
        {
            // 남의 땅: 통행료를 낸다
            PlayerState owner = GetPlayer(property.OwnerId.Value);
            long toll = PropertyManager.Instance.GetToll(property);
            Debug.Log($"[HostGameService] 남의 땅: {propertyId}번 (주인 {owner.Nickname}, 통행료 {toll})");

            if (player.Money >= toll)
            {
                PayToll(player, owner, toll);

                // 통행료를 내고도 인수할 돈이 있으면 인수를 선택할 수 있다
                long acquireValue = PropertyManager.Instance.GetAcquireValue(property);
                if (player.Money >= acquireValue)
                    state.WaitingType = GameMessageType.PROPERTY_ACQUIRED;
                else
                    ProcessEndTurn();
            }
            else if (player.Money + PropertyManager.Instance.GetTotalSellValue(player.PlayerId, state.PropertyStates) >= toll)
            {
                // 현금이 모자라지만 땅을 팔면 낼 수 있다. 팔 땅을 고르는 단계로 들어간다.
                Debug.Log($"[HostGameService] 현금 부족: {player.Nickname} 땅을 팔아서 통행료를 내야 합니다.");
                state.WaitingType = GameMessageType.PROPERTIES_SOLD;
            }
            else
            {
                // 전부 팔아도 모자라면 파산한다. 더블이어도 다음 플레이어로 넘어간다.
                Debug.Log($"[HostGameService] 현금과 땅을 모두 팔아도 통행료를 낼 수 없습니다: {player.Nickname} 파산");
                Bankrupt(player, owner);
                StartTurn(GetNextPlayerId());
            }
        }
    }

    // GameManager.HandleTollPaid (통행료는 알림 없이 양쪽이 같은 규칙으로 반영한다)
    private void PayToll(PlayerState payer, PlayerState receiver, long amount)
    {
        payer.Money -= amount;
        receiver.Money += amount;
        Debug.Log($"[HostGameService] 통행료: {payer.Nickname} → {receiver.Nickname} {amount}");
    }

    // GameManager.HandleBankruptcy
    // 파산: 가진 땅을 매각가로 현금화하고, 현금과 함께 통행료를 받을 사람에게 전부 넘긴다. 땅은 주인 없음 + 땅 단계로 돌아간다.
    private void Bankrupt(PlayerState payer, PlayerState receiver)
    {
        long liquidated = 0;
        foreach (PropertyState property in state.PropertyStates)
        {
            if (property.OwnerId != payer.PlayerId) continue;

            liquidated += PropertyManager.Instance.GetSellValue(property);
            property.OwnerId = null;
            property.BuildingLevel = BuildingLevel.Land;
        }

        receiver.Money += payer.Money + liquidated;
        payer.Money = 0;

        ProcessBankruptcy(payer);
    }

    // GameManager.ProcessBankruptcy
    private void ProcessBankruptcy(PlayerState player)
    {
        if (player.IsBankrupt) return; // 이미 파산한 플레이어는 다시 처리하지 않는다

        // 최종 등수는 파산하는 시점에 남아 있던 인원 수다. (예: 4명 중 첫 파산은 4위)
        player.FinalRank = state.PlayerStates.Count(p => !p.IsBankrupt);
        player.IsBankrupt = true;
        Debug.Log($"[HostGameService] 파산: {player.Nickname} (최종 {player.FinalRank}위)");

        if (state.PlayerStates.Count(p => !p.IsBankrupt) <= 1)
            EndGame("남은 플레이어가 1명입니다.");
    }
}