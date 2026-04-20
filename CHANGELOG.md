# Changelog

All notable changes to the Ava-Twin Unity SDK will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-04-18

### Added
- `SDK.OpenCustomizerAsync()` — open the avatar customizer, returns `AvatarResult`
- `SDK.LoadAvatar(avatarId)` — load any avatar by ID, concurrent-safe for multiplayer
- `AvatarResult` with `Root`, `AvatarId`, `SkinToneHex`, `GetUnityHumanoidAvatar()`
- WebGL embedded iframe customizer
- Native mobile customizer UI (Android/iOS — coming soon)
- Editor quick-test: loads random avatar without customizer UI
- URP and Built-in render pipeline shaders (auto-detected)
- Humanoid avatar configuration for Mecanim animations
- In-memory GLB caching for multiplayer (same avatar = one download)
- Disk caching with configurable TTL
- Automatic dependency resolution (glTFast, Newtonsoft JSON)
- Demo scene with third-person controller
- Welcome window with credential setup
