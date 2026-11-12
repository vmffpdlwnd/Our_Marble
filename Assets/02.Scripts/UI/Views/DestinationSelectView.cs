using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 목적지 선택 안내 배너. Canvas_DestinationSelect에 붙는다.
/// 칸 선택은 보드에서 하고, 선택되면 Close를 부른다.
/// </summary>
public class DestinationSelectView : UIView
{
    public override string PanelPath => "DestinationSelectPopup/Window";

    [SerializeField] private TMP_Text titleText;
    [SerializeField] private TMP_Text hintText;
    [SerializeField] private Button completeButton;   // "여행지 선택 완료"

    public override void Bind()
    {
        titleText = Find<TMP_Text>("DestinationSelectPopup/Window/TitleText");
        hintText = Find<TMP_Text>("DestinationSelectPopup/Window/HintText");
        completeButton = Find<Button>("DestinationSelectPopup/Window/CompleteButton");
    }

    /// <summary>창을 연다. 완료 버튼은 꺼진 채로 시작하고, 칸이 하나 골라지면 SetCompleteInteractable(true)로 켠다.</summary>
    public void Show(Action onComplete, string title = "세계여행", string hint = "보드에서 이동할 칸을 선택하세요")
    {
        if (titleText != null) titleText.text = Loc.T(title);
        if (hintText != null) hintText.text = Loc.T(hint);
        SetOnClick(completeButton, () => onComplete?.Invoke());
        SetCompleteInteractable(false);
        Open();
    }

    public void SetCompleteInteractable(bool interactable)
    {
        if (completeButton != null) completeButton.interactable = interactable;
    }
}