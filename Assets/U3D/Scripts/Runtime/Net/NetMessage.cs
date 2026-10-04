using System;

namespace U3D.Net
{
    public sealed class NetMessage
    {
        private readonly NetComponent _owner;
        private readonly string _key;
        private readonly Action<PeerId, object[]> _handler;

        internal NetMessage(NetComponent owner, string key, Action<PeerId, object[]> handler)
        {
            _owner = owner;
            _key = key;
            _handler = handler;
        }

        public string Key => _key;

        internal void Invoke(PeerId sender, object[] args) => _handler?.Invoke(sender, args);

        public void SendToAll(params object[] args)
        {
            if (Net.Session == null || _owner.Entity == null) return;
            Net.Session.Send(_owner.Entity, _key, PeerId.None, args);
        }

        public void SendToOwner(params object[] args)
        {
            if (Net.Session == null || _owner.Entity == null) return;

            PeerId owner = _owner.Entity.Owner;
            if (!owner.IsValid) return;

            Net.Session.Send(_owner.Entity, _key, owner, args);
        }

        public void SendToReporter(params object[] args)
        {
            if (Net.Session == null || _owner.Entity == null) return;

            PeerId reporter = Net.Session.Reporter;
            if (!reporter.IsValid) return;

            Net.Session.Send(_owner.Entity, _key, reporter, args);
        }

        public void SendTo(PeerId target, params object[] args)
        {
            if (Net.Session == null || _owner.Entity == null) return;
            if (!target.IsValid) return;

            Net.Session.Send(_owner.Entity, _key, target, args);
        }
    }
}