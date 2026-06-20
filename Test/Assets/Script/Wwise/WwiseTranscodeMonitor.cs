using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

/// <summary>
/// 实时显示音频转码的内存占用（以及总托管内存做对照）到一个 UI 文本。
/// "转码缓冲" = WwiseAudioInputPlayer 当前用于解码/流式播放的缓冲字节数，
/// 用环形缓冲流式后这个值很小且基本恒定，与歌曲长短无关。
/// </summary>
public class WwiseTranscodeMonitor : MonoBehaviour
{
    // 转码缓冲字节数从静态单例 WwiseAudioInputPlayer.TranscodeMemoryBytes 读取，无需在此持有引用。

    [Header("显示文本（挂哪个填哪个）")]
    public UnityEngine.UI.Text label;        // uGUI Text
    public TMPro.TextMeshProUGUI labelTMP;    // TMP Text

    [Header("刷新间隔（秒）")]
    public float refreshInterval = 0.5f;

    private float timer;
    private readonly StringBuilder sb = new StringBuilder(160);

    void Update()
    {
        timer += Time.unscaledDeltaTime;
        if (timer < refreshInterval) return;
        timer = 0f;

        long transcode = WwiseAudioInputPlayer.TranscodeMemoryBytes;
        long mono = Profiler.GetMonoUsedSizeLong();          // 托管堆已用
        long totalReserved = Profiler.GetTotalReservedMemoryLong(); // Unity 总预留

        sb.Clear();
        sb.Append("Transcode: ").Append(FormatBytes(transcode)).Append('\n');
        sb.Append("Managed:   ").Append(FormatBytes(mono)).Append('\n');
        sb.Append("Reserved:  ").Append(FormatBytes(totalReserved));

        string text = sb.ToString();
        if (label != null) label.text = text;
        if (labelTMP != null) labelTMP.text = text;
    }

    private static string FormatBytes(long b)
    {
        if (b < 1024) return b + " B";
        if (b < 1024L * 1024L) return (b / 1024f).ToString("F1") + " KB";
        return (b / (1024f * 1024f)).ToString("F2") + " MB";
    }
}
