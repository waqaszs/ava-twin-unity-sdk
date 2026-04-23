using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace AvaTwin
{
    /// <summary>
    /// Public facade for player identity + persistence. Mobile parity for
    /// the web customizer's PlayerProvider.
    ///
    /// Phase A (shipped): DeviceId, InitGuestAsync, LogoutAsync, token decode.
    /// Phase B (later): SaveActiveAvatarAsync, GetActiveAvatarAsync, token refresh.
    /// Phase C (later): RegisterAsync, LoginAsync, MigrateGuestAsync.
    ///
    /// Typical usage:
    ///   await AvaTwinPlayer.InitGuestAsync();              // idempotent
    ///   var result = await SDK.OpenCustomizerAsync();       // uses AvaTwinPlayer.PlayerId internally
    ///   // AvaTwinPlayer.IsAuthenticated == true here
    /// </summary>
    public static class AvaTwinPlayer
    {
        // ── Constants ────────────────────────────────────────────────────

        /// <summary>
        /// If the stored token has fewer than this many seconds left on
        /// its exp claim, <see cref="RefreshTokenIfNeededAsync"/> calls
        /// the backend to refresh it. Default: 1 day.
        /// </summary>
        private const long RefreshThresholdSeconds = 24 * 60 * 60;

        // ── State cache (populated on init + on token changes) ───────────

        private static string _cachedPlayerId;
        private static AvaTwinJwt.Claims _cachedClaims;
        private static bool _claimsResolved;

        /// <summary>
        /// Fires when IsAuthenticated / PlayerId changes (InitGuest, Login,
        /// Register, Logout). Handlers run on the Unity main thread since
        /// the operations are Task-based and awaited from the main thread.
        /// </summary>
        public static event Action OnAuthStateChanged;

        // ── Public read-only properties ──────────────────────────────────

        /// <summary>
        /// Stable per-device identifier. Generated on first access and
        /// persisted in PlayerPrefs. Safe to read before InitGuestAsync.
        /// </summary>
        public static string DeviceId => AvaTwinPlayerStorage.GetDeviceId();

        /// <summary>
        /// The current player's server-issued ID, or null if not signed in.
        /// Populated from the player JWT on init / token change.
        /// </summary>
        public static string PlayerId
        {
            get
            {
                EnsureClaimsResolved();
                return _cachedPlayerId;
            }
        }

        /// <summary>
        /// True when a non-expired player token is present. Expiry check
        /// reads the JWT's <c>exp</c> claim with a 60s safety skew.
        /// </summary>
        public static bool IsAuthenticated
        {
            get
            {
                EnsureClaimsResolved();
                return AvaTwinJwt.IsUnexpired(_cachedClaims);
            }
        }

        /// <summary>
        /// Whether the token belongs to a guest (anonymous) or a registered
        /// (username+password) account. Guest detection currently requires
        /// a round-trip with the server to fetch username — Phase A returns
        /// true when authenticated (guest) and will be extended in Phase C
        /// once we cache username from register/login responses.
        /// </summary>
        public static bool IsGuest => IsAuthenticated && string.IsNullOrEmpty(CachedUsername);

        /// <summary>Cached username from last register/login response. Null for guests.</summary>
        public static string CachedUsername { get; private set; }

        // ── Lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// Creates or recovers a guest player identity. Idempotent — safe
        /// to call multiple times; the server upserts by device_id so the
        /// same device always maps to the same player_id.
        ///
        /// Returns true on success. On failure logs and returns false so
        /// callers can gracefully fall back (local-only mode).
        /// </summary>
        public static async Task<bool> InitGuestAsync(CancellationToken ct = default)
        {
            // Fast path — already authenticated with a live token.
            if (IsAuthenticated)
                return true;

            var creds = GetCredentialsOrWarn();
            if (creds == null) return false;
            var baseUrl = GetBaseUrl();

            try
            {
                var resp = await PlayerApi.GuestInitAsync(
                    baseUrl, creds.AppId, creds.ApiKey, DeviceId, ct);

                if (resp == null || string.IsNullOrEmpty(resp.token))
                {
                    Debug.LogError("[Ava-Twin] player-guest-init returned no token.");
                    return false;
                }

                SetTokenInternal(resp.token, resp.username);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Ava-Twin] InitGuestAsync failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Clears the player token and cached avatar on-device. Does NOT
        /// sign out from the server (tokens are short-lived by design).
        /// Does NOT clear device_id.
        /// </summary>
        public static void Logout()
        {
            AvaTwinPlayerStorage.ClearSession();
            CachedUsername = null;
            _cachedPlayerId = null;
            _cachedClaims = default;
            _claimsResolved = true;  // already known: unauthenticated
            OnAuthStateChanged?.Invoke();
        }

        // ─────────────────────────────────────────────────────────────────
        // Phase B — Cloud persistence
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Persists the given variation_selections as the player's active
        /// avatar on the Ava-Twin backend. Overwrites any previous active
        /// avatar (backend sets is_active=false on prior row, inserts new).
        ///
        /// Returns the server-issued <c>avatar_id</c> (public_combo_id) on
        /// success, or null on failure. Updates the local cache on success.
        /// </summary>
        public static async Task<string> SaveActiveAvatarAsync(
            VariationSelections selections, CancellationToken ct = default)
        {
            if (selections == null)
                throw new ArgumentNullException(nameof(selections));

            if (!await EnsureAuthenticatedAsync(ct))
                return null;

            await RefreshTokenIfNeededAsync(ct);

            try
            {
                var resp = await PlayerApi.AvatarSaveAsync(
                    GetBaseUrl(), GetToken(), selections, ct);

                if (resp == null || !resp.success || string.IsNullOrEmpty(resp.avatar_id))
                {
                    Debug.LogWarning("[Ava-Twin] player-avatar-save did not return a usable avatar_id.");
                    return null;
                }

                CacheActiveAvatar(selections);
                return resp.avatar_id;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Ava-Twin] SaveActiveAvatarAsync failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fetches the player's currently-active avatar from the backend.
        /// Returns null if the player has no saved avatar, auth fails, or
        /// the network call errors. On network failure with a cached value
        /// available, returns the cache (offline fallback).
        /// </summary>
        public static async Task<PlayerAvatarRecord> GetActiveAvatarAsync(CancellationToken ct = default)
        {
            if (!await EnsureAuthenticatedAsync(ct))
                return GetCachedActiveAvatar();  // fall through to cache

            await RefreshTokenIfNeededAsync(ct);

            try
            {
                var resp = await PlayerApi.AvatarGetSelfAsync(GetBaseUrl(), GetToken(), ct);

                if (resp?.avatar == null)
                    return null;  // server says no saved avatar (valid "empty" state)

                if (resp.avatar.variation_selections != null)
                    CacheActiveAvatar(resp.avatar.variation_selections);

                return resp.avatar;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[Ava-Twin] GetActiveAvatarAsync failed, using cache: {ex.Message}");
                return GetCachedActiveAvatar();
            }
        }

        /// <summary>
        /// Synchronous accessor for the cached active avatar JSON (persisted
        /// in PlayerPrefs). Returns null if no avatar has been saved yet on
        /// this device. Use for fast offline startup before kicking off the
        /// network call.
        /// </summary>
        public static PlayerAvatarRecord GetCachedActiveAvatar()
        {
            var json = AvaTwinPlayerStorage.GetCachedAvatarJson();
            if (string.IsNullOrEmpty(json)) return null;

            try
            {
                var selections = JsonUtility.FromJson<VariationSelections>(json);
                if (selections == null) return null;
                return new PlayerAvatarRecord
                {
                    id = null,
                    avatar_id = null,
                    variation_selections = selections,
                    created_at = null
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Refreshes the 30-day player JWT when it's within the skew window
        /// of expiring (default: &lt; 1 day left). No-op otherwise.
        /// Idempotent and safe to call frequently — e.g. on OnApplicationFocus.
        /// </summary>
        public static async Task<bool> RefreshTokenIfNeededAsync(CancellationToken ct = default)
        {
            EnsureClaimsResolved();
            if (!_cachedClaims.IsValidShape) return false;

            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var secondsLeft = _cachedClaims.ExpUnix - nowUnix;
            if (secondsLeft > RefreshThresholdSeconds) return true;  // still fresh, no work needed

            var creds = GetCredentialsOrWarn();
            if (creds == null) return false;

            try
            {
                var resp = await PlayerApi.TokenRefreshAsync(
                    GetBaseUrl(), creds.AppId, creds.ApiKey, GetToken(), ct);

                if (resp == null || string.IsNullOrEmpty(resp.token))
                {
                    Debug.LogWarning("[Ava-Twin] player-token-refresh returned no token.");
                    return false;
                }

                SetTokenInternal(resp.token, CachedUsername);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Ava-Twin] RefreshTokenIfNeededAsync failed: {ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Phase C — Identity linking (register / login / migrate / username)
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a new registered player from the current guest session.
        /// The caller must already be authenticated as a guest (InitGuestAsync
        /// first). Guest avatars transfer to the new registered player
        /// server-side.
        ///
        /// Validation (server-side):
        ///   username: /^[a-zA-Z0-9_]{3,20}$/
        ///   password: min 8 chars, 1 upper + 1 lower + 1 digit + 1 symbol
        ///
        /// On success, updates the stored token to the new registered JWT.
        /// </summary>
        public static async Task<bool> RegisterAsync(
            string username, string password, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                throw new ArgumentException("username and password are required");

            if (!IsAuthenticated)
            {
                Debug.LogError(
                    "[Ava-Twin] RegisterAsync requires a guest session first. " +
                    "Call AvaTwinPlayer.InitGuestAsync() before RegisterAsync().");
                return false;
            }

            try
            {
                var resp = await PlayerApi.RegisterAsync(
                    GetBaseUrl(), GetToken(), username, password, ct);

                if (resp == null || string.IsNullOrEmpty(resp.token))
                {
                    Debug.LogWarning("[Ava-Twin] player-register returned no token.");
                    return false;
                }

                SetTokenInternal(resp.token, resp.username);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Ava-Twin] RegisterAsync failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Signs into an existing registered Ava-Twin account. If the device
        /// currently has a guest session, its PlayerId is passed as
        /// <c>guest_player_id</c> so the server transfers the guest's saved
        /// avatars to the registered account (atomic on the server side).
        ///
        /// On success, replaces the stored token with the registered JWT.
        /// </summary>
        public static async Task<bool> LoginAsync(
            string username, string password, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                throw new ArgumentException("username and password are required");

            var creds = GetCredentialsOrWarn();
            if (creds == null) return false;

            // If we already have a guest session, pass its PlayerId so the
            // server transfers guest avatars into the registered account.
            string guestPlayerId = null;
            if (IsAuthenticated && IsGuest)
                guestPlayerId = PlayerId;

            try
            {
                var resp = await PlayerApi.LoginAsync(
                    GetBaseUrl(), creds.AppId, creds.ApiKey,
                    username, password, guestPlayerId, ct);

                if (resp == null || string.IsNullOrEmpty(resp.token))
                {
                    Debug.LogWarning("[Ava-Twin] player-login returned no token.");
                    return false;
                }

                SetTokenInternal(resp.token, resp.username);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Ava-Twin] LoginAsync failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Explicit guest-to-registered migration. Only valid when the
        /// current session is registered (not guest). Looks up a guest
        /// record by this device's device_id and merges its avatar_config
        /// into the registered player, then deletes the guest row.
        ///
        /// Normally a no-op after login (login already does the transfer);
        /// exposed for edge cases (manual reconciliation, moving devices).
        /// </summary>
        public static async Task<bool> MigrateGuestAsync(CancellationToken ct = default)
        {
            if (!IsAuthenticated || IsGuest)
            {
                Debug.LogError(
                    "[Ava-Twin] MigrateGuestAsync requires a registered session. " +
                    "Call LoginAsync or RegisterAsync first.");
                return false;
            }

            var creds = GetCredentialsOrWarn();
            if (creds == null) return false;

            try
            {
                var resp = await PlayerApi.MigrateGuestAsync(
                    GetBaseUrl(), creds.AppId, creds.ApiKey, GetToken(), DeviceId, ct);

                return resp != null && resp.migrated;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Ava-Twin] MigrateGuestAsync failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Server-side check for username availability.
        /// Returns true if available, false if taken OR format-invalid.
        /// The <paramref name="reason"/> out parameter explains false
        /// results (e.g., "Username is taken" vs format-specific messages).
        /// </summary>
        public static async Task<bool> CheckUsernameAvailableAsync(
            string username, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(username)) return false;

            var creds = GetCredentialsOrWarn();
            if (creds == null) return false;

            try
            {
                var resp = await PlayerApi.UsernameCheckAsync(
                    GetBaseUrl(), creds.AppId, creds.ApiKey, username, ct);
                return resp != null && resp.available;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Ava-Twin] CheckUsernameAvailableAsync failed: {ex.Message}");
                return false;
            }
        }

        // ── Internal helpers used by sibling classes (PlayerApi callers, SDK) ──

        /// <summary>
        /// Returns the current player token (may be expired). Null if not set.
        /// Consumed by SDK.LoadAvatar and similar to attach Authorization.
        /// </summary>
        internal static string GetToken() => AvaTwinPlayerStorage.GetPlayerToken();

        /// <summary>
        /// Replaces the stored token and updates cached claims + username.
        /// Fires OnAuthStateChanged.
        /// </summary>
        internal static void SetTokenInternal(string token, string username)
        {
            AvaTwinPlayerStorage.SetPlayerToken(token);
            CachedUsername = string.IsNullOrEmpty(username) ? null : username;
            _claimsResolved = false;
            EnsureClaimsResolved();
            OnAuthStateChanged?.Invoke();
        }

        private static void EnsureClaimsResolved()
        {
            if (_claimsResolved) return;
            var token = AvaTwinPlayerStorage.GetPlayerToken();
            _cachedClaims = AvaTwinJwt.DecodeClaims(token);
            _cachedPlayerId = _cachedClaims.IsValidShape ? _cachedClaims.Sub : null;
            _claimsResolved = true;
        }

        /// <summary>
        /// Makes sure a valid player token exists. If the device already has
        /// a non-expired token: no-op. Otherwise: attempts InitGuestAsync.
        /// Returns true if authenticated after the call.
        ///
        /// Public so <see cref="SDK.OpenCustomizerAsync"/> and host apps can
        /// lazily establish a guest session just before they need one.
        /// </summary>
        public static async Task<bool> EnsureAuthenticatedAsync(CancellationToken ct = default)
        {
            if (IsAuthenticated) return true;
            return await InitGuestAsync(ct);
        }


        private static void CacheActiveAvatar(VariationSelections selections)
        {
            if (selections == null) return;
            try
            {
                var json = JsonUtility.ToJson(selections);
                AvaTwinPlayerStorage.SetCachedAvatarJson(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Ava-Twin] CacheActiveAvatar serialization failed: {ex.Message}");
            }
        }

        private static Credentials GetCredentialsOrWarn()
        {
            var creds = Resources.Load<Credentials>("Credentials");
            if (creds == null || string.IsNullOrEmpty(creds.AppId) || string.IsNullOrEmpty(creds.ApiKey))
            {
                Debug.LogError(
                    "[Ava-Twin] Credentials missing. Set appId and apiKey in " +
                    "Assets/Resources/Credentials.asset (Window > Ava-Twin > Setup).");
                return null;
            }
            return creds;
        }

        private static string GetBaseUrl()
        {
            var cfg = Resources.Load<AvaTwinConfig>("AvaTwinConfig");
            return cfg != null && !string.IsNullOrEmpty(cfg.baseApiUrl)
                ? cfg.baseApiUrl
                : "https://customizer.ava-twin.me";
        }
    }
}
