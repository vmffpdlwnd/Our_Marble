using System;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 내 프로필을 PlayFab에 저장하는 쪽. 지금은 닉네임을 PlayFab의 "표시 이름"으로 저장한다.
///
/// PlayFab의 표시 이름은 3~25자이고 타이틀 안에서 유일해야 한다. 그런데 이 게임은 닉네임이 한두 글자일 수도 있고
/// 같은 닉네임을 쓰는 사람이 여럿일 수도 있다. 그래서 표시 이름은 "닉네임#계정ID 끝 몇 글자"로 만들어 저장하고
/// (예: 철수#C2A9), 화면에는 # 앞의 닉네임만 보여 준다. 같은 이름이 이미 있으면 뒤의 글자를 늘려서 다시 해 본다.
/// </summary>
public class PlayFabProfile
{
    public const int MaxDisplayNameLength = 25;
    private const int FirstSuffixLength = 4;
    private const int LastSuffixLength = 8;
    private const string SyncedKeyPrefix = "PlayFab.DisplayNameFor.";

    private readonly PlayFabIdentity identity;
    private readonly Action<string> log;
    private Task<bool> syncTask;
    private string syncingNickname;

    /// <summary>PlayFab에 저장된 내 표시 이름 (닉네임#끝글자). 아직 저장하지 못했으면 null</summary>
    public string DisplayName { get; private set; }

    public event Action Changed;

    public PlayFabProfile(PlayFabIdentity identity, Action<string> log)
    {
        this.identity = identity;
        this.log = log ?? (_ => { });
    }

    // ───────────── 표시 이름 만들기와 닉네임 꺼내기 ─────────────

    /// <summary>닉네임과 계정 ID로 PlayFab 표시 이름을 만든다. 길이가 넘으면 닉네임을 자른다.</summary>
    public static string Compose(string nickname, string playFabId, int suffixLength)
    {
        string id = (playFabId ?? "").ToUpperInvariant();
        string tail = id.Substring(Math.Max(0, id.Length - suffixLength));
        string suffix = "#" + tail;

        string name = (nickname ?? "").Trim();
        int room = MaxDisplayNameLength - suffix.Length;
        if (name.Length > room) name = name.Substring(0, room);
        return name + suffix;
    }

    /// <summary>표시 이름에서 닉네임만 꺼낸다. (철수#C2A9 → 철수) #이 없으면 그대로 돌려준다.</summary>
    public static string ExtractNickname(string displayName)
    {
        if (string.IsNullOrEmpty(displayName)) return displayName;

        int index = displayName.LastIndexOf('#');
        return index > 0 ? displayName.Substring(0, index) : displayName;
    }

    // ───────────── 저장 ─────────────

    /// <summary>
    /// 닉네임을 PlayFab 표시 이름으로 저장한다. 이미 같은 닉네임으로 저장해 뒀으면 아무것도 하지 않는다.
    /// 실패해도 게임에는 영향이 없고, 다음 접속 때 다시 해 본다. (같은 닉네임으로 동시에 불러도 한 번만 한다)
    /// </summary>
    public Task<bool> SyncNicknameAsync(string nickname)
    {
        if (string.IsNullOrWhiteSpace(nickname)) return Task.FromResult(false);

        if (syncTask != null && !syncTask.IsCompleted && syncingNickname == nickname) return syncTask;

        syncingNickname = nickname;
        syncTask = SyncAsync(nickname);
        return syncTask;
    }

    private async Task<bool> SyncAsync(string nickname)
    {
        try
        {
            if (!await identity.EnsureLoggedIn()) return false;

            // 이 계정에 마지막으로 저장한 닉네임이 같으면 다시 저장하지 않는다
            string syncedKey = SyncedKeyPrefix + identity.PlayFabId;
            string syncedPair = PlayerPrefs.GetString(syncedKey, "");
            string[] saved = syncedPair.Split(new[] { '\n' }, 2);
            if (saved.Length == 2 && saved[0] == nickname)
            {
                DisplayName = saved[1];
                Changed?.Invoke();
                return true;
            }

            string baseName = nickname;
            for (int suffix = FirstSuffixLength; suffix <= LastSuffixLength; suffix += 2)
            {
                string display = Compose(baseName, identity.PlayFabId, suffix);
                PlayFabRest.Result result = await identity.CallAsync("/Client/UpdateUserTitleDisplayName", new { DisplayName = display });

                if (result.Ok)
                {
                    DisplayName = (string)result.Data["DisplayName"] ?? display;
                    PlayerPrefs.SetString(syncedKey, nickname + "\n" + DisplayName);
                    PlayerPrefs.Save();
                    log($"닉네임을 PlayFab에 저장했습니다: {DisplayName}");
                    Changed?.Invoke();
                    return true;
                }

                if (result.ErrorCode == "NameNotAvailable")
                {
                    log($"같은 표시 이름이 이미 있어 뒤의 글자를 늘려 다시 해 봅니다 ({display})");
                    continue;
                }

                if (result.ErrorCode == "ProfaneDisplayName" && baseName != "Player")
                {
                    // PlayFab이 이 닉네임을 부적절하다고 거절했다. 표시 이름에는 기본 이름을 쓰고, 게임 안의 닉네임은 그대로 둔다.
                    log("PlayFab이 이 닉네임을 표시 이름으로 받아 주지 않아 기본 이름으로 저장합니다");
                    baseName = "Player";
                    suffix -= 2; // 같은 길이로 다시
                    continue;
                }

                log($"닉네임을 PlayFab에 저장하지 못했습니다: {result.ErrorCode} {result.ErrorMessage}");
                return false;
            }

            log("닉네임을 PlayFab에 저장하지 못했습니다: 같은 표시 이름이 계속 겹칩니다");
            return false;
        }
        catch (Exception e)
        {
            log("닉네임을 PlayFab에 저장하는 중 오류: " + e.Message);
            return false;
        }
    }
}