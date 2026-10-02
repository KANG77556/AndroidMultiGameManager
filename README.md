# Android Multi Game Manager

## v1.9.4

- Google 로그인 전 TLS 인증서 사전점검 추가
- ssl.gstatic.com 인증서 발급자를 직접 확인
- GNE_CERT 등 HTTPS 보안 검사 인증서 감지 시 Google 로그인 화면을 열지 않고 원인 안내
- 기본 화면에 'Google 네트워크 점검' 버튼 및 상태 메시지 추가
- 현재 학교/기관망에서 CN=GNE_CERT, O=GNE, C=KR 실제 탐지 검증 완료
- v1.9.3의 순차 AVD 시작, ADB 복구, DNS 안정화, 다중 실행 자원 제한 유지
