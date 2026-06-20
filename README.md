# 游戏音频设计期末作业说明

项目主要实现环境音混合器以及经过Wwise总线的用户导入音频播放功能。



**开发环境：**

- Unity 6000.3.10

- Wwise 2025.1.5



**提交内容：**

- Test（Unity 工程目录，子目录 ”.\Test\Test_WwiseProject“ 为 Wwise 工程目录）

- TestBuild（打包好的项目文件夹，点击 Test.exe 运行）

- README.md（本文档）

- 演示视频.mp4（功能演示视频）



**项目概况与解决目标**

因为 Wwise 的 `External Source` 运行时**只能读取经过 Wwise 工具动态生成的 `.wem` 格式音频**。为此，本项目提供了一种基于 `Wwise Audio Input` 作为音频输入源的=替代方案。

该方案在 Unity 侧集成了轻量级的底层解码器，在游戏运行时动态、异步地将玩家导入的常规原生音频格式（WAV、MP3、OGG）还原为原始 PCM 信号。并优化逻辑将数据实时重组并稳定喂给 Wwise 声音引擎。

这使得用户导入的自定义物理音频能够彻底打通并无缝接入 Wwise 的 `Main Audio Bus`。在**完全消除跨平台技术阻碍并规避商业转码工具法律风险**的同时，让自定义音频能够完美享受 Wwise 侧的后期混音、环境混响及 RTPC 实时参数控制，在零门槛支持用户 UGC 生态层面上具备极高的工程落地价值与优秀的性能表现。



**项目内容主要操作界面包括：**

总音量推子、自定义音频音量推子、各个环境音的混合分推子、音频音频文件路径输入框（绝对路径）、播放音频切换下拉菜单、自定义音频播放/停止按钮、环境音播放/停止按钮。

![ScreenShot.png](C:\Users\patri\Documents\School\GameAudioDesign\FinalProject\Image\ScreenShot.png)





注：以下文本内容主要讲解根据路径读取音频输入到 Wwise 总线的实现以及优化方法。

## 一、 技术方案整体架构与数据链路

本方案的核心是通过 Unity 在运行时异步加载磁盘上的外部音频文件（WAV、MP3、OGG），解码为原始 PCM 数据后，直接喂给 Wwise 的 `Audio Input` 插件源，最终进入 Wwise 的 `Main Audio Bus` 总线进行混音和音效处理。

在结构上，整个方案清晰地拆分为两层：

1. **路径传输 (`WwiseMusicLibrary`)**：只负责扫描本地文件夹、管理文件路径、以及驱动 UI 下拉框和按钮。它与底层播放引擎之间唯一的作用是：**拿到文件的完整路径（string），然后调用实例 `player.Play(path)`**。

2. **播放引擎 (`WwiseAudioInputPlayer`)**：方案的技术核心。负责多格式流式解码、环形缓冲区管理、双流交叉淡变调度以及 Wwise 底层回调的对接。

![](C:/Users/patri/AppData/Roaming/marktext/images/2026-06-20-22-17-04-image.png)



## 二、 核心技术实现与底层细节

### 1. Wwise 输入源接口规范与格式限制 (Format & Execute)

要将 Unity 侧的音频数据塞进 Wwise，必须严格遵循 Wwise SDK 的原生回调机制（基于 `AkAudioInputSourceFactory.h` 暴露的接口）：

- **格式回调 (`AkAudioInputPluginGetFormatCallbackFunc`)**：在音频流 PostEvent 启动后触发一次，用于向 Wwise 报备音频的基础格式（采样率、声道数、位深）。
  
  - **底层的硬性限制**：Wwise 规定，**交错式（`AK_INTERLEAVED`）格式只支持整型数据（`AK_INT`，如16位有符号整数）**；而**非交错式（`AK_NONINTERLEAVED`）格式只支持浮点数（`AK_FLOAT`，如32位浮点数）**。
    
    - 注：交错式 16 位整型：`LRLRLRLRLRLRLRLRLRLRLRLRLRLRLRLR`，非交错式 32 位浮点数：`LLLLLLLLLLLLLLLLRRRRRRRRRRRRRRRR`

- **本方案的选择**：在初始化 `AkAudioFormat` 时，方案配置为 $48000\text{Hz}$ 采样率、2声道（立体声）、16位有符号整型（`AK_INT`），并明确指定使用交错式（`AK_INTERLEAVED`）布局。

- **采样回调 (`AkAudioInputPluginExecuteCallbackFunc`)**：一旦起播，**Wwise 的底层音频线程**会高频、定时地调用该函数，驱动 Unity 往特定的数组里填入 PCM 数据，直到文件放完返回 `AK_NoMoreData`。



### 2. PCM 数据的转换与 LRLR 传输机制

由于我们配置的是 **16位整型、交错式（`AK_INTERLEAVED`）** 格式，数据在磁盘解码出来或者在环形缓冲区（Ring Buffer）里存放时，是按照左右声道样本交替排列的，即：

$$\text{缓冲区数据排列} = [L_1, R_1, L_2, R_2, L_3, R_3, L_4, R_4, \dots]$$

然而，**Wwise 的底层采样回调（`AudioSamplesCallback`）是非常奇葩的“按声道（`channelIndex`）分别拉取”机制**。这意味着 Wwise 线程在一个音频帧周期内，会调两次回调：先调一次拉左声道，再调一次拉右声道。为了完美对接，方案实现了一套**去交错（Deinterleave）读取算法**：

- 当 `channelIndex == 0`（Wwise 来要**左声道**）时，系统立刻加锁（`lock`），从交错的 `LRLRLRLR` 缓冲区中，隔一个样本抽一个，把 $L_1, L_2, L_3 \dots$ 提取出来填给 Wwise。同时，方案在内部快照（Snapshot）记录下这一批次到底读了多少帧（`servedFramesThisBlock`）。**此时，绝对不推进环形缓冲区的读游标**。

- 当 `channelIndex == 1`（Wwise 来要**右声道**）时，系统直接根据上一步的快照信息，从缓冲区中抽取对应的 $R_1, R_2, R_3 \dots$ 填给 Wwise。

- **只有当最后一个声道（右声道）全部交接完毕后，系统才会正式向前推进环形缓冲区的读游标（`ringReadFrame`）**。这套机制完美避免了左右声道数据错位或读取步调不一致的问题。



### 3. 基于环形缓冲（Ring Buffer）的多线程解耦

为了防止磁盘 I/O 阻塞或者转码计算导致音频撕裂（欠载），方案采用了多线程的生产者-消费者模型：

- **输出（Unity 主线程 `Update`）**：高频检查环形缓冲区的剩余空间，根据文件的扩展名（WAV/MP3/OGG）进行增量分流解码，把转码后的 `LRLR` 字节流写进环形缓冲，内存占用恒定在 1 秒左右（Inspector中可配置），与歌曲长度无关。

- **输入（Wwise 音频线程）**：在底层回调中直接通过上述的去交错算法从环形缓冲里捞数据。

- **线程安全**：由于主线程在写、音频线程在读，所有读写游标的推进和缓冲区访问均置于 `lock(ringLock)` 临界区内。在流被销毁（`Dispose`）时，锁内会将缓冲置空，音频线程进锁一旦发现缓冲为空则直接填充静音并返回 `false`，从而优雅抹平了 Wwise 延迟回调导致的空指针崩溃（NRE）隐患。

- ![](C:/Users/patri/AppData/Roaming/marktext/images/2026-06-20-22-46-44-image.png)

![](C:/Users/patri/AppData/Roaming/marktext/images/2026-06-20-22-40-26-image.png)



### 4. 临时多流并行的Crossfade

为了在切歌时实现Crossfade，这里利用了 Wwise Event的Fade In/Out

- 方案将每一首播放的歌曲都抽象为一个独立的 `MusicStream` 实例，并且**各自挂在独立的 Unity GameObject 上**。

- **切歌流程**：当用户点选新歌时，引擎将当前的 `currentStream`（旧歌流）标记为“移除”并移入淡出队列（`fadingStreams`），同时向该 GameObject 发送 Wwise 的 `Stop` 事件（设定 Fade Out）；紧接着，立刻创建一个全新的 GameObject 触发新歌的 `Play` 事件（设定 Fade In）。

- **并行喂数**：在交叉淡变的重叠期间，Unity 主线程的 `Update` 会同时作为生产者为这两个（或多个）常驻在内存中的流补充环形缓冲；Wwise 底层则通过 `playingID` 将音频线程的回调精准路由到对应的 GameObject 流实例上。旧歌在完全淡出后（等满 `fadeOutSeconds` 延时），Unity 侧协程才会安全地执行 `Dispose` 释放资源。整个过程在 Wwise 混音总线上产生了双信号流重叠交叠的淡变效果。

![](C:/Users/patri/AppData/Roaming/marktext/images/2026-06-20-22-31-27-image.png)

注：支持临时多流并存。如果玩家快速连点切歌，旧的几首歌会同时挂在它们各自的 GameObject 上进入 Wwise 的淡出队列（`fadingStreams`）。主线程的 `Update` 会同时为这几个残留在内存中的流补满最后一点缓冲，直到它们在 Wwise 侧彻底淡出后（满 `fadeOutSeconds` 延时）由协程安全回收。这保证了频繁切歌时，声音能稳定重叠淡变。


