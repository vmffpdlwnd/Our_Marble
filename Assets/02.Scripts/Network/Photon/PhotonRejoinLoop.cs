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