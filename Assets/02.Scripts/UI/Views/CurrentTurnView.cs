using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// "OO 님의 차례" 표시. Canvas_CurrentTurn에 붙는다.
/// 이미 떠 있을 때 차례가 바뀌면 이전 이름이 왼쪽으로 빠지고 새 이름이 오른쪽에서 들어온다.
/// </summary>
public class CurrentTurnView : UIView
{
    public override string PanelPath => "CurrentTurn";

    private const float SlideDistance = 60f;

    [SerializeField] private TMP_Text playerNameText;
    [SerializeField] private Image playerStroke;
    [SerializeField] private Image portraitRing;
    [SerializeField] private Image portraitImage;

    private Sequence swap;
    private Vector2 restPosition;
    private bool hasRestPosition;

    public override void Bind()
    {
        playerNameText = Find<TMP_Text>("CurrentTurn/PlayerNameText");
        playerStroke = Find<Image>("CurrentTurn/PlayerStroke");
        portraitRing = Find<Image>("CurrentTurn/Portrait/Ring");
        portraitImage = Find<Image>("CurrentTurn/Portrait/PortraitMask/PortraitImage");
    }

    public void Show(string playerName, Sprite portrait, int playerColorIndex)
    {
        var panel = Panel;
        if (panel != null && !hasRestPosition)
        {
            restPosition = panel.anchoredPosition;
            hasRestPosition = true;
        }

        // 처음 뜰 때는 기본 열림 연출(튀어나오기)
        if (!IsOpen || panel == null || PanelGroup == null)
        {
            Apply(playerName, portrait, playerColorIndex);
            Open();
            return;
        }

        swap?.Kill();
        panel.anchoredPosition = restPosition;
        swap = DOTween.Sequence().SetUpdate(true).SetLink(gameObject)
            // 이전 차례가 왼쪽으로 빠진다
            .Append(panel.DOAnchorPosX(restPosition.x - SlideDistance, 0.18f).SetEase(Ease.InQuad))
            .Join(PanelGroup.DOFade(0f, 0.18f))
            // 내용을 바꾸고 오른쪽에서 들어온다
            .AppendCallback(() =>
            {
                Apply(playerName, portrait, playerColorIndex);
                panel.anchoredPosition = new Vector2(restPosition.x + SlideDistance, restPosition.y);
            })
            .Append(panel.DOAnchorPosX(restPosition.x, 0.3f).SetEase(Ease.OutBack, 1.4f))
            .Join(PanelGroup.DOFade(1f, 0.2f));
    }

    private void Apply(string playerName, Sprite portrait, int playerColorIndex)
    {
        Color color = UIPalette.PlayerColor(playerColorIndex);
        if (playerNameText != null) playerNameText.text = Loc.TName(playerName);
        if (playerStroke != null) playerStroke.color = color;
        if (portraitRing != null) portraitRing.color = color;
        SetPortrait(portraitImage, portrait, UIPalette.PlayerLightColor(playerColorIndex));
    }
}