using ProtoBuf;
using SocketGameProtocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Multiple_Game.Controller
{
    class LoginController : BaseController
    {
        public LoginController()
        {
            rqc = RequestCode.Login;
        }
        public MainPack LoginRequest(Server server, Client client, MainPack mainPack)
        {
            string username = mainPack.loginReqData?.username;

            if (string.IsNullOrEmpty(username))
            {
                return BuildResponse(ReturnCode.Failed, "用户名不能为空");
            }

            // TODO: 实际应用中在此查库验证用户名/密码
            Console.WriteLine($"[登录] 玩家 [{username}] 登录成功");
            return BuildResponse(ReturnCode.Success, "登录成功");
        }

        // 统一构建响应包，使用 MainPack + Protobuf 序列化
        private MainPack BuildResponse(ReturnCode code, string message)
        {
            return new MainPack
            {
                requestcode = RequestCode.Login,
                actioncode = ActionCode.LoginResponse,
                returncode = code,
                loginResData = new LoginResponseData
                {
                    success = code == ReturnCode.Success,
                    message = message
                }
            };
        }
    }
}