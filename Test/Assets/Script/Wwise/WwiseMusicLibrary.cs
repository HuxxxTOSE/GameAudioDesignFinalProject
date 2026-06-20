using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// 【曲库数据管理 / 临时·可丢弃·交给程序重写】
/// 职责：扫描文件夹 → 维护曲目路径列表 → 填充 Dropdown / 响应选曲 → 把「完整路径」交给播放引擎。
/// 不含任何音频解码/播放逻辑——播放全在 WwiseAudioInputPlayer 里。
///
/// 这是 Demo 取数方式（用户手输文件夹路径）。正式项目里这一层大概率被替换为：
///   随包内置(StreamingAssets) / 存档配置 / 热更目录 等真正的来源策略 + 真正的播放列表 UI。
/// 替换时只需保证：最终拿到一条「能读到文件的完整路径」并调用 WwiseAudioInputPlayer.Play(path)。
/// </summary>
public class WwiseMusicLibrary : MonoBehaviour
{
    // 播放引擎已是静态单例（WwiseAudioInputPlayer.Play/Stop/IsPlaying），无需在此持有引用。

    [Header("曲库来源")]
    [Tooltip("路径输入框（TMP）。留空则用下面的默认文件夹。输入后按回车重新扫描。")]
    public TMPro.TMP_InputField folderInputField;
    [Tooltip("默认音乐文件夹（InputField 为空时使用）")]
    public string musicFolderPath = @"C:\Users\User\Music";
    [Tooltip("要扫描的扩展名")]
    public string[] extensions = { ".wav", ".mp3", ".ogg" };

    [Header("选曲 Dropdown（挂哪个就填哪个，二选一）")]
    public UnityEngine.UI.Dropdown uguiDropdown;
    public TMPro.TMP_Dropdown tmpDropdown;

    // 给机器用：完整路径列表，与 Dropdown 选项一一对应（下标即 Dropdown 序号）
    private readonly List<string> musicFiles = new List<string>();

    /// <summary>转发引擎播放状态，供按钮等 UI 使用。</summary>
    public bool IsPlaying => WwiseAudioInputPlayer.IsPlaying;

    private void Start()
    {
        RefreshLibrary();
        BindDropdown();
        if (folderInputField != null)
            folderInputField.onEndEdit.AddListener(_ => RefreshLibrary()); // 输入路径回车后重新扫描
    }

    // ============================================================
    //  扫描 / 填充
    // ============================================================
    /// <summary>路径来源：InputField 有内容用它，否则用默认文件夹。</summary>
    private string ResolveFolderPath()
    {
        if (folderInputField != null && !string.IsNullOrWhiteSpace(folderInputField.text))
            return folderInputField.text.Trim();
        return musicFolderPath;
    }

    /// <summary>重新扫描文件夹并刷新 Dropdown 列表（路径变更时调用）。</summary>
    public void RefreshLibrary()
    {
        ScanFolder();
        PopulateDropdown();
    }

    private void ScanFolder()
    {
        musicFiles.Clear();
        string folder = ResolveFolderPath();
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            Debug.LogError($"[MusicLibrary] 音乐文件夹不存在: {folder}");
            return;
        }

        foreach (var path in Directory.GetFiles(folder))
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            foreach (var e in extensions)
            {
                if (ext == e) { musicFiles.Add(path); break; }
            }
        }
        musicFiles.Sort();
        Debug.Log($"[MusicLibrary] 在 {folder} 扫描到 {musicFiles.Count} 个音频文件");
    }

    private void PopulateDropdown()
    {
        var names = new List<string>();
        foreach (var f in musicFiles)
            names.Add(Path.GetFileName(f)); // 给你看：纯文件名

        if (tmpDropdown != null)
        {
            tmpDropdown.ClearOptions();
            tmpDropdown.AddOptions(names);
            tmpDropdown.value = 0;
            tmpDropdown.RefreshShownValue();
        }
        if (uguiDropdown != null)
        {
            uguiDropdown.ClearOptions();
            uguiDropdown.AddOptions(names);
            uguiDropdown.value = 0;
            uguiDropdown.RefreshShownValue();
        }
    }

    private void BindDropdown()
    {
        if (tmpDropdown != null) tmpDropdown.onValueChanged.AddListener(OnDropdownChanged);
        if (uguiDropdown != null) uguiDropdown.onValueChanged.AddListener(OnDropdownChanged);
    }

    private int SelectedIndex
    {
        get
        {
            if (tmpDropdown != null) return tmpDropdown.value;
            if (uguiDropdown != null) return uguiDropdown.value;
            return 0;
        }
    }

    // ============================================================
    //  对外（按钮 / Dropdown）→ 翻译成路径 → 交给引擎
    // ============================================================
    /// <summary>播放/停止切换（供按钮调用）。</summary>
    public void TogglePlayStop()
    {
        if (!WwiseAudioInputPlayer.IsPlaying) PlaySelected();
        else WwiseAudioInputPlayer.Stop();
    }

    /// <summary>播放 Dropdown 当前选中的曲目（把序号翻译成完整路径后交给引擎）。</summary>
    public void PlaySelected()
    {
        if (musicFiles.Count == 0) { Debug.LogWarning("[MusicLibrary] 没有可播放的文件"); return; }
        int idx = Mathf.Clamp(SelectedIndex, 0, musicFiles.Count - 1);
        WwiseAudioInputPlayer.Play(musicFiles[idx]); // 完整路径 → 引擎（正在播则交叉淡变）
    }

    private void OnDropdownChanged(int index)
    {
        if (!WwiseAudioInputPlayer.IsPlaying) return; // 没在播就只记住选择，等点播放
        PlaySelected();                                // 正在播 → 交叉淡变到新选中曲
    }
}
