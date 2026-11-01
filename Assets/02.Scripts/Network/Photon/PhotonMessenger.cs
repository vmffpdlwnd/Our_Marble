using System;
using System.Collections.Generic;
using System.Text;
using Fusion;
using Fusion.Sockets;
using UnityEngine;

/// <summary>
/// Photon으로 메시지(JSON 문자열)를 주고받는 일을 맡는다. 기존 로비와 게임이 서버와 주고받던 요청과 알림을 그대로 실어 나른다.
///
///   요청(Request)  참가자 → 방장.   내가 방장이면 네트워크를 타지 않고 바로 내 쪽으로 돌려준다.
///   알림(Notify)   방장 → 참가자.   방 전원에게 보낼 때는 나도 받는다.
///
/// 전송에는 Fusion의 신뢰 데이터(Reliable Data)를 쓴다. 네트워크 오브젝트가 필요 없고 크기 제한이 없어서 큰 메시지도 같은 길로 보낸다.
/// 신뢰 데이터는 서로 다른 메시지의 도착 순서를 보장하지 않아서, 보내는 쪽이 번호를 매기고 받는 쪽이 번호 순서대로 꺼내 준다.
/// 번호는 (보내는 사람 → 받는 사람) 한 쌍마다 따로 센다. 사람이 나가면 그 사람과의 번호도 지우고, 다시 들어오면 새 번호로 시작한다.
///
/// 보낸 사람은 메시지에 적힌 값이 아니라 Photon이 알려 주는 연결(PlayerRef)로 정한다.
/// </summary>
public class PhotonMessenger
{
    public enum Channel { Request = 1, Notify = 2 }

    // 메시지가 속한 영역. 받는 쪽이 어느 쪽 코드로 넘길지 정한다.
    public const int DomainLobby = 0;
    public const int DomainGame = 1;
    public const int DomainTest = 99;

    public delegate void RequestHandler(PlayerRef sender, int domain, string json);
    public delegate void NotifyHandler(PlayerRef sender, int domain, string typeName, string json);

    /// <summary>방장이 받는 요청</summary>
    public event RequestHandler RequestReceived;

    /// <summary>참가자(방장 자신 포함)가 받는 알림</summary>
    public event NotifyHandler NotifyReceived;

    public int SentCount { get; private set; }
    public int ReceivedCount { get; private set; }
    /// <summary>번호가 어긋난 채로 도착해서 기다렸다가 꺼낸 메시지 수. 0이 아니면 Photon이 실제로 순서를 바꿔 보낸 것이다</summary>
    public int ReorderedCount { get; private set; }
    public int DuplicateCount { get; private set; }

    private const float GapWarningSeconds = 5f;

    private class Packet { public int Channel; public int Domain; public byte[] Data; }

    private class Stream
    {
        public int Next;
        public readonly Dictionary<int, Packet> Pending = new Dictionary<int, Packet>();
        public float WaitingSince;
        public bool Warned;
    }

    private class LocalItem { public Channel Channel; public int Domain; public string TypeName; public string Json; }

    private readonly PhotonConnection connection;
    private readonly Dictionary<int, int> sendSeq = new Dictionary<int, int>();   // 받는 사람 → 다음에 보낼 번호
    private readonly Dictionary<int, Stream> streams = new Dictionary<int, Stream>(); // 보낸 사람 → 받은 메시지 정리
    private readonly Queue<LocalItem> localQueue = new Queue<LocalItem>();
    private readonly List<int> idBuffer = new List<int>();

    public PhotonMessenger(PhotonConnection connection)
    {
        this.connection = connection;
    }

    // ───────────── 보내기 ─────────────

    /// <summary>방장에게 요청을 보낸다. 내가 방장이면 바로 내 쪽으로 돌려준다.</summary>
    public bool SendRequest(int domain, string json)
    {
        if (!connection.IsInSession) return false;

        if (connection.IsMaster)
        {
            localQueue.Enqueue(new LocalItem { Channel = Channel.Request, Domain = domain, Json = json });
            SentCount++;
            return true;
        }

        return SendOverNetwork(connection.MasterPlayerRef, Channel.Request, domain, null, json);
    }

    /// <summary>한 사람에게 알림을 보낸다. 받는 사람이 나라면 네트워크를 타지 않고 내 쪽으로 돌려준다.</summary>
    public bool SendNotify(PlayerRef target, int domain, string typeName, string json)
    {
        if (!connection.IsInSession) return false;

        if (target == connection.LocalPlayerRef)
        {
            localQueue.Enqueue(new LocalItem { Channel = Channel.Notify, Domain = domain, TypeName = typeName, Json = json });
            SentCount++;
            return true;
        }

        return SendOverNetwork(target, Channel.Notify, domain, typeName, json);
    }

    /// <summary>방 전원(나 포함)에게 알림을 보낸다.</summary>
    public void BroadcastNotify(int domain, string typeName, string json)
    {
        var targets = new List<PlayerRef>(connection.Players);
        foreach (PlayerRef target in targets)
            SendNotify(target, domain, typeName, json);
    }

    private bool SendOverNetwork(PlayerRef target, Channel channel, int domain, string typeName, string json)
    {
        int targetId = target.PlayerId;
        sendSeq.TryGetValue(targetId, out int seq);

        // 알림은 "종류 이름\n내용", 요청은 내용만 (종류는 요청 JSON 안의 type 필드에 있다)
        string text = channel == Channel.Notify ? typeName + "\n" + json : json;
        byte[] bytes = Encoding.UTF8.GetBytes(text);

        // 키: 채널, 번호, 영역, 보낸 사람. 키가 같은 전송이 겹치지 않게 보낸 사람 번호도 넣는다.
        ReliableKey key = ReliableKey.FromInts((int)channel, seq, domain, connection.LocalPlayerRef.PlayerId);
        try
        {
            connection.SendReliable(target, key, bytes);
        }
        catch (Exception e)
        {
            connection.WriteLog($"메시지 보내기 오류 ({targetId}번에게): {e.Message}");
            return false;
        }

        sendSeq[targetId] = seq + 1;
        SentCount++;
        return true;
    }

    // ───────────── 받기 ─────────────

    /// <summary>Fusion의 신뢰 데이터 수신 콜백이 부른다.</summary>
    public void OnReliableData(PlayerRef sender, ReliableKey key, ReadOnlySpan<byte> data)
    {
        key.GetInts(out int channel, out int seq, out int domain, out int senderId);
        if (channel != (int)Channel.Request && channel != (int)Channel.Notify) return; // 우리 메시지가 아닌 데이터

        ReceivedCount++;
        Stream stream = GetStream(sender.PlayerId);

        if (seq < stream.Next || stream.Pending.ContainsKey(seq))
        {
            DuplicateCount++;
            return;
        }

        if (seq != stream.Next) ReorderedCount++;

        stream.Pending[seq] = new Packet { Channel = channel, Domain = domain, Data = data.ToArray() };
        Drain(sender, stream);
    }

    private Stream GetStream(int senderId)
    {
        if (!streams.TryGetValue(senderId, out Stream stream))
        {
            stream = new Stream();
            streams[senderId] = stream;
        }
        return stream;
    }

    // 번호 순서대로 이어지는 메시지를 모두 꺼내서 넘긴다
    private void Drain(PlayerRef sender, Stream stream)
    {
        while (stream.Pending.TryGetValue(stream.Next, out Packet packet))
        {
            stream.Pending.Remove(stream.Next);
            stream.Next++;
            Dispatch(sender, packet.Channel, packet.Domain, Encoding.UTF8.GetString(packet.Data));
        }

        stream.WaitingSince = stream.Pending.Count > 0 ? Time.unscaledTime : 0f;
        stream.Warned = false;
    }

    private void Dispatch(PlayerRef sender, int channel, int domain, string text)
    {
        try
        {
            if (channel == (int)Channel.Notify)
            {
                int newline = text.IndexOf('\n');
                string typeName = newline >= 0 ? text.Substring(0, newline) : text;
                string json = newline >= 0 ? text.Substring(newline + 1) : "";
                NotifyReceived?.Invoke(sender, domain, typeName, json);
            }
            else
            {
                RequestReceived?.Invoke(sender, domain, text);
            }
        }
        catch (Exception e)
        {
            // 처리 중 오류가 나도 뒤따르는 메시지가 막히지 않게 한다
            connection.WriteLog($"메시지 처리 오류: {e.Message}");
            Debug.LogError(e);
        }
    }

    // ───────────── 주기적으로 / 상태가 바뀔 때 ─────────────

    /// <summary>나에게 돌려주는 메시지를 처리하고, 오래 기다리는 어긋난 메시지가 있는지 본다. 매 프레임 부른다.</summary>
    public void Tick()
    {
        // 처리하는 동안 새로 쌓이는 것은 다음 프레임에 처리한다
        int count = localQueue.Count;
        for (int i = 0; i < count; i++)
        {
            LocalItem item = localQueue.Dequeue();
            ReceivedCount++;
            if (item.Channel == Channel.Notify)
                DispatchLocalNotify(item);
            else
                DispatchLocalRequest(item);
        }

        foreach (KeyValuePair<int, Stream> pair in streams)
        {
            Stream stream = pair.Value;
            if (stream.Pending.Count == 0 || stream.Warned) continue;
            if (Time.unscaledTime - stream.WaitingSince < GapWarningSeconds) continue;

            stream.Warned = true;
            connection.WriteLog($"{pair.Key}번의 메시지 {stream.Next}번이 {GapWarningSeconds}초 넘게 도착하지 않습니다 (뒤의 {stream.Pending.Count}개가 기다리는 중)");
        }
    }

    private void DispatchLocalNotify(LocalItem item)
    {
        try { NotifyReceived?.Invoke(connection.LocalPlayerRef, item.Domain, item.TypeName, item.Json); }
        catch (Exception e) { connection.WriteLog($"메시지 처리 오류: {e.Message}"); Debug.LogError(e); }
    }

    private void DispatchLocalRequest(LocalItem item)
    {
        try { RequestReceived?.Invoke(connection.LocalPlayerRef, item.Domain, item.Json); }
        catch (Exception e) { connection.WriteLog($"메시지 처리 오류: {e.Message}"); Debug.LogError(e); }
    }

    /// <summary>방 접속자가 바뀌었을 때 부른다. 나간 사람과의 번호 기록을 지운다. (다시 들어오면 새 번호로 시작한다)</summary>
    public void OnPlayersChanged(HashSet<int> present)
    {
        idBuffer.Clear();
        foreach (int id in sendSeq.Keys) if (!present.Contains(id)) idBuffer.Add(id);
        foreach (int id in idBuffer) sendSeq.Remove(id);

        idBuffer.Clear();
        foreach (int id in streams.Keys) if (!present.Contains(id)) idBuffer.Add(id);
        foreach (int id in idBuffer) streams.Remove(id);
    }

    /// <summary>연결이 바뀌거나 끝났을 때 모든 기록을 지운다.</summary>
    public void Reset()
    {
        sendSeq.Clear();
        streams.Clear();
        localQueue.Clear();
    }
}