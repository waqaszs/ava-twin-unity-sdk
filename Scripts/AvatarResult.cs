using UnityEngine;

namespace AvaTwin
{
    /// <summary>
    /// Result of loading an Ava-Twin avatar. Contains everything needed to wire into a game.
    /// </summary>
    public sealed class AvatarResult
    {
        /// <summary>The instantiated root GameObject of the avatar.</summary>
        public GameObject Root { get; }

        /// <summary>The avatar ID used for persistence and network sync.</summary>
        public string AvatarId { get; }

        /// <summary>The skin tone hex color applied to the avatar.</summary>
        public string SkinToneHex { get; }

        /// <summary>Returns the Unity Humanoid Avatar for Animator configuration.</summary>
        public Avatar GetUnityHumanoidAvatar()
        {
            if (Root == null) return null;
            var animator = Root.GetComponentInChildren<Animator>();
            return animator != null ? animator.avatar : null;
        }

        public AvatarResult(GameObject root, string avatarId, string skinToneHex)
        {
            Root = root;
            AvatarId = avatarId;
            SkinToneHex = skinToneHex;
        }
    }
}
