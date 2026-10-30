using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 게임 화면에 보여 주는 일을 맡는 클래스. GameManager는 규칙(Rules/)으로 게임 상태를 맞춘 뒤, 화면에 보여 주는 일을 여기에 맡긴다.
///
///   결과 표시   세무조사, 적립금, 통행료, 파산처럼 돈 변화 알림, 로그, 효과를 한꺼번에 보여 주는 일 (코드가 여기에 있다)
///   로그        로그 문구와 이름 표시 도우미 (코드가 여기에 있다)
///   화면 호출   팝업 띄우기, 보드 색과 건물 모양 갱신, 말 이동 같은 화면 매니저 호출의 창구
///
/// 화면 매니저(UIManager, PlayerManager, BoardManager, SpecialTileManager, EconomyManager)의 같은 이름 함수를 그대로 부른다.
/// 화면 객체가 없을 때를 대비한 null 확인은 GameManager가 이전에 하던 그대로 유지했다. (확인하던 곳은 확인하고, 안 하던 곳은 하지 않는다)
/// </summary>
public class GameScreen
{
    private GameState state;
    private GameData data;

    /// <summary>게임 상태와 데이터를 연결한다. 게임 상태를 동기화할 때마다 부른다.</summary>
    public void Bind(GameState gameState, GameData gameData)
    {
        state = gameState;
        data = gameData;
    }

    // ───────────── 로그와 표시 이름 ─────────────

    public void Log(string message)
    {
        Debug.Log($"[Game] {message}");
    }

    // 로그용 이름: "1P[123]", "2P(봇)[456]"처럼 순서와 봇 여부를 보여줍니다.
    public string P(long playerId)
    {
        int order = state.PlayerOrder.IndexOf(playerId) + 1;
        string nickname = state.PlayerStates[order - 1].Nickname;
        return $"[{order}P]{nickname}";
    }

    public string Won(long amount)
    {
        return $"{amount:N0}원";
    }

    public string CityName(int propertyId)
    {
        PropertyData propertyData = PropertyManager.Instance.GetData(propertyId);
        return propertyData != null ? propertyData.CityName : $"땅{propertyId}";
    }

    public string TileName(int position)
    {
        TileData tile = BoardManager.Instance.GetTileData(position);
        if (tile == null) return $"{position}번";

        if (tile.Type == TileType.PROPERTY)
            return $"{CityName(tile.PropertyId)}({position}번)";

        return $"{tile.Type}({position}번)";
    }

    // 최종 순위를 FinalRank 순으로 로그에 남깁니다.
    public void LogRanking()
    {
        var ranked = new List<PlayerState>(state.PlayerStates);
        ranked.Sort((a, b) => a.FinalRank.CompareTo(b.FinalRank));
        foreach (PlayerState player in ranked)
        {
            long totalAsset = data.Properties.GetTotalAsset(player.PlayerId, player.Money, state.PropertyStates);
            string result = player.IsBankrupt ? "파산" : (player.FinalRank == 1 ? "승리" : "패배");
            Log($"  {player.FinalRank}위 {P(player.PlayerId)}: 총자산 {Won(totalAsset)} (현금 {Won(player.Money)}) - {result}");
        }
    }

    // ───────────── 결과 표시 ─────────────

    // 세무조사 결과를 화면에 보여줍니다. 현금 감소와 적립금 증가는 규칙(ArrivalRules)이 이미 반영했습니다.
    // moneyBefore: 도착한 순간의 현금, paid: 실제로 낸 금액 (현금 한도 안에서)
    public void ShowDonationPaid(PlayerState player, long moneyBefore, long paid)
    {
        long playerId = player.PlayerId;

        EconomyManager.NotifyMoneyChanged(playerId, moneyBefore, player.Money);
        Log($"  [세무조사] {P(playerId)}: 벌금 {Won(state.Config.taxAmount)} 중 {Won(paid)} 납부 (현금 {Won(player.Money)}, 적립금 {Won(state.WelfareFund)})");

        // 재화 손실 연출을 재생합니다.
        SpecialTileManager.Instance.PlayTaxPaid(playerId, paid);
    }

    // 적립금 수령 결과를 화면에 보여줍니다. 현금 증가와 적립금 감소는 규칙(ArrivalRules)이 이미 반영했습니다.
    // moneyBefore: 도착한 순간의 현금, amount: 받은 적립금 (0이면 받을 것이 없었음)
    public void ShowWelfareFundReceived(PlayerState player, long moneyBefore, long amount)
    {
        long playerId = player.PlayerId;

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

    // 통행료 이동 연출을 재생합니다.
    public void ShowTollPaid(long payerId, long receiverId, long amount)
    {
        if (UIManager.Instance != null)
            UIManager.Instance.PlayTollEffect(payerId, receiverId, amount);
    }

    // 파산을 화면에 보여줍니다. GameState는 규칙이 이미 갱신했습니다.
    public void ShowBankrupt(PlayerState player)
    {
        Log($"[파산] {P(player.PlayerId)}가 파산했습니다. (최종 {player.FinalRank}위)");

        // 파산 연출을 재생합니다. (GameState를 먼저 갱신한 뒤 부르므로 UI는 IsBankrupt를 그대로 읽으면 됨)
        if (UIManager.Instance != null)
            UIManager.Instance.PlayBankruptEffect(player.PlayerId);
    }

    // 규칙이 바꾼 땅 주인을 보드의 땅 색에 반영합니다.
    public void UpdatePropertyOwner(PropertyState property)
    {
        BoardManager.Instance.UpdatePropertyTileColor(property.PropertyId, property.OwnerId);
    }

    // 규칙이 바꾼 건물 단계를 보드의 건물 모양에 반영합니다.
    public void UpdatePropertyBuilding(PropertyState property)
    {
        BoardManager.Instance.UpdatePropertyBuildingVisual(property.PropertyId, property.BuildingLevel);
    }

    // ───────────── 화면 초기화 ─────────────

    /// <summary>
    /// 게임 상태에 맞춰 화면을 채운다. 이 화면을 조작하는 사람 플레이어를 지정하고, 플레이어 정보 카드, 보드, 말을 GameState에 맞게 갱신한다.
    /// </summary>
    public void InitializeScreen(long localPlayerId, int maxRound)
    {
        if (UIManager.Instance != null)
        {
            UIManager.Instance.SetLocalPlayer(localPlayerId);
            UIManager.Instance.SetMaxRound(maxRound); // 최대 라운드는 GameManager가 관리하고 UI에 알려줍니다.
            UIManager.Instance.InitPlayers();

            // UI 리프레시 함수 호출
            UIManager.Instance.RefreshUIFromGameState();
        }

        // Board 리프레시 함수 호출
        BoardManager.Instance.RefreshBoard();

        PlayerManager.Instance.Initialize(state.PlayerStates.ConvertAll(playerState => playerState.PlayerId));
        // PlayerPawn 리프레시 함수 호출
        PlayerManager.Instance.RefreshPawns(state.PlayerStates);
    }

    // ───────────── 턴 ─────────────

    /// <summary>'OO의 턴'이라는 UI를 표시한다.</summary>
    public void ShowTurn(long playerId)
    {
        if (UIManager.Instance != null)
            UIManager.Instance.ShowTurn(playerId);
    }

    /// <summary>말에 차례 강조를 준다.</summary>
    public void SetCurrentTurn(long playerId)
    {
        PlayerManager.Instance.SetCurrentTurn(playerId);
    }

    // ───────────── 선택창 ─────────────

    public void ShowRollDicePopup()
    {
        if (UIManager.Instance != null)
            UIManager.Instance.ShowRollDicePopup();
    }

    public void ShowChooseDestinationPopup()
    {
        if (UIManager.Instance != null)
            UIManager.Instance.ShowChooseDestinationPopup();
    }

    public void ShowPurchasePropertyPopup(long playerId, int propertyId, bool isPurchasable)
    {
        if (UIManager.Instance != null)
            UIManager.Instance.ShowPurchasePropertyPopup(playerId, propertyId, isPurchasable);
    }

    public void ShowBuildPopup(long playerId, int propertyId, bool isBuildable)
    {
        if (UIManager.Instance != null)
            UIManager.Instance.ShowBuildPopup(playerId, propertyId, isBuildable);
    }

    public void ShowAcquirePropertyPopup(long playerId, int propertyId)
    {
        if (UIManager.Instance != null)
            UIManager.Instance.ShowAcquirePropertyPopup(playerId, propertyId);
    }

    public void ShowSellPropertiesPopup(long payerId, long receiverId, long requiredAmount)
    {
        if (UIManager.Instance != null)
            UIManager.Instance.ShowSellPropertiesPopup(payerId, receiverId, requiredAmount);
    }

    /// <summary>주사위 결과를 보여 준다. 연출이 끝나면 onCompleted를 부른다. 화면이 없으면 보여 주지 않고 false를 돌려준다. (이때 onCompleted는 부르지 않는다)</summary>
    public bool ShowDiceResult(int dice1, int dice2, Action onCompleted)
    {
        if (UIManager.Instance == null) return false;

        UIManager.Instance.ShowDiceResult(dice1, dice2, onCompleted);
        return true;
    }

    /// <summary>황금열쇠 카드를 뽑는 연출. 확인하면(또는 자동으로 닫히면) onConfirm을 부른다. 화면이 없으면 오류가 난다. (이전과 같음)</summary>
    public void ShowCardDrawPopup(long playerId, int cardId, bool forceAutoClose, Action onConfirm)
    {
        UIManager.Instance.ShowCardDrawPopup(playerId, cardId, forceAutoClose, onConfirm);
    }

    /// <summary>게임 결과 창을 띄운다. (총자산 순으로 순위 표시)</summary>
    public void ShowGameResultPopup(Action onClose)
    {
        if (UIManager.Instance != null)
            UIManager.Instance.ShowGameResultPopup(onClose);
    }

    /// <summary>떠 있는 선택창을 닫는다. 화면이 없으면 오류가 난다. (이전과 같음)</summary>
    public void CloseSelectionPopups()
    {
        UIManager.Instance.CloseSelectionPopups();
    }

    /// <summary>굴리기 버튼을 잠근다. 화면이 없으면 오류가 난다. (이전과 같음)</summary>
    public void LockDiceRoll()
    {
        UIManager.Instance.LockDiceRoll();
    }

    /// <summary>결과를 기다리며 구르던 주사위를 멈춘다.</summary>
    public void CancelDiceRolling()
    {
        if (UIManager.Instance != null)
            UIManager.Instance.CancelDiceRolling();
    }

    // ───────────── 돈과 효과 ─────────────

    /// <summary>플레이어의 현금이 바뀐 것을 화면에 알린다.</summary>
    public void NotifyMoneyChanged(long playerId, long before, long after)
    {
        EconomyManager.NotifyMoneyChanged(playerId, before, after);
    }

    public void PlayMoneyChange(long playerId, long amount, string reason)
    {
        if (UIManager.Instance != null)
            UIManager.Instance.PlayMoneyChange(playerId, amount, reason);
    }

    public void PlayMoneyTransfer(long payerId, long receiverId, long amount, string payerReason, string receiverReason)
    {
        if (UIManager.Instance != null)
            UIManager.Instance.PlayMoneyTransfer(payerId, receiverId, amount, payerReason, receiverReason);
    }

    public void PlayIslandEscapeFailed(long playerId, int remainingTurns)
    {
        SpecialTileManager.Instance.PlayIslandEscapeFailed(playerId, remainingTurns);
    }

    public void PlayIslandEscaped(long playerId)
    {
        SpecialTileManager.Instance.PlayIslandEscaped(playerId);
    }

    // ───────────── 말 이동 ─────────────

    /// <summary>fromIndex에서 steps칸 앞으로 이동하는 연출. 끝나면 onCompleted를 부른다.</summary>
    public void MoveBySteps(long playerId, int fromIndex, int steps, Action<int> onTileReached, Action onCompleted)
    {
        PlayerManager.Instance.MoveBySteps(playerId, fromIndex, steps, onTileReached, onCompleted);
    }

    /// <summary>fromIndex에서 toIndex까지 타일을 따라 이동하는 연출. 끝나면 onCompleted를 부른다.</summary>
    public void MoveAlongTiles(long playerId, int fromIndex, int toIndex, Action<int> onTileReached, Action onCompleted)
    {
        PlayerManager.Instance.MoveAlongTiles(playerId, fromIndex, toIndex, onTileReached, onCompleted);
    }

    /// <summary>tileIndex로 바로 이동하는 연출. 끝나면 onCompleted를 부른다.</summary>
    public void MoveToTile(long playerId, int tileIndex, Action onCompleted)
    {
        PlayerManager.Instance.MoveToTile(playerId, tileIndex, onCompleted);
    }
}