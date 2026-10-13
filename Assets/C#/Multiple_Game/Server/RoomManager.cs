using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SocketGameProtocol;

namespace Multiple_Game
{
    // 房间管理器：管理房间创建/销毁、玩家加入/离开、广播。
    internal class RoomManager
    {
        // 每个房间最多 4 名玩家（1 主机 + 3 客机）
        public const int MAX_PLAYERS_PER_ROOM = 4;

        private Dictionary<int, Room> rooms = new Dictionary<int, Room>();
        private object roomLock = new object();
        private int nextPlayerId = 1;

        // 尝试将玩家加入指定房间。
        // 返回 (是否成功, 分配的 playerId, 房间内已有玩家列表)
        public (bool success, int playerId, string failReason, List<PlayerInfoData> existingPlayers)
            TryJoinRoom(Client client, string username, int roomId)
        {
            lock (roomLock)
            {
                if (!rooms.ContainsKey(roomId))
                {
                    rooms[roomId] = new Room(roomId);
                    Console.WriteLine($"[房间] 房间 {roomId} 已自动创建");
                }

                Room room = rooms[roomId];

                if (room.players.Count >= MAX_PLAYERS_PER_ROOM)
                {
                    return (false, 0, "房间已满", null);
                }

                // 检查用户名是否已存在于房间
                if (room.players.Values.Any(p => p.username == username))
                {
                    return (false, 0, "用户名已被占用", null);
                }

                int pid = Interlocked.Increment(ref nextPlayerId);

                var info = new PlayerInfoData
                {
                    playerId = pid,
                    username = username,
                    posX = 0f, posY = 0f, posZ = 0f
                };

                // 记录已有玩家（用于告诉新来者房间里还有谁）
                var existingPlayers = room.players.Values.Select(p => new PlayerInfoData
                {
                    playerId = p.playerId,
                    username = p.username,
                    posX = p.posX, posY = p.posY, posZ = p.posZ
                }).ToList();

                room.players[pid] = info;
                room.clientMap[pid] = client;

                Console.WriteLine($"[房间] 玩家 [{username}](ID={pid}) 加入房间 {roomId}，当前人数: {room.players.Count}");

                return (true, pid, null, existingPlayers);
            }
        }
        // 玩家离开房间（主动离开或断线触发）。
        // 返回离开的 playerId 和房间剩余玩家列表。
        public (int playerId, List<int> remainingPlayerIds) RemoveFromAllRooms(Client client)
        {
            lock (roomLock)
            {
                foreach (var kvp in rooms)
                {
                    Room room = kvp.Value;
                    var entry = room.clientMap.FirstOrDefault(e => e.Value == client);
                    if (entry.Key != 0)
                    {
                        int pid = entry.Key;
                        room.players.Remove(pid);
                        room.clientMap.Remove(pid);

                        var remaining = room.players.Keys.ToList();
                        Console.WriteLine($"[房间] 玩家 ID={pid} 离开房间 {kvp.Key}，剩余人数: {remaining.Count}");

                        // 房间空了则自动销毁
                        if (room.players.Count == 0)
                        {
                            rooms.Remove(kvp.Key);
                            Console.WriteLine($"[房间] 房间 {kvp.Key} 已自动销毁");
                        }

                        return (pid, remaining);
                    }
                }
                return (0, new List<int>());
            }
        }

        // 获取客户端对应的 playerId（0 表示不在任何房间）。
        public int GetPlayerId(Client client)
        {
            lock (roomLock)
            {
                foreach (var room in rooms.Values)
                {
                    var entry = room.clientMap.FirstOrDefault(e => e.Value == client);
                    if (entry.Key != 0) return entry.Key;
                }
                return 0;
            }
        }
        // 获取客户端所在房间内其他所有客户端。

        public List<(Client client, int playerId)> GetOtherClientsInRoom(Client client)
        {
            lock (roomLock)
            {
                foreach (var room in rooms.Values)
                {
                    var entry = room.clientMap.FirstOrDefault(e => e.Value == client);
                    if (entry.Key != 0)
                    {
                        return room.clientMap
                            .Where(e => e.Key != entry.Key)
                            .Select(e => (e.Value, e.Key))
                            .ToList();
                    }
                }
                return new List<(Client, int)>();
            }
        }

        // 向房间内除发送者外的所有玩家广播消息。

        public void BroadcastToRoom(Client sender, MainPack pack)
        {
            var others = GetOtherClientsInRoom(sender);
            foreach (var (c, _) in others)
            {
                try { c.Send(pack); }
                catch (Exception e) { Console.WriteLine($"[广播异常] {e.Message}"); }
            }
        }

        // 向房间内所有玩家广播消息（包括发送者）。

        public void BroadcastToRoomAll(Client sender, MainPack pack)
        {
            lock (roomLock)
            {
                foreach (var room in rooms.Values)
                {
                    if (room.clientMap.ContainsValue(sender))
                    {
                        foreach (var c in room.clientMap.Values)
                        {
                            try { c.Send(pack); }
                            catch (Exception e) { Console.WriteLine($"[广播异常] {e.Message}"); }
                        }
                        return;
                    }
                }
            }
        }


        // 向房间内指定玩家发送消息

        public void SendToPlayer(int targetPlayerId, MainPack pack)
        {
            lock (roomLock)
            {
                foreach (var room in rooms.Values)
                {
                    if (room.clientMap.TryGetValue(targetPlayerId, out var client))
                    {
                        try { client.Send(pack); }
                        catch (Exception e) { Console.WriteLine($"[定向发送异常] {e.Message}"); }
                        return;
                    }
                }
            }
        }


        // 更新玩家在房间中记录的位置
        public void UpdatePlayerPosition(int playerId, float x, float y, float z)
        {
            lock (roomLock)
            {
                foreach (var room in rooms.Values)
                {
                    if (room.players.ContainsKey(playerId))
                    {
                        room.players[playerId].posX = x;
                        room.players[playerId].posY = y;
                        room.players[playerId].posZ = z;
                        return;
                    }
                }
            }
        }


        // 获取玩家所在房间的 clientMap（用于 SyncController 获取广播定时器）

        public Dictionary<int, Client> GetClientMapForClient(Client client)
        {
            lock (roomLock)
            {
                foreach (var room in rooms.Values)
                {
                    if (room.clientMap.ContainsValue(client))
                        return room.clientMap;
                }
                return null;
            }
        }


        // 获取房间内所有玩家信息（用于首次同步）。

        public List<PlayerInfoData> GetAllPlayersInRoom(Client client)
        {
            lock (roomLock)
            {
                foreach (var room in rooms.Values)
                {
                    if (room.clientMap.ContainsValue(client))
                        return room.players.Values.Select(p => new PlayerInfoData
                        {
                            playerId = p.playerId,
                            username = p.username,
                            posX = p.posX, posY = p.posY, posZ = p.posZ
                        }).ToList();
                }
                return new List<PlayerInfoData>();
            }
        }

        private class Room
        {
            public int roomId;
            public Dictionary<int, PlayerInfoData> players = new Dictionary<int, PlayerInfoData>();
            public Dictionary<int, Client> clientMap = new Dictionary<int, Client>();

            public Room(int roomId) { this.roomId = roomId; }
        }
    }
}
