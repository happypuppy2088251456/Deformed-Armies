using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Multiple_Game
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== 游戏服务端启动中 ===");

            // 创建 Server 实例（内部自动开始监听）
            Server server = new Server();

            Console.WriteLine($"服务器IP: {server.Server_IP}");
            Console.WriteLine("端口: 10003");


            // 保持主线程不退出，直到用户关闭
            Console.ReadLine();
        }
    }
}