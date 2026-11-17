using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class PlayerPawn : MonoBehaviour
{
    [SerializeField] private SpriteRenderer spriteRenderer;
    [SerializeField] private SortingGroup sortingGroup;
    [SerializeField] private int currentTileIndex = -1;
    [SerializeField] private float hopHeight = 0.25f;
    [SerializeField] private float turnFocusScale = 1.2f;
    [SerializeField] private float turnFocusDuration = 0.15f;

    private PlayerSpriteSet spriteSet;
    private PlayerPawnAnimation currentAnimation = PlayerPawnAnimation.Idle;
    private PlayerPawnDirection facing = PlayerPawnDirection.SouthEast;
    private PlayerPawnDirection facingBeforeEmotion;
    private bool restoreFacingAfterEmotion;
    private float animationElapsed;
    private float animationDuration;
    private Vector3 baseVisualScale;
    private Coroutine focusRoutine;

    public int CurrentTileIndex => currentTileIndex;
    public float SadAnimationDuration => spriteSet != null
        ? spriteSet.GetDefaultDuration(PlayerPawnAnimation.Sad)
        : 1.02f;

    private void Awake()
    {
        if (spriteRenderer == null)
            spriteRenderer = GetComponentInChildren<SpriteRenderer>(true);
        if (sortingGroup == null)
            sortingGroup = GetComponent<SortingGroup>();

        if (spriteRenderer != null)
            baseVisualScale = spriteRenderer.transform.localScale;
    }

    private void Update()
    {
        if (spriteSet == null || spriteRenderer == null) return;

        animationElapsed += Time.deltaTime;
        int frameCount = spriteSet.GetFrameCount(currentAnimation);
        float duration = Mathf.Max(0.01f, animationDuration);

        if (currentAnimation == PlayerPawnAnimation.Idle)
        {
            animationElapsed %= duration;
        }
        else if (animationElapsed >= duration)
        {
            if (restoreFacingAfterEmotion)
            {
                facing = facingBeforeEmotion;
                restoreFacingAfterEmotion = false;
            }
            Play(PlayerPawnAnimation.Idle);
            return;
        }

        int frameIndex = Mathf.Min(frameCount - 1, Mathf.FloorToInt(animationElapsed / duration * frameCount));
        Sprite frame = spriteSet.GetFrame(currentAnimation, facing, frameIndex);
        if (frame != null) spriteRenderer.sprite = frame;
    }

    public void SetCharacter(int slotIndex)
    {
        spriteSet = Resources.Load<PlayerSpriteSet>($"Player/player_{slotIndex + 1}");
        if (spriteSet == null)
        {
            Debug.LogError($"[PlayerPawn] player_{slotIndex + 1} SpriteSet을 찾지 못했습니다. Tools/Our Marble/Setup Player Sprites를 실행하세요.", this);
            return;
        }

        spriteRenderer.color = Color.white;
        Play(PlayerPawnAnimation.Idle);
    }

    public void SetTurnFocus(bool focused, bool animate = true)
    {
        if (spriteRenderer == null) return;
        if (baseVisualScale == Vector3.zero)
            baseVisualScale = spriteRenderer.transform.localScale;

        if (focusRoutine != null)
            StopCoroutine(focusRoutine);

        Vector3 targetScale = baseVisualScale * (focused ? turnFocusScale : 1f);
        if (!animate || !gameObject.activeInHierarchy)
        {
            spriteRenderer.transform.localScale = targetScale;
            focusRoutine = null;
            return;
        }

        focusRoutine = StartCoroutine(AnimateFocusScale(targetScale));
    }

    public void PlayCheer() => PlayEmotion(PlayerPawnAnimation.Cheer);
    public void PlaySad() => PlayEmotion(PlayerPawnAnimation.Sad);

    /// <summary>즉시 이동 (초기 배치용)</summary>
    public void Move(int tileIndex, Vector3 targetPosition)
    {
        transform.position = targetPosition;
        currentTileIndex = tileIndex;
    }

    /// <summary>한 칸 점프 이동 (주사위 이동용)</summary>
    public IEnumerator HopTo(int tileIndex, Vector3 targetPosition, float duration)
    {
        SoundManager.Play(Sfx.Move); // 한 칸 뛸 때마다
        Vector3 start = transform.position;
        PlayMovement(targetPosition - start, duration);
        for (float t = 0f; t < 1f; t += Time.deltaTime / duration)
        {
            float height = Mathf.Sin(t * Mathf.PI) * hopHeight;
            transform.position = Vector3.Lerp(start, targetPosition, t) + Vector3.up * height;
            yield return null;
        }
        Move(tileIndex, targetPosition);
    }

    /// <summary>한 칸 미끄러지는 이동 (점프 없음). 타일을 따라 빠르게 이동할 때 한 칸씩 이어서 사용합니다.</summary>
    public IEnumerator GlideTo(int tileIndex, Vector3 targetPosition, float duration)
    {
        Vector3 start = transform.position;
        PlayMovement(targetPosition - start, duration);
        for (float t = 0f; t < 1f; t += Time.deltaTime / duration)
        {
            transform.position = Vector3.Lerp(start, targetPosition, t);
            yield return null;
        }
        Move(tileIndex, targetPosition);
    }

    /// <summary>목적지로 한 번에 미끄러지는 이동 (자유여행, 무인도용)</summary>
    public IEnumerator SlideTo(int tileIndex, Vector3 targetPosition, float duration)
    {
        Vector3 start = transform.position;
        PlayMovement(targetPosition - start, duration);
        for (float t = 0f; t < 1f; t += Time.deltaTime / duration)
        {
            transform.position = Vector3.Lerp(start, targetPosition, Mathf.SmoothStep(0f, 1f, t));
            yield return null;
        }
        Move(tileIndex, targetPosition);
    }
    
    public void SetSpriteColor(Color color)
    {
        spriteRenderer.color = color;
    }

    public void SetSortingOrder(int sortingOrder)
    {
        sortingGroup.sortingOrder = sortingOrder;
    }

    private void PlayMovement(Vector3 delta, float duration)
    {
        restoreFacingAfterEmotion = false;

        if (delta.sqrMagnitude > 0.0001f)
        {
            if (delta.y >= 0f)
                facing = delta.x >= 0f ? PlayerPawnDirection.NorthEast : PlayerPawnDirection.NorthWest;
            else
                facing = delta.x >= 0f ? PlayerPawnDirection.SouthEast : PlayerPawnDirection.SouthWest;
        }

        Play(PlayerPawnAnimation.Hop, duration);
    }

    private void PlayEmotion(PlayerPawnAnimation animation)
    {
        if (!restoreFacingAfterEmotion)
            facingBeforeEmotion = facing;

        facing = PlayerPawnDirection.SouthEast;
        restoreFacingAfterEmotion = true;
        Play(animation);
    }

    private void Play(PlayerPawnAnimation animation, float duration = 0f)
    {
        if (spriteSet == null) return;

        currentAnimation = animation;
        animationElapsed = 0f;
        animationDuration = duration > 0f ? duration : spriteSet.GetDefaultDuration(animation);

        Sprite firstFrame = spriteSet.GetFrame(animation, facing, 0);
        if (firstFrame != null) spriteRenderer.sprite = firstFrame;
    }

    private IEnumerator AnimateFocusScale(Vector3 targetScale)
    {
        Transform visual = spriteRenderer.transform;
        Vector3 startScale = visual.localScale;
        float duration = Mathf.Max(0.01f, turnFocusDuration);

        for (float t = 0f; t < 1f; t += Time.deltaTime / duration)
        {
            visual.localScale = Vector3.LerpUnclamped(startScale, targetScale, Mathf.SmoothStep(0f, 1f, t));
            yield return null;
        }

        visual.localScale = targetScale;
        focusRoutine = null;
    }
}