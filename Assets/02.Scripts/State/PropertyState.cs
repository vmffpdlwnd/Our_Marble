public class PropertyState
{
    public int PropertyId { get; set; }
    public long? OwnerId { get; set; }
    public BuildingLevel BuildingLevel { get; set; }

    // 통행료 2배 카드로 이 땅을 고른 사람. 그 사람이 아직 주인이면 다음 통행료 한 번이 2배이고, 통행료를 받으면 지운다.
    // 주인이 바뀌면(매각, 인수, 파산) 고른 사람과 달라져 저절로 효과가 없어진다
    public long? TollDoubleOwnerId { get; set; }

    public PropertyState(int propertyId)
    {
        PropertyId = propertyId;
        OwnerId = null;
        BuildingLevel = BuildingLevel.Land;
        TollDoubleOwnerId = null;
    }
}