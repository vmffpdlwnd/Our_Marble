using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

/// <summary>
/// 글꼴에 없는 글자를 실행 중에 그려 주는 "동적 대체 글꼴"을 만들고 TMP 설정(Fallback Font Assets)에 등록한다.
///
/// 이 프로젝트의 TextMeshPro 글꼴은 Static이라 FontChars.txt에 있는 787글자만 들어 있다.
/// 닉네임이나 채팅에 입력한 드문 한글(예: 왜)은 그 안에 없어서 네모로 보였다.
/// 동적 대체 글꼴은 원본 글꼴 파일(Maplestory Bold.ttf)에서 필요한 글자만 그때그때 그려서 채우므로 모든 한글 음절이 보인다.
/// 원본 글꼴은 한글 11,172자와 호환 자모 94자를 모두 가지고 있다.
///
/// 처음 한 번 Tools > Font > Create Dynamic Fallback Font 를 누른다. 이미 있으면 등록만 확인한다.
/// </summary>
public static class FallbackFontTools
{
    private const string SourceFontPath = "Assets/TextMesh Pro/Fonts/Maplestory Bold.ttf";
    private const string FallbackAssetPath = "Assets/TextMesh Pro/Fonts/Maplestory Bold Dynamic SDF.asset";

    // 기존 Maplestory Bold SDF와 같은 값 (샘플링 69, 여백 6, SDFAA). 글자판은 1024 한 장에서 시작하고 모자라면 한 장씩 늘어난다.
    private const int SamplingPointSize = 69;
    private const int AtlasPadding = 6;
    private const int AtlasSize = 1024;

    [MenuItem("Tools/Font/Create Dynamic Fallback Font")]
    private static void CreateFallback()
    {
        TMP_FontAsset fallback = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FallbackAssetPath);
        bool created = false;

        if (fallback == null)
        {
            Font source = AssetDatabase.LoadAssetAtPath<Font>(SourceFontPath);
            if (source == null)
            {
                Fail($"원본 글꼴을 찾지 못했어요.\n{SourceFontPath}");
                return;
            }

            fallback = TMP_FontAsset.CreateFontAsset(source, SamplingPointSize, AtlasPadding, GlyphRenderMode.SDFAA,
                AtlasSize, AtlasSize, AtlasPopulationMode.Dynamic, true);
            if (fallback == null)
            {
                Fail("동적 글꼴을 만들지 못했어요. 원본 글꼴(Maplestory Bold.ttf)의 Import Settings에서 'Include Font Data'가 켜져 있는지 확인해 주세요.");
                return;
            }

            fallback.name = "Maplestory Bold Dynamic SDF";
            AssetDatabase.CreateAsset(fallback, FallbackAssetPath);

            // 글자판(Atlas)과 재질(Material)을 이 에셋 안에 넣는다. (Font Asset Creator가 저장할 때와 같다)
            for (int i = 0; i < fallback.atlasTextures.Length; i++)
            {
                fallback.atlasTextures[i].name = fallback.name + " Atlas";
                AssetDatabase.AddObjectToAsset(fallback.atlasTextures[i], fallback);
            }
            fallback.material.name = fallback.name + " Material";
            AssetDatabase.AddObjectToAsset(fallback.material, fallback);

            EditorUtility.SetDirty(fallback);
            created = true;
        }

        bool registered = Register(fallback);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        string message = (created ? "동적 대체 글꼴을 만들었어요.\n" : "동적 대체 글꼴이 이미 있어요.\n")
            + (registered ? "TMP Settings의 Fallback Font Assets에 등록했어요." : "TMP Settings의 Fallback Font Assets에 이미 등록돼 있어요.")
            + $"\n\n{FallbackAssetPath}";
        Debug.Log("[FallbackFont] " + message.Replace("\n", " "));
        EditorUtility.DisplayDialog("동적 대체 글꼴", message, "확인");
    }

    /// <summary>
    /// 동적 대체 글꼴에 쌓인 글자판을 비운다. 에디터에서 실행하면 그린 글자가 에셋에 저장되어 파일이 바뀐 것으로 잡히므로,
    /// 커밋하기 전에 누른다. (비워도 실행 중에 필요한 글자를 다시 그린다)
    /// </summary>
    [MenuItem("Tools/Font/Reset Dynamic Fallback Font Data")]
    private static void ResetFallback()
    {
        TMP_FontAsset fallback = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FallbackAssetPath);
        if (fallback == null)
        {
            Fail("동적 대체 글꼴이 아직 없어요. Tools > Font > Create Dynamic Fallback Font 를 먼저 눌러 주세요.");
            return;
        }

        fallback.ClearFontAssetData(true);
        EditorUtility.SetDirty(fallback);
        AssetDatabase.SaveAssets();
        Debug.Log("[FallbackFont] 동적 대체 글꼴의 글자판을 비웠습니다");
    }

    // TMP Settings의 대체 글꼴 목록에 넣는다. 새로 넣었으면 true, 이미 있었으면 false
    private static bool Register(TMP_FontAsset fallback)
    {
        List<TMP_FontAsset> list = TMP_Settings.fallbackFontAssets;
        if (list == null)
        {
            list = new List<TMP_FontAsset>();
            TMP_Settings.fallbackFontAssets = list;
        }

        if (list.Contains(fallback)) return false;

        list.Add(fallback);
        EditorUtility.SetDirty(TMP_Settings.instance);
        return true;
    }

    private static void Fail(string message)
    {
        Debug.LogError("[FallbackFont] " + message.Replace("\n", " "));
        EditorUtility.DisplayDialog("동적 대체 글꼴", message, "확인");
    }
}