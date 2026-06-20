using System.Collections;
using System.Collections.Generic;
using System.IO;
using NLayer;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 【纯播放引擎 / 可复用移植】
/// 职责：接收一个本地音频文件路径 → 直接读取/解码 → 通过 Wwise Audio Input 流式播放。
/// 包含：双流交叉淡变、wav/mp3/ogg 解码、环形缓冲。
/// 不负责：曲库扫描、Dropdown、选曲、UI —— 那些在 WwiseMusicLibrary 里（可替换/丢弃）。
///
/// 对外只暴露（全部静态，程序端 WwiseAudioInputPlayer.Play(path) 直调）：
///   Play(path) / Stop() / IsPlaying / TranscodeMemoryBytes。
/// 静态调用会自动转发到「场景里那个配好 Wwise 事件的实例」（单例 Instance）。
///
/// 【交叉淡变模型】平时只有一条数据流（currentStream）。切歌时：
///   旧流 Post(Stop 事件) 开始淡出  +  新流 Post(Play 事件) 开始淡入  →  两条流同时喂数据（真重叠交叉淡变）
///   旧流淡出结束后（等 fadeOutSeconds）回收，立刻回到单流。即「双流」只在切歌的 crossfade 期间临时存在。
/// </summary>
public class WwiseAudioInputPlayer : MonoBehaviour
{
    [Header("Wwise 配置")]
    public AK.Wwise.Event audioInputEvent; // Play_CustomMusic（带淡入 fade in）
    public AK.Wwise.Event stopEvent;       // Stop_CustomMusic（带淡出 fade out）

    [Header("环形缓冲设置")]
    [Tooltip("环形缓冲时长（秒）。越大越抗卡顿，但越占内存。")]
    public float ringBufferSeconds = 1.0f;

    [Tooltip("旧曲淡出后多久回收资源（秒）—— 必须 ≥ Wwise 里 Stop 事件的淡出时长，否则尾巴被硬切。" +
             "这是回收延时，不阻塞新曲（新曲在切歌瞬间已起播淡入）。")]
    public float fadeOutSeconds = 1.0f;

    [Header("测试专用（可选，填你电脑上的真实音频路径，右键脚本菜单可试播）")]
    public string testAbsoluteFilePath;

    // ============================================================
    //  单例：场景里放一个配好 Wwise 事件的实例，程序端用静态接口直调
    // ============================================================
    private static WwiseAudioInputPlayer _instance;

    /// <summary>全局唯一播放器。优先用场景里已配好事件的那个（Awake 自动注册）；
    /// 都没有则自动创建一个——但它的 audioInputEvent/stopEvent 为空，播放会失败并报警。</summary>
    public static WwiseAudioInputPlayer Instance
    {
        get
        {
            if (_instance != null) return _instance;
            _instance = FindObjectOfType<WwiseAudioInputPlayer>();
            if (_instance == null)
            {
                var go = new GameObject("WwiseAudioInputPlayer (Auto)");
                _instance = go.AddComponent<WwiseAudioInputPlayer>();
                Debug.LogWarning("[CustomMusic] 场景里没有 WwiseAudioInputPlayer，已自动创建一个；" +
                    "但 audioInputEvent/stopEvent 未配置，播放会失败。建议在场景挂一个并在 Inspector 配好事件。");
            }
            return _instance;
        }
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning($"[CustomMusic] 场景中存在多个 WwiseAudioInputPlayer，保留先注册的那个，销毁多余的：{name}");
            Destroy(this);
            return;
        }
        _instance = this;
    }

    // ============================================================
    //  对外静态接口（程序端只用这 4 个）
    // ============================================================
    /// <summary>播放本地音频文件（绝对路径，wav/mp3/ogg）。正在播则与当前曲目交叉淡变切过去。</summary>
    public static void Play(string filePath) => Instance.PlayInternal(filePath);

    /// <summary>播放随包的 Unity AudioClip（取整段 PCM 喂 Wwise Audio Input）。正在播则交叉淡变。
    /// clip 导入设置需用 Decompress On Load / Compressed In Memory（勿用 Streaming，否则取不到数据）。</summary>
    public static void Play(AudioClip clip) => Instance.PlayInternal(clip);

    /// <summary>带淡出停止当前曲目（没有播放器实例时为空操作，不会自动创建）。</summary>
    public static void Stop()
    {
        if (_instance != null) _instance.StopInternal();
    }

    /// <summary>是否有前台曲目在播放（切歌淡变期间也算 true；无实例则 false）。</summary>
    public static bool IsPlaying => _instance != null && _instance.currentStream != null;

    /// <summary>当前所有存活流（前台 + 正在淡出回收中）转码缓冲占用的字节数（无实例则 0）。</summary>
    public static long TranscodeMemoryBytes => _instance != null ? _instance.CurrentTranscodeMemoryBytes : 0;

    /// <summary>实例级实现：累计本播放器所有存活流的缓冲占用。</summary>
    private long CurrentTranscodeMemoryBytes
    {
        get
        {
            long bytes = 0;
            if (currentStream != null) bytes += currentStream.MemoryBytes;
            for (int i = 0; i < fadingStreams.Count; i++)
                bytes += fadingStreams[i].MemoryBytes;
            return bytes;
        }
    }

    // 前台曲目（平时唯一的一条流）
    private MusicStream currentStream;
    // 正在淡出回收中的旧流（仅切歌/停止的淡出期间临时存在）
    private readonly List<MusicStream> fadingStreams = new List<MusicStream>();
    // 流的 GameObject 命名计数，保证每条流的 GO 名字唯一
    private int streamCounter;
    // ogg 走 UnityWebRequest，异步加载协程引用（便于打断）
    private Coroutine loadRoutine;

    // ============================================================
    //  实例级实现（由上面的静态门面转发进来）
    // ============================================================
    /// <summary>
    /// 主入口：传入本地音频文件的绝对路径，加载并播放（与正在播放的曲目交叉淡变）。
    /// WAV / mp3 走真流式；ogg 等走 UnityWebRequest 整段解码后再喂。
    /// </summary>
    private void PlayInternal(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) { Debug.LogWarning("[CustomMusic] Play 收到空路径"); return; }
        if (loadRoutine != null) { StopCoroutine(loadRoutine); loadRoutine = null; }

        string lower = filePath.ToLowerInvariant();
        if (lower.EndsWith(".wav"))
        {
            var s = new MusicStream();
            if (s.OpenWav(filePath, ringBufferSeconds)) StartNewStream(s);
        }
        else if (lower.EndsWith(".mp3"))
        {
            var s = new MusicStream();
            if (s.OpenMp3(filePath, ringBufferSeconds)) StartNewStream(s);
        }
        else
        {
            loadRoutine = StartCoroutine(LoadExternalAudioCoroutine(filePath));
        }
    }

    /// <summary>带淡出地停止当前曲目（淡出后回收，不再起新流）。</summary>
    private void StopInternal()
    {
        if (loadRoutine != null) { StopCoroutine(loadRoutine); loadRoutine = null; }
        RetireCurrent();
    }

    /// <summary>播放一个随包 AudioClip：必要时先加载音频数据，再取整段 PCM 喂内存流（与 ogg 同管线）。</summary>
    private void PlayInternal(AudioClip clip)
    {
        if (clip == null) { Debug.LogWarning("[CustomMusic] Play 收到空 AudioClip"); return; }
        if (loadRoutine != null) { StopCoroutine(loadRoutine); loadRoutine = null; }
        loadRoutine = StartCoroutine(LoadClipCoroutine(clip));
    }

    // 随包 AudioClip：若资源未解码（Load In Background 等）先加载，再取 PCM 起播
    private IEnumerator LoadClipCoroutine(AudioClip clip)
    {
        if (clip.loadState != AudioDataLoadState.Loaded)
        {
            clip.LoadAudioData();
            while (clip.loadState == AudioDataLoadState.Loading) yield return null;
            if (clip.loadState != AudioDataLoadState.Loaded)
            {
                Debug.LogError($"[CustomMusic] AudioClip 加载失败: {clip.name}");
                loadRoutine = null;
                yield break;
            }
        }

        if (!StartFromClip(clip))
            Debug.LogError($"[CustomMusic] 从 AudioClip 取 PCM 失败（导入请用 Decompress On Load / Compressed In Memory，勿用 Streaming）: {clip.name}");
        loadRoutine = null;
    }

    /// <summary>从一个已加载好的 AudioClip 取整段 PCM，建内存流并起播（交叉淡变）。
    /// ogg(路径) 与 Play(AudioClip) 共用。返回 false 表示 GetData 失败（多半是 Streaming 导入模式）。</summary>
    private bool StartFromClip(AudioClip clip)
    {
        var data = new float[clip.samples * clip.channels];
        if (!clip.GetData(data, 0)) return false;

        var s = new MusicStream();
        s.OpenMemory(data, clip.channels, clip.frequency, ringBufferSeconds);
        StartNewStream(s);
        return true;
    }

    [ContextMenu("Debug Play Custom Music")]
    private void TestPlay()
    {
        if (string.IsNullOrEmpty(testAbsoluteFilePath))
        {
            Debug.LogError("请先在 Inspector 中填写测试音频的绝对路径！");
            return;
        }
        PlayInternal(testAbsoluteFilePath);
    }

    // ogg/aiff 等：经 UnityWebRequest 整段解码进内存，再创建流交叉淡变播放
    private IEnumerator LoadExternalAudioCoroutine(string filePath)
    {
        string uri;
        try { uri = new System.Uri(filePath).AbsoluteUri; }
        catch (System.UriFormatException) { uri = new System.Uri("file://" + filePath).AbsoluteUri; }

        AudioType audioType = GetAudioTypeFromPath(uri);
        Debug.Log($"[CustomMusic] (UnityWebRequest) 加载: {uri}, 格式={audioType}");

        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(uri, audioType))
        {
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[CustomMusic] 无法读取文件: {www.error}");
                loadRoutine = null;
                yield break;
            }

            AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
            if (clip == null)
            {
                Debug.LogError("[CustomMusic] AudioClip 为空（该格式可能不支持桌面端运行时解码，如 mp3）");
                loadRoutine = null;
                yield break;
            }

            if (!StartFromClip(clip))
                Debug.LogError("[CustomMusic] 从 AudioClip 取 PCM 失败");
            loadRoutine = null;
        }
    }

    private static AudioType GetAudioTypeFromPath(string path)
    {
        string lower = path.ToLowerInvariant();
        if (lower.EndsWith(".mp3")) return AudioType.MPEG;
        if (lower.EndsWith(".ogg")) return AudioType.OGGVORBIS;
        if (lower.EndsWith(".aiff") || lower.EndsWith(".aif")) return AudioType.AIFF;
        if (lower.EndsWith(".wav")) return AudioType.WAV;
        return AudioType.UNKNOWN;
    }

    // ============================================================
    //  交叉淡变核心：起新流 + 退旧流
    // ============================================================
    /// <summary>开始播放一条新流：先让旧流淡出回收，再起新流（带淡入），两者重叠 = 交叉淡变。</summary>
    private void StartNewStream(MusicStream s)
    {
        RetireCurrent(); // 旧流 Post(Stop) 开始淡出，加入回收队列

        string goName = $"MusicStream_{streamCounter++}";
        if (!s.Start(audioInputEvent != null ? audioInputEvent.Id : 0, transform, goName))
        {
            Debug.LogError("[CustomMusic] 新流启动失败（事件或 SoundBank 可能未加载）");
            s.Dispose();
            return;
        }
        currentStream = s; // 新流成为前台曲目
    }

    /// <summary>把当前前台流转入「淡出回收」：触发 Stop 事件（带淡出），延时后回收资源。</summary>
    private void RetireCurrent()
    {
        if (currentStream == null) return;

        var old = currentStream;
        currentStream = null;
        old.PostStop(stopEvent);          // 只在 old 自己的 GameObject 上触发，不影响新流
        fadingStreams.Add(old);
        StartCoroutine(DisposeAfter(old, fadeOutSeconds));
    }

    private IEnumerator DisposeAfter(MusicStream s, float seconds)
    {
        yield return new WaitForSeconds(seconds);
        fadingStreams.Remove(s);
        s.Dispose();
    }

    // ============================================================
    //  主循环：给所有存活流补充缓冲 + 回收自然播完的前台流
    // ============================================================
    private void Update()
    {
        if (currentStream != null)
        {
            currentStream.Fill();
            if (!currentStream.active)   // 前台曲自然播完 → 回收
            {
                currentStream.Dispose();
                currentStream = null;
            }
        }

        for (int i = 0; i < fadingStreams.Count; i++)
            fadingStreams[i].Fill();     // 淡出期间旧流仍需喂真实数据
    }

    private void OnDestroy()
    {
        if (currentStream != null) { currentStream.Dispose(); currentStream = null; }
        for (int i = 0; i < fadingStreams.Count; i++)
            fadingStreams[i].Dispose();
        fadingStreams.Clear();

        if (_instance == this) _instance = null; // 让单例可重新指向新实例
    }

    // ============================================================
    //  MusicStream —— 单条音乐数据流（解码源 + 环形缓冲 + 独立 GameObject + playingID）
    //  切歌时新旧各一条，互不干扰；这就是「临时双流」的载体。
    // ============================================================
    private class MusicStream
    {
        /// <summary>是否仍在播放（含淡出期间）。源读完且缓冲排空后置 false。</summary>
        public volatile bool active;

        public long MemoryBytes
        {
            get
            {
                long bytes = 0;
                if (ringBuffer != null) bytes += (long)ringBuffer.Length * sizeof(float);
                if (readByteBuffer != null) bytes += readByteBuffer.Length;
                if (mp3ReadBuffer != null) bytes += (long)mp3ReadBuffer.Length * sizeof(float);
                if (memSource != null) bytes += (long)memSource.Length * sizeof(float);
                return bytes;
            }
        }

        // ---- 音频格式 ----
        private int channelCount;
        private int sampleRate;

        // ---- 环形缓冲（交错存放 LRLR...）----
        private float[] ringBuffer;
        private int ringCapacityFrames;
        private int ringReadFrame;
        private int ringWriteFrame;
        private int ringAvailableFrames;
        private readonly object ringLock = new object();
        private int servedFramesThisBlock; // 一个音频块内各声道服务同一批帧

        // ---- 数据源 ----
        private enum SourceMode { None, WavStream, Mp3Stream, MemoryArray }
        private SourceMode sourceMode = SourceMode.None;
        private volatile bool sourceExhausted;

        // WavStream
        private FileStream wavStream;
        private long wavBytesRemaining;
        private int wavBytesPerSample;
        private bool wavIsFloat;
        private int wavBits;
        private byte[] readByteBuffer;

        // Mp3Stream
        private MpegFile mp3File;
        private float[] mp3ReadBuffer;

        // MemoryArray
        private float[] memSource;
        private int memReadFrame;
        private int memTotalFrames;

        // ---- Wwise ----
        private GameObject go;       // 本流独立的 GameObject（让 Stop 事件只命中本流）
        private uint playingID;

        // ========================================================
        //  打开数据源
        // ========================================================
        public bool OpenWav(string filePath, float ringSeconds)
        {
            try
            {
                var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);

                var header = new byte[12];
                if (fs.Read(header, 0, 12) < 12 ||
                    header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'F' ||
                    header[8] != 'W' || header[9] != 'A' || header[10] != 'V' || header[11] != 'E')
                {
                    Debug.LogError("[CustomMusic] 不是合法的 WAV 文件（RIFF/WAVE 头缺失）");
                    fs.Dispose();
                    return false;
                }

                int audioFormat = 0, channels = 0, rate = 0, bits = 0;
                long dataStart = -1, dataSize = 0;
                var chunkHdr = new byte[8];

                while (ReadFull(fs, chunkHdr, 0, 8) == 8)
                {
                    string id = System.Text.Encoding.ASCII.GetString(chunkHdr, 0, 4);
                    int size = System.BitConverter.ToInt32(chunkHdr, 4);

                    if (id == "fmt ")
                    {
                        var fmt = new byte[size];
                        if (size < 16 || ReadFull(fs, fmt, 0, size) < size)
                        {
                            Debug.LogError("[CustomMusic] WAV fmt 块读取不完整或过短");
                            fs.Dispose();
                            return false;
                        }
                        audioFormat = System.BitConverter.ToUInt16(fmt, 0);
                        channels = System.BitConverter.ToUInt16(fmt, 2);
                        rate = System.BitConverter.ToInt32(fmt, 4);
                        bits = System.BitConverter.ToUInt16(fmt, 14);
                        if ((size & 1) == 1) fs.Seek(1, SeekOrigin.Current);
                    }
                    else if (id == "data")
                    {
                        dataStart = fs.Position;
                        dataSize = size;
                        break;
                    }
                    else
                    {
                        fs.Seek(size + (size & 1), SeekOrigin.Current);
                    }
                }

                if (dataStart < 0 || channels <= 0 || bits <= 0)
                {
                    Debug.LogError("[CustomMusic] WAV 缺少 fmt/data 块或参数异常");
                    fs.Dispose();
                    return false;
                }
                if (dataStart + dataSize > fs.Length)
                    dataSize = fs.Length - dataStart;

                fs.Position = dataStart;

                channelCount = channels;
                sampleRate = rate;
                wavBits = bits;
                wavBytesPerSample = bits / 8;
                wavIsFloat = (audioFormat == 3); // WAVE_FORMAT_IEEE_FLOAT
                wavBytesRemaining = dataSize;
                wavStream = fs;
                sourceMode = SourceMode.WavStream;

                AllocateRing(ringSeconds);
                readByteBuffer = new byte[ringCapacityFrames * channelCount * wavBytesPerSample];

                Debug.Log($"[CustomMusic] (WAV流式) 打开成功: 声道={channelCount}, 采样率={sampleRate}, 位深={bits}, " +
                          $"float={wavIsFloat}, data={dataSize}字节, 环形缓冲={ringCapacityFrames}帧");
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError("[CustomMusic] 打开 WAV 失败: " + e);
                return false;
            }
        }

        public bool OpenMp3(string filePath, float ringSeconds)
        {
            try
            {
                mp3File = new MpegFile(filePath); // 只解析头，不整段解码
                channelCount = mp3File.Channels;
                sampleRate = mp3File.SampleRate;
                sourceMode = SourceMode.Mp3Stream;

                AllocateRing(ringSeconds);
                mp3ReadBuffer = new float[ringCapacityFrames * channelCount];

                Debug.Log($"[CustomMusic] (MP3流式) 打开成功: 声道={channelCount}, 采样率={sampleRate}, 环形缓冲={ringCapacityFrames}帧");
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError("[CustomMusic] 打开 MP3 失败: " + e);
                CloseMp3Stream();
                return false;
            }
        }

        public void OpenMemory(float[] data, int channels, int rate, float ringSeconds)
        {
            channelCount = channels;
            sampleRate = rate;
            memSource = data;
            memTotalFrames = data.Length / channels;
            memReadFrame = 0;
            sourceMode = SourceMode.MemoryArray;

            AllocateRing(ringSeconds);
            Debug.Log($"[CustomMusic] (内存源) 就绪: 声道={channelCount}, 采样率={sampleRate}, 帧数={memTotalFrames}");
        }

        private void AllocateRing(float ringSeconds)
        {
            ringCapacityFrames = Mathf.Max(1024, Mathf.CeilToInt(sampleRate * Mathf.Max(0.1f, ringSeconds)));
            ringBuffer = new float[ringCapacityFrames * channelCount];
            ringReadFrame = 0;
            ringWriteFrame = 0;
            ringAvailableFrames = 0;
            servedFramesThisBlock = 0;
            sourceExhausted = false;
        }

        // ========================================================
        //  起播：建独立 GameObject → 预填 → Post(Play 事件，带淡入)
        // ========================================================
        public bool Start(uint playEventId, Transform parent, string name)
        {
            if (sourceMode == SourceMode.None || playEventId == 0) return false;

            go = new GameObject(name);
            go.transform.SetParent(parent, false);
            AkUnitySoundEngine.RegisterGameObj(go, name);

            active = true;
            Fill(); // 预填，避免起播瞬间欠载

            playingID = AkAudioInputManager.PostAudioInputEvent(
                playEventId, go, AudioSamplesCallback, AudioFormatCallback);

            if (playingID == AkUnitySoundEngine.AK_INVALID_PLAYING_ID)
            {
                active = false;
                return false;
            }
            Debug.Log($"[CustomMusic] 已起播，PlayingID={playingID}（GameObject={name}）");
            return true;
        }

        // ========================================================
        //  生产者：把数据源读进环形缓冲（主线程调用，绝不在音频线程读磁盘）
        // ========================================================
        public void Fill()
        {
            if (sourceExhausted || sourceMode == SourceMode.None) return;

            int free;
            lock (ringLock) { free = ringCapacityFrames - ringAvailableFrames; }
            if (free <= 0) return;

            if (sourceMode == SourceMode.WavStream) FillFromWav(free);
            else if (sourceMode == SourceMode.Mp3Stream) FillFromMp3(free);
            else if (sourceMode == SourceMode.MemoryArray) FillFromMemory(free);
        }

        private void FillFromMp3(int freeFrames)
        {
            int floatsWanted = freeFrames * channelCount;
            if (floatsWanted > mp3ReadBuffer.Length)
                floatsWanted = mp3ReadBuffer.Length;

            int got = mp3File.ReadSamples(mp3ReadBuffer, 0, floatsWanted); // 交错 float，[-1,1]
            if (got <= 0)
            {
                sourceExhausted = true;
                CloseMp3Stream();
                return;
            }

            int framesGot = got / channelCount;
            lock (ringLock)
            {
                for (int f = 0; f < framesGot; f++)
                {
                    int slot = (ringWriteFrame + f) % ringCapacityFrames;
                    for (int c = 0; c < channelCount; c++)
                        ringBuffer[slot * channelCount + c] = mp3ReadBuffer[f * channelCount + c];
                }
                ringWriteFrame = (ringWriteFrame + framesGot) % ringCapacityFrames;
                ringAvailableFrames += framesGot;
            }
        }

        private void FillFromWav(int freeFrames)
        {
            long framesLeftInFile = wavBytesRemaining / (wavBytesPerSample * channelCount);
            int framesToRead = (int)Mathf.Min(freeFrames, framesLeftInFile);
            if (framesToRead <= 0)
            {
                sourceExhausted = true;
                CloseWavStream();
                return;
            }

            int bytesToRead = framesToRead * channelCount * wavBytesPerSample;
            int got = wavStream.Read(readByteBuffer, 0, bytesToRead);
            if (got <= 0)
            {
                sourceExhausted = true;
                CloseWavStream();
                return;
            }
            wavBytesRemaining -= got;

            int framesGot = (got / wavBytesPerSample) / channelCount;
            lock (ringLock)
            {
                for (int f = 0; f < framesGot; f++)
                {
                    int slot = (ringWriteFrame + f) % ringCapacityFrames;
                    for (int c = 0; c < channelCount; c++)
                    {
                        int o = (f * channelCount + c) * wavBytesPerSample;
                        ringBuffer[slot * channelCount + c] = ConvertSample(readByteBuffer, o);
                    }
                }
                ringWriteFrame = (ringWriteFrame + framesGot) % ringCapacityFrames;
                ringAvailableFrames += framesGot;
            }

            if (wavBytesRemaining <= 0)
            {
                sourceExhausted = true;
                CloseWavStream();
            }
        }

        private float ConvertSample(byte[] buf, int o)
        {
            if (wavIsFloat && wavBits == 32) return System.BitConverter.ToSingle(buf, o);
            if (wavBits == 16) return System.BitConverter.ToInt16(buf, o) / 32768f;
            if (wavBits == 24)
            {
                int s = buf[o] | (buf[o + 1] << 8) | (buf[o + 2] << 16);
                if ((s & 0x800000) != 0) s |= unchecked((int)0xFF000000); // 符号扩展
                return s / 8388608f;
            }
            if (wavBits == 32) return System.BitConverter.ToInt32(buf, o) / 2147483648f;
            if (wavBits == 8) return (buf[o] - 128) / 128f; // 8-bit 为无符号
            return 0f;
        }

        private void FillFromMemory(int freeFrames)
        {
            int framesLeft = memTotalFrames - memReadFrame;
            int framesToCopy = Mathf.Min(freeFrames, framesLeft);
            if (framesToCopy <= 0)
            {
                sourceExhausted = true;
                return;
            }

            lock (ringLock)
            {
                for (int f = 0; f < framesToCopy; f++)
                {
                    int slot = (ringWriteFrame + f) % ringCapacityFrames;
                    int srcFrame = memReadFrame + f;
                    for (int c = 0; c < channelCount; c++)
                        ringBuffer[slot * channelCount + c] = memSource[srcFrame * channelCount + c];
                }
                ringWriteFrame = (ringWriteFrame + framesToCopy) % ringCapacityFrames;
                ringAvailableFrames += framesToCopy;
            }

            memReadFrame += framesToCopy;
            if (memReadFrame >= memTotalFrames)
                sourceExhausted = true;
        }

        // ========================================================
        //  消费者：Wwise 音频线程来拉数据（按声道去交错）
        // ========================================================
        private void AudioFormatCallback(uint id, AkAudioFormat format)
        {
            format.uSampleRate = (uint)sampleRate;
            format.uBitsPerSample = 32;
            format.uTypeID = AkUnitySoundEngine.AK_FLOAT;
            format.uInterleaveID = AkUnitySoundEngine.AK_NONINTERLEAVED;

            if (channelCount == 1)
                format.channelConfig.SetStandard(AkUnitySoundEngine.AK_SPEAKER_SETUP_MONO);
            else
                format.channelConfig.SetStandard(AkUnitySoundEngine.AK_SPEAKER_SETUP_STEREO);

            format.uBlockAlign = (uint)(channelCount * sizeof(float));
        }

        private bool AudioSamplesCallback(uint id, uint channelIndex, float[] samples)
        {
            int n = samples.Length;

            lock (ringLock)
            {
                if (ringBuffer == null) // 流已被 Dispose 回收：填静音并结束（避免与回调竞态崩溃）
                {
                    for (int i = 0; i < n; i++) samples[i] = 0f;
                    return false;
                }

                if (channelIndex == 0)
                    servedFramesThisBlock = Mathf.Min(n, ringAvailableFrames);

                int real = servedFramesThisBlock;
                for (int i = 0; i < n; i++)
                {
                    if (i < real)
                    {
                        int slot = (ringReadFrame + i) % ringCapacityFrames;
                        samples[i] = ringBuffer[slot * channelCount + (int)channelIndex];
                    }
                    else
                    {
                        samples[i] = 0f; // 欠载补 0
                    }
                }

                if (channelIndex == (uint)(channelCount - 1))
                {
                    ringReadFrame = (ringReadFrame + real) % ringCapacityFrames;
                    ringAvailableFrames -= real;
                }
            }

            // 源读完且缓冲排空 → 通知 Wwise 停止
            if (sourceExhausted)
            {
                bool empty;
                lock (ringLock) { empty = ringAvailableFrames <= 0; }
                if (empty)
                {
                    active = false;
                    return false;
                }
            }
            return active;
        }

        // ========================================================
        //  停止 / 清理
        // ========================================================
        /// <summary>触发 Stop 事件（带淡出）。只在本流自己的 GameObject 上生效，不影响其它流。
        /// 淡出期间消费者仍需读到真实数据，所以这里不关流、不动 active。</summary>
        public void PostStop(AK.Wwise.Event stopEvent)
        {
            if (playingID == 0 || playingID == AkUnitySoundEngine.AK_INVALID_PLAYING_ID) return;

            if (stopEvent != null && stopEvent.IsValid())
                stopEvent.Post(go);                                  // 用 Wwise 里授权的淡出曲线
            else
                AkUnitySoundEngine.StopPlayingID(playingID);          // 没配 Stop 事件则直接停
        }

        /// <summary>彻底回收：硬停 + 关流 + 释放缓冲 + 注销并销毁 GameObject。可重复调用。</summary>
        public void Dispose()
        {
            if (playingID != 0 && playingID != AkUnitySoundEngine.AK_INVALID_PLAYING_ID)
            {
                AkUnitySoundEngine.StopPlayingID(playingID); // 淡出已结束，这里是兜底硬停
                playingID = 0;
            }
            active = false;
            sourceMode = SourceMode.None;
            sourceExhausted = false;

            CloseWavStream();
            CloseMp3Stream();
            memReadFrame = 0;
            memTotalFrames = 0;

            // ringBuffer 与音频线程回调共享：置空必须在锁内，和回调里的 null 检查同步
            lock (ringLock)
            {
                ringBuffer = null;
                readByteBuffer = null;
                memSource = null;
            }

            if (go != null)
            {
                AkUnitySoundEngine.UnregisterGameObj(go);
                Object.Destroy(go);
                go = null;
            }
        }

        /// <summary>循环读取直到读满 count 字节或到达流末尾；返回实际读到的字节数。
        /// （FileStream.Read 不保证一次读满，必须循环。）</summary>
        private static int ReadFull(Stream s, byte[] buf, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int r = s.Read(buf, offset + total, count - total);
                if (r <= 0) break; // 流结束
                total += r;
            }
            return total;
        }

        private void CloseWavStream()
        {
            if (wavStream != null)
            {
                wavStream.Dispose();
                wavStream = null;
            }
        }

        private void CloseMp3Stream()
        {
            if (mp3File != null)
            {
                mp3File.Dispose();
                mp3File = null;
            }
            mp3ReadBuffer = null;
        }
    }
}
