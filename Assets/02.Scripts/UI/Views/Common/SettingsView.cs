using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 설정 팝업. Canvas_Settings에 붙는다. 로비와 게임 씬이 같은 프리팹을 쓴다.
/// 창 아래의 닫기 버튼으로만 닫는다. (배경을 누르거나 Esc를 눌러도, 설정 버튼을 다시 눌러도 닫히지 않는다.)
/// 값은 PlayerPrefs에 저장만 하고, 실제 오디오 연결은 사운드 시스템이 생기면 아래 정적 값을 읽어 쓰면 된다.
/// </summary>
public class SettingsView : UIView
{
    public override string PanelPath => "SettingsPopup/Window";
    public override string DimPath => "SettingsPopup/Dim";

    private const string BgmKey = "Settings.Bgm";
    private const string SfxKey = "Settings.Sfx";
    private const string BgmOnKey = "Settings.BgmOn";
    private const string SfxOnKey = "Settings.SfxOn";

    private static readonly Color SwitchOn = UIPalette.Mint;
    private static readonly Color SwitchOff = new Color32(205, 210, 224, 255);

    private const float RowStep = 112f;            // 설정 한 줄(96)과 줄 사이 간격(16). 배경음과 효과음 줄 사이 간격과 같다.
    private const float LanguageButtonWidth = 180f;

    /// <summary>스위치와 슬라이더 한 줄(배경음 또는 효과음).</summary>
    [System.Serializable]
    private class Channel
    {
        public Slider slider;
        public TMP_Text valueText;
        public Toggle toggle;
        public Image switchTrack;
        public RectTransform switchKnob;
        public CanvasGroup sliderGroup;
        public CanvasGroup valueGroup;
    }

    [SerializeField] private Channel bgm = new Channel();
    [SerializeField] private Channel sfx = new Channel();
    [SerializeField] private Button confirmButton; // 창 아래의 닫기 버튼 (오브젝트 이름은 ConfirmButton)

    /// <summary>소리 설정. 사운드 시스템이 읽어 가는 값이다. 꺼져 있으면 볼륨은 0으로 본다.</summary>
    public static float BgmVolume => PlayerPrefs.GetInt(BgmOnKey, 1) == 1 ? PlayerPrefs.GetFloat(BgmKey, 0.7f) : 0f;
    public static float SfxVolume => PlayerPrefs.GetInt(SfxOnKey, 1) == 1 ? PlayerPrefs.GetFloat(SfxKey, 0.7f) : 0f;

    public override void Bind()
    {
        const string w = "SettingsPopup/Window/";
        BindChannel(bgm, w + "BgmSection/", "BgmSlider", "BgmToggle");
        BindChannel(sfx, w + "SfxSection/", "SfxSlider", "SfxToggle");
        confirmButton = Find<Button>(w + "ConfirmButton");
    }

    private void BindChannel(Channel c, string section, string sliderName, string toggleName)
    {
        c.slider = Find<Slider>(section + sliderName);
        c.valueText = Find<TMP_Text>(section + "ValueChip/ValueText");
        c.toggle = Find<Toggle>(section + toggleName);
        c.switchTrack = c.toggle != null ? c.toggle.GetComponent<Image>() : null;
        c.switchKnob = c.toggle != null ? c.toggle.transform.Find("Knob") as RectTransform : null;
        c.sliderGroup = c.slider != null ? UIFx.EnsureGroup(c.slider.gameObject) : null;
        var chip = transform.Find(section + "ValueChip");
        c.valueGroup = chip != null ? UIFx.EnsureGroup(chip.gameObject) : null;
    }

    private void Awake()
    {
        SetupChannel(bgm, BgmKey, BgmOnKey);
        SetupChannel(sfx, SfxKey, SfxOnKey);
        SetOnClick(confirmButton, Close);
        BuildLanguageSection();
    }

    private void SetupChannel(Channel c, string volumeKey, string onKey)
    {
        if (c.slider == null || c.toggle == null) return;

        c.slider.SetValueWithoutNotify(PlayerPrefs.GetFloat(volumeKey, 0.7f));
        UpdateValueText(c);
        c.slider.onValueChanged.RemoveAllListeners();
        c.slider.onValueChanged.AddListener(v =>
        {
            PlayerPrefs.SetFloat(volumeKey, v);
            UpdateValueText(c);
        });

        c.toggle.SetIsOnWithoutNotify(PlayerPrefs.GetInt(onKey, 1) == 1);
        ApplySwitch(c, c.toggle.isOn, false);
        c.toggle.onValueChanged.RemoveAllListeners();
        c.toggle.onValueChanged.AddListener(on =>
        {
            PlayerPrefs.SetInt(onKey, on ? 1 : 0);
            ApplySwitch(c, on, true);
        });
    }

    private static void UpdateValueText(Channel c)
    {
        if (c.valueText != null) c.valueText.text = $"{Mathf.RoundToInt(c.slider.value * 100)}%";
    }

    /// <summary>스위치를 켜면 소리가 나고, 끄면 음소거 + 슬라이더와 값이 흐려져 못 만진다.</summary>
    private static void ApplySwitch(Channel c, bool on, bool animate)
    {
        if (c.switchTrack != null) c.switchTrack.color = on ? SwitchOn : SwitchOff;
        if (c.switchKnob != null)
        {
            float knobX = on ? 46f : 18f;
            c.switchKnob.DOKill();
            if (animate)
                c.switchKnob.DOAnchorPosX(knobX, 0.15f).SetEase(Ease.OutQuad).SetUpdate(true).SetLink(c.switchKnob.gameObject);
            else
                c.switchKnob.anchoredPosition = new Vector2(knobX, c.switchKnob.anchoredPosition.y);
        }
        foreach (var group in new[] { c.sliderGroup, c.valueGroup })
        {
            if (group == null) continue;
            group.interactable = on;
            group.alpha = on ? 1f : 0.45f;
        }
    }

    // ───────────── 언어 선택 줄 ─────────────
    // 프리팹을 고치지 않고 코드로 만든다. 효과음 줄을 복사해 같은 배경 모양을 쓰고, 그 아래에 한 줄을 더한다.

    private Button koreanButton, englishButton;

    private void BuildLanguageSection()
    {
        var window = transform.Find("SettingsPopup/Window") as RectTransform;
        var sfxSection = window != null ? window.Find("SfxSection") as RectTransform : null;
        if (window == null || sfxSection == null || confirmButton == null) return;
        if (window.Find("LanguageSection") != null) return; // 이미 만들었다

        // 효과음 줄을 복사하고 슬라이더, 스위치, 값 칸은 뺀다
        var section = Instantiate(sfxSection, window);
        section.name = "LanguageSection";
        section.anchoredPosition = sfxSection.anchoredPosition + new Vector2(0f, -RowStep);
        foreach (string child in new[] { "SfxSlider", "SfxToggle", "ValueChip" })
        {
            var t = section.Find(child);
            if (t == null) continue;
            t.gameObject.SetActive(false);
            Destroy(t.gameObject);
        }

        // 줄이 하나 늘어난 만큼 창을 키운다. (닫기 버튼은 창 아래에 붙어 있어서 같이 내려간다)
        window.sizeDelta += new Vector2(0f, RowStep);

        var labelTransform = section.Find("Label");
        if (labelTransform != null) Loc.Bind(labelTransform.GetComponent<TMP_Text>(), "언어");

        englishButton = MakeLanguageButton(section, "EnglishButton", "English", -24f);
        koreanButton = MakeLanguageButton(section, "KoreanButton", "한국어", -24f - LanguageButtonWidth - 12f);
        koreanButton.onClick.AddListener(() => ChooseLanguage(GameLanguage.Korean));
        englishButton.onClick.AddListener(() => ChooseLanguage(GameLanguage.English));
        RefreshLanguageButtons();
    }

    // 닫기 버튼을 복사해서 같은 모양의 작은 버튼을 만든다
    private Button MakeLanguageButton(RectTransform parent, string objectName, string caption, float x)
    {
        var button = Instantiate(confirmButton, parent);
        button.name = objectName;
        button.onClick.RemoveAllListeners();

        var rect = (RectTransform)button.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(1f, 0.5f);
        rect.pivot = new Vector2(1f, 0.5f);
        rect.sizeDelta = new Vector2(LanguageButtonWidth, 56f);
        rect.anchoredPosition = new Vector2(x, 0f);

        var label = button.GetComponentInChildren<TMP_Text>(true);
        if (label != null) label.text = caption; // 언어 이름은 번역하지 않는다
        return button;
    }

    private void ChooseLanguage(GameLanguage language)
    {
        Loc.SetLanguage(language);
        RefreshLanguageButtons();
    }

    // 지금 쓰는 언어의 버튼을 강조한다
    private void RefreshLanguageButtons()
    {
        bool korean = Loc.Language == GameLanguage.Korean;
        Paint(koreanButton, korean);
        Paint(englishButton, !korean);
    }

    private static void Paint(Button button, bool selected)
    {
        if (button != null && button.targetGraphic != null) button.targetGraphic.color = selected ? SwitchOn : SwitchOff;
    }
}