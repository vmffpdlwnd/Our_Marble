# 우리의 마블 포지 (Our_Marble)

> [Our-Marble/our-marble-client](https://github.com/Our-Marble/our-marble-client)를 포크한 프로젝트입니다.
> 원본의 화면·연출·규칙을 바탕으로, **별도 서버 없이 방장이 게임 규칙을 돌리는 멀티플레이**(Photon Fusion Shared 모드)로 개조합니다.

**[지금 플레이하기 (itch.io)](https://vmffpdlwnd35.itch.io/our-marble)** · 브라우저에서 바로 실행됩니다.

## 소개

"모두의 마블"을 모티브로 한 **2~4인 온라인 보드게임**입니다. 서버 프로그램 없이 게임 파일 하나만 켜서 친구와 바로 플레이하는 것이 목표입니다.

## 원본과 달라지는 점

| 항목 | 원본 | 이 프로젝트 |
| --- | --- | --- |
| 구조 | Java/Spring 전용 서버가 판정 | 방장(마스터 클라이언트)이 판정 |
| 멀티 | WebSocket 직접 구현 | Photon Fusion (Shared 모드) |
| 로그인 | JWT, 이메일 인증 | 닉네임 입력 |
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
- [ ] Photon Fusion 연결 (Shared 모드: 멀티, 방장 승계, 재접속)
- [ ] v1.1 배포

## 현재 상태

**v1.0은 혼자(봇과 함께) 하는 버전입니다.** 서버 코드는 모두 제거되어 브라우저(WebGL)에서 서버 없이 실행됩니다. 친구와 함께 하는 멀티플레이는 개발 중입니다.

## 기술 스택

Unity 6 (6000.3), C#, URP 2D, uGUI, TextMesh Pro, DOTween, Newtonsoft.Json

멀티플레이용 Photon Fusion(Shared 모드)은 도입 예정입니다. 웹(WebGL)에서는 Photon이 Shared 모드를 권장해서 정했습니다.

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
- TextMeshPro 폰트는 **Static**이라 `Assets/Editor/FontChars.txt`에 없는 글자는 네모로 보입니다. 새 글자를 쓰면 이 파일에 추가하고 Font Asset Creator에서 같은 폰트를 다시 만듭니다. (Atlas Population Mode: Static, Character Set: Characters from File)
- 같은 파일이 계속 잡히면 `git update-index --skip-worktree <경로>`로 숨깁니다.

## 빌드 (WebGL)

- Build Settings에 씬을 `LoginScene`, `LobbyScene`, `GameScene` 순서로 등록해 둡니다. (`SceneFlow`가 등록된 씬만 불러오고, 씬 이름이 아니라 **경로**로 구분합니다. 씬 파일을 옮기거나 이름을 바꾸면 `SceneFlow`의 경로도 같이 고칩니다)
- Web 빌드 설정에서 Compression Format을 **Disabled**로 둡니다. (itch.io 업로드용)
- 결과는 `Builds/` 폴더 안에 만듭니다. (`.gitignore`에 포함)
- itch.io에는 결과 폴더의 **내용물**(`index.html` 등)을 zip으로 묶어 올립니다.