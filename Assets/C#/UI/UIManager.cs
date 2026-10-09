#if UNITY_EDITOR
using UnityEditor;
#endif

using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
public class UIManager : MonoBehaviour
{
    [Header("UI引用 - 设置面板")]
    public Button Btn_Setting;
    public GameObject SettingPanel;
    public Button Btn_Close;
    public TMP_Dropdown Dropdown_Resolution;
    public Toggle Toggle_FullScreen;
    public TMP_Dropdown Dropdown_Quality;
    public Slider Slider_MasterVolume;
    public TextMeshProUGUI Txt_MasterVolume;
    public Slider Slider_BGMVolume;
    public Slider Slider_SFXVolume;
    public TextMeshProUGUI Txt_SFXVolume;
    public TextMeshProUGUI Txt_BGMVolume;

    [Header("UI引用 - 联机菜单")]
    public GameObject MainMenu_Panel;
    public GameObject Panel_HostOrClie;
    public GameObject Panel_Host;
    public GameObject Panel_Client;

    // 分辨率列表
    private List<Resolution> resList;

    void Start()
    {
        SettingPanel.SetActive(false);
        // 初始状态：打开主菜单，关闭其他联机面板
        MainMenu_Panel.SetActive(true);
        Panel_HostOrClie.SetActive(false);
        Panel_Host.SetActive(false);
        Panel_Client.SetActive(false);

        InitResolutionDropdown();
        InitFullScreenToggle();
        InitVolumeSlider();
        InitQualityDropdown();
        LoadSettings();
    }

    #region 设置面板开关
    public void OpenSettingPanel()
    {
        SettingPanel.SetActive(true);
    }
    public void CloseSettingPanel()
    {
        SettingPanel.SetActive(false);
        SaveSettings();
    }
    #endregion

    #region 页面跳转逻辑
    // 主菜单 → 进入游戏页面（Panel_HostOrClie）
    public void OpenHostOrClientPanel()
    {
        MainMenu_Panel.SetActive(false);
        Panel_HostOrClie.SetActive(true);
    }

    // HostOrClient页面 → 主机页面
    public void OpenHostPanel()
    {
        Panel_HostOrClie.SetActive(false);
        Panel_Host.SetActive(true);
    }

    // HostOrClient页面 → 客户端页面
    public void OpenClientPanel()
    {
        Panel_HostOrClie.SetActive(false);
        Panel_Client.SetActive(true);
    }

    // 从HostOrClient返回主菜单
    public void BackToMainMenu()
    {
        Panel_HostOrClie.SetActive(false);
        MainMenu_Panel.SetActive(true);
    }

    // 主机页面 返回 HostOrClient
    public void BackToHostOrClientFromHost()
    {
        Panel_Host.SetActive(false);
        Panel_HostOrClie.SetActive(true);
    }

    // 客户端页面 返回 HostOrClient
    public void BackToHostOrClientFromClient()
    {
        Panel_Client.SetActive(false);
        Panel_HostOrClie.SetActive(true);
    }

    // 创建房间按钮占位（后面写网络逻辑）
    public void OnCreateRoom()
    {
        Debug.Log("点击创建房间");
    }

    // 退出游戏
    public void QuitGame()
    {
#if UNITY_EDITOR
        // 在编辑器里：停止播放
        UnityEditor.EditorApplication.isPlaying = false;
#else
            // 打包后：直接关闭程序
            Application.Quit();
#endif
    }
    #endregion

    #region 分辨率
    void InitResolutionDropdown()
    {
        resList = new List<Resolution>(Screen.resolutions);
        Dropdown_Resolution.ClearOptions();
        List<string> resOption = new List<string>();
        int currentIndex = 0;
        for (int i = 0; i < resList.Count; i++)
        {
            var res = resList[i];
            string opt = $"{res.width} × {res.height}";
            resOption.Add(opt);
            if (res.width == Screen.currentResolution.width && res.height == Screen.currentResolution.height)
            {
                currentIndex = i;
            }
        }
        Dropdown_Resolution.AddOptions(resOption);
        Dropdown_Resolution.value = currentIndex;
        Dropdown_Resolution.onValueChanged.AddListener(OnResolutionChange);
    }
    void OnResolutionChange(int idx)
    {
        var res = resList[idx];
        Screen.SetResolution(res.width, res.height, Screen.fullScreenMode);
    }
    #endregion

    #region 全屏
    void InitFullScreenToggle()
    {
        Toggle_FullScreen.isOn = Screen.fullScreen;
        Toggle_FullScreen.onValueChanged.AddListener(OnFullScreenChange);
    }
    void OnFullScreenChange(bool isFull)
    {
        Screen.fullScreen = isFull;
    }
    #endregion

    #region 音量
    void InitVolumeSlider()
    {
        Slider_MasterVolume.value = AudioListener.volume;
        Slider_MasterVolume.onValueChanged.AddListener(OnMasterVolumeChange);
        Txt_MasterVolume.text = $"Master Volume: {Slider_MasterVolume.value:F2}";
        Slider_BGMVolume.onValueChanged.AddListener(OnBGMVolumeChange);
        Slider_SFXVolume.onValueChanged.AddListener(OnSFXVolumeChange);
    }
    void OnMasterVolumeChange(float val)
    {
        AudioListener.volume = val;
        Txt_MasterVolume.text = $"Master Volume: {val:F2}";
    }
    void OnBGMVolumeChange(float val)
    {
        Txt_BGMVolume.text = $"BGM Volume: {val:F2}";
    }
    void OnSFXVolumeChange(float val)
    {
        Txt_SFXVolume.text = $"SFX Volume: {val:F2}";
    }
    #endregion

    #region 画质
    void InitQualityDropdown()
    {
        Dropdown_Quality.ClearOptions();
        List<string> qualityNames = new List<string>(QualitySettings.names);
        Dropdown_Quality.AddOptions(qualityNames);
        Dropdown_Quality.value = QualitySettings.GetQualityLevel();
        Dropdown_Quality.onValueChanged.AddListener(OnQualityChange);
    }
    void OnQualityChange(int qualityIndex)
    {
        QualitySettings.SetQualityLevel(qualityIndex, true);
    }
    #endregion

    #region 保存读取设置
    void SaveSettings()
    {
        PlayerPrefs.SetFloat("MasterVolume", AudioListener.volume);
        PlayerPrefs.SetInt("FullScreen", Screen.fullScreen ? 1 : 0);
        PlayerPrefs.SetInt("QualityLevel", QualitySettings.GetQualityLevel());
        PlayerPrefs.Save();
    }
    void LoadSettings()
    {
        float masterVol = PlayerPrefs.GetFloat("MasterVolume", 1f);
        AudioListener.volume = masterVol;
        Slider_MasterVolume.value = masterVol;

        bool fullScreen = PlayerPrefs.GetInt("FullScreen", Screen.fullScreen ? 1 : 0) == 1;
        Screen.fullScreen = fullScreen;
        Toggle_FullScreen.isOn = fullScreen;

        int qualityLv = PlayerPrefs.GetInt("QualityLevel", QualitySettings.GetQualityLevel());
        QualitySettings.SetQualityLevel(qualityLv);
        Dropdown_Quality.value = qualityLv;
    }
    #endregion
}