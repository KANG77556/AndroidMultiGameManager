# Android Multi Game Manager

## v1.8.2

- Block placeholder package `com.example.game` from ADB package checks and game launch
- ADB timeout recovery: device state check -> reconnect -> one retry
- Google account settings fallback: ADD_ACCOUNT_SETTINGS -> SYNC_SETTINGS -> SETTINGS
- Check ADB health before Google account / Play Store operations
- Preview status messages for stopped, connecting, scrcpy failure and timeout states
- Preserve v1.8.1 updater fallback and robust AVD discovery
- Preserve per-instance Google Play accounts, same-game install/play, groups and synchronized controls
