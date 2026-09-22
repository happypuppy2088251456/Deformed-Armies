using NetGame.Unity;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 客户端面板：输入 IP:端口（与可选用户名）→ 连接到主机服务端并加入房间。
/// </summary>
public class Client_UI_Connection : MonoBehaviour
{
    [Header("输入")]
    public InputField ipInputField;        // 支持 "IP" 或 "IP:端口"
    public InputField usernameInputField;

    [Header("按钮")]
    public Button confirmButton;

    [Header("状态")]
    public Text stateText;

    private void Start()
    {
        if (ipInputField != null && string.IsNullOrEmpty(ipInputField.text))
            ipInputField.text = "127.0.0.1:10003";

        if (confirmButton != null)
        {
            confirmButton.onClick.RemoveAllListeners();
            confirmButton.onClick.AddListener(OnConfirmClick);
        }

        if (stateText != null) stateText.text = "";

        if (LobbyController.Instance != null)
            LobbyController.Instance.OnStatus += ShowState;

        if (ClientGameController.Instance != null)
        {
            ClientGameController.Instance.OnJoinedRoom += OnJoined;
            ClientGameController.Instance.OnJoinFailed += ShowState;
            ClientGameController.Instance.OnLeftRoom += OnLeft;
        }
    }

    private void OnDestroy()
    {
        if (LobbyController.Instance != null)
            LobbyController.Instance.OnStatus -= ShowState;
    }

    private void OnConfirmClick()
    {
        string host = ipInputField != null ? ipInputField.text.Trim() : "";
        if (!TryParseHostPort(host, out string ip, out int port))
        {
            ShowState("格式应为 IP:端口，例如 192.168.1.5:10003");
            return;
        }

        string username = usernameInputField != null ? usernameInputField.text.Trim() : "";
        if (string.IsNullOrEmpty(username))
            username = "Client_" + Random.Range(100, 999);

        ShowState("正在连接 " + ip + ":" + port + " ...");
        if (confirmButton != null) confirmButton.interactable = false;

        if (LobbyController.Instance != null)
            LobbyController.Instance.StartClient(ip, port, username);
        else
            ShowState("缺少 LobbyController");
    }

    private void OnJoined(int playerId, string username)
    {
        ShowState("加入成功，我的 ID=" + playerId);
        if (confirmButton != null) confirmButton.interactable = true;
    }

    private void OnLeft()
    {
        ShowState("已离开房间 / 连接断开");
        if (confirmButton != null) confirmButton.interactable = true;
    }

    private void ShowState(string s)
    {
        if (stateText != null) stateText.text = s;
    }

    private static bool TryParseHostPort(string host, out string ip, out int port)
    {
        ip = null;
        port = 10003;

        if (string.IsNullOrEmpty(host)) return false;
        host = host.Trim();

        int idx = host.LastIndexOf(':');
        if (idx > 0 && host.IndexOf(':') == idx)
        {
            string ipPart = host.Substring(0, idx);
            string portPart = host.Substring(idx + 1);
            if (!int.TryParse(portPart, out port)) return false;
            if (ipPart == "localhost") { ip = "127.0.0.1"; return true; }
            ip = ipPart;
            return IsValidIpv4(ip);
        }

        ip = host == "localhost" ? "127.0.0.1" : host;
        return IsValidIpv4(ip);
    }

    private static bool IsValidIpv4(string ip)
    {
        string[] parts = ip.Split('.');
        if (parts.Length != 4) return false;
        foreach (string part in parts)
        {
            if (!int.TryParse(part, out int value)) return false;
            if (value < 0 || value > 255) return false;
        }
        return true;
    }
}
