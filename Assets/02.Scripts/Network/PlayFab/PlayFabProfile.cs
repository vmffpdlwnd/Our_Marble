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

    public const string GamesStat = "Games"; // 플레이 횟수
    public const string WinsStat = "Wins";   // 승리 횟수
    private const string PendingKeyPrefix = "PlayFab.PendingRecord.";

    private readonly PlayFabIdentity identity;
    private readonly Action<string> log;
    private Task<bool> syncTask;
    private string syncingNickname;

    // 전적 동기화: 한 번에 하나만 돌고, 도는 중에 요청이 또 오면 끝난 뒤에 한 번 더 돈다
    private Task<bool> recordTask;
    private bool recordDirty;
    private int lastReportedSerial = -1;

    /// <summary>PlayFab에 저장된 내 표시 이름 (닉네임#끝글자). 아직 저장하지 못했으면 null</summary>
    public string DisplayName { get; private set; }

    /// <summary>플레이 횟수 (아직 PlayFab에 못 올린 것도 포함)</summary>
    public int Games { get; private set; }

    /// <summary>승리 횟수 (아직 PlayFab에 못 올린 것도 포함)</summary>
    public int Wins { get; private set; }

    /// <summary>전적을 한 번이라도 받아 왔는지. false면 화면에는 "-"를 보여 준다</summary>
    public bool RecordLoaded { get; private set; }

    /// <summary>표시 이름이나 전적이 바뀔 때마다 1씩 늘어난다. (화면이 바뀌었는지 알아보는 용도)</summary>
    public int Revision { get; private set; }

    public event Action Changed;

    private void RaiseChanged()
    {
        Revision++;
        Changed?.Invoke();
    }

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
                RaiseChanged();
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
                    RaiseChanged();
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

    // ───────────── 전적 ─────────────

    /// <summary>
    /// 게임이 끝났을 때 결과를 기록한다. 플레이 횟수는 항상 1 늘고, 이겼으면 승리 횟수도 1 는다.
    /// 올리지 못하면(오프라인, PlayFab 설정 등) 이 PC에 보관했다가 다음 동기화 때 올린다.
    /// serial은 몇 번째 게임인지. 같은 게임의 결과를 두 번 기록하지 않는다. (0 이하면 구분하지 않는다)
    /// </summary>
    public Task<bool> ReportResultAsync(bool won, int serial)
    {
        if (serial > 0 && serial == lastReportedSerial) return Task.FromResult(true);
        lastReportedSerial = serial;

        AddPending(1, won ? 1 : 0);
        if (RecordLoaded)
        {
            // 서버 응답을 기다리지 않고 화면에는 바로 반영한다
            Games++;
            if (won) Wins++;
            RaiseChanged();
        }
        return SyncRecordAsync();
    }

    /// <summary>
    /// 서버의 전적을 받아 오고, 못 올린 기록이 있으면 더해서 올린다. 동시에 여러 번 불러도 한 번에 하나씩 순서대로 처리한다.
    /// 서버에는 "더한 합계"를 쓴다. (통계의 집계 방식은 기본값인 마지막 값 덮어쓰기여야 한다. 합산으로 바꾸면 두 번 더해진다)
    /// </summary>
    public Task<bool> SyncRecordAsync()
    {
        if (recordTask != null && !recordTask.IsCompleted)
        {
            recordDirty = true;
            return recordTask;
        }

        recordTask = RunRecordLoop();
        return recordTask;
    }

    private async Task<bool> RunRecordLoop()
    {
        bool ok;
        do
        {
            recordDirty = false;
            ok = await SyncRecordOnce();
        }
        while (recordDirty);
        return ok;
    }

    private async Task<bool> SyncRecordOnce()
    {
        try
        {
            if (!await identity.EnsureLoggedIn()) return false;

            // 1) 서버의 현재 값. 아직 한 번도 기록하지 않은 통계는 목록에 없다 (0으로 본다)
            PlayFabRest.Result read = await identity.CallAsync("/Client/GetPlayerStatistics", new { StatisticNames = new[] { GamesStat, WinsStat } });
            if (!read.Ok)
            {
                log($"전적을 받아 오지 못했습니다: {read.ErrorCode} {read.ErrorMessage}");
                return false;
            }

            int games = 0, wins = 0;
            if (read.Data["Statistics"] is Newtonsoft.Json.Linq.JArray list)
            {
                foreach (Newtonsoft.Json.Linq.JToken stat in list)
                {
                    string name = (string)stat["StatisticName"];
                    int value = (int?)stat["Value"] ?? 0;
                    if (name == GamesStat) games = value;
                    else if (name == WinsStat) wins = value;
                }
            }

            // 2) 못 올린 기록이 있으면 더해서 올린다
            GetPending(out int pendingGames, out int pendingWins);
            if (pendingGames > 0 || pendingWins > 0)
            {
                PlayFabRest.Result write = await identity.CallAsync("/Client/UpdatePlayerStatistics", new
                {
                    Statistics = new[]
                    {
                        new { StatisticName = GamesStat, Value = games + pendingGames },
                        new { StatisticName = WinsStat, Value = wins + pendingWins },
                    },
                });

                if (write.Ok)
                {
                    games += pendingGames;
                    wins += pendingWins;
                    ClearPending();
                    log($"전적을 PlayFab에 기록했습니다: 플레이 {games}회, 승리 {wins}회");
                }
                else
                {
                    log($"전적을 PlayFab에 올리지 못했습니다: {write.ErrorCode} {write.ErrorMessage}{RecordHint(write.ErrorCode)}. 이 PC에 보관했다가 다음에 다시 올립니다");
                    games += pendingGames; // 못 올려도 화면에는 내 기록을 보여 준다
                    wins += pendingWins;
                }
            }

            Games = games;
            Wins = wins;
            RecordLoaded = true;
            RaiseChanged();
            return true;
        }
        catch (Exception e)
        {
            log("전적을 처리하는 중 오류: " + e.Message);
            return false;
        }
    }

    private static string RecordHint(string errorCode)
    {
        return errorCode == "APINotEnabledForGameClientAccess"
            ? " → PlayFab Game Manager의 Settings > API Features에서 'Allow client to post player statistics'를 켜세요"
            : "";
    }

    // 못 올린 기록(플레이 횟수, 승리 횟수). 앱을 껐다 켜도 남도록 이 PC에 저장한다.
    private string PendingKey => PendingKeyPrefix + identity.CustomId;

    private void GetPending(out int games, out int wins)
    {
        games = 0;
        wins = 0;
        string[] parts = PlayerPrefs.GetString(PendingKey, "").Split(',');
        if (parts.Length != 2) return;

        int.TryParse(parts[0], out games);
        int.TryParse(parts[1], out wins);
        if (games < 0) games = 0;
        if (wins < 0) wins = 0;
    }

    private void AddPending(int games, int wins)
    {
        GetPending(out int pendingGames, out int pendingWins);
        PlayerPrefs.SetString(PendingKey, $"{pendingGames + games},{pendingWins + wins}");
        PlayerPrefs.Save();
    }

    private void ClearPending()
    {
        PlayerPrefs.SetString(PendingKey, "");
        PlayerPrefs.Save();
    }
}