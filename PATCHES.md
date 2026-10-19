# SCU 패치 반영 목록

`app/patcher/scu_map_patch.py`는 랜더마이저(Four9, 0.30.21) 파이썬 소스에 **문자열 패치**를 얹어서 "호스트 방은 그대로 두고
다른 스테이지의 방을 풀에 추가하는(RETAIN)" 맵을 만듭니다. C# 포트는 문자열 치환을 쓰지 않고 이 패치 결과를 **코드에 직접 내장**합니다.
이 문서는 패치 43곳(필수 39 + 선택 4)이 C#의 어디에 어떻게 반영됐는지 추적합니다.
(이 파이썬 파일들은 앱의 `app/patcher`에서 제거되었습니다. 필요하면 git 기록에서 볼 수 있습니다.)

- 코드 안의 표시: 패치로 바뀐 동작은 `// SCU-PATCH <파일>#<번호>` 주석으로 표시합니다. (`grep -rn "SCU-PATCH" src` 로 찾습니다)
- 환경변수 `SCU_RETAIN_DONORS` / `SCU_RETAIN_STAGES` / `SCU_RETAIN_LT`는 C#에서 `retain` 인자(또는 `Compose.Run`의 `retainDonors`)로 바뀝니다. 환경변수를 쓰지 않습니다.
- 상태: **반영** = C#에 구현하고 파이썬 결과와 비교 검증을 마침, **생략(검증)** = 교체 전제의 검증 코드라 유지 모드에서는 어차피 건너뛰던 곳(이식하지 않음), **해당 없음** = 문자열 패치 방식에서만 필요했던 것.

## 0. 패치 빌더 자체 (`app/patcher`)

| 파이썬 | 역할 | C# | 상태 |
|---|---|---|---|
| `scu_retain_host.py` `transplant_retain` | 호스트 방을 남기고 병합본의 방을 풀에 추가 | `RetainHost.cs` | **반영** (sanctuary·soul·abyss: 저장한 umap/uexp 바이트까지 동일) |
| `scu_retain_host.py` `STAGE_PREFIX`/`ROOM_PREFIX` | 액터 이름 접두어 규칙 (`SCApp_Host_`, `SCMix_*`) | `RetainHost.cs` | **반영** |
| `scu_map_patch.py` `prepare_work` / `junction` / `apply_patches` | 랜더마이저 폴더 복제 + 문자열 패치 | 필요 없음 (코드 내장) | 해당 없음 |
| `scu_map_patch.py` `district_look_data` | 가져온 구역의 문·선반 외형 | `DistrictLook.cs` + `StyleRuntime.cs` | **반영** (cicada 최종 맵: 135,699개 export 중 클래스/CDO 순서 외 동일) |
| `scu_map_patch.py` `run_build` / `build_maps` / `main` | 병합 → 기본 맵별 변환 → pak → 검증 | `Builder.cs` (`scu build`) | **반영** (4개 기본 맵 + 초월자 포함 빌드 실행 확인) |
| `scu_map_patch.py` `build_difficulty_pak` | 초월자 규칙 pak 분리 | `DifficultyRules.cs` | **반영** (실행 확인, 파이썬 결과와의 비교는 하지 않음) |
| `scu_map_patch.py` `ensure_prepared` (`launcher.prepare`, `local_map_materials`) | 게임에서 준비 자료 생성 | `Prepare.cs` (`scu prepare`, `scu build`가 자동 실행) | **반영** (준비 맵 4개가 파이썬 결과와 바이트 동일) |
| `make_magatama_color_pak.py` | 곡옥 색상 pak 제작 | — | 제외 (불필요하다고 확인) |

## 1. `app/composer.py` — 성역 도너 방 유지 (필수 13곳) → `Compose.cs`

| # | 원본 변경 요지 | 의도 | 상태 |
|---|---|---|---|
| 1 | `RETAIN` 집합 도입 (`SCU_RETAIN_DONORS`) | 교체하지 않고 남길 성역 방 번호 | **반영** `Compose.Run(retainDonors)` |
| 2 | `if 8 in keep or 8 in RETAIN` | X8 낡은 호스트 가드 제거를 유지 도너에도 적용 | **반영** |
| 3 | `if 4 in keep or 4 in RETAIN` | X4 보관 아이템 제거를 유지 도너에도 적용 | **반영** |
| 4 | `retained_dr`, `is_retain` | 유지 도너를 구분 | **반영** |
| 5 | 유지 도너는 `closure` 제거 대상에서 제외 | 방 액터를 지우지 않음 | **반영** |
| 6 | 유지 도너는 CDO의 루트 참조만 교체, 교체 맵 미등록 | 풀에 원래 방을 남김 | **반영** |
| 7 | `if 9 not in keep and 9 not in RETAIN` | X9 다리 타임라인 유지 | **반영** |
| 8 | `exists==(donor_id in keep or donor_id in RETAIN)` | 검증을 유지 모드에 맞춤 | **반영** |
| 9 | `verify_sanctuary_lt` 를 `SCU_RETAIN_LT`에서 생략 | 교체 전제 검증 생략 | 생략(검증) |
| 10 | 2·3갈래 후보 자리 중복 허용 | 세 스테이지가 같은 자리를 가리켜도 정상 | 생략(검증) — C#에는 이 검사가 없음 |
| 11 | `verify_soul_lt` 생략 | 〃 | 생략(검증) |
| 12 | `verify_lt` 생략 | 〃 | 생략(검증) |
| 13 | 영혼·심연 슬롯 충돌 검사를 `SCU_RETAIN_STAGES`에서 생략 | 추가 모드에서는 슬롯이 겹침 | 생략(검증) |

## 2. 번들 주입 모듈 (필수 12곳)

| 파일 | # | 원본 변경 요지 | C# | 상태 |
|---|---|---|---|---|
| `soul_bundles.py` | 1~3 | `RETAIN`, 호스트 액터 `closure` 제거 안 함, 루트 교체 안 함 | `BundleInjector.cs` (`retain`), `SoulBundles.cs`, `AbyssBundles.cs` | **반영** (abyss/soul 단계 모든 export 동일) |
| `lt_bundles.py` | 1~4 | `RETAIN`, 루트 이름 `(SCApp_Host_<슬롯>_\d+_)?` 허용, 호스트 액터에 `SCApp_Host_<슬롯>_<번호>_` 접두어, 루트 교체 안 함 | `LtBundles.InjectBundles` (abyss 2·3갈래) | **반영** |
| `soul_lt_bundles.py` | 1~4 | 위와 동일 (영혼 2·3갈래) | `LtBundles.InjectBundles` (soul 2·3갈래) | **반영** |
| `updates/sanctuary-LT/bundle_core.py` | 1~4 | 위와 동일 (성역 LT 공통 코어) | `LtBundles.InjectBundles` (`SCMix_Stage6_LT_`) | **반영** |
| `sanctuary_relocation.py` | 1 | `set(rr)==set(dest)` → `set(dest)<=set(rr)` | — (검증 모듈 `verify`에서만 쓰임) | 생략(검증) |
| `sanctuary_lt_bundles.py` | 1 | 유지 모드에서 `verify(data,D)` 생략 | `SanctuaryLt.cs` (검증 호출 없음) | 생략(검증) |

## 3. `app/special_validation.py` — 유지 모드 검증 완화 (필수 9곳)

이 모듈은 합성 결과를 *검증*만 하는 코드입니다. C# 빌더는 그 검증들을 이식하지 않았으므로(아래 "이식하지 않은 검증" 참고) 9곳 모두 **생략(검증)** 입니다.

## 4. 선택 패치 (4곳)

| 파일 | # | 원본 변경 요지 | C# | 상태 |
|---|---|---|---|---|
| `composer.py` | 1 | 유지 모드에서는 호스트 생성(구역 외형 계산) 단계 생략 | `Builder.cs`는 해당 단계를 호출하지 않음 | 해당 없음 |
| `composer.py` | 2 | `multi_map.verify_generation` → `special_validation.verify` | 검증 생략 | 해당 없음 |
| `district_style_runtime.py` | 1 | 시작 이벤트 노드 수 가정(`==3`)을 `>=3`, 끝 쪽 검사로 완화 | `StyleRuntime.Install` | **반영** |
| `district_style_runtime.py` | 2 | 외형 예약 호출을 `Return` 앞에 삽입, 크기는 원래 크기 + 호출 크기 | `StyleRuntime.Install` | **반영** |

## 5. C# 쪽에서 새로 생긴 변경 (원본 파이썬과 다른 부분)

원본은 JSON 문자열을 바꾸는 방식이라 필요 없던 처리가 객체 모델에서 필요해졌습니다. 결과는 파이썬과 의미상 같도록 맞췄습니다.

| 위치 | 내용 | 이유 |
|---|---|---|
| `DeepClone.cs` | 복제한 `FName`을 대상 패키지에 다시 묶고 이름표 인덱스를 다시 계산 | `FName`은 자기 패키지 기준 인덱스를 들고 있어, 다른 패키지로 옮기면 잘못된 인덱스로 저장됨 |
| 번들 주입 전반 | 원본 패키지의 이름을 export 복사 **전에** 대상 이름표에 추가 | 위 재결합이 최종 인덱스를 필요로 함. 이름표 순서는 원본과 동일 |
| `Names.cs` | 이름을 "더미 `FName`"으로 만들고 저장 직전 `Names.Finalize`에서 이름표에 추가 | 파이썬은 문자열만 다루다가 JSON 변환기가 쓰는 순간 모르는 이름을 이름표 끝에 붙임. 같은 효과를 재현. 저장되지 않는 이름(배열 요소 이름, 조상 경로)은 추가하지 않음 |
| `Names.cs` | 이름의 `_숫자` 접미사를 JSON 변환기처럼 번호로 분리 | 게임이 `Foo_10`과 (`Foo`, 11)을 같은 이름으로 취급하도록 |
| `HostSerialization.cs` | UAssetGUI 프로세스 2회 호출 대신 같은 프로세스에서 2회 쓰기 | 중간 JSON(수백 MB) 제거 |
| 집합 순회 순서 | 파이썬 `set` 순회 순서에 의존하던 곳(방 액터 이름 등록 순서, 클래스 `Children`·CDO `Data` 순서)은 오름차순으로 고정 | 파이썬의 `set` 순회 순서는 구현 세부사항이라 재현하지 않음. 이름표 순서와 목록 순서만 달라지고 내용은 같음 |
| `SanctuaryLt.cs` | 성역 LT의 `prepared-donor.json`(258MB)·`parsed.json`(232MB)을 처음 한 번만 읽어 바이너리 패키지로 캐시 | 랜더마이저가 JSON으로 배포하는 준비 자료 |

## 6. 파이썬과의 비교 검증 현황

파이썬 빌더를 같은 입력으로 단계별로 실행해 각 단계 직후 패키지를 JSON으로 저장하고(기준), C# 결과와 비교했습니다.

| 단계 | export 수 | 결과 |
|---|---|---|
| 1 compose | 45,297 | 모두 동일 |
| 2 abyss (+콜백 5) | 58,493 | 모두 동일 |
| 3 soul (+콜백, 래치, 꽃) | 77,163 | 모두 동일 |
| 4 soul-lt (+콜백 11) | 100,227 | 모두 동일 |
| 5 abyss-lt (+콜백 6) | 116,144 | 모두 동일 |
| 6 sanctuary-lt (+콜백·다리·물) | 135,695 | 2개(클래스·CDO)만 목록 **순서** 차이 |
| 7 cicada 구역 외형 | 135,699 | 클래스·CDO 순서 차이 외 동일 |
| 기본 맵 sanctuary / soul / abyss 변환·저장 | 138,456 / 138,851 / 137,299 | 저장한 umap/uexp **바이트 동일** (SHA-256 일치) |

이름표(NameMap)는 파이썬 쪽 이름이 모두 C# 안에 들어 있고, C#에는 저장 시 자동으로 붙는 이름이 추가로 있습니다.

## 7. 이식하지 않은 것

- **검증 코드** (fail-closed 검사): `special_validation`, `soul_lt_validation`, `lt_validation`, `abyss_validation`, `sanctuary_relocation.verify`, `*_behavior` 시뮬레이션. 유지 모드에서는 대부분 건너뛰던 것들이고, 대신 위 비교 검증으로 결과를 확인했습니다. 랜더마이저를 새 버전으로 갱신할 때는 이 비교를 다시 해야 합니다.
- **초월자 난이도 결과의 파이썬 비교**: 빌드는 돌아가고 pak 4개가 만들어지지만, 파이썬 결과와 export 단위로 비교하지는 않았습니다.
- **곡옥 색상 pak**: 제외.
- **무결성 해시 검사**: `resources.json`, 각 JSON의 SHA-256 고정값, `payload_hashes`. (게임 pak 해시 검사는 준비 단계에서 합니다.)
- **준비 단계의 일부**: `ensure_actor_inventories`, `ensure_local_assets`(곡옥·팔레트 등). SCU 빌드가 쓰지 않는 자료라 만들지 않습니다.
