using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

/// <summary>
/// 글자를 시스템 클립보드에 복사한다.
/// 웹(WebGL) 빌드는 Unity가 시스템 클립보드를 지원하지 않아서, 브라우저에 직접 복사를 시킨다. (Assets/Plugins/WebGL/WebClipboard.jslib)
/// 브라우저가 막아서 복사하지 못하면 false를 돌려주므로, 복사하지 못했는데 복사했다고 알리는 일은 없다.
/// </summary>
public static class ClipboardCopy
{
#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern int WebClipboard_Copy(string text);
#endif

    /// <returns>복사에 성공했으면 true. 복사하지 못했거나 성공 여부를 알 수 없으면 false</returns>
    public static bool Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

#if UNITY_WEBGL && !UNITY_EDITOR
        try
        {
            return WebClipboard_Copy(text) == 1;
        }
        catch (System.Exception)
        {
            return false;
        }
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