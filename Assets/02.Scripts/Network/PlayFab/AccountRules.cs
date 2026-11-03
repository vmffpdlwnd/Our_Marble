using System.Text.RegularExpressions;

/// <summary>이메일 계정 요청의 결과. 화면이 그대로 보여 줄 수 있는 한국어 문장을 담는다.</summary>
public class AccountResult
{
    public bool Ok { get; private set; }
    public string Message { get; private set; }   // 화면에 보여 줄 문장 (성공이어도 알려 줄 것이 있으면 들어 있다)
    public string ErrorCode { get; private set; } // 서버가 알려 준 오류 이름. 성공이면 null

    public static AccountResult Success(string message = null) => new AccountResult { Ok = true, Message = message };

    public static AccountResult Fail(string errorCode, string message) => new AccountResult { Ok = false, ErrorCode = errorCode, Message = message };

    /// <summary>서버에 보내기 전에 걸러낸 경우 (형식이 틀림)</summary>
    public static AccountResult Invalid(string message) => Fail("INVALID_INPUT", message);
}

/// <summary>이메일과 비밀번호가 PlayFab 규칙에 맞는지 미리 확인하고, 서버 오류를 한국어 문장으로 바꾼다.</summary>
public static class AccountRules
{
    public const int MinPasswordLength = 6;   // PlayFab 규칙: 6~100자
    public const int MaxPasswordLength = 100;

    private static readonly Regex EmailPattern = new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    /// <summary>이메일이 올바른지. 괜찮으면 null, 아니면 화면에 보여 줄 문장을 돌려준다.</summary>
    public static string CheckEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "이메일을 입력해 주세요";
        if (!EmailPattern.IsMatch(email.Trim())) return "이메일 형식이 올바르지 않아요";
        return null;
    }

    /// <summary>비밀번호가 올바른지. 괜찮으면 null, 아니면 화면에 보여 줄 문장을 돌려준다.</summary>
    public static string CheckPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return "비밀번호를 입력해 주세요";
        if (password.Length < MinPasswordLength) return $"비밀번호는 {MinPasswordLength}자 이상이어야 해요";
        if (password.Length > MaxPasswordLength) return $"비밀번호는 {MaxPasswordLength}자 이하여야 해요";
        return null;
    }

    /// <summary>서버 오류 이름을 사용자에게 보여 줄 문장으로 바꾼다.</summary>
    public static string MessageFor(string errorCode)
    {
        switch (errorCode)
        {
            case "InvalidEmailAddress": return "이메일 형식이 올바르지 않아요";
            case "InvalidPassword": return $"비밀번호는 {MinPasswordLength}자 이상 {MaxPasswordLength}자 이하여야 해요";
            case "EmailAddressNotAvailable": return "이미 가입된 이메일이에요";
            case "InvalidEmailOrPassword": return "이메일 또는 비밀번호가 맞지 않아요";
            case "AccountNotFound": return "가입되지 않은 이메일이에요";
            case "AccountBanned": return "이용이 제한된 계정이에요";
            case "AccountAlreadyLinked": return "이미 이메일이 연결된 계정이에요";
            case "NoContactEmailAddressFound": return "이 이메일로 가입된 계정이 없어요";
            case "EmailRecipientBlacklisted": return "이 이메일로는 메일을 보낼 수 없어요";
            case "SmtpAddonNotEnabled": return "메일 발송 설정이 아직 준비되지 않았어요";
            case "NETWORK":
            case "TIMEOUT": return "서버에 연결하지 못했어요. 연결 상태를 확인해 주세요";
            case "NOT_LOGGED_IN": return "서버에 로그인하지 못했어요";
            default: return $"처리하지 못했어요 ({errorCode})";
        }
    }
}