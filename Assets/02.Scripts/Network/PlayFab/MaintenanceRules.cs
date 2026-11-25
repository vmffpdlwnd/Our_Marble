using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>온라인을 막는 이유. 막히면 Photon에 연결하지 않고 혼자 하기로 시작한다.</summary>
public enum MaintenanceBlock
{
    None,        // 막지 않음 (평소처럼 연결)
    Maintenance, // 서버 점검 중
    Outdated,    // 구버전 (내 네트워크 버전이 minNetworkVersion보다 낮음)
}

/// <summary>점검 확인 결과. EndTime은 점검이 끝나는 예정 시각이고, 모르면 null.</summary>
public struct MaintenanceCheck
{
    public MaintenanceBlock Block;
    public DateTimeOffset? EndTime;

    public bool IsBlocked => Block != MaintenanceBlock.None;

    public static MaintenanceCheck Open => new MaintenanceCheck { Block = MaintenanceBlock.None };
}

/// <summary>
/// 점검과 구버전 판단 (v1.3.1, 기획서 "점검과 구버전").
/// 전용 서버가 없어서 PlayFab Title Data의 Maintenance 키를 점검 표시로 쓴다. 값은 운영하는 사람이 PlayFab Game Manager에서 바꾼다.
///   예: { "on": false, "endTime": "2026-12-01T18:00:00+09:00", "minNetworkVersion": 5 }
/// 읽는 일(PlayFab 호출)은 PhotonConnection이 하고, 여기는 받은 글을 판단만 한다. Unity 기능을 쓰지 않는 순수 클래스다.
/// </summary>
public static class MaintenanceRules
{
    /// <summary>PlayFab Title Data의 키</summary>
    public const string TitleDataKey = "Maintenance";

    /// <summary>
    /// Title Data 값(JSON)과 내 네트워크 버전으로 온라인을 막을지 정한다.
    /// 값이 없거나 깨졌으면 막지 않는다. (읽지 못했다고 모두를 혼자 하기로 보내면, Title Data 실수 하나로 온라인 전체가 멈추기 때문이다)
    /// 구버전을 점검보다 먼저 본다. 구버전은 점검이 끝나도 업데이트해야 들어올 수 있어서, 할 일(업데이트)을 알려 주는 쪽이 낫다.
    /// </summary>
    public static MaintenanceCheck Evaluate(string json, int myNetworkVersion)
    {
        if (string.IsNullOrWhiteSpace(json)) return MaintenanceCheck.Open;

        JObject value;
        try
        {
            // 시각 글을 Newtonsoft가 DateTime으로 바꾸면 시간대(+09:00)가 사라지므로, 글 그대로 읽는다
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                value = JObject.Load(reader);
        }
        catch (Exception) { return MaintenanceCheck.Open; }

        int? minVersion = ReadInt(value["minNetworkVersion"]);
        if (minVersion.HasValue && myNetworkVersion < minVersion.Value)
            return new MaintenanceCheck { Block = MaintenanceBlock.Outdated };

        if (ReadBool(value["on"]))
            return new MaintenanceCheck { Block = MaintenanceBlock.Maintenance, EndTime = ReadTime(value["endTime"]) };

        return MaintenanceCheck.Open;
    }

    /// <summary>네트워크 버전 글("5")을 정수로. 읽지 못하면 0. (0이면 minNetworkVersion이 1 이상일 때 구버전으로 본다)</summary>
    public static int ParseNetworkVersion(string version)
    {
        return int.TryParse(version, out int number) ? number : 0;
    }

    // 숫자(5)와 글("5") 모두 받는다. Title Data는 사람이 손으로 적는 값이라 따옴표를 붙이기 쉽다
    private static int? ReadInt(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        return int.TryParse(token.ToString(), out int number) ? number : (int?)null;
    }

    // true와 "true" 모두 받는다
    private static bool ReadBool(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null) return false;
        return bool.TryParse(token.ToString(), out bool flag) && flag;
    }

    // ISO 8601 시각("2026-12-01T18:00:00+09:00"). 비었거나 읽지 못하면 null (시각 없이 안내한다)
    private static DateTimeOffset? ReadTime(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null) return null;

        string text = token.ToString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTimeOffset.TryParse(text, out DateTimeOffset time) ? time : (DateTimeOffset?)null;
    }
}