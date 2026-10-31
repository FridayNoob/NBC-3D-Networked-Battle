// 这个文件里有：
//   AudioManager（类）：音频总入口 —— 单路 BGM、可复用的音效播放器池、三组音量，每帧按探针回收播完的播放器
//   EndedRecord（结构体）：一次播放结束的记录项，句柄与离开原因成对缓冲，供延后派发

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NBC.Framework.Audio
{
    // 主要功能：音频总入口 —— 一条专用 BGM 播放器，加一个可复用的音效播放器池；每次播放按分组音量 × 单次音量算最终音量，池满时按优先级抢占或丢弃。
    // 主要数据成员：m_busVolumes / m_busMuted（三组音量与静音，0..1 线性值）、m_active（正在播的音效，个）、m_voicePool（播放器池）、m_bgmSource / m_bgmVoiceId（BGM 播放器与句柄编号）、m_maxConcurrentVoices（并发上限，默认 16 个）、m_min3DDistance / m_max3DDistance（3D 衰减距离，米），以及五项统计计数。
    // 主要外部方法：Play / PlayBgm / Stop / StopAll / IsActive / Tick / Prewarm，音量分组 GetBusVolume / SetBusVolume / IsBusMuted / SetBusMuted / EffectiveVolume，统计 ActiveVoiceCount / IdleVoiceCount / CreatedVoiceCount / DroppedCount / PreemptedCount / FinishedCount / LostCount，诊断用的 Host / VoiceRoot / Probe / CurrentBgm，以及 VoiceEnded 事件。
    // 谁在用它：目前只有 EditMode 的 AudioManagerTests 与 PlayMode 的 AudioHostLifetimeTests 经 Singleton 单例调用（Game / Boot / Editor 还没接入）；播放模式下由 MonoManager 每帧调 Tick。
    // 注意：池满时只有「被抢者优先级严格低于新请求」才抢，同级不抢 —— 否则两个同优先级音效会互相踩，密集打击音听上去像丢音，比丢音效更难查；Critical 没有更高的档，永远不会被抢。
    // 注意：Tick 刻意不带 deltaTime ——「播完没有」由探针读 AudioSource 的真实状态回答，自己按秒数推算会和真实进度漂移（改 pitch、暂停、编辑器卡顿都会偏），不引入第二份时间就不会不一致。
    // 注意：回池只停播放、不销毁任何东西；播放模式的宿主在 EnsureHost 里立刻 DontDestroyOnLoad，切场景不会连宿主一起丢。
    // 注意：Unity 销毁组件（Destroy 传 Component）只销毁该组件，不销毁宿主 —— 旧框架「播完销毁 AudioSource」不涉及共享宿主被连带销毁。
    /// <summary>音频总入口：单路 BGM + 可复用音效池 + 三组音量；池满时按优先级抢占或丢弃。</summary>
    public sealed class AudioManager : Singleton<AudioManager>
    {
        /// <summary>音量分组数量，个；取值必须与 AudioBus 的枚举个数一致（3），分组数组按它开。</summary>
        public const int BusCount = 3;

        /// <summary>默认并发播放上限，个；只数音效，BGM 不占额度。</summary>
        public const int DefaultMaxConcurrentVoices = 16;

        /// <summary>宿主对象名；PlayMode 测试按它断言场景里的宿主。</summary>
        public const string HostName = "[NBC]AudioHost";

        // 三组音量，0..1 线性值；下标是 AudioBus 的编号，静音时也不清零，取消静音直接恢复
        private readonly float[] m_busVolumes = { 1f, 1f, 1f };

        // 三组是否静音；与音量分开存 —— 静音要能「记住」原音量，取消静音时直接恢复
        private readonly bool[] m_busMuted = { false, false, false };

        // 正在播的音效，个，不含 BGM；应当与池的 CountInUse 始终一致，有测试盯着这条不变式
        private readonly List<AudioVoice> m_active = new List<AudioVoice>();

        // 本帧结束的播放器缓冲，个；先收集、后派发，避免监听者回调里 Play / Stop 改列表导致遍历错乱
        private readonly List<EndedRecord> m_endedBuffer = new List<EndedRecord>();

        // 宿主对象；播放模式下在 DontDestroyOnLoad 场景里，切场景不销毁
        private GameObject m_host;

        // 音效播放器的父节点；挂在宿主下的 Voices 节点，保持原点无缩放，3D 位置可直接赋世界坐标
        private Transform m_voiceRoot;

        // BGM 专用播放器；单路、不进池、不参与抢占，也不计入并发数
        private AudioSource m_bgmSource;

        // BGM 当前的句柄编号；0 表示没有 BGM，编号由 m_nextVoiceId 发放
        private int m_bgmVoiceId;

        // BGM 的单次音量，0..1 线性值；分组音量变化时要用它重算最终音量
        private float m_bgmRequestVolume = 1f;

        // 播放器池；惰性建，只在播放器不够时新建，回池复用，不设闲置上限
        private ObjectPool<AudioVoice> m_voicePool;

        // 「还在播吗」的探针；默认读 AudioSource.isPlaying，测试可注入假实现
        private IAudioPlaybackProbe m_probe = new UnityAudioPlaybackProbe();

        // 音效并发上限，个，默认 16；BGM 不占额度，池满时按优先级抢占或丢弃
        private int m_maxConcurrentVoices = DefaultMaxConcurrentVoices;

        // 3D 最小距离，米，默认 1；小于它不衰减，必须严格小于 m_max3DDistance
        private float m_min3DDistance = 1f;
        // 3D 最大距离，米，默认 30；超出听不到，必须严格大于 m_min3DDistance
        private float m_max3DDistance = 30f;

        // 下一个播放器编号，从 1 起只增不减；句柄靠它永不复用来避免 ABA
        private int m_nextVoiceId = 1;
        // 播放发号顺序，从 0 起递增；池满时同级取最老的，靠它比较
        private long m_playSequence;

        // 逻辑帧计数，帧；只用来判断「是不是刚起播的那一帧」，帧末自增
        private int m_tickCount;

        // 累计创建过的播放器数，个；连播再多也不该超过并发上限
        private int m_createdCount;
        // 因池满且优先级不够被丢弃的请求数，个
        private int m_droppedCount;
        // 被更高优先级抢占的播放器数，个
        private int m_preemptedCount;
        // 自然播完的数量，个
        private int m_finishedCount;
        // 宿主被外部销毁而丢失的播放器数，个；旧框架在这里抛 MissingReferenceException
        private int m_lostCount;

        /// <summary>一个播放器离开播放列表时触发（播完 / 被抢占 / 被停掉 / 宿主丢了）；调用方在这里释放为该音效加载的素材句柄。</summary>
        // 注意：四种结束原因都必须通知到，漏一种，替它加载的素材句柄就泄漏了。
        public event Action<AudioVoiceHandle, AudioEndReason> VoiceEnded;

        // 宿主 / 容器（诊断用）

        /// <summary>宿主对象；播放模式下它在 DontDestroyOnLoad 场景里，切场景不销毁；没建过时为 null。</summary>
        public GameObject Host
        {
            get { return m_host; }
        }

        /// <summary>音效播放器的父节点；挂在宿主下的 Voices 节点，没建宿主时为 null。</summary>
        public Transform VoiceRoot
        {
            get { return m_voiceRoot; }
        }

        /// <summary>「还在播吗」的探针；可注入假实现，赋 null 会抛 ArgumentNullException。</summary>
        public IAudioPlaybackProbe Probe
        {
            get { return m_probe; }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException(nameof(value));
                }

                m_probe = value;
            }
        }

        // 容量配置

        /// <summary>音效并发上限，个（默认 16）；必须大于 0，也不能小于正在播的数量，BGM 不占额度。</summary>
        public int MaxConcurrentVoices
        {
            get { return m_maxConcurrentVoices; }
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value, "[AudioManager] 并发上限必须大于 0。");
                }

                if (value < m_active.Count)
                {
                    throw new InvalidOperationException(
                        "[AudioManager] 不能把并发上限降到比正在播放的数量（" + m_active.Count +
                        "）还小。先 StopAll() 或等它们播完。");
                }

                m_maxConcurrentVoices = value;
            }
        }

        /// <summary>3D 最小距离，米（此距离内不衰减）；不能为负，必须严格小于最大距离。</summary>
        public float Min3DDistance
        {
            get { return m_min3DDistance; }
            set
            {
                if (value < 0f)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "[AudioManager] 最小距离不能为负。");
                }

                // 两边都校验，保证 min < max 这个不变式从哪个方向设都不会被破坏。
                if (value >= m_max3DDistance)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value,
                        "[AudioManager] 最小距离必须小于最大距离（当前最大 " + m_max3DDistance +
                        "）。若要一起改大，请先设 Max3DDistance。");
                }

                m_min3DDistance = value;
            }
        }

        /// <summary>3D 最大距离，米（超出后听不到）；必须严格大于最小距离。</summary>
        public float Max3DDistance
        {
            get { return m_max3DDistance; }
            set
            {
                if (value <= m_min3DDistance)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value,
                        "[AudioManager] 最大距离必须大于最小距离（当前最小 " + m_min3DDistance + "）。");
                }

                m_max3DDistance = value;
            }
        }

        // 统计

        /// <summary>正在播放的音效数量，个；不含 BGM。</summary>
        public int ActiveVoiceCount
        {
            get { return m_active.Count; }
        }

        /// <summary>池里闲置的播放器数量，个；池还没建时为 0。</summary>
        public int IdleVoiceCount
        {
            get { return m_voicePool != null ? m_voicePool.CountInPool : 0; }
        }

        /// <summary>累计创建过的播放器数量，个；连播 200 个音效也不该超过并发上限，跟着播放次数涨就说明又在用完就扔。</summary>
        public int CreatedVoiceCount
        {
            get { return m_createdCount; }
        }

        /// <summary>因池满且优先级不够而被丢弃的播放请求数，个。</summary>
        public int DroppedCount
        {
            get { return m_droppedCount; }
        }

        /// <summary>被更高优先级抢占掉的播放器数量，个。</summary>
        public int PreemptedCount
        {
            get { return m_preemptedCount; }
        }

        /// <summary>自然播完的数量，个。</summary>
        public int FinishedCount
        {
            get { return m_finishedCount; }
        }

        /// <summary>宿主被外部销毁而丢失的播放器数量，个；旧框架在这种情况下会抛 MissingReferenceException。</summary>
        public int LostCount
        {
            get { return m_lostCount; }
        }

        // 音量分组

        /// <summary>取某个分组的音量，0..1 线性值；不触发任何应用。</summary>
        /// <param name="bus">分组，取值 AudioBus 的 0..2。</param>
        /// <returns>分组音量，0..1 线性值。</returns>
        public float GetBusVolume(AudioBus bus)
        {
            // 功能：按分组下标读出当前音量。
            // 入参：bus 分组，0..2，越界由 BusIndex 抛异常
            // 返回：该分组音量，0..1 线性值
            // 步骤：先转下标（顺带挡非法值），再直接读数组 —— 只读，不改任何播放状态
            // 上游：业务层、测试（目前只有测试调用）
            // 下游：BusIndex
            return m_busVolumes[BusIndex(bus)];
        }

        /// <summary>设置某个分组的音量并立刻应用到正在播的声音；音量钳到 0..1。</summary>
        /// <param name="bus">分组，取值 AudioBus 的 0..2。</param>
        /// <param name="volume">目标音量，0..1 线性值；超出范围自动钳住。</param>
        public void SetBusVolume(AudioBus bus, float volume)
        {
            // 功能：写分组音量，并立刻把它应用到该组正在播的 BGM 或音效上。
            // 入参：bus 分组 0..2；volume 目标音量，0..1 线性值（越界钳住）
            // 返回：无
            // 步骤：
            //   1) 先钳到 0..1 再存 —— 存进去的就是最终会用的值，不留越界状态
            //   2) 最后才 ApplyBusVolume：先存再应用，重算时读到的就是新值
            // 上游：业务层、测试（目前只有测试调用）
            // 下游：ApplyBusVolume
            m_busVolumes[BusIndex(bus)] = Mathf.Clamp01(volume);
            ApplyBusVolume(bus);
        }

        /// <summary>某个分组是否静音。</summary>
        /// <param name="bus">分组，取值 AudioBus 的 0..2。</param>
        /// <returns>true 表示该组静音。</returns>
        public bool IsBusMuted(AudioBus bus)
        {
            // 功能：读分组的静音开关。
            // 入参：bus 分组 0..2
            // 返回：true 表示静音
            // 步骤：先转下标，再直接读数组，不参与音量计算
            // 上游：业务层、测试（目前只有测试调用）
            // 下游：BusIndex
            return m_busMuted[BusIndex(bus)];
        }

        /// <summary>设置某个分组静音；音量值保留，取消静音后回到原来的音量。</summary>
        /// <param name="bus">分组，取值 AudioBus 的 0..2。</param>
        /// <param name="muted">是否静音。</param>
        public void SetBusMuted(AudioBus bus, bool muted)
        {
            // 功能：改分组的静音开关，并立刻把结果应用到正在播的声音。
            // 入参：bus 分组 0..2；muted 是否静音
            // 返回：无
            // 步骤：
            //   1) 只改开关，不动 m_busVolumes —— 静音要能记住原音量
            //   2) 再 ApplyBusVolume 重算，静音时最终音量为 0
            // 上游：业务层、测试（目前只有测试调用）
            // 下游：ApplyBusVolume
            // 注意：静音不是停止播放：isPlaying 仍为 true，探针不会回收，取消静音时声音接着播。
            m_busMuted[BusIndex(bus)] = muted;
            ApplyBusVolume(bus);
        }

        /// <summary>算最终音量：分组音量 × 单次音量，两者都在 0..1；分组静音时直接是 0。</summary>
        /// <param name="bus">分组，取值 AudioBus 的 0..2。</param>
        /// <param name="requestVolume">单次请求音量，0..1 线性值。</param>
        /// <returns>最终音量，0..1 线性值；静音时为 0。</returns>
        public float EffectiveVolume(AudioBus bus, float requestVolume)
        {
            // 功能：把分组音量与单次音量相乘，静音时短路成 0，得到交给 AudioSource 的最终音量。
            // 入参：bus 分组 0..2；requestVolume 单次音量，0..1 线性值
            // 返回：最终音量，0..1 线性值；静音时为 0
            // 步骤：
            //   1) 先转下标，非法分组当场抛异常
            //   2) 静音直接返回 0 —— 不乘也不读音量
            //   3) 否则两个因子各钳一次再相乘：调用方传进来的单次音量不保证在范围内
            // 上游：Play、PlayBgm、ApplyBusVolume
            // 下游：BusIndex
            int index = BusIndex(bus);

            if (m_busMuted[index])
            {
                return 0f;
            }

            return Mathf.Clamp01(m_busVolumes[index]) * Mathf.Clamp01(requestVolume);
        }

        // 播放：音效

        /// <summary>播放一个音效，素材由调用方加载好再传进来；池满且优先级不够时返回 AudioVoiceHandle.None。</summary>
        /// <param name="request">播放请求；Clip 不能为 null，否则抛 ArgumentNullException。</param>
        /// <returns>播放句柄；这次请求被丢弃时是 None（编号 0）。</returns>
        public AudioVoiceHandle Play(AudioPlayRequest request)
        {
            // 功能：校验请求、备好宿主与池，从池里取一个播放器配好参数开始播，并把句柄交给调用方。
            // 入参：request 播放请求（Clip 不能为 null）
            // 返回：播放句柄；池满且优先级不够时返回 None（编号 0）
            // 步骤：
            //   1) 先挡 Clip 为 null —— 素材由调用方加载，本类不碰资源加载，null 进来只会静默不出声
            //   2) 再 EnsureHost / EnsurePool：宿主与池都是惰性建的，顺序不能反（播放器要挂在宿主下）
            //   3) RentVoice 决定复用、抢占还是丢弃；返回 null 表示这次请求被丢弃，计完数直接返回 None
            //   4) 发编号与顺序号：编号只增不复用，顺序号让池满时能按同级取最老挑牺牲者
            //   5) 记录起始逻辑帧与单次音量，再把最终音量（分组 × 单次）交给 Apply 配播放器
            //   6) 最后才加进 m_active —— 前面任何一步失败都不会留下半个在播记录
            // 上游：业务层播音效（目前是 EditMode / PlayMode 测试）
            // 下游：EnsureHost / EnsurePool / RentVoice / AudioVoice.Apply / EffectiveVolume
            // 注意：并发上限只数音效，BGM 不占额度；丢弃是正常结果而不是异常，调用方要判 None。
            if (request.Clip == null)
            {
                throw new ArgumentNullException(
                    nameof(request), "[AudioManager] 播放请求里的 Clip 为 null。素材要先加载好再传进来。");
            }

            EnsureHost();
            EnsurePool();

            AudioVoice voice = RentVoice(request.Priority);

            if (voice == null)
            {
                m_droppedCount++;
                return AudioVoiceHandle.None;
            }

            voice.Id = m_nextVoiceId++;
            voice.Sequence = ++m_playSequence;
            voice.Priority = request.Priority;
            voice.Bus = request.Bus;
            voice.StartFrame = m_tickCount;
            voice.RequestVolume = request.Volume;
            voice.Apply(request, EffectiveVolume(request.Bus, request.Volume), m_min3DDistance, m_max3DDistance);

            m_active.Add(voice);
            return new AudioVoiceHandle(voice.Id);
        }

        /// <summary>某个句柄对应的音效现在是否还在播；用编号比对，编号永不复用，所以旧句柄不会误判成新音效。</summary>
        /// <param name="handle">播放句柄；无效句柄直接返回 false。</param>
        /// <returns>true 表示这个编号还在播放列表里，或它就是当前 BGM。</returns>
        public bool IsActive(AudioVoiceHandle handle)
        {
            // 功能：回答某个句柄现在是否还有效地在播。
            // 入参：handle 播放句柄（无效句柄直接 false）
            // 返回：true 表示编号对应的音效还在 m_active 里，或它就是当前 BGM；否则 false
            // 步骤：
            //   1) 先挡无效句柄，省掉后面的遍历
            //   2) 再比对 BGM 编号 —— BGM 不进 m_active，单独判
            //   3) 最后线性扫 m_active 比编号；播放器会复用但编号永不复用，所以不会把上一轮的句柄认成这一轮的音效（ABA）
            // 上游：业务层查询播放状态、Stop 前判活、测试
            // 下游：m_active、m_bgmVoiceId
            if (!handle.IsValid)
            {
                return false;
            }

            if (handle.Id == m_bgmVoiceId)
            {
                return true;
            }

            for (int i = 0; i < m_active.Count; i++)
            {
                if (m_active[i].Id == handle.Id)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>停掉某个音效，BGM 句柄也可以传进来；停成功会发 VoiceEnded（Stopped）。</summary>
        /// <param name="handle">播放句柄；无效或已经结束的句柄返回 false，不做事。</param>
        /// <returns>true 表示确实停掉了一个正在播的。</returns>
        public bool Stop(AudioVoiceHandle handle)
        {
            // 功能：按句柄找播放器，停掉并回池；句柄指向 BGM 时改为停 BGM。
            // 入参：handle 播放句柄（无效句柄直接 false）
            // 返回：true 表示确实停掉了一个正在播的；false 表示句柄无效或已经结束
            // 步骤：
            //   1) 先挡无效句柄
            //   2) BGM 编号单独处理：BGM 不在 m_active 里，交给 StopBgm 统一发结束通知
            //   3) 线性找编号，命中就先回收（停止 + 回池），再发 Stopped 结束通知
            // 上游：业务层按句柄停止、测试
            // 下游：StopBgm / RecycleVoice / RaiseVoiceEnded
            // 注意：先回收再派发 —— 监听者拿到通知时播放器已经回池，不会看到一个还占着并发名额的已停播放器。
            if (!handle.IsValid)
            {
                return false;
            }

            if (handle.Id == m_bgmVoiceId)
            {
                StopBgm();
                return true;
            }

            for (int i = 0; i < m_active.Count; i++)
            {
                AudioVoice voice = m_active[i];

                if (voice.Id == handle.Id)
                {
                    RecycleVoice(voice);
                    RaiseVoiceEnded(handle, AudioEndReason.Stopped);
                    return true;
                }
            }

            return false;
        }

        /// <summary>停掉所有音效与背景音乐，每个结束的音效都发一次 VoiceEnded（Stopped）。</summary>
        public void StopAll()
        {
            // 功能：把正在播的音效全部停掉回池，再停 BGM，最后统一派发结束通知。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 先清空结束缓冲，避免上一轮残留
            //   2) 倒序遍历 m_active：边遍历边 Remove，倒序才不会跳元素
            //   3) 每个播放器先把「句柄 + 原因」记进缓冲，再回收（停止并回池）
            //   4) 再停 BGM，它有自己的结束通知
            //   5) 最后才统一派发缓冲 —— 监听者可能在回调里再 Play，此时 m_active 已经改完
            // 上游：业务层整场收尾（切场景 / 回大厅）、测试
            // 下游：RecycleVoice / StopBgm / RaiseVoiceEnded
            // 注意：派发必须在遍历之后 —— 边遍历边回调，监听者一 Play 就会改 m_active，遍历当场错乱。
            m_endedBuffer.Clear();

            for (int i = m_active.Count - 1; i >= 0; i--)
            {
                AudioVoice voice = m_active[i];
                m_endedBuffer.Add(new EndedRecord(new AudioVoiceHandle(voice.Id), AudioEndReason.Stopped));
                RecycleVoice(voice);
            }

            StopBgm();

            for (int i = 0; i < m_endedBuffer.Count; i++)
            {
                RaiseVoiceEnded(m_endedBuffer[i].Handle, m_endedBuffer[i].Reason);
            }

            m_endedBuffer.Clear();
        }

        /// <summary>预建若干个播放器，避免第一次播放时才新建造成首播卡顿；数量不能超过并发上限。</summary>
        /// <param name="count">预建数量，个；小于等于 0 直接返回，大于并发上限抛 ArgumentOutOfRangeException。</param>
        public void Prewarm(int count)
        {
            // 功能：先建好若干个闲置播放器，把首次播放的创建开销挪到加载阶段。
            // 入参：count 预建数量，个（小于等于 0 直接返回）
            // 返回：无
            // 步骤：
            //   1) 小于等于 0 直接返回 —— 没有东西要建
            //   2) 超过并发上限就抛异常 —— 建了也用不上，还白占内存
            //   3) 最后交给池的 Prewarm 建出闲置播放器（池不设闲置上限）
            // 上游：业务层预载（目前只有测试调用）
            // 下游：EnsureHost / EnsurePool / ObjectPool.Prewarm
            if (count <= 0)
            {
                return;
            }

            if (count > m_maxConcurrentVoices)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count), count,
                    "[AudioManager] 预建数量不能超过并发上限（" + m_maxConcurrentVoices + "）。");
            }

            EnsureHost();
            EnsurePool();
            m_voicePool.Prewarm(count);
        }

        // 播放：背景音乐

        /// <summary>播放背景音乐：单路，重复调用会替换当前 BGM；BGM 不进池、不参与抢占、不计入并发数。</summary>
        /// <param name="clip">音频素材，不能为 null。</param>
        /// <param name="volume">单次音量，0..1 线性值；最终音量还要乘 Bgm 分组音量。</param>
        /// <param name="loop">是否循环，默认 true。</param>
        /// <returns>新的 BGM 句柄；旧句柄随之失效并收到 VoiceEnded（Stopped）。</returns>
        public AudioVoiceHandle PlayBgm(AudioClip clip, float volume = 1f, bool loop = true)
        {
            // 功能：换上一首 BGM —— 停掉旧的、配好新的专用播放器并开始播，返回新句柄。
            // 入参：clip 音频素材（不能为 null）；volume 单次音量 0..1 线性值；loop 是否循环
            // 返回：新 BGM 的句柄
            // 步骤：
            //   1) 先挡 null 素材
            //   2) 再 EnsureHost：BGM 播放器挂在宿主上，宿主没建就先建
            //   3) 停掉上一首并让它收到结束通知 —— 不通知的话，调用方为旧 BGM 加载的素材句柄就泄漏了
            //   4) 配 clip、循环、单次音量与最终音量、音高归 1，再 Play
            //   5) 最后才发新编号，旧编号已经失效
            // 上游：业务层播音（目前只有测试调用）
            // 下游：EnsureHost / StopBgm / EffectiveVolume
            // 注意：单路 BGM 直接配 AudioSource，不走播放池；要音效那套优先级请用 Play。
            if (clip == null)
            {
                throw new ArgumentNullException(nameof(clip));
            }

            EnsureHost();

            // 换 BGM 等于停掉上一首 —— 要让上一首的句柄收到结束通知，否则素材句柄会泄漏。
            StopBgm();

            m_bgmSource.clip = clip;
            m_bgmSource.loop = loop;
            m_bgmRequestVolume = volume;
            m_bgmSource.volume = EffectiveVolume(AudioBus.Bgm, volume);
            m_bgmSource.pitch = 1f;
            m_bgmSource.Play();

            m_bgmVoiceId = m_nextVoiceId++;
            return new AudioVoiceHandle(m_bgmVoiceId);
        }

        /// <summary>暂停背景音乐；BGM 播放器不存在（宿主已销毁）时什么也不做。</summary>
        public void PauseBgm()
        {
            // 功能：暂停当前 BGM，保留播放位置。
            // 入参：无
            // 返回：无
            // 步骤：先判播放器还在，再 Pause —— 宿主被销毁后 m_bgmSource 是伪 null，直接调会抛 MissingReferenceException
            // 上游：业务层、测试（目前只有测试调用）
            // 下游：AudioSource.Pause
            if (m_bgmSource != null)
            {
                m_bgmSource.Pause();
            }
        }

        /// <summary>继续背景音乐；没有 BGM 或素材已清空时什么也不做。</summary>
        public void ResumeBgm()
        {
            // 功能：从暂停位置继续播当前 BGM。
            // 入参：无
            // 返回：无
            // 步骤：先判播放器与素材都还在，再 UnPause —— 停止后 clip 已被清空，没素材就没什么可继续的
            // 上游：业务层、测试（目前只有测试调用）
            // 下游：AudioSource.UnPause
            if (m_bgmSource != null && m_bgmSource.clip != null)
            {
                m_bgmSource.UnPause();
            }
        }

        /// <summary>停止背景音乐并清空素材；停成功会发 VoiceEnded（Stopped）。</summary>
        /// <returns>true 表示确实停掉了一首；没有 BGM 时 false。</returns>
        public bool StopBgm()
        {
            // 功能：停掉当前 BGM、清空素材与编号，并通知监听者释放素材句柄。
            // 入参：无
            // 返回：true 表示确实停掉了一首；false 表示当前没有 BGM 或宿主已丢
            // 步骤：
            //   1) 先判编号与播放器 —— 没有 BGM 就没有通知要发
            //   2) 用旧编号造句柄再清零：先记下句柄，才发得对是哪一路结束
            //   3) Stop、清 clip、编号归 0，保证 CurrentBgm 立刻返回 None
            //   4) 最后发 Stopped 通知
            // 上游：PlayBgm（换曲）、StopAll、Stop（句柄指向 BGM）、业务层
            // 下游：RaiseVoiceEnded
            if (m_bgmVoiceId == 0 || m_bgmSource == null)
            {
                return false;
            }

            AudioVoiceHandle handle = new AudioVoiceHandle(m_bgmVoiceId);
            m_bgmSource.Stop();
            m_bgmSource.clip = null;
            m_bgmVoiceId = 0;

            RaiseVoiceEnded(handle, AudioEndReason.Stopped);
            return true;
        }

        /// <summary>当前 BGM 的句柄；没有 BGM 时是 AudioVoiceHandle.None（编号 0）。</summary>
        public AudioVoiceHandle CurrentBgm
        {
            get { return new AudioVoiceHandle(m_bgmVoiceId); }
        }

        // 推进

        /// <summary>推进一帧：把已经播完或宿主丢失的播放器摘出列表并回池；播放模式下由 MonoManager 每帧调，测试里直接调。</summary>
        public void Tick()
        {
            // 功能：巡检正在播的播放器，把死了的、播完了的摘掉回池，并延后派发结束通知。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 先清结束缓冲，准备本帧的结束记录
            //   2) 倒序遍历 m_active：后面要 Remove，倒序不会跳元素
            //   3) 宿主被外部销毁的（IsAlive 为 false）先摘掉、计数、记一条 Lost，仍然把播放器还给池 —— 池按取出 / 归还记账，不还 CountInUse 会永久偏高
            //   4) 刚起播那一帧不判定：某些后端同一帧 isPlaying 仍为 false，立刻回收会把刚播的音效掐掉
            //   5) 再问探针；false 才当播完，记 Finished 并回收
            //   6) 全部收完之后统一派发 —— 监听者可能在回调里 Play / Stop，此时 m_active 已改完，不会打乱遍历
            //   7) 帧号最后自增：这样刚起播的判断只跳过一帧，下帧就能正常判定
            // 上游：MonoManager 每帧（OnUpdate）、测试直接调
            // 下游：IAudioPlaybackProbe.IsPlaying / RecycleVoice / RaiseVoiceEnded
            // 注意：不自己按秒数推算播完 —— 探针读 AudioSource 的真实状态，自己记时间会和改 pitch、暂停、编辑器卡顿后的真实进度漂移。
            // 注意：死掉的播放器也要还池；池的 isValid 校验会在下次 Get 时把它丢掉，不会把死对象再发出去。
            m_endedBuffer.Clear();

            for (int i = m_active.Count - 1; i >= 0; i--)
            {
                AudioVoice voice = m_active[i];

                // 防御：宿主被外部销毁 / 随场景销毁
                if (!voice.IsAlive)
                {
                    m_active.RemoveAt(i);
                    m_lostCount++;
                    m_endedBuffer.Add(new EndedRecord(new AudioVoiceHandle(voice.Id), AudioEndReason.Lost));

                    // 死的播放器仍然要还给池：池按取出 / 归还记账（m_inUse），不还它会让 CountInUse 永久偏高，
                    // 并发上限就再也算不准；对象已经死了不影响归还 —— 池的 isValid 校验会在下次 Get 时把它丢掉。
                    if (m_voicePool != null)
                    {
                        m_voicePool.Release(voice);
                    }

                    continue;
                }

                // 刚起播的这一帧不判定：某些后端在同一帧里 isPlaying 仍是 false，
                // 立刻回收会把刚播的音效直接掐掉。
                if (voice.StartFrame >= m_tickCount)
                {
                    continue;
                }

                if (m_probe.IsPlaying(voice.Source))
                {
                    continue;
                }

                m_endedBuffer.Add(new EndedRecord(new AudioVoiceHandle(voice.Id), AudioEndReason.Finished));
                RecycleVoice(voice);
                m_finishedCount++;
            }

            // 先收集、后派发：监听者可能在回调里再次 Play / Stop，
            // 那时 m_active 已经改完了，不会打乱上面的遍历。
            for (int i = 0; i < m_endedBuffer.Count; i++)
            {
                RaiseVoiceEnded(m_endedBuffer[i].Handle, m_endedBuffer[i].Reason);
            }

            // 帧号在最后自增：这样这一帧刚起播的（StartFrame == m_tickCount）
            // 只会在下一次 Tick 被跳过一次，下下次就能正常判定。
            m_tickCount++;
        }

        // 内部：池

        // 功能：从池里取一个播放器；池满时按优先级找牺牲者，严格更低才抢占，否则放弃这次请求。
        // 入参：priority 新请求的优先级，0..3
        // 返回：可用的播放器；null 表示这次请求应当被丢弃（调用方负责计 DroppedCount）
        // 步骤：
        //   1) 没满就直接从池里取，不参与抢占比较
        //   2) 满了就找优先级最低、同级最老的牺牲者
        //   3) 牺牲者不存在、或它的优先级不低于新请求，就放弃 —— 同级不抢
        //   4) 能抢就记 Preempted、回收牺牲者并发抢占通知，再从池里取
        // 上游：Play
        // 下游：FindPreemptionVictim / RecycleVoice / RaiseVoiceEnded / ObjectPool.Get
        // 注意：抢占条件是「严格更低」—— 同级互相抢会让密集打击音听起来像丢音，比直接丢音效更难查。
        // 注意：Critical 没有更高的档，永远抢不走；剧情语音不该被打击音吃掉。
        private AudioVoice RentVoice(AudioPriority priority)
        {
            if (m_active.Count < m_maxConcurrentVoices)
            {
                return m_voicePool.Get();
            }

            AudioVoice victim = FindPreemptionVictim();

            // 严格更低才抢：同级不抢，否则两个同优先级的音效会互相踩。
            if (victim == null || victim.Priority >= priority)
            {
                return null;
            }

            m_preemptedCount++;

            AudioVoiceHandle handle = new AudioVoiceHandle(victim.Id);
            RecycleVoice(victim);
            RaiseVoiceEnded(handle, AudioEndReason.Preempted);

            return m_voicePool.Get();
        }

        // 功能：在正在播的音效里找优先级最低的那个；同级取 Sequence 最小的（最老的）。
        // 入参：无
        // 返回：牺牲者；列表为空或全都已死时返回 null
        // 步骤：先跳过已死的播放器（回收交给 Tick），优先级更低就换，相同则比 Sequence 取更老的
        // 上游：RentVoice
        // 下游：无
        private AudioVoice FindPreemptionVictim()
        {
            AudioVoice victim = null;

            for (int i = 0; i < m_active.Count; i++)
            {
                AudioVoice voice = m_active[i];

                if (!voice.IsAlive)
                {
                    continue;
                }

                bool lower = victim == null || voice.Priority < victim.Priority;
                bool sameButOlder = victim != null
                                    && voice.Priority == victim.Priority
                                    && voice.Sequence < victim.Sequence;

                if (lower || sameButOlder)
                {
                    victim = voice;
                }
            }

            return victim;
        }

        // 功能：停止播放器并还给池，同时从播放列表摘掉。
        // 入参：voice 播放器；null 直接返回
        // 返回：无
        // 步骤：先 StopAndReset 清干净本轮参数，再从 m_active 移除，最后 Release 回池
        // 上游：Stop、StopAll、Tick、RentVoice
        // 下游：AudioVoice.StopAndReset / ObjectPool.Release
        // 注意：不销毁任何东西 —— 复用就是靠「只停不销毁」消掉旧框架反复 AddComponent / Destroy 的抖动。
        private void RecycleVoice(AudioVoice voice)
        {
            if (voice == null)
            {
                return;
            }

            voice.StopAndReset();
            m_active.Remove(voice);
            m_voicePool.Release(voice);
        }

        // 功能：池的工厂 —— 新建一个播放器并累计创建数。
        // 入参：无
        // 返回：新播放器，挂在宿主下的 Voices 节点
        // 步骤：先累计 m_createdCount，再建 AudioVoice；这个计数只用于观测，创建失败时偏高也不影响池
        // 上游：ObjectPool.Get（池里没有可用对象时）
        // 下游：AudioVoice 构造函数
        // 注意：m_createdCount 是验证池有没有复用的关键数字：连播再多也不该超过并发上限，跟着播放次数涨就说明又在用完就扔。
        private AudioVoice CreateVoice()
        {
            m_createdCount++;
            return new AudioVoice("AudioVoice", m_voiceRoot);
        }

        // 功能：池的销毁器 —— 真正销毁播放器的宿主。
        // 入参：voice 播放器；null 直接返回
        // 返回：无
        // 步骤：判空后调 AudioVoice.Dispose，由它按运行模式选 Destroy 或 DestroyImmediate
        // 上游：ObjectPool.Clear / Dispose
        // 下游：AudioVoice.Dispose
        private static void DisposeVoice(AudioVoice voice)
        {
            if (voice != null)
            {
                voice.Dispose();
            }
        }

        // 功能：池的校验器 —— 判断播放器是否还活着。
        // 入参：voice 池里的播放器，可能是已被外部销毁的伪 null
        // 返回：true 表示宿主与组件都还在
        // 步骤：先判引用非空，再读 IsAlive
        // 上游：ObjectPool.Get（取到闲置对象时先校验）
        // 下游：AudioVoice.IsAlive
        private static bool IsVoiceValid(AudioVoice voice)
        {
            return voice != null && voice.IsAlive;
        }

        // 功能：惰性建播放器池，把工厂、销毁器、校验器接上。
        // 入参：无
        // 返回：无
        // 步骤：已建过就直接返回；否则创建 ObjectPool，闲置上限传 0（不限）—— 并发上限由本类管，池只负责复用
        // 上游：Play、Prewarm
        // 下游：ObjectPool 构造函数
        // 注意：池不设闲置上限：能建多少由 m_maxConcurrentVoices 决定，闲置上限再拦一刀只会让回池对象被反复销毁重建。
        private void EnsurePool()
        {
            if (m_voicePool != null)
            {
                return;
            }

            m_voicePool = new ObjectPool<AudioVoice>(
                "AudioVoice", CreateVoice, maxSize: 0, initialSize: 0,
                destroyer: DisposeVoice, isValid: IsVoiceValid);
        }

        // 内部：宿主

        // 功能：惰性创建宿主、Voices 父节点与 BGM 播放器。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 已建过就直接返回
        //   2) 建宿主；只在播放模式下 DontDestroyOnLoad —— EditMode 下调它会报错，而 EditMode 测试需要宿主存在
        //   3) 建 Voices 节点挂到宿主下，作为音效播放器的父节点
        //   4) 在宿主上挂 BGM 用的 AudioSource，关掉 playOnAwake、开循环、2D
        // 上游：OnInit、Play、PlayBgm、Prewarm、测试
        // 下游：DontDestroyOnLoad / AddComponent
        // 注意：DontDestroyOnLoad 的这个分支是刻意的：EditMode 下调它 Unity 会报错，而 EditMode 测试需要宿主存在，否则池逻辑没法测。
        private void EnsureHost()
        {
            if (m_host != null)
            {
                return;
            }

            m_host = new GameObject(HostName);

            if (Application.isPlaying)
            {
                // 宿主跟着整个游戏走，不随场景切换被销毁
                UnityEngine.Object.DontDestroyOnLoad(m_host);
            }

            GameObject root = new GameObject("Voices");
            root.transform.SetParent(m_host.transform, false);
            m_voiceRoot = root.transform;

            m_bgmSource = m_host.AddComponent<AudioSource>();
            m_bgmSource.playOnAwake = false;
            m_bgmSource.loop = true;
            m_bgmSource.spatialBlend = 0f;
        }

        // 功能：销毁宿主对象，连上面的播放器与 Voices 节点一起。
        // 入参：无
        // 返回：无
        // 步骤：先判空；播放模式用 Destroy（帧末销毁），EditMode 用 DestroyImmediate（否则 Unity 报错）
        // 上游：OnDispose
        // 下游：Object.Destroy / Object.DestroyImmediate
        // 注意：EditMode 下不能调 Object.Destroy —— Unity 会报「Destroy may not be called from edit mode」，污染测试日志断言。
        private void DestroyHost()
        {
            if (m_host == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(m_host);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(m_host);
            }
        }

        // 内部：杂项

        // 功能：把某个分组的音量 / 静音变化应用到正在播的声音上。
        // 入参：bus 发生变化的组，0..2
        // 返回：无
        // 步骤：
        //   1) BGM 组只重算 BGM：用 m_bgmRequestVolume 乘分组音量，不能拿当前最终音量再乘
        //   2) 其他组遍历 m_active，只改同组且还活着的播放器
        //   3) 每个播放器都按「分组音量 × 单次音量」重算，不做增量乘
        // 上游：SetBusVolume / SetBusMuted
        // 下游：EffectiveVolume / AudioVoice.SetVolume
        private void ApplyBusVolume(AudioBus bus)
        {
            if (bus == AudioBus.Bgm)
            {
                if (m_bgmSource != null)
                {
                    m_bgmSource.volume = EffectiveVolume(AudioBus.Bgm, m_bgmRequestVolume);
                }

                return;
            }

            for (int i = 0; i < m_active.Count; i++)
            {
                AudioVoice voice = m_active[i];

                if (voice.IsAlive && voice.Bus == bus)
                {
                    voice.SetVolume(EffectiveVolume(bus, voice.RequestVolume));
                }
            }
        }

        // 功能：把分组枚举转成数组下标，顺带挡住越界值。
        // 入参：bus 分组
        // 返回：下标 0..2
        // 步骤：先强转，再判 0..BusCount-1；越界抛 ArgumentOutOfRangeException —— 枚举是强类型的，但强转进来的值挡不住
        // 上游：所有分组相关方法
        // 下游：无
        private static int BusIndex(AudioBus bus)
        {
            int index = (int)bus;

            if (index < 0 || index >= BusCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bus), bus, "[AudioManager] 未知的音量分组。");
            }

            return index;
        }

        // 功能：把结束事件派发给监听者。
        // 入参：handle 结束的句柄；reason 离开原因
        // 返回：无
        // 步骤：先把事件取到本地副本再调 —— 判空与调用看到的是同一份订阅者列表
        // 上游：Tick、Stop、StopAll、RentVoice、StopBgm
        // 下游：VoiceEnded 订阅者
        // 注意：监听者抛的异常会照常往上冒，这里不吞异常；需要在回调里释放素材的订阅者，要自己保证每条路径都走到。
        private void RaiseVoiceEnded(AudioVoiceHandle handle, AudioEndReason reason)
        {
            Action<AudioVoiceHandle, AudioEndReason> handlers = VoiceEnded;

            if (handlers != null)
            {
                handlers(handle, reason);
            }
        }

        // 生命周期

        // 功能：单例初始化 —— 建宿主，播放模式下挂到 MonoManager 的每帧回调上。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 先 EnsureHost：宿主与 BGM 播放器要在任何人能播之前就位
        //   2) 只在播放模式挂 OnUpdate —— EditMode 没有 MonoBehaviour 循环，测试直接调 Tick
        // 上游：Singleton.Instance 首次取用时
        // 下游：EnsureHost / MonoManager.AddUpdateListener
        protected override void OnInit()
        {
            EnsureHost();

            if (Application.isPlaying)
            {
                MonoManager.Instance.AddUpdateListener(OnUpdate);
            }
        }

        // 功能：每帧回调，转给 Tick 回收播完的播放器。
        private void OnUpdate()
        {
            Tick();
        }

        // 功能：单例销毁时把更新回调、播放器池、宿主、统计与音量状态一起复位。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 先摘掉 MonoManager 的每帧回调，避免销毁后还被驱动
        //   2) 清池并真正销毁闲置播放器（destroyItems 为 true），再释放池
        //   3) 清播放列表与结束缓冲
        //   4) 复位 BGM 字段、销毁宿主与父节点引用
        //   5) 复位编号、距离、上限、统计与三组音量
        //   6) 最后重建探针、清空事件订阅
        // 上游：Singleton.DisposeInstance / 测试的 TearDown
        // 下游：MonoManager.RemoveUpdateListener / ObjectPool.Clear / DestroyHost
        // 注意：播放器必须真正销毁（Clear 传 true），否则单例没了、GameObject 还挂在场景里，是另一种泄漏。
        protected override void OnDispose()
        {
            if (Application.isPlaying && MonoManager.HasInstance)
            {
                MonoManager.Instance.RemoveUpdateListener(OnUpdate);
            }

            if (m_voicePool != null)
            {
                m_voicePool.Clear(destroyItems: true);
                m_voicePool.Dispose();
                m_voicePool = null;
            }

            m_active.Clear();
            m_endedBuffer.Clear();

            m_bgmSource = null;
            m_bgmVoiceId = 0;
            m_bgmRequestVolume = 1f;

            DestroyHost();
            m_host = null;
            m_voiceRoot = null;

            m_nextVoiceId = 1;
            m_playSequence = 0L;
            m_tickCount = 0;
            m_maxConcurrentVoices = DefaultMaxConcurrentVoices;
            m_min3DDistance = 1f;
            m_max3DDistance = 30f;

            m_createdCount = 0;
            m_droppedCount = 0;
            m_preemptedCount = 0;
            m_finishedCount = 0;
            m_lostCount = 0;

            for (int i = 0; i < BusCount; i++)
            {
                m_busVolumes[i] = 1f;
                m_busMuted[i] = false;
            }

            m_probe = new UnityAudioPlaybackProbe();
            VoiceEnded = null;
        }

        // 主要功能：把「哪个句柄」与「为什么结束」绑成一条记录，先缓冲再派发，保证结束时能告诉调用方真实原因。
        // 主要数据成员：Handle（结束的播放句柄）、Reason（离开播放列表的原因）；两个字段都 readonly。
        // 主要外部方法：无，只有构造函数。
        // 谁在用它：AudioManager 的 m_endedBuffer；Tick、StopAll 往缓冲里写，遍历完再统一派发到 VoiceEnded。
        // 注意：两个字段必须一起缓冲 —— 只缓冲句柄的话，Lost（宿主丢了）和 Finished（正常播完）都会被当成 Finished，调用方分不清「播完了」和「宿主没了」，正是要修的那类静默错误。
        private readonly struct EndedRecord
        {
            // 播放句柄，编号从 1 起；0 不会出现在记录里
            public readonly AudioVoiceHandle Handle;

            // 离开播放列表的原因：播完 / 被抢占 / 被停掉 / 宿主丢失
            public readonly AudioEndReason Reason;

            // 功能：存下句柄与原因，得到一条不可变的结束记录。
            // 入参：handle 结束的播放句柄；reason 离开原因
            // 返回：无（构造函数）
            // 步骤：逐个字段搬值，顺序与声明一致，不做校验
            // 上游：Tick、StopAll
            // 下游：无
            public EndedRecord(AudioVoiceHandle handle, AudioEndReason reason)
            {
                Handle = handle;
                Reason = reason;
            }
        }
    }
}
