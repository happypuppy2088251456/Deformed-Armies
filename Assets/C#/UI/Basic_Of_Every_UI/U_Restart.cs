using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class UI_Restart : MonoBehaviour
{
    public GameObject RestartPannel;
    public GameObject otherPannel1;
    public GameObject otherPannel2;
    void Awake()
    {
        RestartPannel.SetActive(true);
        otherPannel1.SetActive(false);
        otherPannel2.SetActive(false);
    }
}
