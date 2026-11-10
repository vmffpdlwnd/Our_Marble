using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

public enum GameLanguage { Korean, English }

/// <summary>
/// 한/영 전환. 한국어 문장이 곧 번역표의 키다. (번역표: Resources/Localization/en.txt)
///
/// 쓰는 법
///   Loc.T("방 만들기")                  지금 언어의 글을 돌려준다. 번역이 없으면 한국어 그대로.
///   Loc.T("{0}님의 방", name)           자리표({0})가 있는 글. 영어는 "{0}'s room"처럼 어순을 바꿔 쓴다.
///   Loc.Bind(label, "방 만들기")        글자를 넣고, 언어가 바뀌면 자동으로 다시 번역한다. (코드가 나중에 글자를 바꾸면 손을 뗀다)
///   Loc.AutoLocalize(root)             씬이나 프리팹에 적힌 한국어 글자를 찾아 같은 방식으로 묶는다. 씬이 로드될 때 자동으로 한 번 돈다.
///
/// 언어는 설정창에서 고르고(SettingsView) PlayerPrefs에 저장한다. 고른 적이 없으면 브라우저와 OS 언어를 따른다.
/// 규칙 코드(Rules/)는 이 클래스를 몰라야 한다. 화면에 글을 넣는 곳에서 번역한다.
/// </summary>
public static class Loc
{
    private const string LanguageKey = "Settings.Language"; // "ko" 또는 "en"
    private const string TableResource = "Localization/en";

    /// <summary>언어가 바뀌었을 때. 직접 글자를 만드는 화면은 여기서 다시 그린다.</summary>
    public static event Action Changed;

    private static GameLanguage? current;
    private static Dictionary<string, string> english;

#if UNITY_EDITOR
    private static readonly HashSet<string> reported = new HashSet<string>();
#endif

    public static GameLanguage Language
    {
        get
        {
            if (current == null) current = LoadSavedLanguage();
            return current.Value;
        }
    }

    private static GameLanguage LoadSavedLanguage()
    {
        string saved = PlayerPrefs.GetString(LanguageKey, "");
        if (saved == "ko") return GameLanguage.Korean;
        if (saved == "en") return GameLanguage.English;
        // 고른 적이 없으면 한국어 기기만 한국어로 시작한다
        return Application.systemLanguage == SystemLanguage.Korean ? GameLanguage.Korean : GameLanguage.English;
    }

    public static void SetLanguage(GameLanguage language)
    {
        if (Language == language) return;
        current = language;
        PlayerPrefs.SetString(LanguageKey, language == GameLanguage.Korean ? "ko" : "en");
        PlayerPrefs.Save();
        Changed?.Invoke();
    }

    /// <summary>지금 언어의 글. 영어가 아니거나 번역이 없으면 받은 한국어 그대로 돌려준다.</summary>
    public static string T(string ko)
    {
        if (string.IsNullOrEmpty(ko) || Language == GameLanguage.Korean) return ko;
        if (English.TryGetValue(ko, out string en)) return en;

#if UNITY_EDITOR
        if (HasHangul(ko) && reported.Add(ko)) Debug.LogWarning($"[Loc] 영어 번역이 없습니다: {ko}");
#endif
        return ko;
    }

    /// <summary>자리표({0}, {1})가 있는 글. 번역한 뒤에 값을 채운다.</summary>
    public static string T(string ko, params object[] args)
    {
        string text = T(ko);
        if (args == null || args.Length == 0) return text;
        try { return string.Format(text, args); }
        catch (FormatException) { return text; }
    }

    /// <summary>
    /// 사람이 정한 글(닉네임, 방 이름)을 보여 줄 때 쓴다. 번역표에 있을 때만 번역한다. (봇 이름처럼 게임이 만든 이름만 번역표에 둔다)
    /// 번역표에 없어도 경고하지 않는다. 닉네임은 번역표에 없는 게 정상이기 때문이다.
    /// </summary>
    public static string TName(string name)
    {
        if (string.IsNullOrEmpty(name) || Language == GameLanguage.Korean) return name;
        return English.TryGetValue(name, out string en) ? en : name;
    }

    /// <summary>글자를 넣고, 언어가 바뀌면 자동으로 다시 번역하게 묶는다.</summary>
    public static void Bind(TMP_Text label, string ko, params object[] args)
    {
        if (label == null) return;
        LocalizedText.Attach(label).SetSource(ko, args);
    }

    /// <summary>root 아래에서 한국어가 적힌 글자를 모두 찾아 Bind한다. 꺼져 있는 오브젝트도 포함한다.</summary>
    public static void AutoLocalize(Transform root)
    {
        if (root == null) return;
        foreach (var label in root.GetComponentsInChildren<TMP_Text>(true))
        {
            if (!HasHangul(label.text) || IsInputText(label)) continue;

            var bound = label.GetComponent<LocalizedText>();
            if (bound != null && bound.HasSource) continue; // 이미 묶여 있다

            LocalizedText.Attach(label).SetSource(label.text, null);
        }
    }

    /// <summary>한글이 들어 있는지.</summary>
    public static bool HasHangul(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char c in text)
            if ((c >= '\uAC00' && c <= '\uD7A3') || (c >= '\u3131' && c <= '\u318E')) return true;
        return false;
    }

    // 입력칸에 사용자가 친 글자는 번역하지 않는다. (안내 글자인 Placeholder는 번역한다)
    private static bool IsInputText(TMP_Text label)
    {
        var input = label.GetComponentInParent<TMP_InputField>(true);
        return input != null && input.textComponent == label;
    }

    // ───────────── 번역표 ─────────────

    private static Dictionary<string, string> English
    {
        get
        {
            if (english == null) english = LoadTable();
            return english;
        }
    }

    // 한 줄에 "한국어 문장<탭>영어 문장". 줄바꿈은 \n, 탭은 \t로 적는다. #으로 시작하는 줄은 설명이다.
    private static Dictionary<string, string> LoadTable()
    {
        var map = new Dictionary<string, string>();
        var asset = Resources.Load<TextAsset>(TableResource);
        if (asset == null)
        {
            Debug.LogWarning($"[Loc] 번역표(Resources/{TableResource})를 찾을 수 없습니다. 영어로 바꿔도 한국어로 보입니다.");
            return map;
        }

        foreach (string raw in asset.text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;

            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;

            string ko = Unescape(line.Substring(0, tab));
            string en = Unescape(line.Substring(tab + 1));
            if (en.Length > 0) map[ko] = en;
        }
        return map;
    }

    private static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\t", "\t");

    // ───────────── 씬이 열릴 때 자동으로 묶기 ─────────────

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Init()
    {
        // 에디터에서 도메인 리로드를 끄고 실행해도 이전 실행의 값이 남지 않게 한다
        current = null;
        english = null;
        Changed = null;

        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        foreach (var root in scene.GetRootGameObjects()) AutoLocalize(root.transform);
    }
}