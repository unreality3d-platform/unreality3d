using System;

namespace U3D.Net
{
    [Serializable]
    public readonly struct PeerId : IEquatable<PeerId>, IComparable<PeerId>
    {
        public static readonly PeerId None = new PeerId(0);

        public readonly ushort Raw;

        public PeerId(ushort raw) { Raw = raw; }

        public bool IsValid => Raw != 0;

        /// <summary>
        /// The peer ID belonging to a personal block index, per G158. Block n produces
        /// peer ID n + 1, so block 0's holder is Peer1 rather than PeerNone, which the
        /// wire format reserves for "nobody" in an owner field.
        /// The range ends at 64 while ushort ends at 65535, so the bound is checked here
        /// rather than left to the type, per G53.
        /// </summary>
        public static PeerId FromBlockIndex(int blockIndex)
        {
            if (blockIndex < 0 || blockIndex >= NetEntity.PersonalBlockCount) return None;
            return new PeerId((ushort)(blockIndex + 1));
        }

        /// <summary>
        /// The personal block index this peer ID came from, or -1 if the value is outside
        /// the range a block can produce.
        /// </summary>
        public int BlockIndex
        {
            get
            {
                if (!IsValid || Raw > NetEntity.PersonalBlockCount) return -1;
                return Raw - 1;
            }
        }

        public bool Equals(PeerId other) => Raw == other.Raw;
        public override bool Equals(object obj) => obj is PeerId p && Equals(p);
        public override int GetHashCode() => Raw;
        public int CompareTo(PeerId other) => Raw.CompareTo(other.Raw);
        public override string ToString() => IsValid ? $"Peer{Raw}" : "PeerNone";

        public static bool operator ==(PeerId a, PeerId b) => a.Raw == b.Raw;
        public static bool operator !=(PeerId a, PeerId b) => a.Raw != b.Raw;
    }
}