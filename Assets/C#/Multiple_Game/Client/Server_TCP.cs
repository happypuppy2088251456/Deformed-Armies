using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Multiple_Game.Controller;

namespace Multiple_Game
{
    class Server
    {
        private Socket Server_Tcp_Socket;
        private Dictionary<string, Client> dic_ClientSocket = new Dictionary<string, Client>();
        private byte[] buffer = new byte[1024];
        private ControllerManager controllerManager;
        private RoomManager roomManager;  // 新增
        public string Server_IP;

        // 暴露 ControllerManager 供 Client 订阅事件后路由
        public ControllerManager ControllerManager => controllerManager;

        // 新增：暴露 RoomManager
        public RoomManager RoomManager => roomManager;

        public Server()
        {
            controllerManager = new ControllerManager(this);
            roomManager = new RoomManager();  // 新增

            Server_Tcp_Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Server_IP = GetLocalIP();
            Server_Tcp_Socket.Bind(new IPEndPoint(IPAddress.Parse(Server_IP), 10003));
            Server_Tcp_Socket.Listen(20);
            Start_Accept();
        }

        void Start_Accept()
        {
            Server_Tcp_Socket.BeginAccept(Accept_Callback, null);
        }

        void Accept_Callback(IAsyncResult iar)
        {
            try
            {
                // 创建 Client 时传入 Server 引用，用于事件路由
                Client receiveClient = new Client(Server_Tcp_Socket.EndAccept(iar), this);
                string receiveClientIP = receiveClient.clientSocket.RemoteEndPoint.ToString();

                // 订阅断连事件
                receiveClient.OnDisconnected += OnClientDisconnected;

                AddOrUpdate(dic_ClientSocket, receiveClientIP, receiveClient, () => receiveClient.CloseClientSocket());

                Console.WriteLine($"[连接] 新客户端连接：{receiveClientIP}");
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
            finally
            {
                Start_Accept();
            }
        }

        /// <summary>
        /// 客户端断连回调：清理房间状态并广播离开通知
        /// </summary>
        private void OnClientDisconnected(Client client)
        {
            // 从房间中移除并广播
            var roomController = controllerManager.GetController<RoomController>();
            if (roomController != null)
            {
                roomController.HandleLeave(this, client);
            }

            // 从连接字典中移除
            string clientKey = null;
            foreach (var kvp in dic_ClientSocket)
            {
                if (kvp.Value == client)
                {
                    clientKey = kvp.Key;
                    break;
                }
            }

            if (clientKey != null)
            {
                dic_ClientSocket.Remove(clientKey);
                Console.WriteLine($"[断连] 已从连接列表移除：{clientKey}");
            }

            client.CloseClientSocket();
        }

        public void AddOrUpdate<T>(Dictionary<string, T> dic, string key, T value, Action onExists)
        {
            if (dic.ContainsKey(key))
            {
                onExists();
                dic[key] = value;
            }
            else
            {
                dic.Add(key, value);
            }
        }

        public string GetLocalIP()
        {
            NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface nic in nics)
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                IPInterfaceProperties props = nic.GetIPProperties();
                foreach (UnicastIPAddressInformation ip in props.UnicastAddresses)
                {
                    if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ipStr = ip.Address.ToString();
                    if (ipStr.StartsWith("192.168."))
                    {
                        return ipStr;
                    }
                }
            }
            return null;
        }
    }
}
