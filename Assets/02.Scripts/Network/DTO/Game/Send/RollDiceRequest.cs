using System;

[Serializable]
public class RollDiceRequest
{
    public string type;
    public bool useIslandEscape; // 무인도 탈출권을 쓰고 굴린다 (들고 있고 무인도에서 영업정지 중일 때만 호스트가 받아들인다)
}