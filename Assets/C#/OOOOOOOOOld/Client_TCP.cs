using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
//客户端（Client）启动连接脚本


public class Client_TCP : MonoBehaviour
{
    public Socket Client_Tcp_Socket;
    public string Server_IP;
    //public int Server_Port;
    public Thread Server_Recv_TCP_Thread;
    public bool isRun=false;
    public void Start_TCP_Connect(string Server_IP, int Server_Port)
    {
        try
        {
            isRun = true;
            //创建客户端 Socket
            Client_Tcp_Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            //连接服务器
            Client_Tcp_Socket.Connect(Server_IP, Server_Port);
            Debug.Log("已连接服务器: " + Server_IP + ":" + Server_Port);

            // 启动后台线程，持续监听服务器发来的数据
            Server_Recv_TCP_Thread = new Thread(Recv_Server_Data);
            Server_Recv_TCP_Thread.IsBackground = true; // 设置为后台线程，随主程序关闭而关闭
            Server_Recv_TCP_Thread.Start();
        }
        catch (Exception ex)
        {
            Debug.LogError("连接服务器失败: " + ex.Message);
        }
    }

    private void Recv_Server_Data()
    {
        byte[] buffer = new byte[1024];
        while (isRun)
        {
            try
            {
                // 阻塞等待服务器发来的数据
                int length = Client_Tcp_Socket.Receive(buffer);
                if (length <= 0)
                {
                    Debug.Log("服务器已断开连接");
                    break;
                }

                //在这里处理服务器发送的数据
                string msg = System.Text.Encoding.UTF8.GetString(buffer, 0, length);
                Debug.Log("收到服务器数据: " + msg);
            }
            catch (Exception ex)
            {
                Debug.LogError("接收服务器数据异常: " + ex.Message);
                break;
            }
        }
    }

    //关闭连接
    public void Close_Connect()
    {
        try
        {
            if (Client_Tcp_Socket != null)
            {
                Client_Tcp_Socket.Close();
                Client_Tcp_Socket = null;
                isRun = false;
                Debug.Log("已关闭连接");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("关闭连接失败: " + ex.Message);
        }
    }
}