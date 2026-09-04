# Changelog

All notable changes to the Ava-Twin Unity SDK will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- The native Android/iOS customizer now presents Male and Female avatar families. Existing
  `generic` avatars remain immutable and appear under Male; new canonical `male` items are merged
  into that presentation, while canonical `female` items remain isolated.

### Compatibility
- Existing generic selections continue to save as `generic`. Female selections save as `female`,
  and previously saved generic, male, or female recipes restore into the matching presentation.

### Server-side change (no SDK API change)
- The Ava-Twin server now enforces per-platform identifier registration. Register your origin (WebGL) or bundle ID (native) in the [Console](https://console.ava-twin.me) before deploying. See README "Register Your Build Identifiers" for details.

### Improved
- Token-mint failures with `ORIGIN_NOT_REGISTERED`, `ORIGIN_NOT_ALLOWED`, `BUNDLE_NOT_REGISTERED`, or `BUNDLE_NOT_ALLOWED` now log a developer-friendly Debug.LogError that includes the server's message and a link to https://ava-twin.me/docs/setup#registering-identifiers. The general token-mint error path is unchanged.

## [1.0.0] - 2026-05-06 (re-release)

### Added (this re-release)
- Editor mode token-mint bypass — running in Unity Editor no longer counts toward session quota.
- Welcome Window's Test Connection now works post-strict server enforcement.

### Original 1.0.0 features
- `SDK.OpenCustomizerAsync()` — open the avatar customizer, returns `AvatarResult`.
- `SDK.LoadAvatar(avatarId)` — load any avatar by ID, concurrent-safe for multiplayer.
- `AvatarResult` with `Root`, `AvatarId`, `SkinToneHex`, `GetUnityHumanoidAvatar()`.
- WebGL embedded iframe customizer.
- Native mobile customizer UI for Android and iOS, with drag-to-rotate avatar preview, dynamic portrait/landscape camera framing, and category-switching loading indicators.
- Default/clear skin tone option matching the web customizer.
- Editor quick-test: loads a random avatar without opening the customizer UI.
- URP and Built-in render pipeline shaders (auto-detected). URP shader optimized for mobile and WebGL build size, with safe variants for aggressive shader stripping.
- Humanoid avatar configuration for Mecanim animations.
- In-memory GLB caching for multiplayer (same avatar = one download) and disk caching with configurable TTL.
- Automatic dependency resolution (glTFast, Newtonsoft JSON).
- Demo scene with third-person controller and Welcome window for credential setup.
- `bundle_id` (Application.identifier) reporting on customizer-token-mint — used by the server for app-cap enforcement.
- `platform` (Application.platform) reporting on customizer-token-mint — used by the server for analytics.
- `AvaTwinPlayer.ExternalUserId` API for Agency-tier customers to use their own user identity system instead of Ava-Twin's built-in login panel.
- `external_user_id` parameter sent on player-* endpoints (guest-init, login, avatar-save) when `ExternalUserId` is set.
- Server-controlled watermark rendering — the customizer page (WebView/iframe) respects per-plan branding configuration returned by the server. The SDK does not render any in-game branding, so no client-side guard is required.

### Notes
- Initial public release. SDK contracts aligned with Ava-Twin v4 pricing.
