using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// [임시] 게임 씬 개발용 창. 에디터·개발 빌드에서만 그려지고, F1로 접거나 펼친다.
/// 씬의 UIDebugPanel 오브젝트는 평소 꺼 두고, 쓸 때만 켠다. (켠 씬 변경은 커밋하지 않는다)
///
/// 게임은 호스트가 진행하므로, 이 창은 내 화면의 GameState만 바꾸는 버튼을 두지 않는다. (호스트와 상태가 어긋나 [StateCheck] 에러가 난다)
/// 남겨 둔 것은 읽기만 하거나 실제 경로(GameManager → 호스트 → 알림)로 요청을 보내는 것뿐이다.
/// - 방 정보, 플레이어 상태(현금, 땅, 파산) 보기
/// - 순위 보정 보기 (1등, 꼴찌, 켜짐 여부. 규칙 BalanceRules.GetRankBalance로 내 화면의 상태를 계산한다)
/// - 봇 성향 보기 (숫자, 성향, 너프 여부, 공격형의 목표)
/// - 다음 주사위 정하기 (이 PC가 호스트일 때만. 호스트가 굴리는 눈을 바꾼다. 한 번 또는 계속)
/// </summary>
public class UIDebugPanel : MonoBehaviour
{
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private bool expanded = true;
    private bool placed;
    private bool ready;
    private Rect windowRect = new Rect(16, 120, 460, 10);
    private Vector2 scroll;
    private string lastLog = "";

    private UIManager UI => UIManager.Instance;
    private GameManager Game => GameManager.Instance;
    private GameState State => Game != null ? Game.gameState : null;
    private List<PlayerState> Players => State.PlayerStates;

    // 이 화면의 플레이어 번호(PlayerStates 인덱스). GameManager가 UIManager에 알려 준 값을 따른다
    private int LocalPlayer => Mathf.Max(0, Players.FindIndex(p => p.PlayerId == UI.LocalPlayerId));

    private IEnumerator Start()
    {
        yield return null;
        if (UI == null || Game == null) yield break;

        // 서버에서 게임 상태(GAME_STATE_SYNC)를 받기 전에는 플레이어 목록이 비어 있다. 받을 때까지 기다린다.
        while (State == null || State.PlayerStates == null || State.PlayerStates.Count == 0) yield return null;
        ready = true;
    }

    private void OnGUI()
    {
        if (UI == null) return;

        var e = Event.current;
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.F1) { expanded = !expanded; e.Use(); }

        // 1080p 기준으로 글자 크기를 맞춘다
        float s = Mathf.Max(1f, Screen.height / 1080f * 1.25f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));

        // 처음에는 왼쪽 플레이어 카드 두 장 사이 빈 공간에 둔다
        float viewHeight = Screen.height / s;
        if (!placed) { windowRect.y = viewHeight * 0.27f; placed = true; }
        windowRect.height = expanded ? Mathf.Max(120, viewHeight * 0.53f) : 10;
        windowRect = GUILayout.Window(98231, windowRect, DrawWindow, expanded ? "개발용 (F1 접기)" : "개발용 (F1)");
    }

    private void DrawWindow(int id)
    {
        if (!expanded)
        {
            if (GUILayout.Button("펼치기")) expanded = true;
            GUI.DragWindow();
            return;
        }

        if (Game == null)
        {
            GUILayout.Label("씬에 GameManager가 없습니다.");
            GUI.DragWindow();
            return;
        }
        if (!ready)
        {
            GUILayout.Label("GameManager 준비 중...");
            GUI.DragWindow();
            return;
        }

        scroll = GUILayout.BeginScrollView(scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);

        DrawRoomSection();
        DrawPlayerSection();
        DrawRankBalanceSection();
        DrawBotSection();
        DrawDiceSection();

        GUILayout.EndScrollView();

        if (!string.IsNullOrEmpty(lastLog)) GUILayout.Label(lastLog);
        GUI.DragWindow();
    }

    // ───────────── 방 정보 ─────────────

    // 로비에서 들어온 방(LobbyManager.CurrentRoom)을 보여 준다
    private void DrawRoomSection()
    {
        var room = LobbyManager.CurrentRoom;
        Header("방 정보");
        if (room == null)
        {
            GUILayout.Label("로비를 거치지 않고 실행했습니다. (방 정보 없음)");
            return;
        }
        // 이 클래스에 LocalPlayer라는 멤버(내 자리 번호)가 있어서, 정적 클래스 LocalPlayer는 global::로 구분한다
        int localIndex = room.Players.FindIndex(p => p.playerId == global::LocalPlayer.Id);
        GUILayout.Label($"{room.roomId} / 맵 {room.mapId} / {room.Players.Count}/{room.maxPlayers}명 (봇 {room.botCount})");
        GUILayout.Label($"내 자리 {localIndex + 1}번 ({(room.hostId == global::LocalPlayer.Id ? "방장" : "참가자")})");
    }

    // ───────────── 플레이어 상태 (읽기만) ─────────────

    private void DrawPlayerSection()
    {
        Header($"플레이어 ({Players.Count}명, {State.RoundNumber}/{State.Config.maxRound}라운드)");
        for (int i = 0; i < Players.Count; i++)
        {
            PlayerState p = Players[i];
            int lands = 0; long sellValue = 0;
            foreach (PropertyState ps in State.PropertyStates)
                if (ps.OwnerId == p.PlayerId) { lands++; sellValue += PropertyManager.Instance.GetSellValue(ps); }

            string turn = p.PlayerId == State.CurrentPlayerId ? "▶ " : "";
            string me = i == LocalPlayer ? " (나)" : "";
            string status = p.IsBankrupt ? $"파산 {p.FinalRank}위" : "생존";
            GUILayout.Label($"{turn}{p.Nickname}{me}: 현금 {p.Money:N0} / 땅 {lands}개 (매각가 {sellValue:N0}) / {status}");
        }
        GUILayout.Label(State.IsGameOver ? "(게임 종료 상태)" : $"대기 단계: {(State.WaitingType.HasValue ? State.WaitingType.Value.ToString() : "연출 중")}");
    }

    // ───────────── 순위 보정 (읽기만) ─────────────

    // 호스트와 같은 규칙(BalanceRules.GetRankBalance)으로 내 화면의 GameState를 계산해 보여 준다.
    // 내 화면의 상태로 계산하므로 연출 중에는 호스트보다 늦을 수 있다
    private void DrawRankBalanceSection()
    {
        Header("순위 보정");
        RankBalance balance = BalanceRules.GetRankBalance(State, PropertyManager.Instance.Rules);
        if (balance.Leader == null)
        {
            GUILayout.Label("꺼짐 (살아 있는 플레이어가 2명 미만)");
            return;
        }

        long percent = balance.LeaderAsset > 0 ? balance.LastAsset * 100 / balance.LeaderAsset : 0;
        RichLabel(balance.Active
            ? $"<b>켜짐</b>: 꼴찌가 1등의 {percent}% (기준 {BalanceRules.ActivePercent}% 미만)"
            : $"꺼짐: 꼴찌가 1등의 {percent}% ({BalanceRules.ActivePercent}% 미만이면 켜짐)");
        GUILayout.Label($"1등: {balance.Leader.Nickname} 총자산 {balance.LeaderAsset:N0}");
        GUILayout.Label($"꼴찌: {balance.Last.Nickname} 총자산 {balance.LastAsset:N0}");
    }

    // ───────────── 봇 성향 (읽기만) ─────────────

    // 봇마다 숫자, 성향, 너프 여부, 공격형이면 목표를 보여 준다. 호스트와 같은 규칙으로 내 화면의 GameState를 계산한다
    private void DrawBotSection()
    {
        Header("봇 성향");
        PropertyRules rules = PropertyManager.Instance.Rules;
        bool anyBot = false;
        foreach (PlayerState bot in State.PlayerStates)
        {
            if (!bot.IsBot) continue;
            anyBot = true;

            BotSettings settings = BotSettings.For(BotSettings.PersonalityOf(bot.BotNumber));
            string number = bot.BotNumber.HasValue ? bot.BotNumber.Value.ToString() : "없음";
            string line = $"{bot.Nickname}: 숫자 {number} → {settings.Name}";

            if (bot.IsBankrupt)
            {
                line += " (파산)";
            }
            else if (BalanceRules.IsBotAheadOfHumans(State, rules, bot.PlayerId))
            {
                line += " <b>너프</b>"; // 공격형도 너프 중에는 노리지 않는다
            }
            else if (settings.TargetsLastHuman)
            {
                PlayerState target = LastHuman(rules);
                line += $", 목표 {(target != null ? target.Nickname : "없음")}";
            }
            RichLabel(line);
        }
        if (!anyBot) GUILayout.Label("봇 없음");
    }

    // 살아 있는 사람 중 꼴찌 (뉴비 제외). BasicBotStrategy.FindTarget과 같은 기준이다 (바꾸면 같이 고친다)
    private PlayerState LastHuman(PropertyRules rules)
    {
        List<PlayerState> ranked = RankingRules.Rank(State.PlayerStates, State.PropertyStates, rules);
        for (int i = ranked.Count - 1; i >= 0; i--)
        {
            if (!ranked[i].IsBankrupt && !ranked[i].IsBot && !ranked[i].IsNewbie) return ranked[i];
        }
        return null;
    }

    // ───────────── 주사위 정하기 ─────────────
    // 다음 주사위 두 개의 눈을 고른다. 호스트가 굴리므로 이 PC가 호스트일 때만 된다. (누가 굴리든, 봇이 굴려도 적용)
    // "적용"하면 다음 한 번만, "계속"을 켜면 지울 때까지 그 눈이 나온다.

    private int pickDice1 = 1, pickDice2 = 1;

    private void DrawDiceSection()
    {
        Header("주사위");

        if (HostGameService.Current == null)
        {
            GUILayout.Label("이 PC는 호스트가 아니라서 주사위를 정할 수 없어요.");
            return;
        }

        int set1 = HostGameService.DebugDice1, set2 = HostGameService.DebugDice2;
        GUILayout.BeginHorizontal();
        RichLabel(set1 > 0 ? $"<b>다음 주사위: {set1} + {set2} = {set1 + set2}{(set1 == set2 ? " (더블)" : "")}{(HostGameService.DebugKeepDice ? ", 계속" : "")}</b>" : "다음 주사위: 무작위");
        if (set1 > 0 && GUILayout.Button("지우기", GUILayout.Width(70)))
        {
            HostGameService.DebugDice1 = HostGameService.DebugDice2 = 0;
            Log("주사위 지정을 지웠습니다");
        }
        GUILayout.EndHorizontal();

        pickDice1 = DicePicker("첫째", pickDice1);
        pickDice2 = DicePicker("둘째", pickDice2);

        GUILayout.BeginHorizontal();
        HostGameService.DebugKeepDice = GUILayout.Toggle(HostGameService.DebugKeepDice, "계속 (지울 때까지)");
        if (GUILayout.Button($"적용 ({pickDice1 + pickDice2}칸)", GUILayout.Width(110))) SetDice(pickDice1, pickDice2);
        GUILayout.EndHorizontal();

        // 자주 쓰는 눈: 더블(무인도 탈출, 한 번 더), 3연속 더블은 "계속"을 켜고 더블
        GUILayout.BeginHorizontal();
        GUILayout.Label("빠른 선택", GUILayout.Width(60));
        if (GUILayout.Button("1+1 더블")) SetDice(1, 1);
        if (GUILayout.Button("1+2 (3칸)")) SetDice(1, 2);
        if (GUILayout.Button("6+6 더블")) SetDice(6, 6);
        GUILayout.EndHorizontal();
    }

    // 1~6 버튼 줄. 고른 눈을 돌려준다
    private static int DicePicker(string label, int current)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(40));
        for (int pip = 1; pip <= 6; pip++)
        {
            bool selected = pip == current;
            if (GUILayout.Toggle(selected, pip.ToString(), GUI.skin.button, GUILayout.Width(36)) && !selected) current = pip;
        }
        GUILayout.EndHorizontal();
        return current;
    }

    private void SetDice(int dice1, int dice2)
    {
        pickDice1 = dice1;
        pickDice2 = dice2;
        HostGameService.DebugDice1 = dice1;
        HostGameService.DebugDice2 = dice2;
        Log($"다음 주사위를 {dice1} + {dice2}로 정했습니다{(HostGameService.DebugKeepDice ? " (계속)" : "")}");
    }

    // ───────────── GUI 헬퍼 ─────────────

    private static GUIStyle richLabel;

    // 굵은 글씨 같은 리치 텍스트를 쓰는 라벨. 스타일은 한 번만 만든다
    private static void RichLabel(string text)
    {
        richLabel ??= new GUIStyle(GUI.skin.label) { richText = true };
        GUILayout.Label(text, richLabel);
    }

    private static void Header(string text)
    {
        GUILayout.Space(8);
        RichLabel($"<b>{text}</b>");
    }

    private void Log(string message)
    {
        lastLog = message;
        Debug.Log($"[UIDebugPanel] {message}");
    }
#endif
}