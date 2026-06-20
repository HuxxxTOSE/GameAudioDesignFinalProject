# WwiseAudioInputPlayer 接入指南

> 运行时把本地音频文件（wav/mp3/ogg）流式播放出去的播放引擎。

---

## 1. 它是什么

`WwiseAudioInputPlayer` 是一个 **MonoBehaviour 组件**，但对外接口是 **静态的**：直接 `WwiseAudioInputPlayer.Play(路径)` 即可，**不用拿实例引用**。
给它一条音频文件路径，它就负责播放（经 Wwise 输出）。**全局同时播一首**；再调一次 `Play` 会自动交叉淡变切到新曲。

> 原理：场景里挂一个配好 Wwise 事件的 `WwiseAudioInputPlayer`（单例），静态接口自动转发给它。所以你只管静态调，**事件由音频那边在 Inspector 配好**。

---

## 2. 开放的端口（你只用这 4 个）

| 端口                       | 签名（全部 static）                               | 传入                         | 作用                      |
| ------------------------ | ------------------------------------------- | -------------------------- | ----------------------- |
| **Play**                 | `static void Play(string filePath)`         | 音频文件的**绝对路径**（wav/mp3/ogg） | 播放该文件；若正在播则交叉淡变切过去      |
| **Stop**                 | `static void Stop()`                        | 无                          | 带淡出停止当前曲目               |
| **IsPlaying**            | `static bool IsPlaying { get; }`            | 只读                         | 当前是否在播放（自然播完会自动变 false） |
| **TranscodeMemoryBytes** | `static long TranscodeMemoryBytes { get; }` | 只读                         | 当前播放占用的缓冲字节数（监控用，可不管）   |

就这些。**核心就两个动作：`Play(路径)` 和 `Stop()`。**

---

## 3. 怎么用（直接静态调）

### 前提（一次性，音频那边做）

场景里挂一个 `WwiseAudioInputPlayer`，在 Inspector 把 `audioInputEvent` / `stopEvent` 配好。**你（程序）不用拿引用、不用拖物体。**

### 直接调端口

```csharp
void PlayBattle()
{
    WwiseAudioInputPlayer.Play(@"C:\Music\battle.wav");   // 传绝对路径
}

void SwitchToCalm()
{
    WwiseAudioInputPlayer.Play(@"C:\Music\calm.ogg");     // 播放中再调 = 自动交叉淡变切歌
}

void StopMusic()
{
    WwiseAudioInputPlayer.Stop();                          // 淡出停止
}
```

### 查状态（可选）

```csharp
if (!WwiseAudioInputPlayer.IsPlaying) { /* 已停 / 自然播完 */ }
```

> 若场景里忘了挂组件，首次 `Play` 会自动建一个并打 `Warning`——但它没配事件，播放会失败。正确做法仍是场景里挂一个配好事件的。

---

## 4. 传入路径的约定

- **必须是绝对路径**，且运行时该文件确实存在。
- 支持 **.wav / .mp3 / .ogg**。
- 不要写死开发机的盘符路径（`C:\Users\...`）。正式项目用：
  - 随包内置 → `Application.streamingAssetsPath`（注意安卓 StreamingAssets 在压缩包内，需特殊读取）
  - 跨平台可写目录 → `Application.persistentDataPath`
- 路径无效/文件损坏：当前只打 `Debug.LogError`，**不抛异常**（你那边如需 UI 反馈要自行加）。

---

## 5. 组件上要配的东西（Inspector）

挂上这个组件后，需要设置：

| 字段                     | 填什么                                    | 必填   |
| ---------------------- | -------------------------------------- | ---- |
| `audioInputEvent`      | Wwise 的 **Play 事件**（Play_CustomMusic）  | ✅    |
| `stopEvent`            | Wwise 的 **Stop 事件**（Stop__CustomMusic） | ✅    |
| `ringBufferSeconds`    | 环形缓冲秒数，默认 1.0（弱机可调大）                   | 默认即可 |
| `fadeOutSeconds`       | 0.5                                    | ✅    |
| `testAbsoluteFilePath` | 仅测试用（右键组件菜单 Debug Play 可试播）            | 可空   |

---

## 6. 运行的前置条件（Wwise 侧，要确保具备）

引擎本身不发声，它把数据喂给 Wwise，所以需要：

1. 工程已集成 **Wwise（AkUnitySoundEngine，2024.1+）**。
2. **mp3 依赖 `NLayer.dll`**（桌面端运行时解 mp3 必需；只用 wav/ogg 可不需要）。
3. Wwise 工程里有一个 **Audio Input 源**的 Sound，路由到目标总线。
4. 准备好 **Play 事件（带 fade in）/ Stop 事件（带 fade out）**，并打进 **运行时已加载的 SoundBank**。
5. 把这两个事件拖到组件的 `audioInputEvent` / `stopEvent`。

> 缺第 3~5 条会导致 `Play` 时 Wwise 返回无效 playingID，引擎会 `Debug.LogError` 提示。

---

## 7. 需要知道的调用行为

- **`Play` 期间再 `Play` 另一首** = 旧曲淡出 + 新曲淡入**重叠**（交叉淡变），不用你先 `Stop`。
- **`Stop`** = 当前曲淡出后停止。
- **全局同时只播一首**（静态接口走单例，前台一首）。若真要多轨并行（如 BGM + 环境轨各自独立），静态门面满足不了——那需要单独的实例化方案，找音频/我改。

`IsPlaying` 反映的是音乐播放状态，播放成功才为 `true`，分两种情况：

| 格式                                | 调 `Play()` 后 `IsPlaying` 何时变 `true`                  |
| --------------------------------- | ---------------------------------------------------- |
| **wav / mp3**（流式，同步起播）            | **当帧立刻** `true`——`Play()` 返回后同一帧查就是 `true`           |
| **ogg 等**（走 UnityWebRequest 整段解码） | **有空窗期**：`Play()` 只是启动异步加载，文件读完解码好（下一帧或几帧后）才变 `true` |

> ⚠️ 刚调完 `Play(ogg)` 立刻查 `IsPlaying` 会是 `false`，那是**还在加载**，不代表没播。别用「`Play()` 之后马上 `IsPlaying==true`」判断 ogg 已开播。

**变回 `false` 的时机：**

- **曲子自然播完** → **自动变 `false`，无需处理**。
- **调 `Stop()`** → **立刻** `false`（即便淡出尾巴还在响 `fadeOutSeconds` 那段，状态上已算停）。
- **起播失败**（事件 / SoundBank 没加载）→ 保持 `false`。

---

## 8. 最小可用示例

```csharp
using UnityEngine;

public class MusicDemo : MonoBehaviour
{
    void Start()
    {
        WwiseAudioInputPlayer.Play(Application.streamingAssetsPath + "/bgm/title.wav");
    }

    public void OnEnterBattle() => WwiseAudioInputPlayer.Play(Application.streamingAssetsPath + "/bgm/battle.mp3"); // 交叉淡变切歌
    public void OnGameOver()    => WwiseAudioInputPlayer.Stop();
}
```

就这么多——你这边只跟 `WwiseAudioInputPlayer.Play(路径)` / `WwiseAudioInputPlayer.Stop()` 打交道即可，无需任何引用。

---

## 9. 实战示例（照着这个写就行）

**你要做的只有 3 件事：**
1. 把音频文件放好，运行时能拼出**绝对路径**（下例用 `StreamingAssets/Music/`）。
2. 在游戏的**状态切换点**调 `Play(路径)`；该停的时候调 `Stop()`。
3. **切歌不用先 `Stop`、不用查 `IsPlaying`**——正在放别的，直接再 `Play` 就会自动交叉淡变。

把"放哪首"收口到一个小总控里，别处只管喊：

```csharp
using UnityEngine;

/// 游戏音乐总控：别处只管喊「现在该放哪首」，路径与切歌都收在这里。
public class GameMusic : MonoBehaviour
{
    // 音乐随包放在 StreamingAssets/Music/ 下，运行时拼成绝对路径
    private static string Path(string file) =>
        System.IO.Path.Combine(Application.streamingAssetsPath, "Music", file);

    public void PlayMenu()  => WwiseAudioInputPlayer.Play(Path("menu.ogg"));
    public void PlayField() => WwiseAudioInputPlayer.Play(Path("field.wav"));
    public void PlayBoss()  => WwiseAudioInputPlayer.Play(Path("boss.mp3")); // 正在放别的 → 自动交叉淡变
    public void StopMusic() => WwiseAudioInputPlayer.Stop();                 // 淡出停止
}
```

游戏逻辑里就这么调，**完全不用关心当前在放什么、也不用先停**：

```csharp
public class GameFlow : MonoBehaviour
{
    public GameMusic music;

    void OnEnterTown()   => music.PlayField();
    void OnEnterBattle() => music.PlayBoss();   // 直接切，自动交叉淡变到 Boss 曲
    void OnOpenMenu()    => music.PlayMenu();
    void OnGameOver()    => music.StopMusic();
}
```

> 嫌总控多余也行，任意脚本任意位置直接 `WwiseAudioInputPlayer.Play(那条绝对路径)` 即可，效果一样。收口只是方便统一管路径。

**几个别踩的点：**
- 路径必须是**绝对路径**且文件真实存在；无效只打 `Debug.LogError`，不抛异常（要 UI 反馈自己加）。
- 想做"这首放完自动接下一首"：在 `Update` 里轮询 `WwiseAudioInputPlayer.IsPlaying`，从 `true` 变 `false` 时再 `Play` 下一首。**注意 ogg 刚 `Play` 后有加载空窗期**（见第 7 节），别一调完就判 `false`。
- 不要为了切歌先 `Stop` 再 `Play`——那样是"先停再起"没有重叠，直接连调两次 `Play` 才是交叉淡变。

---

## 10. 随包音频（Unity AudioClip，按名字点播）

前面第 4~9 节传的是**磁盘上的绝对路径**（外部文件 / 热更）。如果你的音频是**直接导入 Unity、随包打进 build 的资源**（AudioClip），用这一套更省事——不用拼路径，**在 Inspector 拖一拖、起个名字，程序按名字播**。出声方式完全一样（还是走 Wwise Audio Input，和路径曲目能互相交叉淡变）。

### 10.1 音频那边配（一次性）
1. 把音频文件拖进 Unity 工程（它会变成 AudioClip 资源，随包打包）。
2. 选中该资源，在 Inspector 把 **Load Type** 设成 **Decompress On Load** 或 **Compressed In Memory**（**不要选 Streaming**，否则取不到数据播不了）。
3. 场景里挂一个 **`WwiseMusicBank`** 组件，在它的 `clips` 列表里：每行拖入一个 AudioClip + 起一个名字（如 `boss`、`menu`、`field`）。

### 10.2 程序怎么调
```csharp
WwiseMusicBank.Play("boss");   // 播放注册表里名为 boss 的随包音频；正在播则自动交叉淡变
WwiseMusicBank.Play("menu");   // 直接切到 menu，不用先 Stop
WwiseMusicBank.Stop();         // 淡出停止
if (WwiseMusicBank.IsPlaying) { /* ... */ }
```
> `WwiseMusicBank.Stop()` / `IsPlaying` 只是转发给同一个引擎，和 `WwiseAudioInputPlayer.Stop()` 是一回事——随包曲和路径曲共用一个播放器，能互相交叉淡变。名字没注册会打 `Debug.LogError`。

### 10.3 两套怎么选
| 你的音频 | 用哪个 | 调用 |
|---------|--------|------|
| 导入 Unity、随包打进 build 的 AudioClip | `WwiseMusicBank` | `WwiseMusicBank.Play("名字")` |
| 磁盘上的外部 / 热更文件（绝对路径）| `WwiseAudioInputPlayer` | `WwiseAudioInputPlayer.Play(@"...\x.wav")` |

> ⚠️ **内存代价**：随包 AudioClip 是「整段解码进内存」播放（非流式）。短 cue 无所谓；**很长的 BGM 会占内存**（3 分钟立体声 ≈ 30MB）。需要省内存的超长曲，建议仍走路径方式的 wav/mp3（那是真流式）。
