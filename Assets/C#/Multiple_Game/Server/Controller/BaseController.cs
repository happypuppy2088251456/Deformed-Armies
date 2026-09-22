using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SocketGameProtocol;
using ProtoBuf;
using UnityEditor.PackageManager.Requests;
namespace Multiple_Game.Controller
{
    abstract class BaseController
    {
        protected RequestCode rqc = RequestCode.RequestNone;
        public RequestCode GetRequestCode
        {
            get { return rqc; }
        }
    }
}
