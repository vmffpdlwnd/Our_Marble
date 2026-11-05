using System;
using System.Threading.Tasks;

/// <summary>한 번의 다시 들어가기 시도가 끝난 결과</summary>
public enum RejoinAttempt
{
    Success,  // 방에 돌아와 자리를 되찾았다
    Retry,    // 이번에는 안 됐지만 잠시 뒤 다시 해 볼 만하다 (연결 실패, 방장의 답이 없음 등)
    RoomGone, // 해 봐야 소용없다 (방이 사라졌거나, 게임이 이미 끝났거나, 이 방의 참가자가 아님)
}

/// <summary>자동으로 다시 들어가기를 마친 결과</summary>
public enum RejoinResult { Success, RoomGone, TimedOut, Cancelled }

/// <summary>
/// 연결이 끊긴 사람이 같은 방으로 자동으로 다시 들어가는 반복. 시도가 성공하거나 소용없다고 정해질 때까지,
/// 정한 시간 안에서 일정한 간격으로 다시 해 본다. (시간과 기다림을 밖에서 받아서 Unity 없이 시험할 수 있다)
/// </summary>
public static class PhotonRejoinLoop
{
    /// <param name="attempt">한 번 시도한다. 인자는 몇 번째 시도인지</param>
    /// <param name="now">지금 시각(초)</param>
    /// <param name="waitUntil">그 시각까지 기다린다</param>
    /// <param name="keepGoing">false가 되면 중단한다 (연결을 끄거나 화면을 떠난 경우)</param>
    /// <param name="totalSeconds">전체 제한 시간</param>
    /// <param name="retrySeconds">시도 사이의 간격</param>
    public static async Task<RejoinResult> RunAsync(
        Func<int, Task<RejoinAttempt>> attempt, Func<float> now, Func<float, Task> waitUntil,
        Func<bool> keepGoing, float totalSeconds, float retrySeconds)
    {
        float deadline = now() + totalSeconds;
        int count = 0;

        while (keepGoing() && now() < deadline)
        {
            count++;
            RejoinAttempt result = await attempt(count);

            if (result == RejoinAttempt.Success) return RejoinResult.Success;
            if (result == RejoinAttempt.RoomGone) return RejoinResult.RoomGone;

            await waitUntil(Math.Min(now() + retrySeconds, deadline));
        }

        return keepGoing() ? RejoinResult.TimedOut : RejoinResult.Cancelled;
    }
}

/// <summary>
/// 다시 연결하는 흐름의 판단 규칙. 화면과 Photon 없이 값만으로 정하므로 Unity 없이 시험할 수 있다.
/// </summary>
public static class RejoinRules
{
    /// <summary>
    /// 방에서 연결이 끊겼을 때 대기방에 다시 들어가 볼지.
    /// 게임 화면이면 게임 쪽 자동 재연결이 맡고, 돌아갈 방 코드를 모르거나 이미 게임이 시작된 방이면 로비로 간다.
    /// </summary>
    public static bool ShouldRejoinWaitingRoom(bool inGameScene, bool hasRoomCode, bool roomWasPlaying)
    {
        return !inGameScene && hasRoomCode && !roomWasPlaying;
    }

    /// <summary>
    /// 로비에 연결하기를 포기하고 혼자 하기로 넘어가기까지 다시 시도하는 전체 시간(초).
    /// 연결된 적이 있다가 끊긴 경우는 길게 기다린다. (웹에서 탭이 뒤로 갔다 돌아온 직후처럼 잠깐 느린 것을 연결 실패로 보지 않으려고)
    /// 처음 연결하는 경우는 인터넷이 없을 때 오래 기다리지 않게 짧게 한다.
    /// </summary>
    public static float LobbyGiveUpSeconds(bool everOnline, float firstConnectSeconds, float afterDropSeconds)
    {
        return everOnline ? afterDropSeconds : firstConnectSeconds;
    }

    /// <summary>
    /// 대기방에 다시 들어가려는데 방장이 오류로 답했을 때, 해 봐야 소용없는지(RoomGone) 잠시 뒤 다시 해 볼 만한지(Retry).
    /// 방이 사라졌거나, 게임이 시작됐거나, 가득 찬 경우는 포기한다. 그 밖에는 다시 해 본다.
    /// (같은 사람의 예전 연결이 방장에게 아직 남아 있는 ALREADY_IN_ROOM은 곧 정리되므로 다시 해 본다)
    /// </summary>
    public static RejoinAttempt ClassifyEnterError(string errorCode)
    {
        switch (errorCode)
        {
            case "ROOM_NOT_FOUND":
            case "ROOM_PLAYING":
            case "ROOM_FULL":
            case "NOT_REJOINABLE":
                return RejoinAttempt.RoomGone;
            default:
                return RejoinAttempt.Retry;
        }
    }
}