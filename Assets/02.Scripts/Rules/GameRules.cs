using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 게임 진행 규칙: 턴 시작과 종료, 주사위 결과(무인도, 더블), 이동과 월급, 게임 종료, 순서 계산.
/// 화면(Unity 오브젝트)을 모른다. GameState를 바꾸고 결과를 돌려주며, 그 결과를 연출하거나 다음 단계로 이을지는 부른 쪽이 정한다.
/// 호스트(HostGameService)가 이 규칙을 부른다.
/// 각 함수 위에 짝이 되는 GameManager 함수를 적어 두었다. 규칙을 바꿀 때는 양쪽을 함께 고친다.
/// </summary>
public static class GameRules
{
    public const int IslandTurns = 3; // GameManager.IslandTurns와 같아야 한다 (무인도 영업정지 턴 수)

    // ───────────── 순서 계산 ─────────────

    // GameManager.IsGameOver
    /// <summary>살아 있는 플레이어가 1명 이하인가</summary>
    public static bool IsGameOver(GameState state)
    {
        return state.PlayerStates.Count(p => !p.IsBankrupt) <= 1;
    }

    // GameManager.GetNextPlayerId (파산한 플레이어는 건너뛴다)
    public static long GetNextPlayerId(GameState state)
    {
        List<long> order = state.PlayerOrder;
        int currentIndex = order.IndexOf(state.CurrentPlayerId);
        for (int i = 1; i <= order.Count; i++)
        {
            long candidateId = order[(currentIndex + i) % order.Count];
            PlayerState candidate = FindPlayer(state, candidateId);
            if (candidate != null && !candidate.IsBankrupt) return candidateId;
        }
        return state.CurrentPlayerId; // 모두 파산한 경우 (게임 종료 상황)
    }

    // GameManager.GetNextRound
    // nextPlayerId의 차례가 몇 번째 라운드인지. 턴 순서가 처음으로 돌아오면 1 증가한다.
    public static int GetNextRound(GameState state, long nextPlayerId)
    {
        int prevIndex = state.PlayerOrder.IndexOf(state.CurrentPlayerId);
        int nextIndex = state.PlayerOrder.IndexOf(nextPlayerId);
        return nextIndex < prevIndex ? state.RoundNumber + 1 : state.RoundNumber;
    }

    // GameManager.FindIslandTileIndex
    /// <summary>무인도 칸의 번호. 없으면 -1</summary>
    public static int FindIslandTileIndex(GameData data)
    {
        int tileCount = data.TileCount;
        for (int i = 0; i < tileCount; i++)
        {
            TileData tile = data.GetTile(i);
            if (tile != null && tile.Type == TileType.ISLAND) return i;
        }
        return -1;
    }

    // ───────────── 턴 ─────────────

    // GameManager.HandleTurnChanged
    /// <summary>
    /// 새 차례를 시작한다. 게임이 끝났는지 먼저 확인하고, 끝나지 않았으면 차례와 라운드를 바꾸고 어떤 입력을 기다릴지(WaitingType) 정한다.
    /// 게임이 끝나는 경우 상태는 바꾸지 않는다. 끝내는 일(EndGame)은 부른 쪽이 한다.
    /// </summary>
    public static TurnStartResult StartTurn(GameState state, GameData data, long playerId)
    {
        if (state.IsGameOver) return TurnStartResult.AlreadyOver;

        if (IsGameOver(state)) return TurnStartResult.LastPlayerLeft;

        int nextRound = GetNextRound(state, playerId);
        if (nextRound > state.Config.maxRound) return TurnStartResult.RoundLimit;

        state.CurrentPlayerId = playerId;
        state.TurnNumber += 1;
        state.RoundNumber = nextRound;
        state.IsDouble = false;
        state.ConsecutiveDoubleCount = 0;

        PlayerState player = FindPlayer(state, playerId);

        // 세계여행 칸에서 시작하는 차례는 주사위 대신 목적지 선택으로 시작한다
        TileData tile = data.GetTile(player.Position);
        state.WaitingType = tile != null && tile.Type == TileType.WORLD_TRAVEL
            ? GameMessageType.DESTINATION_CHOSEN
            : GameMessageType.DICE_ROLLED;

        return TurnStartResult.Started;
    }

    // GameManager.ProcessEndTurn
    /// <summary>
    /// 턴을 끝낸다. 더블이면 같은 사람이 한 번 더 굴리게 하고(RollAgain), 아니면 다음 사람에게 넘겨야 한다고 알린다(NextTurn).
    /// </summary>
    public static EndTurnResult EndTurn(GameState state)
    {
        if (IsGameOver(state)) return EndTurnResult.GameOver;

        if (state.IsDouble)
        {
            state.WaitingType = GameMessageType.DICE_ROLLED; // 더블이면 같은 사람이 한 번 더 굴린다
            return EndTurnResult.RollAgain;
        }

        return EndTurnResult.NextTurn;
    }

    // ───────────── 주사위와 이동 ─────────────

    // GameManager.ContinueAfterDiceResult
    /// <summary>
    /// 주사위 결과를 반영한다. 더블 횟수와 무인도 남은 턴은 여기서 바꾸고, 이동은 하지 않는다.
    /// 어디로 어떻게 이동할지(또는 이동 없이 턴이 넘어가는지)는 돌려주는 값에 담겨 있다.
    /// </summary>
    public static DiceOutcome ApplyDice(GameState state, GameData data, PlayerState player, int dice1, int dice2)
    {
        if (dice1 == dice2)
        {
            state.IsDouble = true;
            state.ConsecutiveDoubleCount++;
        }
        else
        {
            state.IsDouble = false;
            state.ConsecutiveDoubleCount = 0;
        }

        TileData currentTile = data.GetTile(player.Position);
        bool onIslandTile = currentTile != null && currentTile.Type == TileType.ISLAND;
        bool islandTurnsRemaining = player.IslandTurnsRemaining > 0;

        // 무인도에서 영업정지 중인데 더블이 아니면 이동 없이 턴이 넘어간다
        if (onIslandTile && islandTurnsRemaining && !state.IsDouble)
        {
            player.IslandTurnsRemaining -= 1;
            return new DiceOutcome { Kind = DiceMoveKind.IslandStay, IslandTurnsRemaining = player.IslandTurnsRemaining };
        }

        // 더블이면 영업정지가 풀리고 그 더블로 이동한다. 추가 턴은 없다.
        // 남은 턴이 0이 된 뒤의 더블은 여기에 걸리지 않고 일반 더블로 처리된다.
        bool escapedIsland = false;
        if (onIslandTile && islandTurnsRemaining && state.IsDouble)
        {
            player.IslandTurnsRemaining = 0;
            state.IsDouble = false;
            escapedIsland = true;
        }

        // 3번째 연속 더블이면 이동 없이 무인도로 간다. (월급 없음)
        if (state.ConsecutiveDoubleCount >= 3)
        {
            int islandPosition = FindIslandTileIndex(data);
            if (islandPosition >= 0)
            {
                state.IsDouble = false;
                state.ConsecutiveDoubleCount = 0;
                return new DiceOutcome { Kind = DiceMoveKind.DirectToIsland, To = islandPosition, EscapedIsland = escapedIsland };
            }
        }

        int tileCount = data.TileCount;
        int from = player.Position;
        int to = (from + dice1 + dice2) % tileCount;
        bool shouldReceiveSalary = to < from; // 출발 지점을 지나침
        return new DiceOutcome { Kind = DiceMoveKind.Move, To = to, ReceivesSalary = shouldReceiveSalary, EscapedIsland = escapedIsland };
    }

    // GameManager.HandlePlayerMoved, MovePlayerDirectly
    /// <summary>플레이어를 to 칸으로 옮긴다. receiveSalary이면 월급을 주고 그 금액을 돌려준다. (월급 없는 순간이동은 false)</summary>
    public static long MoveTo(GameState state, PlayerState player, int to, bool receiveSalary)
    {
        player.Position = to;

        if (!receiveSalary) return 0;

        player.Money += state.Config.salaryAmount;
        return state.Config.salaryAmount;
    }

    // ───────────── 게임 종료 ─────────────

    // GameManager.EndGame
    /// <summary>
    /// 게임을 끝낸다. 상태를 종료로 바꾸고 살아남은 플레이어에게 등수를 준다.
    /// 이미 끝난 게임이면 아무것도 하지 않고 false.
    /// </summary>
    public static bool EndGame(GameState state, GameData data)
    {
        if (state.IsGameOver) return false;

        state.IsGameOver = true;
        state.WaitingType = null;

        AssignSurvivorRanks(state, data);
        return true;
    }

    // GameManager.AssignSurvivorRanks (살아남은 플레이어에게 총자산 → 현금 → 원래 순서로 1위부터 부여)
    public static void AssignSurvivorRanks(GameState state, GameData data)
    {
        List<PlayerState> ranked = RankingRules.Rank(state.PlayerStates, state.PropertyStates, data.Properties);
        int rank = 1;
        foreach (PlayerState player in ranked)
        {
            if (player.IsBankrupt) continue;
            player.FinalRank = rank++;
        }
    }

    private static PlayerState FindPlayer(GameState state, long playerId)
    {
        return state.PlayerStates.Find(p => p.PlayerId == playerId);
    }
}