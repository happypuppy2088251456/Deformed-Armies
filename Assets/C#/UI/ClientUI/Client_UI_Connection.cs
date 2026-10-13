using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class ConnectionUI : MonoBehaviour
{
    [Header("输入框")]
    public InputField ipInputField;

    [Header("按钮")]
    public Button confirmButton;

    [Header("文本")]
    public Text stateText;

    private void Start()
    {
        // 初始化输入框默认值
        if (ipInputField != null)
        {
            ipInputField.text = "";
            if (ipInputField.placeholder is Text placeholderText)
            {
                placeholderText.text = "Enter host IP...";
            }
        }

        // 初始化状态文本
        if (stateText != null)
        {
            stateText.text = "";
            stateText.color = Color.white;
        }

        // 绑定确认按钮事件
        if (confirmButton != null)
        {
            confirmButton.onClick.RemoveAllListeners();
            confirmButton.onClick.AddListener(OnConfirmClick);
        }

    }

    private void OnConfirmClick()
    {
        string ip = ipInputField.text.Trim();

        if (string.IsNullOrEmpty(ip))
        {
            //ShowState(stateText, "IP地址不能为空！", Color.yellow);
            return;
        }

        if (!IsValidIP(ip))
        {
            //ShowState(stateText, "IP地址格式错误！", Color.red);
            return;
        }

        //ShowState(stateText, "正在连接到服务器: " + ip + " ...", Color.cyan);
        confirmButton.interactable = false;

        // TODO: NetworkManager.instance.ConnectToServer(ip);
        Invoke(nameof(OnConnectSuccess), 1.5f);
    }

    private void OnConnectSuccess()
    {
        //ShowState(stateText, "连接成功！", Color.green);
        confirmButton.interactable = true;
        // TODO: SceneManager.LoadScene("GameScene");
    }

    private bool IsValidIP(string ip)
    {
        string[] parts = ip.Split('.');
        if (parts.Length != 4) return false;

        foreach (string part in parts)
        {
            if (!int.TryParse(part, out int value))
                return false;
            if (value < 0 || value > 255)
                return false;
        }
        return true;
    }
}