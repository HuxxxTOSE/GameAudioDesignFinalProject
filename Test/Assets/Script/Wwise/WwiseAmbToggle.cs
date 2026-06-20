using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Button))] // 确保挂载此脚本的物体一定有 Button 组件
public class WwiseAmbToggle : MonoBehaviour
{
    [Header("Wwise 事件名称")]
    public string playEventName = "Play_Amb";
    public string stopEventName = "Stop_Amb";

    // 状态标记：0 = 停止状态（默认），1 = 播放状态
    private int clickFlag = 0;
    private Button button;

    void Start()
    {
        button = GetComponent<Button>();

        // 绑定按钮点击事件
        if (button != null)
        {
            button.onClick.AddListener(OnButtonClicked);
        }
    }

    void OnButtonClicked()
    {
        if (clickFlag == 0)
        {
            // 默认/标记为0时点击：触发播放，并将标记改为 1
            AkUnitySoundEngine.PostEvent(playEventName, gameObject);
            clickFlag = 1;

            Debug.Log($"[Wwise] 已调用 {playEventName}，当前标记更改为: {clickFlag}");
        }
        else
        {
            // 标记为1时点击：触发停止，并将标记重置为 0
            AkUnitySoundEngine.PostEvent(stopEventName, gameObject);
            clickFlag = 0;

            Debug.Log($"[Wwise] 已调用 {stopEventName}，当前标记更改为: {clickFlag}");
        }
    }
}
