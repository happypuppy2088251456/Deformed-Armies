using System;

namespace NetGame.ServerSide
{
    /// <summary>
    /// 可选：独立控制台服务端，用于脱离 Unity 单独联调（局域网测试房主不在本机时）。
    /// Unity 运行时不调用此 Main，可安全保留；若项目不需要，删除本文件亦可。
    /// </summary>
    internal static class Program
    {
        private static void Main(string[] args)
        {
            int port = 10003;
            if (args.Length > 0 && int.TryParse(args[0], out int p)) port = p;

            var server = new Server();
            server.OnLog += Console.WriteLine;
            server.OnPlayerJoin += (id, info) => Console.WriteLine($"[JOIN] {info.Username} id={id}");
            server.OnPlayerLeave += id => Console.WriteLine($"[LEAVE] id={id}");
            server.OnRoomDissolved += () => Console.WriteLine("[ROOM DISSOLVED]");

            server.CreateRoom(port);
            Console.WriteLine($"服务端已监听端口 {port}，局域网地址 {Server.GetLocalIPv4()}:{port}");
            Console.WriteLine("输入 exit 关停服务端");

            while (server.IsRunning)
            {
                string line = Console.ReadLine();
                if (line != null && line.Trim().ToLower() == "exit") break;
            }

            server.Shutdown();
        }
    }
}
