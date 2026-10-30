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
        if (player == null) return;

        // 최종 등수를 정하고 파산 표시를 하는 일은 규칙(TradeRules.MarkBankrupt)이 합니다. (이미 파산한 플레이어는 다시 처리하지 않습니다.)
        AfterBankrupt(player, TradeRules.MarkBankrupt(gameState, player));
    }

    // 파산 뒤처리: 화면 표시(GameScreen), 파산 이벤트, 남은 플레이어가 1명이면 게임 종료. GameState는 규칙이 이미 갱신했습니다.
    private void AfterBankrupt(PlayerState player, BankruptResult result)
    {
        if (result == BankruptResult.AlreadyBankrupt) return;

        screen.ShowBankrupt(player); // 파산 로그와 연출
        OnPlayerBankrupt?.Invoke(player.PlayerId);

        // 남은 플레이어 수가 1이라면 게임 종료 함수를 호출합니다. (팀전의 경우 조건이 바뀔 수 있음.)
        // 파산 연출 뒤에 판정해서, 결과 창이 파산 연출보다 먼저 뜨지 않게 합니다.
        if (result == BankruptResult.LastPlayerLeft)
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
            screen.ShowPurchasePropertyPopup(playerId, propertyId, isPurchasable);
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

        // GameState를 갱신합니다. (플레이어 현금 차감, 자산 주인 갱신, 새로 산 땅은 건물 없음) 규칙(TradeRules.Purchase)이 합니다.
        long amount = TradeRules.Purchase(gameData, player, property);
        screen.UpdatePropertyOwner(property);
        screen.UpdatePropertyBuilding(property);
        Log($"[구매] {P(playerId)}: {CityName(propertyId)} 구매 (-{Won(amount)}, 남은 현금 {Won(player.Money)})");

        // 자산 구매 연출을 재생합니다.
        OnPropertyPurchasedSuccessfully?.Invoke(playerId);
        screen.PlayMoneyChange(playerId, -amount, "땅 구매");

        // 턴을 종료합니다.
        ProcessEndTurn();
    }


    // ────────────────────────── 건물 건설 ──────────────────────────
    public void HandleBuildPrompt(long playerId, int propertyId, bool isBuildable)
    {
        // 자신의 차례인 경우에만 건설할 것인지 선택 가능한 UI를 표시합니다.
        if (IsLocalPlayerTurn())
        {
            screen.ShowBuildPopup(playerId, propertyId, isBuildable);
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

        // GameState를 갱신합니다. (플레이어 현금 차감, 건설 레벨 증가) 규칙(TradeRules.Build)이 합니다.
        long amount = TradeRules.Build(gameData, playerState, propertyState);
        screen.UpdatePropertyBuilding(propertyState);

        // 건설 연출을 재생합니다.
        OnBuildingBuiltSuccessfully?.Invoke(playerId);
        screen.PlayMoneyChange(playerId, -amount, "건설");

        // 턴을 종료합니다.
        ProcessEndTurn();
    }


    // ────────────────────────── 자산 인수 ──────────────────────────
    public void HandleAcquirePropertyPrompt(long playerId, int propertyId, long amount)
    {
        // 자신의 차례인 경우에만 인수할 것인지 선택 가능한 UI를 표시합니다.
        if (IsLocalPlayerTurn())
        {
            screen.ShowAcquirePropertyPopup(playerId, propertyId);
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

        var acquirer = GetPlayerState(playerId); //인수하는 사람
        var property = GetPropertyState(propertyId); //인수할 땅
        var owner = property != null && property.OwnerId.HasValue ? GetPlayerState(property.OwnerId.Value) : null; //인수당하는 사람
        if (acquirer == null || property == null || owner == null) return;

        // GameState를 갱신합니다. (인수자 현금 차감, 인수당하는 사람 현금 증가, 자산 주인 갱신) 규칙(TradeRules.Acquire)이 합니다.
        long amount = TradeRules.Acquire(gameData, acquirer, owner, property);
        screen.UpdatePropertyOwner(property); //소유권 이전

        // 인수 연출을 재생합니다.
        screen.PlayMoneyTransfer(playerId, owner.PlayerId, amount, "인수", "인수 대금");

        // 턴을 종료합니다.
        ProcessEndTurn();
    }


    // ────────────────────────── 자산 매각 ──────────────────────────
    public void HandleSellPropertiesPrompt(long payerId, long receiverId, long requiredAmount)
    {
        // 자신의 차례인 경우에만 청산할 자산들을 선택 가능한 UI를 표시합니다.
        if (IsLocalPlayerTurn())
        {
            screen.ShowSellPropertiesPopup(payerId, receiverId, requiredAmount);
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
        if (payer == null) return;

        // 통행료를 낼 땅 위에 있는지, 팔 땅이 모두 내 땅인지, 판 돈으로 통행료를 낼 수 있는지는 규칙(TradeRules.CheckSell)이 확인합니다.
        SellPlan plan = TradeRules.CheckSell(gameState, gameData, payer, propertyIds);
        if (plan.Error != TradeError.None)
        {
            Debug.LogWarning($"서버에서 승인한 자산 매각 처리 결과를 반영할 수 없습니다. ({plan.Error}) 판 돈 {plan.TotalAmount}, 지불해야할 돈 {plan.Toll}");
            return;
        }

        // 매각된 자산을 환원하고, 판 돈을 받아 통행료를 냅니다. 규칙(TradeRules.Sell)이 합니다.
        TradeRules.Sell(gameState, payer, plan, propertyIds);
        foreach (var id in propertyIds)
        {
            var property = GetPropertyState(id);
            if (property == null) continue;

            screen.UpdatePropertyBuilding(property); // 건설 레벨 초기화
            screen.UpdatePropertyOwner(property); // 주인 없는 땅으로
        }

        // 자산 청산 및 재화 이동 연출을 재생합니다.
        screen.PlayMoneyTransfer(payerId, plan.Receiver.PlayerId, plan.Toll, "통행료", "통행료 수입");

        // 턴을 종료합니다.
        ProcessEndTurn();
    }

    /// <summary>
    /// 통행료를 낼 수 없을 때 호출. 납부자의 현금 전부와 모든 땅(매각가로 현금화)을 수납자에게 주고 파산 처리한다.
    /// </summary>
    public void HandleBankruptcy(long payerId, long receiverId)
    {
        var payer = GetPlayerState(payerId); // 파산하는 사람
        var receiver = GetPlayerState(receiverId); // 통행료 받는 사람
        if (payer == null || receiver == null) return;

        // 재산이 정리되는 땅 목록을 미리 기억해 둡니다. (규칙이 상태를 바꾼 뒤 보드에 반영하려고)
        var ownedIds = gameState.PropertyStates.FindAll(p => p.OwnerId == payerId).ConvertAll(p => p.PropertyId);

        // GameState를 갱신합니다. (납부자 재산 현금화, 수납자 현금 증가, 납부자 파산) 규칙(TradeRules.Bankrupt)이 합니다.
        BankruptResult result = TradeRules.Bankrupt(gameState, gameData, payer, receiver);
        foreach (var id in ownedIds)
        {
            var property = GetPropertyState(id);
            if (property == null) continue;

            screen.UpdatePropertyOwner(property); // 주인 없는 땅으로
            screen.UpdatePropertyBuilding(property); // 건설 레벨 초기화
        }

        AfterBankrupt(payer, result); // 납부자 파산 처리 (파산 연출 → 게임 종료 판정)

        // 턴 넘기기는 ProcessArrival에서 처리 (HandleTurnChanged(GetNextPlayerId()) 호출)
    }
    // 통행료를 이체하고 연출을 재생합니다. 이체는 규칙(TradeRules.PayToll)이 합니다.
    // 땅 도착 처리에서는 규칙(TradeRules.ArriveAtProperty)이 이미 이체하므로 ShowTollPaid만 부릅니다. (개발용 패널은 이 함수를 직접 부릅니다)
    public void HandleTollPaid(long payerId, long receiverId, long amount)
    {
        var payer = GetPlayerState(payerId); // 통행료 내는 사람
        var receiver = GetPlayerState(receiverId); // 통행료 받는 사람
        if (payer == null || receiver == null) return;

        TradeRules.PayToll(payer, receiver, amount);
        screen.ShowTollPaid(payerId, receiverId, amount);
    }

}