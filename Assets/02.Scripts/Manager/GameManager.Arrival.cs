using UnityEngine;

/// <summary>
/// GameManager의 칸 도착 처리, 세무조사, 적립금. (나뉜 파일 설명은 GameManager.cs 참고)
/// </summary>
public partial class GameManager
{
    #region ProcessArrival
    private void ProcessArrival(long playerId, int toPosition)
    {
        PlayerState player = GetPlayerState(playerId);
        OnPlayerArrived?.Invoke(playerId, toPosition);

        Log($"[도착] {P(playerId)} → {TileName(toPosition)} (현금 {Won(player.Money)})");

        // 칸 종류별 처리(출발, 무인도, 세무조사, 적립금, 세계여행, 황금열쇠)는 규칙(ArrivalRules.ArriveAtTile)이 하고,
        // 규칙은 상태만 바꿉니다. 연출과 이어서 진행하는 일은 아래에서 합니다.
        TileArrival arrival = ArrivalRules.ArriveAtTile(gameState, gameData, player, toPosition);
        if (arrival.Kind == TileArrivalKind.MissingTile)
        {
            Debug.LogError($"[GameManager] 도착한 칸 {toPosition}의 데이터가 없습니다.");
            ProcessEndTurn();
            return;
        }

        TileData arrivalTile = arrival.Tile;

        switch (arrival.Kind)
        {
            case TileArrivalKind.EndTurn: // 시작지점에 도착
                Log("  출발 칸: 특별한 효과 없음");
                ProcessEndTurn(); // 턴을 종료합니다. 
                break;

            case TileArrivalKind.NextTurn: // 무인도(영업정지 설정은 규칙이 함) 또는 자유여행에 도착
                // 강제로 턴을 넘깁니다.
                Log(arrivalTile.Type == TileType.ISLAND ? "  무인도: 턴이 넘어갑니다." : "  세계여행 칸: 턴이 넘어갑니다.");
                HandleTurnChanged(GetNextPlayerId());
                break;

            case TileArrivalKind.Welfare: // 기부금수령(푸드 페스티벌)에 도착
                HandleWelfareFundReceived(playerId, arrival.MoneyBefore, arrival.Amount); // 쌓인 적립금을 모두 받았습니다. (규칙이 반영)
                ProcessEndTurn();
                break;

            case TileArrivalKind.Donation: // 기부금납부(세무조사)에 도착
                HandleDonationPaid(playerId, arrival.MoneyBefore, arrival.Amount); // 벌금을 내고 적립금에 쌓았습니다. (규칙이 반영)
                ProcessEndTurn();
                break;

            case TileArrivalKind.WaitCard: // 황금열쇠에 도착
                // 황금 열쇠 카드 드로우 (턴 처리는 각 CardEffect 실행 메서드에서 진행하므로 ProcessEndTurn 호출하지 않음)
                Log("  황금열쇠 카드를 뽑습니다.");
                DrawCard();
                break;

            case TileArrivalKind.Property: // 자산 유형의 타일에 도착
                int propertyId = arrivalTile.PropertyId;
                PropertyState propertyState = GetPropertyState(propertyId);

                // 땅에 도착했을 때 어떤 입력을 기다릴지, 남의 땅이면 통행료 이체까지는 규칙(TradeRules.ArriveAtProperty)이 합니다.
                // 규칙은 상태만 바꾸고, 로그와 선택창, 연출은 결과(Kind)대로 아래에서 합니다.
                PropertyArrival propertyArrival = TradeRules.ArriveAtProperty(gameState, gameData, player, propertyId);

                switch (propertyArrival.Kind)
                {
                    case PropertyArrivalKind.MissingData:
                        Debug.LogError($"[GameManager] 땅 데이터 없음: propertyId {propertyId}");
                        ProcessEndTurn();
                        break;

                    case PropertyArrivalKind.Purchase: // 주인 없는 땅인 경우
                    {
                        long landPrice = gameData.Properties.GetLandPrice(propertyState.PropertyId);
                        bool canAffordToPurchase = TradeRules.CheckPurchase(gameData, player, propertyState, true) == TradeError.None;
                        if (canAffordToPurchase) // 땅 구매할 돈이 충분하면
                        {
                            Log($"  빈 땅: {CityName(propertyId)} 구매 가능 (땅값 {Won(landPrice)})");
                            HandlePurchasePropertyPrompt(playerId, propertyId, true);
                        }
                        else // 땅 구매할 돈이 충분하지 않으면
                        {
                            Log($"  빈 땅: {CityName(propertyId)} 구매 불가능 (땅값 {Won(landPrice)})");
                            HandlePurchasePropertyPrompt(playerId, propertyId, false);
                        }
                        break;
                    }

                    case PropertyArrivalKind.Build: // 본인 소유의 땅인 경우
                    {
                        BuildingLevel currentLevel = propertyState.BuildingLevel;
                        TradeError buildError = TradeRules.CheckBuild(gameData, player, propertyState, true);
                        switch (buildError)
                        {
                            // 건설 불가 자산 Case 처리
                            case TradeError.CannotBuild:
                                Log($"  내 땅: {CityName(propertyId)} 현재 BuildingLevel : {BuildingLevel.Land} / 건설 불가능 - 건설 불가 칸");
                                HandleBuildPrompt(playerId, propertyId, false);
                                break;

                            // 이미 최고 레벨인 Case 처리
                            case TradeError.MaxLevel:
                                Log($"  내 땅: {CityName(propertyId)} 현재 BuildingLevel : {currentLevel} / 건설 불가능 - 이미 최고 단계");
                                HandleBuildPrompt(playerId, propertyId, false);
                                break;

                            // 돈이 부족한 Case 처리
                            case TradeError.NotEnoughMoney:
                                Log($"  내 땅: {CityName(propertyId)} 현재 BuildingLevel : {currentLevel}) / 건설 불가능 - 현금 부족 (건설 비용 : {Won(gameData.Properties.GetBuildCost(propertyId, (BuildingLevel)(currentLevel + 1)))})");
                                HandleBuildPrompt(playerId, propertyId, false);
                                break;

                            // 건설 가능한 Case 처리
                            default:
                                Log($"  내 땅: {CityName(propertyId)} 현재 BuildingLevel : {currentLevel}) 건설 가능 (비용 {Won(gameData.Properties.GetBuildCost(propertyId, (BuildingLevel)(currentLevel + 1)))})");
                                HandleBuildPrompt(playerId, propertyId, true);
                                break;
                        }
                        break;
                    }

                    default: // 타인 소유의 땅인 경우 (통행료는 규칙이 이미 이체했습니다)
                    {
                        long ownerId = propertyArrival.Owner.PlayerId;
                        long toll = propertyArrival.Toll;
                        Log($"  남의 땅: {CityName(propertyId)} (주인 {P(ownerId)}, 통행료 {Won(toll)})");

                        switch (propertyArrival.Kind)
                        {
                            case PropertyArrivalKind.TollPaidAcquire: // 통행료 납부하고도 인수할 돈이 있다면
                            {
                                ShowTollPaid(playerId, ownerId, toll); // 통행료 이동 연출을 재생합니다.

                                long acquireValue = gameData.Properties.GetAcquireValue(propertyState);
                                Log($"  인수 가능 (인수가 {Won(acquireValue)})");
                                HandleAcquirePropertyPrompt(playerId, propertyId, acquireValue); // 인수 선택지 UI를 표시합니다.
                                break;
                            }

                            case PropertyArrivalKind.TollPaidEnd: // 통행료는 냈지만 인수할 돈은 없다면
                            {
                                ShowTollPaid(playerId, ownerId, toll); // 통행료 이동 연출을 재생합니다.

                                long acquireValue = gameData.Properties.GetAcquireValue(propertyState);
                                Log($"  인수 불가 (인수가 {Won(acquireValue)}, 현금 {Won(player.Money)})");
                                ProcessEndTurn();
                                break;
                            }

                            case PropertyArrivalKind.Sell: // 통행료 납부 불가지만, 자산을 팔면 납부
                                Log($"  현금 부족: 자산을 팔아서 통행료를 내야 합니다.");
                                HandleSellPropertiesPrompt(playerId, ownerId, toll); // 자산 매각 선택지 UI를 표시합니다.
                                break;

                            case PropertyArrivalKind.Bankrupt: // 전부 팔아도 모자라면 파산
                                Log($"  현금과 자산을 모두 팔아도 통행료를 낼 수 없습니다. 파산!");
                                HandleBankruptcy(playerId, ownerId); // 재산 현금화 -> 수납자 지급 -> 파산 처리. GameState를 갱신합니다.
                                HandleTurnChanged(GetNextPlayerId()); // 강제로 턴을 넘깁니다.
                                break;
                        }
                        break;
                    }
                }
                break;
        }
        
    }
    #endregion

    // 세무조사 결과를 화면에 보여줍니다. 현금 감소와 적립금 증가는 규칙(ArrivalRules)이 이미 반영했습니다.
    // moneyBefore: 도착한 순간의 현금, paid: 실제로 낸 금액 (현금 한도 안에서)
    public void HandleDonationPaid(long playerId, long moneyBefore, long paid)
    {
        PlayerState player = GetPlayerState(playerId);
        if (player == null) return;

        EconomyManager.NotifyMoneyChanged(playerId, moneyBefore, player.Money);
        Log($"  [세무조사] {P(playerId)}: 벌금 {Won(gameState.Config.taxAmount)} 중 {Won(paid)} 납부 (현금 {Won(player.Money)}, 적립금 {Won(gameState.WelfareFund)})");

        // 재화 손실 연출을 재생합니다.
        SpecialTileManager.Instance.PlayTaxPaid(playerId, paid);
    }

    // 적립금 수령 결과를 화면에 보여줍니다. 현금 증가와 적립금 감소는 규칙(ArrivalRules)이 이미 반영했습니다.
    // moneyBefore: 도착한 순간의 현금, amount: 받은 적립금 (0이면 받을 것이 없었음)
    public void HandleWelfareFundReceived(long playerId, long moneyBefore, long amount)
    {
        PlayerState player = GetPlayerState(playerId);
        if (player == null) return;

        if (amount <= 0)
        {
            Log($"  [푸드 페스티벌] {P(playerId)}: 쌓인 적립금이 없습니다.");
            return;
        }

        EconomyManager.NotifyMoneyChanged(playerId, moneyBefore, player.Money);
        Log($"  [푸드 페스티벌] {P(playerId)}: 적립금 {Won(amount)} 획득 (현금 {Won(player.Money)})");

        // 재화 획득 연출을 재생합니다.
        SpecialTileManager.Instance.PlayWelfareFundReceived(playerId, amount);
    }
}