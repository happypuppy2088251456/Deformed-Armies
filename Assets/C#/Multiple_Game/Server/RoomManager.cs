using System;
using System.Collections.Generic;
using System.Linq;
using NetGame.Protocol;

namespace NetGame.ServerSide
{
    /// <summary>
    /// 服务端权威状态：维护房间内玩家列表与每个人的最新状态。线程安全。
    /// 本类不接触任何 Socket，只负责数据；由 Server 负责收发与生命周期。
    /// </summary>
    public sealed class RoomManager
    {
        public const int MaxPlayers = 8;

        private readonly object _lock = new object();
        private readonly Dictionary<int, PlayerInfo> _players = new Dictionary<int, PlayerInfo>();
        private readonly Dictionary<int, PlayerState> _states = new Dictionary<int, PlayerState>();
        private readonly HashSet<string> _usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>尝试加入玩家。成功返回 true，否则 failReason 说明原因。</summary>
        public bool TryAddPlayer(int playerId, string username, Vec3 position, out string failReason)
        {
            lock (_lock)
            {
                failReason = null;
                if (string.IsNullOrWhiteSpace(username)) { failReason = "用户名为空"; return false; }
                if (_usernames.Contains(username)) { failReason = "用户名已被占用"; return false; }
                if (_players.Count >= MaxPlayers) { failReason = "房间已满"; return false; }

                var info = new PlayerInfo { PlayerId = playerId, Username = username, Position = position };
                _players[playerId] = info;
                _states[playerId] = new PlayerState { PlayerId = playerId, Position = position, IsGrounded = true };
                _usernames.Add(username);
                return true;
            }
        }

        /// <summary>移除玩家。若玩家不存在返回 false。</summary>
        public bool RemovePlayer(int playerId, out string username)
        {
            lock (_lock)
            {
                username = null;
                if (!_players.TryGetValue(playerId, out var info)) return false;
                username = info.Username;
                _players.Remove(playerId);
                _states.Remove(playerId);
                _usernames.Remove(info.Username);
                return true;
            }
        }

        /// <summary>更新玩家最新状态（权威：Server 已覆写 playerId）。</summary>
        public void UpdateState(PlayerState state)
        {
            lock (_lock)
            {
                if (_states.ContainsKey(state.PlayerId))
                    _states[state.PlayerId] = state;
            }
        }

        public List<PlayerInfo> GetPlayersExcept(int playerId)
        {
            lock (_lock)
            {
                return _players.Values
                    .Where(p => p.PlayerId != playerId)
                    .Select(p => new PlayerInfo { PlayerId = p.PlayerId, Username = p.Username, Position = LatestPosition(p.PlayerId, p.Position) })
                    .ToList();
            }
        }

        public PlayerInfo GetPlayer(int playerId)
        {
            lock (_lock)
            {
                if (_players.TryGetValue(playerId, out var p))
                    return new PlayerInfo { PlayerId = p.PlayerId, Username = p.Username, Position = LatestPosition(p.PlayerId, p.Position) };
                return null;
            }
        }

        private Vec3 LatestPosition(int playerId, Vec3 fallback)
        {
            return _states.TryGetValue(playerId, out PlayerState s) ? s.Position : fallback;
        }

        public int Count
        {
            get { lock (_lock) return _players.Count; }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _players.Clear();
                _states.Clear();
                _usernames.Clear();
            }
        }
    }
}
