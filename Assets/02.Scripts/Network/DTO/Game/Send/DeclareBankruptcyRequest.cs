using System;

/// <summary>파산 신청 요청. 내 차례에 입력을 기다리는 동안만 받는다. (단계는 가리지 않는다)</summary>
[Serializable]
public class DeclareBankruptcyRequest
{
    public string type;
}