using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>효과음 종류. 파일은 Resources/Audio/Sfx/ 아래 (SoundManager.SfxTable)</summary>
public enum Sfx
{
    Click,      // UI 버튼 (모든 버튼, 자동)
    Toggle,     // UI 스위치·체크 (자동)
    TileClick,  // 보드 칸을 눌러 땅 정보 보기
    Dice,       // 주사위 결과 연출
    Move,       // 말이 한 칸 점프
    MoneyGet,   // 내 현금이 늘었을 때 (자동)
    MoneyPay,   // 내 현금이 줄었을 때 (자동)
    Buy,        // 땅 구매, 인수
    Build,      // 건설
    CardFlip,   // 황금열쇠 카드 창
    TurnStart,  // 내 차례가 됐을 때
    TimerTick,  // 제한시간 막바지 (반복)
    Island,     // 무인도 도착
    Fail,       // 파산
    Win,        // 게임 결과
}

/// <summary>
/// 배경음과 효과음. 첫 씬이 열릴 때 스스로 생겨서(씬에 따로 놓지 않는다) 씬이 바뀌어도 사라지지 않는다.
/// - 배경음: 씬 이름에 맞는 Resources/Audio/Bgm/Bgm_이름 (LoginScene → Bgm_Login)으로 0.6초 동안 바꿔 튼다. 곡이 없는 씬은 지금 곡을 이어서 튼다.
/// - 효과음: SoundManager.Play(Sfx.종류). 파일과 볼륨, 최대 길이는 SfxTable 한 곳에서 정한다.
/// - UI 버튼과 스위치를 누르는 소리, 내 현금이 바뀌는 소리는 따로 부르지 않아도 여기서 알아서 낸다.
/// - 볼륨은 설정 창 값(SettingsView.BgmVolume, SfxVolume)을 따르고, 바꾸면 바로 반영한다. (SettingsView.SoundChanged)
/// - 웹 빌드는 브라우저가 첫 클릭 전에는 소리를 막는다. 재생은 시작해 두고, 첫 클릭 뒤부터 들린다.
/// 시간은 모두 Time.unscaled로 센다. (F1 연출 속도가 DOTween 시간을 바꿔도 소리 길이는 그대로)
/// </summary>
public class SoundManager : MonoBehaviour
{
    // ───────────── 효과음 표 ─────────────
    // file: Resources/Audio/Sfx/ 아래 이름 (확장자 없이), volume: 설정 볼륨에 곱하는 값, maxSeconds: 이 길이에서 짧게 줄이며 끊는다 (0이면 끝까지)
    private struct SfxInfo
    {
        public string file; public float volume; public float maxSeconds;
        public SfxInfo(string file, float volume = 1f, float maxSeconds = 0f) { this.file = file; this.volume = volume; this.maxSeconds = maxSeconds; }
    }

    private static readonly Dictionary<Sfx, SfxInfo> SfxTable = new Dictionary<Sfx, SfxInfo>
    {
        { Sfx.Click,     new SfxInfo("Sfx_Click", 0.8f) },
        { Sfx.Toggle,    new SfxInfo("Sfx_button4", 0.8f) },
        { Sfx.TileClick, new SfxInfo("Sfx_button", 0.7f) },
        { Sfx.Dice,      new SfxInfo("Sfx_Dice") },
        { Sfx.Move,      new SfxInfo("Sfx_Move", 0.6f) },
        { Sfx.MoneyGet,  new SfxInfo("Sfx_MoneyGet") },
        { Sfx.MoneyPay,  new SfxInfo("Sfx_MoneyPay") },
        { Sfx.Buy,       new SfxInfo("Sfx_Buy") },
        { Sfx.Build,     new SfxInfo("Sfx_Build", 1f, 1.5f) },     // 망치질이 9초 이어지는 파일이라 두 번만 치고 끊는다
        { Sfx.CardFlip,  new SfxInfo("Sfx_CardFlip") },
        { Sfx.TurnStart, new SfxInfo("Sfx_TurnStart", 1f, 2.7f) }, // 2.6초 뒤는 무음
        { Sfx.TimerTick, new SfxInfo("Sfx_TimerTick") },           // StartTimerTick으로 반복 재생
        { Sfx.Island,    new SfxInfo("Sfx_island") },
        { Sfx.Fail,      new SfxInfo("Sfx_Fail", 1f, 4f) },        // 8초짜리라 앞부분만
        { Sfx.Win,       new SfxInfo("Sfx_Win", 1f, 7f) },         // 6.7초 뒤는 무음
    };

    private const string BgmFolder = "Audio/Bgm/Bgm_";
    private const string SfxFolder = "Audio/Sfx/";
    private const float BgmFadeSeconds = 0.6f;
    private const float SfxFadeOutSeconds = 0.25f;  // maxSeconds에서 끊을 때 줄이는 시간
    private const float ResultBgmScale = 0.3f;      // 결과 창에서는 배경음을 줄여 결과 소리가 들리게
    private const int SfxVoices = 8;                // 동시에 날 수 있는 효과음 수
    private const float MoneyAfterTradeSeconds = 0.4f; // 구매·건설 소리 직후의 돈 소리는 겹치므로 내지 않는다

    public static SoundManager Instance { get; private set; }

    /// <summary>효과음을 낸다. SoundManager가 없거나 파일이 없으면 아무것도 하지 않는다.</summary>
    public static void Play(Sfx sfx)
    {
        if (Instance != null) Instance.PlaySfx(sfx);
    }

    // ───────────── 생성 ─────────────

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (Instance != null) return;

        var go = new GameObject("SoundManager");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<SoundManager>();
    }

    // 에디터에서 도메인 리로드 없이 플레이를 다시 시작해도 이전 플레이의 값이 남지 않게 한다
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => Instance = null;

    private AudioSource bgmA, bgmB;     // 배경음을 바꿀 때 둘을 엇갈려 쓴다
    private AudioSource bgmCurrent;
    private float bgmScale = 1f;        // 결과 창에서 줄이는 비율
    private Coroutine bgmFade;

    private readonly List<AudioSource> sfxVoices = new List<AudioSource>();
    private readonly Dictionary<AudioSource, Coroutine> sfxStops = new Dictionary<AudioSource, Coroutine>();
    private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
    private AudioSource tickSource;
    private float lastTradeSoundAt = -100f;

    private void Awake()
    {
        bgmA = CreateSource(true);
        bgmB = CreateSource(true);
        for (int i = 0; i < SfxVoices; i++) sfxVoices.Add(CreateSource(false));
        tickSource = CreateSource(true);

        SettingsView.SoundChanged += ApplyVolumes;
        EconomyManager.OnMoneyChanged += OnMoneyChanged;
        SceneManager.sceneLoaded += OnSceneLoaded;

        PlaySceneBgm(SceneManager.GetActiveScene().name); // 이미 열려 있는 첫 씬
    }

    private void OnDestroy()
    {
        SettingsView.SoundChanged -= ApplyVolumes;
        EconomyManager.OnMoneyChanged -= OnMoneyChanged;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (Instance == this) Instance = null;
    }

    private AudioSource CreateSource(bool loop)
    {
        var source = gameObject.AddComponent<AudioSource>();
        source.loop = loop;
        source.playOnAwake = false;
        source.spatialBlend = 0f; // 카메라 위치와 상관없이 같은 크기로 들린다
        return source;
    }

    private AudioClip Load(string path)
    {
        if (clips.TryGetValue(path, out AudioClip clip)) return clip;
        clip = Resources.Load<AudioClip>(path);
        if (clip == null) Debug.LogWarning($"[SoundManager] Resources/{path} 소리 파일이 없습니다.");
        clips[path] = clip; // 없는 것도 기억해서 경고를 한 번만 띄운다
        return clip;
    }

    // ───────────── 배경음 ─────────────

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode != LoadSceneMode.Single) return;
        StopTimerTick();
        bgmScale = 1f; // 결과 창에서 줄였던 배경음을 되돌린다
        PlaySceneBgm(scene.name);
    }

    // "LoginScene" → Resources/Audio/Bgm/Bgm_Login
    private void PlaySceneBgm(string sceneName)
    {
        string name = sceneName.EndsWith("Scene") ? sceneName.Substring(0, sceneName.Length - "Scene".Length) : sceneName;
        AudioClip clip = Resources.Load<AudioClip>(BgmFolder + name);
        if (clip == null)
        {
            ApplyVolumes(); // 곡이 없는 씬은 지금 곡을 이어서 튼다
            return;
        }
        if (bgmCurrent != null && bgmCurrent.clip == clip && bgmCurrent.isPlaying)
        {
            ApplyVolumes(); // 같은 곡이면 처음부터 다시 틀지 않는다
            return;
        }

        AudioSource from = bgmCurrent;
        AudioSource to = bgmCurrent == bgmA ? bgmB : bgmA;
        to.clip = clip;
        to.volume = 0f;
        to.Play();
        bgmCurrent = to;

        if (bgmFade != null) StopCoroutine(bgmFade);
        bgmFade = StartCoroutine(CrossFade(from, to));
    }

    private IEnumerator CrossFade(AudioSource from, AudioSource to)
    {
        float fromStart = from != null ? from.volume : 0f;
        for (float t = 0f; t < BgmFadeSeconds; t += Time.unscaledDeltaTime)
        {
            float k = t / BgmFadeSeconds;
            to.volume = BgmTarget * k;
            if (from != null) from.volume = fromStart * (1f - k);
            yield return null;
        }
        to.volume = BgmTarget;
        if (from != null) { from.Stop(); from.clip = null; }
        bgmFade = null;
    }

    private float BgmTarget => SettingsView.BgmVolume * bgmScale;

    /// <summary>결과 창이 뜰 때 배경음을 줄인다. 다음 씬에서 되돌아간다.</summary>
    public static void DuckBgmForResult()
    {
        if (Instance == null) return;
        Instance.bgmScale = ResultBgmScale;
        Instance.ApplyVolumes();
    }

    // 설정 창의 값(스위치가 꺼져 있으면 0)을 그대로 쓴다. 배경음을 바꾸는 중이면 페이드가 알아서 맞춘다
    private void ApplyVolumes()
    {
        if (bgmFade == null && bgmCurrent != null) bgmCurrent.volume = BgmTarget;
        if (tickSource.isPlaying) tickSource.volume = SfxVolumeOf(Sfx.TimerTick);
    }

    // ───────────── 효과음 ─────────────

    private float SfxVolumeOf(Sfx sfx) => SettingsView.SfxVolume * (SfxTable.TryGetValue(sfx, out SfxInfo info) ? info.volume : 1f);

    private void PlaySfx(Sfx sfx)
    {
        if (!SfxTable.TryGetValue(sfx, out SfxInfo info)) return;
        float volume = SfxVolumeOf(sfx);
        if (volume <= 0f) return;

        AudioClip clip = Load(SfxFolder + info.file);
        if (clip == null) return;

        if (sfx == Sfx.Buy || sfx == Sfx.Build) lastTradeSoundAt = Time.unscaledTime;

        // 쉬고 있는 목소리를 쓴다. 모두 바쁘면 가장 먼저 시작한 소리를 끊는다
        AudioSource voice = sfxVoices.Find(v => !v.isPlaying);
        if (voice == null) { voice = sfxVoices[0]; sfxVoices.RemoveAt(0); sfxVoices.Add(voice); }

        if (sfxStops.TryGetValue(voice, out Coroutine stop) && stop != null) StopCoroutine(stop);
        sfxStops.Remove(voice);

        voice.clip = clip;
        voice.volume = volume;
        voice.Play();
        if (info.maxSeconds > 0f && clip.length > info.maxSeconds)
            sfxStops[voice] = StartCoroutine(StopAfter(voice, info.maxSeconds));
    }

    // maxSeconds가 되기 직전부터 짧게 줄이면서 끊는다
    private IEnumerator StopAfter(AudioSource voice, float seconds)
    {
        float wait = Mathf.Max(0f, seconds - SfxFadeOutSeconds);
        for (float t = 0f; t < wait; t += Time.unscaledDeltaTime) yield return null;

        float start = voice.volume;
        for (float t = 0f; t < SfxFadeOutSeconds; t += Time.unscaledDeltaTime)
        {
            voice.volume = start * (1f - t / SfxFadeOutSeconds);
            yield return null;
        }
        voice.Stop();
        sfxStops.Remove(voice);
    }

    /// <summary>제한시간 막바지의 째깍 소리를 반복해서 튼다. 이미 나고 있으면 그대로 둔다.</summary>
    public static void StartTimerTick()
    {
        if (Instance == null || Instance.tickSource.isPlaying) return;
        AudioClip clip = Instance.Load(SfxFolder + SfxTable[Sfx.TimerTick].file);
        if (clip == null) return;

        Instance.tickSource.clip = clip;
        Instance.tickSource.volume = Instance.SfxVolumeOf(Sfx.TimerTick);
        Instance.tickSource.Play();
    }

    /// <summary>째깍 소리를 멈춘다. (선택을 마쳤거나 시간이 끝났을 때)</summary>
    public static void StopTimerTick()
    {
        if (Instance != null && Instance.tickSource.isPlaying) Instance.tickSource.Stop();
    }

    // 내 현금이 바뀌면 소리를 낸다. 다른 사람의 돈은 소리 없이 숫자만 바뀐다. (게임 씬에서만 불린다)
    private void OnMoneyChanged(long playerId, long before, long after)
    {
        if (playerId != LocalPlayer.Id || before == after) return;
        if (Time.unscaledTime - lastTradeSoundAt < MoneyAfterTradeSeconds) return; // 구매·건설 소리와 겹치지 않게
        PlaySfx(after > before ? Sfx.MoneyGet : Sfx.MoneyPay);
    }

    // ───────────── UI 클릭 (자동) ─────────────
    // 버튼마다 소리를 붙이지 않고, 누른 곳과 뗀 곳이 같은 버튼·스위치면 소리를 낸다. (코드로 만든 버튼도 따로 할 일이 없다)
    // 누르는 순간에 눌릴 수 있는 상태였는지 기억한다. (누르자마자 스스로 잠기는 버튼도 소리가 나게)

    private GameObject pressedTarget;
    private Selectable pressedSelectable;
    private static readonly List<RaycastResult> raycastHits = new List<RaycastResult>();

    private void Update()
    {
        if (EventSystem.current == null) return;

        if (Input.GetMouseButtonDown(0))
        {
            pressedTarget = ClickTargetUnderPointer();
            Selectable selectable = pressedTarget != null ? pressedTarget.GetComponent<Selectable>() : null;
            pressedSelectable = selectable != null && selectable.IsInteractable() ? selectable : null;
        }
        else if (Input.GetMouseButtonUp(0))
        {
            if (pressedSelectable != null && ClickTargetUnderPointer() == pressedTarget)
            {
                if (pressedSelectable is Toggle) PlaySfx(Sfx.Toggle);
                else if (pressedSelectable is Button) PlaySfx(Sfx.Click);
            }
            pressedTarget = null;
            pressedSelectable = null;
        }
    }

    private GameObject ClickTargetUnderPointer()
    {
        var pointer = new PointerEventData(EventSystem.current) { position = Input.mousePosition };
        raycastHits.Clear();
        EventSystem.current.RaycastAll(pointer, raycastHits);
        return raycastHits.Count > 0 ? ExecuteEvents.GetEventHandler<IPointerClickHandler>(raycastHits[0].gameObject) : null;
    }
}