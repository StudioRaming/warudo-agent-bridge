# WARUDO Agent Bridge

[English](README.md) | **한국어** | [日本語](README.ja.md)

**AI에게 말로 부탁하면, AI가 WARUDO를 대신 조작해 줍니다.**

"웃는 얼굴로 손 흔드는 포즈 잡고, 정면에서 한 장 찍어 줘." 이렇게 채팅으로 보내면 AI가 WARUDO에서 카메라를 옮기고,
표정을 바꾸고, 포즈를 잡고, 사진을 찍어서 보여 줍니다. 코딩을 몰라도 됩니다. 설치할 때 명령어 한 줄만 붙여 넣으면 됩니다.

<p>
<img src="docs/images/photo-full.jpg" width="24%" alt="정면 전신 사진">
<img src="docs/images/face-wink.jpg" width="24%" alt="윙크 표정">
<img src="docs/images/pose-wave.jpg" width="24%" alt="손 흔드는 포즈">
<img src="docs/images/pose-kneel.jpg" width="24%" alt="무릎 꿇은 포즈">
</p>

<sub>위 사진은 전부 WARUDO 기본 캐릭터(Shipilka)로 아래 [예시](#무엇을-할-수-있나요)의 부탁을 이 브릿지로 실제로 실행해서 찍은 화면입니다.</sub>

> WARUDO·HakuyaLabs와 무관한 비공식 도구입니다. 무료(MIT 라이선스), StudioRaming 제작.

## 목차

1. [MCP가 뭔가요? (처음이라면 여기부터)](#mcp가-뭔가요)
2. [무엇을 할 수 있나요? (예시 모음)](#무엇을-할-수-있나요)
3. [준비물](#준비물)
4. [설치하기 (따라 하기)](#설치하기)
5. [AI 앱에 연결하기](#ai-앱에-연결하기): Claude 데스크톱 앱, Claude Code, Codex, ChatGPT 데스크톱 앱
6. [처음 써 보기](#처음-써-보기)
7. [안전하게 쓰기](#안전하게-쓰기)
8. [자주 묻는 질문](#자주-묻는-질문)
9. [문제가 생겼을 때](#문제가-생겼을-때)
10. [더 알아보기 (고급)](#더-알아보기-고급)

## MCP가 뭔가요?

ChatGPT나 Claude 같은 AI는 원래 **글로 대화만** 할 수 있습니다. 내 컴퓨터에 켜져 있는 프로그램의 버튼을 직접
누르지는 못해요.

**MCP**(Model Context Protocol)는 AI에게 **도구를 쥐여 주는 공통 규격**입니다. USB를 떠올리면 쉬워요. 규격이
같으니 어떤 AI 앱에든 꽂아 쓸 수 있습니다.

**WARUDO Agent Bridge**는 WARUDO용 MCP 도구 모음입니다. 설치하면 AI가 "카메라 옮기기", "표정 바꾸기",
"포즈 잡기", "사진 찍기" 같은 도구 66개를 쓸 수 있게 됩니다.

```
나 (채팅으로 부탁)
 ↓
AI 앱 (Claude, Codex, ChatGPT 데스크톱)
 ↓ MCP
WARUDO Agent Bridge (내 PC 안에서 동작)
 ↓
WARUDO
```

알아 두면 좋은 것:

- **AI 앱과 WARUDO가 같은 PC에서 켜져 있어야 합니다.** 브릿지는 내 PC 안의 파일로만 WARUDO와 주고받고,
  인터넷 포트를 열지 않습니다.
- **대화 내용은 AI 서비스로 갑니다.** 내가 보낸 말과 AI가 읽은 결과(씬 정보, 캡처한 사진 포함)는 평소 채팅처럼
  그 AI 회사의 서버에서 처리됩니다.
- **브릿지는 무료입니다.** AI 앱 이용료는 각 서비스의 요금제를 따릅니다.
- **Windows 전용입니다.** WARUDO가 Windows 프로그램이기 때문입니다.

## 무엇을 할 수 있나요?

AI에게 보낸 말과 그 결과입니다. 사진은 각 부탁에 맞는 도구를 WARUDO 기본 캐릭터(Shipilka)에게 이 브릿지로
실제로 실행해서 찍었고, AI의 답은 짧게 줄여서 옮겼습니다. "쓴 도구"는 AI가 알아서 고르는 것이라 외울 필요가
없습니다.

### 📸 사진과 카메라

| 💬 AI에게 보낸 말 | 🤖 결과 |
| --- | --- |
| **"캐릭터 전신을 정면에서 찍어 줘."** | <img src="docs/images/photo-full.jpg" width="260" alt="정면 전신"><br>"카메라를 캐릭터 정면으로 옮겨서 찍었어요."<br><sub>쓴 도구: `warudo_camera_pose` → `warudo_camera_capture`</sub> |
| **"얼굴만 크게 클로즈업해서 찍어 줘."** | <img src="docs/images/photo-face.jpg" width="260" alt="얼굴 클로즈업"><br>"머리 뼈 위치를 읽어서 얼굴 앞으로 카메라를 붙였어요."<br><sub>쓴 도구: `warudo_character_pose_get` → `warudo_camera_pose` → `warudo_camera_capture`</sub> |
| **"아래에서 올려다보는 멋있는 구도로 찍어 줘."** | <img src="docs/images/photo-low.jpg" width="260" alt="로우 앵글"><br>"무릎 높이에서 비스듬히 올려다보게 찍었어요."<br><sub>쓴 도구: `warudo_camera_pose` → `warudo_camera_capture`</sub> |

**"앞, 대각선, 옆, 뒤에서 한 장씩 찍어서 보여 줘."**

<img src="docs/images/camera-turnaround.jpg" width="720" alt="네 방향에서 찍은 사진">

"화각을 60도로 넓히고 캐릭터 둘레를 돌면서 네 장 찍었어요."<br>
<sub>쓴 도구: `warudo_camera_zoom` → `warudo_camera_pose` → `warudo_camera_capture` (네 번 반복, 여기서는 네 장을 한 장으로 붙여 실었습니다)</sub>

### 😊 표정

캐릭터마다 표정 이름이 다르기 때문에 AI가 먼저 캐릭터의 표정 목록(블렌드셰이프)을 읽고, 부탁에 맞는 것을 골라서
켭니다. 이 캐릭터에는 106개가 있었습니다.

| 💬 AI에게 보낸 말 | 🤖 결과 |
| --- | --- |
| **"활짝 웃게 해 줘."** | <img src="docs/images/face-smile.jpg" width="260" alt="웃는 표정"><br>"웃는 눈(笑い), 웃는 눈썹(にこり), 입꼬리(口角上げ), 벌린 입(あ), 볼 홍조(照れ)를 켰어요."<br><sub>쓴 도구: `warudo_character_blendshapes` → `warudo_character_blendshape`</sub> |
| **"윙크하면서 혀 살짝 내밀어 줘."** | <img src="docs/images/face-wink.jpg" width="260" alt="윙크 표정"><br>"윙크(ウィンク)와 혀 내밀기(てへぺろ)를 켰어요."<br><sub>쓴 도구: `warudo_character_blendshape`</sub> |
| **"깜짝 놀란 표정!"** | <img src="docs/images/face-surprised.jpg" width="260" alt="놀란 표정"><br>"놀란 눈(びっくり), 놀란 입(口_驚き), 올린 눈썹(上)을 켰어요."<br><sub>쓴 도구: `warudo_character_blendshape`</sub> |
| **"표정 원래대로 돌려 줘."** | "제가 덮어쓴 표정 값을 전부 지웠어요. 원래 표정으로 돌아왔어요."<br><sub>쓴 도구: `warudo_character_blendshape` (`clear`)</sub> |

### 🙋 포즈

포즈는 "팔을 어느 방향으로 뻗어라" 같은 방향으로 잡습니다. 관절이 사람이 못 하는 각도로 꺾이지 않게 브릿지가
막아 주고, 포즈가 화면에 실제로 보인 다음에 답을 돌려줍니다.

| 💬 AI에게 보낸 말 | 🤖 결과 |
| --- | --- |
| **"손 흔드는 포즈 만들어 줘."** | <img src="docs/images/pose-wave.jpg" width="260" alt="손 흔들기"><br>"오른팔을 옆으로 들고 팔꿈치를 세웠어요. 고개도 살짝 기울였어요."<br><sub>쓴 도구: `warudo_character_pose_set`</sub> |
| **"만세 해 줘!"** | <img src="docs/images/pose-banzai.jpg" width="260" alt="만세"><br>"양팔을 위로 쭉 뻗고 고개를 살짝 들었어요."<br><sub>쓴 도구: `warudo_character_pose_set`</sub> |
| **"양손 허리에 올리고 당당하게 서 줘."** | <img src="docs/images/pose-hips.jpg" width="260" alt="허리에 손"><br>"팔꿈치를 바깥으로 빼고 손을 허리에 댔어요. 가슴을 펴고 턱을 조금 들었어요."<br><sub>쓴 도구: `warudo_character_pose_set`</sub> |
| **"무릎 꿇고 앉아서 두 손 모아 줘."** | <img src="docs/images/pose-kneel.jpg" width="260" alt="무릎 꿇기"><br>"골반을 바닥에서 33.5cm 높이로 내리고 정강이를 바닥에 눕혔어요. 두 손은 앞으로 모았어요."<br><sub>쓴 도구: `warudo_character_pose_set` (`hipsHeight`)</sub> |

**"포즈가 제대로 됐는지 확인해 줘. 발이 바닥에 파묻히진 않았어?"**

<img src="docs/images/analysis-kneel.jpg" width="640" alt="분석 렌더: 정면과 오른쪽에서 본 뼈대">

"옷과 배경을 빼고 뼈대를 앞과 옆에서 그려 봤어요. 무릎과 발은 바닥에 닿았고 신발 끝이 0.5cm쯤 들어가 있어요.
꼬리 장식은 바닥 아래 18cm까지 내려가 있어서 앉은 자세에서는 바닥에 묻혀 보일 거예요."<br>
<sub>쓴 도구: `warudo_analysis_capture` (WARUDO 화면은 바뀌지 않습니다)</sub>

| 💬 AI에게 보낸 말 | 🤖 결과 |
| --- | --- |
| **"이 포즈를 유니티에서 쓰게 .anim 파일로 저장해 줘."** | "`Kneel_Readme.anim`(70KB)으로 저장했어요. 유니티 프로젝트에 넣으면 휴머노이드 애니메이션으로 쓸 수 있어요."<br><sub>쓴 도구: `warudo_character_pose_save`</sub> |
| **"포즈 풀고 원래 대기 동작으로 돌려 줘."** | "포즈를 멈추고 원래 대기 애니메이션(AGIA_Idle_generic_01)으로 돌려놨어요."<br><sub>쓴 도구: `warudo_character_pose_clear`</sub> |

### 💃 애니메이션과 의상

| 💬 AI에게 보낸 말 | 🤖 결과 |
| --- | --- |
| **"신나게 춤추게 해 줘."** | <img src="docs/images/anim-dance.jpg" width="260" alt="춤추는 모습"><br>"WARUDO 기본 댄스 중 '하이 리듬 댄스 01'을 대기 애니메이션으로 틀었어요."<br><sub>쓴 도구: `warudo_resource_list` → `warudo_character_animation`</sub> |
| **"모자랑 날개 벗겨 줘."** | <img src="docs/images/outfit-off.jpg" width="260" alt="모자와 날개를 끈 모습"><br>"캐릭터 안에서 모자(HeadItem_Hat, HeadItem_Halo)와 날개(Item_Wing, Item_SmallWing) 오브젝트를 찾아서 껐어요."<br><sub>쓴 도구: `warudo_hierarchy_list` → `warudo_object_active`</sub> |

### 🔎 씬 살펴보기와 되돌리기

| 💬 AI에게 보낸 말 | 🤖 결과 |
| --- | --- |
| **"WARUDO랑 잘 연결됐어?"** | "연결됐어요. 브릿지 0.2.0이 WARUDO 안에서 돌고 있고, 지금 열린 씬 이름도 확인했어요."<br><sub>쓴 도구: `warudo_status`</sub> |
| **"지금 씬에 뭐가 있어?"** | "카메라 1, 환경, 디렉셔널 라이트, 캐릭터 1(Shipilka), FPS 카운터, 초보자 설정 도우미가 있어요."<br><sub>쓴 도구: `warudo_scene_get`</sub> |
| **"블루프린트 뭐 있어?"** | "'새 블루프린트' 두 개가 켜져 있는데 둘 다 노드가 없는 빈 블루프린트예요."<br><sub>쓴 도구: `warudo_blueprint_list`</sub> |
| **"이것저것 바꿔 볼 거니까 지금 상태부터 저장해 둬."** | "복구용 스냅샷을 만들었어요. 씬 파일은 건드리지 않았어요. 되돌리고 싶으면 말해 주세요."<br><sub>쓴 도구: `warudo_scene_snapshot` (되돌릴 때 `warudo_scene_restore`)</sub> |

### 💡 이런 부탁도 됩니다

| 부탁 예시 | AI가 쓰는 도구 |
| --- | --- |
| "캐릭터를 오른쪽으로 1미터 옮기고 카메라 쪽을 보게 돌려 줘." | `warudo_asset_transform` |
| "조명을 노을처럼 주황색으로 바꾸고 조금 밝게 해 줘." | `warudo_asset_fields` → `warudo_asset_patch` |
| "배경을 다른 환경으로 바꿔 줘. 뭐가 있는지 먼저 보여 주고." | `warudo_resource_list` → `warudo_asset_source` → `warudo_asset_wait_loaded` |
| "손 흔드는 동작 한 번만 재생해 줘." | `warudo_character_animation` (`oneShot`) |
| "오른손이 이 소품에 닿게 뻗어 줘." | `warudo_character_ik_set` |
| "이 사진처럼 포즈 잡아 줘." (사진 첨부) | `warudo_analysis_capture` → `warudo_character_pose_set` 반복 |
| "옷은 다 끄고 몸만 남겨서 보여 줘. 끝나면 다시 켜 줘." | `warudo_hierarchy_list` → `warudo_object_active` |
| "WARUDO 화면을 UI까지 통째로 캡처해 줘." | `warudo_screen_capture` |
| "방금 에러 난 것 같은데 WARUDO 로그 보고 원인 찾아 줘." | `warudo_runtime_logs` |
| "지금 씬을 '방송용_테스트'라는 이름으로 따로 저장해 줘." | `warudo_scene_save_as` |
| "씬 목록 보여 주고 어제 쓰던 씬 열어 줘." | `warudo_scene_list` → `warudo_scene_open` |
| "블루프린트에서 이 노드 값 바꿔 줘." | `warudo_blueprint_get` → `warudo_node_patch` |

전체 도구 66개는 [docs/TOOLS.md](docs/TOOLS.md)에 있습니다.

**부탁 잘하는 요령**

- 결과를 눈으로 보고 싶으면 **"찍어서 보여 줘"** 를 붙이세요. AI가 사진을 찍어 채팅에 보여 줍니다.
- 마음에 안 들면 이어서 고쳐 달라고 하면 됩니다. "팔을 조금 더 올려", "카메라를 조금 왼쪽으로".
- 한 번에 여러 개를 부탁해도 됩니다. "웃는 얼굴로 손 흔들고, 정면에서 찍어 줘."
- 끝나면 **"원래대로 돌려 줘"** 한마디로 표정·포즈·카메라를 되돌릴 수 있습니다.

## 준비물

| 준비물 | 설명 |
| --- | --- |
| Windows 10 또는 11 PC | |
| [WARUDO](https://store.steampowered.com/app/2079120/WARUDO/) (Steam) | 모드 설치는 필요 없습니다. 브릿지는 WARUDO의 Playground 기능으로 돌아갑니다. |
| AI 앱 하나 | Claude 데스크톱 앱, Claude Code, Codex, ChatGPT 데스크톱 앱, Cursor 중 하나. [연결 방법](#ai-앱에-연결하기) |
| Node.js 20 이상 | 몰라도 됩니다. 없으면 설치 프로그램이 대신 설치할지 물어봅니다. |

**AI 앱을 먼저 설치하고 한 번 실행한 다음** 브릿지를 설치하세요. 설치 프로그램이 이 PC에 있는 AI 앱을 찾아서
자동으로 연결하기 때문입니다.

## 설치하기

### 1단계: PowerShell 열기

- **Windows 11**: 화면 아래 시작 버튼(윈도우 로고)을 **마우스 오른쪽 클릭** → **터미널**
- **Windows 10**: 시작 버튼을 **마우스 오른쪽 클릭** → **Windows PowerShell**

검은색(또는 파란색) 창이 뜨면 됩니다. 관리자 권한은 필요 없습니다.

### 2단계: 설치 명령 붙여 넣기

아래 한 줄을 복사해서 PowerShell 창에 붙여 넣고(`Ctrl+V` 또는 마우스 오른쪽 클릭) `Enter`를 누르세요.

```powershell
irm https://raw.githubusercontent.com/StudioRaming/warudo-agent-bridge/main/install.ps1 | iex
```

이 명령은 이 저장소의 [install.ps1](install.ps1)을 내려받아 실행합니다. 무엇을 하는지 먼저 보고 싶으면 링크를
열어 보세요.

### 3단계: 질문이 나오면 답하기

Node.js가 없으면 이렇게 물어봅니다.

```
Install Node.js LTS now with winget? [Y/n]
```

`Y`를 입력하고 `Enter`를 누르세요. Windows가 "이 앱이 디바이스를 변경할 수 있도록 허용하시겠어요?"라고 물으면
**예**를 누릅니다. 설치가 끝났는데 "Open a new PowerShell window and run the installer again"이라고 나오면
PowerShell 창을 닫고 새로 열어서 2단계를 한 번 더 하면 됩니다.

### 4단계: 결과 확인하기

끝나면 이런 화면이 나옵니다(찾은 AI 앱에 따라 줄 수가 다릅니다).

```
WARUDO Agent Bridge 0.2.0
  home:     C:\Users\YOU\.warudo-agent-bridge
  runtime:  C:\...\Warudo\Warudo_Data\StreamingAssets\Playground\WarudoAgentBridge\WarudoAgentBridge.cs
  claude-code     registered (user scope)
  codex           registered
  claude-desktop  registered Restart Claude Desktop to load it.

Next: start WARUDO. A running WARUDO loads the new bridge by itself once Playground recompiles (up to about 30 seconds).
Open a new agent session and ask it to call warudo_status. Check anything odd with: warudo-agent-bridge doctor
```

- `runtime:` 줄은 WARUDO에 브릿지 파일을 넣었다는 뜻입니다.
- `registered`는 그 AI 앱에 연결을 등록했다는 뜻입니다. 쓰려는 앱이 목록에 없으면
  [AI 앱에 연결하기](#ai-앱에-연결하기)를 보세요.
- 고치는 설정 파일은 먼저 `%USERPROFILE%\.warudo-agent-bridge\backups`에 백업해 둡니다.

### 5단계: WARUDO와 AI 앱 다시 켜기

1. WARUDO를 켭니다. 이미 켜져 있었다면 그대로 두고 **30초쯤 기다리세요.** WARUDO가 새 파일을 알아서 불러옵니다.
2. AI 앱을 **완전히 껐다가** 다시 켭니다. (창만 닫으면 백그라운드에 남아 있을 수 있습니다. 작업 표시줄 오른쪽 아래
   트레이 아이콘에서도 종료하세요.)

이제 [처음 써 보기](#처음-써-보기)로 가면 됩니다.

## AI 앱에 연결하기

설치 프로그램이 찾은 앱에는 자동으로 등록되어 있으니, 아래는 **확인하는 방법**과 **안 될 때 할 일**입니다.

### Claude 데스크톱 앱

- 자동 등록 위치: `%APPDATA%\Claude\claude_desktop_config.json`
- 확인: 앱을 트레이 아이콘까지 완전히 종료한 뒤 다시 켭니다. 대화 입력창 왼쪽 아래 **+** 버튼 → **Connectors**
  → **Manage connectors** 에 `warudo-agent-bridge`가 보이면 연결된 것입니다. 설정의 **Developer** 탭에서도 볼 수
  있습니다. 대화할 때 AI가 도구를 쓰려고 하면 허용할지 물어봅니다.
- 연결이 안 되면 `%APPDATA%\Claude\logs\mcp-server-warudo-agent-bridge.log`에 이유가 남습니다.
- 설치 결과에 `claude-desktop`이 없었다면: 앱을 한 번 실행한 뒤 PowerShell에서 아래를 실행하세요.

```powershell
npx --yes @studioraming/warudo-agent-bridge@latest install --clients claude-desktop
```

### Claude Code

- 자동 등록: `claude mcp add -s user ...` (모든 폴더에서 쓸 수 있게 사용자 범위로 등록)
- 확인: 새 터미널에서 `claude mcp list`를 실행하거나, Claude Code 안에서 `/mcp`를 입력합니다.
- 설치 결과에 `manual`이 나왔다면 `claude` 명령을 못 찾은 것입니다. 결과에 함께 나온 `run:` 명령을 그대로
  실행하면 됩니다.

### Codex (CLI, IDE 확장)

- 자동 등록 위치: `%USERPROFILE%\.codex\config.toml`
  (오래 걸리는 캡처가 끊기지 않도록 `tool_timeout_sec = 180`도 함께 넣습니다)
- 확인: `codex mcp list`를 실행하거나, Codex 안에서 `/mcp`를 입력합니다.
- 설치 결과에 `codex`가 없었다면:

```powershell
npx --yes @studioraming/warudo-agent-bridge@latest install --clients codex
```

### ChatGPT

- **ChatGPT 웹사이트와 모바일 앱의 일반 채팅에서는 쓸 수 없습니다.** 일반 채팅은 인터넷에 공개된(HTTPS) MCP
  서버만 연결할 수 있는데, 이 브릿지는 내 PC 안에서만 돌기 때문입니다.
- **ChatGPT 데스크톱 앱(Windows)에서는 쓸 수 있습니다.** 데스크톱 앱은 Codex CLI·IDE 확장과 같은 설정 파일
  (`%USERPROFILE%\.codex\config.toml`)을 함께 쓰고, 설치 프로그램이 여기에 브릿지를 등록합니다.
  1. ChatGPT 데스크톱 앱을 설치하고 로그인합니다.
  2. 위 Codex 항목의 `--clients codex` 명령을 실행합니다(설치 결과에 `codex`가 이미 있었다면 건너뛰세요).
  3. 앱의 **설정 → MCP servers** 에서 `warudo-agent-bridge`가 보이는지 확인하고, 보이면 **Restart**를 누릅니다.
  4. 입력창에 `/mcp`를 입력하면 연결된 서버 목록이 나옵니다.
- 메뉴 이름과 이용 가능한 요금제는 OpenAI가 바꿀 수 있습니다. 2026년 9월 기준 OpenAI 문서
  ([MCP 설정](https://learn.chatgpt.com/docs/extend/mcp?surface=app))을 따랐습니다.

### Cursor와 그 밖의 앱

Cursor는 `%USERPROFILE%\.cursor\mcp.json`에 자동 등록됩니다. 다른 MCP 앱은 [직접 등록하기](#직접-등록하기)를
보세요.

## 처음 써 보기

1. **WARUDO를 켜고** 캐릭터가 있는 씬을 엽니다.
2. **AI 앱에서 새 대화를 엽니다.** (설치 전에 열어 둔 대화에는 도구가 안 보일 수 있습니다.)
3. 이렇게 보내 보세요.

   > WARUDO랑 연결됐는지 warudo_status로 확인해 줘.

   "연결됐어요" 같은 답과 함께 WARUDO 버전·열린 씬 이름이 나오면 성공입니다.
4. AI 앱이 **도구를 써도 되는지 물으면** 내용을 보고 허용을 누르세요.
5. 이제 [예시](#무엇을-할-수-있나요)를 하나씩 보내 보세요. 처음에는 "캐릭터 정면에서 찍어서 보여 줘"가 결과가
   바로 보여서 좋습니다.

## 안전하게 쓰기

- **중요한 씬은 먼저 저장하세요.** 또는 "지금 상태 스냅샷 만들어 줘"라고 부탁하세요. 브릿지도 씬 열기, 스냅샷
  복원, 에셋 삭제 전에는 복구용 스냅샷을 자동으로 남기고, 저장된 씬을 덮어쓸 때는 이전 파일을 남깁니다.
- **방송 중에는 조심하세요.** AI가 바꾼 것은 WARUDO 화면에 바로 나옵니다. 방송 전에 리허설로 써 보세요.
- **한 번에 한 AI만 WARUDO를 바꿀 수 있습니다.** 두 번째 AI는 씬을 두고 다투는 대신 "사용 중"이라는 답을 받습니다.
- **결과를 부풀리지 않습니다.** 도중에 WARUDO가 꺼지면 "실행 안 됨" 또는 "확인 필요"로 알려 주고, 결과가
  불확실한 변경은 저절로 다시 보내지 않습니다.
- **유니티 스크립트 함수 호출은 기본으로 꺼져 있습니다.** 설정 파일에서 허락한 것만 부를 수 있습니다.

## 자주 묻는 질문

**돈이 드나요?**\
브릿지는 무료입니다. AI 앱 이용료는 각 서비스의 요금제를 따릅니다.

**코딩을 몰라도 되나요?**\
네. 설치 때 명령어 한 줄을 붙여 넣는 것 말고는 채팅으로 부탁만 하면 됩니다.

**WARUDO 모드를 설치해야 하나요?**\
아니요. WARUDO의 Playground 폴더에 스크립트 파일 하나를 넣는 것으로 끝납니다. 설치 프로그램이 알아서 넣습니다.

**내 캐릭터에도 되나요?**\
네. VRM이든 일반 휴머노이드 아바타든 포즈 도구가 그대로 맞습니다. 표정 이름은 캐릭터마다 다르지만 AI가 목록을 먼저
읽고 알맞은 것을 고릅니다.

**한국어로 부탁해도 되나요?**\
네. AI 앱이 알아듣는 언어라면 무엇이든 됩니다. 도구 이름이 영어여도 부탁은 한국어로 하면 됩니다.

**Mac에서도 되나요?**\
아니요. WARUDO가 Windows 전용이라 브릿지도 Windows 전용입니다.

**AI가 바꾼 걸 되돌릴 수 있나요?**\
표정·포즈·카메라는 "원래대로 돌려 줘" 한마디면 됩니다. 씬 전체는 미리 만든 스냅샷으로 되돌립니다.

**업데이트는 어떻게 하나요?**\
[2단계](#2단계-설치-명령-붙여-넣기)의 설치 명령을 다시 실행하면 최신 버전으로 바뀝니다.

## 문제가 생겼을 때

| 증상 | 해결 방법 |
| --- | --- |
| AI가 "브릿지가 꺼져 있다", "연결할 수 없다"고 함 | WARUDO를 켜고 30초 기다린 뒤 다시 부탁하세요. 그래도 안 되면 아래 `doctor`를 실행해 보세요. |
| AI 앱에 도구가 안 보임 | AI 앱을 트레이 아이콘까지 완전히 종료한 뒤 다시 켜고, **새 대화**를 여세요. 설치 결과에 그 앱 이름이 없었다면 [AI 앱에 연결하기](#ai-앱에-연결하기)의 명령을 실행하세요. |
| `'npx' 용어가 ... 인식되지 않습니다` (또는 `'node'`) | Node.js를 설치한 직후라면 PowerShell 창을 닫고 새로 연 다음 다시 실행하세요. |
| `runtime: warudo-not-found` | Steam 라이브러리가 다른 곳에 있는 경우입니다. `Warudo.exe`가 있는 폴더를 알려 주세요: `npx --yes @studioraming/warudo-agent-bridge@latest install --warudo "D:\SteamLibrary\steamapps\common\Warudo"` |
| `runtime: NOT installed yet` | 옛 버전 파일이 많아서 WARUDO가 켜진 채로는 바꾸지 않은 것입니다. WARUDO를 끄고 설치를 한 번 더 실행하세요. |
| Codex에서 캡처 도중 시간 초과 | 설치를 한 번 더 실행하면 `tool_timeout_sec = 180`이 들어갑니다. |

상태를 한 번에 점검하려면:

```powershell
npx --yes @studioraming/warudo-agent-bridge@latest doctor
```

`FAIL`인 줄이 원인입니다(`INFO` 줄은 참고용 정보라 문제가 아닙니다). 해결이 안 되면 그 결과를 붙여서
[이슈](https://github.com/StudioRaming/warudo-agent-bridge/issues)로 남겨 주세요.

## 더 알아보기 (고급)

### 설치 옵션

- `--clients claude-code,codex`(또는 `none`): 등록할 AI 앱을 고릅니다.
- `--warudo "D:\...\Warudo"`: Steam에서 WARUDO를 못 찾을 때 위치를 알려 줍니다.
- `--skip-runtime`: WARUDO에는 손대지 않고 AI 앱 등록만 합니다.

한 줄 설치에 옵션을 주려면 환경 변수를 먼저 설정합니다.

```powershell
$env:WARUDO_AGENT_BRIDGE_CLIENTS = "claude-code,codex"; irm https://raw.githubusercontent.com/StudioRaming/warudo-agent-bridge/main/install.ps1 | iex
```

### 직접 등록하기

설치 프로그램이 이 PC에 맞는 명령을 그대로 출력해 줍니다. Claude Code라면 이런 모양입니다.

```bash
claude mcp add -s user warudo-agent-bridge -- node "C:\Users\YOU\.warudo-agent-bridge\app\bin\warudo-agent-bridge.mjs" mcp
```

JSON 설정을 쓰는 클라이언트는 `{"mcpServers": {"warudo-agent-bridge": {"command": "node", "args": ["C:\\Users\\YOU\\.warudo-agent-bridge\\app\\bin\\warudo-agent-bridge.mjs", "mcp"]}}}`입니다.

### 동작 방식

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

### 캐릭터 포즈

포즈 도구는 Unity 휴머노이드 기준이라 VRM이든 일반 휴머노이드 아바타든 그대로 맞습니다.

- `warudo_character_pose_set`은 뼈 조준(`{"LeftUpperArm": {"aim": [-1, -0.2, 0]}}`, x 오른쪽·y 위·
  z 앞)과 머슬 값을 받아 계층 순서(부모 먼저)대로 적용합니다. `base: "standing"`은 아바타 자신의 기본
  자세에서 시작하고, `hipsHeight`로 골반 높이를 미터로 정합니다(어른 체형이 무릎 꿇고 선 자세는 약 0.35).
- 관절은 아바타의 가동 범위를 넘지 않습니다. 조준이 범위를 넘으면 응답의 `limited`에 적어 주므로
  에이전트가 그 조준을 줄일 수 있습니다.
- 캐릭터에 포즈가 실제로 보인 뒤에 응답합니다(`shown`). 바로 이어서 찍는 캡처가 정확합니다.
- `warudo_analysis_capture`는 재질과 배경 없이 부위별 색으로 몸을 여러 방향에서 한 번에 렌더하고,
  손의 접촉, 부위별 바닥 높이, 카메라 화면에서 각 관절의 위치를 알려 줍니다. 에이전트는 셰이딩된
  스크린샷을 짐작하는 대신 이것으로 레퍼런스 이미지와 포즈를 맞춰 봅니다.
- `warudo_character_pose_save`는 포즈를 Unity 휴머노이드 `.anim`으로 저장합니다. WARUDO 애니메이션
  목록에 넣거나 원하는 폴더(예: 유니티 프로젝트)에 씁니다.
- `warudo_character_pose_clear`는 원래 대기 애니메이션으로 돌려 놓습니다.

### 설정

`%USERPROFILE%\.warudo-agent-bridge\config.json` ([config.example.json](config.example.json)에서 만들어짐):

- `componentCall.allow`: 에이전트가 호출해도 되는 Unity 컴포넌트 메서드.
  `{"typePrefix": "My.Namespace.", "methods": ["Refresh"]}` 형식으로 적습니다. 기본은 비어 있어서
  `warudo_component_call`은 꺼져 있습니다.
- `retention`: 응답, 캡처, 스냅샷, 포즈 미리보기를 `warudo_maintenance_cleanup`이 지우기 전까지
  보관하는 기간.

### 명령줄

AI 클라이언트 없이도 같은 기능을 쓸 수 있습니다.

```bash
npx @studioraming/warudo-agent-bridge status
npx @studioraming/warudo-agent-bridge call scene.get
npx @studioraming/warudo-agent-bridge tools
```

설치 후에는 `%USERPROFILE%\.warudo-agent-bridge\bin\warudo-agent.cmd`가 같은 명령줄의 바로가기입니다.

### 제거

```bash
npx @studioraming/warudo-agent-bridge uninstall
```

WARUDO Playground에서 런타임을 빼고(완전히 내리려면 WARUDO 재시작) 클라이언트의 MCP 항목을 백업과 함께
지웁니다. `--purge`를 붙이면 `backups` 폴더만 남기고 `%USERPROFILE%\.warudo-agent-bridge`도 지웁니다.

### 개발

[CONTRIBUTING.md](CONTRIBUTING.md)를 보세요. 보안 문제 제보는 [SECURITY.md](SECURITY.md)를 따라 주세요.

## 라이선스

[MIT](LICENSE) © StudioRaming
