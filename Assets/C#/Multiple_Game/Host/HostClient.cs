using System.Threading.Tasks;
using NetGame.ClientSide;
using NetGame.ServerSide;

namespace NetGame.Host
{
    /// <summary>
    /// 主机玩家（纯 C#）。复用 Client 的全部能力，唯一区别：
    /// 1) 先在进程内 new 一个 Server 实例并 CreateRoom(port)；
    /// 2) Client 的连接目标是 127.0.0.1:本机端口。
    ///
    /// 主机 = 玩家，不拥有任何特权 API；它的“房主”身份仅体现为
    /// “房间里第一个玩家 + 持有 Server 实例的生命周期”。
    /// 本类只做组合与编排：它自身不监听、也不连接（分别委托给 Server / Client）。
    /// </summary>
    public sealed class HostClient
    {
        public Server Server { get; }
        public Client Client { get; }

        public HostClient(int port)
        {
            Server = new Server();
            Server.CreateRoom(port);
            Client = new Client();
        }

        /// <summary>以主机身份启动：让本地 Client 连回 127.0.0.1:本机端口。</summary>
        public async Task StartAsync()
        {
            await Client.ConnectAsync("127.0.0.1", Server.Port);
        }

        /// <summary>主机退出：断开本地 Client，并销毁 Server（房间解散）。</summary>
        public void Shutdown()
        {
            Client.Disconnect("主机退出");
            Server.Shutdown();
        }
    }
}
