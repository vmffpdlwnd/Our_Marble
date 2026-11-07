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
/// 동적 대체 글꼴은 원본 글꼴 파일에서 필요한 글자만 그때그때 그려서 채운다.
///
/// 원본으로 Pretendard를 쓰는 이유: Maplestory 글꼴은 한글 11,172자를 모두 "등록"해 두었지만 실제로 그려져 있는 것은 2,479자뿐이고,
/// 나머지 8,693자(아주 드문 글자 대부분)는 빈 그림이라 네모도 아니고 아무것도 안 보인다. Pretendard는 한글 11,172자를 모두 그리고
/// 채팅에 자주 쓰는 자모(ㅋ, ㅠ 등 51자)도 그린다.
///
/// 처음 한 번 Tools > Font > Create Dynamic Fallback Font 를 누른다. 이미 있으면 등록만 확인한다.
///
/// 에디터에서 게임을 실행하면 대체 글꼴이 그린 글자(방 목록의 ·, 게임 중의 ● 같은 기호)가 에셋 파일에 저장되어
/// Git에 변경된 파일로 계속 보인다. 그래서 에디터에서 플레이를 멈출 때마다 글자판을 자동으로 비운다.
/// (빌드에는 영향이 없다. 빌드할 때도 비워진다. 실행 중에 필요한 글자는 다시 그려진다)
/// </summary>
[InitializeOnLoad]
public static class FallbackFontTools
{
    static FallbackFontTools()
    {
        RegisterAutoReset();
    }

    private const string SourceFontPath = "Assets/TextMesh Pro/Fonts/Pretendard-Regular.otf";
    private const string FallbackAssetPath = "Assets/TextMesh Pro/Fonts/Pretendard Dynamic SDF.asset";

    // 이전에 Maplestory Bold로 만들었던 동적 대체 글꼴. 거기서는 드문 한글이 빈 그림이라 안 보여서 지우고 Pretendard로 바꾼다.
    private const string OldFallbackAssetPath = "Assets/TextMesh Pro/Fonts/Maplestory Bold Dynamic SDF.asset";

    // 기존 Pretendard-Regular SDF와 같은 값 (샘플링 72, 여백 7, SDFAA). 글자판은 1024 한 장에서 시작하고 모자라면 한 장씩 늘어난다.
    private const int SamplingPointSize = 72;
    private const int AtlasPadding = 7;
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
                Fail("동적 글꼴을 만들지 못했어요. 원본 글꼴(Pretendard-Regular.otf)의 Import Settings에서 'Include Font Data'가 켜져 있는지 확인해 주세요.");
                return;
            }

            fallback.name = "Pretendard Dynamic SDF";
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

        bool removedOld = RemoveOldFallback();
        bool registered = Register(fallback);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        string message = (created ? "동적 대체 글꼴을 만들었어요.\n" : "동적 대체 글꼴이 이미 있어요.\n")
            + (registered ? "TMP Settings의 Fallback Font Assets에 등록했어요." : "TMP Settings의 Fallback Font Assets에 이미 등록돼 있어요.")
            + (removedOld ? "\n예전에 만든 Maplestory Bold 동적 대체 글꼴은 지웠어요. (드문 한글이 안 보여서 Pretendard로 바꿨어요)" : "")
            + $"\n\n{FallbackAssetPath}";
        Debug.Log("[FallbackFont] " + message.Replace("\n", " "));
        EditorUtility.DisplayDialog("동적 대체 글꼴", message, "확인");
    }

    /// <summary>
    /// 동적 대체 글꼴에 쌓인 글자판을 비운다. 플레이를 멈출 때 자동으로 비워지지만, 수동으로도 쓸 수 있다.
    /// (에디터가 멈췄거나 이 스크립트를 넣기 전에 쌓인 글자를 비울 때. 비워도 실행 중에 필요한 글자를 다시 그린다)
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

        ClearData(fallback);
        Debug.Log("[FallbackFont] 동적 대체 글꼴의 글자판을 비웠습니다");
    }

    /// <summary>플레이를 멈추면 대체 글꼴의 글자판을 비우도록 연결한다. (여러 번 불러도 한 번만 연결된다)</summary>
    public static void RegisterAutoReset()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        // 플레이가 끝나고 에디터 모드로 돌아왔을 때만. 바로 비우지 않고 한 박자 뒤에 비운다.
        // (TMP가 플레이 중에 마지막으로 그린 글자를 화면을 그린 뒤에 저장하므로, 그 저장이 끝난 다음에 비워야 한다)
        if (state != PlayModeStateChange.EnteredEditMode) return;

        EditorApplication.delayCall -= ClearAfterPlay;
        EditorApplication.delayCall += ClearAfterPlay;
    }

    private static void ClearAfterPlay()
    {
        TMP_FontAsset fallback = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FallbackAssetPath);
        if (fallback == null) return; // 아직 만들지 않았다

        int drawn = fallback.characterTable != null ? fallback.characterTable.Count : 0;
        if (drawn == 0) return; // 이미 비어 있다. 파일을 건드리지 않는다.

        ClearData(fallback);
        Debug.Log($"[FallbackFont] 플레이를 멈춰서 대체 글꼴이 그린 글자 {drawn}개를 비웠습니다. (Git에 변경으로 남지 않게)");
    }

    // 글자판을 비우고 저장한다
    private static void ClearData(TMP_FontAsset fallback)
    {
        fallback.ClearFontAssetData(true);
        EditorUtility.SetDirty(fallback);
        AssetDatabase.SaveAssets();
    }

    // 예전에 만든 Maplestory Bold 동적 대체 글꼴을 목록에서 빼고 지운다. 있었으면 true
    private static bool RemoveOldFallback()
    {
        TMP_FontAsset old = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(OldFallbackAssetPath);
        if (old == null) return false;

        List<TMP_FontAsset> list = TMP_Settings.fallbackFontAssets;
        if (list != null)
        {
            list.RemoveAll(font => font == old);
            EditorUtility.SetDirty(TMP_Settings.instance);
        }

        AssetDatabase.DeleteAsset(OldFallbackAssetPath);
        return true;
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