using NetGame.Unity;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 主机面板：输入端口（与可选用户名）→ 创建房间（进程内启动 Server + 本地 Client 连回 127.0.0.1）。
/// “开始游戏”会广播 GameStart 给房间内所有玩家。
/// </summary>
public class Sever_UI_CreatRoom : MonoBehaviour
{
    [Header("输入")]
    public InputField portInputField;
    public InputField usernameInputField;

    [Header("按钮")]
    public Button creatRoomButton;
    public Button startGameButton;

    [Header("状态")]
    public Text statusText;

    private void Start()
    {
        if (portInputField != null && string.IsNullOrEmpty(portInputField.text))
            portInputField.text = "10003";

        if (creatRoomButton != null)
        {
            creatRoomButton.onClick.RemoveAllListeners();
            creatRoomButton.onClick.AddListener(OnCreatRoomClick);
        }

        if (startGameButton != null)
        {
            startGameButton.onClick.RemoveAllListeners();
            startGameButton.onClick.AddListener(OnStartGameClick);
        }

        if (LobbyController.Instance != null)
            LobbyController.Instance.OnStatus += ShowStatus;

        if (ClientGameController.Instance != null)
        {
            ClientGameController.Instance.OnJoinedRoom += OnJoined;
            ClientGameController.Instance.OnJoinFailed += ShowStatus;
        }

        // 展示本机局域网地址，方便告诉其他玩家
        if (statusText != null)
            statusText.text = "本机局域网地址：" + NetGame.ServerSide.Server.GetLocalIPv4() +
                              (portInputField != null ? ":" + portInputField.text : "");
    }

    private void OnDestroy()
    {
        if (LobbyController.Instance != null)
            LobbyController.Instance.OnStatus -= ShowStatus;
    }

    private void OnCreatRoomClick()
    {
        int port = 10003;
        if (portInputField != null && !int.TryParse(portInputField.text.Trim(), out port))
            port = 10003;

        string username = usernameInputField != null ? usernameInputField.text.Trim() : "";
        if (string.IsNullOrEmpty(username))
            username = "Host_" + Random.Range(100, 999);

        if (LobbyController.Instance != null)
            LobbyController.Instance.StartHost(username, port);
        else
            ShowStatus("缺少 LobbyController");
    }

    private void OnStartGameClick()
    {
        if (ClientGameController.Instance != null)
            ClientGameController.Instance.SendGameStart();
    }

    private void OnJoined(int playerId, string username)
    {
        ShowStatus($"房间已创建，你是房主（ID={playerId}）");
    }

    private void ShowStatus(string s)
    {
        if (statusText != null) statusText.text = s;
    }
}
