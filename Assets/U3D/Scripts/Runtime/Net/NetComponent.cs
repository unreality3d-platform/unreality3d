using System;
using System.Collections.Generic;
using UnityEngine;

namespace U3D.Net
{
    public abstract class NetComponent : MonoBehaviour
    {
        private NetEntity _entity;
        private Dictionary<string, NetMessage> _messages;

        public NetEntity Entity => _entity;
        public INetSession Session => Net.Session;
        public bool HasAuthority => _entity != null && _entity.HasAuthority;
        public bool IsLive => _entity != null && _entity.IsLive;

        internal void BindEntity(NetEntity entity) => _entity = entity;

        protected NetMessage RegisterMessage(string key, Action<PeerId, object[]> handler)
        {
            _messages ??= new Dictionary<string, NetMessage>();
            var msg = new NetMessage(this, key, handler);
            _messages[key] = msg;
            return msg;
        }

        internal void DeliverMessage(string key, PeerId sender, object[] args)
        {
            if (_messages != null && _messages.TryGetValue(key, out NetMessage msg))
                msg.Invoke(sender, args);
        }

#if UNITY_EDITOR
        internal void CollectKeys(List<string> into)
        {
            if (_messages == null) return;
            foreach (string key in _messages.Keys)
                into.Add(key);
        }
#endif

        internal void InvokeNetSpawn() => OnNetSpawn();
        internal void InvokeNetDespawn() => OnNetDespawn();
        internal void InvokeAuthorityChanged(PeerId previous, PeerId current) => OnAuthorityChanged(previous, current);
        internal void InvokeNetTick() => OnNetTick();
        internal void InvokeNetRender() => OnNetRender();

        protected virtual void OnNetSpawn() { }
        protected virtual void OnNetDespawn() { }
        protected virtual void OnAuthorityChanged(PeerId previous, PeerId current) { }
        protected virtual void OnNetTick() { }
        protected virtual void OnNetRender() { }
    }
}