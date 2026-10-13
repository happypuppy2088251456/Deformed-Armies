using UnityEngine;
using UnityEngine.UI;

public class NewBehaviourScript : MonoBehaviour
{
    [Header("S/C")]
    public Button StartAsServer;   
    public Button StartAsClient;   

    public GameObject serverPanel;
    public GameObject clientPanel;

    private void Start()
    {
        if (StartAsServer != null)
        {
            StartAsServer.onClick.RemoveAllListeners();
            StartAsServer.onClick.AddListener(() => OnServerClick(serverPanel));
        }

        if (StartAsClient != null)
        {
            StartAsClient.onClick.RemoveAllListeners();
            StartAsClient.onClick.AddListener(() => OnClientClick(clientPanel));
        }
    }

    public void OnServerClick(GameObject target)
    {
        if (target == null)
        {
            Debug.LogWarning($"[{GetType().Name}] 目标为空", this);
            return;
        }
        target.SetActive(true);
        gameObject.SetActive(false);
    }

    public void OnClientClick(GameObject target)
    {
        if (target == null)
        {
            Debug.LogWarning($"[{GetType().Name}] 目标为空", this);
            return;
        }
        target.SetActive(true);
        gameObject.SetActive(false);
    }
}