using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using SocketGameProtocol;
using Multiple_Game.Controller;
using Multiple_Game.Tool;

namespace Multiple_Game
{
    class Client
    {
        public Socket clientSocket;
        private Message message;
        private Server server;

        // 新增：记录玩家在房间中的 ID 和用户名
        public int PlayerId { get; set; }
        public string Username { get; set; }

        // 新增：断连事件
        public event Action<Client> OnDisconnected;

        public Client(Socket s, Server server)
        {
            this.clientSocket = s;
            this.server = server;
            message = new Message();
            // 订阅消息到达事件，交由 ControllerManager 统一路由
            message.OnMessageReceived += OnMessageReceived;
            StartReceive();
        }

        private void OnMessageReceived(MainPack pack)
        {
            // 将解析好的 MainPack 交给 ControllerManager
            server.ControllerManager.HandleRequest(pack, this);
        }

        public void StartReceive()
        {
            try
            {
                clientSocket.BeginReceive(message.Buffer, message.StartIndex, message.RemainSize,
                    SocketFlags.None, ReceiveCallBack, null);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[接收启动异常] {e.Message}");
                HandleDisconnect();
            }
        }

        public void ReceiveCallBack(IAsyncResult iar)
        {
            try
            {
                if (clientSocket == null || clientSocket.Connected == false)
                {
                    HandleDisconnect();
                    return;
                }

                int len = clientSocket.EndReceive(iar);
                if (len == 0)
                {
                    // 连接已关闭
                    HandleDisconnect();
                    return;
                }

                message.ReadBuffer(len);
                StartReceive();
            }
            catch (Exception e)
            {
                Console.WriteLine($"[接收异常] {e.Message}");
                HandleDisconnect();
            }
        }

        private void HandleDisconnect()
        {
            Console.WriteLine($"[断连] 玩家 [{Username ?? "未登录"}](ID={PlayerId}) 断开连接");
            OnDisconnected?.Invoke(this);
        }

        public void CloseClientSocket()
        {
            try
            {
                if (clientSocket != null && clientSocket.Connected)
                {
                    clientSocket.Shutdown(SocketShutdown.Both);
                }
                clientSocket?.Close();
            }
            catch (Exception e)
            {
                Console.WriteLine($"[关闭异常] {e.Message}");
            }
        }

        // 统一发送入口：所有响应都通过 Message.Send 用 Protobuf 序列化
        public void Send(MainPack pack)
        {
            try
            {
                if (clientSocket != null && clientSocket.Connected)
                {
                    message.Send(pack, clientSocket);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[发送异常] {e.Message}");
                HandleDisconnect();
            }
        }
    }
}
