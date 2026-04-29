using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace AvaTwin
{
    /// <summary>
    /// Thin HTTP client for the Ava-Twin player identity API.
    ///
    /// All endpoints POST to <c>{baseApiUrl}/api/player/&lt;name&gt;</c> — the
    /// Next.js proxies on customizer.ava-twin.me that wrap the Supabase
    /// player-* edge functions. This matches the web customizer's call
    /// pattern exactly (same routes, same auth mechanisms).
    ///
    /// Auth matrix (mirrors the Next.js proxies):
    ///   guest-init / login / username-check : Authorization: Bearer &lt;customizer-token&gt;
    ///   register / avatar-save / avatar-get : x-player-token &lt;player-token&gt;
    ///
    /// The customizer token is minted on demand via
    /// <see cref="MintCustomizerTokenAsync"/> (hits {baseApiUrl}/api/token-mint
    /// with {appId, apiKey} — identical to the AvaTwinApiClient flow).
    /// </summary>
    internal static class PlayerApi
    {
        private enum AuthMode { None, CustomizerBearer, PlayerTokenHeader }

        // ── Endpoints ────────────────────────────────────────────────────

        public static async Task<PlayerGuestInitResponse> GuestInitAsync(
            string baseUrl, string appId, string apiKey, string deviceId, CancellationToken ct = default)
        {
            var customizerToken = await MintCustomizerTokenAsync(baseUrl, appId, apiKey, ct);
            var body = new PlayerGuestInitRequest { device_id = deviceId };
            return await PostAsync<PlayerGuestInitRequest, PlayerGuestInitResponse>(
                baseUrl, "guest-init", body,
                AuthMode.CustomizerBearer, customizerToken, ct);
        }

        public static Task<PlayerAvatarSaveResponse> AvatarSaveAsync(
            string baseUrl, string playerToken, VariationSelections selections, CancellationToken ct = default)
        {
            var body = new PlayerAvatarSaveRequest { variation_selections = selections };
            return PostAsync<PlayerAvatarSaveRequest, PlayerAvatarSaveResponse>(
                baseUrl, "avatar-save", body,
                AuthMode.PlayerTokenHeader, playerToken, ct);
        }

        /// <summary>Client-mode: fetches the caller's own active avatar via player token.</summary>
        public static Task<PlayerAvatarGetResponse> AvatarGetSelfAsync(
            string baseUrl, string playerToken, CancellationToken ct = default)
        {
            return PostRawAsync<PlayerAvatarGetResponse>(
                baseUrl, "avatar-get", bodyJson: "{}",
                AuthMode.PlayerTokenHeader, playerToken, ct);
        }

        /// <summary>Server-mode: look up another player's active avatar via API key.
        /// Not available through Next.js proxy — deferred. Current implementation
        /// proxies through avatar-get with x-player-token instead (self-only).</summary>
        public static Task<PlayerAvatarGetResponse> AvatarGetByIdAsync(
            string baseUrl, string appId, string apiKey, string playerId, CancellationToken ct = default)
        {
            throw new NotSupportedException(
                "AvatarGetByIdAsync (server-mode lookup by player_id) requires a " +
                "dedicated Next.js proxy that does not yet exist. Use AvatarGetSelfAsync " +
                "with the player's own token.");
        }

        public static Task<PlayerAuthResponse> RegisterAsync(
            string baseUrl, string playerToken, string username, string password, CancellationToken ct = default)
        {
            var body = new PlayerRegisterRequest { username = username, password = password };
            return PostAsync<PlayerRegisterRequest, PlayerAuthResponse>(
                baseUrl, "register", body,
                AuthMode.PlayerTokenHeader, playerToken, ct);
        }

        public static async Task<PlayerAuthResponse> LoginAsync(
            string baseUrl, string appId, string apiKey,
            string username, string password, string guestPlayerId, CancellationToken ct = default)
        {
            var customizerToken = await MintCustomizerTokenAsync(baseUrl, appId, apiKey, ct);
            var body = new PlayerLoginRequest
            {
                username = username,
                password = password,
                guest_player_id = guestPlayerId
            };
            return await PostAsync<PlayerLoginRequest, PlayerAuthResponse>(
                baseUrl, "login", body,
                AuthMode.CustomizerBearer, customizerToken, ct);
        }

        public static Task<PlayerMigrateGuestResponse> MigrateGuestAsync(
            string baseUrl, string appId, string apiKey, string playerToken, string deviceId, CancellationToken ct = default)
        {
            // No Next.js proxy for migrate-guest currently; login auto-migrates guest
            // avatars server-side via guest_player_id, so this is rarely needed.
            throw new NotSupportedException(
                "MigrateGuestAsync is not available — /api/player/migrate-guest " +
                "proxy has not been added. Login already transfers guest avatars " +
                "via guest_player_id on the login request.");
        }

        public static Task<PlayerTokenRefreshResponse> TokenRefreshAsync(
            string baseUrl, string appId, string apiKey, string playerToken, CancellationToken ct = default)
        {
            // No Next.js proxy for token-refresh currently. The 30-day player
            // token is long-lived, so in practice a fresh InitGuestAsync on
            // near-expiry is adequate.
            throw new NotSupportedException(
                "TokenRefreshAsync is not available — /api/player/token-refresh " +
                "proxy has not been added. Call InitGuestAsync again to re-mint " +
                "on the existing device_id.");
        }

        public static async Task<PlayerUsernameCheckResponse> UsernameCheckAsync(
            string baseUrl, string appId, string apiKey, string username, CancellationToken ct = default)
        {
            var customizerToken = await MintCustomizerTokenAsync(baseUrl, appId, apiKey, ct);
            var body = new PlayerUsernameCheckRequest { username = username };
            return await PostAsync<PlayerUsernameCheckRequest, PlayerUsernameCheckResponse>(
                baseUrl, "username-check", body,
                AuthMode.CustomizerBearer, customizerToken, ct);
        }

        // ── Customizer token mint (matches AvaTwinApiClient.MintTokenAsync) ──

        [Serializable]
        private class TokenMintRequest { public string appId; public string apiKey; }
        [Serializable]
        private class TokenMintResponse { public string token; public string error; }

        private static async Task<string> MintCustomizerTokenAsync(
            string baseUrl, string appId, string apiKey, CancellationToken ct)
        {
            var url = TrimSlash(baseUrl) + "/api/token-mint";
            var bodyJson = JsonUtility.ToJson(new TokenMintRequest { appId = appId, apiKey = apiKey });

            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(bodyJson));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Accept", "application/json");
                req.timeout = 15;

                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                }

                var status = req.responseCode;
                var text = req.downloadHandler?.text ?? string.Empty;

#if UNITY_2020_2_OR_NEWER
                var networkFailed = req.result == UnityWebRequest.Result.ConnectionError
                                 || req.result == UnityWebRequest.Result.DataProcessingError;
#else
                var networkFailed = req.isNetworkError;
#endif
                if (networkFailed)
                    throw new Exception($"[Ava-Twin] Network error on token-mint: {req.error}");

                if (status < 200 || status >= 300)
                {
                    var errMsg = TryExtractError(text) ?? req.error ?? "unknown";
                    throw new Exception($"[Ava-Twin] token-mint failed ({status}): {errMsg}");
                }

                TokenMintResponse resp;
                try { resp = JsonUtility.FromJson<TokenMintResponse>(text); }
                catch (Exception ex) { throw new Exception($"[Ava-Twin] token-mint invalid JSON: {ex.Message}"); }

                if (resp == null || string.IsNullOrEmpty(resp.token))
                    throw new Exception($"[Ava-Twin] token-mint returned no token: {resp?.error ?? "empty"}");

                return resp.token;
            }
        }

        // ── Internals ────────────────────────────────────────────────────

        private static async Task<TRes> PostAsync<TReq, TRes>(
            string baseUrl, string path, TReq body,
            AuthMode authMode, string authToken,
            CancellationToken ct)
            where TRes : class
        {
            var json = JsonUtility.ToJson(body);
            return await PostRawAsync<TRes>(baseUrl, path, json, authMode, authToken, ct);
        }

        private static async Task<TRes> PostRawAsync<TRes>(
            string baseUrl, string path, string bodyJson,
            AuthMode authMode, string authToken,
            CancellationToken ct)
            where TRes : class
        {
            var url = TrimSlash(baseUrl) + "/api/player/" + path;

            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(bodyJson ?? "{}"));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Accept", "application/json");

                switch (authMode)
                {
                    case AuthMode.CustomizerBearer:
                        if (string.IsNullOrEmpty(authToken))
                            throw new Exception($"[Ava-Twin] {path} requires a customizer token.");
                        req.SetRequestHeader("Authorization", "Bearer " + authToken);
                        break;
                    case AuthMode.PlayerTokenHeader:
                        if (string.IsNullOrEmpty(authToken))
                            throw new Exception($"[Ava-Twin] {path} requires a player token.");
                        req.SetRequestHeader("x-player-token", authToken);
                        break;
                    case AuthMode.None:
                    default:
                        break;
                }

                req.timeout = 15;

                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                }

                var status = req.responseCode;
                var text = req.downloadHandler?.text ?? string.Empty;

#if UNITY_2020_2_OR_NEWER
                var networkFailed = req.result == UnityWebRequest.Result.ConnectionError
                                 || req.result == UnityWebRequest.Result.DataProcessingError;
#else
                var networkFailed = req.isNetworkError;
#endif

                if (networkFailed)
                    throw new Exception($"[Ava-Twin] Network error on {path}: {req.error}");

                if (status < 200 || status >= 300)
                {
                    var errMsg = TryExtractError(text) ?? req.error ?? "unknown";
                    throw new Exception($"[Ava-Twin] {path} failed ({status}): {errMsg}");
                }

                if (string.IsNullOrEmpty(text))
                    throw new Exception($"[Ava-Twin] {path} returned empty body");

                TRes result;
                try
                {
                    result = JsonUtility.FromJson<TRes>(text);
                }
                catch (Exception ex)
                {
                    throw new Exception($"[Ava-Twin] {path} invalid JSON: {ex.Message}");
                }

                if (result == null)
                    throw new Exception($"[Ava-Twin] {path} deserialized to null");

                return result;
            }
        }

        private static string TryExtractError(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var err = JsonUtility.FromJson<PlayerErrorResponse>(json);
                return err?.error;
            }
            catch { return null; }
        }

        private static string TrimSlash(string s)
            => string.IsNullOrEmpty(s) ? s : s.TrimEnd('/');
    }
}
