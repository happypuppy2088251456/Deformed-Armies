using System;
using System.Collections.Generic;
using System.Linq;
using SocketGameProtocol;

namespace Multiple_Game.Controller
{
    /// <summary>
    /// 同步控制器：处理玩家位置/视角上报，并广播给房间内其他玩家。
    /// 服务器在此做"中继分发"，收到一个客户端的数据后原样转发给房间内其他人。
    /// </summary>
    internal class SyncController : BaseController
    {
        public SyncController()
        {
            rqc = RequestCode.PlayerSync;
        }

        /// <summary>
        /// 处理位置同步：收到客户端的位置数据，转发给房间内其他玩家。
        /// 同时更新 RoomManager 中的玩家位置记录。
        /// </summary>
        public MainPack SyncPosition(Server server, Client client, MainPack mainPack)
        {
            var posData = mainPack.syncPosData;
            if (posData == null) return null;

            // 使用 Client 上记录的 playerId 覆写（防止伪造）
            posData.playerId = client.PlayerId;

            // 更新服务器记录的玩家位置
            server.RoomManager.UpdatePlayerPosition(posData.playerId, posData.posX, posData.posY, posData.posZ);

            // 构建广播包转发给其他玩家
            var broadcastPack = new MainPack
            {
                requestcode = RequestCode.PlayerSync,
                actioncode = ActionCode.SyncBroadcast,
                returncode = ReturnCode.Success,
                syncBroadcastData = new SyncBroadcastData()
            };
            // 改用 Add() 逐个添加
            broadcastPack.syncBroadcastData.positions.Add(posData);

            server.RoomManager.BroadcastToRoom(client, broadcastPack);
            return null; // 已手动广播，不需要再回复发送者
        }

        /// <summary>
        /// 处理视角同步：收到客户端的视角数据，转发给房间内其他玩家。
        /// </summary>
        public MainPack SyncLook(Server server, Client client, MainPack mainPack)
        {
            var lookData = mainPack.syncLookData;
            if (lookData == null) return null;

            lookData.playerId = client.PlayerId;

            var broadcastPack = new MainPack
            {
                requestcode = RequestCode.PlayerSync,
                actioncode = ActionCode.SyncBroadcast,
                returncode = ReturnCode.Success,
                syncBroadcastData = new SyncBroadcastData()
            };
            // 改用 Add() 逐个添加
            broadcastPack.syncBroadcastData.looks.Add(lookData);

            server.RoomManager.BroadcastToRoom(client, broadcastPack);
            return null;
        }
    }
}