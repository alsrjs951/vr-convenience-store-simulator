# Unity 셋업 체크리스트 (P0-L1~L3)

> 담당: 이민건 트랙. 마일스톤 `docs/VR편의점_알바_시뮬레이터_마일스톤.md`의 P0-L1(프로젝트·패키지 고정), P0-L2(그래픽스·URP·OpenXR 설정), P0-L3(레이어·태그)를 **위에서 아래로 순서대로** 수행한다.
> 대응 종료 기준: ① 샘플 `.ply` → `GaussianSplatAsset` 변환·씬 렌더링 + XR Device Simulator 카메라 이동, ② INRIA 비상업 조항 확인.
> 검증 기준일: 2026-10-25. "기록란"은 실제 설치한 값을 반드시 적는다. 순서를 바꾸면(특히 6번 이전에 5번) 렌더링 누락이 발생한다.
> 학습 서버(RTX 4070) 쪽은 `docs/p0/RTX4070_학습환경_세팅_가이드.md` 참조.

---

## 1. Unity Hub 설치·서명 (P0-L1)

- [ ] Unity Hub를 설치한다(https://unity.com/download — Windows 빌드 작업이므로 **데스크톱 Windows 쪽에** 설치).
- [ ] Unity ID로 로그인 후, Hub → 환경설정(Preferences) → Licenses → Add → **개인(Personal) 라이선스**를 활성화한다.
  - 기록란: 라이선스 활성화 일자 `______________`

## 2. 에디터 설치 (P0-L1)

- [ ] Hub → Installs → Install Editor → **Unity 6.3 LTS(6000.3.x 계열) 설치**.
  - Unity 6.3 LTS 최초 릴리스는 6000.3.0f1(2025-12-03 공개, unity.com 릴리스 페이지 확인). 설치 시점의 최신 6000.3.x 패치를 설치한다.
  - 기록란: 설치한 정확한 버전 `______________` (예: 6000.3.xfN)
- [ ] 같은 에디터에 **Add modules → Windows Build Support (IL2CPP)** 모듈을 함께 설치한다(빌드용 — 미설치 시 나중에 Installs에서 추가 가능).
- [ ] 폴백용 **Unity 6.0(6000.0.23f1)** 도 같은 방식으로 설치해 둔다(프로젝트 지정 폴백 버전 — 마일스톤 P0-L1·D7; G1 게이트에서 6.3과 병행 검증용).
  - 기록란: 6.0 설치 버전 `______________`

## 3. 프로젝트 생성 (P0-L1)

- [x] Hub → New project → **Universal 3D(URP)** 템플릿, 프로젝트명 `vr-convenience-store`, 에디터는 2번에서 설치한 6.3 선택 → 생성. *(2026-10-25 macOS에서 완료 — 6000.3.26f1 batch 모드, 프로젝트 폴더 = 저장소 루트. Windows는 Hub → Add existing project로 저장소 루트를 열면 됨)*
  - **생성 위치 주의**: Hub는 지정한 위치 아래 `<프로젝트명>/` 하위 폴더를 새로 만들므로 저장소 루트를 프로젝트 폴더로 직접 쓸 수 없다. 임시 위치에 생성한 뒤 `Assets/`, `Packages/`, `ProjectSettings/` 등 프로젝트 최상위 항목을 **저장소 루트(`vr-convenience-store-simulator/`)로 이동**해 "프로젝트 폴더 = 저장소 루트"로 만든다. 마일스톤 1장 기준선(루트 바로 아래 `Assets/`)과 루트 `.gitattributes`의 `Assets/GaussianSplats/**` LFS 패턴(`.asset`/`.bytes` 추적)이 이 구조를 전제하므로, 중첩 프로젝트(예: `vr-convenience-store-simulator/vr-convenience-store/`)로 남겨 두면 에셋이 LFS 추적에서 빠진다.
  - 기록란: 생성된 프로젝트 경로 `______________`
- [ ] 생성 직후 첫 커밋을 남긴다(P0-L5의 Git LFS 설정과 함께 — `*.ply`, `*.asset`, `*.bytes` 추적은 P0-L5 참조).

## 4. XR 패키지 설치·샘플 임포트 (P0-L1)

- [x] Window → Package Manager에서 설치: *(2026-10-25 macOS에서 설치 완료 — manifest.json 고정)*
  - [x] **XR Plugin Management**(`com.unity.xr.management`)
  - [x] **OpenXR Plugin**(`com.unity.xr.openxr`)
  - [x] **XR Interaction Toolkit 3.x**(`com.unity.xr.interaction.toolkit` — 레지스트리 조회 결과 3.6.1이 6000.0+ 최신 안정판)
  - 기록란: XRI 설치 버전 `3.6.1`, OpenXR 플러그인 버전 `1.18.0`, XR Plugin Management 버전 `4.7.0` (URP `17.3.0`)
- [ ] Package Manager에서 XR Interaction Toolkit 선택 → Samples 탭 → **Starter Assets** Import.
- [ ] 이어서 **XR Device Simulator** 샘플도 Import(HMD 미착용 개발용 — 종료 기준 1의 카메라 이동 확인에 사용).

## 5. UnityGaussianSplatting 설치 (P0-L1)

- [x] Package Manager → `+` → **Add package from git URL** 에 아래 URL을 **그대로** 입력: *(2026-10-25 설치 완료)*
  ```
  https://github.com/aras-p/UnityGaussianSplatting.git?path=/package#v1.1.0
  ```
  - **중요 — `/package` 하위 경로를 반드시 포함한다.** 저장소 루트(`.../UnityGaussianSplatting.git#v1.1.0`)를 지정하면 루트에 `package.json`이 없어 설치가 실패한다(실제 저장소 구조: package.json은 `package/` 폴더 안에 있음 — 2026-10-25 GitHub API로 확인). 태그 고정(`#v1.1.0`)도 생략하지 않는다.
- [ ] 설치 확인: Packages 목록에 **Gaussian Splatting**(`org.nesnausk.gaussian-splatting`, v1.1.0)이 나타난다. 의존성(Burst 1.8.8, Collections 2.1.4, Mathematics 1.2.6)이 함께 끌려 온다(package.json v1.1.0 확인값).
- [ ] **커밋 해시 기록란**: Package Manager에서 해당 패키지를 선택해 표시되는 해시 또는 `Packages/packages-lock.json`의 `org.nesnausk.gaussian-splatting` 항목에서 확인.
  - v1.1.0 태그의 커밋(2026-10-25 GitHub API로 미리 확인한 값 — 설치 후 일치하는지 대조): `d2227009f6a5e2218655dae22e0b30bed2e75edd`
  - 기록란: 실제 설치된 커밋 해시 `d2227009f6a5e2218655dae22e0b30bed2e75edd` (packages-lock.json hash — 사전 예측값과 일치)
- [ ] 셰이더·스크립트 패치가 필요해지면 UPM 캐시(`Library/PackageCache`는 재실행 시 초기화) 대신 폴더를 `Packages/org.nesnausk.gaussian-splatting/`으로 복사해 **로컬 임베드**한다(마일스톤 P0-L1). 지금 단계에서는 임베드하지 않는다.

## 6. 그래픽스·URP 설정 (P0-L2 — 누락 시 스플랫 미렌더링, 윈도우 빌드 기준)

- [x] **Project Settings → Player → Other Settings → Graphics APIs for Windows**: 먼저 **Auto Graphics API** 체크를 해제한다(체크 상태에서는 목록 편집이 비활성화되어 바로 따라 할 수 없다). 그 뒤 목록에서 **Direct3D12만 남기고** DirectX11·Vulkan 등 나머지는 제거한다. (플러그인 v1.1.0 readme 명시: Windows에서는 D3D12 또는 Vulkan만 동작, **DX11은 동작하지 않음**. 프로젝트 결정은 DX12 고정.) *(2026-10-25 — PlayerSettings에 DX12 전용 프리셋을 미리 설정함. Windows 에디터에서 최종 확인만)*
- [x] `Assets/Settings/PC_Renderer.asset`(활성 **UniversalRendererData**)을 선택 → Inspector → **Add Renderer Feature → Gaussian Splat URP Feature** 등록. *(2026-10-25 등록 완료 — Feature는 렌더러 데이터의 서브 에셋으로 저장됨)*
  - 기록란: 등록한 Renderer Data 에셋 이름 `PC_Renderer.asset`
- [x] **Project Settings → Graphics → URP → Render Graph** 섹션에서 **`Compatibility Mode (Render Graph Disabled)` 를 체크 해제(OFF)**로 둔다(플러그인 v1.1.0의 URP 통합은 Render Graph 전용으로 동작 — 구현 문서 2.2절). *(2026-10-25 확인 — **Unity 6000.3에서는 이 설정 자체가 제거됨**(`#breakingFrom(6000.3)`, 항상 Render Graph) → 요건 자동 충족. 6000.0 폴백 시에만 수동 확인)*
- [x] 사용 중 **URP Asset**(PC_RPAsset): **MSAA = Off**(스플랫 렌더러 미지원), Render Scale = `1.0`, **Additional Lights / Additional Lights Shadows 최소화**(무조명 파이프라인 — 스플랫은 자체 셰이딩). *(2026-10-25 — msaaSampleCount=1 설정, 추가 광원은 씬 작성 시 규칙으로 유지)*
- [ ] 씬에 추가 광원(포인트·스팟 등)을 두지 않는다. 기본 Directional Light는 렌더에 불필요하므로 삭제 또는 비활성화.

## 7. OpenXR 설정 (P0-L2 — PC 탭 기준)

- [ ] Project Settings → **XR Plug-in Management** → **PC 탭**에서 **OpenXR**에 체크.
- [ ] OpenXR 하위 설정에서 **Interaction Profiles → Add: Oculus Touch Controller Profile** 추가(Meta Quest 2 컨트롤러 — 2026-11-06 기종 확정, 마일스톤 P0-L2·구현 문서 2.7절).
- [ ] **Stereo Rendering Mode = Multi-pass**로 설정(기본값; 필요 시 Single Pass Instanced와 양안 정렬 비교 — 마일스톤 P0-L2·리스크표).
- [ ] 보유 Quest 2(2026-11-06 확보)를 유선 Link로 연결해 에디터 Play에서 양안 출력 확인 — Meta Quest Link 앱 설치·72Hz 설정은 마일스톤 P0-K3b. P0 마감(11/06) 또는 P1 첫날(11/07)에 수행(G1-H 사전 확인).

## 8. 레이어·태그·충돌 매트릭스 (P0-L3)

- [x] Project Settings → **Tags and Layers** → Layers(사용자 레이어)에 등록: `Splat`, `Interactable`, `BarcodeFace`, `Scanner`, `Zone`. *(2026-10-25 등록 완료)*
  - 기록란: 실제 부여된 레이어 번호 — Splat `8`, Interactable `9`, BarcodeFace `10`, Scanner `11`, Zone `12`
- [x] 같은 화면 Tags에 태그 등록: `Barcode`. *(2026-10-25 등록 완료)*
- [x] Project Settings → **Physics → Layer Collision Matrix**: *(2026-10-25 설정 완료)*
  - [x] **`Splat` 행 전체 체크 해제**(모든 레이어와의 물리·레이캐스트 충돌 완전 제외).
  - [x] **`BarcodeFace`는 오직 `Scanner`와만 충돌 허용**(나머지 전부 해제).
  - [x] 나머지 조합은 기본값 유지(이후 기능 단계에서 필요 시 조정 — 5장 물리 설계).

## 9. 검증 — 스플랫 에셋 변환·렌더링 (종료 기준 1)

- [x] 샘플 `.ply` 확보. *(2026-10-25 — `scripts/tools/make_sample_splat.py`로 합성 생성: `Assets/GaussianSplats/Sample/sample_sphere.ply`, 1200 가우시안, SH degree 3 레이아웃(62 float/정점) 준수)* 참고: 플러그인 저장소에는 샘플 스플랫 파일이 포함되어 있지 않다(2026-10-25 확인 — v1.1.0의 `projects/GaussianExample/Assets`에는 `GSTestScene.unity`만 존재). 실물 스캔 샘플은 이하 절차 중 하나를 쓴다:
  - RTX 4070 검증(`scripts/windows/nerfstudio_setup.ps1`) 이후: `ns-export gaussian-splat`으로 뽑은 `splat.ply`(가이드 §5.1)를 맥/데스크톱에서 복사해 사용, 또는
  - Scaniverse 내보내기(.ply 또는 .spz — 플러그인은 PLY와 Scaniverse SPZ 입력을 지원, v1.1.0 readme 확인).
- [x] Unity 메뉴 **Tools → Gaussian Splats → Create GaussianSplatAsset** 실행 → `Input PLY/SPZ File`에 샘플 파일 지정 → 압축 옵션(우선 기본/Very Low)과 출력 폴더(`Assets/GaussianSplats/` 권장 — LFS 추적 경로와 일치) 지정 → **Create Asset**. *(2026-10-25 — batch 자동 변환, `sample_sphere.asset` + `_pos/_oth/_col/_shs.bytes` 생성, splats=1200)*
- [x] 씬에 GameObject 추가 → **GaussianSplatRenderer** 컴포넌트 부착 → Asset 필드에 방금 만든 에셋 지정 → 에디터 뷰에서 스플랫이 정상 렌더링되는지 확인. *(2026-10-25 — macOS Metal(Apple M5)에서 렌더 성공, 증거: `docs/p0/splat_render_check_macos_metal.png`. **Windows DX12에서의 동일 확인과 XR Device Simulator 카메라 이동 확인은 미수행** — 사용자 조치 1·6번)*
- [ ] XR Device Simulator 샘플(4번에서 임포트)로 카메라 이동이 가능한지 확인(종료 기준 1의 두 번째 요건).
- [ ] 결과 기록: 변환한 파일명 `______________`, 에셋 크기 `______________`, 렌더 확인 일자 `______________`, 발생 이슈 `______________`

## 10. 라이선스 확인 (종료 기준 2 — INRIA 비상업 조항)

> **검증 결과에 따른 주의(2026-10-25 확인):** UnityGaussianSplatting **v1.1.0 태그의 `LICENSE.md`는 MIT만** 포함한다(전문 20줄 확인 — Aras Pranckevičius, MIT License). 즉 **INRIA 비상업 조항은 이 저장소의 LICENSE 파일 안에 직접 들어 있지 않다.** INRIA 비상업 조건은 (a) v1.1.0 `readme.md`의 **"License and External Code Used"** 절에 명시되고 — "the official *training* software for the Gaussian Splats is for educational / academic / non-commercial purpose; commercial usage requires getting license from INRIA" — (b) 원논문 구현 저장소(graphdeco-inria/gaussian-splatting)의 LICENSE.md에 조항 본문이 있다. 마일스톤 종료 기준 2번의 "LICENSE 파일 내 INRIA 비상업 조항 확인"은 아래와 같이 실제 위치 기준으로 수행한다.

- [x] v1.1.0의 `readme.md` "License and External Code Used" 절을 읽고 위 인용 문구(학술/비상업 목적 외 사용 시 INRIA 라이선스 필요)를 확인한다. *(2026-10-25 완료 — 발췌 사본: `docs/p0/UnityGaussianSplatting_v1.1.0_readme_라이선스_발췌.txt`)*
- [x] 원논문 구현 저장소 라이선스(https://github.com/graphdeco-inria/gaussian-splatting/blob/main/LICENSE.md)에서 INRIA 비상업 조항 본문을 확인한다. *(2026-10-25 완료 — §3 "The *Software* may be used 'non-commercially', i.e., for research and/or evaluation purposes only." 확인, 사본: `docs/p0/Inria_gaussian-splatting_LICENSE_발췌.txt`)*
- [x] 확인한 라이선스 파일 사본을 `docs/p0/UnityGaussianSplatting_v1.1.0_LICENSE.txt`로 저장한다(저장소 LICENSE.md MIT 전문 + readme 해당 절 인용 + 원논문 라이선스 출처 링크를 함께 기재할 것). *(2026-10-25 완료 — MIT 전문: `docs/p0/UnityGaussianSplatting_v1.1.0_LICENSE.txt`, readme 발췌: `..._readme_라이선스_발췌.txt`, 원논문 라이선스 사본: `Inria_gaussian-splatting_LICENSE_발췌.txt`)*
  - 기록란: 사본 저장 일자 `2026-10-25`, 확인자 `ZCode P0 워크플로(통합 에이전트)`, 원논문 라이선스 비상업 조항 요지 `연구·평가 등 비상업 목적으로만 사용 가능, 상업적 사용은 INRIA 별도 라이선스 필요`
- [ ] 본 프로젝트는 비공개 학술/교내 과제(구현 문서 D6·3.9절)이므로 비상업 범위에 해당한다 — 상업 전환 검토 시 이 확인을 먼저 반복한다.

## 11. SuperSplat 정제 산출물 포맷 주의 (상시)

- [ ] SuperSplat에서 정제 완료 후 내보내기 시 **표준 Binary `.ply`** 를 선택한다. 최신 SuperSplat의 기본 SPZ 내보내기(SPZ v4, ZSTD 기반)는 플러그인 v1.1.0 내장 SPZ 파서(Gzip 기반 SPZ v2)와 호환되지 않아 Unity 임포트 시 헤더/압축 오류가 발생할 수 있다(구현 문서 3장 포맷 호환성 주의 — 2026-10-25 기준 최신 SuperSplat 동작은 사용 시점에 재확인).
- [ ] `.spz`(SPZ v2)를 꼭 써야 하는 경우 내보내기 옵션에서 SPZ v2를 명시적으로 선택한다(해당 옵션 존재 여부는 사용 시점 SuperSplat에서 확인).
- [ ] Unity 반입은 어떤 포맷이든 `Tools → Gaussian Splats → Create GaussianSplatAsset`으로 `.asset` 변환 후 사용한다(3장 파이프라인).

---

## 완료 판정

- [ ] 9번(스플랫 변환·렌더 + XR Device Simulator 카메라 이동)과 10번(라이선스 기록)이 모두 체크되면 **P0-L1~L3 및 종료 기준 1·2 충족**. 종료 기준 3(RTX 4070 CLI)은 `docs/p0/RTX4070_학습환경_세팅_가이드.md` §3에서 판정한다.
- [ ] 완료 후 `docs/build_log.md`에 1줄 기록(마일스톤 P0-K4 규약).
