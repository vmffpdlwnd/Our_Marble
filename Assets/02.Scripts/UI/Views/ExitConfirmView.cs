using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>나가기 확인 팝업. 게임에서는 로비로, 로비에서는 방 밖이나 로그인 화면으로 나갈지 묻는다. 문구는 부르는 쪽이 정한다. Canvas_ExitConfirm에 붙는다.</summary>
public class ExitConfirmView : UIView
{
    public override string PanelPath => "ExitConfirmPopup/Window";
    public override string DimPath => "ExitConfirmPopup/Dim";

    [SerializeField] private Button confirmButton;
    [SerializeField] private Button cancelButton;
    [SerializeField] private TMP_Text messageText;

    private string defaultMessage, defaultConfirmLabel, defaultCancelLabel;
    private bool defaultsCached;

    public override void Bind()
    {
        const string w = "ExitConfirmPopup/Window/";
        confirmButton = Find<Button>(w + "ConfirmButton");
        cancelButton = Find<Button>(w + "CancelButton");
        messageText = Find<TMP_Text>(w + "MessageText");
    }

    /// <param name="onConfirm">나가기를 눌렀을 때. 창을 닫은 뒤 호출된다.</param>
    /// <param name="message">안내 문장. 비우면 프리팹에 적힌 문장을 쓴다.</param>
    /// <param name="confirmLabel">확인 버튼 글자. 비우면 프리팹에 적힌 글자(나가기)를 쓴다.</param>
    /// <param name="cancelLabel">취소 버튼 글자. 비우면 프리팹에 적힌 글자를 쓴다.</param>
    public void Show(Action onConfirm, string message = null, string confirmLabel = null, string cancelLabel = null)
    {
        // 이미 만들어진 프리팹에는 이 칸이 연결돼 있지 않을 수 있어 처음 쓸 때 찾는다
        if (messageText == null) messageText = Find<TMP_Text>("ExitConfirmPopup/Window/MessageText");
        var confirmText = confirmButton != null ? confirmButton.GetComponentInChildren<TMP_Text>(true) : null;
        var cancelText = cancelButton != null ? cancelButton.GetComponentInChildren<TMP_Text>(true) : null;
        if (!defaultsCached)
        {
            defaultMessage = SourceOf(messageText);
            defaultConfirmLabel = SourceOf(confirmText);
            defaultCancelLabel = SourceOf(cancelText);
            defaultsCached = true;
        }
        // 이 창은 여러 곳에서 같이 쓰므로, 부를 때마다 문구를 새로 정한다 (이전에 바꾼 문구가 남지 않게)
        Loc.Bind(messageText, message ?? defaultMessage);
        Loc.Bind(confirmText, confirmLabel ?? defaultConfirmLabel);
        Loc.Bind(cancelText, cancelLabel ?? defaultCancelLabel);
        SetOnClick(confirmButton, () => { Close(); onConfirm?.Invoke(); });
        SetOnClick(cancelButton, Close);
        Open();
    }

    // 프리팹에 적힌 글자의 한국어 원문. 영어로 바뀐 뒤에 처음 열려도 한국어 원문을 기억해 두기 위해서다
    private static string SourceOf(TMP_Text label)
    {
        if (label == null) return null;
        var bound = label.GetComponent<LocalizedText>();
        return bound != null && bound.HasSource ? bound.Source : label.text;
    }
}