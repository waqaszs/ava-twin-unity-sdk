using UnityEngine;

namespace AvaTwin
{
    // ─────────────────────────────────────────────────────────────────────
    // Shared editor-quota helper used by every /api/token-mint caller.
    //
    // The token-mint edge function attaches an `editor_quota` object to its
    // response when the request is sent with `mode=editor`. This file is the
    // single source of truth for:
    //   1. The DTO shape that all callers parse into (AvaTwin.EditorQuota)
    //   2. The Debug.Log emitted on every Editor-mode mint
    //
    // The DTO itself lives in PlayerModels.cs (also under namespace AvaTwin)
    // so that JsonUtility-based callers can deserialize it without pulling
    // additional references; see EditorQuota class there. This file owns the
    // logging contract so every code path produces an identical console line.
    //
    // The whole logging method body is wrapped in #if UNITY_EDITOR so the
    // string formatting is compile-stripped from shipped Player builds —
    // production binaries never contain the "Editor mode quota:" literal.
    // ─────────────────────────────────────────────────────────────────────

    // public (not internal) so the AvaTwin.Editor assembly (which lives in a
    // separate asmdef) can also call into it from AvaTwinWelcomeWindow.
    public static class EditorQuotaLogger
    {
        /// <summary>
        /// Emit the standard "[Ava-Twin] Editor mode quota: X/Y used today" log
        /// line if the response carries a populated editor_quota object.
        ///
        /// Safe to call unconditionally on any token-mint response: when the
        /// server omits the field (embed/playground mints) JsonUtility yields
        /// a default-initialized struct with plan="" which we treat as absent.
        /// </summary>
        public static void LogIfPresent(EditorQuota eq)
        {
#if UNITY_EDITOR
            if (eq == null || string.IsNullOrEmpty(eq.plan)) return;
            if (eq.limit == -1)
            {
                Debug.Log($"[Ava-Twin] Editor mode quota: unlimited ({eq.plan} plan)");
            }
            else
            {
                Debug.Log(
                    $"[Ava-Twin] Editor mode quota: {eq.used}/{eq.limit} used today " +
                    $"({eq.remaining} remaining, {eq.plan} plan)");
            }
#endif
        }
    }
}
