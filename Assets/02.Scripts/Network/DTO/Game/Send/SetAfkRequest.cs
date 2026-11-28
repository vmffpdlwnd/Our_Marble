using System;

/// <summary>
/// 자리 비움 요청 (v1.3.2). 상단바의 자리 비움 버튼을 누를 때 보낸다. 차례와 단계를 가리지 않고 받는다.
/// afk가 true면 호스트가 바로 AFK로 표시하고, false면 푼다. 이 요청 자체로는 다른 요청처럼 AFK가 풀리지 않는다.
/// v1.3.1 방장은 이 요청을 몰라 "요청을 처리하지 못했어요"로 거절할 뿐이라 네트워크 버전은 그대로다.
/// </summary>
[Serializable]
public class SetAfkRequest
{
    public string type;
    public bool afk;
}