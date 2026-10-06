More Mushrooms 오늘 작업 인계 묶음
저장일: 2026-10-05, 한국 시간

오늘 진행한 독버섯 5종의 그림 제작, 독성 조사, 게임 효과 논의와 최신 수정안을 모았습니다.
이 묶음은 제작 자료와 설계 인계본입니다. 설치 가능한 독버섯 모드 업데이트는 아닙니다.

빠르게 확인하기
1. art/comparison.html을 브라우저로 열면 5종의 성장 모습과 수확 상자 3단계를 볼 수 있습니다.
2. art/preview.html에서는 성장률과 스택 수량을 직접 조절할 수 있습니다.
3. notes/01-독버섯효과최신설계안.txt가 오늘 논의의 최신 설계 기준입니다.
4. notes/02-대화와결정사항요약.txt에는 작업 흐름, 사용자 선호, 남은 작업을 정리했습니다.
5. notes/03-독성조사와출처.txt에는 실제 중독 정보와 확인한 출처 링크를 기록했습니다.

그림 파일
- art/masters/: ImageGen 생성 원본 25장, 1254×1254 RGBA
- art/Textures/: 모드용 내보내기 25장, 256×256 RGBA
- 식물 외형 A/B 10장 + 수확 상자 적음/중간/가득 15장
- art/references/: 실물 사진, 기존 모드 그림 참조, 사진 저작자와 라이선스
- art/prompts/ 및 art/generation-prompts.json: 각 그림의 생성 프롬프트와 참조 기록
- art/build_assets.py: 원본의 크기·위치를 정렬하고 미리보기 조각을 만드는 스크립트

다른 컴퓨터에서 보기
- ZIP을 먼저 폴더로 풀고 art/comparison.html 또는 art/preview.html을 여세요.
- 미리보기는 그림이 파일 안에 포함되어 있어 별도 서버나 이미지 경로 설정이 필요 없습니다.
- 저장된 프롬프트 안의 원래 컴퓨터 절대 경로는 작업 이력입니다. 실제 파일은 art/masters/와 art/references/에 함께 들어 있습니다.
- build_assets.py 재실행에는 Python과 Pillow가 필요합니다. 이미 완성된 PNG와 HTML을 보는 데는 필요하지 않습니다.
- build_assets.py는 미리보기 조각을 생성합니다. 독립 실행 HTML 두 개는 완성본을 그대로 포함했습니다.

작업 기준 저장소
https://github.com/Izzypizzy1218/MoreMushrooms
검토 기준: v0.3.3
로컬 확인 커밋: 82c5c1c9bbb801560a0d14a0c1fd16f0eaf8dc16
이 인계본에는 저장소 전체 복제본이나 새 게임 코드가 포함되어 있지 않습니다.
기존 식용 버섯 모드 작업을 이어갈 때에는 본체의 최신 저장소 상태를 기준으로 통합하세요.

완료 상태
- 독버섯 그림 제작과 투명도·크기 검사 완료
- 오늘 제작 당시 브라우저 미리보기 검수 완료: art/validation.json
- 효과와 의료 규칙은 설계 단계
- 독버섯 Def, 중독 로직, 등장 이벤트, 감정 시스템 구현 및 RimWorld 내 테스트는 미실시

검증 파일
- package-manifest.json: 묶음 안 파일별 크기와 SHA-256
- ZIP 옆의 .sha256.txt: 압축 파일 자체의 SHA-256

대화 기록 범위
대화는 결정사항 중심의 요약입니다. 전체 채팅 원문 내보내기는 아닙니다.
예전 수치와 최신 수치가 다르면 notes/01-독버섯효과최신설계안.txt를 우선하세요.

사진 출처와 라이선스는 art/references/sources.json에 보존되어 있습니다.
