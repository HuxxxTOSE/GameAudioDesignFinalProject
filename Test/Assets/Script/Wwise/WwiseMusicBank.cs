using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 【随包音频注册表 / 给程序按名字点播】
/// 职责：在 Inspector 维护一张「名字 → AudioClip」表（这些 clip 是 Unity 导入的资源，随包打进 build）。
/// 程序只需 WwiseMusicBank.Play("boss")，内部查到 clip 后交给播放引擎 WwiseAudioInputPlayer.Play(clip)
/// （经 Wwise Audio Input 出声、与路径曲目互通交叉淡变）。
///
/// 与 WwiseMusicLibrary 的区别：那个是「扫描磁盘文件夹 → 路径」的来源层；
/// 这个是「随包资源 AudioClip → 名字」的来源层。两者最终都调同一个引擎。
///
/// 用法：场景里挂一个本组件，把音频资源拖进 clips 列表并各起一个名字即可。
/// </summary>
public class WwiseMusicBank : MonoBehaviour
{
    [System.Serializable]
    public struct Entry
    {
        [Tooltip("程序点播用的名字，如 boss / menu / field")]
        public string name;
        [Tooltip("随包的音频资源（导入设置用 Decompress On Load 或 Compressed In Memory，勿用 Streaming）")]
        public AudioClip clip;
    }

    [Header("随包音频（名字 → AudioClip）")]
    public Entry[] clips;

    private static WwiseMusicBank _instance;
    private readonly Dictionary<string, AudioClip> map = new Dictionary<string, AudioClip>();

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning($"[MusicBank] 场景中存在多个 WwiseMusicBank，保留先注册的那个，销毁多余的：{name}");
            Destroy(this);
            return;
        }
        _instance = this;

        map.Clear();
        if (clips != null)
        {
            foreach (var e in clips)
            {
                if (e.clip == null || string.IsNullOrEmpty(e.name)) continue;
                if (map.ContainsKey(e.name))
                    Debug.LogWarning($"[MusicBank] 名字重复，后者覆盖前者：{e.name}");
                map[e.name] = e.clip;
            }
        }
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    // ============================================================
    //  对外静态接口（程序端只用这几个）
    // ============================================================
    /// <summary>按名字播放随包音频（走 Wwise Audio Input，正在播则交叉淡变切过去）。</summary>
    public static void Play(string name)
    {
        if (_instance == null)
        {
            Debug.LogError("[MusicBank] 场景里没有 WwiseMusicBank，无法按名字播放。请挂一个并在 Inspector 配好 clips。");
            return;
        }
        if (!_instance.map.TryGetValue(name, out var clip))
        {
            Debug.LogError($"[MusicBank] 没有名为「{name}」的音频，请在 Inspector 的 clips 列表里注册。");
            return;
        }
        WwiseAudioInputPlayer.Play(clip);
    }

    /// <summary>带淡出停止当前曲目（转发给引擎，与路径曲目共用同一个引擎）。</summary>
    public static void Stop() => WwiseAudioInputPlayer.Stop();

    /// <summary>当前是否在播放（转发给引擎）。</summary>
    public static bool IsPlaying => WwiseAudioInputPlayer.IsPlaying;
}
