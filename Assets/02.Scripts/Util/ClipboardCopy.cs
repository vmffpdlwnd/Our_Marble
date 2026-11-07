using UnityEngine;

/// <summary>
/// 글자를 시스템 클립보드에 복사한다.
/// 웹(WebGL) 빌드는 Unity가 시스템 클립보드를 지원하지 않고, itch.io의 iframe 안에서는 브라우저가 막는 일도 있어서 복사를 시도하지 않는다.
/// (복사하지 못했는데 복사했다고 알리지 않으려고 항상 false를 돌려준다)
/// </summary>
public static class ClipboardCopy
{
    /// <returns>복사에 성공했으면 true. 웹 빌드에서는 항상 false</returns>
    public static bool Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

#if UNITY_WEBGL && !UNITY_EDITOR
        return false;
#else
        try
        {
            GUIUtility.systemCopyBuffer = text;
            return GUIUtility.systemCopyBuffer == text; // 정말 들어갔는지 읽어서 확인한다
        }
        catch (System.Exception)
        {
            return false;
        }
#endif
    }
}