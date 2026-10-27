using UnityEngine;

/// <summary>
/// 호스트의 시간 흐름(봇 차례, 제한 시간, AFK)을 돌리는 오브젝트.
/// HostGameService는 Unity 오브젝트가 아니라서, 매 프레임 Tick을 불러 줄 곳이 필요하다.
/// 게임 호스트를 만들 때 함께 만들어지고, 호스트가 끝나거나 버려지면 스스로 사라진다.
/// </summary>
public class HostAutoPlayRunner : MonoBehaviour
{
    private HostGameService service;

    public static HostAutoPlayRunner Create(HostGameService service)
    {
        GameObject runnerObject = new GameObject("HostAutoPlayRunner");
        HostAutoPlayRunner runner = runnerObject.AddComponent<HostAutoPlayRunner>();
        runner.service = service;
        return runner;
    }

    private void Update()
    {
        if (service == null || !service.IsRunning)
        {
            Destroy(gameObject);
            return;
        }

        service.Tick(Time.unscaledDeltaTime);
    }
}