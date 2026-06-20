using UnityEngine;
using UnityEngine.UI;

public class WwiseParameterManager : MonoBehaviour
{
    // 定义一个结构体，方便在 Inspector 中成对配置
    [System.Serializable]
    public struct SliderRTPCBinding
    {
        [Tooltip("Unity 的滑动条组件")]
        public Slider slider;
        [Tooltip("Wwise 中对应的 Game Parameter (RTPC) 名称")]
        public string wwiseParameterName;
    }

    [Header("Wwise 参数绑定列表")]
    public SliderRTPCBinding[] parameterBindings;

    void Start()
    {
        // 遍历所有配置好的绑定项
        foreach (var binding in parameterBindings)
        {
            // 安全检查：确保滑动条和名字都不为空
            if (binding.slider != null && !string.IsNullOrEmpty(binding.wwiseParameterName))
            {
                // 1. 游戏刚启动时，先将滑动条当前的默认值发送给 Wwise 一次
                AkUnitySoundEngine.SetRTPCValue(binding.wwiseParameterName, binding.slider.value);

                // 2. 局部变量捕获，防止 Lambda 表达式在循环中出错
                string rtpcName = binding.wwiseParameterName;

                // 3. 监听滑动条的拖拽事件
                binding.slider.onValueChanged.AddListener((value) =>
                {
                    // 实时将新数值传递给对应的 Wwise 参数
                    AkUnitySoundEngine.SetRTPCValue(rtpcName, value);
                    Debug.Log($"[Wwise] 参数 {rtpcName} 已更新为: {value}");
                });
            }
            else
            {
                Debug.LogWarning("[WwiseManager] 发现未配置完全的滑动条绑定项！");
            }
        }
    }
}