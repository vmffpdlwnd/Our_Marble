using System;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// PlayFab 웹 API(REST)를 부르는 작은 도구. SDK를 쓰지 않고 UnityWebRequest로 직접 보낸다.
/// (웹/WebGL에서도 되는 방식이고, 저장소에 SDK 파일이 들어오지 않는다)
///
/// 요청은 POST https://{TitleId}.playfabapi.com{경로}, 본문은 JSON이다. 로그인 뒤의 요청에는 X-Authorization 헤더에 세션 티켓을 넣는다.
/// 응답은 성공이면 {"code":200,"status":"OK","data":{...}}, 실패면 {"code":400,"error":"오류 이름","errorMessage":"..."} 모양이다.
/// </summary>
public static class PlayFabRest
{
    public struct Result
    {
        public bool Ok;
        public JObject Data;        // 성공했을 때 응답의 data
        public string ErrorCode;    // PlayFab 오류 이름(예: AccountNotFound) 또는 NETWORK, TIMEOUT, BAD_RESPONSE
        public string ErrorMessage;

        public static Result Success(JObject data) => new Result { Ok = true, Data = data };
        public static Result Fail(string code, string message) => new Result { Ok = false, ErrorCode = code, ErrorMessage = message };
    }

    /// <summary>시험용: 실제 네트워크 대신 쓸 전송 방법. 정하지 않으면 UnityWebRequest를 쓴다. (url, 본문 JSON, 세션 티켓, 제한 시간)</summary>
    public static Func<string, string, string, float, Task<Result>> SendOverride;

    public static Task<Result> PostAsync(string path, object body, string sessionTicket, float timeoutSeconds)
    {
        string url = PlayFabConfig.BaseUrl + path;
        string json = JsonConvert.SerializeObject(body);

        if (SendOverride != null) return SendOverride(url, json, sessionTicket, timeoutSeconds);
        return SendWithUnity(url, json, sessionTicket, timeoutSeconds);
    }

    private static async Task<Result> SendWithUnity(string url, string json, string sessionTicket, float timeoutSeconds)
    {
        using (var request = new UnityWebRequest(url, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(sessionTicket)) request.SetRequestHeader("X-Authorization", sessionTicket);

            UnityWebRequestAsyncOperation operation = request.SendWebRequest();

            // 웹(WebGL)에서도 쓰려고 코루틴이나 Task.Delay 없이 프레임마다 확인한다
            float until = Time.realtimeSinceStartup + timeoutSeconds;
            while (!operation.isDone && Time.realtimeSinceStartup < until)
                await Task.Yield();

            if (!operation.isDone)
            {
                request.Abort();
                return Result.Fail("TIMEOUT", $"{timeoutSeconds}초 안에 응답이 없습니다");
            }

            // 서버가 오류로 답한 경우(HTTP 400)에도 본문에 오류 이름이 들어 있어서 그대로 읽는다. 연결 자체가 안 된 경우만 따로 본다.
            string networkError = request.result == UnityWebRequest.Result.ConnectionError ? request.error : null;
            return Parse(request.responseCode, request.downloadHandler.text, networkError);
        }
    }

    /// <summary>PlayFab의 응답 본문을 읽는다.</summary>
    public static Result Parse(long httpStatus, string body, string networkError)
    {
        if (networkError != null && string.IsNullOrEmpty(body))
            return Result.Fail("NETWORK", networkError);

        JObject root;
        try { root = string.IsNullOrWhiteSpace(body) ? null : JObject.Parse(body); }
        catch { root = null; }

        if (root == null)
            return Result.Fail("BAD_RESPONSE", $"읽을 수 없는 응답입니다 (HTTP {httpStatus})");

        int code = (int?)root["code"] ?? (int)httpStatus;
        if (code == 200 && root["data"] is JObject data)
            return Result.Success(data);

        string error = (string)root["error"] ?? "UNKNOWN";
        string message = (string)root["errorMessage"] ?? error;
        return Result.Fail(error, message);
    }
}