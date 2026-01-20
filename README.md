# RoadBuilderPackage4Unity

Unity에서 도로를 만들 수 있는 클릭 기반 시스템입니다.

## 기능

- **도로 만들기 시스템**: 첫 클릭으로 시작, 두 번째 클릭으로 완료하여 도로를 만드는 시스템
- **스냅 기능**: 기존 도로의 끝이나 중간에 자동으로 연결
- **곡선 지원**: 부드러운 곡선으로 도로를 자연스럽게 생성
- **직선 모드**: Alt 키를 누른 상태로 직선 도로를 생성
- **커브 조정**: Shift 키로 커브 강도를 조정
- **청크 삭제**: 우클릭으로 특정 도로 청크 삭제
- **미리보기**: 투명한 노란색/주황색으로 실시간 미리보기

## 저장소 구조

```
RoadBuilderPackage4Unity/
├── Dev/                    # 개발 및 테스트용 Unity 프로젝트
│   └── Assets/
│       ├── Scripts/
│       │   ├── Road/
│       │   │   ├── RoadBuilder.cs
│       │   │   └── RoadComponent.cs
│       │   └── RoadActionsClass.cs
│       ├── Materials/
│       ├── Scenes/
│       └── RoadActions.inputactions
│
├── Package/                # Unity 패키지 (자동 동기화됨)
│   ├── package.json
│   ├── README.md
│   ├── Runtime/           # 런타임 스크립트
│   └── Samples~/          # 샘플 파일
│
├── Sample/                 # 샘플 소스 (예약됨)
└── .git/hooks/pre-commit  # 자동 동기화 훅
```

## 패키지 설치 방법

### Unity Package Manager로 설치

1. Unity에서 **Window > Package Manager** 열기
2. **+** 버튼 클릭 → **Add package from git URL** 선택
3. 다음 URL 입력:
   ```
   https://github.com/nedcrow/RoadBuilderPackage4Unity.git?path=/Package
   ```

### 샘플 임포트

1. Package Manager에서 **RoadBuilderPackage4Unity** 선택
2. **Samples** 탭에서 **Example Scene** 찾기
3. **Import** 버튼 클릭

샘플 임포트 경로:
```
Assets/Samples/RoadBuilderPackage4Unity/1.0.0/Example Scene/
```

## 설치 방법

### 1. Scene 설정

1. **빈 GameObject 생성**
   - Hierarchy에서 우클릭 → Create Empty
   - 이름을 "RoadBuilder"로 변경

2. **RoadBuilder 컴포넌트 추가**
   - GameObject를 선택 → Add Component → RoadBuilder

3. **Ground 설정 (필수)**
   - 바닥이 될 Plane 또는 Terrain 생성하고 원하는 Layer를 할당

### 2. RoadBuilder Inspector 설정

#### 필수 설정

**Raycast / Input**
- **Ground Mask**: 바닥과 같은 Layer를 선택해야 합니다.
  - 이 설정이 없거나 다르면 도로를 만들 수 없습니다.
- **Ray Max Distance**: 2000 (기본값)

#### 선택 설정

**Appearance**
- **Road Width**: 도로 너비 (기본값 2.0)
- **Road Material**: 도로에 적용할 Material
- **UV Tiling Per Meter**: UV 타일링 비율

**Curve**
- **Samples Per Meter**: 곡선 샘플링 밀도 (기본값 1.5)
- **Handle Len Ratio**: 베지어 핸들 길이 비율 (기본값 0.25)
- **Max Handle Len**: 최대 핸들 길이 (기본값 8.0)

**Alt Curve (Sin Wave)**
- **Alt Curve Strength**: Alt 도로 생성 시 곡선 강도 (기본값 0.3)

**Segmentation / Snapping**
- **Segment Length**: 도로 청크 길이 (기본값 6.0)
- **Snap Distance**: 스냅 감지 거리 (기본값 0.5)

**Preview**
- **Preview Color**: 미리보기 색상 (기본값: 노란색)

### 3. Input System 설정

#### Option A: New Input System 사용 (권장)

1. **Package Manager 설치**
   - Window → Package Manager
   - Input System 패키지 설치

2. **PlayerInput 컴포넌트 추가**
   - RoadBuilder GameObject 선택
   - Add Component → Player Input

3. **Actions 설정**
   - Actions: `RoadActions.inputactions` 선택
   - Default Map: `RoadActions` 선택
   - Behavior: `Invoke Unity Events` 선택

4. **Events 연결**

   PlayerInput의 Events 섹션에서 다음과 같이 이벤트를 연결하세요:

   | Action | 연결할 메서드 |
   |--------|--------|
   | LeftClick | `RoadBuilder.OnLeftClick` |
   | RightClick | `RoadBuilder.OnRightClick` |
   | Cancel | `RoadBuilder.OnCancel` |
   | StraightModifier | `RoadBuilder.OnStraightModifier` |
   | PressedCurveModifier | `RoadBuilder.OnPressedCurveModifier` |
   | ReleasedCurveModifier | `RoadBuilder.OnReleasedCurveModifier` |

#### Option B: Old Input System 사용

- PlayerInput 컴포넌트를 추가하지 않으면 자동으로 Old Input System으로 동작합니다.
- 별도의 설정이 필요하지 않습니다.

## 사용 방법

> **⚠️ 필수 사항 (New Input System 사용 시)**
>
> New Input System을 사용하려면 다음 설정이 반드시 필요합니다:
> 1. `RoadActions.inputactions` 파일을 PlayerInput 컴포넌트의 **Actions**에 바인딩
> 2. PlayerInput 컴포넌트의 **Events** 섹션에서 각 액션(LeftClick, RightClick, Cancel 등)을 RoadBuilder의 해당 메서드에 연결
>
> 자세한 설정 방법은 위의 [Input System 설정](#3-input-system-설정) 섹션을 참고하세요.

### 기본 조작법

| 키/마우스 | 동작 |
|------|------|
| **좌클릭 (1번째)** | 도로 시작 지점 설정 |
| **좌클릭 (2번째)** | 도로 생성 완료 |
| **우클릭 (미리보기 중)** | 취소 |
| **우클릭 (평상시)** | 클릭한 도로 청크 삭제 |
| **ESC** | 취소 |
| **Shift (Hold)** | 커브 조정 (곡선 강도 조절) |
| **Alt (Hold)** | 직선 모드 활성화 |
| **Alt + Scroll** | 곡선 강도 조정 |

### 도로 생성 예시

1. **직선 도로 만들기**
   ```
   1. Shift 키를 누른 상태로 좌클릭 (시작)
   2. 원하는 위치로 마우스를 이동
   3. Shift 키를 누른 상태로 좌클릭 (완료)
   ```

2. **곡선 도로 만들기**
   ```
   1. 좌클릭 (시작)
   2. Alt 키를 누른 상태로 원하는 위치
   3. Alt + Scroll로 곡선 강도 조정
   4. 좌클릭 (완료)
   ```

3. **기존 도로에 연결**
   ```
   1. 새 도로를 만들 때 완료 지점을
   2. 다른 도로의 끝점 근처로 이동하면 자동으로 스냅됩니다
   3. 충분히 가까워지면 자동으로 도로가 연결됩니다
   ```

4. **도로 삭제**
   - 삭제할 도로를 우클릭하면 해당 청크만 삭제됩니다
   - 스냅 거리는 `Snap Distance`로 조절할 수 있습니다

### 미리보기 색상

도로 생성 시 3가지 상태로 표시됩니다:

- **녹색**: 시작 지점 (다른 도로에 스냅된 상태)
- **노란색**: 미리보기 (생성할 도로)
- **빨간색**: 완료 지점 (스냅된 도로에 연결 가능)

## 문제 해결

### 도로를 만들 수 없을 때

1. **Ground Mask 확인**
   - RoadBuilder Inspector의 Ground Mask에 Ground Layer가 선택되어 있는지 확인
   - Ground 설정한 Layer를 정확히 지정했는지 확인

2. **Camera 확인**
   - Main Camera가 존재하는지 확인
   - Camera의 Far Clip Plane이 충분히 큰지 확인

3. **Material 확인**
   - Road Material이 할당되어 있는지 확인
   - Material의 Shader가 올바른지 확인

### 키입력이 작동하지 않을 때

1. **New Input System 사용 시**
   - PlayerInput의 Events가 정확히 연결되어 있는지 확인
   - Console에 `[RoadBuilder] New Input System 활성화` 메시지가 있는지 확인

2. **Old Input System 사용 시**
   - Console에 `[RoadBuilder] Old Input System 사용중` 메시지가 있는지 확인
   - Project Settings의 Old Input System이 활성화 되어있는지 확인

### 도로가 생성되지 않을 때

1. **Ground Mask 미설정**
   - Raycast가 Ground에 닿지 않으면 도로를 만들 수 없습니다
   - Console에 `[RayToGround] Null exception: Camera for raycasting.` 등의 에러가 있는지 확인

2. **Build Mode 확인**
   - `BuildModeEnabled` 변수가 true인지 확인

## 퍼블릭 API

### RoadBuilder

```csharp
public class RoadBuilder : MonoBehaviour
{
    // 빌드 모드 활성화 여부
    public bool BuildModeEnabled { get; set; }

    // 마지막으로 생성된 도로
    public RoadComponent LastRoad { get; private set; }
}
```

### RoadComponent

```csharp
public class RoadComponent : MonoBehaviour
{
    // 도로의 중심선 (World Space)
    public List<Vector3> Centerline { get; }

    // 도로 상태
    public RoadState State { get; set; }

    // 도로 Cap 업데이트
    public void UpdateCaps();
}
```

## 라이선스

이 프로젝트는 MIT 라이선스 하에 배포됩니다.

## 기여

버그 리포트나 기능 요청은 Issue를 등록해 주세요.
