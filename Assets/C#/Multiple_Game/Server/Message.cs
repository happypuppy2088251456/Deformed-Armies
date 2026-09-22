using ProtoBuf;
using SocketGameProtocol;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using UnityEditor.PackageManager;
namespace Multiple_Game.Tool
{
    internal class Message
    {
        private byte[] buffer = new byte[1024];
        private int startindex;
        public event Action<MainPack> OnMessageReceived;

        public byte[] Buffer
        {
            get { return buffer; }
        }
        public int StartIndex
        {
            get { return startindex; }
        }
        public int RemainSize
        {
            get { return buffer.Length - startindex; }
        }
        public void ReadBuffer(int len)
        {
            startindex += len;

            // 逐包解析
            while (startindex >= 8)
            {
                // 读取包头，4字节长度 + 4字节RequestCode
                int count = buffer[0] | (buffer[1] << 8) | (buffer[2] << 16) | (buffer[3] << 24);
                int requestCodeValue = BitConverter.ToInt32(buffer, 4);

                if (startindex >= (count + 8))
                {
                    // 反序列化 MainPack
                    MainPack pack = new MainPack();
                    using (MemoryStream ms = new MemoryStream(buffer, 8, count))
                    {
                        pack = Serializer.Deserialize<MainPack>(ms);
                    }

                    // 触发事件，交由 ControllerManager导航到具体控制器
                    OnMessageReceived?.Invoke(pack);

                    // 粘包处理: 将未解析的剩余数据移到缓冲区头部
                    Array.Copy(buffer, count + 8, buffer, 0, startindex - count - 8);
                    startindex -= (count + 8);
                }
                else
                {
                    // 数据不够一个完整包，等待下次接收
                    break;
                }
            }
        }

        // === 统一 Protobuf 序列化发送方法 ===
        // 所有响应（登录/聊天/战斗）都通过此方法发送
        public void Send(MainPack pack, Socket socket)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                //序列化消息体
                Serializer.Serialize(ms, pack);
                byte[] data = ms.ToArray();

                int packLength = data.Length;
                int requestCodeValue = (int)pack.requestcode;

                // 构建 8 字节包头
                byte[] header = new byte[8];
                // 4字节消息体长度 (Little Endian)
                header[0] = (byte)(packLength & 0xFF);
                header[1] = (byte)((packLength >> 8) & 0xFF);
                header[2] = (byte)((packLength >> 16) & 0xFF);
                header[3] = (byte)((packLength >> 24) & 0xFF);
                // 4字节 RequestCode
                Array.Copy(BitConverter.GetBytes(requestCodeValue), 0, header, 4, 4);

                // 先发送包头，再发送数据体
                socket.Send(header, SocketFlags.None);
                socket.Send(data, SocketFlags.None);
            }
        }
    }
}

