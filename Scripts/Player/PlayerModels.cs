using System;

namespace AvaTwin
{
    // ─────────────────────────────────────────────────────────────────────
    // Request/response DTOs for the Ava-Twin player identity edge functions.
    //
    // All classes use the SDK's existing pattern: [Serializable] + public
    // fields, compatible with UnityEngine.JsonUtility. Field names match
    // the edge-function JSON shape exactly (snake_case where the server
    // uses it, camelCase otherwise).
    //
    // Reference: /Users/waqashaider/ZedStack/ava-twin/supabase/functions/player-*
    // ─────────────────────────────────────────────────────────────────────

    // ── Shared: variation_selections + avatar payload ────────────────────

    [Serializable]
    public class VariationSelections
    {
        public string gender;      // "male" | "female" | "generic"
        public string head;        // e.g. "h1", "h2"
        public string top;         // e.g. "t1"
        public string bottom;      // e.g. "b1"
        public string shoes;       // e.g. "s1"
        public string skin_tone;   // "#RRGGBB" — optional, nullable
    }

    [Serializable]
    public class PlayerAvatarRecord
    {
        public string id;                  // player_avatars row UUID
        public string avatar_id;           // public_combo_id (nullable)
        public VariationSelections variation_selections;
        public string created_at;          // ISO timestamp string
    }

    [Serializable]
    public class PlayerPublicInfo
    {
        public string username;            // null for guests
    }

    // ── player-guest-init ────────────────────────────────────────────────

    [Serializable]
    public class PlayerGuestInitRequest
    {
        public string device_id;
    }

    [Serializable]
    public class PlayerGuestInitResponse
    {
        public string player_id;
        public string token;
        public long expires_in;    // seconds (server always returns 2592000 = 30 days)
        public bool is_guest;
        public string username;    // null for guests
    }

    // ── player-avatar-save ───────────────────────────────────────────────

    [Serializable]
    public class PlayerAvatarSaveRequest
    {
        public VariationSelections variation_selections;
    }

    [Serializable]
    public class PlayerAvatarSaveResponse
    {
        public bool success;
        public string avatar_id;   // public_combo_id
        public string db_id;       // player_avatars row UUID
        public string player_id;
    }

    // ── player-avatar-get ────────────────────────────────────────────────

    [Serializable]
    public class PlayerAvatarGetServerRequest
    {
        public string player_id;   // only used in server-key mode
    }

    [Serializable]
    public class PlayerAvatarGetResponse
    {
        public PlayerAvatarRecord avatar;   // null when none active
        public PlayerPublicInfo player;     // null when player not found
    }

    // ── player-register ──────────────────────────────────────────────────

    [Serializable]
    public class PlayerRegisterRequest
    {
        public string username;    // regex ^[a-zA-Z0-9_]{3,20}$
        public string password;    // min 8, 1 upper, 1 lower, 1 digit, 1 symbol
    }

    [Serializable]
    public class PlayerAuthResponse
    {
        public string player_id;
        public string token;
        public long expires_in;
        public string username;
        public bool is_guest;      // always false for register/login
    }

    // ── player-login ─────────────────────────────────────────────────────

    [Serializable]
    public class PlayerLoginRequest
    {
        public string username;
        public string password;
        public string guest_player_id;   // optional — triggers avatar transfer
    }

    // ── player-migrate-guest ─────────────────────────────────────────────

    [Serializable]
    public class PlayerMigrateGuestRequest
    {
        public string device_id;
    }

    [Serializable]
    public class PlayerMigrateGuestResponse
    {
        public bool migrated;
        public string message;          // set when migrated=false
        // avatar_config is a JSON blob; we don't deserialize it here since
        // shape varies. Expose raw string if needed later.
    }

    // ── player-token-refresh ─────────────────────────────────────────────

    [Serializable]
    public class PlayerTokenRefreshResponse
    {
        public string token;
        public long expiresIn;          // server returns camelCase here (per audit)
    }

    // ── player-username-check ────────────────────────────────────────────

    [Serializable]
    public class PlayerUsernameCheckRequest
    {
        public string username;
    }

    [Serializable]
    public class PlayerUsernameCheckResponse
    {
        public bool available;
        public string reason;           // set when available=false
    }

    // ── Shared error response ────────────────────────────────────────────

    [Serializable]
    internal class PlayerErrorResponse
    {
        public string error;
    }
}
