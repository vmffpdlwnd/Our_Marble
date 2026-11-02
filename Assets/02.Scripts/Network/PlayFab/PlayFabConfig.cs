/// <summary>
/// PlayFab 설정. Title ID는 공개돼도 되는 값이다. (웹 빌드에도 그대로 들어간다)
/// Secret Key(Developer Secret Key)는 이 프로젝트의 코드, 설정 파일, 저장소 어디에도 두지 않는다.
/// </summary>
public static class PlayFabConfig
{
    public const string TitleId = "1D07CB";

    public static string BaseUrl => $"https://{TitleId}.playfabapi.com";
}