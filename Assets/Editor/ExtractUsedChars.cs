using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 정적 글꼴(Maplestory Bold, Light)에 넣을 글자 목록(FontChars.txt)을 만든다.
///
/// 목록은 세 가지를 합친 것이다.
///  1) 기본 ASCII (32~126)
///  2) FontCharsBase.txt: Maplestory 글꼴이 실제로 그려 주는 한글 음절 2,479자와 자모 93자 전부.
///     (Maplestory는 한글 11,172자를 등록만 해 두고 나머지 8,693자는 빈 그림이라, 그리는 글자만 넣는다.
///      그 밖의 드문 글자는 동적 대체 글꼴 Pretendard가 그려 준다. FallbackFontTools 참고)
///  3) 프로젝트의 코드, 씬, 프리팹, 데이터에 쓰인 한글. (기본 글자에 이미 있으면 그대로)
///
/// 씬, 프리팹, 에셋 파일은 한글을 \uD1B5처럼 이스케이프로 저장하므로 읽을 때 풀어서 본다.
/// (예전에는 이스케이프를 풀지 않아서 씬과 데이터에만 있는 글자가 목록에서 빠졌다)
/// </summary>
public static class ExtractUsedChars
{
    private static readonly Regex UnicodeEscape = new Regex(@"\\u([0-9A-Fa-f]{4})", RegexOptions.Compiled);

    [MenuItem("Tools/Font/Extract Used Chars")]
    private static void Extract()
    {
        var chars = new SortedSet<char>();

        // 기본 ASCII (32~126)
        for (char c = (char)32; c <= (char)126; c++) chars.Add(c);

        // 기본 글자: 글꼴이 그려 주는 한글 전부
        int baseCount = 0;
        string basePath = Path.Combine(Application.dataPath, "Editor/FontCharsBase.txt");
        if (File.Exists(basePath))
        {
            foreach (char c in File.ReadAllText(basePath, Encoding.UTF8))
                if (IsHangul(c) && chars.Add(c)) baseCount++;
        }
        else
        {
            Debug.LogWarning("[ExtractUsedChars] Assets/Editor/FontCharsBase.txt가 없어서 프로젝트에 쓰인 글자만 모읍니다");
        }

        // 프로젝트에 쓰인 한글
        int usedCount = 0;
        string[] exts = { "*.cs", "*.prefab", "*.unity", "*.asset", "*.json", "*.txt", "*.csv" };
        foreach (string ext in exts)
        {
            foreach (string path in Directory.GetFiles(Application.dataPath, ext, SearchOption.AllDirectories))
            {
                // 폰트 에셋 자체와 출력 파일은 제외
                if (path.Contains("TextMesh Pro") || path.EndsWith("FontChars.txt")) continue;

                string text = File.ReadAllText(path, Encoding.UTF8);
                text = UnicodeEscape.Replace(text, match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());
                foreach (char c in text)
                {
                    if (IsHangul(c) && chars.Add(c)) usedCount++;
                }
            }
        }

        string output = Path.Combine(Application.dataPath, "Editor/FontChars.txt");
        File.WriteAllText(output, new string(chars.ToArray()), new UTF8Encoding(false));
        AssetDatabase.Refresh();
        Debug.Log($"[ExtractUsedChars] {chars.Count}자 추출 완료 (기본 글자에서 {baseCount}자, 프로젝트에서 더 찾은 글자 {usedCount}자): {output}");
    }

    // 한글 음절, 한글 자모
    private static bool IsHangul(char c) => (c >= 0xAC00 && c <= 0xD7A3) || (c >= 0x3131 && c <= 0x318E);
}