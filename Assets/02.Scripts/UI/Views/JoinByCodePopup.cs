using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 방 코드를 입력해서 방에 들어가는 창. 로비의 "코드로 입장" 버튼이 연다.
/// 씬에 있는 비밀번호 입력 창(Canvas_Password)을 그대로 쓰고, 제목과 안내 글자, 입력칸 설정은 여기서 바꾼다.
/// (비밀번호 창의 입력칸은 숫자 네 자리를 가리는 설정이라, 방 코드에 맞게 일반 글자 입력으로 바꾼다)
/// </summary>
public class JoinByCodePopup
{
    private const string WindowPath = "PasswordPopup/Window";
    private const int InputLimit = 12; // ABCD-EFGH와 공백까지 넉넉히. 올바른 코드인지는 입장을 누를 때 확인한다

    private readonly PopupView popup;
    private readonly Action<string> onJoin;
    private readonly Action<string> showMessage;

    private TMP_InputField input;
    private Button confirmButton;
    private string lastCode = "";

    /// <param name="popup">방 코드 입력 창으로 쓸 팝업 (Canvas_Password)</param>
    /// <param name="onJoin">올바른 코드를 넣고 입장을 눌렀을 때. 정리된 코드(8자리 대문자)를 받는다. 창은 닫은 뒤에 부른다.</param>
    /// <param name="showMessage">코드가 올바르지 않을 때 보여 줄 안내를 받는다</param>
    public JoinByCodePopup(PopupView popup, Action<string> onJoin, Action<string> showMessage)
    {
        this.popup = popup;
        this.onJoin = onJoin;
        this.showMessage = showMessage ?? (_ => { });
        Bind();
    }

    public bool IsOpen => popup != null && popup.IsOpen;

    private Transform Window => popup.Window != null ? popup.Window : popup.transform.Find(WindowPath);

    private void Bind()
    {
        Transform w = Window;
        if (w == null) { Debug.LogWarning("[JoinByCode] 방 코드 입력 창(Canvas_Password/PasswordPopup/Window)을 찾지 못했습니다"); return; }

        Transform inputTransform = w.Find("PasswordInput");
        input = inputTransform != null ? inputTransform.GetComponent<TMP_InputField>() : null;
        confirmButton = Find<Button>(w, "ConfirmButton");
        Button cancel = Find<Button>(w, "CancelButton");
        Button close = Find<Button>(w, "CloseButton");

        if (input != null)
        {
            // 숫자 네 자리를 가리는 비밀번호 입력칸을 방 코드 입력칸으로 바꾼다
            input.contentType = TMP_InputField.ContentType.Standard;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.characterValidation = TMP_InputField.CharacterValidation.None;
            input.characterLimit = InputLimit;
            input.onValidateInput = (text, index, added) => char.ToUpperInvariant(added); // 소문자로 쳐도 대문자로 들어간다
            input.onSubmit.AddListener(_ => Confirm());
            if (input.placeholder is TMP_Text placeholder) placeholder.text = "ABCD-EFGH";
        }

        if (confirmButton != null) confirmButton.onClick.AddListener(Confirm);
        if (cancel != null) cancel.onClick.AddListener(Close);
        if (close != null) close.onClick.AddListener(Close);
    }

    private static T Find<T>(Transform root, string path) where T : Component
    {
        Transform target = root.Find(path);
        return target != null ? target.GetComponent<T>() : null;
    }

    /// <summary>창을 연다. 지난번에 넣은 코드가 입력칸에 남아 있다. (틀렸을 때 고쳐서 다시 넣기 쉽게)</summary>
    public void Open()
    {
        Transform w = Window;
        if (w != null)
        {
            SetText(w, "TitleText", "방 코드로 입장");
            SetText(w, "RoomNameText", "친구에게 받은 방 코드를 입력해 주세요");
        }

        if (input != null) input.SetTextWithoutNotify(lastCode);
        popup.Open();
        if (input != null) input.ActivateInputField();
    }

    public void Close() => popup.Close();

    private static void SetText(Transform root, string path, string text)
    {
        TMP_Text label = Find<TMP_Text>(root, path);
        if (label != null) label.text = text;
    }

    private void Confirm()
    {
        if (!TryRead(input != null ? input.text : "", out string code, out string message))
        {
            if (Window != null) UIFx.Shake(Window);
            showMessage(message);
            return;
        }

        lastCode = RoomCodeRules.Format(code);
        popup.Close();
        onJoin?.Invoke(code);
    }

    /// <summary>입력한 글자가 방 코드로 쓸 수 있으면 true와 정리된 코드를, 아니면 false와 안내 문장을 돌려준다.</summary>
    public static bool TryRead(string text, out string code, out string message)
    {
        RoomCodeRules.Problem problem = RoomCodeRules.Check(text, out code);
        message = RoomCodeRules.MessageFor(problem);
        return problem == RoomCodeRules.Problem.None;
    }
}