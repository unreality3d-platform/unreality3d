using System.Collections.Generic;
using UnityEngine;
using U3D.Net;

namespace U3D
{
    /// <summary>
    /// Per-player cosmetic attachment manager. Sits on both player prefabs — the local one
    /// alongside U3DPlayerController, and the remote avatar alongside U3DAvatarManager.
    /// Holds the set of attachment sources this player is wearing and rebuilds the cosmetic
    /// visuals from it.
    ///
    /// The accessory is a plain prefab instantiated locally and parented to the avatar's bones, so it
    /// rides the player's position and animation with no separate handling. The only state is which
    /// sources are worn; the visuals are always rebuilt from that set rather than tracked separately.
    ///
    /// A source carries ONE prefab. The pieces of that prefab are defined by its attachment-point
    /// markers: each U3DAttachmentPoint marks one piece — the piece is the object the marker is
    /// parented to. A single accessory (a helmet) has one marker on the prefab root, so the whole
    /// prefab is one piece on one bone. A costume has a marker inside each child (hat, glove, glove),
    /// so each child rides its own bone and the now-empty container root is discarded. A child with
    /// no marker is not a piece — it's ignored. The whole source still occupies ONE entry in the worn
    /// list, so its pieces share one on/off state and can't drift apart.
    ///
    /// Toggle is driven from U3DAttachmentSource.OnInteract: interacting once adds the source to the
    /// list (worn), interacting again removes it (taken off). A source with a non-empty Slot replaces
    /// anything worn from another source sharing that slot; unslotted sources stack freely.
    ///
    /// Visibility follows the avatar's own rules: each built instance's renderers are registered with
    /// U3DAvatarManager, which toggles them in lockstep with the body — no special-casing.
    ///
    /// What travels, and what does not. The worn set reaches everyone else as eight station IDs on
    /// tag 21 — the stations' own authored numbers, so every machine looks up its own copy of the
    /// station and builds from that. Nothing about an accessory travels. The whole set is sent every
    /// time it changes and again to each player as they become reachable, so a message lost, doubled
    /// or arriving late still leaves every machine holding what the wearer holds.
    ///
    /// Only the local player's copy sends. A remote copy receives, writes the set, and lets the same
    /// build loop below dress the body — it never runs Wear, which would have this machine deciding a
    /// question the wearer already answered, and would fire the station's creator events once per
    /// player in the room.
    /// </summary>
    [RequireComponent(typeof(U3DAvatarManager))]
    // U3DAvatarManager is a plain MonoBehaviour and declares nothing, so the requirement above
    // does not reach NetEntity transitively. F40.
    [RequireComponent(typeof(NetEntity))]
    public class U3DPlayerAttachments : NetComponent
    {
        // Maximum simultaneous sources per player. Each source may itself be a multi-piece costume,
        // so this caps sources, not individual accessory pieces.
        //
        // This is a wire constant. Tag 21 carries exactly this many slots, and a published build
        // names that layout permanently. Wanting more attachments later is a new tag, not an edit to
        // this number.
        public const int MAX_ATTACHMENTS = 8;

        private readonly List<U3DAttachmentSource> _worn = new List<U3DAttachmentSource>();

        // Per-source record. One source carries one prefab whose markers define its pieces, so a
        // source builds N local piece instances but stays ONE entry in the worn list. Renderers are
        // aggregated across the pieces so the whole costume registers with the avatar's visibility
        // rules as a single unit. HeadRenderers is the subset riding the head bone (or a socket under
        // it); the avatar manager renders those shadow-only for the wearer in VR first person so a
        // face-covering piece can't blind them.
        private struct Built
        {
            public U3DAttachmentSource Source;
            public GameObject[] Instances;
            public Renderer[] Renderers;
            public Renderer[] HeadRenderers;
        }

        private readonly List<Built> _built = new List<Built>();
        private U3DAvatarManager _avatarManager;
        private NetMessage _wornMessage;
        private readonly object[] _sendScratch = new object[MAX_ATTACHMENTS];

        private void Awake()
        {
            _avatarManager = GetComponent<U3DAvatarManager>();

            // Registered here rather than in a spawn callback because NetEntity walks its
            // component list for duplicate keys at spawn, and a key registered after that
            // walk would never be checked.
            _wornMessage = RegisterMessage(NetKeys.AttachmentsWorn, ReceiveWorn);

            if (Session != null)
                Session.PeerReachable += HandlePeerReachable;
        }

        /// <summary>
        /// Whether this copy of the component is the one its player is actually wearing things on.
        /// The remote avatars in the room carry the same component, and a send from one of those
        /// would tell the room that somebody else's body had changed clothes.
        /// </summary>
        private bool IsLocalWearer
        {
            get
            {
                if (Session == null || Entity == null) return false;
                return Entity.Owner == Session.LocalPeer;
            }
        }

        /// <summary>
        /// Toggles the given source on this player. Interacting with a station whose item is
        /// already worn takes it off (firing its On Remove); otherwise the item goes on (firing
        /// its On Wear). If the source has a non-empty Slot, anything worn from another station
        /// with the same slot is removed first — same-role items replace each other, while
        /// unslotted items stack freely. Removals preserve the order of what remains, so
        /// RemoveLast still takes items off newest-first. At capacity, additions are refused
        /// silently and no event fires.
        /// </summary>
        public void Wear(U3DAttachmentSource source)
        {
            if (source == null) return;

            // Toggle off: the wardrobe-stand behavior. Interacting again with something
            // you're wearing takes it off.
            if (_worn.Contains(source))
            {
                _worn.Remove(source);
                source.InvokeOnRemove();
                PublishWorn();
                return;
            }

            // Slot replacement: remove anything worn that shares this source's non-empty slot.
            // Walked by index from the end so removals don't disturb unvisited entries; removes
            // every match, which also converges any duplicates worn before slot rules existed.
            string slot = NormalizeSlot(source.Slot);
            if (slot != null)
            {
                for (int i = _worn.Count - 1; i >= 0; i--)
                {
                    U3DAttachmentSource worn = _worn[i];
                    if (worn == null) continue;
                    if (NormalizeSlot(worn.Slot) != slot) continue;

                    _worn.RemoveAt(i);
                    worn.InvokeOnRemove();
                }
            }

            if (_worn.Count < MAX_ATTACHMENTS)
            {
                _worn.Add(source);
                source.InvokeOnWear();
            }

            // Published once at the end rather than per removal above, because the whole set
            // travels and an intermediate state is not one anybody needs to see.
            PublishWorn();
        }

        /// <summary>
        /// Takes off the most recently worn source. Reads the last entry in the worn list (kept in
        /// wear order, newest last) and removes it, firing that source's On Remove; the next call
        /// takes off the new last entry, and so on, like undo. No-op when nothing is worn. Works
        /// from anywhere — it does not require returning to a source.
        /// </summary>
        public void RemoveLast()
        {
            if (_worn.Count == 0) return;

            U3DAttachmentSource last = _worn[_worn.Count - 1];
            _worn.RemoveAt(_worn.Count - 1);

            if (last != null)
                last.InvokeOnRemove();

            PublishWorn();
        }

        /// <summary>
        /// Tells everyone what this player is wearing. Eight station IDs in wear order, newest
        /// last, zero for an empty slot — the whole set every time rather than what changed, so a
        /// message that arrives late or twice still leaves the receiver holding what we hold.
        ///
        /// Every value is cast to ushort explicitly. Arithmetic on a ushort in C# produces an int,
        /// which the codec's field test refuses, and the refusal is a console error a deployed
        /// build has nowhere to show.
        /// </summary>
        private void PublishWorn()
        {
            if (_wornMessage == null || !IsLocalWearer) return;

            FillSlots(_sendScratch);
            _wornMessage.SendToAll(_sendScratch);
        }

        /// <summary>
        /// Sends the current set to one player who has just become reachable.
        ///
        /// Needed because a set that changed while that player was in the room but not yet
        /// connected reached nobody — the send happened and the transport had nowhere to put it.
        /// Without this the newcomer sees this player wrong until their next wardrobe action,
        /// which may be minutes and looks like nothing at all.
        /// </summary>
        private void HandlePeerReachable(PeerId peer)
        {
            if (_wornMessage == null || !IsLocalWearer) return;

            FillSlots(_sendScratch);
            _wornMessage.SendTo(peer, _sendScratch);
        }

        /// <summary>
        /// Writes the worn set into a payload array: each source's station ID in wear order,
        /// zero-filled to the full eight slots. A source whose station carries no ID sends zero,
        /// which the receiver reads as an empty slot — the same outcome as it being unresolvable
        /// there, and silent either way per the discard rule.
        /// </summary>
        private void FillSlots(object[] slots)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                ushort id = 0;

                if (i < _worn.Count && _worn[i] != null)
                {
                    NetEntity entity = _worn[i].GetComponent<NetEntity>();
                    if (entity != null) id = entity.Id;
                }

                slots[i] = id;
            }
        }

        /// <summary>
        /// Applies a worn set that arrived from the wire.
        ///
        /// Refused unless the sender is the player this body belongs to. Every receiving peer
        /// enforces that for itself rather than trusting the claim, which is the same posture the
        /// authority rules take.
        ///
        /// Writes the set directly and fires nothing. Running Wear here would re-run slot
        /// replacement the wearer's own machine already settled, and would fire the station's On
        /// Wear and On Remove events — which live on the station, one shared scene object, so a
        /// creator's wardrobe sound would play once per player in the room on every machine.
        ///
        /// A station ID that does not resolve is dropped silently: the object is missing from this
        /// build, which no announcement here would repair.
        /// </summary>
        private void ReceiveWorn(PeerId sender, object[] args)
        {
            if (args == null) return;
            if (Entity == null || sender != Entity.Owner) return;
            if (Session == null) return;

            _worn.Clear();

            for (int i = 0; i < args.Length && i < MAX_ATTACHMENTS; i++)
            {
                if (!(args[i] is ushort id) || id == 0) continue;

                if (!Session.TryGetEntity(id, out NetEntity entity) || entity == null) continue;

                U3DAttachmentSource source = entity.GetComponent<U3DAttachmentSource>();
                if (source == null) continue;

                _worn.Add(source);
            }
        }

        /// <summary>
        /// Canonical form of a slot name for comparison: null when the slot is empty or
        /// whitespace (meaning "stacks with everything"), otherwise trimmed and lowercased so
        /// creator typing differences ("Head" vs "head ") don't split a slot in two.
        /// </summary>
        private static string NormalizeSlot(string slot)
        {
            if (string.IsNullOrWhiteSpace(slot)) return null;
            return slot.Trim().ToLowerInvariant();
        }

        // Runs after the Animator has posed the skeleton, because the alignment below reads live
        // bone rotations. Update would read the previous frame's pose.
        private void LateUpdate()
        {
            ReconcileAttachments();
        }

        /// <summary>
        /// Brings the built cosmetic instances in line with the worn set. Destroys instances whose
        /// source left the set, builds instances for sources newly in it. A source whose pieces can't
        /// be built yet (avatar still initializing) is simply retried next frame — nothing is
        /// instantiated until the rig is ready, so there is no churn and no partial costume.
        ///
        /// That retry is also what makes a received set safe to write at any moment: a remote body's
        /// avatar is built when its first packet lands, which can be after the worn set arrives, and
        /// the set simply waits here until there is a rig to hang it on.
        ///
        /// A worn entry whose source object has been destroyed is dropped from the set here, which is
        /// the only way an entry can become invalid now that entries are direct references.
        /// </summary>
        private void ReconcileAttachments()
        {
            for (int i = _worn.Count - 1; i >= 0; i--)
                if (_worn[i] == null)
                    _worn.RemoveAt(i);

            for (int i = _built.Count - 1; i >= 0; i--)
            {
                if (!_worn.Contains(_built[i].Source))
                {
                    DestroyBuilt(_built[i]);
                    _built.RemoveAt(i);
                }
            }

            for (int i = 0; i < _worn.Count; i++)
            {
                U3DAttachmentSource source = _worn[i];
                if (IsBuilt(source)) continue;

                if (TryBuildAttachment(source, out Built built))
                    _built.Add(built);
            }
        }

        private bool IsBuilt(U3DAttachmentSource source)
        {
            for (int i = 0; i < _built.Count; i++)
                if (_built[i].Source == source) return true;
            return false;
        }

        /// <summary>
        /// Builds a source's accessory. The source carries ONE prefab whose attachment-point markers
        /// define its pieces: each marker marks one piece — the piece is the object the marker is
        /// parented to. A single accessory has one marker on the prefab root (the whole prefab is one
        /// piece); a costume has a marker inside each child (each child is a piece). Children with no
        /// marker are ignored.
        ///
        /// Two failure modes are kept apart on purpose. While the avatar's rig is still initializing,
        /// the build waits — it instantiates nothing and returns false so the caller retries next
        /// frame, so a costume never appears half-on during load. Once the rig is ready, any marker
        /// that still can't resolve its bone (a bone the rig lacks, an unset bone, a missing override
        /// name) is skipped for good; the pieces that resolve are built and the build is committed so
        /// it never retries forever. A prefab with no markers, and a source with no prefab at all,
        /// both commit an empty build for the same reason.
        ///
        /// Pieces whose bone is the head bone or anything under it are additionally collected as head
        /// renderers and registered separately: the avatar manager renders them shadow-only for the
        /// wearer in VR first person, so a face-covering piece never blocks their own view while
        /// everyone else still sees it.
        /// </summary>
        private bool TryBuildAttachment(U3DAttachmentSource source, out Built built)
        {
            built = default;

            if (source == null) return false;

            GameObject prefab = source.AccessoryPrefab;

            // No prefab assigned → there is nothing to build and nothing to wait for. Commit an
            // empty build so an unconfigured source doesn't re-enter this method every frame.
            if (prefab == null)
            {
                built = EmptyBuild(source);
                return true;
            }

            // Readiness gate, first half (transient). The avatar must be instantiated. Checked before
            // reading the prefab's markers so the ordinary loading wait allocates nothing.
            GameObject avatarInstance = _avatarManager != null ? _avatarManager.GetAvatarInstance() : null;
            if (avatarInstance == null) return false;

            // Markers are static in the prefab, so read them from the asset without instantiating.
            U3DAttachmentPoint[] prefabMarkers = prefab.GetComponentsInChildren<U3DAttachmentPoint>(true);

            // No markers anywhere → nothing is a piece. Commit an empty build so we don't retry
            // forever waiting for pieces that will never come.
            if (prefabMarkers.Length == 0)
            {
                built = EmptyBuild(source);
                return true;
            }

            Animator animator = _avatarManager.GetAvatarAnimator();

            // Readiness gate, second half (transient). If any marker targets a Humanoid bone (no name
            // override), the rig must also be reporting humanoid — otherwise it may still be
            // initializing. Until ready, build nothing and retry.
            // PORT: a genuinely non-humanoid avatar never reports humanoid, so a humanoid-only
            // accessory keeps retrying on it and re-reads the marker array every frame. Harmless in
            // frame cost, but it is an allocation with no end condition. Resolving it needs per-source
            // cached marker data, which is a shape change rather than a port. B31
            bool anyHumanoidMarker = false;
            for (int i = 0; i < prefabMarkers.Length; i++)
            {
                U3DAttachmentPoint m = prefabMarkers[i];
                if (string.IsNullOrEmpty(m.BoneNameOverride) && m.TargetBone != HumanBodyBones.LastBone)
                {
                    anyHumanoidMarker = true;
                    break;
                }
            }
            if (anyHumanoidMarker && (animator == null || !animator.isHuman)) return false;

            // Rig is ready. Resolve each marker's bone. A null result now is permanent (a bone the
            // rig lacks, an unset bone, a missing override name) — that piece is skipped silently.
            var resolvedBones = new Transform[prefabMarkers.Length];
            var resolvedByRole = new bool[prefabMarkers.Length];
            bool anyResolved = false;
            for (int i = 0; i < prefabMarkers.Length; i++)
            {
                Transform bone = ResolveBone(prefabMarkers[i], out resolvedByRole[i]);
                resolvedBones[i] = bone;
                if (bone != null) anyResolved = true;
            }

            // Every marker failed to resolve — nothing to attach. Commit empty so we don't loop.
            if (!anyResolved)
            {
                built = EmptyBuild(source);
                return true;
            }

            // At least one piece resolves. Instantiate the prefab and map each prefab marker to its
            // counterpart on the instance. GetComponentsInChildren walks an identical hierarchy in
            // the same order, so instance marker i matches prefab marker i.
            GameObject instance = Instantiate(prefab);
            Transform instRoot = instance.transform;
            U3DAttachmentPoint[] instMarkers = instance.GetComponentsInChildren<U3DAttachmentPoint>(true);

            // Defensive: an order/count mismatch should never happen for an identical hierarchy, but
            // if it did, attaching to the wrong bones would be worse than nothing. Commit empty.
            if (instMarkers.Length != prefabMarkers.Length)
            {
                Destroy(instance);
                built = EmptyBuild(source);
                return true;
            }

            // Fallback reference when no neutral-pose data exists for a bone: the avatar's current
            // facing, read once (the pre-neutral-capture behavior).
            Quaternion avatarFacing = avatarInstance.transform.rotation;

            // Reference for the head-piece check below. Null on a non-humanoid rig — override-socket
            // pieces on such rigs can't be classified, so none count as head pieces there.
            Transform headBone = (animator != null && animator.isHuman)
                ? animator.GetBoneTransform(HumanBodyBones.Head)
                : null;

            var handledPieces = new HashSet<Transform>();
            var instances = new List<GameObject>();
            var allRenderers = new List<Renderer>();
            var headRenderers = new List<Renderer>();
            bool rootIsPiece = false;

            for (int i = 0; i < instMarkers.Length; i++)
            {
                Transform bone = resolvedBones[i];
                if (bone == null) continue; // skipped marker

                U3DAttachmentPoint marker = instMarkers[i];
                Transform piece = marker.transform.parent;
                if (piece == null) continue; // marker with no parent — not a valid piece

                // Two markers under one piece would otherwise reparent and re-align it twice; the
                // first marker wins, later ones for the same piece are ignored.
                if (handledPieces.Contains(piece)) continue;
                handledPieces.Add(piece);

                if (piece == instRoot) rootIsPiece = true;

                Transform m = marker.transform;

                // Parent with worldPositionStays = false so the piece inherits the bone's scale (a
                // hat on a scaled-up avatar scales with it). Then align: orientation from the
                // neutral-stance frame, position from the bone.
                piece.SetParent(bone, false);

                // Orient against the bone's NEUTRAL-STANCE frame, not its animated rotation at
                // this instant. bone.rotation * Inverse(neutralRel) is the world frame in which
                // the marker's authored forward faces the avatar's forward whenever the bone is
                // in the neutral pose — so the baked local orientation is a constant, and wearing
                // the accessory mid-fly, mid-jump, or mid-swing lands identically to wearing it
                // at idle. Never the bone's own axes, which point in arbitrary per-rig directions.
                // Falls back to the avatar's current facing when no neutral data exists (override
                // sockets, non-humanoid rigs, capture failure) — the previous behavior.
                Quaternion targetMarkerWorld = avatarFacing;
                if (resolvedByRole[i]
                    && _avatarManager.TryGetNeutralBoneRotation(prefabMarkers[i].TargetBone, out Quaternion neutralRel))
                {
                    targetMarkerWorld = bone.rotation * Quaternion.Inverse(neutralRel);
                }

                piece.rotation = (targetMarkerWorld * Quaternion.Inverse(m.rotation)) * piece.rotation;

                // Position read after the rotation, because rotating the piece carries its marker
                // with it.
                piece.position += bone.position - m.position;

                instances.Add(piece.gameObject);

                Renderer[] pieceRenderers = piece.GetComponentsInChildren<Renderer>(true);
                allRenderers.AddRange(pieceRenderers);

                // IsChildOf is true for the head bone itself and anything nested under it, so this
                // covers pieces on the head and on custom sockets inside the head.
                if (headBone != null && bone.IsChildOf(headBone))
                    headRenderers.AddRange(pieceRenderers);
            }

            // If the prefab root was itself a piece (a single accessory, marker on the root), it has
            // been reparented onto a bone and is in our instance list — keep it. Otherwise the root is
            // just an empty container now that its child pieces have been handed to their bones, so
            // discard it; this also drops any unmarked children, which are not pieces.
            if (!rootIsPiece)
                Destroy(instance);

            Renderer[] renderers = allRenderers.ToArray();
            Renderer[] headRendererArray = headRenderers.ToArray();
            if (_avatarManager != null)
            {
                _avatarManager.RegisterAttachmentRenderers(renderers);
                if (headRendererArray.Length > 0)
                    _avatarManager.RegisterHeadAttachmentRenderers(headRendererArray);
            }

            built = new Built
            {
                Source = source,
                Instances = instances.ToArray(),
                Renderers = renderers,
                HeadRenderers = headRendererArray
            };
            return true;
        }

        /// <summary>
        /// A committed build that produced nothing. Returned for every permanent dead end so the
        /// source is recorded as handled and never re-enters the build path.
        /// </summary>
        private static Built EmptyBuild(U3DAttachmentSource source)
        {
            return new Built
            {
                Source = source,
                Instances = new GameObject[0],
                Renderers = new Renderer[0],
                HeadRenderers = new Renderer[0]
            };
        }

        /// <summary>
        /// Resolves the target bone on this player's equipped avatar. Honors an optional exact bone-
        /// name override first (for non-humanoid rigs or custom sockets), then the Humanoid role.
        /// Returns null when the avatar isn't ready, isn't humanoid, or the rig has no such bone.
        /// resolvedByRole reports whether the Humanoid role produced the result — only role-resolved
        /// bones have neutral-pose data, so override-named sockets keep the pose-dependent alignment.
        /// </summary>
        private Transform ResolveBone(U3DAttachmentPoint marker, out bool resolvedByRole)
        {
            resolvedByRole = false;
            if (_avatarManager == null) return null;

            if (!string.IsNullOrEmpty(marker.BoneNameOverride))
            {
                GameObject avatarInstance = _avatarManager.GetAvatarInstance();
                if (avatarInstance != null)
                {
                    Transform[] all = avatarInstance.GetComponentsInChildren<Transform>(true);
                    for (int i = 0; i < all.Length; i++)
                        if (all[i].name == marker.BoneNameOverride)
                            return all[i];
                }
            }

            Animator animator = _avatarManager.GetAvatarAnimator();
            if (animator == null || !animator.isHuman) return null;
            if (marker.TargetBone == HumanBodyBones.LastBone) return null;

            Transform roleBone = animator.GetBoneTransform(marker.TargetBone);
            resolvedByRole = roleBone != null;
            return roleBone;
        }

        private void DestroyBuilt(Built built)
        {
            if (_avatarManager != null)
            {
                if (built.Renderers != null)
                    _avatarManager.UnregisterAttachmentRenderers(built.Renderers);
                if (built.HeadRenderers != null && built.HeadRenderers.Length > 0)
                    _avatarManager.UnregisterHeadAttachmentRenderers(built.HeadRenderers);
            }

            if (built.Instances != null)
            {
                for (int i = 0; i < built.Instances.Length; i++)
                    if (built.Instances[i] != null)
                        Destroy(built.Instances[i]);
            }
        }

        private void OnDestroy()
        {
            if (Session != null)
                Session.PeerReachable -= HandlePeerReachable;

            for (int i = 0; i < _built.Count; i++)
                DestroyBuilt(_built[i]);
            _built.Clear();
            _worn.Clear();
        }
    }
}