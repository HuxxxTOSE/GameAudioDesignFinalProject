using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 极简的播放/停止按钮：点击时调用 WwiseMusicLibrary.TogglePlayStop()。
/// 「播放/停止切换」需要知道选了哪首，属于曲库层职责，所以这里引用曲库（不是播放引擎）。
/// </summary>
[RequireComponent(typeof(Button))]
public class WwisePlayStopBotton : MonoBehaviour
{
    [Header("曲库引用（选曲/切歌都在它上面）")]
    public WwiseMusicLibrary library;

    [Header("可选：按钮文字（挂哪个填哪个）")]
    public Text buttonLabel;                     // uGUI Text
    public TMPro.TextMeshProUGUI buttonLabelTMP; // TMP Text

    private Button button;
    private bool lastState;

    void Start()
    {
        button = GetComponent<Button>();
        if (button != null)
            button.onClick.AddListener(OnClicked);
        UpdateLabel();
    }

    void Update()
    {
        // 跟随播放状态刷新文字（例如曲子自然播完后自动回到 "Play"）
        if (library != null && library.IsPlaying != lastState)
            UpdateLabel();
    }

    void OnClicked()
    {
        if (library == null)
        {
            Debug.LogError("[PlayStop] 未指定 library");
            return;
        }
        library.TogglePlayStop();
        UpdateLabel();
    }

    void UpdateLabel()
    {
        bool playing = library != null && library.IsPlaying;
        lastState = playing;
        string txt = playing ? "Stop" : "Play";
        if (buttonLabel != null) buttonLabel.text = txt;
        if (buttonLabelTMP != null) buttonLabelTMP.text = txt;
    }
}
