using System;
using UnityEngine;
using UnityEngine.Audio;

namespace U3D
{
    /// <summary>
    /// The emotes players can send, and how their swirl looks and sounds. Both player prefabs'
    /// U3DEmote components use the same set, so a sender and every viewer agree on what each
    /// index shows. An emote's position in the list is what travels between players, so entries
    /// are never reordered or removed: replace an emote's sprite, or add new emotes at the end.
    /// </summary>
    public class U3DEmoteSet : ScriptableObject
    {
        [Serializable]
        public class Emote
        {
            public string displayName;
            [Tooltip("All emote sprites must come from one texture.")]
            public Sprite sprite;
            [Tooltip("Optional. Leave empty to use the set's Default Sound.")]
            public AudioClip sound;
        }

        [Tooltip("Emotes in index order. Never reorder or remove entries; add new ones at the end.")]
        [SerializeField] private Emote[] emotes = new Emote[0];

        [Header("Sound")]
        [SerializeField] private AudioClip defaultSound;
        [Tooltip("Mixer group emote sounds play through.")]
        [SerializeField] private AudioMixerGroup output;

        [Header("Swirl")]
        [Tooltip("Particle system played once per emote, around the player. Its sprite is replaced by the emote's.")]
        [SerializeField] private ParticleSystem swirlPrefab;
        [Tooltip("Radius of the ring the swirl starts on, in metres.")]
        [Min(0f)][SerializeField] private float ringRadius = 0.7f;
        [Tooltip("Height of that ring above the player's feet, in metres.")]
        [Min(0f)][SerializeField] private float emitHeight = 0.7f;
        [Min(1)][SerializeField] private int particlesPerEmote = 8;
        [Tooltip("Time over which one emote's particles are released, in seconds, so they spiral rather than appear at once.")]
        [Min(0f)][SerializeField] private float emitDuration = 0.4f;

        [Header("Sending")]
        [Tooltip("Shortest time between two emotes from one player, in seconds.")]
        [Min(0.1f)][SerializeField] private float sendInterval = 1f;

        public int Count => emotes != null ? emotes.Length : 0;
        public AudioMixerGroup Output => output;
        public ParticleSystem SwirlPrefab => swirlPrefab;
        public float RingRadius => ringRadius;
        public float EmitHeight => emitHeight;
        public int ParticlesPerEmote => particlesPerEmote;
        public float EmitDuration => emitDuration;
        public float SendInterval => sendInterval;

        public Sprite GetSprite(int index)
        {
            Emote emote = Get(index);
            return emote != null ? emote.sprite : null;
        }

        public string GetName(int index)
        {
            Emote emote = Get(index);
            return emote != null ? emote.displayName : string.Empty;
        }

        public AudioClip GetSound(int index)
        {
            Emote emote = Get(index);
            if (emote != null && emote.sound != null) return emote.sound;
            return defaultSound;
        }

        private Emote Get(int index)
        {
            if (emotes == null || index < 0 || index >= emotes.Length) return null;
            return emotes[index];
        }
    }
}