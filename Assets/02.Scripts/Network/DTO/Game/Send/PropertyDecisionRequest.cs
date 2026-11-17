using System;

[Serializable]
public class PropertyDecisionRequest
{
    public string type;
    public int propertyId;
    public bool isAccept;
    public bool useBuildDiscount; // 건설할 때 건설 할인권을 쓴다 (BUILD에서만 본다)
}