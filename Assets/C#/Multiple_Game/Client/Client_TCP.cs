using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using UnityEngine;
using ProtoBuf;
using SocketGameProtocol;

namespace Multiple_Game
{
    //
    /// Unity 客户端 TCP 连接层。
    /// 负责与服务器建立 TCP 连接、Protobuf 序列化/反序列化、粘包处理。
    //
    public class Client_TCP : MonoBehaviour
    {
        // ==================== 单例 ====================
        public static Client_TCP Instance { get; private set; }

        // ==================== 公共事件 ====================

        //当成功连接到服务器时触发
        public event Action OnConnected;

        /// <summary>当与服务器断开连接时触发</summary>
        public event Action OnDisconnected;

        /// <summary>当收到一个完整的 MainPack 时触发（在主线程中调用）</summary>
        public event Action<MainPack> OnMessageReceived;

        // ==================== 内部状态 ====================

        private Socket clientSocket;
        private byte[] buffer = new byte[4096];
        private int startIndex = 0;
        private bool isConnected = false;

        // 线程安全队列：网络线程产生的消息在主线程 Update 中分发
        private Queue<MainPack> messageQueue = new Queue<MainPack>();
        private object queueLock = new object();

        public bool IsConnected => isConnected;

        // ==================== 生命周期 ====================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            // 主线程分发队列中的消息
            lock (queueLock)
            {
                while (messageQueue.Count > 0)
                {
                    MainPack pack = messageQueue.Dequeue();
                    OnMessageReceived?.Invoke(pack);
                }
            }
        }

        private void OnDestroy()
        {
            Disconnect();
            if (Instance == this) Instance = null;
        }

        // ==================== 连接 / 断开 ====================

        /// <summary>
        /// 连接到指定 IP 和端口的服务器。
        /// </summary>
        public void Connect(string serverIP, int serverPort)
        {
            if (isConnected)
            {
                Debug.LogWarning("[Client_TCP] 已经连接到服务器，请先断开");
                return;
            }

            try
            {
                clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                clientSocket.BeginConnect(serverIP, serverPort, ConnectCallback, null);
                Debug.Log($"[Client_TCP] 正在连接服务器: {serverIP}:{serverPort}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Client_TCP] 连接失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 断开与服务器的连接。
        /// </summary>
        public void Disconnect()
        {
            if (!isConnected) return;

            isConnected = false;
            try
            {
                if (clientSocket != null && clientSocket.Connected)
                {
                    clientSocket.Shutdown(SocketShutdown.Both);
                }
                clientSocket?.Close();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Client_TCP] 断开时异常: {ex.Message}");
            }

            clientSocket = null;
            startIndex = 0;
            Debug.Log("[Client_TCP] 已断开连接");
            OnDisconnected?.Invoke();
        }

        // ==================== 发送 ====================

        /// <summary>
        /// 通过 Protobuf 序列化发送 MainPack 到服务器。
        /// 线程安全，可在任意线程调用。
        /// </summary>
        public void Send(MainPack pack)
        {
            if (!isConnected || clientSocket == null || !clientSocket.Connected)
            {
                Debug.LogWarning("[Client_TCP] 未连接，无法发送");
                return;
            }

            try
            {
                byte[] data;
                using (MemoryStream ms = new MemoryStream())
                {
                    Serializer.Serialize(ms, pack);
                    data = ms.ToArray();
                }

                int packLength = data.Length;
                int requestCodeValue = (int)pack.requestcode;

                // 构建 8 字节包头：4 字节长度 + 4 字节 RequestCode
                byte[] header = new byte[8];
                header[0] = (byte)(packLength & 0xFF);
                header[1] = (byte)((packLength >> 8) & 0xFF);
                header[2] = (byte)((packLength >> 16) & 0xFF);
                header[3] = (byte)((packLength >> 24) & 0xFF);
                Array.Copy(BitConverter.GetBytes(requestCodeValue), 0, header, 4, 4);

                // 先发包头，再发数据体
                clientSocket.Send(header, SocketFlags.None);
                clientSocket.Send(data, SocketFlags.None);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Client_TCP] 发送异常: {ex.Message}");
                HandleDisconnect();
            }
        }

        // ==================== 接收 ====================

        private void ConnectCallback(IAsyncResult iar)
        {
            try
            {
                clientSocket.EndConnect(iar);
                isConnected = true;
                startIndex = 0;
                Debug.Log("[Client_TCP] 连接成功");
                StartReceive();

                // Connect 回调在后台线程，放入队列在主线程触发事件
                EnqueueEvent(OnConnected);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Client_TCP] 连接回调异常: {ex.Message}");
            }
        }

        private void StartReceive()
        {
            if (!isConnected || clientSocket == null) return;

            try
            {
                clientSocket.BeginReceive(buffer, startIndex, buffer.Length - startIndex,
                    SocketFlags.None, ReceiveCallback, null);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Client_TCP] 启动接收异常: {ex.Message}");
                HandleDisconnect();
            }
        }

        private void ReceiveCallback(IAsyncResult iar)
        {
            try
            {
                if (clientSocket == null || !clientSocket.Connected)
                {
                    HandleDisconnect();
                    return;
                }

                int len = clientSocket.EndReceive(iar);
                if (len == 0)
                {
                    HandleDisconnect();
                    return;
                }

                startIndex += len;
                ParseBuffer();
                StartReceive();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Client_TCP] 接收回调异常: {ex.Message}");
                HandleDisconnect();
            }
        }

        /// <summary>
        /// 粘包处理：循环解析缓冲区中的完整 Protobuf 包。
        /// 协议格式：[4 字节数据长度] [4 字节 RequestCode] [Protobuf 数据体]
        /// </summary>
        private void ParseBuffer()
        {
            while (startIndex >= 8)
            {
                // 读取包头
                int count = buffer[0] | (buffer[1] << 8) | (buffer[2] << 16) | (buffer[3] << 24);
                // int requestCodeValue = BitConverter.ToInt32(buffer, 4); // 可用于校验

                if (startIndex >= count + 8)
                {
                    MainPack pack;
                    using (MemoryStream ms = new MemoryStream(buffer, 8, count))
                    {
                        pack = Serializer.Deserialize<MainPack>(ms);
                    }

                    // 放入主线程队列分发
                    lock (queueLock)
                    {
                        messageQueue.Enqueue(pack);
                    }

                    // 粘包处理：剩余数据前移
                    Array.Copy(buffer, count + 8, buffer, 0, startIndex - count - 8);
                    startIndex -= (count + 8);
                }
                else
                {
                    break; // 数据不够一个完整包，等待下次接收
                }
            }
        }


        private void HandleDisconnect()
        {
            if (!isConnected) return;
            isConnected = false;

            try
            {
                clientSocket?.Close();
            }
            catch { }

            clientSocket = null;
            startIndex = 0;

            EnqueueEvent(OnDisconnected);
        }
        private void EnqueueEvent(Action callback)
        {
            if (callback == null) return;
            lock (queueLock)
            {
                messageQueue.Enqueue(new MainPack
                {
                    requestcode = RequestCode.RequestNone,
                    actioncode = ActionCode.ActionNone,
                    returncode = ReturnCode.ReturnNone
                });
            }
            _pendingActions.Enqueue(callback);
        }

        // 线程安全的 Action 队列
        private Queue<Action> _pendingActions = new Queue<Action>();

        // 在 Update 中也处理 Action 队列
        private void ProcessPendingActions()
        {
            while (_pendingActions.Count > 0)
            {
                Action action = _pendingActions.Dequeue();
                action?.Invoke();
            }
        }

        // 重写 Update 以处理两个队列
        private void LateUpdate()
        {
            ProcessPendingActions();
        }
    }
}
