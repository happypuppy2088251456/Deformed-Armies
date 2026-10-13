using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class Sever_UI_CreatRoom :MonoBehaviour
{
    [Header("按钮")]
    public Button creatRoomButton;
    public Button startGameButton;


    private void Start()
    {

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
    }

    private void OnCreatRoomClick()
    {
        Debug.Log("[Server] 创建房间...");
        // TODO: NetworkManager.instance.CreateRoom();
    }

    private void OnStartGameClick()
    {
        Debug.Log("[Server] 开始游戏...");
        // TODO: SceneManager.LoadScene("GameScene");
    }
}