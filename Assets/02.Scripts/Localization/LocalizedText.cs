using TMPro;
using UnityEngine;

/// <summary>
/// 한국어 원문을 기억해 두고, 언어가 바뀌면 글자를 다시 번역해서 넣는다. (직접 붙이지 않고 Loc.Bind나 Loc.AutoLocalize가 붙인다)
/// 코드가 나중에 이 글자를 다른 글로 바꾸면(내가 마지막으로 넣은 글과 달라지면) 그 글자는 더 이상 건드리지 않는다.
/// 그런 글자는 코드가 언어가 바뀔 때 직접 다시 그리거나, Loc.Bind로 다시 묶어야 한다.
/// 원문과 값은 저장하지 않는다. 씬이 열릴 때 글자에서 다시 찾는다.
/// </summary>
[DisallowMultipleComponent]
public class LocalizedText : MonoBehaviour
{
    private TMP_Text label;
    private string source;     // 한국어 원문 (번역표의 키)
    private object[] args;     // 자리표에 채울 값
    private string applied;    // 내가 마지막으로 글자에 넣은 글

    public bool HasSource => !string.IsNullOrEmpty(source);

    public static LocalizedText Attach(TMP_Text label)
    {
        var component = label.GetComponent<LocalizedText>();
        if (component == null) component = label.gameObject.AddComponent<LocalizedText>();
        component.label = label;
        return component;
    }

    public void SetSource(string ko, object[] formatArgs)
    {
        if (label == null) label = GetComponent<TMP_Text>();
        if (label == null) return;

        if (string.IsNullOrEmpty(ko))
        {
            // 글자를 비우는 경우. 더 묶을 원문이 없다
            source = null; args = null; applied = null;
            label.text = ko ?? string.Empty;
            return;
        }

        source = ko;
        args = formatArgs;
        Apply();
    }

    private void OnEnable()
    {
        Loc.Changed += OnLanguageChanged;
        if (HasSource) Refresh(); // 꺼져 있는 동안 바뀐 언어를 따라잡는다
    }

    private void OnDisable()
    {
        Loc.Changed -= OnLanguageChanged;
    }

    private void OnLanguageChanged() => Refresh();

    private void Refresh()
    {
        if (!HasSource || label == null) return;

        // 코드가 글자를 다른 글로 바꿨다. 그 글은 코드가 책임지므로 손을 뗀다
        if (applied != null && label.text != applied)
        {
            source = null; args = null; applied = null;
            return;
        }
        Apply();
    }

    private void Apply()
    {
        applied = args == null || args.Length == 0 ? Loc.T(source) : Loc.T(source, args);
        label.text = applied;
    }
}