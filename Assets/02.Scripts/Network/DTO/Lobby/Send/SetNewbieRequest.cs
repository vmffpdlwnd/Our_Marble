using System;

/// <summary>
/// 뉴비 옵션을 켜거나 끈다 (v1.3). 방 밖에서 보내도 호스트가 기억해 두었다가 방에 들어갈 때 쓴다. (SetPortraitRequest와 같은 방식)
/// 게임 중에는 바꿀 수 없어서, 호스트는 대기 중인 방에서만 자리에 반영한다.
/// </summary>
[Serializable]
public class SetNewbieRequest
{
    public string type;
    public bool newbie;
}