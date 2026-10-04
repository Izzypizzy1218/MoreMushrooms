# More Mushrooms 작업 규칙

사용자 요청(2026-10-03): 모드는 실제 RimWorld Mods 폴더에 설치하고, 이후 모든 업데이트를 버전별로 남긴다.

## 업데이트와 보관

- 최초 배포 기준은 `v0.1.0`이다. 이후 사용자에게 전달하는 업데이트마다 버전을 올린다. 작은 수정·밸런스 조정은 패치 버전, 기능 추가는 마이너 버전을 기본으로 한다.
- `Balance/mushrooms.json`과 `About/About.xml`의 버전을 일치시키고, README의 현재 버전 및 `CHANGELOG.md`에 날짜·변경 내용·검증 결과를 반영한다.
- 수정한 동작에 필요한 검증을 완료한 후 `Scripts/Release.ps1 -Install`을 실행한다. 게임 실행 중에는 설치하지 않는다.
- `Releases/v<버전>/`마다 실행 ZIP, 소스 ZIP, SHA-256을 담은 `manifest.json`을 함께 보관한다. 기존 버전의 폴더·ZIP·manifest를 덮어쓰거나 삭제하지 않는다.
- 설치 위치는 `C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\MoreMushrooms`이다. `Scripts/Install.ps1`로 이전 설치를 프로젝트의 `Backups/`에 보존하고 새 설치의 해시를 확인한다.
- 이전 설치 백업은 Mods 폴더 밖에 둔다. 복구할 때는 원하는 버전의 실행 ZIP을 사용한다.
- 완료 보고에는 설치한 버전, 주요 변경점, 검증 결과와 미검증 범위를 적는다.

## 프로젝트 원본과 검증

- 원본 인계 ZIP과 인계 폴더, 승인된 버섯 이미지 및 출처 기록을 보존한다.
- 개발 원본은 이 폴더에서 수정하고 설치 폴더만 단독으로 수정하지 않는다.
- `Tests/validate_assets.py`로 정의·번역·이미지·버전 일치를 검사한다. 게임 동작 변경에는 관련 실행 검증을 추가한다.
- 격리 테스트용 모드와 테스트 저장은 배포 실행 ZIP에 포함하지 않는다. 사용자 저장·설정 대신 기존 격리 테스트 절차를 사용한다.
- 실행 검증과 실제 화면 검수는 구분한다. v0.1.0의 확인된 범위와 남은 항목은 `Docs/VALIDATION.md`를 참고한다.

## 이름과 호환성

- v0.1.3부터 사용자 지정 프로젝트명은 **More Mushrooms**, 개발 폴더와 설치 폴더는 `MoreMushrooms`, 빌드 파일은 `MoreMushrooms.csproj`이다.
- 기존 저장 호환성을 위해 packageId `izzypizzy.rimmushrooms`, 어셈블리/DLL 이름 `RimMushrooms`, C# 네임스페이스 및 클래스명, Def 이름, 텍스처 경로를 유지한다.
- 새 ZIP 이름과 ZIP 내부 최상위 폴더는 `MoreMushrooms`를 사용한다. 과거 버전 ZIP과 기록에 있는 기존 이름은 수정하지 않는다.
- 설치 스크립트는 기존 `Mods/RimMushrooms`와 새 `Mods/MoreMushrooms`를 함께 확인하고, 동일 packageId의 기존 설치를 모두 Mods 밖에 백업한 뒤 하나만 설치한다.
