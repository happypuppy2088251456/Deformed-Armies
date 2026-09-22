using Multiple_Game;
using ProtoBuf;
using SocketGameProtocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Multiple_Game.Controller
{
    internal class ControllerManager
    {
        private Dictionary<RequestCode, BaseController> dic_Controller = new Dictionary<RequestCode, BaseController>();
        private Server server;

        public ControllerManager(Server server)
        {
            this.server = server;

            // 注册所有控制器
            LoginController loginController = new LoginController();
            dic_Controller.Add(loginController.GetRequestCode, loginController);

            // 新增：注册房间控制器
            RoomController roomController = new RoomController();
            dic_Controller.Add(roomController.GetRequestCode, roomController);

            // 新增：注册同步控制器
            SyncController syncController = new SyncController();
            dic_Controller.Add(syncController.GetRequestCode, syncController);

            //ChatController chatController = new ChatController();
            //dic_Controller.Add(chatController.GetRequestCode, chatController);

            //BattleController battleController = new BattleController();
            //dic_Controller.Add(battleController.GetRequestCode, battleController);
        }

        /// <summary>
        /// 获取指定类型的控制器实例（用于断连时调用 RoomController.HandleLeave）
        /// </summary>
        public T GetController<T>() where T : BaseController
        {
            foreach (var controller in dic_Controller.Values)
            {
                if (controller is T result)
                    return result;
            }
            return null;
        }

        public void HandleRequest(MainPack pack, Client client)
        {
            if (dic_Controller.TryGetValue(pack.requestcode, out BaseController controller))
            {
                // ActionCode 的 ToString() 直接得到方法名：
                //   LoginRequest  -> LoginRequest()
                //   RoomJoin      -> RoomJoin()
                //   SyncPosition  -> SyncPosition()
                string methodName = pack.actioncode.ToString();
                MethodInfo method = controller.GetType().GetMethod(methodName);

                if (method != null)
                {
                    try
                    {
                        object[] parameters = new object[] { server, client, pack };
                        object result = method.Invoke(controller, parameters);

                        // 如果控制器方法返回了 MainPack，通过统一序列化通道发送响应
                        if (result is MainPack responsePack)
                        {
                            client.Send(responsePack);
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"[控制器异常] {pack.requestcode}.{pack.actioncode}: {e.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"[路由失败] 未找到方法：{methodName} (RequestCode={pack.requestcode}, ActionCode={pack.actioncode})");
                }
            }
            else
            {
                Console.WriteLine($"[路由失败] 未找到 Controller: {pack.requestcode}");
            }
        }
    }
}
