# Android Multi Game Manager

## v1.9.2

- scrcpy가 앱과 동일한 Android SDK adb.exe를 사용하도록 ADB 환경변수 고정
- scrcpy 실행 전 ADB 연결 및 Android 부팅 완료 확인
- scrcpy 콘솔창 숨김 실행 및 실패 로그를 카드 상태 메시지에 표시
- AVD 시작 시 no-snapshot-load + software GPU + no-boot-anim으로 안정 부팅
- Play Store 이미지 AVD의 PlayStore/GPU/Fast Boot 설정 자동 보정
- 휴대폰 미리보기 상태를 중지됨/연결 중/ADB 오류/scrcpy 오류로 구체화
- 인스턴스별 scrcpy 창 제목 고유화
- v1.9.1 설치 자동 종료/교체 기능 유지
