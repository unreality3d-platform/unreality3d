using System.Collections;
using UnityEngine;
using U3D.Net;

namespace U3D
{
    /// <summary>
    /// Emotes for one player: a swirl of the emote's sprite around their avatar, below eye level,
    /// with an optional sound, seen the same way by everyone in the room including the player who
    /// sent it.
    ///
    /// Lives on the root of both player prefabs and registers the same message on each. The local
    /// player's copy sends and every copy plays. A send is delivered back to the sender inside the
    /// send call, so the sender's own swirl plays through the same handler as everyone else's,
    /// in a room of one as well.
    ///
    /// A message about this avatar is accepted only from the peer whose block the avatar belongs
    /// to, and at most one per ReceiveInterval, so a sender who skips their own send interval
    /// shows nobody anything extra.
    ///
    /// Each emote gets its own particle system, made the first time that emote plays. A particle
    /// system's sprite is read when its particles are drawn, not when they are emitted, so one
    /// system switching sprites would change a swirl already in the air.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetEntity))]
    public class U3DEmote : NetComponent
    {
        private const float ReceiveInterval = 0.8f;
        private const string SwirlRootName = "Emote Swirl";

        [Tooltip("The emotes this player can send and show. Both player prefabs use the same set.")]
        [SerializeField] private U3DEmoteSet emoteSet;

        private NetMessage _message;
        private float _nextSendAt;
        private float _nextReceiveAt;
        private Transform _swirlRoot;
        private ParticleSystem[] _swirls;
        private AudioSource _audio;

        public U3DEmoteSet EmoteSet => emoteSet;

        void Awake()
        {
            // Registered here rather than at spawn, because the entity checks for duplicate keys
            // at spawn and a key registered afterwards would never be checked.
            _message = RegisterMessage(NetKeys.PlayerEmote, HandleEmote);
        }

        /// <summary>
        /// Sends an emote to everyone in the room, this player included. Returns false, sending
        /// nothing, before the player has joined, on any avatar but the local player's, for an
        /// index with no sprite, and within the set's send interval of the last emote.
        /// </summary>
        public bool Send(int index)
        {
            if (_message == null || emoteSet == null || !IsLive || Session == null) return false;
            if (!IsLocalAvatar()) return false;
            if (emoteSet.GetSprite(index) == null) return false;
            if (Time.unscaledTime < _nextSendAt) return false;

            _nextSendAt = Time.unscaledTime + emoteSet.SendInterval;
            _message.SendToAll((ushort)index);
            return true;
        }

        /// <summary>Plays an emote on this machine only. Nothing is sent.</summary>
        public void Play(int index)
        {
            if (emoteSet == null || emoteSet.SwirlPrefab == null) return;

            Sprite sprite = emoteSet.GetSprite(index);
            if (sprite == null) return;

            EnsureSwirlRoot();

            ParticleSystem swirl = GetSwirl(index, sprite);
            if (swirl != null && isActiveAndEnabled)
                StartCoroutine(EmitSwirl(swirl));

            AudioClip clip = emoteSet.GetSound(index);
            if (clip != null && _audio != null)
                _audio.PlayOneShot(clip);
        }

        private bool IsLocalAvatar()
        {
            PeerId local = Session.LocalPeer;
            return local.IsValid
                && NetEntity.IsAvatarId(Entity.Id)
                && NetEntity.PersonalBlockOf(Entity.Id) == local.BlockIndex;
        }

        private void HandleEmote(PeerId sender, object[] args)
        {
            if (args == null || args.Length < 1 || !(args[0] is ushort index)) return;
            if (!sender.IsValid || Entity == null || !NetEntity.IsAvatarId(Entity.Id)) return;
            if (NetEntity.PersonalBlockOf(Entity.Id) != sender.BlockIndex) return;
            if (Time.unscaledTime < _nextReceiveAt) return;

            _nextReceiveAt = Time.unscaledTime + ReceiveInterval;
            Play(index);
        }

        private void EnsureSwirlRoot()
        {
            if (_swirlRoot != null) return;

            GameObject root = new GameObject(SwirlRootName);
            root.layer = gameObject.layer;
            _swirlRoot = root.transform;
            _swirlRoot.SetParent(transform, false);
            _swirlRoot.localPosition = new Vector3(0f, emoteSet.EmitHeight, 0f);

            _audio = root.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.spatialBlend = 1f;
            _audio.rolloffMode = AudioRolloffMode.Linear;
            _audio.minDistance = 1f;
            _audio.maxDistance = 15f;
            _audio.dopplerLevel = 0f;
            _audio.outputAudioMixerGroup = emoteSet.Output;

            _swirls = new ParticleSystem[emoteSet.Count];
        }

        private ParticleSystem GetSwirl(int index, Sprite sprite)
        {
            if (index < 0 || index >= _swirls.Length) return null;
            if (_swirls[index] != null) return _swirls[index];

            ParticleSystem swirl = Instantiate(emoteSet.SwirlPrefab, _swirlRoot, false);
            swirl.name = "Swirl " + index;
            swirl.gameObject.layer = gameObject.layer;

            ParticleSystem.TextureSheetAnimationModule sheet = swirl.textureSheetAnimation;
            if (sheet.spriteCount > 0) sheet.SetSprite(0, sprite);
            else sheet.AddSprite(sprite);

            _swirls[index] = swirl;
            return swirl;
        }

        private IEnumerator EmitSwirl(ParticleSystem swirl)
        {
            int count = emoteSet.ParticlesPerEmote;
            float radius = emoteSet.RingRadius;
            float step = count > 1 ? emoteSet.EmitDuration / (count - 1) : 0f;
            float arc = Mathf.PI * 2f / count;
            float angle = UnityEngine.Random.value * Mathf.PI * 2f;
            var emit = new ParticleSystem.EmitParams();

            for (int i = 0; i < count; i++)
            {
                if (swirl == null) yield break;

                emit.position = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                swirl.Emit(emit, 1);
                angle += arc;

                if (step > 0f && i < count - 1)
                    yield return new WaitForSeconds(step);
            }
        }
    }
}