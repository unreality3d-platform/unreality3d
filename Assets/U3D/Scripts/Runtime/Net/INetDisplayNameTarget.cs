namespace U3D.Net
{
    /// <summary>
    /// A component that can receive a player's display name from the wire.
    ///
    /// Defined in the net assembly so ReceiveDisplayName can find it without naming the
    /// game-side nametag class. The nametag implements it, and the session discovers it
    /// by GetComponentInChildren on the sender's entity, exactly as INetAvatarPlayback
    /// lets the session hand avatar packets to a component it cannot name.
    /// </summary>
    public interface INetDisplayNameTarget
    {
        void SetDisplayName(string name);
    }
}
