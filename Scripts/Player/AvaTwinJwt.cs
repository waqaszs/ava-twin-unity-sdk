using System;
using System.Text;

namespace AvaTwin
{
    /// <summary>
    /// JWT claim reader (read-only). The server signs and verifies with
    /// HS256 + PLAYER_JWT_SECRET; the SDK does NOT verify signatures (we
    /// don't have the secret). We only need to read claims for expiry
    /// checks and to get the player_id.
    /// </summary>
    internal static class AvaTwinJwt
    {
        /// <summary>
        /// Parsed claims we care about.
        /// </summary>
        public struct Claims
        {
            public string Sub;          // player_id
            public string AppId;        // app_id claim
            public string Audience;     // "ava-player"
            public long ExpUnix;        // exp claim (seconds since epoch)
            public bool IsValidShape;   // true if required fields parsed
        }

        /// <summary>
        /// Decodes claims from a JWT. Does NOT verify signature.
        /// Returns a default-filled <see cref="Claims"/> with IsValidShape=false
        /// on any parse failure.
        /// </summary>
        public static Claims DecodeClaims(string jwt)
        {
            var result = new Claims();
            if (string.IsNullOrEmpty(jwt)) return result;

            var parts = jwt.Split('.');
            if (parts.Length < 2) return result;

            string payloadJson;
            try
            {
                var payloadBytes = Base64UrlDecode(parts[1]);
                payloadJson = Encoding.UTF8.GetString(payloadBytes);
            }
            catch
            {
                return result;
            }

            try
            {
                // Use Unity's JsonUtility via a tiny DTO — matches the SDK's
                // existing pattern of [Serializable] fields + JsonUtility.
                var dto = UnityEngine.JsonUtility.FromJson<JwtPayload>(payloadJson);
                if (dto == null) return result;

                result.Sub = dto.sub;
                result.AppId = dto.app_id;
                result.Audience = dto.aud;
                result.ExpUnix = dto.exp;
                result.IsValidShape = !string.IsNullOrEmpty(dto.sub) && dto.exp > 0;
            }
            catch
            {
                // leave IsValidShape=false
            }

            return result;
        }

        /// <summary>
        /// True when claims parsed and exp is in the future.
        /// <paramref name="skewSeconds"/> treats tokens within N seconds of
        /// expiring as already expired (default 60s to avoid race with clock).
        /// </summary>
        public static bool IsUnexpired(Claims claims, long skewSeconds = 60)
        {
            if (!claims.IsValidShape) return false;
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return claims.ExpUnix - skewSeconds > nowUnix;
        }

        // ── internals ────────────────────────────────────────────────────

        private static byte[] Base64UrlDecode(string s)
        {
            // JWT uses base64url (URL-safe alphabet, no padding).
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 0: break;
                default: throw new FormatException("Bad base64url length");
            }
            return Convert.FromBase64String(s);
        }

        [Serializable]
        private class JwtPayload
        {
            public string sub;
            public string aud;
            public string iss;
            public long iat;
            public long exp;
            public string app_id;
        }
    }
}
