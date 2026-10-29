using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 개발용: 호스트와 클라이언트의 게임 상태가 같은지 비교해서, 다르면 콘솔에 에러로 알려 준다.
/// 클라이언트(GameManager)도 호스트와 같은 규칙으로 상태를 계산하기 때문에, 둘이 어긋나면 규칙 코드가 서로 달라진 것이다.
/// 에디터와 개발 빌드(Development Build)에서만 동작하고, 일반 빌드에서는 호출 자체가 빠진다.
/// 이 PC에 호스트가 없는 참가자라면 비교할 대상이 없어 아무것도 하지 않는다.
///
/// 비교는 둘이 같은 지점에 있을 때만 한다.
///   - 클라이언트가 입력 대기 단계(WaitingType)에 있고, (연출 중이면 비교하지 않는다)
///   - 호스트가 보낸 알림을 클라이언트가 모두 처리했을 때 (호스트는 한 번에 끝까지 계산하므로 클라이언트가 따라잡은 뒤에 비교한다)
/// 같은 지점은 한 번만 비교하고, 같으면 조용히 넘어간다. 다르면 다른 항목을 모두 적어 에러로 남긴다.
/// </summary>
public static class HostStateCheck
{
    private static int lastCheckedCount = -1;

    [System.Diagnostics.Conditional("UNITY_EDITOR"), System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
    public static void Run(GameState clientState)
    {
        if (clientState == null || clientState.WaitingType == null) return; // 클라이언트가 아직 연출 중

        HostGameService host = HostGameService.Current;
        if (host == null) return; // 이 PC에 호스트가 없음

        int processed = GameNetwork.Instance.ProcessedBroadcastCount;
        if (processed != host.BroadcastCountForDebug) return; // 호스트가 보낸 알림을 아직 다 처리하지 못함
        if (processed == lastCheckedCount) return;            // 같은 지점은 한 번만 비교
        lastCheckedCount = processed;

        GameState hostState = host.DebugState;
        if (processed == 0)
            Debug.Log("[StateCheck] 호스트와 클라이언트 상태 비교를 시작합니다. 서로 다르면 [StateCheck] 에러로 표시됩니다.");

        List<string> differences = Compare(hostState, clientState);
        if (differences.Count == 0) return;

        Debug.LogError($"[StateCheck] 호스트와 클라이언트 상태가 다릅니다. ({hostState.TurnNumber}번째 턴, 알림 {processed}개 처리 후)\n  - {string.Join("\n  - ", differences)}");
    }

    private static List<string> Compare(GameState host, GameState client)
    {
        var differences = new List<string>();

        Check(differences, "턴 번호", host.TurnNumber, client.TurnNumber);
        Check(differences, "라운드", host.RoundNumber, client.RoundNumber);
        Check(differences, "현재 차례", host.CurrentPlayerId, client.CurrentPlayerId);
        Check(differences, "대기 단계", host.WaitingType, client.WaitingType);
        Check(differences, "적립금", host.WelfareFund, client.WelfareFund);
        Check(differences, "더블 여부", host.IsDouble, client.IsDouble);
        Check(differences, "연속 더블 횟수", host.ConsecutiveDoubleCount, client.ConsecutiveDoubleCount);
        Check(differences, "게임 종료 여부", host.IsGameOver, client.IsGameOver);

        foreach (PlayerState hostPlayer in host.PlayerStates)
        {
            PlayerState clientPlayer = client.PlayerStates.Find(p => p.PlayerId == hostPlayer.PlayerId);
            if (clientPlayer == null)
            {
                differences.Add($"{hostPlayer.Nickname}: 클라이언트에 없는 플레이어");
                continue;
            }

            string who = hostPlayer.Nickname;
            Check(differences, $"{who} 위치", hostPlayer.Position, clientPlayer.Position);
            Check(differences, $"{who} 현금", hostPlayer.Money, clientPlayer.Money);
            Check(differences, $"{who} 파산", hostPlayer.IsBankrupt, clientPlayer.IsBankrupt);
            Check(differences, $"{who} 무인도 남은 턴", hostPlayer.IslandTurnsRemaining, clientPlayer.IslandTurnsRemaining);
        }

        foreach (PropertyState hostProperty in host.PropertyStates)
        {
            PropertyState clientProperty = client.PropertyStates.Find(p => p.PropertyId == hostProperty.PropertyId);
            if (clientProperty == null)
            {
                differences.Add($"{hostProperty.PropertyId}번 땅: 클라이언트에 없는 땅");
                continue;
            }

            Check(differences, $"{hostProperty.PropertyId}번 땅 주인", hostProperty.OwnerId, clientProperty.OwnerId);
            Check(differences, $"{hostProperty.PropertyId}번 땅 건물 단계", hostProperty.BuildingLevel, clientProperty.BuildingLevel);
        }

        return differences;
    }

    private static void Check<T>(List<string> differences, string name, T host, T client)
    {
        if (!EqualityComparer<T>.Default.Equals(host, client))
            differences.Add($"{name}: 호스트 {host} / 클라이언트 {client}");
    }
}