using Fusion;
using UnityEngine;

/// <summary>
/// 네트워크 러너에 Fusion 기본 도우미(씬 관리, 오브젝트 생성)를 붙인다.
/// Fusion 샘플(FusionBootstrap)이 하는 방식과 같다. 우리 게임은 씬을 직접 불러오고 네트워크 오브젝트를 아직 쓰지 않지만,
/// 러너를 시작하려면 이 두 가지가 있는 편이 안전해서 기본 구현을 붙인다.
/// </summary>
public static class PhotonRunnerSetup
{
    public static void Prepare(NetworkRunner runner, out INetworkSceneManager sceneManager, out INetworkObjectProvider objectProvider)
    {
        sceneManager = runner.GetComponent<INetworkSceneManager>();
        if (sceneManager == null)
            sceneManager = runner.gameObject.AddComponent<NetworkSceneManagerDefault>();

        objectProvider = runner.GetComponent<INetworkObjectProvider>();
        if (objectProvider == null)
            objectProvider = runner.gameObject.AddComponent<NetworkObjectProviderDefault>();
    }
}