using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

public class Client_UI_StartConnect : MonoBehaviour
{

    public UnityEngine.UI.Text serverIP;
    public UnityEngine.UI.InputField input;
    public UnityEngine.UI.Button butt1;
    public UnityEngine.UI.Button butt2;
    public Client_TCP Client;
    public int port=10003;//服务器写的时候留的端口就是这个
    void Start()
    {
        UnityEngine.Cursor.lockState = CursorLockMode.None;
        UnityEngine.Cursor.visible = true;
        butt1.onClick.AddListener(StartTCPConnect);
        butt2.onClick.AddListener(EndTCPConnect);
    }

    public void StartTCPConnect()
    {
        string serverIP = input.text;
        Client.Start_TCP_Connect(serverIP,port);
    }
    public void EndTCPConnect()
    {
        string serverIP = input.text;
        Client.Close_Connect();
    }
}