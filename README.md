# SCU 맵 빌더 (C# 포트)

그림자 복도 랜더마이저(Four9, 0.30.21)의 맵 합성 엔진과 SCU 패치 빌더(예전 `app/patcher`의 파이썬 코드, 지금은 앱에서 제거됨)를 **C#(.NET 8) CLI 하나로** 옮긴 것입니다.
파이썬과 중간 JSON(수백 MB)을 없애고, UAssetAPI 객체를 메모리에서 직접 다룹니다. GUI는 없습니다.

결과물은 파이썬 패처와 같은 `Kageroh-SCUMapShuffle_0_P.pak` 이고, 진행 상황도 같은 형식(한 줄에 JSON 하나)으로 출력합니다.

## 요약

| | 파이썬 | C# |
|---|---|---|
| 전체 빌드 (기본 맵 4개) | 965초 | **217초** |
| 매미의 복도만 | 506초 | **103초** |
| 최대 메모리 (전체 빌드) | 13.9GB | **7.9GB** |
| 준비 단계 (처음 한 번) | 약 12분 | **약 6.5분** (배포 zip만 풀어 둔 상태에서 JSON 문서·`rules.pak` 포함 388초) |

- 빌드 시간은 준비 자료가 이미 있는 같은 PC에서 순서대로 잰 값입니다. 파이썬은 예전 `scu_map_patch.py`, C#은 `scu build` 입니다. 초월자 기준 맵과 난이도 pak까지 함께 만들면 전체 빌드는 약 6분입니다.
- 결과 맵의 내용은 파이썬과 단계별로 비교해서 확인했습니다([검증](#검증) 참고). **일반 빌드와 초월자 빌드 모두 게임에서 직접 실행해 확인했습니다**(문제 없음).

## 필요한 것

- .NET 8 이상 (빌드에는 SDK). 단일 실행 파일(`Scu.Cli.exe`)을 쓰면 필요 없습니다.
- 랜더마이저 0.30.21 배포 파일 중 아래 파일들. 압축만 풀어 두면 되고 **미리 준비할 필요는 없습니다**(첫 실행에서 알아서 준비합니다).
- 그림자 복도 설치 폴더 (원본은 읽기만 합니다)
- 여유 공간 6GB 이상, 메모리는 전체 빌드 기준 약 8~9GB

### 랜더마이저 폴더에서 읽는 파일

| 파일 | 용도 |
|---|---|
| `portable-spec.json` | 버전 확인, 게임 파일 해시, 준비 사양 |
| `tools/repak-0.2.3/repak.exe` | pak 만들기와 풀기 |
| `app/local-map-recipes/recipe.json` | 사용자 게임에서 준비 맵과 JSON 문서를 만드는 레시피 |
| `app/local-templates/rules.template.bin` | 초월자 규칙 pak 템플릿 |
| `app/difficulty-assets/catalog.json`, `20261011-v100/manifest.json` | 난이도 자료 정보 |
| `app/updates/sanctuary-LT/all/event-plan.json`, `donor-preflight.json` | 성역 2·3갈래 계획 |

파이썬 런타임, `.py`, `UAssetGUI`, `dotnet` 폴더 등 나머지는 필요 없습니다. 배포할 때는 위 파일에 Four9의 안내문(`사용 방법.txt` 등)과 `licenses/` 를 함께 두세요.

## 빌드

```bash
dotnet build randomizer/src/Scu.Cli -c Release
```

## 사용법

```bash
dotnet randomizer/src/Scu.Cli/bin/Release/net8.0/Scu.Cli.dll build \
  --randomizer <랜더마이저 폴더> --game <게임 폴더> --out <출력 폴더>
```

| 옵션 | 기본값 | 설명 |
|---|---|---|
| `--stages` | `sanctuary,soul,abyss,soul-lt,abyss-lt` | 추가 구역으로 얹을 스테이지 |
| `--bases` | `cicada` | 기본 맵. 매미의 복도는 항상 포함. `soul,abyss,sanctuary` 추가 가능 |
| `--profiles` | `original` | 난이도 기준. `original,transcendent` 가능 (난이도 pak 2개도 함께 생성) |
| `--parallel` | `1` | 기본 맵별 작업 동시 처리(1~4). 효과가 작아 기본은 1 |
| `--cache` | 랜더마이저 폴더 | 게임에서 만든 자료(추출한 맵, 준비 맵, JSON 문서, `rules.pak`)를 저장할 폴더. 주면 랜더마이저 폴더는 읽기만 하므로 배포 파일을 통째로 바꿔도 준비 자료가 유지됩니다 |
| `--work` / `--keep-work` | `<out>/work` | 작업 폴더 지정 / 성공해도 남기기 |
| `--no-validate` | 검증 켬 | 구조 검증 끄기 |
| `--strict-verify` | 끔 | 저장 후 비교를 export별 JSON 비교(느림)로 |

출력 폴더에는 `Kageroh-SCUMapShuffle_0_P.pak`(초월자 포함 시 `…T_0_P.pak`, 난이도 pak 2개)과 `result.json` 이 생깁니다.

처음 실행하면 준비 단계가 자동으로 돕니다. 따로 하려면:

```bash
dotnet …Scu.Cli.dll prepare <랜더마이저 폴더> <게임 폴더>
```

준비 결과는 `--cache` 폴더(주지 않으면 `<랜더마이저>/`)의 `scu-prepared/`, `extracted/`, `app/` 에 만들어집니다. 처음 한 번만 필요하고, 이후 빌드는 준비 단계 없이 바로 병합으로 들어갑니다. 파이썬 랜더마이저가 이미 준비한 폴더도 그대로 쓸 수 있습니다.

### 진행 이벤트

stdout 에 한 줄에 하나씩 JSON 을 냅니다. `start`, `progress`(`stage`, `message`, `percent`, `t`=경과 초), `done`, `error`. 형식은 파이썬 패처와 같습니다.

## 구조

```
randomizer/
  PATCHES.md           예전 app/patcher 패치 43곳이 C#의 어디에 반영됐는지
  src/Scu.Core/        엔진
  src/Scu.Cli/         명령줄 (build, prepare, 단계별 진단 명령)
```

| 파일 | 하는 일 (원본 파이썬) |
|---|---|
| `Builder.cs` | 전체 흐름, pak 검증, 결과 JSON (`scu_map_patch.main/run_build/build_maps`) |
| `Prepare.cs` | 게임에서 원본 맵 추출, 레시피로 준비 맵·JSON 문서·`rules.pak` 생성, `--cache` 폴더 관리 (`launcher.prepare`, `local_map_materials`, `local_original_assets`) |
| `PythonJson.cs` | 해시가 고정된 JSON 문서를 파이썬과 같은 바이트로 쓰기 (들여쓰기, float 표기, CRLF) |
| `FsUtil.cs` | junction·읽기 전용 파일이 있어도 실패하지 않는 폴더 삭제 |
| `Compose.cs` | 성역 도너 방 + 매미의 복도 호스트 방 합성 (`composer.compose`) |
| `BundleInjector.cs`, `AbyssBundles.cs`, `SoulBundles.cs` | 심연·영혼 X 구역 (`soul_bundles`, `abyss_bundles`, `soul_special`) |
| `LtBundles.cs`, `LtStages.cs`, `SanctuaryLt.cs` | 영혼·심연·성역 2·3갈래 (`*_lt_bundles`, `bundle_core`, `all_build`) |
| `CallbackBuilder.cs`, `HandlerPlanner.cs`, `LatentBuilder.cs`, `KismetOps.cs` | 콜백·래치 함수 만들기 (`soul_x4_callback`, `integrate_sanctuary_callback`, `latent_x6`) |
| `RetainHost.cs` | 호스트 방을 남기고 가져오기 (`scu_retain_host.transplant_retain`) |
| `DistrictLook.cs`, `StyleRuntime.cs` | 문·선반 외형과 런타임 도우미 (`wall_styles`, `district_style_runtime`) |
| `HostSerialization.cs`, `PakTools.cs`, `DifficultyRules.cs` | 저장·재파싱·pak·초월자 규칙 |
| `PackageValidator.cs` | 구조 검증 |
| `Names.cs`, `DeepClone.cs`, `ObjectWalker.cs`, `Refs.cs`, `AssetCache.cs` | 이름 처리, 복제, 참조 재작성, 미리 읽기 |

## 검증

파이썬 빌더를 같은 입력으로 **단계별로** 실행해 각 단계 직후의 패키지를 JSON 으로 저장하고, C# 결과와 export 하나하나 비교했습니다.

| 단계 | export 수 | 결과 |
|---|---|---|
| 1 compose | 45,297 | 모두 동일 |
| 2 심연 | 58,493 | 모두 동일 |
| 3 영혼 | 77,163 | 모두 동일 |
| 4 영혼 2·3갈래 | 100,227 | 모두 동일 |
| 5 심연 2·3갈래 | 116,144 | 모두 동일 |
| 6 성역 2·3갈래 | 135,695 | 2개(클래스·기본 객체)만 목록 **순서** 차이 |
| 7 매미의 복도 구역 외형 | 135,699 | 위와 같은 2개 외 동일 |
| 영혼·심연·성역 기본 맵 변환·저장 | 137~139천 | 저장한 umap/uexp **바이트 동일** |
| 준비 단계 산출물 4개 | – | 파이썬이 만든 파일과 **바이트 동일** |
| 준비 단계 JSON 문서 5개, `rules.pak` | – | 레시피의 sha256과 **일치** (파이썬이 만든 파일과 같은 바이트) |

- 빌드마다 구조 검증을 돌립니다: 참조 범위, 이름 중복, 레벨, 클래스/함수 표, 바이트코드 크기와 점프 목적지, 방 존재. 전체 빌드에서 약 4초 걸립니다.
- 게임 실행 확인: 일반 빌드와 초월자 빌드(`--profiles original,transcendent`) 모두 인 게임에서 문제 없이 동작했습니다.
- 배포 zip만 풀어 둔 깨끗한 폴더(필요한 파일 8개만 남긴 상태)에서 준비부터 전체 빌드까지 통과했고, 랜더마이저 폴더를 바꿔도 같은 `--cache` 로 준비 없이 빌드됩니다.

## 파이썬 결과와 다른 점

- 파이썬 `set` 순회 순서는 재현하지 않았습니다. 그 때문에 이름표(NameMap) 순서와 클래스 `Children`·기본 객체 `Data` 목록의 **순서**만 다르고 내용은 같습니다. pak 파일 전체의 해시는 `repak` 이 파일을 담는 순서가 매번 달라 어차피 실행마다 다를 수 있습니다.
- 이름표에는 저장할 때 자동으로 붙는 이름이 파이썬 쪽 이름 위에 더 들어 있습니다.
- 환경변수 `SCU_RETAIN_*` 대신 코드 인자로 같은 동작을 합니다. 패치별 반영 위치는 [`PATCHES.md`](PATCHES.md) 에 있습니다.

## 하지 않은 것

- **초월자 난이도 결과의 파이썬 비교**: pak 4개가 만들어지고 게임에서 정상 동작을 확인했지만, export 단위로 파이썬과 비교하지는 않았습니다.
- **파이썬 안전 검증 모듈**: 교체 모드 전제의 검증(`special_validation`, `*_validation`, 동작 시뮬레이션)과 SHA-256 고정값 검사. 대신 위의 구조 검증과 단계별 비교로 확인했습니다. 랜더마이저를 새 버전으로 바꿀 때는 비교를 다시 해야 합니다.
- **곡옥 색상 pak**: 이 도구로 만들지 않습니다. [여기서 받을 수 있습니다](https://github.com/MaGyul/SCU-Randomizer/releases/download/1.0.0/Kageroh-SCUMagatamaColors_0_P.pak) (`Kageroh-SCUMagatamaColors_0_P.pak`).
- **랜더마이저 0.30.21 외 버전**: 이 포크는 0.30.21 에 고정돼 있습니다.

## 진단용 명령

`stages`, `transplant`, `serialize`, `build-base`, `compose`, `roundtrip`, `dump-json`, `validate-level`, `inspect`, `json2umap` 은 개발·비교용입니다. 일반 사용에는 `build` 와 `prepare` 만 씁니다.
