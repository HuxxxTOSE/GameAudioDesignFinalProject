using UnityEngine;
using UnityEngine.UI;
using TMPro; // 如果你使用的是旧版Text，请把下面对应的TextMeshProUGUI改成Text

public class SliderValueToText : MonoBehaviour
{
    [Header("把显示数值的文本框拖到这里")]
    public TextMeshProUGUI targetText; 

    [Header("是否只显示整数？")]
    public bool showAsInteger = true;

    private Slider slider;

    void Start()
    {
        // 自动获取当前物体上的 Slider 组件
        slider = GetComponent<Slider>();

        if (slider != null && targetText != null)
        {
            // 1. 游戏刚开始时，先根据滑块当前值初始化文本
            UpdateText(slider.value);

            // 2. 监听滑块拖动事件，实时更新文本
            slider.onValueChanged.AddListener(UpdateText);
        }
    }

    void UpdateText(float value)
    {
        if (targetText != null)
        {
            // 根据勾选情况，决定显示整数还是两位小数
            targetText.text = showAsInteger ? 
                Mathf.RoundToInt(value).ToString() : 
                value.ToString("F2");
        }
    }
}