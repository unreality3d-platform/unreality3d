namespace U3D.Net
{
    /// <summary>
    /// Something that plays back another player's body from the packets they send.
    ///
    /// Declared here so the session can hand an arriving packet to it without naming the
    /// component that implements it — the playback lives on a player prefab and drives an
    /// Animator, both of which are outside this assembly, and reaching for them directly is
    /// the side door the seam's empty reference list exists to close.
    /// </summary>
    public interface INetAvatarPlayback
    {
        void Receive(ushort sequence, in NetAvatarState state);
    }
}