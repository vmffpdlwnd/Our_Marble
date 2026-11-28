using System;
using System.Collections.Generic;

[Serializable]
public class SellPropertiesRequest
{
    public string type;
    public List<int> propertyIds;
    // 매각 창 시간이 끝나 화면이 자동으로 고른 땅을 보낸 것인지. 호스트가 사람이 고른 것과 구별해 AFK로 표시한다. (v1.3.2)
    // v1.3.1 호스트는 모르는 값이라 무시하므로 네트워크 버전은 그대로다
    public bool isTimeout;
}