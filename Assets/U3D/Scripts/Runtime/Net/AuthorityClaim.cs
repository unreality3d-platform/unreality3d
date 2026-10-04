namespace U3D.Net
{
    public readonly struct AuthorityClaim
    {
        public static readonly AuthorityClaim Empty = new AuthorityClaim(PeerId.None, 0);

        public readonly PeerId Peer;
        public readonly ushort Handoff;

        public AuthorityClaim(PeerId peer, ushort handoff)
        {
            Peer = peer;
            Handoff = handoff;
        }

        public bool IsEmpty => Peer == PeerId.None;

        public override string ToString() =>
            IsEmpty ? "Claim(none)" : $"Claim({Peer}, h{Handoff})";
    }
}