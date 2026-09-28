# WARUDO Agent Bridge

[English](README.md) | **한국어** | [日本語](README.ja.md)

실행 중인 [WARUDO](https://warudo.app)를 AI 에이전트가 보고 조작하게 해 줍니다. Claude Code, Codex,
Claude Desktop, Cursor를 비롯한 [MCP](https://modelcontextprotocol.io) 클라이언트에서 쓸 수 있습니다.

말로 부탁하면 클릭은 에이전트가 합니다.

- "씬에 뭐가 있는지 보여 주고, 캐릭터를 정면에서 찍어 줘."
- "이 사진처럼 무릎 꿇은 포즈로 만들고, 유니티 프로젝트에 .anim으로 저장해 줘."
- "바디만 남기고 메쉬 다 꺼 줘. 끝나면 원래대로 돌려 놓고."
- "대기 애니메이션 바꾸고, 카메라를 얼굴로 당겨서 캡처해 줘."

도구 63개로 씬, 에셋, 캐릭터(포즈·IK·표정·블렌드셰이프·애니메이션), 블루프린트, 카메라와 캡처,
Unity 오브젝트를 다룹니다. 목록은 [docs/TOOLS.md](docs/TOOLS.md)에 있습니다.

> WARUDO·HakuyaLabs와 무관한 비공식 도구입니다. MIT 라이선스, StudioRaming 제작.

## 준비물

- Windows 10 또는 11
- WARUDO (Steam). 브릿지는 WARUDO 안에서 Playground 스크립트로 돌아가므로 모드 설치가 필요 없습니다.
- Node.js 20 이상 (없으면 설치 스크립트가 설치를 제안합니다)

## 설치

PowerShell에서:

```powershell
irm https://raw.githubusercontent.com/StudioRaming/warudo-agent-bridge/main/install.ps1 | iex
```

Node.js가 이미 있다면:

```bash
npx --yes @studioraming/warudo-agent-bridge@latest install
```

설치 프로그램이 하는 일:

1. 브릿지를 `%USERPROFILE%\.warudo-agent-bridge`에 복사합니다.
2. Steam에서 WARUDO를 찾아 `Warudo_Data\StreamingAssets\Playground\WarudoAgentBridge\`에 런타임 파일
   하나를 넣습니다. 켜져 있는 WARUDO는 알아서 불러옵니다 (Playground 재컴파일에 몇 초에서 25초쯤 걸립니다).
3. 찾은 클라이언트 전부(Claude Code, Codex, Claude Desktop, Cursor)에 MCP 서버를 등록합니다.
   고치는 설정 파일은 먼저 `%USERPROFILE%\.warudo-agent-bridge\backups`에 백업합니다.

그다음 AI 클라이언트를 다시 켜고 `warudo_status`를 호출해 보라고 하면 됩니다.

옵션: `--clients claude-code,codex`(또는 `none`)로 등록할 클라이언트를 고르고, Steam에서 못 찾으면
`--warudo "D:\...\Warudo"`로 위치를 알려 주고, `--skip-runtime`은 클라이언트 등록만 합니다.
설치 상태는 `npx @studioraming/warudo-agent-bridge doctor`로 점검합니다.

### 직접 등록하기

설치 프로그램이 이 PC에 맞는 명령을 그대로 출력해 줍니다. Claude Code라면 이런 모양입니다.

```bash
claude mcp add -s user warudo-agent-bridge -- node "C:\Users\YOU\.warudo-agent-bridge\app\bin\warudo-agent-bridge.mjs" mcp
```

JSON 설정을 쓰는 클라이언트는 `{"mcpServers": {"warudo-agent-bridge": {"command": "node", "args": ["C:\\Users\\YOU\\.warudo-agent-bridge\\app\\bin\\warudo-agent-bridge.mjs", "mcp"]}}}`입니다.

## 동작 방식

```
AI 클라이언트 ──stdio──> MCP 서버 (Node) ──파일──> WARUDO 안의 런타임 (Playground C#)
                         %USERPROFILE%\.warudo-agent-bridge\runtime
```

- **네트워크를 쓰지 않습니다.** 요청과 응답은 사용자 폴더 안의 파일입니다. 포트를 열지 않습니다.
- **쓰기는 한 번에 한 에이전트.** WARUDO를 바꾸는 도구는 짧은 쓰기 잠금(lease)이 필요합니다. MCP 서버가
  알아서 잡고, 두 번째 에이전트는 씬을 두고 다투는 대신 "사용 중"이라는 답을 받습니다.
- **결과를 부풀리지 않습니다.** 요청은 실행 전에 접수 표시를 합니다. 도중에 WARUDO가 꺼지거나 브릿지가
  다시 로드되면 `NOT_EXECUTED`(다시 보내도 안전) 또는 `UNKNOWN`(상태를 다시 읽어 확인)으로 답합니다.
  읽기와 절댓값 쓰기는 재로드를 넘어 한 번 자동으로 재시도합니다.
- **되돌릴 길을 남깁니다.** 씬 열기, 스냅샷 복원, 에셋 삭제 전에는 복구용 스냅샷을 저장하고,
  저장된 씬을 덮어쓸 때는 이전 파일을 남깁니다.

## 캐릭터 포즈

포즈 도구는 Unity 휴머노이드 기준이라 VRM이든 일반 휴머노이드 아바타든 그대로 맞습니다.

- `warudo_character_pose_set`은 뼈 조준(`{"LeftUpperArm": {"aim": [-1, -0.2, 0]}}`, x 오른쪽·y 위·
  z 앞)과 머슬 값을 받아 계층 순서(부모 먼저)대로 적용합니다. `base: "standing"`은 아바타 자신의 기본
  자세에서 시작하고, `hipsHeight`로 골반 높이를 미터로 정합니다(무릎 꿇고 선 자세는 약 0.35).
- 관절은 아바타의 가동 범위를 넘지 않습니다. 조준이 범위를 넘으면 응답의 `limited`에 적어 주므로
  에이전트가 그 조준을 줄일 수 있습니다.
- 캐릭터에 포즈가 실제로 보인 뒤에 응답합니다(`shown`). 바로 이어서 찍는 캡처가 정확합니다.
- `warudo_analysis_capture`는 재질과 배경 없이 부위별 색으로 몸을 여러 방향에서 한 번에 렌더하고,
  손의 접촉, 부위별 바닥 높이, 카메라 화면에서 각 관절의 위치를 알려 줍니다. 에이전트는 셰이딩된
  스크린샷을 짐작하는 대신 이것으로 레퍼런스 이미지와 포즈를 맞춰 봅니다.
- `warudo_character_pose_save`는 포즈를 Unity 휴머노이드 `.anim`으로 저장합니다. WARUDO 애니메이션
  목록에 넣거나 원하는 폴더(예: 유니티 프로젝트)에 씁니다.
- `warudo_character_pose_clear`는 원래 대기 애니메이션으로 돌려 놓습니다.

## 설정

`%USERPROFILE%\.warudo-agent-bridge\config.json` ([config.example.json](config.example.json)에서 만들어짐):

- `componentCall.allow`: 에이전트가 호출해도 되는 Unity 컴포넌트 메서드.
  `{"typePrefix": "My.Namespace.", "methods": ["Refresh"]}` 형식으로 적습니다. 기본은 비어 있어서
  `warudo_component_call`은 꺼져 있습니다.
- `retention`: 응답, 캡처, 스냅샷, 포즈 미리보기를 `warudo_maintenance_cleanup`이 지우기 전까지
  보관하는 기간.

## 명령줄

AI 클라이언트 없이도 같은 기능을 쓸 수 있습니다.

```bash
npx @studioraming/warudo-agent-bridge status
npx @studioraming/warudo-agent-bridge call scene.get
npx @studioraming/warudo-agent-bridge tools
```

설치 후에는 `%USERPROFILE%\.warudo-agent-bridge\bin\warudo-agent.cmd`가 같은 명령줄의 바로가기입니다.

## 제거

```bash
npx @studioraming/warudo-agent-bridge uninstall
```

WARUDO Playground에서 런타임을 빼고(완전히 내리려면 WARUDO 재시작) 클라이언트의 MCP 항목을 백업과 함께
지웁니다. `--purge`를 붙이면 `backups` 폴더만 남기고 `%USERPROFILE%\.warudo-agent-bridge`도 지웁니다.

## 문제 해결

- **`warudo_status`가 브릿지가 꺼져 있다고 할 때**: WARUDO를 켜고 Playground 컴파일이 끝나길 기다린 뒤
  `doctor`를 실행하세요.
- **WARUDO를 못 찾을 때**: `Warudo.exe`가 있는 폴더를 `--warudo`로 넘기거나 `WARUDO_PATH`를 설정하세요.
- **Codex가 오래 걸리는 호출을 끊을 때**: 설치 프로그램이 이 서버에 `tool_timeout_sec = 180`을 넣습니다.
  예전 설정이면 설치를 한 번 더 실행하세요.
- **그 밖의 문제**: `warudo_runtime_logs`로 WARUDO 최근 로그를 볼 수 있고, 문의는
  [github.com/StudioRaming/warudo-agent-bridge/issues](https://github.com/StudioRaming/warudo-agent-bridge/issues)에 남겨 주세요.

## 개발

[CONTRIBUTING.md](CONTRIBUTING.md)를 보세요. 보안 문제 제보는 [SECURITY.md](SECURITY.md)를 따라 주세요.

## 라이선스

[MIT](LICENSE) © StudioRaming
