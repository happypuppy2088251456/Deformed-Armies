using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace UIWrapper
{
    /// <summary>
    /// UIBind 属性标注
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public class UIBindAttribute : Attribute
    {
        public string Path { get; }
        public string AssociatedTextPath { get; }
        public string TextFormat { get; }

        public UIBindAttribute(string path, string associatedTextPath = null, string textFormat = null)
        {
            Path = path;
            AssociatedTextPath = associatedTextPath;
            TextFormat = textFormat;
        }
    }

    /// <summary>
    /// UI 控件基类
    /// </summary>
    public abstract class UIElement
    {
        public GameObject GameObject { get; protected set; }
        public Transform Transform => GameObject?.transform;

        public UIElement(GameObject gameObject)
        {
            GameObject = gameObject;
        }

        public virtual void Show()
        {
            if (GameObject != null) GameObject.SetActive(true);
        }

        public virtual void Hide()
        {
            if (GameObject != null) GameObject.SetActive(false);
        }

        public bool IsActive => GameObject != null && GameObject.activeSelf;
    }

    /// <summary>
    /// 面板包装类
    /// </summary>
    public class UIPanel : UIElement
    {
        public UIPanel(GameObject gameObject) : base(gameObject) { }
    }

    /// <summary>
    /// 按钮包装类
    /// </summary>
    public class UIButton : UIElement
    {
        public Button Component { get; }

        public UIButton(GameObject gameObject) : base(gameObject)
        {
            Component = gameObject.GetComponent<Button>();
            if (Component == null)
            {
                Debug.LogError($"[UIButton] GameObject '{gameObject.name}' 上没有找到 Button 组件！");
            }
        }

        public void OnClick(Action action)
        {
            if (Component == null) return;
            Component.onClick.RemoveAllListeners();
            Component.onClick.AddListener(() => action?.Invoke());
        }

        public bool Interactable
        {
            get => Component != null && Component.interactable;
            set { if (Component != null) Component.interactable = value; }
        }
    }

    /// <summary>
    /// 开关/复选框包装类
    /// </summary>
    public class UIToggle : UIElement
    {
        public Toggle Component { get; }

        public UIToggle(GameObject gameObject) : base(gameObject)
        {
            Component = gameObject.GetComponent<Toggle>();
            if (Component == null)
            {
                Debug.LogError($"[UIToggle] GameObject '{gameObject.name}' 上没有找到 Toggle 组件！");
            }
        }

        public void OnValueChanged(Action<bool> onValueChanged)
        {
            if (Component == null) return;
            Component.onValueChanged.RemoveAllListeners();
            Component.onValueChanged.AddListener(val => onValueChanged?.Invoke(val));
        }

        public bool IsOn
        {
            get => Component != null && Component.isOn;
            set { if (Component != null) Component.isOn = value; }
        }
    }

    /// <summary>
    /// 下拉菜单包装类 (支持 TextMeshPro Dropdown)
    /// </summary>
    public class UIDropdown : UIElement
    {
        public TMP_Dropdown Component { get; }

        public UIDropdown(GameObject gameObject) : base(gameObject)
        {
            Component = gameObject.GetComponent<TMP_Dropdown>();
            if (Component == null)
            {
                Debug.LogError($"[UIDropdown] GameObject '{gameObject.name}' 上没有找到 TMP_Dropdown 组件！");
            }
        }

        public void OnValueChanged(Action<int> onValueChanged)
        {
            if (Component == null) return;
            Component.onValueChanged.RemoveAllListeners();
            Component.onValueChanged.AddListener(idx => onValueChanged?.Invoke(idx));
        }

        public int Value
        {
            get => Component != null ? Component.value : 0;
            set { if (Component != null) Component.value = value; }
        }
    }

    /// <summary>
    /// 滑块包装类（支持关联 TextMeshProUGUI 显示文本格式）
    /// </summary>
    public class UISlider : UIElement
    {
        public Slider Component { get; }
        public TextMeshProUGUI AssociatedText { get; }
        public string TextFormat { get; }

        public UISlider(GameObject gameObject, TextMeshProUGUI associatedText = null, string textFormat = null) : base(gameObject)
        {
            Component = gameObject.GetComponent<Slider>();
            AssociatedText = associatedText;
            TextFormat = textFormat;

            if (Component == null)
            {
                Debug.LogError($"[UISlider] GameObject '{gameObject.name}' 上没有找到 Slider 组件！");
            }
        }

        public void OnValueChanged(Action<float> onValueChanged)
        {
            if (Component == null) return;
            Component.onValueChanged.RemoveAllListeners();
            Component.onValueChanged.AddListener(val =>
            {
                UpdateText(val);
                onValueChanged?.Invoke(val);
            });
        }

        public float Value
        {
            get => Component != null ? Component.value : 0f;
            set
            {
                if (Component != null)
                {
                    Component.value = value;
                    UpdateText(value);
                }
            }
        }

        public void UpdateText(float value)
        {
            if (AssociatedText != null && !string.IsNullOrEmpty(TextFormat))
            {
                AssociatedText.text = string.Format(TextFormat, value);
            }
        }
    }

    /// <summary>
    /// 输入框包装类 (支持 TextMeshPro InputField)
    /// </summary>
    public class UIInputField : UIElement
    {
        public TMP_InputField Component { get; }

        public UIInputField(GameObject gameObject) : base(gameObject)
        {
            Component = gameObject.GetComponent<TMP_InputField>();
            if (Component == null)
            {
                Debug.LogError($"[UIInputField] GameObject '{gameObject.name}' 上没有找到 TMP_InputField 组件！");
            }
        }

        public void OnValueChanged(Action<string> onValueChanged)
        {
            if (Component == null) return;
            Component.onValueChanged.RemoveAllListeners();
            Component.onValueChanged.AddListener(text => onValueChanged?.Invoke(text));
        }

        public void OnEndEdit(Action<string> onEndEdit)
        {
            if (Component == null) return;
            Component.onEndEdit.RemoveAllListeners();
            Component.onEndEdit.AddListener(text => onEndEdit?.Invoke(text));
        }

        public string Text
        {
            get => Component != null ? Component.text : string.Empty;
            set { if (Component != null) Component.text = value; }
        }
    }
}