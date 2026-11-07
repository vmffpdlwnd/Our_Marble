using System;

/// <summary>
/// 이메일 가입 2단계 흐름의 규칙. 화면과 서버 호출에서 떼어 낸 순수한 부분이라 시험할 수 있다.
///
///   1단계 "이메일 인증"   지금 계정에 이메일을 붙이고(아무도 모르는 임시 비밀번호로), PlayFab이 비밀번호를 정하는 메일을 보낸다.
///   (메일)               사용자가 메일의 링크에서 비밀번호를 정한다. 그 주소의 주인만 할 수 있다.
///   2단계 "가입 완료"     게임으로 돌아와 정한 비밀번호를 입력한다. 그 이메일과 비밀번호로 로그인이 되면 가입이 끝난다.
///
/// 1단계와 2단계 사이에 앱을 닫아도 이어 가려고, 1단계를 마친 이메일과 그때의 PlayFab ID를 이 기기에 저장해 둔다.
/// </summary>
public static class SignupRules
{
    public const string PendingEmailKey = "Account.PendingEmail"; // 1단계를 마치고 비밀번호를 정하기를 기다리는 이메일
    public const string PendingIdKey = "Account.PendingId";       // 그때의 PlayFab ID. 다른 계정으로 바뀌면 기다림을 버린다

    /// <summary>
    /// 1단계에서 이메일을 붙일 때 쓰는 임시 비밀번호. 아무도 모르는 값(무작위 값 두 개)이라, 이 계정은 메일의 링크로 비밀번호를 정해야 로그인할 수 있다.
    /// 길이는 67자로 PlayFab 규칙(6~100자)을 지킨다.
    /// </summary>
    public static string NewTemporaryPassword() => "Aa1" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    /// <summary>가입을 기다리는 상태</summary>
    public enum PendingState
    {
        None,   // 기다리는 가입이 없다
        Active, // 1단계를 마쳤고 비밀번호 입력을 기다린다
        Stale,  // 기다리던 가입이 더는 맞지 않는다 (다른 계정으로 바뀌었거나 이메일이 연결돼 있지 않다). 저장한 값을 지워야 한다
    }

    /// <summary>
    /// 저장해 둔 기다림이 아직 맞는지 판단한다.
    /// accountInfoKnown은 서버에서 이 계정의 이메일을 이미 확인했는지다. (확인하기 전에는 이메일로는 판단하지 않는다)
    /// </summary>
    public static PendingState EvaluatePending(string pendingEmail, string pendingId, string currentId, bool accountInfoKnown, bool hasEmail, string currentEmail)
    {
        if (string.IsNullOrWhiteSpace(pendingEmail)) return PendingState.None;

        // 계정이 바뀌었다 (이메일 로그인이나 새 계정으로 시작)
        if (!string.IsNullOrEmpty(pendingId) && !string.IsNullOrEmpty(currentId) && pendingId != currentId) return PendingState.Stale;

        // 서버 확인 결과, 이 계정에 그 이메일이 없다
        if (accountInfoKnown && (!hasEmail || !SameEmail(currentEmail, pendingEmail))) return PendingState.Stale;

        return PendingState.Active;
    }

    /// <summary>두 이메일이 같은지. 앞뒤 공백과 대소문자는 무시한다.</summary>
    public static bool SameEmail(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // ───────────── 화면 글 (한 곳에 모아 두면 글꼴에 있는 글자인지 시험으로 확인할 수 있다) ─────────────

    public const string LoginTitle = "이메일 로그인";
    public const string PendingTitle = "가입 이어서 하기";

    // 이미 쓰는 이메일로 1단계를 눌렀을 때 (다른 기기에서 시작한 가입을 이어 갈 때, 새 계정으로 시작한 뒤, 이미 가입을 끝낸 이메일)
    public const string ExistingTitle = "이미 있는 이메일";
    public const string ExistingHint = "이미 있는 이메일이라 비밀번호를 정하는 메일을 보냈어요. 정한 비밀번호(이미 있다면 그 비밀번호)로 로그인해 주세요. 메일은 영어로 오고 안 보이면 스팸함을 확인해 주세요.";
    public const string ExistingMailSentMessage = "이미 있는 이메일이에요. 비밀번호를 정하는 메일을 보냈어요";
    public const string ExistingMailFailedMessage = "이미 있는 이메일이에요. 메일을 보내지 못했으니 메일 다시 보내기를 눌러 주세요";

    public const string LoginHint = "처음이면 이메일만 넣고 '이메일 인증'을 눌러 주세요. 연결한 이메일은 바꿀 수 없으니 주소를 정확히 확인해 주세요. 이미 가입했다면 비밀번호도 넣고 로그인해요.";
    public const string PendingHint = "메일의 링크에서 비밀번호를 정한 뒤 여기에 입력해 주세요. 메일은 영어로 오고 제목은 Our Marble account recovery예요. 안 보이면 스팸함을 확인해 주세요.";

    public const string LoginLabel = "로그인";
    public const string SignupLabel = "이메일 인증";
    public const string ForgotLabel = "비밀번호 찾기";
    public const string CompleteLabel = "가입 완료";
    public const string ResendLabel = "메일 다시 보내기";
    public const string NewAccountLabel = "새 계정으로 시작";

    public const string EntryLoginLabel = "이메일 로그인 / 가입"; // 가입도 이 카드에서 하므로 가입이라는 말을 넣는다
    public const string EntryPendingLabel = "가입 이어서 하기";

    public const string PendingPasswordHint = "메일에서 정한 비밀번호";

    public const string MailSentMessage = "메일을 보냈어요. 비밀번호를 정한 뒤 돌아와 주세요";
    public const string MailFailedMessage = "이메일은 연결했지만 메일을 보내지 못했어요. 메일 다시 보내기를 눌러 주세요";
    public const string CompletedMessage = "가입을 마쳤어요. 닉네임을 입력하고 시작하세요";
    public const string WrongPasswordMessage = "비밀번호가 맞지 않아요. 메일의 링크에서 정한 비밀번호를 입력해 주세요";
    public const string NewAccountWarning = "지금 계정의 기록은 이 기기에서 사라져요. 계속하려면 한 번 더 눌러 주세요";
    public const string NewAccountDone = "새 계정으로 시작했어요";
    public const string AlreadyHasEmailMessage = "이미 이메일이 연결된 계정이에요. 로그아웃한 뒤 다시 해 주세요";
}