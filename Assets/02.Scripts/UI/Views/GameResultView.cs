using System;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Random = UnityEngine.Random;

/// <summary>게임 결과 팝업. Canvas_GameResult에 붙는다.</summary>
public class GameResultView : UIView
{
    public override string PanelPath => "GameResultPopup/Window";
    public override string DimPath => "GameResultPopup/Dim";

    /// <summary>결과 카드 1장에 들어갈 정보. 등수 순으로 넘긴다.</summary>
    public struct Entry
    {
        public string Name;
        public Sprite Portrait;
        public int ColorIndex;
        public long FinalAsset;   // 파산이면 못 낸 금액만큼 음수 (금액을 모르면 0)
        public bool IsWinner;
        public bool IsBankrupt;
    }

    private static readonly Color WinBadgeBg = new Color32(255, 224, 138, 255);
    private static readonly Color WinBadgeText = new Color32(154, 106, 16, 255);
    private static readonly Color LoseBadgeBg = new Color32(238, 241, 247, 255);
    private static readonly Color BankruptBadgeBg = new Color32(255, 211, 216, 255);
    private static readonly Color BankruptBadgeText = new Color32(214, 90, 110, 255);

    [Serializable]
    private class CardRefs
    {
        public GameObject root;
        public Image background;
        public Image stroke;
        public Image resultBadge;
        public TMP_Text resultText;
        public GameObject[] badgeStars;
        public Image portraitImage;
        public Image portraitRing;
        public GameObject bankruptDim;
        public TMP_Text rankText;
        public TMP_Text nicknameText;
        public TMP_Text assetText;
    }

    [SerializeField] private CardRefs[] cards = new CardRefs[4];
    [SerializeField] private Button closeButton;

    public override void Bind()
    {
        for (int i = 0; i < 4; i++)
        {
            string c = $"GameResultPopup/Window/ResultCards/ResultCard_{i + 1}/";
            var refs = new CardRefs
            {
                root = FindObject(c.TrimEnd('/')),
                background = Find<Image>(c.TrimEnd('/')),
                stroke = Find<Image>(c + "Stroke"),
                resultBadge = Find<Image>(c + "ResultBadge"),
                resultText = Find<TMP_Text>(c + "ResultBadge/Text"),
                portraitImage = Find<Image>(c + "Portrait/PortraitMask/PortraitImage"),
                portraitRing = Find<Image>(c + "Portrait/Ring"),
                bankruptDim = FindObject(c + "Portrait/PortraitMask/BankruptDim"),
                rankText = Find<TMP_Text>(c + "Portrait/RankBadge/Text"),
                nicknameText = Find<TMP_Text>(c + "NicknameText"),
                assetText = Find<TMP_Text>(c + "AssetText"),
            };
            // 승리 별은 첫 카드에만 만들어져 있다
            var stars = new List<GameObject>();
            foreach (var s in new[] { "Star_L", "Star_R" })
            {
                var t = transform.Find(c + "ResultBadge/" + s);
                if (t != null) stars.Add(t.gameObject);
            }
            refs.badgeStars = stars.ToArray();
            cards[i] = refs;
        }
        closeButton = Find<Button>("GameResultPopup/Window/CloseButton");
    }

    /// <summary>entries를 등수 순으로 채운다. 남는 카드는 숨긴다.</summary>
    public void Show(IList<Entry> entries, Action onClose)
    {
        // 금액이 굴러가려면 텍스트가 켜져 있어야 해서 먼저 연다
        Open();
        for (int i = 0; i < cards.Length; i++)
        {
            var refs = cards[i];
            if (refs == null) continue;
            bool used = entries != null && i < entries.Count;
            SetActive(refs.root, used);
            if (!used) continue;

            var e = entries[i];
            Color playerColor = UIPalette.PlayerColor(e.ColorIndex);

            if (refs.background != null) refs.background.color = UIPalette.PlayerLightColor(e.ColorIndex);
            if (refs.stroke != null) refs.stroke.color = e.IsWinner ? UIPalette.Gold : playerColor;
            if (refs.portraitRing != null) refs.portraitRing.color = e.IsWinner ? UIPalette.Gold : playerColor;

            if (refs.resultBadge != null)
                refs.resultBadge.color = e.IsWinner ? WinBadgeBg : e.IsBankrupt ? BankruptBadgeBg : LoseBadgeBg;
            if (refs.resultText != null)
            {
                refs.resultText.text = Loc.T(e.IsWinner ? "승리" : e.IsBankrupt ? "파산" : "패배");
                refs.resultText.color = e.IsWinner ? WinBadgeText : e.IsBankrupt ? BankruptBadgeText : UIPalette.InkSub;
            }
            foreach (var star in refs.badgeStars) SetActive(star, e.IsWinner);

            SetPortrait(refs.portraitImage, e.Portrait, Color.white);
            SetActive(refs.bankruptDim, e.IsBankrupt);
            if (refs.rankText != null) refs.rankText.text = Loc.Rank(i + 1, 65);
            if (refs.nicknameText != null) refs.nicknameText.text = Loc.TName(e.Name);
            if (refs.assetText != null)
            {
                // 0에서 최종 자산까지, 카드마다 조금씩 늦게 굴린다
                refs.assetText.color = e.FinalAsset < 0 ? UIPalette.Red : UIPalette.Ink;
                var roll = new RollingNumber(refs.assetText, v => v < 0 ? UIPalette.SignedMoney(v) : UIPalette.Money(v));
                roll.Set(0, animate: false);
                roll.Set(e.FinalAsset, animate: true, delay: CardDelay(i) + 0.2f, duration: 0.9f);
            }
        }

        SetCountdown(0);
        SetOnClick(closeButton, () => { Close(); onClose?.Invoke(); });
        PlayEntrance(entries);
    }

    private TMP_Text closeLabel;
    private string closeLabelBase;

    /// <summary>확인 버튼에 남은 시간을 붙여 "확인 (12)"처럼 보여 준다. 0이면 원래 글자로 되돌린다.</summary>
    public void SetCountdown(int seconds)
    {
        if (closeButton == null) return;
        if (closeLabel == null)
        {
            closeLabel = closeButton.GetComponentInChildren<TMP_Text>(true);
            if (closeLabel == null) return;
            closeLabelBase = closeLabel.text;
        }
        closeLabel.text = seconds > 0 ? $"{closeLabelBase} ({seconds})" : closeLabelBase;
    }

    // ───────────── 등장 연출 ─────────────

    private const int ConfettiCount = 40;
    private Sequence entrance;
    private RectTransform confettiLayer;

    private static float CardDelay(int index) => 0.15f + index * 0.12f;

    /// <summary>카드가 1위부터 차례로 튀어나오고, 우승자 카드가 나오면 금빛으로 튄 뒤 꽃가루가 터진다.</summary>
    private void PlayEntrance(IList<Entry> entries)
    {
        entrance?.Kill();
        entrance = DOTween.Sequence().SetUpdate(true).SetLink(gameObject);

        for (int i = 0; i < cards.Length; i++)
        {
            var refs = cards[i];
            if (refs == null || refs.root == null || !refs.root.activeSelf) continue;

            var card = refs.root.transform;
            var group = refs.root.GetComponent<CanvasGroup>();
            if (group == null) group = refs.root.AddComponent<CanvasGroup>();
            card.localScale = Vector3.one * 0.6f;
            group.alpha = 0f;

            float at = CardDelay(i);
            entrance.Insert(at, card.DOScale(1f, 0.4f).SetEase(Ease.OutBack, 1.8f));
            entrance.Insert(at, group.DOFade(1f, 0.2f));

            bool winner = entries != null && i < entries.Count && entries[i].IsWinner;
            if (!winner) continue;

            // 우승자: 한 번 더 튀고 배지 별이 돌며, 꽃가루가 터진다
            float shine = at + 0.4f;
            entrance.Insert(shine, card.DOPunchScale(Vector3.one * 0.08f, 0.35f, 5, 0.6f));
            foreach (var star in refs.badgeStars)
            {
                if (star == null) continue;
                entrance.Insert(shine, star.transform.DOLocalRotate(new Vector3(0f, 0f, -360f), 0.6f, RotateMode.FastBeyond360).SetEase(Ease.OutCubic));
                entrance.Insert(shine, star.transform.DOPunchScale(Vector3.one * 0.5f, 0.5f, 5, 0.6f));
            }
            var burstFrom = (RectTransform)card;
            var shapes = ConfettiSprites(refs);
            entrance.InsertCallback(shine, () => BurstConfetti(burstFrom, shapes));
        }
    }

    // 꽃가루 모양: 둥근 사각형(배지 스프라이트)과 별(승리 별 스프라이트)
    private static Sprite[] ConfettiSprites(CardRefs refs)
    {
        var list = new List<Sprite>();
        if (refs.resultBadge != null && refs.resultBadge.sprite != null) list.Add(refs.resultBadge.sprite);
        foreach (var star in refs.badgeStars)
            if (star != null && star.TryGetComponent(out Image image) && image.sprite != null) { list.Add(image.sprite); break; }
        return list.ToArray();
    }

    private void BurstConfetti(RectTransform origin, Sprite[] shapes)
    {
        var layer = GetConfettiLayer();
        Vector2 start = layer.InverseTransformPoint(origin.TransformPoint(new Vector3(0f, origin.rect.yMax - 40f, 0f)));
        Color[] colors = { UIPalette.Gold, UIPalette.Player[0], UIPalette.Player[1], UIPalette.Player[2], UIPalette.Player[3] };
        const float gravity = -1500f;

        for (int i = 0; i < ConfettiCount; i++)
        {
            var go = new GameObject("Confetti", typeof(RectTransform));
            go.layer = gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(layer, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);

            var image = go.AddComponent<Image>();
            image.raycastTarget = false;
            image.color = colors[Random.Range(0, colors.Length)];
            bool star = shapes.Length > 1 && Random.value < 0.3f;
            if (shapes.Length > 0)
            {
                image.sprite = star ? shapes[1] : shapes[0];
                image.type = star ? Image.Type.Simple : Image.Type.Sliced;
                image.pixelsPerUnitMultiplier = 6f;
            }
            rt.sizeDelta = star ? new Vector2(20f, 20f) : new Vector2(Random.Range(8f, 12f), Random.Range(14f, 20f));
            rt.anchoredPosition = start;

            // 위쪽 부채꼴로 튀어올랐다가 중력으로 떨어진다
            float angle = Random.Range(35f, 145f) * Mathf.Deg2Rad;
            float speed = Random.Range(520f, 900f);
            var velocity = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * speed;
            float spin = Random.Range(-720f, 720f);
            float life = Random.Range(1.3f, 1.8f);
            float t = 0f;

            DOTween.To(() => t, v =>
            {
                t = v;
                rt.anchoredPosition = start + velocity * v + 0.5f * gravity * v * v * Vector2.up;
                rt.localRotation = Quaternion.Euler(0f, 0f, spin * v);
                var c = image.color;
                c.a = v < life * 0.65f ? 1f : Mathf.Lerp(1f, 0f, (v - life * 0.65f) / (life * 0.35f));
                image.color = c;
            }, life, life).SetEase(Ease.Linear).SetUpdate(true).SetLink(go)
              .OnComplete(() => Destroy(go));
        }
    }

    private RectTransform GetConfettiLayer()
    {
        if (confettiLayer != null) return confettiLayer;
        var go = new GameObject("ConfettiLayer", typeof(RectTransform));
        go.layer = gameObject.layer;
        confettiLayer = (RectTransform)go.transform;
        var popup = transform.Find("GameResultPopup");
        confettiLayer.SetParent(popup != null ? popup : transform, false);
        confettiLayer.SetAsLastSibling();
        confettiLayer.anchorMin = Vector2.zero;
        confettiLayer.anchorMax = Vector2.one;
        confettiLayer.offsetMin = confettiLayer.offsetMax = Vector2.zero;
        // 움직이는 동안 결과창이 다시 그려지지 않게 하위 캔버스로 둔다
        go.AddComponent<Canvas>();
        return confettiLayer;
    }
}