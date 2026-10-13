using System;
using System.Collections.Generic;
using System.Linq;
using SocketGameProtocol;

namespace Multiple_Game.Controller
{
    /// <summary>
    /// 房间控制器：处理加入/离开房间请求。
    /// </summary>
    internal class RoomController : BaseController
    {
        public RoomController()
        {
            rqc = RequestCode.Room;
        }

        /// <summary>
        /// 处理加入房间请求。
        /// 1. 将玩家加入房间
        /// 2. 回复加入结果 + 已有玩家列表
        /// 3. 向房间内其他玩家广播 Spawn 通知
        /// </summary>
        public MainPack RoomJoin(Server server, Client client, MainPack mainPack)
        {
            var reqData = mainPack.roomJoinReqData;
            if (reqData == null || string.IsNullOrEmpty(reqData.username))
            {
                return BuildJoinResponse(false, "请求数据不完整", 0, null);
            }

            var (success, playerId, failReason, existingPlayers) =
                server.RoomManager.TryJoinRoom(client, reqData.username, reqData.roomId);

            if (!success)
            {
                return BuildJoinResponse(false, failReason, 0, null);
            }

            // 将 playerId 记录到 Client 上，方便后续断线时查找
            client.PlayerId = playerId;
            client.Username = reqData.username;

            // 1) 回复加入结果（附带已有玩家列表）
            var responsePack = BuildJoinResponse(true, "加入成功", playerId, existingPlayers);
            client.Send(responsePack);

            // 2) 向房间内其他玩家广播 Spawn 通知
            var spawnPack = new MainPack
            {
                requestcode = RequestCode.PlayerSync,
                actioncode = ActionCode.SyncPlayerSpawn,
                returncode = ReturnCode.Success,
                syncSpawnData = new SyncPlayerSpawnData
                {
                    playerId = playerId,
                    username = reqData.username,
                    posX = 0f,
                    posY = 0f,
                    posZ = 0f
                }
            };
            server.RoomManager.BroadcastToRoom(client, spawnPack);

            // 返回 null 因为已经手动 Send 过了，不需要 ControllerManager 再发一次
            return null;
        }

        /// <summary>
        /// 处理离开房间请求。
        /// </summary>
        public MainPack RoomLeave(Server server, Client client, MainPack mainPack)
        {
            HandleLeave(server, client);
            return null;
        }

        /// <summary>
        /// 公共方法：断线或主动离开时统一调用。
        /// </summary>
        public void HandleLeave(Server server, Client client)
        {
            var (playerId, remainingIds) = server.RoomManager.RemoveFromAllRooms(client);
            if (playerId == 0) return;

            // 广播离开通知给房间内剩余玩家
            var leavePack = new MainPack
            {
                requestcode = RequestCode.PlayerSync,
                actioncode = ActionCode.SyncPlayerLeave,
                returncode = ReturnCode.Success,
                syncLeaveData = new SyncPlayerLeaveData { playerId = playerId }
            };

            // 向剩余玩家发送
            foreach (int pid in remainingIds)
            {
                server.RoomManager.SendToPlayer(pid, leavePack);
            }
        }

        private MainPack BuildJoinResponse(bool success, string message, int playerId,
            List<PlayerInfoData> existingPlayers)
        {
            var resData = new RoomJoinResponseData
            {
                success = success,
                message = message,
                playerId = playerId
            };
            if (existingPlayers != null)
            {
                // 改用 AddRange() 批量添加
                resData.playerList.AddRange(existingPlayers);
            }

            return new MainPack
            {
                requestcode = RequestCode.Room,
                actioncode = ActionCode.RoomJoinResponse,
                returncode = success ? ReturnCode.Success : ReturnCode.Failed,
                roomJoinResData = resData
            };
        }
    }
}