// ============================================================================
//  NBC.Framework.Audio · 音频管理（BGM + 音效池 + 音量分组）
//  替代：唐老师框架 Music/MusicMgr.cs（原 139 行）
//  缺陷编号：FW-09（① 不复用 ② 宿主未 DontDestroyOnLoad ③ Update 无防御）、P-06、P-07
//  需求条目：FW-M08（背景音乐 + 3D 音效池 + 音量分组 + 回池）
//  完整记录：Docs/06-框架改造记录.md §三 FW-09、§四 P-06 / P-07
//
//  ---------------------------------------------------------------------------
//  原版错在哪（真实代码，`Music/MusicMgr.cs`）
//  ---------------------------------------------------------------------------
//      private void Update()                                   // :27-34
//      {
//          for( int i = soundList.Count - 1; i >= 0; --i )
//              if( !soundList[i].isPlaying ) { GameObject.Destroy(soundList[i]); soundList.RemoveAt(i); }
//      }
//
//      public void PlaySound(string name, bool isLoop, UnityAction<AudioSource> callBack = null)
//      {
//          if(soundObj == null) { soundObj = new GameObject(); soundObj.name = "Sound"; }   // :97-101
//          ResMgr.GetInstance().LoadAsync<AudioClip>("Music/Sound/" + name, (clip) =>
//          {
//              AudioSource source = soundObj.AddComponent<AudioSource>();                   // :105
//              ...
//          });
//      }
//
//    ① **不复用**：每次播放都 `AddComponent<AudioSource>()`，播完销毁组件。
//       没有池、没有复用、没有并发上限 → 密集音效时组件创建/销毁抖动。
//    ② **宿主未 `DontDestroyOnLoad`**：切场景后 `soundObj` 被销毁，字段仍持引用（伪 null）
//       → 下次 `PlaySound` 往已销毁对象上 `AddComponent` → `MissingReferenceException`（P-06）。
//    ③ `Update` 里直接访问 `soundList[i].isPlaying`，**无有效性防御**（P-07）。
//
//  ⚠️ 另外更正审计报告自己的一处错误：它说 `GameObject.Destroy(AudioSource)` 是
//     "等价于销毁其 gameObject"。**Unity 官方文档相反**：传 Component 时只销毁该组件，
//     宿主还活着。所以不存在"把共享宿主一起销毁"的连锁问题。
//
//  ---------------------------------------------------------------------------
//  本类的四条修法，一一对应上面的问题
//  ---------------------------------------------------------------------------
//    ① **播放器池**（复用 A2 的 `ObjectPool<AudioVoice>`）：取出 → 播放 → `StopAndReset()` 回池。
//       **播完不销毁任何东西。**
//    ② 宿主 `[NBC]AudioHost` 在**播放模式下**立刻 `DontDestroyOnLoad`（`EnsureHost`）。
//    ③ `Tick()` 里先查 `IsAlive` 再碰播放器；探针 `IsPlaying` 自身也容忍 null。
//    ④ 音量**分组**（BGM / SFX / UI），各组独立可调、可静音（FW-M08）。
//
//  ---------------------------------------------------------------------------
//  「池满了」怎么办 —— 规则必须写死，不能靠猜
//  ---------------------------------------------------------------------------
//  并发上限默认 16（`MaxConcurrentVoices`）。满了以后再 `Play`：
//    1. 在**正在播的**里找"优先级最低"的那个；**同级取最老的**（按 `Sequence`）
//    2. 若它的优先级**严格低于**新请求 → **抢占**它（停掉、复用），`PreemptedCount++`
//    3. 否则 → **丢掉新请求**，返回 `AudioVoiceHandle.None`，`DroppedCount++`
//
//  ⚠️ "严格更低"这个措辞很关键：**同级不抢**。否则两个同优先级的音效会互相踩，
//     表现是"密集打击音时后一个把前一个吃掉"，比丢音效更难查。
//  ⚠️ 因此 `Critical` 永远不会被抢占（没有比它更高的优先级）——
//     这正是它存在的意义（剧情语音不该被打击音吃掉）。
//
//  ---------------------------------------------------------------------------
//  为什么 `Tick()` **不带** deltaTime（和 A5 / A7 不一样，这是刻意的）
//  ---------------------------------------------------------------------------
//  A5 的超时、A7 的帧号都需要时间，所以把时间做成参数。这里**不需要**：
//  "这一路播完了没有"由**探针**回答，而不是由"我们记的秒数"推算 ——
//  自己算时间会和 `AudioSource` 的真实进度**漂移**（改 pitch、暂停、编辑器卡顿都会偏）。
//  **不引入第二份时间，就不会有不一致。**
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NBC.Framework.Audio
{
    /// <summary>
    /// 音频管理：背景音乐 + 音效播放器池 + 音量分组。
    /// </summary>
    public sealed class AudioManager : Singleton<AudioManager>
    {
        /// <summary>音量分组数量（<see cref="AudioBus"/> 的取值个数）。</summary>
        public const int BusCount = 3;

        /// <summary>默认并发播放上限。</summary>
        public const int DefaultMaxConcurrentVoices = 16;

        /// <summary>宿主对象名。</summary>
        public const string HostName = "[NBC]AudioHost";

        /// <summary>各分组音量（0..1）。</summary>
        private readonly float[] m_busVolumes = { 1f, 1f, 1f };

        /// <summary>各分组是否静音。**独立于音量** —— 静音要能"记住"原音量。</summary>
        private readonly bool[] m_busMuted = { false, false, false };

        /// <summary>正在播放的音效（不含 BGM）。它和池的 `CountInUse` 应当**始终一致**（有测试盯着）。</summary>
        private readonly List<AudioVoice> m_active = new List<AudioVoice>();

        /// <summary>本帧结束的播放器缓冲。**先收集、后派发**，避免监听者回调里改列表导致遍历错乱。</summary>
        private readonly List<EndedRecord> m_endedBuffer = new List<EndedRecord>();

        /// <summary>BGM 的宿主对象（DontDestroyOnLoad）。</summary>
        private GameObject m_host;

        /// <summary>音效播放器的父节点。</summary>
        private Transform m_voiceRoot;

        /// <summary>BGM 专用播放器（单路，不进池、不参与抢占）。</summary>
        private AudioSource m_bgmSource;

        /// <summary>BGM 当前的句柄编号；0 表示没有 BGM。</summary>
        private int m_bgmVoiceId;

        /// <summary>BGM 的单次音量（分组音量调整时要用它重算）。</summary>
        private float m_bgmRequestVolume = 1f;

        /// <summary>播放器池。</summary>
        private ObjectPool<AudioVoice> m_voicePool;

        /// <summary>"还在播吗"的探针（测试里可注入）。</summary>
        private IAudioPlaybackProbe m_probe = new UnityAudioPlaybackProbe();

        private int m_maxConcurrentVoices = DefaultMaxConcurrentVoices;

        private float m_min3DDistance = 1f;
        private float m_max3DDistance = 30f;

        private int m_nextVoiceId = 1;
        private long m_playSequence;

        /// <summary>逻辑帧计数。**只用来判断"是不是刚起播的那一帧"**。</summary>
        private int m_tickCount;

        private int m_createdCount;
        private int m_droppedCount;
        private int m_preemptedCount;
        private int m_finishedCount;
        private int m_lostCount;

        /// <summary>
        /// 一个播放器**离开播放列表**时触发（播完 / 被抢占 / 被停掉 / 宿主丢了）。
        /// <para>
        /// 用途：调用方在这里释放它为该音效加载的素材句柄（见 <see cref="AudioEndReason"/>）。
        /// </para>
        /// </summary>
        public event Action<AudioVoiceHandle, AudioEndReason> VoiceEnded;

        // ====================================================================
        //  宿主 / 容器（诊断用）
        // ====================================================================

        /// <summary>宿主对象；**播放模式下它在 `DontDestroyOnLoad` 场景里**。</summary>
        public GameObject Host
        {
            get { return m_host; }
        }

        /// <summary>音效播放器的父节点。</summary>
        public Transform VoiceRoot
        {
            get { return m_voiceRoot; }
        }

        /// <summary>"还在播吗"的探针。</summary>
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

        // ====================================================================
        //  容量配置
        // ====================================================================

        /// <summary>同时播放的音效上限（默认 16）。BGM 不占额度。</summary>
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

        /// <summary>3D 音效的最小距离（此距离内不衰减）。**必须小于最大距离。**</summary>
        public float Min3DDistance
        {
            get { return m_min3DDistance; }
            set
            {
                if (value < 0f)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "[AudioManager] 最小距离不能为负。");
                }

                // 两边都校验，保证 min < max 这个不变式**从哪个方向设都不会被破坏**。
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

        /// <summary>3D 音效的最大距离（超出后听不到）。必须大于 <see cref="Min3DDistance"/>。</summary>
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

        // ====================================================================
        //  统计（FW-09 的验证就靠这几个数）
        // ====================================================================

        /// <summary>正在播放的音效数量（不含 BGM）。</summary>
        public int ActiveVoiceCount
        {
            get { return m_active.Count; }
        }

        /// <summary>池里闲置的播放器数量。</summary>
        public int IdleVoiceCount
        {
            get { return m_voicePool != null ? m_voicePool.CountInPool : 0; }
        }

        /// <summary>
        /// **累计创建过**的播放器数量。
        /// <para>
        /// ⚠️ 这就是 FW-09 验证单里的那个数字：连播 200 个音效后它**不该超过并发上限**。
        /// 如果它跟着播放次数一起涨，说明又在"用完就扔"了。
        /// </para>
        /// </summary>
        public int CreatedVoiceCount
        {
            get { return m_createdCount; }
        }

        /// <summary>因为池满且优先级不够而被丢弃的播放请求数。</summary>
        public int DroppedCount
        {
            get { return m_droppedCount; }
        }

        /// <summary>被更高优先级抢占掉的播放器数量。</summary>
        public int PreemptedCount
        {
            get { return m_preemptedCount; }
        }

        /// <summary>自然播完的数量。</summary>
        public int FinishedCount
        {
            get { return m_finishedCount; }
        }

        /// <summary>宿主被外部销毁而丢失的播放器数量（原版这里是 `MissingReferenceException`）。</summary>
        public int LostCount
        {
            get { return m_lostCount; }
        }

        // ====================================================================
        //  音量分组（FW-M08）
        // ====================================================================

        /// <summary>取某个分组的音量。</summary>
        /// <param name="bus">分组。</param>
        /// <returns>音量（0..1）。</returns>
        public float GetBusVolume(AudioBus bus)
        {
            return m_busVolumes[BusIndex(bus)];
        }

        /// <summary>
        /// 设置某个分组的音量。**正在播的会立刻跟着变**。
        /// </summary>
        /// <param name="bus">分组。</param>
        /// <param name="volume">音量（自动钳到 0..1）。</param>
        public void SetBusVolume(AudioBus bus, float volume)
        {
            m_busVolumes[BusIndex(bus)] = Mathf.Clamp01(volume);
            ApplyBusVolume(bus);
        }

        /// <summary>某个分组是否静音。</summary>
        /// <param name="bus">分组。</param>
        /// <returns>是否静音。</returns>
        public bool IsBusMuted(AudioBus bus)
        {
            return m_busMuted[BusIndex(bus)];
        }

        /// <summary>
        /// 设置某个分组静音。**音量值会被保留**，取消静音后恢复原音量。
        /// </summary>
        /// <param name="bus">分组。</param>
        /// <param name="muted">是否静音。</param>
        public void SetBusMuted(AudioBus bus, bool muted)
        {
            m_busMuted[BusIndex(bus)] = muted;
            ApplyBusVolume(bus);
        }

        /// <summary>
        /// 算最终音量：**分组音量 × 单次音量**，静音时是 0。
        /// </summary>
        /// <param name="bus">分组。</param>
        /// <param name="requestVolume">单次请求音量。</param>
        /// <returns>最终音量。</returns>
        public float EffectiveVolume(AudioBus bus, float requestVolume)
        {
            int index = BusIndex(bus);

            if (m_busMuted[index])
            {
                return 0f;
            }

            return Mathf.Clamp01(m_busVolumes[index]) * Mathf.Clamp01(requestVolume);
        }

        // ====================================================================
        //  播放：音效
        // ====================================================================

        /// <summary>
        /// 播放一个音效。**素材由调用方加载好再传进来**（本类不碰资源加载）。
        /// </summary>
        /// <param name="request">播放请求。</param>
        /// <returns>播放句柄；**池满且优先级不够时返回 <see cref="AudioVoiceHandle.None"/>**。</returns>
        public AudioVoiceHandle Play(AudioPlayRequest request)
        {
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

        /// <summary>
        /// 某个句柄对应的音效**现在是否还在播**。
        /// <para>
        /// ⚠️ 用**编号**比对，不是比对对象引用 —— 播放器会被复用，但编号**永不复用**。
        /// 所以"上一轮拿到的句柄"不会误判成"这一轮正在播的音效"（ABA 问题）。
        /// </para>
        /// </summary>
        /// <param name="handle">播放句柄。</param>
        /// <returns>true 表示还在播。</returns>
        public bool IsActive(AudioVoiceHandle handle)
        {
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

        /// <summary>
        /// 停掉某个音效。**BGM 的句柄也可以传进来**。
        /// </summary>
        /// <param name="handle">播放句柄。</param>
        /// <returns>true 表示确实停掉了一个正在播的。</returns>
        public bool Stop(AudioVoiceHandle handle)
        {
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

        /// <summary>停掉所有音效与背景音乐。</summary>
        public void StopAll()
        {
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

        /// <summary>
        /// 预建若干个播放器，避免第一次播放时才创建（首播卡顿）。
        /// </summary>
        /// <param name="count">预建数量。</param>
        public void Prewarm(int count)
        {
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

        // ====================================================================
        //  播放：背景音乐
        // ====================================================================

        /// <summary>
        /// 播放背景音乐（单路，重复调用会**替换**当前 BGM）。
        /// <para>BGM 不进池、不参与抢占、不计入 <see cref="ActiveVoiceCount"/>。</para>
        /// </summary>
        /// <param name="clip">音频素材。</param>
        /// <param name="volume">单次音量（0..1）。</param>
        /// <param name="loop">是否循环。</param>
        /// <returns>播放句柄。</returns>
        public AudioVoiceHandle PlayBgm(AudioClip clip, float volume = 1f, bool loop = true)
        {
            if (clip == null)
            {
                throw new ArgumentNullException(nameof(clip));
            }

            EnsureHost();

            // 换 BGM 等于"停掉上一首"—— 要让上一首的句柄收到结束通知，否则素材句柄会泄漏。
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

        /// <summary>暂停背景音乐。</summary>
        public void PauseBgm()
        {
            if (m_bgmSource != null)
            {
                m_bgmSource.Pause();
            }
        }

        /// <summary>继续背景音乐。</summary>
        public void ResumeBgm()
        {
            if (m_bgmSource != null && m_bgmSource.clip != null)
            {
                m_bgmSource.UnPause();
            }
        }

        /// <summary>停止背景音乐。</summary>
        /// <returns>true 表示确实停掉了一首。</returns>
        public bool StopBgm()
        {
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

        /// <summary>当前 BGM 的句柄；没有则 <see cref="AudioVoiceHandle.None"/>。</summary>
        public AudioVoiceHandle CurrentBgm
        {
            get { return new AudioVoiceHandle(m_bgmVoiceId); }
        }

        // ====================================================================
        //  推进
        // ====================================================================

        /// <summary>
        /// 推进一帧：回收已经播完的播放器。
        /// <para>播放模式下由 <c>MonoManager</c> 每帧驱动；**测试里直接调**。</para>
        /// </summary>
        public void Tick()
        {
            m_endedBuffer.Clear();

            for (int i = m_active.Count - 1; i >= 0; i--)
            {
                AudioVoice voice = m_active[i];

                // —— 防御（P-07）：宿主被外部销毁 / 随场景销毁 ——
                if (!voice.IsAlive)
                {
                    m_active.RemoveAt(i);
                    m_lostCount++;
                    m_endedBuffer.Add(new EndedRecord(new AudioVoiceHandle(voice.Id), AudioEndReason.Lost));

                    // ⚠️ 死的播放器**仍然要还给池**：池是按"取出 / 归还"记账的（m_inUse），
                    // 不还的话它的 CountInUse 会永久偏高，并发上限就再也算不准了。
                    // 对象已经死了不影响归还 —— 池的 isValid 校验会在下次 Get 时把它丢掉。
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

            // **先收集、后派发**：监听者可能在回调里再次 Play / Stop，
            // 那时 m_active 已经改完了，不会打乱上面的遍历。
            for (int i = 0; i < m_endedBuffer.Count; i++)
            {
                RaiseVoiceEnded(m_endedBuffer[i].Handle, m_endedBuffer[i].Reason);
            }

            // ⚠️ 帧号在**最后**自增。这样"这一帧刚起播的"（StartFrame == m_tickCount）
            // 只会在下一次 Tick 被跳过**一次**，下下次就能正常判定。
            m_tickCount++;
        }

        // ====================================================================
        //  内部：池
        // ====================================================================

        /// <summary>
        /// 取一个可用播放器。池满时按优先级规则**抢占或放弃**。
        /// </summary>
        /// <param name="priority">新请求的优先级。</param>
        /// <returns>可用播放器；返回 null 表示这次请求应当被丢弃。</returns>
        private AudioVoice RentVoice(AudioPriority priority)
        {
            if (m_active.Count < m_maxConcurrentVoices)
            {
                return m_voicePool.Get();
            }

            AudioVoice victim = FindPreemptionVictim();

            // "严格更低"才抢：同级不抢，否则两个同优先级的音效会互相踩。
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

        /// <summary>
        /// 找"最该被牺牲"的那个：**优先级最低，同级取最老**。
        /// </summary>
        /// <returns>牺牲者；没有可用候选时返回 null。</returns>
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

        /// <summary>
        /// 停止播放器并把它还给池。**不销毁任何东西**（这就是 FW-09 问题①的修法）。
        /// </summary>
        /// <param name="voice">播放器。</param>
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

        /// <summary>创建播放器（池的工厂）。</summary>
        /// <returns>新播放器。</returns>
        private AudioVoice CreateVoice()
        {
            m_createdCount++;
            return new AudioVoice("AudioVoice", m_voiceRoot);
        }

        /// <summary>销毁播放器（池的销毁器）。</summary>
        /// <param name="voice">播放器。</param>
        private static void DisposeVoice(AudioVoice voice)
        {
            if (voice != null)
            {
                voice.Dispose();
            }
        }

        /// <summary>播放器是否仍然可用（池的校验器）。</summary>
        /// <param name="voice">播放器。</param>
        /// <returns>是否可用。</returns>
        private static bool IsVoiceValid(AudioVoice voice)
        {
            return voice != null && voice.IsAlive;
        }

        /// <summary>惰性建池。**并发上限由本类管，池只负责复用**，所以池不设闲置上限。</summary>
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

        // ====================================================================
        //  内部：宿主
        // ====================================================================

        /// <summary>
        /// 惰性创建宿主。
        /// <para>
        /// ⚠️ **`DontDestroyOnLoad` 只在播放模式下调用** —— EditMode 下调它 Unity 会报错，
        /// 而 EditMode 测试需要宿主存在（否则池逻辑没法测）。这个分支是**刻意的**。
        /// </para>
        /// </summary>
        private void EnsureHost()
        {
            if (m_host != null)
            {
                return;
            }

            m_host = new GameObject(HostName);

            if (Application.isPlaying)
            {
                // —— P-06 的修法：宿主跟着整个游戏走，不随场景切换被销毁 ——
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

        /// <summary>销毁宿主对象。</summary>
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

        // ====================================================================
        //  内部：杂项
        // ====================================================================

        /// <summary>把分组音量的变化应用到正在播的东西上。</summary>
        /// <param name="bus">发生变化的组。</param>
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

        /// <summary>分组枚举转下标，顺带挡住非法值。</summary>
        /// <param name="bus">分组。</param>
        /// <returns>下标。</returns>
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

        /// <summary>派发结束事件，并挡住监听者抛异常影响主流程之外的东西。</summary>
        /// <param name="handle">句柄。</param>
        /// <param name="reason">原因。</param>
        private void RaiseVoiceEnded(AudioVoiceHandle handle, AudioEndReason reason)
        {
            Action<AudioVoiceHandle, AudioEndReason> handlers = VoiceEnded;

            if (handlers != null)
            {
                handlers(handle, reason);
            }
        }

        // ====================================================================
        //  生命周期
        // ====================================================================

        /// <summary>
        /// 建宿主；播放模式下挂到 <c>MonoManager</c> 上。
        /// </summary>
        protected override void OnInit()
        {
            EnsureHost();

            if (Application.isPlaying)
            {
                MonoManager.Instance.AddUpdateListener(OnUpdate);
            }
        }

        /// <summary>每帧回收播完的播放器。</summary>
        private void OnUpdate()
        {
            Tick();
        }

        /// <summary>
        /// 销毁时把宿主、池、统计**一起清干净**。
        /// <para>
        /// ⚠️ 播放器必须**真正销毁**（`Clear(true)`），否则单例销毁了、
        /// GameObject 还挂在场景里 —— 那就是另一形式的泄漏。
        /// </para>
        /// </summary>
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

        /// <summary>
        /// "先收集、后派发"用的记录项：**句柄 + 离开原因**。
        /// <para>
        /// ⚠️ 两个字段必须一起缓冲。第一版只缓冲了句柄，结果 `Lost`（宿主丢了）
        /// 和 `Finished`（正常播完）派发出去的原因**都是 `Finished`** —— 调用方
        /// 分不清"播完了"和"宿主没了"，这正是本次要修的那类静默错误。
        /// </para>
        /// </summary>
        private readonly struct EndedRecord
        {
            /// <summary>播放句柄。</summary>
            public readonly AudioVoiceHandle Handle;

            /// <summary>离开播放列表的原因。</summary>
            public readonly AudioEndReason Reason;

            /// <summary>构造记录项。</summary>
            /// <param name="handle">播放句柄。</param>
            /// <param name="reason">离开原因。</param>
            public EndedRecord(AudioVoiceHandle handle, AudioEndReason reason)
            {
                Handle = handle;
                Reason = reason;
            }
        }
    }
}
