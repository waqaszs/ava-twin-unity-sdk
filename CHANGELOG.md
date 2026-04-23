# Changelog

All notable changes to the Ava-Twin Unity SDK will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.1] - 2026-04-24

### Fixed
- Mobile customizer preview avatar and camera framing can now be repositioned by the host project. Assigning the PreviewObject root's transform moves the preview avatar, camera, and all focus points together — visual framing is preserved regardless of where the group is placed.

### Changed
- Renamed Resources/CameraSetup.prefab to Resources/PreviewObject.prefab. The default value of `cameraSetupResourcePath` updated accordingly; existing projects that overrode this field in the Inspector may need to re-point it to "PreviewObject".

## [1.1.0] - 2026-04-19

### Added
- Android and iOS mobile support with native in-app customizer
- Drag-to-rotate avatar preview in mobile customizer
- Default/clear skin tone option matching web customizer
- Loading indicators during category switching and thumbnail loading

### Changed
- Renamed AvaTwinCustomizerController to AvaTwinMobileCustomizer
- Simplified mobile customizer loading — single Resources.Load path, no Inspector configuration needed
- Platform routing: WebGL uses iframe, Android/iOS uses native customizer, Editor uses random load, Desktop shows not-supported warning

### Fixed
- UV seam artifacts on avatar meshes (disabled mipmaps on albedo textures)
- Mobile customizer prefab now auto-loads from Resources — no manual setup required

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
