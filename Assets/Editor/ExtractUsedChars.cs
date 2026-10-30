using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class ExtractUsedChars
{
    [MenuItem("Tools/Font/Extract Used Chars")]
    private static void Extract()
    {
        var chars = new System.Collections.Generic.SortedSet<char>();

        // 기본 ASCII (32~126)
        for (char c = (char)32; c <= (char)126; c++) chars.Add(c);

        string[] exts = { "*.cs", "*.prefab", "*.unity", "*.asset", "*.json", "*.txt", "*.csv" };
        foreach (string ext in exts)
        {
            foreach (string path in Directory.GetFiles(Application.dataPath, ext, SearchOption.AllDirectories))
            {
                // 폰트 에셋 자체와 출력 파일은 제외
                if (path.Contains("TextMesh Pro") || path.EndsWith("FontChars.txt")) continue;

                string text = File.ReadAllText(path, Encoding.UTF8);
                foreach (char c in text)
                {
                    // 한글 음절, 한글 자모
                    if ((c >= 0xAC00 && c <= 0xD7A3) || (c >= 0x3131 && c <= 0x318E))
                        chars.Add(c);
                }
            }
        }

        string output = Path.Combine(Application.dataPath, "Editor/FontChars.txt");
        File.WriteAllText(output, new string(chars.ToArray()), new UTF8Encoding(false));
        AssetDatabase.Refresh();
        Debug.Log($"[ExtractUsedChars] {chars.Count}자 추출 완료: {output}");
    }
}