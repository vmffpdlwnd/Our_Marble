# CLAUDE.md

AI(Claude)가 이 저장소에서 작업하기 전에 가장 먼저 읽는 안내서입니다. 규칙과 "무엇이 어디에 있는지"만 짧게 적습니다. 자세한 내용은 아래 문서로 안내합니다.

## 먼저 할 일

1. 이 파일, [`docs/작업규칙.md`](docs/작업규칙.md), [`docs/작업현황.md`](docs/작업현황.md)를 읽는다.
2. `git log -10`으로 최신 커밋을 확인한다.
3. **사용자가 "다음 작업 하자"라고 말하기 전에는 작업을 시작하지 않는다.** 그 전은 의견을 나누는 것이다. ("todo"는 다음 작업을 정리해 보자는 뜻이고, 작업 시작이 아니다.)

## 프로젝트

- "모두의 마블"을 모티브로 한 2~4인 온라인 보드게임. 원본 [our-marble-client](https://github.com/Our-Marble/our-marble-client)를 포크해, 서버 없이 **방장이 규칙을 돌리는** 멀티플레이(Photon Fusion Shared 모드)로 바꿨다. 계정과 전적은 PlayFab(REST).
- Unity 6 (6000.3), C#, URP 2D, uGUI, TextMesh Pro, DOTween. **웹(WebGL)과 Windows(.exe)를 둘 다** 지원한다.
- 배포 버전과 할 일은 [`docs/작업현황.md`](docs/작업현황.md), 구조와 규칙은 [`docs/기획서.md`](docs/기획서.md), 개발 설정은 [`README.md`](README.md).

## 환경 제약 (Claude가 못 하는 것)

- Unity 에디터와 웹 빌드를 돌릴 수 없다. 코드는 쓸 수 있지만 **동작은 사용자가 에디터와 웹 빌드로 확인**한다. 확인하지 못한 것은 확인하지 못했다고 밝힌다.
- 씬(`.unity`)과 프리팹은 직접 고치지 않는다. 필요하면 에디터에서 할 일을 위치와 함께 안내한다.
- 새 파일은 사용자가 Unity에서 먼저 만들어야 `.meta`가 생긴다. 그래서 추가할 파일 목록을 작업 시작 때 먼저 알려 준다.
- `Pretendard Dynamic SDF.asset`(동적 글꼴)은 Unity가 자동으로 바꾸므로 가능하면 커밋에서 제외한다. (`FallbackFontTools`가 비워 준다)

## 코딩 규칙

- 주석과 설명은 한글로, 무엇을 하는지보다 **왜 그렇게 하는지**를 적는다. 기존 파일의 `/// <summary>` 스타일을 따른다.
- 웹 전용 코드는 `#if UNITY_WEBGL && !UNITY_EDITOR`와 `Assets/Plugins/WebGL/` 폴더로 분리해, Windows와 에디터 동작을 바꾸지 않는다.
- 수정 위치는 줄 번호가 아니라 **"~ 함수 밑에", "~ 주석 위에"**처럼 함수나 주석 이름으로 안내한다. (줄 번호는 금방 어긋난다)
- 호스트와 클라이언트가 같은 규칙 코드(`Rules/`)를 쓴다. 규칙은 화면(Unity 오브젝트)을 몰라야 한다.
- `PlayFab Secret Key`와 Photon secret 값은 코드, 설정 파일, 저장소 어디에도 넣지 않는다.

## 문서 갱신

- 파일을 추가, 이동, 삭제하면 이 파일의 "코드 지도"도 같이 고쳐서 완성 파일에 포함한다.
- 작업이 끝나면 [`docs/작업현황.md`](docs/작업현황.md)도 같이 갱신한다.
- README와 기획서를 고칠 때는 부분이 아니라 **풀버전**으로 올린다.

## 코드 지도

씬은 `Assets/01.Scenes`의 `LoginScene`, `LobbyScene`, `GameScene` 순서로 이어진다. (`LobbyScene_old`는 옛 파일) 씬 이동은 `SceneFlow`가 맡는다.

### 기능 → 파일

| 기능 | 어디 |
| --- | --- |
| 타이틀, 닉네임 입력, 시작 | `Manager/TitleManager` |
| 이메일 가입과 로그인 카드 | `Manager/AccountCards`, 규칙은 `Network/PlayFab/SignupRules` |
| 로비: 방 목록, 방 만들기, 방 이름 입력 | `Manager/LobbyManager`, 방 이름 규칙은 `State/RoomNames` |
| 방 코드로 입장 | `UI/Views/JoinByCodePopup`, 코드 규칙은 `Network/Photon/RoomCodeRules` |
| 방 코드 복사(칩을 눌렀을 때) | `LobbyManager`의 `CopyRoomCode` → `Util/ClipboardCopy` |
| 대기방 화면(준비, 설정, 채팅) | `UI/Views/RoomSetupView`, 채팅 창구는 `Chat/ChatService` |
| 게임 씬 진행(알림 받아 화면 연출) | `Manager/GameManager`, 화면 표시는 `Screen/GameScreen`, UI 진입점은 `Manager/UIManager` |
| 게임 규칙(구매, 통행료, 카드, 파산, 순위) | `Rules/` (`TradeRules`, `GameRules`, `CardRules`, `ArrivalRules`, `PropertyRules`, `RankingRules`, `TimeRules`) |
| 호스트가 하는 일(게임 진행, 로비 관리) | `Host/HostGameService`, `Host/LocalLobbyHost`, 봇과 제한 시간은 `Host/HostAutoPlayRunner` |
| 봇 | `Bot/` (`BasicBotStrategy`, 성향 값은 `BotSettings`) |
| Photon 연결, 재접속, 방장 승계 | `Network/Photon/` (`PhotonConnection`, `PhotonRejoinLoop`, `PlayerRegistry`) |
| 요청과 알림 메시지 | `Network/DTO/`, 보내고 받는 입구는 `Network/GameNetwork`, `Network/LobbyNetwork` |
| 계정과 전적(PlayFab) | `Network/PlayFab/` (`PlayFabIdentity`, `PlayFabProfile`, `PlayFabRest`, `GameRecord`) |
| 방과 게임 상태 데이터 | `State/` (`GameState`, `PlayerState`, `RoomSnapshot`, `RoomSummary`) |
| 보드, 땅, 카드 데이터 | `Board/`, `Property/`, `Card/` (데이터는 `Assets/04.ScriptableObject`) |
| 웹 한글 입력 | `UI/Components/WebIMEInput` + `Assets/Plugins/WebGL/WebIMEInput.jslib` |
| 웹 클립보드 복사 | `Util/ClipboardCopy` + `Assets/Plugins/WebGL/WebClipboard.jslib` |
| 글꼴 도구, 동적 대체 글꼴 자동 비우기 | `Assets/Editor/FallbackFontTools`, `ExtractUsedChars` |
| 개발용 창(에디터와 개발 빌드에서만) | `F1`: `UI/Debug/UIDebugPanel`, `F2`: `Network/Photon/PhotonDebugPanel` |

### 폴더

| 폴더 (`Assets/02.Scripts/`) | 역할 |
| --- | --- |
| `Manager/` | 씬별 매니저(타이틀, 로비, 게임)와 씬 이동 |
| `Rules/` | 호스트와 클라가 같이 쓰는 게임 규칙. 화면을 모른다 |
| `Host/` | 예전에 서버가 하던 일(방장 PC에서 실행) |
| `Network/` | Photon, PlayFab, 요청과 알림(DTO) |
| `State/` | 게임과 방의 상태 데이터 |
| `UI/` | 뷰(`Views`), 공통 컴포넌트(`Components`), 색과 연출(`Core`), 개발용 창(`Debug`) |
| `Board`, `Property`, `Card`, `Bot`, `Chat`, `Portrait`, `Screen`, `Util` | 이름 그대로 |

### 참고

- `Manager/LobbyManager_temp`는 가짜 방 데이터로 돌던 옛 임시 파일이고, 참조하는 코드가 없다. 현재 로비는 `LobbyManager`다.
- 네트워크 버전은 `PhotonConnection.NetworkVersion`(현재 3). 방을 다루는 방식이 바뀌면 올려서 옛 빌드와 섞이지 않게 한다.
- 호스트 권위 방식과 요청 검증 순서는 [`docs/기획서.md`](docs/기획서.md) 5장.