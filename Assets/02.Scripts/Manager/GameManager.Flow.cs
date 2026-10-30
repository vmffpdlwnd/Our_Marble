using UnityEngine;

/// <summary>
/// GameManager의 턴, 주사위, 이동, 월급, 무인도, 게임 종료, 세계여행. (나뉜 파일 설명은 GameManager.cs 참고)
/// </summary>
public partial class GameManager
{
    // 파산하지 않은 플레이어가 1명 이하면 게임 종료
    private bool IsGameOver()
    {
        return GameRules.IsGameOver(gameState);
    }

    /// <summary>
    /// 게임을 종료합니다. (남은 플레이어 1명 이하, 최대 라운드 도달)
    /// 결과 창은 한 번만 띄우고, 이후에는 턴을 진행하지 않습니다.
    /// </summary>
    private void EndGame(string reason)
    {
        // 게임 종료 상태로 바꾸고 생존자 최종 등수를 확정하는 일은 규칙(GameRules.EndGame)이 한다. (파산자는 이미 정해짐)
        if (!GameRules.EndGame(gameState, gameData)) return;

        Log($"[게임 종료] {reason}");
        LogRanking();

        // 게임 결과 창을 띄웁니다. (총자산 순으로 순위 표시)
        if (UIManager.Instance != null)
            UIManager.Instance.ShowGameResultPopup(ReturnToRoom);
    }

    private void ReturnToRoom()
    {
        if (SceneFlow.ReturnToRoom()) return;
    }

    // ────────────────────────── 게임 진행 ──────────────────────────

    void StartGame()
    {
        Log($"게임 시작!");
        
        // 첫 번째 순서부터 턴 시작
        HandleTurnChanged(gameState.PlayerOrder[0]);
    }

    public void HandleTurnChanged(long playerId)
    {
        // 게임 종료 판정(새 턴을 시작하기 전), 차례와 라운드 갱신, 더블 초기화, 어떤 입력을 기다릴지는 규칙(GameRules.StartTurn)이 한다.
        switch (GameRules.StartTurn(gameState, gameData, playerId))
        {
            case TurnStartResult.AlreadyOver:
                return;

            case TurnStartResult.LastPlayerLeft:
                EndGame("남은 플레이어가 1명입니다.");
                return;

            case TurnStartResult.RoundLimit:
                EndGame($"최대 라운드({maxRound}라운드)에 도달했습니다.");
                return;
        }

        // 'OO의 턴'이라는 UI를 표시합니다.
        PlayerState playerState = GetPlayerState(playerId);
        Log($"===== {gameState.RoundNumber}/{maxRound}라운드 ({gameState.TurnNumber}번째 턴): {P(playerId)} 차례 (현금 {Won(playerState.Money)}, 위치 {TileName(playerState.Position)}) =====");
        if (UIManager.Instance != null)
            UIManager.Instance.ShowTurn(playerId);
        PlayerManager.Instance.SetCurrentTurn(playerId);

        // 새 차례가 온 플레이어의 위치가 자유여행인 경우
        TileData currentTile = gameData.GetTile(playerState.Position);
        if (currentTile != null && currentTile.Type == TileType.WORLD_TRAVEL)
        {
            // 주사위를 굴리는 창 대신, 원하는 타일을 선택하는 창을 띄웁니다.
            HandleChooseDestinationPrompt();
        }
        else
        {
            HandleRollDicePrompt();
        }
    }

    public void HandleChooseDestinationPrompt()
    {
        // 다른 Prompt 함수들처럼 HandleRoutine을 부르지 않습니다.
        // HandleRoutine은 브로드캐스트를 받았을 때 쓰는 함수라서, 요청을 보내기 전인 여기서 부르면
        // isAfk = !sendAndWaiting 이 true가 되어 잠수용 짧은 제한시간(afkSelectSeconds)으로 여행지 선택창이 금방 닫힙니다.

        // 자신의 차례인 경우에만 자유 여행할 타일을 선택하는 UI를 표시합니다.
        if (IsLocalPlayerTurn())
        {
            if (UIManager.Instance != null)
            {
                UIManager.Instance.ShowChooseDestinationPopup();
            }
        }

        gameState.WaitingType = GameMessageType.DESTINATION_CHOSEN;
    }

    /// <summary>
    /// 화면에서 자유 이동할 위치를 누르면 이 함수가 호출됩니다.
    /// </summary>
    public void ChooseDestination(int destinationPosition)
    {
        sendAndWaiting = true;
        GameNetwork.Instance.SendChooseDestinationRequest(destinationPosition);
    }
    
    public void HandleDestinationChosen(long playerId, int destinationPosition)
    {
        HandleRoutine();
        
        var player = GetPlayerState(playerId);

        int fromPosition = player.Position;
        int toPosition = destinationPosition;
        bool shouldReceiveSalary = toPosition < fromPosition;   // 출발 지점을 지나침

        Log($"[세계여행] {P(playerId)}: {TileName(fromPosition)} → {TileName(toPosition)}");

        MovePlayerToDestination(playerId, fromPosition, toPosition, shouldReceiveSalary);
    }

    public void HandleRollDicePrompt()
    {
        if (IsLocalPlayerTurn())
        {
            if (UIManager.Instance != null)
            {
                UIManager.Instance.ShowRollDicePopup();
            }
        }

        // broadcast메시지 수신 대기 상태에 들어갑니다.
        gameState.WaitingType = GameMessageType.DICE_ROLLED;
    }

    [ContextMenu("RollDiceTest")]   // 플레이 모드에서 테스트용
    private void RollDiceTest() => RollDice();

    /// <summary>
    /// 화면의 '주사위 굴리기' 버튼을 누르면 이 함수가 호출됩니다.
    /// </summary>
    public void RollDice()
    {
        sendAndWaiting = true;
        GameNetwork.Instance.SendRollDiceRequest();
    }

    public void HandleDiceRolled(long playerId, int dice1, int dice2)
    {
        HandleRoutine();

        Log($"[주사위] {P(playerId)}: {dice1}+{dice2}={dice1 + dice2}{(dice1 == dice2 ? " (더블!)" : "")}");
        if (UIManager.Instance != null)
        {
            UIManager.Instance.ShowDiceResult(dice1, dice2,
                () => ContinueAfterDiceResult(playerId, dice1, dice2));
            return;
        }

        ContinueAfterDiceResult(playerId, dice1, dice2);
    }

    /// <summary>주사위 결과 UI가 완전히 닫힌 뒤 실제 판정과 말 이동을 시작합니다.</summary>
    private void ContinueAfterDiceResult(long playerId, int dice1, int dice2)
    {
        PlayerState player = GetPlayerState(playerId);
        if (player == null)
        {
            Debug.LogError($"[GameManager] 주사위 이동 실패: 플레이어 {playerId}를 찾을 수 없습니다.");
            return;
        }

        // 말이 이동 중이면 새 주사위 결과를 처리하지 않습니다. (정상 흐름에서는 걸리지 않는 보호 장치)
        if (isMoving) return;

        // 더블 횟수, 무인도 탈출 판정, 3연속 더블, 이동할 칸과 월급 여부는 규칙(GameRules.ApplyDice)이 정합니다.
        // 규칙은 상태만 바꾸고, 연출과 이어서 진행하는 일은 아래에서 합니다.
        DiceOutcome outcome = GameRules.ApplyDice(gameState, gameData, player, dice1, dice2);

        // 무인도에서 영업정지 중인데 더블이 아니면: 이동 없이 턴이 넘어갑니다.
        if (outcome.Kind == DiceMoveKind.IslandStay)
        {
            Log($"[무인도] {P(playerId)}: 탈출 실패 (남은 영업정지 {outcome.IslandTurnsRemaining}턴)");
            OnIslandEscapeFailed?.Invoke(playerId);
            SpecialTileManager.Instance.PlayIslandEscapeFailed(playerId, outcome.IslandTurnsRemaining); // 탈출 실패 연출
            HandleTurnChanged(GetNextPlayerId()); // 강제로 턴을 넘깁니다.
            return;
        }

        // 더블로 무인도에서 탈출했습니다. (추가 턴은 없습니다)
        if (outcome.EscapedIsland)
        {
            Log($"[무인도 탈출] {P(playerId)}: 더블로 탈출했습니다!");
            SpecialTileManager.Instance.PlayIslandEscaped(playerId); // 탈출 연출
        }

        // 3연속 더블이면 이동하지 않고 무인도로 보낸다.
        if (outcome.Kind == DiceMoveKind.DirectToIsland)
        {
            Log($"[3연속 더블] {P(playerId)}: 무인도로 이동합니다.");
            MovePlayerDirectly(playerId, outcome.To); // 순간이동 (월급 없음) → 도착 처리에서 영업정지 설정 + 턴 넘김
            return;
        }

        int fromPosition = player.Position;
        Log($"[이동] {P(playerId)}: {TileName(fromPosition)} → {TileName(outcome.To)}");

        HandlePlayerMoved(playerId, fromPosition, outcome.To, outcome.ReceivesSalary);
    }

    public void HandlePlayerMoved(long playerId, int fromPosition, int toPosition, bool shouldReceiveSalary)
    {
        PlayerState player = GetPlayerState(playerId);
        if (player == null) return;

        int tileCount = BoardManager.Instance.TileCount;
        int steps = (toPosition - fromPosition + tileCount) % tileCount;

        // 1) GameState는 즉시 갱신 (이동과 월급 지급은 규칙이 한다)
        long before = player.Money;
        long salary = GameRules.MoveTo(gameState, player, toPosition, shouldReceiveSalary);
        if (shouldReceiveSalary)
        {
            EconomyManager.NotifyMoneyChanged(playerId, before, player.Money);
            Log($"[월급] {P(playerId)}: 출발 지점을 지나 월급 {Won(salary)} 지급 (현금 {Won(player.Money)})");
        }

        // 2) 말 이동 연출 → 끝나면 도착 처리
        isMoving = true;
        PlayerManager.Instance.MoveBySteps(
            playerId, fromPosition, steps,
            onTileReached: tileIndex =>
            {
                if (shouldReceiveSalary && tileIndex == 0)
                {
                    // 월급 획득 연출
                }
            },
            onCompleted: () =>
            {
                isMoving = false;
                ProcessArrival(playerId, toPosition);
            });
    }

    /// <summary>
    /// 월급 수령이 가능한 지정 위치로 이동합니다. (세계여행, 황금열쇠 지정 위치 이동)
    /// 무인도 강제 이동처럼 직선으로 가지 않고, 주사위 이동처럼 한 칸씩 뛰지도 않고, 타일을 따라 시계방향으로 빠르게 이동합니다.
    /// 출발 지점을 지나면 월급을 받고, 이동이 끝나면 도착 처리를 합니다.
    /// </summary>
    private void MovePlayerToDestination(long playerId, int fromPosition, int toPosition, bool shouldReceiveSalary)
    {
        PlayerState player = GetPlayerState(playerId);
        if (player == null) return;

        // 1) GameState는 즉시 갱신 (이동과 월급 지급은 규칙이 한다)
        long before = player.Money;
        long salary = GameRules.MoveTo(gameState, player, toPosition, shouldReceiveSalary);
        if (shouldReceiveSalary)
        {
            EconomyManager.NotifyMoneyChanged(playerId, before, player.Money);
            Log($"[월급] {P(playerId)}: 출발 지점을 지나 월급 {Won(salary)} 지급 (현금 {Won(player.Money)})");
        }

        // 2) 타일을 따라 이동하는 연출 → 끝나면 도착 처리
        isMoving = true;
        PlayerManager.Instance.MoveAlongTiles(
            playerId, fromPosition, toPosition,
            onTileReached: tileIndex =>
            {
                if (shouldReceiveSalary && tileIndex == 0)
                {
                    // 월급 획득 연출
                }
            },
            onCompleted: () =>
            {
                isMoving = false;
                ProcessArrival(playerId, toPosition);
            });
    }

    /// <summary>
    /// 말이 목적지로 직접 이동합니다. (자유여행, 뒤로 이동 카드, 무인도행 등) 월급은 없으며, 이동이 끝나면 도착 처리를 합니다.
    /// </summary>
    private void MovePlayerDirectly(long playerId, int toPosition)
    {
        PlayerState player = GetPlayerState(playerId);
        if (player == null) return;

        Log($"[직접 이동] {P(playerId)}: {TileName(player.Position)} → {TileName(toPosition)}");

        GameRules.MoveTo(gameState, player, toPosition, false); // 월급 없는 순간이동
        isMoving = true;
        PlayerManager.Instance.MoveToTile(playerId, toPosition, () =>
        {
            isMoving = false;
            ProcessArrival(playerId, toPosition);
        });
    }

    /// <summary>
    /// 턴을 종료하는 함수입니다. 무조건 턴을 넘기는게 아닙니다! 추가턴 진행 조건(isDouble == true)를 만족하면 추가턴을 진행합니다.
    /// </summary>
    private void ProcessEndTurn()
    {
        // 게임이 끝났는지, 더블이라 추가 턴인지, 다음 사람에게 넘길지는 규칙(GameRules.EndTurn)이 정합니다.
        switch (GameRules.EndTurn(gameState))
        {
            case EndTurnResult.GameOver:
                Log("게임 종료: 남은 플레이어가 1명 이하입니다.");
                return;

            case EndTurnResult.RollAgain:
                // isDouble인 경우 추가턴을 진행합니다.
                HandleRollDicePrompt();
                return;

            default:
                HandleTurnChanged(GetNextPlayerId());
                return;
        }
    }

    private long GetNextPlayerId()
    {
        return GameRules.GetNextPlayerId(gameState);
    }
}