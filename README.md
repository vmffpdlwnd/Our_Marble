# 우리의 마블 포지 (Our_Marble)

> [Our-Marble/our-marble-client](https://github.com/Our-Marble/our-marble-client)를 포크한 프로젝트입니다.
> 원본의 화면·연출·규칙을 바탕으로, **별도 서버 없이 방장 PC가 호스트가 되는 멀티플레이**로 개조합니다.

**[지금 플레이하기 (itch.io)](https://vmffpdlwnd35.itch.io/our-marble)** · 브라우저에서 바로 실행됩니다.

## 소개

"모두의 마블"을 모티브로 한 **2~4인 온라인 보드게임**입니다. 서버 프로그램 없이 게임 파일 하나만 켜서 친구와 바로 플레이하는 것이 목표입니다.

## 원본과 달라지는 점

| 항목 | 원본 | 이 프로젝트 |
| --- | --- | --- |
| 구조 | Java/Spring 전용 서버가 판정 | 방장 클라가 판정 (호스트 방식) |
| 멀티 | WebSocket 직접 구현 | Photon Fusion (Host Mode) |
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
- [ ] 규칙 분리 (`Rules/`)
- [ ] 폴더와 씬 정리
- [ ] Photon Fusion 연결 (멀티, 재접속, 호스트 이전)
- [ ] v1.1 배포

## 현재 상태

**v1.0은 혼자(봇과 함께) 하는 버전입니다.** 서버 코드는 모두 제거되어 브라우저(WebGL)에서 서버 없이 실행됩니다. 친구와 함께 하는 멀티플레이는 개발 중입니다.

## 기술 스택

Unity 6 (6000.3), C#, URP 2D, uGUI, TextMesh Pro, DOTween, Newtonsoft.Json

멀티플레이용 Photon Fusion은 도입 예정입니다.

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
  - TextMeshPro 폰트 에셋 `*SDF.asset`
- 같은 파일이 계속 잡히면 `git update-index --skip-worktree <경로>`로 숨깁니다.

## 빌드 (WebGL)

- Build Settings에 씬을 `UI_LoginScene`, `UI_LobbyScene`, `BoardLayout 2` 순서로 등록해 둡니다. (`SceneFlow`가 등록된 씬만 불러옵니다)
- Web 빌드 설정에서 Compression Format을 **Disabled**로 둡니다. (itch.io 업로드용)
- 결과는 `Builds/` 폴더 안에 만듭니다. (`.gitignore`에 포함)
- itch.io에는 결과 폴더의 **내용물**(`index.html` 등)을 zip으로 묶어 올립니다.