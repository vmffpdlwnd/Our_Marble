using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// GameManager의 땅 거래(구매, 건설, 인수, 매각), 통행료, 파산. (나뉜 파일 설명은 GameManager.cs 참고)
/// </summary>
public partial class GameManager
{
    public void ProcessBankruptcy(long playerId)
    {
        // playerId에 해당하는 GameState.PlayerStates의 PlayerState.IsBankrupt 값을 true로 갱신합니다.
        var player = GetPlayerState(playerId);
        if (player == null || player.IsBankrupt) return; // 이미 파산한 플레이어는 다시 처리하지 않습니다.

        // 최종 등수를 정합니다. 파산하는 시점에 남아 있던 인원 수가 등수입니다. (예: 4명 중 첫 파산 → 4위)
        int aliveCount = 0;
        foreach (var p in gameState.PlayerStates)
        {
            if (!p.IsBankrupt) aliveCount++;
        }
        player.FinalRank = aliveCount;

        player.IsBankrupt = true;
        Log($"[파산] {P(playerId)}가 파산했습니다. (최종 {player.FinalRank}위)");

        // 파산 연출을 재생합니다. (GameState를 먼저 갱신한 뒤 부르므로 UI는 IsBankrupt를 그대로 읽으면 됨)
        if (UIManager.Instance != null)
            UIManager.Instance.PlayBankruptEffect(playerId);
        OnPlayerBankrupt?.Invoke(playerId);

        // 남은 플레이어 수가 1이라면 게임 종료 함수를 호출합니다. (팀전의 경우 조건이 바뀔 수 있음.)
        // 파산 연출 뒤에 판정해서, 결과 창이 파산 연출보다 먼저 뜨지 않게 합니다.
        int remaining = 0;
        foreach (var p in gameState.PlayerStates)
        {
            if (!p.IsBankrupt) remaining++;
        }
        if (remaining <= 1)
        {
            // 게임 종료 함수를 호출합니다.
            EndGame("남은 플레이어가 1명입니다.");
        }
    }


    // ────────────────────────── 토지 구매 ──────────────────────────
    public void HandlePurchasePropertyPrompt(long playerId, int propertyId, bool isPurchasable)
    {
        // 자신의 차례인 경우에만 선택 가능한 UI를 표시합니다.
        if (IsLocalPlayerTurn())
        {
            if (UIManager.Instance != null)
            {
                UIManager.Instance.ShowPurchasePropertyPopup(playerId, propertyId, isPurchasable);
            }
        }
        
        // broadcast메시지 수신 대기 상태에 들어갑니다.
        gameState.WaitingType = GameMessageType.PROPERTY_PURCHASED;
    }

    /// <summary>
    /// 화면의 '땅 사기' 버튼을 누르면 이 함수가 호출됩니다.
    /// </summary>
    public void PurchaseProperty(long playerId, int propertyId, bool isAccept = true)
    {
        sendAndWaiting = true;
        GameNetwork.Instance.SendPurchasePropertyRequest(propertyId, isAccept);
    }

    /// <summary>
    /// 화면의 '땅 사지 않기' 버튼을 누르면 이 함수가 호출됩니다.
    /// </summary>
    public void DeclinePropertyPurchase(long playerId, int propertyId) // 거절 함수를 분리한 이유는, chatGPT한테 물어본 결과 bool매개변수를 사용하여 수락/거절을 표현하기보다 함수 자체를 분리하는것을 추천했기 때문입니다.
    {
        PurchaseProperty(playerId, propertyId, false);
    }

    public void HandlePropertyPurchased(long playerId, int propertyId, bool isAccept)
    {
        HandleRoutine();
        
        if (!isAccept)
        {
            Log($"[구매 안 함] {P(playerId)}: {CityName(propertyId)}");
            ProcessEndTurn();
            return;
        }

        var player = GetPlayerState(playerId); //사는 사람
        var property = GetPropertyState(propertyId); //사는 땅
        if (player == null || property == null) return;

        long amount = PropertyManager.Instance.GetLandPrice(propertyId);

        // GameState를 갱신합니다. (플레이어 현금 차감, 자산 주인 갱신)
        player.Money -= amount;
        SetOwnerId(property, playerId);
        SetBuildingLevel(property, BuildingLevel.Land); // 새로 산 땅은 건물 없음
        Log($"[구매] {P(playerId)}: {CityName(propertyId)} 구매 (-{Won(amount)}, 남은 현금 {Won(player.Money)})");

        // 자산 구매 연출을 재생합니다.
        OnPropertyPurchasedSuccessfully?.Invoke(playerId);
        if (UIManager.Instance != null)
            UIManager.Instance.PlayMoneyChange(playerId, -amount, "땅 구매");

        // 턴을 종료합니다.
        ProcessEndTurn();
    }


    // ────────────────────────── 건물 건설 ──────────────────────────
    public void HandleBuildPrompt(long playerId, int propertyId, bool isBuildable)
    {
        // 자신의 차례인 경우에만 건설할 것인지 선택 가능한 UI를 표시합니다.
        if (IsLocalPlayerTurn())
        {
            if (UIManager.Instance != null)
            {
                UIManager.Instance.ShowBuildPopup(playerId, propertyId, isBuildable);
            }
        }
        
        // broadcast메시지 수신 대기 상태에 들어갑니다.
        gameState.WaitingType = GameMessageType.BUILT;
    }

    /// <summary>
    /// 화면의 '건설하기' 버튼을 누르면 이 함수가 호출됩니다.
    /// </summary>
    public void Build(long playerId, int propertyId, bool isAccept = true)
    {
        sendAndWaiting = true;
        GameNetwork.Instance.SendBuildRequest(propertyId, isAccept);
    }

    /// <summary>
    /// 화면의 '건설하지 않기' 버튼을 누르면 이 함수가 호출됩니다.
    /// </summary>
    public void DeclineBuild(long playerId, int propertyId)
    {
        Build(playerId, propertyId, false);
    }

    public void HandleBuilt(long playerId, int propertyId, bool isAccept)
    {
        HandleRoutine();
        
        if (!isAccept)
        {
            Log($"[건설 안 함] {P(playerId)}: {CityName(propertyId)}");
            ProcessEndTurn();
            return;
        }

        var playerState = GetPlayerState(playerId);
        var propertyState = GetPropertyState(propertyId);
        if (playerState == null || propertyState == null) return;

        var propertyData = PropertyManager.Instance.GetData(propertyId);

        long amount = propertyData.GetBuildCost((BuildingLevel)(propertyState.BuildingLevel + 1));

        // GameState를 갱신합니다. (플레이어 현금 차감, 건설 레벨 갱신)
        playerState.Money -= amount; //건설비 차감
        SetBuildingLevel(propertyState, propertyState.BuildingLevel + 1); // 건설 레벨 증가
        // 건설 연출을 재생합니다.
        OnBuildingBuiltSuccessfully?.Invoke(playerId);
        if (UIManager.Instance != null)
            UIManager.Instance.PlayMoneyChange(playerId, -amount, "건설");

        // 턴을 종료합니다.
        ProcessEndTurn();
    }


    // ────────────────────────── 자산 인수 ──────────────────────────
    public void HandleAcquirePropertyPrompt(long playerId, int propertyId, long amount)
    {
        // 자신의 차례인 경우에만 인수할 것인지 선택 가능한 UI를 표시합니다.
        if (IsLocalPlayerTurn())
        {
            if (UIManager.Instance != null)
            {
                UIManager.Instance.ShowAcquirePropertyPopup(playerId, propertyId);
            }
        }

        // broadcast메시지 수신 대기 상태에 들어갑니다.
        gameState.WaitingType = GameMessageType.PROPERTY_ACQUIRED;
    }

    /// <summary>
    /// 화면의 '인수하기' 버튼을 누르면 이 함수가 호출됩니다.
    /// </summary>
    public void AcquireProperty(long playerId, int propertyId, bool isAccept = true)
    {
        sendAndWaiting = true;
        GameNetwork.Instance.SendAcquirePropertyRequest(propertyId, isAccept);
    }

    /// <summary>
    /// 화면의 '인수하지 않기' 버튼을 누르면 이 함수가 호출됩니다.
    /// </summary>
    public void DeclineAcquireProperty(long playerId, int propertyId)
    {
        AcquireProperty(playerId, propertyId, false);
    }

    public void HandlePropertyAcquired(long playerId, int propertyId, bool isAccept)
    {
        HandleRoutine();
        
        if (!isAccept)
        {
            Log($"[인수 안 함] {P(playerId)}: {CityName(propertyId)}");
            ProcessEndTurn();
            return;
        }

        // GameState를 갱신합니다. (인수자 현금 차감, 인수당하는 사람 현금 증가, 자산 주인 갱신)
        var acquirer = GetPlayerState(playerId); //인수하는 사람
        var property = GetPropertyState(propertyId); //인수할 땅
        var owner = property != null && property.OwnerId.HasValue ? GetPlayerState(property.OwnerId.Value) : null; //인수당하는 사람
        if (acquirer == null || property == null || owner == null) return;

        long amount = property != null ? PropertyManager.Instance.GetAcquireValue(property) : 0; //인수가 조회

        acquirer.Money -= amount; //인수자 돈 차감
        owner.Money += amount; //소유자 돈 증가
        SetOwnerId(property, playerId); //소유권 이전

        // 인수 연출을 재생합니다.
        if (UIManager.Instance != null)
            UIManager.Instance.PlayMoneyTransfer(playerId, owner.PlayerId, amount, "인수", "인수 대금");

        // 턴을 종료합니다.
        ProcessEndTurn();
    }


    // ────────────────────────── 자산 매각 ──────────────────────────
    public void HandleSellPropertiesPrompt(long payerId, long receiverId, long requiredAmount)
    {
        // 자신의 차례인 경우에만 청산할 자산들을 선택 가능한 UI를 표시합니다.
        if (IsLocalPlayerTurn())
        {
            if (UIManager.Instance != null)
            {
                UIManager.Instance.ShowSellPropertiesPopup(payerId, receiverId, requiredAmount);
            }
        }

        // broadcast메시지 수신 대기 상태에 들어갑니다.
        gameState.WaitingType = GameMessageType.PROPERTIES_SOLD;
    }

    /// <summary>
    /// 화면의 '선택 완료' 버튼을 누르면 이 함수가 호출됩니다.
    /// </summary>
    public void SellProperties(long payerId, List<int> propertyIds)
    {
        sendAndWaiting = true;
        GameNetwork.Instance.SendSellPropertiesRequest(propertyIds);
    }

    public void HandlePropertiesSold(long payerId, List<int> propertyIds)
    {
        HandleRoutine();
        
        var payer = GetPlayerState(payerId);

        int landedPropertyId = BoardManager.Instance.GetTileData(payer.Position).PropertyId;
        PropertyData landedPropertyData = PropertyManager.Instance.GetData(landedPropertyId);
        PropertyState landedPropertyState = GetPropertyState(landedPropertyId);

        long receiverId = GetPropertyState(landedPropertyId).OwnerId.Value;

        var receiver = GetPlayerState(receiverId);
        if (payer == null || receiver == null) return;

        long toll = PropertyManager.Instance.GetToll(landedPropertyState);

        // 매각가 총합 계산
        long totalAmount = 0;
        foreach (var id in propertyIds)
        {
            var property = GetPropertyState(id);
            if (property == null || property.OwnerId != payerId) continue; // 소유자가 맞는지 확인

            totalAmount += PropertyManager.Instance.GetSellValue(property);
        }

        // 매각 결과가 유효한지 확인
        if (payer.Money + totalAmount < toll)
        {
            Debug.LogWarning($"서버에서 승인한 자산 매각 처리 결과 {payer}.Money + {totalAmount}가 지불해야할 돈 {toll}보다 부족합니다. ");
            return;
        }

        // 매각된 자산을 환원
        foreach (var id in propertyIds)
        {
            var property = GetPropertyState(id);
            if (property == null || property.OwnerId != payerId) continue; // 소유자가 맞는지 확인

            SetBuildingLevel(property, BuildingLevel.Land); // 건설 레벨 초기화
            SetOwnerId(property, null); // 주인 없는 땅으로
        }

        // PlayerState 갱신
        payer.Money = payer.Money + totalAmount - toll; // 판 돈 받고 통행료 냄
        receiver.Money += toll; //통행료 전액 지급

        // 자산 청산 및 재화 이동 연출을 재생합니다.
        if (UIManager.Instance != null)
            UIManager.Instance.PlayMoneyTransfer(payerId, receiverId, toll, "통행료", "통행료 수입");

        // 턴을 종료합니다.
        ProcessEndTurn();
    }

    /// <summary>
    /// 통행료를 낼 수 없을 때 호출. 납부자의 현금 전부와 모든 땅(매각가로 현금화)을 수납자에게 주고 파산 처리한다.
    /// </summary>
    public void HandleBankruptcy(long payerId, long receiverId)
    {
        // GameState를 갱신합니다. (납부자 재산 현금화, 수납자 현금 증가, 납부자 파산)
        var payer = GetPlayerState(payerId); // 파산하는 사람
        var receiver = GetPlayerState(receiverId); // 통행료 받는 사람
        if (payer == null || receiver == null) return;

        long liquidated = 0; //땅을 전부 판 금액
        foreach (var state in gameState.PropertyStates)
        {
            if (state.OwnerId != payerId) continue;

            liquidated += PropertyManager.Instance.GetSellValue(state); // 매각가 합산
            SetOwnerId(state, null); // 주인 없는 땅으로
            SetBuildingLevel(state, BuildingLevel.Land); // 건설 레벨 초기화
        }

        receiver.Money += payer.Money + liquidated; // 납부자 현금 전액 + 땅 매각가 합계
        payer.Money = 0; // 납부자 현금 0

        ProcessBankruptcy(payerId); // 납부자 파산 처리 (GameState 갱신 → 파산 연출 → 게임 종료 판정)

        // 턴 넘기기는 ProcessArrival에서 처리 (HandleTurnChanged(GetNextPlayerId()) 호출)
    }
    public void HandleTollPaid(long payerId, long receiverId, long amount)
    {
        // GameState를 갱신합니다.
        var payer = GetPlayerState(payerId); // 통행료 내는 사람
        var receiver = GetPlayerState(receiverId); // 통행료 받는 사람
        if (payer == null || receiver == null) return;

        payer.Money -= amount; // 통행료 차감
        receiver.Money += amount; // 통행료 수령

        // 재화 이동 연출을 재생합니다.
        if (UIManager.Instance != null)
            UIManager.Instance.PlayTollEffect(payerId, receiverId, amount);
    }

    private void SetOwnerId(PropertyState state, long? playerId)
    {
        state.OwnerId = playerId;

        int propertyId = state.PropertyId;
        BoardManager.Instance.UpdatePropertyTileColor(propertyId, playerId);
    }

    private void SetBuildingLevel(PropertyState state, BuildingLevel buildingLevel)
    {
        state.BuildingLevel = buildingLevel;

        int propertyId = state.PropertyId;
        BoardManager.Instance.UpdatePropertyBuildingVisual(propertyId, buildingLevel);
    }
}