#if UNITY_EDITOR
using UnityEditor;
#endif
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using TMPro;
using UIWrapper;
using UnityEngine.Audio; 

public class UIManager : MonoBehaviour
{
    #region 面板绑定
    //绑定“主菜单” “主机/客户端选择面板” “建房面板” “进房面板” “设置面板”
    [UIBind("Canvas/GameMenuGroup/MainMenu_Panel")]
    private UIPanel MainMenu_Panel;

    [UIBind("Canvas/GameMenuGroup/Panel_HostOrClient")]
    private UIPanel Panel_HostOrClient;

    [UIBind("Canvas/GameMenuGroup/Panel_Host")]
    private UIPanel Panel_Host;

    [UIBind("Canvas/GameMenuGroup/Panel_Client")]
    private UIPanel Panel_Client;

    [UIBind("Canvas/SettingGroup/SettingPanel")]
    private UIPanel SettingPanel;
    #endregion

    #region 按钮绑定
    //绑定全部按钮，UIBottom封装了OnClick避免手写
    [UIBind("Canvas/GameMenuGroup/MainMenu_Panel/Btn_EnterGame")]
    private UIButton Btn_EnterGame;

    [UIBind("Canvas/GameMenuGroup/MainMenu_Panel/Btn_Setting")]
    private UIButton Btn_Setting;

    [UIBind("Canvas/GameMenuGroup/MainMenu_Panel/Btn_Quit")]
    private UIButton Btn_Quit;

    [UIBind("Canvas/SettingGroup/SettingPanel/Btn_Close")]
    private UIButton Btn_Close;

    [UIBind("Canvas/GameMenuGroup/Panel_HostOrClient/Btn_Host")]
    private UIButton Btn_Host;

    [UIBind("Canvas/GameMenuGroup/Panel_HostOrClient/Btn_Client")]
    private UIButton Btn_Client;

    [UIBind("Canvas/GameMenuGroup/Panel_HostOrClient/Btn_Back")]
    private UIButton Btn_Back_HostOrClient;

    [UIBind("Canvas/GameMenuGroup/Panel_Host/Btn_CreateRoom")]
    private UIButton Btn_CreateRoom;

    [UIBind("Canvas/GameMenuGroup/Panel_Host/Btn_Back")]
    private UIButton Btn_Back_Host;

    [UIBind("Canvas/GameMenuGroup/Panel_Client/Btn_Back")]
    private UIButton Btn_Back_Client;
    #endregion

    #region 设置面板控件绑定

    [Header("Audio Mixer")]
    [SerializeField] private AudioMixer audioMixer;

    private const string MIXER_MASTER = "MasterVolume";
    private const string MIXER_BGM = "BGMVolume";
    private const string MIXER_SFX = "SFXVolume";

    [UIBind("Canvas/SettingGroup/SettingPanel/Dropdown_Resolution")]
    private UIDropdown Dropdown_Resolution;

    [UIBind("Canvas/SettingGroup/SettingPanel/Dropdown_Quality")]
    private UIDropdown Dropdown_Quality;

    [UIBind("Canvas/SettingGroup/SettingPanel/Slider_MasterVolume",
        "Canvas/SettingGroup/SettingPanel/Txt_MasterVolume",
        "Master Volume: {0:F2}")]
    private UISlider Slider_MasterVolume;

    [UIBind("Canvas/SettingGroup/SettingPanel/Slider_BGMVolume",
        "Canvas/SettingGroup/SettingPanel/Txt_BGMVolume",
        "BGM Volume: {0:F2}")]
    private UISlider Slider_BGMVolume;

    [UIBind("Canvas/SettingGroup/SettingPanel/Slider_SFXVolume",
        "Canvas/SettingGroup/SettingPanel/Txt_SFXVolume",
        "SFX Volume: {0:F2}")]
    private UISlider Slider_SFXVolume;
    #endregion

    // 全局控件注册表字典，统一管理UI
    private readonly Dictionary<string, UIElement> _uiRegistry = new Dictionary<string, UIElement>();

    // 预设的 4 种分辨率
    private struct CustomResolution
    {
        public int width;
        public int height;
        public CustomResolution(int w, int h)
        {
            width = w;
            height = h;
        }
    }

    private readonly List<CustomResolution> _targetResolutions = new List<CustomResolution>
    {
        new CustomResolution(1920, 1080),
        new CustomResolution(2560, 1440),
        new CustomResolution(3840, 2160),
        new CustomResolution(1366, 768)
    };

//自动UI绑定
    private void Awake()
    {
        AutoBindUI();
    }

//初始化
    private void Start()
    {
        SettingPanel?.Hide();
        MainMenu_Panel?.Show();
        Panel_HostOrClient?.Hide();
        Panel_Host?.Hide();
        Panel_Client?.Hide();

        RegisterEvents();

        InitResolutionDropdown();
        InitQualityDropdown();
        LoadSettings();
    }

    #region 自动查找、实例化包装类与同步写入字典
    private void AutoBindUI()
    {
        FieldInfo[] fields = GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        foreach (var field in fields)
        {
            var attr = field.GetCustomAttribute<UIBindAttribute>();
            if (attr == null) continue;

            GameObject go = GameObject.Find(attr.Path);
            if (go == null)
            {
                Debug.LogError($"[UIManager] 找不到 UI 路径物体：{attr.Path}，请检查 Hierarchy 名称与路径是否一致！");
                continue;
            }

            UIElement elementInstance = null;
            Type fieldType = field.FieldType;

            if (fieldType == typeof(UIPanel))
            {
                elementInstance = new UIPanel(go);
            }
            else if (fieldType == typeof(UIButton))
            {
                elementInstance = new UIButton(go);
            }
            else if (fieldType == typeof(UIToggle))
            {
                elementInstance = new UIToggle(go);
            }
            else if (fieldType == typeof(UIDropdown))
            {
                elementInstance = new UIDropdown(go);
            }
            else if (fieldType == typeof(UIInputField))
            {
                elementInstance = new UIInputField(go);
            }
            else if (fieldType == typeof(UISlider))
            {
                TextMeshProUGUI textComp = null;
                if (!string.IsNullOrEmpty(attr.AssociatedTextPath))
                {
                    GameObject txtGo = GameObject.Find(attr.AssociatedTextPath);
                    if (txtGo != null)
                        textComp = txtGo.GetComponent<TextMeshProUGUI>();
                }
                elementInstance = new UISlider(go, textComp, attr.TextFormat);
            }

            if (elementInstance != null)
            {
                field.SetValue(this, elementInstance);
                _uiRegistry[field.Name] = elementInstance;
            }
        }
    }

    public T GetUI<T>(string elementName) where T : UIElement
    {
        if (_uiRegistry.TryGetValue(elementName, out var element))
        {
            return element as T;
        }
        Debug.LogWarning($"[UIManager] 未在字典中找到控件：{elementName}");
        return null;
    }
    #endregion

    #region 注册事件
    private void RegisterEvents()
    {
        Btn_EnterGame?.OnClick(OpenHostOrClientPanel);
        Btn_Setting?.OnClick(OpenSettingPanel);
        Btn_Close?.OnClick(CloseSettingPanel);
        Btn_Quit?.OnClick(QuitGame);

        Btn_Host?.OnClick(OpenHostPanel);
        Btn_Client?.OnClick(OpenClientPanel);
        Btn_Back_HostOrClient?.OnClick(BackToMainMenu);

        Btn_Back_Host?.OnClick(BackToHostOrClientFromHost);
        Btn_Back_Client?.OnClick(BackToHostOrClientFromClient);

        Btn_CreateRoom?.OnClick(() => Debug.Log("点击创建房间"));

        Slider_MasterVolume?.OnValueChanged(val => {
            SetAudioMixerVolume(MIXER_MASTER, val);
            PlayerPrefs.SetFloat("MasterVolume", val);
        });
        
        Slider_BGMVolume?.OnValueChanged(val => {
            SetAudioMixerVolume(MIXER_BGM, val);
            PlayerPrefs.SetFloat("BGMVolume", val);
        });
        
        Slider_SFXVolume?.OnValueChanged(val => {
            SetAudioMixerVolume(MIXER_SFX, val);
            PlayerPrefs.SetFloat("SFXVolume", val);
        });

        Dropdown_Resolution?.OnValueChanged(OnResolutionChange);
        Dropdown_Quality?.OnValueChanged(OnQualityChange);
    }
    #endregion

    #region 界面控制与逻辑
    public void OpenSettingPanel() => SettingPanel?.Show();

    public void CloseSettingPanel()
    {
        SettingPanel?.Hide();
        SaveSettings();
    }

    public void OpenHostOrClientPanel()
    {
        MainMenu_Panel?.Hide();
        Panel_HostOrClient?.Show();
    }

    public void OpenHostPanel()
    {
        Panel_HostOrClient?.Hide();
        Panel_Host?.Show();
    }

    public void OpenClientPanel()
    {
        Panel_HostOrClient?.Hide();
        Panel_Client?.Show();
    }

    public void BackToMainMenu()
    {
        Panel_HostOrClient?.Hide();
        MainMenu_Panel?.Show();
    }

    public void BackToHostOrClientFromHost()
    {
        Panel_Host?.Hide();
        Panel_HostOrClient?.Show();
    }

    public void BackToHostOrClientFromClient()
    {
        Panel_Client?.Hide();
        Panel_HostOrClient?.Show();
    }

    public void QuitGame()
    {
#if UNITY_EDITOR
        EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
    #endregion

    #region 设置保存与加载、分辨率、画质、音量逻辑
    private void InitResolutionDropdown()
    {
        if (Dropdown_Resolution == null) return;

        Dropdown_Resolution.Component.ClearOptions();
        List<string> optList = new List<string>();
        int currentIndex = 0;

        int currentW = Screen.width;
        int currentH = Screen.height;

        for (int i = 0; i < _targetResolutions.Count; i++)
        {
            var res = _targetResolutions[i];
            optList.Add($"{res.width} * {res.height}");

            if (res.width == currentW && res.height == currentH)
            {
                currentIndex = i;
            }
        }

        Dropdown_Resolution.Component.AddOptions(optList);
        Dropdown_Resolution.Value = currentIndex;
    }

    private void SetAudioMixerVolume(string parameterName, float linearValue)
    {
        if (audioMixer == null) return;
        
        // 防止 Log10(0) 出现负无穷错误，将最小值限制在 0.0001
        float clampedValue = Mathf.Clamp(linearValue, 0.0001f, 1f);
        float db = Mathf.Log10(clampedValue) * 20f;
        
        audioMixer.SetFloat(parameterName, db);
    }

    private void OnResolutionChange(int idx)
    {
        if (idx < 0 || idx >= _targetResolutions.Count) return;

        var res = _targetResolutions[idx];
        Screen.SetResolution(res.width, res.height, Screen.fullScreenMode);
    }

    private void InitQualityDropdown()
    {
        if (Dropdown_Quality == null) return;
        Dropdown_Quality.Component.ClearOptions();
        Dropdown_Quality.Component.AddOptions(new List<string>(QualitySettings.names));
        Dropdown_Quality.Value = QualitySettings.GetQualityLevel();
    }

    private void OnQualityChange(int index)
    {
        QualitySettings.SetQualityLevel(index, true);
    }

    private void SaveSettings()
    {
        PlayerPrefs.SetFloat("MasterVolume", AudioListener.volume);
        PlayerPrefs.SetInt("QualityLevel", QualitySettings.GetQualityLevel());
        PlayerPrefs.Save();
    }

    private void LoadSettings()
    {
        float masterVol = PlayerPrefs.GetFloat("MasterVolume", 1f);
        AudioListener.volume = masterVol;
        if (Slider_MasterVolume != null)
        {
            Slider_MasterVolume.Value = masterVol;
            Slider_MasterVolume.UpdateText(masterVol);
        }

        int qLv = PlayerPrefs.GetInt("QualityLevel", QualitySettings.GetQualityLevel());
        QualitySettings.SetQualityLevel(qLv);
        if (Dropdown_Quality != null) Dropdown_Quality.Value = qLv;

        if (Slider_BGMVolume != null)
        {
            Slider_BGMVolume.Value = 1f;
            Slider_BGMVolume.UpdateText(1f);
        }
        if (Slider_SFXVolume != null)
        {
            Slider_SFXVolume.Value = 1f;
            Slider_SFXVolume.UpdateText(1f);
        }
    }
    #endregion
}