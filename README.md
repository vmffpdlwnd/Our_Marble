# 우리의 마블 포지 (Our_Marble)

> [Our-Marble/our-marble-client](https://github.com/Our-Marble/our-marble-client)를 포크한 프로젝트입니다.
> 원본의 화면·연출·규칙을 바탕으로, **별도 서버 없이 방장이 게임 규칙을 돌리는 멀티플레이**(Photon Fusion Shared 모드)로 개조했습니다. 계정과 전적은 PlayFab을 씁니다.

**[지금 플레이하기 (itch.io)](https://vmffpdlwnd35.itch.io/our-marble)** · 브라우저에서 바로 실행되고, Windows(.exe)용도 내려받을 수 있습니다.

## 소개

"모두의 마블"을 모티브로 한 **2~4인 온라인 보드게임**입니다. 서버 프로그램 없이 게임 파일 하나만 켜서 친구와 바로 플레이하는 것이 목표입니다. 닉네임만 입력해 바로 시작할 수 있고, 원하면 이메일 계정으로 올려 다른 기기에서도 같은 계정을 쓸 수 있습니다.

## 원본과 달라지는 점

| 항목 | 원본 | 이 프로젝트 |
| --- | --- | --- |
| 구조 | Java/Spring 전용 서버가 판정 | 방장(마스터 클라이언트)이 판정 |
| 멀티 | WebSocket 직접 구현 | Photon Fusion (Shared 모드) |
| 로그인 | JWT, 이메일 인증 | 닉네임만 입력해 바로 시작 (PlayFab 익명 계정), 원하면 이메일 계정 (가입 때 인증 코드 없음) |
| 전적 | 서버 DB | PlayFab 통계 (플레이 횟수, 승리 횟수) |
| 방 저장소 | Redis | Photon 세션 (방 코드 8자리) |
| 규칙 코드 | 서버(Java) | C#으로 복귀 |

## 문서

- [기획서](docs/기획서.md): 목표, 범위, 구조, 게임 규칙, 작업 순서

## 진행 상태

- [x] 포크 및 기획서 작성
- [x] 서버 의존 코드 제거
- [x] 싱글 모드 (봇과 한 판 완주)
- [x] 규칙 로직 C# 이식
- [x] 제한 시간, AFK, 봇
- [x] v1.0 배포 (itch.io)
- [x] 규칙 분리 (`Rules/`)
- [x] 폴더와 씬 정리
- [x] v1.0.1 배포 (구조 정리 버전, 테스터 모집)
- [x] Photon Fusion 연결 (Shared 모드: 로비, 방, 방장 판정, 방장 승계, 재접속)
- [x] PlayFab 계정 (익명 로그인, Photon 인증, 닉네임, 전적)
- [x] 이메일 계정 (회원가입, 로그인, 비밀번호 찾기)
- [x] 웹(WebGL) 빌드에서 Photon과 PlayFab 확인
- [x] v1.1 배포 (웹, Windows)

## 현재 상태

**itch.io에는 v1.1이 배포돼 있습니다 (웹 브라우저, Windows .exe).** 친구와 함께 하는 멀티플레이(방 만들기와 입장, 방장 판정, 방장 승계, 끊겼을 때 자동 재연결과 이어하기)와 PlayFab 계정, 전적을 쓸 수 있습니다. 웹과 Windows 사용자가 같은 방에서 함께 할 수 있습니다. Photon이나 PlayFab에 연결하지 못하면 자동으로 혼자 하기(오프라인)로 전환됩니다.

## 기술 스택

Unity 6 (6000.3), C#, URP 2D, uGUI, TextMesh Pro, DOTween, Newtonsoft.Json

멀티플레이는 Photon Fusion 2.1(Shared 모드), 계정과 전적은 PlayFab(REST 호출, SDK 없음)을 씁니다. 웹(WebGL)에서는 Photon이 Shared 모드를 권장해서 정했습니다.

## 멀티플레이 설정 (개발자용)

코드만 받아서는 온라인이 켜지지 않습니다. 처음 한 번 아래를 해 둡니다. (연결되지 않으면 혼자 하기로 동작하므로, 설정 전에도 게임은 열립니다)

| 어디 | 설정 |
| --- | --- |
| 이 PC의 Unity | Fusion Hub에서 Photon **AppId**를 입력합니다. 이 값이 든 `PhotonAppSettings.asset`은 `.gitignore`에 들어 있어 저장소에 올라가지 않으므로 **PC마다 한 번** 입력해야 합니다. |
| Photon 대시보드 (Fusion 앱 > Authentication) | `Custom Server`를 선택하고 인증 주소를 `https://{TitleId}.playfabapi.com/photon/authenticate`로 지정합니다. (Photon 문서는 익명 접속 허용을 끄기를 권합니다) |
| PlayFab Game Manager > Add-ons | **Photon 애드온**을 설치합니다. Realtime App ID에는 Fusion 앱의 AppId를, Chat App ID에는 Chat 타입으로 만든 앱의 ID를 넣습니다. (입력칸이 필수로 요구하며 채팅 기능은 쓰지 않습니다) |
| PlayFab Game Manager > Settings > API Features | `Disable Player Creation`의 Custom ID 항목을 **해제**하고(익명 계정 자동 생성), `Allow client to post player statistics`를 **켭니다**(전적 기록). |
| PlayFab 통계 | `Games`, `Wins`는 **미리 만들지 않습니다.** 집계 방식은 기본값(Last)을 그대로 둡니다. `Sum`으로 바꾸면 코드가 더한 합계를 쓰므로 두 번 더해집니다. |

- PlayFab Title ID는 `PlayFabConfig.cs`에 들어 있습니다. 공개돼도 되는 값입니다.
- **PlayFab Secret Key와 Photon의 secret 값은 코드, 설정 파일, 저장소 어디에도 넣지 않습니다.**

### 여러 클라이언트로 시험하기

- 같은 PC에서 빌드를 여러 개 띄우면 저장 공간을 같이 써서 같은 계정이 됩니다. 두 번째부터는 실행 인자로 번호를 줍니다. 예: `Our_Marble.exe -userSlot 2` (에디터는 저장 공간이 따로라 필요 없습니다)
- `F2`: 개발용 테스트 창 (에디터와 개발 빌드에서만). 연결 상태, PlayFab 로그인, **끊김 흉내**와 **연결 실패 흉내**(인터넷을 끄지 않고 끊김 처리와 혼자 하기 전환을 확인. 연결 실패 흉내는 처음 연결 기준으로 약 12초 뒤에 혼자 하기로 바뀝니다), 이메일 계정 시험이 들어 있습니다.
- 한 PC에서 에디터와 웹으로 시험할 때는 **서로 다른 계정**이어야 합니다. (같은 PlayFab 계정은 한 번에 한 곳에서만 방에 들어갈 수 있습니다. 웹은 시크릿 창이나 다른 브라우저로 구분합니다)
- 방 목록이 서로 보이려면 두 빌드의 **네트워크 버전**이 같아야 합니다. (`PhotonConnection.NetworkVersion`, 현재 3) 방을 다루는 방식이 바뀌면 올려서 옛 빌드와 섞이지 않게 합니다.

### 알려진 한계

- 웹(WebGL)에서는 브라우저가 뒤로 간 탭의 실행을 멈추고 Photon이 약 10초 넘게 신호가 없으면 연결을 끊습니다. 탭이 돌아오면 대기방과 게임 중 모두 같은 방에 자동으로 다시 들어가지만, 방장이 혼자 있는 방은 사라져 돌아올 수 없습니다. 방장은 게임 탭을 앞에 두거나, 창이 뒤로 가도 계속 실행되는 Windows(.exe)를 쓰세요.
- 사람이 혼자(봇만) 있는 방은 그 사람이 끊기면 방이 사라져 돌아올 수 없습니다. (사람이 없는 방을 살려 둘 이유가 없어서 의도한 동작입니다)
- 진행 중인 게임을 다른 기기에서 이어서 할 수는 없습니다. (이어하기는 그 기기에 저장된 방 코드를 씁니다)
- 전적은 클라이언트가 올리는 방식이라 조작할 수 있습니다.
- 이메일 가입에는 인증이 없어서, 오타 이메일로 가입하면 비밀번호를 찾을 수 없습니다.

## 원작 팀

이 프로젝트는 아래 팀이 만든 "우리의 마블"을 바탕으로 합니다.

| 이름 | 담당 |
| --- | --- |
| 김건욱 | 클라이언트 코어·네트워크, 배포 |
| 한수정 | 서버 네트워크·로비, Redis·다중 서버 |
| 이근희 | 서버 게임 로직, 봇, 타이머 |
| 김현규 | 클라이언트 UI·연출, 대기방 채팅 |
| 김희정 | 서버 인증·전적, 보드·캐릭터 비주얼 |

## 개발 시 주의

- 처음 연 뒤 `.meta` 생성과 컴파일 에러를 확인하고 커밋합니다.
- 아래 파일은 Unity가 자동으로 바꾸므로 커밋에서 제외합니다.
  - `Packages/packages-lock.json`
- TextMeshPro 폰트는 **Static**이라 `Assets/Editor/FontChars.txt`에 없는 글자는 네모로 보입니다. 새 글자를 쓰면 이 파일에 추가하고 Font Asset Creator에서 같은 폰트를 다시 만듭니다. (Atlas Population Mode: Static, Character Set: Characters from File) 화면에 새 한국어 문구를 넣을 때는 글자가 이미 있는지 먼저 확인합니다. (예: `넷`은 없어서 "인터넷" 대신 "연결 상태"를 씁니다)
- 같은 파일이 계속 잡히면 `git update-index --skip-worktree <경로>`로 숨깁니다.

## 빌드 (WebGL)

- Build Settings에 씬을 `LoginScene`, `LobbyScene`, `GameScene` 순서로 등록해 둡니다. (`SceneFlow`가 등록된 씬만 불러오고, 씬 이름이 아니라 **경로**로 구분합니다. 씬 파일을 옮기거나 이름을 바꾸면 `SceneFlow`의 경로도 같이 고칩니다)
- Web 빌드 설정에서 Compression Format을 **Disabled**로 둡니다. (itch.io 업로드용)
- **Development Build는 끄고** 빌드합니다. (개발용 패널과 상태 비교 로그는 에디터와 개발 빌드에서만 나옵니다)
- 버전은 `Edit > Project Settings > Player > Other Settings`의 Version에서 올립니다. (현재 1.1)
- 웹 빌드에서도 Photon과 PlayFab 요청이 막히지 않는 것을 v1.1에서 확인했습니다. 배포 환경이나 브라우저가 바뀌면 다시 확인합니다.
- 결과는 `Builds/` 폴더 안에 만듭니다. (`.gitignore`에 포함)
- itch.io에는 결과 폴더의 **내용물**(`index.html` 등)을 zip으로 묶어 올립니다.
- Windows 빌드는 Development Build를 끄고, 빌드 폴더 **전체**(`.exe`, `_Data` 폴더 등)를 zip으로 묶어 올립니다. 서명하지 않은 exe는 처음 실행할 때 Windows 보호(SmartScreen) 경고가 뜨며, "추가 정보 → 실행"으로 넘어갑니다.