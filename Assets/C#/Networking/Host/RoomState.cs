using System;
using System.Collections.Generic;
using System.Net;
using Game.Protocol;

namespace Game.Net.Host
{
    /// <summary>
    /// 房间玩家表。所有操作加锁，因为 UDP 收包线程 / 主线程都可能访问。
    /// 房间内的“业务状态”（准备、分数等）都挂在这里，方便后面扩展。
    /// </summary>
    public sealed class RoomState
    {
        private readonly Dictionary<uint, PlayerSession> _players =
            new Dictionary<uint, PlayerSession>();

        private readonly object _gate = new object();

        public string RoomName = "LAN Room";
        public int MaxPlayers = NetConst.DefaultMaxPlayers;
        public bool GameStarted;

        public int Count
        {
            get { lock (_gate) return _players.Count; }
        }

        public bool IsFull
        {
            get { lock (_gate) return _players.Count >= MaxPlayers; }
        }

        public bool Add(PlayerSession session)
        {
            if (session == null) return false;
            lock (_gate)
            {
                if (_players.ContainsKey(session.PlayerId)) return false;
                _players.Add(session.PlayerId, session);
                return true;
            }
        }

        public PlayerSession Get(uint playerId)
        {
            lock (_gate)
            {
                PlayerSession s;
                return _players.TryGetValue(playerId, out s) ? s : null;
            }
        }

        public bool Remove(uint playerId)
        {
            lock (_gate) return _players.Remove(playerId);
        }

        public PlayerSession FindByTcpPeer(TcpPeer peer)
        {
            if (peer == null) return null;
            lock (_gate)
            {
                foreach (PlayerSession s in _players.Values)
                {
                    if (ReferenceEquals(s.Tcp, peer)) return s;
                }
            }
            return null;
        }

        public PlayerSession FindByUdpEndPoint(IPEndPoint endpoint)
        {
            if (endpoint == null) return null;
            lock (_gate)
            {
                foreach (PlayerSession s in _players.Values)
                {
                    if (s.UdpEndPoint != null && s.UdpEndPoint.Equals(endpoint)) return s;
                }
            }
            return null;
        }

        /// <summary>取一份玩家列表快照（调用方可以安全遍历，不必持锁）。</summary>
        public List<PlayerSession> Snapshot()
        {
            lock (_gate) return new List<PlayerSession>(_players.Values);
        }

        /// <summary>所有已完成 UDP 握手的端点。</summary>
        public List<IPEndPoint> UdpEndpoints()
        {
            var list = new List<IPEndPoint>();
            lock (_gate)
            {
                foreach (PlayerSession s in _players.Values)
                {
                    if (s.UdpEndPoint != null) list.Add(s.UdpEndPoint);
                }
            }
            return list;
        }

        /// <summary>所有远程 TCP 连接（不含房主自己）。</summary>
        public List<TcpPeer> TcpPeers()
        {
            var list = new List<TcpPeer>();
            lock (_gate)
            {
                foreach (PlayerSession s in _players.Values)
                {
                    if (s.Tcp != null) list.Add(s.Tcp);
                }
            }
            return list;
        }

        public RoomStateMsg ToRoomStateMsg()
        {
            var msg = new RoomStateMsg();
            lock (_gate)
            {
                foreach (PlayerSession s in _players.Values) msg.Players.Add(s.ToPlayerInfo());
            }
            return msg;
        }

        public void Clear()
        {
            lock (_gate) _players.Clear();
        }
    }
}
