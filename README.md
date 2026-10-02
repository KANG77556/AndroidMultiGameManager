# Android Multi Game Manager

## v1.9.3

- 여러 AVD를 동시에 시작하지 않고 순차 시작
- 각 AVD가 ADB 등록 및 Android 부팅 완료된 뒤 다음 AVD 시작
- 다중 실행 시 CPU 최대 2코어 / RAM 최대 2048MB 자동 제한
- ADB offline/재시도 시 ADB 서버 재시작 및 reconnect 수행
- AVD 실행 시 DNS 8.8.8.8,1.1.1.1 지정으로 Android 네트워크 VALIDATED 복구
- Cold Boot / software GPU / snapshot 미사용 안정화 옵션 유지
- scrcpy는 Android SDK adb.exe를 강제 사용
- update-config.json을 self-contained Publish/설치본에 외부 파일로 확실히 포함
- v1.9.2의 초간단 UI 및 9:16 휴대폰 미리보기 유지
