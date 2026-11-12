using System;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>상단바: 방 정보, 라운드 수, 파산 신청·설정·나가기 버튼. Canvas_TopBar에 붙는다.</summary>
public class TopBarView : UIView
{
    [SerializeField] private TMP_Text roomNameText;
    [SerializeField] private TMP_Text playerCountText;
    [SerializeField] private TMP_Text turnText;
    [SerializeField] private Button settingsButton;
    [SerializeField] private Button exitButton;
    [SerializeField] private GameObject passwordChip;
    [SerializeField] private TMP_Text passwordText;
    [SerializeField] private GameObject turnInfo;

    private const float BankruptButtonWidth = 140f; // 나가기 버튼(116)보다 넓게. 영어 "Bankrupt"가 들어가야 한다
    private Button bankruptButton; // 파산 신청 버튼. 프리팹에 없고 나가기 버튼을 복사해 코드로 만든다

    public override void Bind()
    {
        roomNameText = Find<TMP_Text>("TopBar/RoomInfo/RoomNameText");
        playerCountText = Find<TMP_Text>("TopBar/RoomInfo/PlayerCountChip/PlayerCountText");
        passwordChip = FindObject("TopBar/RoomInfo/PasswordChip");
        passwordText = Find<TMP_Text>("TopBar/RoomInfo/PasswordChip/PasswordText");
        turnInfo = FindObject("TopBar/TurnInfo");
        turnText = Find<TMP_Text>("TopBar/TurnInfo/TurnText");
        settingsButton = Find<Button>("TopBar/Buttons/SettingsButton");
        exitButton = Find<Button>("TopBar/Buttons/ExitButton");
    }

    /// <summary>방 이름을 보여 준다.</summary>
    public void SetRoom(string roomName)
    {
        if (roomNameText != null) roomNameText.text = roomName;
    }

    /// <summary>"현재 인원 / 최대 인원"을 보여 준다. 숫자만 있어서 번역하지 않는다. (한국어와 영어 모양을 같게 맞췄다)</summary>
    public void SetPlayerCount(int current, int max)
    {
        if (playerCountText != null) playerCountText.text = $"{current} / {max}";
    }

    /// <summary>비밀번호 방이면 인원수 옆에 비밀번호를 보여준다. 비어 있으면 칩을 숨긴다.</summary>
    public void SetPassword(string password)
    {
        bool has = !string.IsNullOrEmpty(password);
        SetActive(passwordChip, has);
        if (has && passwordText != null) passwordText.text = Loc.T("비밀번호  {0}", password);
    }

    /// <summary>라운드 표시를 보일지. 게임이 시작되기 전에는 숨긴다.</summary>
    public void SetRoundVisible(bool visible) => SetActive(turnInfo, visible);

    /// <summary>현재 라운드 / 최대 라운드를 표시한다. animate면 라운드가 바뀔 때 숫자 칸이 톡 튄다. (turnText는 프리팹 연결용 이름이라 그대로 둔다)</summary>
    public void SetRound(int round, int maxRound, bool animate = false)
    {
        bool changed = round != shownRound;
        shownRound = round;
        if (turnText != null) turnText.text = $"{round} <size=70%><color=#C9A770>/ {maxRound}</color></size>";
        // 라운드가 바뀌었을 때만 숫자 칸이 톡 튄다
        if (animate && changed && turnInfo != null && turnInfo.activeInHierarchy)
        {
            turnInfo.transform.DOKill(true);
            turnInfo.transform.DOPunchScale(Vector3.one * 0.15f, 0.4f, 6, 0.6f).SetUpdate(true).SetLink(turnInfo);
        }
    }

    private int shownRound = -1;

    /// <summary>설정·나가기 버튼의 클릭 동작을 정한다. (부를 때마다 이전 동작을 바꾼다)</summary>
    public void SetCallbacks(Action onSettings, Action onExit)
    {
        SetOnClick(settingsButton, onSettings);
        SetOnClick(exitButton, onExit);
    }

    /// <summary>
    /// 파산 신청 버튼을 만들고 누를 때의 동작을 정한다. 게임 화면(UIManager)에서만 부른다. 이미 만들었으면 동작만 바꾼다.
    /// 프리팹을 고치지 않으려고 나가기 버튼을 복사해 버튼 줄 맨 왼쪽(설정 버튼 왼쪽)에 둔다. (설정창의 언어 줄처럼 코드로 만든다)
    /// 처음에는 숨겨 두고, 보일지와 누를 수 있는지는 SetBankruptState로 정한다.
    /// </summary>
    public void SetBankruptButton(Action onClick)
    {
        if (bankruptButton == null)
        {
            if (exitButton == null) return;

            bankruptButton = Instantiate(exitButton, exitButton.transform.parent);
            bankruptButton.name = "BankruptButton";
            bankruptButton.transform.SetSiblingIndex(0);

            var layout = bankruptButton.GetComponent<LayoutElement>();
            if (layout != null) layout.preferredWidth = BankruptButtonWidth;

            Loc.Bind(bankruptButton.GetComponentInChildren<TMP_Text>(true), "파산 신청");
            bankruptButton.gameObject.SetActive(false);
        }
        SetOnClick(bankruptButton, onClick);
    }

    /// <summary>
    /// 파산 신청 버튼의 상태. visible이 false면 숨긴다. (파산했거나 게임이 끝남)
    /// interactable이 false면 흐리게 잠근다. (내 차례가 아니거나 입력을 기다리는 중이 아님)
    /// </summary>
    public void SetBankruptState(bool visible, bool interactable)
    {
        if (bankruptButton == null) return;
        if (bankruptButton.gameObject.activeSelf != visible) bankruptButton.gameObject.SetActive(visible);
        if (bankruptButton.interactable != interactable) bankruptButton.interactable = interactable;
    }
}