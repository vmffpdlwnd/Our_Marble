using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

/// <summary>
/// 방 안의 사람을 두 가지 번호로 나눠서 기억한다. (방장 PC가 쓴다)
///
///   연결 번호  Photon이 접속할 때마다 새로 붙이는 번호 (PlayerRef.PlayerId). 나갔다 들어오면 다른 번호가 된다.
///   게임 번호  게임과 방 안에서 그 사람을 가리키는 번호 (playerId). 한 번 정해지면 그 방에서는 그대로다.
///
/// 처음 들어온 사람은 연결 번호가 곧 게임 번호다. 같은 계정(PlayFab ID)이 연결이 끊겼다가 다시 들어오면,
/// 새 연결 번호를 예전 게임 번호에 붙여서 같은 자리로 돌아오게 한다. (게임 상태는 게임 번호로 사람을 구분한다)
/// 계정은 Photon이 PlayFab에 확인한 사용자 ID라서 남이 흉내 낼 수 없다.
/// </summary>
public class PlayerRegistry
{
    private class Entry
    {
        public long GameId;
        public string AccountId;
        public int RefId = -1; // 지금 연결된 번호. 연결이 끊겼으면 -1
    }

    private readonly Dictionary<long, Entry> byGameId = new Dictionary<long, Entry>();
    private readonly Dictionary<int, Entry> byRefId = new Dictionary<int, Entry>();
    private readonly Dictionary<string, Entry> byAccount = new Dictionary<string, Entry>();

    /// <summary>이 연결 번호의 게임 번호를 이미 알고 있으면 돌려준다.</summary>
    public bool TryGetGameId(int refId, out long gameId)
    {
        if (byRefId.TryGetValue(refId, out Entry entry)) { gameId = entry.GameId; return true; }
        gameId = 0;
        return false;
    }

    /// <summary>연결 번호의 게임 번호. 모르면 연결 번호 그대로다. (처음 온 사람과 같은 규칙)</summary>
    public long GameIdOfRef(int refId) => byRefId.TryGetValue(refId, out Entry entry) ? entry.GameId : refId;

    /// <summary>이 게임 번호의 사람이 지금 연결돼 있으면 그 연결 번호를 돌려준다.</summary>
    public bool TryGetRef(long gameId, out int refId)
    {
        if (byGameId.TryGetValue(gameId, out Entry entry) && entry.RefId >= 0) { refId = entry.RefId; return true; }
        refId = -1;
        return false;
    }

    /// <summary>지금 연결된 사람들의 연결 번호</summary>
    public List<int> ConnectedRefs() => byGameId.Values.Where(e => e.RefId >= 0).Select(e => e.RefId).ToList();

    /// <summary>
    /// 새 연결의 게임 번호를 정한다. 이미 아는 연결이면 그대로 돌려준다.
    /// 같은 계정이 이 방의 자리를 아직 가지고 있으면(canRejoin이 true) 그 자리로 돌아온 것이고, 아니면 새 사람이다.
    /// </summary>
    /// <param name="accountId">Photon이 확인한 계정. 모르면 비워 둔다 (그러면 항상 새 사람으로 본다)</param>
    /// <param name="canRejoin">예전 게임 번호가 아직 이 방에서 돌아올 수 있는 자리인지 (게임 중인 방의 참가자인지)</param>
    public long Resolve(int refId, string accountId, Func<long, bool> canRejoin, out bool returning)
    {
        returning = false;
        if (byRefId.TryGetValue(refId, out Entry known)) return known.GameId;

        if (!string.IsNullOrEmpty(accountId) && byAccount.TryGetValue(accountId, out Entry previous) && canRejoin != null && canRejoin(previous.GameId))
        {
            if (previous.RefId >= 0) byRefId.Remove(previous.RefId); // 예전 연결이 아직 남아 있어도 새 연결이 그 자리를 이어받는다
            previous.RefId = refId;
            byRefId[refId] = previous;
            returning = true;
            return previous.GameId;
        }

        long gameId = refId; // 처음 온 사람: 연결 번호가 곧 게임 번호
        while (byGameId.ContainsKey(gameId)) gameId += 100000; // 이미 쓰는 번호와 겹칠 일은 없지만, 겹치면 피한다

        var entry = new Entry { GameId = gameId, AccountId = accountId, RefId = refId };
        byGameId[gameId] = entry;
        byRefId[refId] = entry;
        if (!string.IsNullOrEmpty(accountId)) byAccount[accountId] = entry; // 같은 계정이 다시 오면 가장 새로운 자리를 가리킨다
        return gameId;
    }

    /// <summary>연결이 끊겼다. 연결 번호만 지우고 계정과 게임 번호는 남겨서, 돌아오면 같은 자리로 붙일 수 있게 한다.</summary>
    public void Release(int refId)
    {
        if (!byRefId.TryGetValue(refId, out Entry entry)) return;

        byRefId.Remove(refId);
        entry.RefId = -1;
    }

    public void Clear()
    {
        byGameId.Clear();
        byRefId.Clear();
        byAccount.Clear();
    }

    private class ExportItem
    {
        public long g; // 게임 번호
        public string a; // 계정
        public int r; // 연결 번호 (끊겼으면 -1)
    }

    /// <summary>모든 사람에게 나눠 줄 기록. 방장이 바뀌어도 새 방장이 이어받으려고 모두가 하나씩 가진다.</summary>
    public string Export()
    {
        return JsonConvert.SerializeObject(byGameId.Values.Select(e => new ExportItem { g = e.GameId, a = e.AccountId, r = e.RefId }).ToList());
    }

    /// <summary>받아 둔 기록으로 되살린다. isConnected가 false인 연결 번호는 끊긴 것으로 본다.</summary>
    public void Import(string json, Func<int, bool> isConnected)
    {
        Clear();
        if (string.IsNullOrEmpty(json)) return;

        List<ExportItem> items;
        try { items = JsonConvert.DeserializeObject<List<ExportItem>>(json); }
        catch { return; }
        if (items == null) return;

        foreach (ExportItem item in items)
        {
            var entry = new Entry { GameId = item.g, AccountId = item.a, RefId = item.r >= 0 && isConnected != null && isConnected(item.r) ? item.r : -1 };
            byGameId[entry.GameId] = entry;
            if (entry.RefId >= 0) byRefId[entry.RefId] = entry;
            if (!string.IsNullOrEmpty(entry.AccountId)) byAccount[entry.AccountId] = entry;
        }
    }
}