# Android Multi Game Manager

Windows GUI manager for running and controlling multiple Android emulator game instances.

## v1.8.1

- Fix updater when update-config.json is missing or empty by using a built-in GitHub manifest URL
- Ensure update-config.json is included in publish/install output
- Recover AVD list from emulator -list-avds, ANDROID_AVD_HOME, ANDROID_USER_HOME, ~/.android/avd and running ADB emulators
- Preserve v1.8 Google Play multi-account, same-game install and synchronized control features
