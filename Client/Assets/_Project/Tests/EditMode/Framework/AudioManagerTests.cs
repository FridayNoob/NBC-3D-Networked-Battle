// ============================================================================
//  M1-A8 · AudioManager 的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md V1 / V2
//  对应记录：Docs/06-框架改造记录.md §三 FW-09、§四 P-06 / P-07
//
//  ---------------------------------------------------------------------------
//  这个文件要回答的核心问题（FW-09 的验证）：
//  ---------------------------------------------------------------------------
//    · 音效播完是**回池**还是**被销毁**？     → `Tick_RecyclesFinishedVoice...`、
//      `Play_200Sounds_...`（连播 200 次，创建过的播放器数不增长）
//    · 宿主丢了会不会抛 `MissingReferenceException`？ → `Tick_WhenVoiceDestroyed...`
//    · 池满了怎么办？（规则写死：抢占 / 丢弃）→ 四条 `Preemption_*` / `Concurrency_*`
//    · 三个分组音量是不是真的互不影响？      → 三条 `BusVolume_*`
//
//  ---------------------------------------------------------------------------
//  为什么必须注入假探针
//  ---------------------------------------------------------------------------
//  `AudioSource.isPlaying` 在 EditMode 下**永远是 false 且不会推进**，
//  所以"播完 → 回池"这条逻辑，不注入就根本测不了。
//  注入之后不但能测，还能**数出探针被问了几次** —— 于是"刚起播那一帧不判定"
//  也是一个可断言的事实（见 `Tick_DoesNotQueryProbeForVoiceThatJustStarted`）。
//
//  ⚠️ 诚实边界：这个文件**不能**证明"声音真的响了"。出声只能靠耳朵，
//     见 `Docs/16` §10.2.8 的手动验证单。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Framework;
using NBC.Framework.Audio;
using NUnit.Framework;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    /// <summary>
    /// 假探针：由测试直接指定"还在播吗"，并统计被查询次数。
    /// </summary>
    internal sealed class FakePlaybackProbe : IAudioPlaybackProbe
    {
        /// <summary>true 表示所有播放器都"还在播"。</summary>
        public bool Playing;

        /// <summary>被查询的总次数。</summary>
        public int QueryCount;

        /// <summary>被查询过的播放器（用来断言"查的是哪一个"）。</summary>
        public readonly List<AudioSource> Queried = new List<AudioSource>();

        /// <summary>查询播放状态。</summary>
        /// <param name="source">目标播放器。</param>
        /// <returns>由 <see cref="Playing"/> 决定。</returns>
        public bool IsPlaying(AudioSource source)
        {
            QueryCount++;
            Queried.Add(source);
            return Playing;
        }
    }

    /// <summary>
    /// A8 音频管理的 EditMode 测试。
    /// </summary>
    public sealed class AudioManagerTests
    {
        private const float Tolerance = 1e-4f;

        private AudioManager m_manager;
        private FakePlaybackProbe m_probe;
        private AudioClip m_clip;
        private GameObject m_listener;

        /// <summary>每个用例前：重置单例、造一个真 AudioClip、装一个监听器。</summary>
        [SetUp]
        public void SetUp()
        {
            if (AudioManager.HasInstance)
            {
                AudioManager.DisposeInstance();
            }

            // 真 AudioClip（不是 mock）：`AudioSource.clip` 要真的能挂上去。
            // 4410 采样 @44100Hz = 0.1 秒，够用且不占内存。
            m_clip = AudioClip.Create("nbc-test-clip", 4410, 1, 44100, false);

            // 没有 AudioListener 时 Unity 会每帧警告一次，噪声会盖住真正的失败信息。
            m_listener = new GameObject("nbc-test-listener");
            m_listener.AddComponent<AudioListener>();

            m_probe = new FakePlaybackProbe();
            m_manager = AudioManager.Instance;
            m_manager.Probe = m_probe;
        }

        /// <summary>每个用例后：销毁单例（连带宿主）与临时对象，避免互相污染。</summary>
        [TearDown]
        public void TearDown()
        {
            if (AudioManager.HasInstance)
            {
                AudioManager.DisposeInstance();
            }

            if (m_clip != null)
            {
                UnityEngine.Object.DestroyImmediate(m_clip);
                m_clip = null;
            }

            if (m_listener != null)
            {
                UnityEngine.Object.DestroyImmediate(m_listener);
                m_listener = null;
            }
        }

        // ====================================================================
        //  基础播放
        // ====================================================================

        /// <summary>播放会返回一个有效句柄，并把播放器挂到 VoiceRoot 下。</summary>
        [Test]
        public void Play_ReturnsValidHandle_AndParentsVoiceUnderVoiceRoot()
        {
            AudioVoiceHandle handle = PlaySound();

            Assert.IsTrue(handle.IsValid, "正常播放应当拿到有效句柄");
            Assert.AreEqual(1, m_manager.ActiveVoiceCount);
            Assert.AreEqual(0, m_manager.DroppedCount);

            AudioSource source = SingleActiveSource();
            Assert.AreSame(m_clip, source.clip, "素材要落到播放器上");
            Assert.IsTrue(m_manager.IsActive(handle));
        }

        /// <summary>请求里没有素材时**立刻抛异常**，而不是播一个静音出去。</summary>
        [Test]
        public void Play_WithNullClip_Throws()
        {
            AudioPlayRequest request = AudioPlayRequest.Sound2D(null);

            Assert.Throws<ArgumentNullException>(() => m_manager.Play(request));
        }

        /// <summary>最终音量 = 分组音量 × 单次音量。</summary>
        [Test]
        public void Play_MultipliesBusVolumeAndRequestVolume()
        {
            m_manager.SetBusVolume(AudioBus.Sfx, 0.5f);

            PlaySound(volume: 0.5f);

            Assert.AreEqual(0.25f, SingleActiveSource().volume, Tolerance);
        }

        /// <summary>2D 音效不做空间化。</summary>
        [Test]
        public void Play2D_SetsSpatialBlendToZero()
        {
            PlaySound();

            Assert.AreEqual(0f, SingleActiveSource().spatialBlend, Tolerance);
        }

        /// <summary>3D 音效要摆到世界坐标，并应用距离衰减参数。</summary>
        [Test]
        public void Play3D_SetsSpatialBlendAndWorldPosition()
        {
            Vector3 position = new Vector3(3f, 0f, -4f);

            m_manager.Play(AudioPlayRequest.Sound3D(m_clip, position));

            AudioSource source = SingleActiveSource();
            Assert.AreEqual(1f, source.spatialBlend, Tolerance);
            Assert.AreEqual(1f, source.minDistance, Tolerance);
            Assert.AreEqual(30f, source.maxDistance, Tolerance);
            Assert.AreEqual(position.x, source.transform.position.x, 1e-3f);
            Assert.AreEqual(position.z, source.transform.position.z, 1e-3f);
        }

        // ====================================================================
        //  池复用（FW-09 问题①的正面证据）
        // ====================================================================

        /// <summary>播完的播放器**回池**：不再在播，但对象**没有被销毁**。</summary>
        [Test]
        public void Tick_RecyclesFinishedVoice_WithoutDestroyingIt()
        {
            PlaySound();
            AudioSource source = SingleActiveSource();

            m_probe.Playing = false;
            TickToRecycle();

            Assert.AreEqual(0, m_manager.ActiveVoiceCount, "播完就不该再占用并发额度");
            Assert.AreEqual(1, m_manager.IdleVoiceCount, "它应当回到池里");
            Assert.AreEqual(1, m_manager.CreatedVoiceCount, "创建过的数量不该增加");

            // 关键：对象还活着，只是被停用 —— "回池"不是"销毁"。
            Assert.IsTrue(source != null, "播放器对象必须还在（不是被 Destroy 了）");
            Assert.IsFalse(source.gameObject.activeSelf, "闲置时应当被停用");
            Assert.IsNull(source.clip, "回池时要清掉素材引用");
        }

        /// <summary>再播一次会**复用同一个播放器对象**（而不是新建）。</summary>
        [Test]
        public void Play_AfterRecycle_ReusesTheSameVoiceInstance()
        {
            PlaySound();
            AudioSource first = SingleActiveSource();

            m_probe.Playing = false;
            TickToRecycle();

            PlaySound();

            Assert.AreEqual(1, m_manager.CreatedVoiceCount, "应当复用，不该新建");
            Assert.AreSame(first, SingleActiveSource(), "复用的就是同一个 AudioSource");
        }

        /// <summary>
        /// **FW-09 验证单里的那条**：连播 200 个音效，创建过的播放器数量不增长。
        /// <para>这就是"组件创建/销毁抖动"被消掉的直接证据。</para>
        /// </summary>
        [Test]
        public void Play_200Sounds_CreatedVoiceCountStaysBounded()
        {
            m_manager.MaxConcurrentVoices = 4;
            m_probe.Playing = false;

            for (int i = 0; i < 200; i++)
            {
                m_manager.Play(AudioPlayRequest.Sound2D(m_clip));
                TickToRecycle();
            }

            Assert.LessOrEqual(m_manager.CreatedVoiceCount, 4,
                "播放 200 次，创建过的播放器不该超过并发上限 —— 超了就说明又在'用完就扔'");
            Assert.AreEqual(0, m_manager.DroppedCount, "每次都是播完再播，不该有丢弃");

            // 场景里的 AudioSource 组件数同样不增长（+1 是宿主上那个 BGM 播放器）。
            int sourceCount = m_manager.Host.GetComponentsInChildren<AudioSource>(true).Length;
            Assert.LessOrEqual(sourceCount, 5, "AudioSource 组件数不该随播放次数增长");
        }

        /// <summary>闲置 + 在播 == 创建过的（池的账目不能漂）。</summary>
        [Test]
        public void IdlePlusActive_AlwaysEqualsCreated()
        {
            m_manager.MaxConcurrentVoices = 5;
            m_probe.Playing = true;

            PlaySound();
            PlaySound();
            Assert.AreEqual(2, m_manager.IdleVoiceCount + m_manager.ActiveVoiceCount);
            Assert.AreEqual(2, m_manager.CreatedVoiceCount);

            m_probe.Playing = false;
            TickToRecycle();

            Assert.AreEqual(2, m_manager.IdleVoiceCount + m_manager.ActiveVoiceCount);
            Assert.AreEqual(2, m_manager.CreatedVoiceCount);
        }

        // ====================================================================
        //  刚起播不判定
        // ====================================================================

        /// <summary>刚起播的那一帧**根本不问探针**（问了就可能把刚播的音效掐掉）。</summary>
        [Test]
        public void Tick_DoesNotQueryProbeForVoiceThatJustStarted()
        {
            PlaySound();

            m_manager.Tick();

            Assert.AreEqual(0, m_probe.QueryCount, "起播的这一帧不该判定它");
            Assert.AreEqual(1, m_manager.ActiveVoiceCount, "更不该把它回收掉");
        }

        /// <summary>每个在播的播放器，每帧恰好被问一次。</summary>
        [Test]
        public void Tick_QueriesProbeOncePerActiveVoice()
        {
            m_probe.Playing = true;
            PlaySound();
            PlaySound();

            m_manager.Tick(); // 起播帧：跳过
            m_manager.Tick(); // 这一帧才判定
            m_manager.Tick();

            Assert.AreEqual(4, m_probe.QueryCount, "2 个播放器 × 2 帧 = 4 次");
            Assert.AreEqual(2, m_manager.ActiveVoiceCount);
        }

        // ====================================================================
        //  并发上限 / 抢占 / 丢弃
        // ====================================================================

        /// <summary>并发上限生效：满了就丢，不是无限创建。</summary>
        [Test]
        public void Concurrency_DropsRequestWhenPoolIsFull()
        {
            m_manager.MaxConcurrentVoices = 3;
            m_probe.Playing = true;

            PlaySound();
            PlaySound();
            PlaySound();
            AudioVoiceHandle fourth = PlaySound();

            Assert.IsFalse(fourth.IsValid, "池满且同级不抢 → 返回无效句柄");
            Assert.AreEqual(3, m_manager.ActiveVoiceCount);
            Assert.AreEqual(1, m_manager.DroppedCount);
            Assert.AreEqual(0, m_manager.PreemptedCount);
        }

        /// <summary>高优先级可以抢占**优先级最低**的那个。</summary>
        [Test]
        public void Preemption_HigherPriorityTakesTheLowestPriorityVoice()
        {
            m_manager.MaxConcurrentVoices = 2;
            m_probe.Playing = true;

            AudioVoiceHandle low = PlaySound(priority: AudioPriority.Low);
            AudioVoiceHandle normal = PlaySound(priority: AudioPriority.Normal);

            AudioVoiceHandle high = PlaySound(priority: AudioPriority.High);

            Assert.IsTrue(high.IsValid);
            Assert.AreEqual(2, m_manager.ActiveVoiceCount, "抢占是复用，不是扩容");
            Assert.AreEqual(1, m_manager.PreemptedCount);
            Assert.AreEqual(0, m_manager.DroppedCount);
            Assert.IsFalse(m_manager.IsActive(low), "被抢的应当是最低优先级的那个");
            Assert.IsTrue(m_manager.IsActive(normal));
            Assert.IsTrue(m_manager.IsActive(high));
        }

        /// <summary>
        /// **同级不抢** —— 否则两个同优先级的音效会互相踩。
        /// </summary>
        [Test]
        public void Preemption_SamePriority_DoesNotPreempt()
        {
            m_manager.MaxConcurrentVoices = 1;
            m_probe.Playing = true;

            AudioVoiceHandle first = PlaySound(priority: AudioPriority.Normal);
            AudioVoiceHandle second = PlaySound(priority: AudioPriority.Normal);

            Assert.IsTrue(second.IsValid == false);
            Assert.IsTrue(m_manager.IsActive(first), "同优先级不该把前一个挤掉");
            Assert.AreEqual(1, m_manager.DroppedCount);
            Assert.AreEqual(0, m_manager.PreemptedCount);
        }

        /// <summary>`Critical` 绝不被抢占（剧情语音不该被打击音吃掉）。</summary>
        [Test]
        public void Preemption_CriticalIsNeverPreempted()
        {
            m_manager.MaxConcurrentVoices = 1;
            m_probe.Playing = true;

            AudioVoiceHandle critical = PlaySound(priority: AudioPriority.Critical);
            PlaySound(priority: AudioPriority.Critical);

            Assert.IsTrue(m_manager.IsActive(critical));
            Assert.AreEqual(0, m_manager.PreemptedCount);
            Assert.AreEqual(1, m_manager.DroppedCount);
        }

        /// <summary>多个同优先级候选时，抢**最老的**那个。</summary>
        [Test]
        public void Preemption_SamePriorityVictims_TakesTheOldest()
        {
            m_manager.MaxConcurrentVoices = 2;
            m_probe.Playing = true;

            AudioVoiceHandle oldest = PlaySound(priority: AudioPriority.Low);
            AudioVoiceHandle newer = PlaySound(priority: AudioPriority.Low);

            List<AudioVoiceHandle> ended = new List<AudioVoiceHandle>();
            m_manager.VoiceEnded += (handle, reason) => ended.Add(handle);

            PlaySound(priority: AudioPriority.High);

            Assert.AreEqual(1, ended.Count);
            Assert.AreEqual(oldest, ended[0], "应当抢最老的那个");
            Assert.IsFalse(m_manager.IsActive(oldest));
            Assert.IsTrue(m_manager.IsActive(newer));
        }

        // ====================================================================
        //  结束通知（调用方靠它释放素材句柄）
        // ====================================================================

        /// <summary>自然播完 → `Finished`，且通知时**已经回收完毕**。</summary>
        [Test]
        public void VoiceEnded_OnFinish_ReportsFinishedAndIsAlreadyRecycled()
        {
            AudioVoiceHandle handle = PlaySound();
            bool wasActiveInsideCallback = true;

            m_manager.VoiceEnded += (h, reason) =>
            {
                wasActiveInsideCallback = m_manager.IsActive(h);
            };

            m_probe.Playing = false;
            TickToRecycle();

            Assert.AreEqual(1, m_manager.FinishedCount);
            Assert.IsFalse(wasActiveInsideCallback,
                "派发通知时播放器应当**已经**回收，调用方才能安全地释放素材句柄");
            Assert.IsFalse(m_manager.IsActive(handle));
        }

        /// <summary>被抢占 → `Preempted`（不是 `Finished`）。</summary>
        [Test]
        public void VoiceEnded_OnPreemption_ReportsPreempted()
        {
            m_manager.MaxConcurrentVoices = 1;
            m_probe.Playing = true;

            AudioVoiceHandle low = PlaySound(priority: AudioPriority.Low);

            List<AudioEndReason> reasons = new List<AudioEndReason>();
            m_manager.VoiceEnded += (h, reason) => reasons.Add(reason);

            PlaySound(priority: AudioPriority.High);

            Assert.AreEqual(1, reasons.Count);
            Assert.AreEqual(AudioEndReason.Preempted, reasons[0],
                "抢占必须报 Preempted —— 报成 Finished 的话调用方会以为它自然播完了");
            Assert.AreEqual(0, m_manager.FinishedCount);
            Assert.IsFalse(m_manager.IsActive(low));
        }

        /// <summary>被显式停掉 → `Stopped`。</summary>
        [Test]
        public void VoiceEnded_OnStop_ReportsStopped()
        {
            AudioVoiceHandle handle = PlaySound(priority: AudioPriority.Normal);

            List<AudioEndReason> reasons = new List<AudioEndReason>();
            m_manager.VoiceEnded += (h, reason) => reasons.Add(reason);

            bool stopped = m_manager.Stop(handle);

            Assert.IsTrue(stopped);
            Assert.AreEqual(1, reasons.Count);
            Assert.AreEqual(AudioEndReason.Stopped, reasons[0]);
        }

        /// <summary>
        /// **宿主外部被销毁 → `Lost`**。原版在这里抛 `MissingReferenceException`（P-06 / P-07）。
        /// </summary>
        [Test]
        public void VoiceEnded_OnExternallyDestroyedVoice_ReportsLostAndDoesNotThrow()
        {
            PlaySound();
            AudioSource source = SingleActiveSource();

            List<AudioEndReason> reasons = new List<AudioEndReason>();
            m_manager.VoiceEnded += (h, reason) => reasons.Add(reason);

            UnityEngine.Object.DestroyImmediate(source.gameObject);

            Assert.DoesNotThrow(() => m_manager.Tick(), "宿主没了不该抛异常");

            Assert.AreEqual(0, m_manager.ActiveVoiceCount);
            Assert.AreEqual(1, m_manager.LostCount);
            Assert.AreEqual(1, reasons.Count);
            Assert.AreEqual(AudioEndReason.Lost, reasons[0], "丢了就是丢了，不能报成 Finished");
        }

        /// <summary>宿主整个被销毁后，下一次播放会**自动重建宿主**（Unity 伪 null 带来的自愈）。</summary>
        [Test]
        public void Play_AfterHostDestroyed_RebuildsHost()
        {
            PlaySound();
            GameObject oldHost = m_manager.Host;

            UnityEngine.Object.DestroyImmediate(oldHost);
            m_manager.Tick();

            Assert.IsTrue(m_manager.Host == null, "宿主已被销毁（Unity 的伪 null）");

            AudioVoiceHandle handle = PlaySound();

            Assert.IsTrue(handle.IsValid);
            Assert.IsTrue(m_manager.Host != null, "应当重建宿主");
            Assert.AreNotSame(oldHost, m_manager.Host);
            Assert.AreEqual(1, m_manager.ActiveVoiceCount);
        }

        // ====================================================================
        //  句柄语义
        // ====================================================================

        /// <summary>无效句柄停不掉任何东西。</summary>
        [Test]
        public void Stop_WithInvalidHandle_ReturnsFalse()
        {
            Assert.IsFalse(m_manager.Stop(AudioVoiceHandle.None));
            Assert.IsFalse(m_manager.Stop(default(AudioVoiceHandle)));
            Assert.IsFalse(m_manager.IsActive(AudioVoiceHandle.None));
        }

        /// <summary>编造一个没发过的编号，也停不掉任何东西。</summary>
        [Test]
        public void Stop_WithUnknownHandle_ReturnsFalse()
        {
            m_probe.Playing = true;
            PlaySound();

            Assert.IsFalse(m_manager.Stop(new AudioVoiceHandle(9999)));
            Assert.AreEqual(1, m_manager.ActiveVoiceCount);
        }

        /// <summary>
        /// **旧句柄不能误伤新音效**：编号永不复用，所以上一轮的句柄只对上一轮有效。
        /// </summary>
        [Test]
        public void Stop_WithStaleHandle_DoesNotStopTheNewVoice()
        {
            AudioVoiceHandle first = PlaySound();

            m_probe.Playing = false;
            TickToRecycle();

            m_probe.Playing = true;
            AudioVoiceHandle second = PlaySound();

            Assert.AreNotEqual(first.Id, second.Id, "编号必须重新发，不能复用");
            Assert.IsFalse(m_manager.Stop(first), "旧句柄应当已经失效");
            Assert.IsTrue(m_manager.IsActive(second), "新音效不能被旧句柄误停");
        }

        /// <summary>全部停掉：在播的清零，且每个都收到一次 `Stopped`。</summary>
        [Test]
        public void StopAll_RecyclesEverythingAndNotifiesEach()
        {
            m_probe.Playing = true;
            PlaySound();
            PlaySound();
            PlaySound();

            List<AudioEndReason> reasons = new List<AudioEndReason>();
            m_manager.VoiceEnded += (h, reason) => reasons.Add(reason);

            m_manager.StopAll();

            Assert.AreEqual(0, m_manager.ActiveVoiceCount);
            Assert.AreEqual(3, m_manager.IdleVoiceCount);
            Assert.AreEqual(3, reasons.Count);
            Assert.IsTrue(reasons.TrueForAll(r => r == AudioEndReason.Stopped));
        }

        // ====================================================================
        //  音量分组（FW-M08）
        // ====================================================================

        /// <summary>三个分组互不影响。</summary>
        [Test]
        public void BusVolumes_AreIndependent()
        {
            m_probe.Playing = true;
            PlaySound(bus: AudioBus.Sfx);

            m_manager.SetBusVolume(AudioBus.Bgm, 0.25f);
            m_manager.SetBusVolume(AudioBus.Ui, 0.1f);

            Assert.AreEqual(1f, SingleActiveSource().volume, Tolerance, "改 BGM/UI 不该动到 Sfx");
            Assert.AreEqual(0.25f, m_manager.GetBusVolume(AudioBus.Bgm), Tolerance);
            Assert.AreEqual(0.1f, m_manager.GetBusVolume(AudioBus.Ui), Tolerance);
            Assert.AreEqual(1f, m_manager.GetBusVolume(AudioBus.Sfx), Tolerance);
        }

        /// <summary>改分组音量，**正在播的**立刻跟着变，而且**不会越乘越小**。</summary>
        [Test]
        public void SetBusVolume_RecomputesPlayingVoicesWithoutCompounding()
        {
            m_probe.Playing = true;
            PlaySound(volume: 0.5f, bus: AudioBus.Sfx);

            m_manager.SetBusVolume(AudioBus.Sfx, 0.4f);
            Assert.AreEqual(0.2f, SingleActiveSource().volume, Tolerance);

            // 再设一次同样的值：如果实现是"拿当前音量再乘一遍"，这里就会变成 0.08。
            m_manager.SetBusVolume(AudioBus.Sfx, 0.4f);
            Assert.AreEqual(0.2f, SingleActiveSource().volume, Tolerance,
                "重复设置不该发生复合相乘 —— 必须存下单次音量再重算");
        }

        /// <summary>静音把音量压成 0，但**记住**原音量，取消后恢复。</summary>
        [Test]
        public void SetBusMuted_ZeroesVolumeButRemembersIt()
        {
            m_probe.Playing = true;
            PlaySound(volume: 1f, bus: AudioBus.Sfx);
            m_manager.SetBusVolume(AudioBus.Sfx, 0.6f);

            m_manager.SetBusMuted(AudioBus.Sfx, true);

            Assert.AreEqual(0f, SingleActiveSource().volume, Tolerance);
            Assert.AreEqual(0.6f, m_manager.GetBusVolume(AudioBus.Sfx), Tolerance, "静音不该改掉音量值");

            m_manager.SetBusMuted(AudioBus.Sfx, false);

            Assert.AreEqual(0.6f, SingleActiveSource().volume, Tolerance, "取消静音要恢复原音量");
        }

        /// <summary>音量被钳在 0..1。</summary>
        [Test]
        public void BusVolume_IsClampedToUnitRange()
        {
            m_manager.SetBusVolume(AudioBus.Sfx, 5f);
            Assert.AreEqual(1f, m_manager.GetBusVolume(AudioBus.Sfx), Tolerance);

            m_manager.SetBusVolume(AudioBus.Sfx, -3f);
            Assert.AreEqual(0f, m_manager.GetBusVolume(AudioBus.Sfx), Tolerance);
        }

        // ====================================================================
        //  背景音乐
        // ====================================================================

        /// <summary>BGM 走宿主上那个独立播放器，**不占音效并发额度**。</summary>
        [Test]
        public void PlayBgm_UsesDedicatedSource_AndDoesNotCountAsActiveVoice()
        {
            AudioVoiceHandle handle = m_manager.PlayBgm(m_clip);

            Assert.IsTrue(handle.IsValid);
            Assert.AreEqual(0, m_manager.ActiveVoiceCount, "BGM 不占音效额度");

            AudioSource bgm = m_manager.Host.GetComponent<AudioSource>();
            Assert.IsTrue(bgm != null, "宿主上应当有一个 BGM 播放器");
            Assert.AreSame(m_clip, bgm.clip);
            Assert.IsTrue(bgm.loop);
            Assert.AreEqual(handle, m_manager.CurrentBgm);
            Assert.IsTrue(m_manager.IsActive(handle));
        }

        /// <summary>换 BGM 等于停掉上一首 —— 上一首的句柄必须收到通知（否则素材句柄泄漏）。</summary>
        [Test]
        public void PlayBgm_Twice_NotifiesThePreviousOne()
        {
            List<AudioVoiceHandle> handles = new List<AudioVoiceHandle>();
            List<AudioEndReason> reasons = new List<AudioEndReason>();
            m_manager.VoiceEnded += (h, reason) => { handles.Add(h); reasons.Add(reason); };

            AudioVoiceHandle first = m_manager.PlayBgm(m_clip);
            AudioVoiceHandle second = m_manager.PlayBgm(m_clip);

            Assert.AreEqual(1, handles.Count);
            Assert.AreEqual(first, handles[0]);
            Assert.AreEqual(AudioEndReason.Stopped, reasons[0]);
            Assert.AreEqual(second, m_manager.CurrentBgm);
            Assert.AreNotEqual(first, second);
        }

        /// <summary>停掉 BGM 后句柄失效。</summary>
        [Test]
        public void StopBgm_InvalidatesHandle()
        {
            AudioVoiceHandle handle = m_manager.PlayBgm(m_clip);

            Assert.IsTrue(m_manager.StopBgm());

            Assert.IsFalse(m_manager.IsActive(handle));
            Assert.IsFalse(m_manager.CurrentBgm.IsValid);
            Assert.IsFalse(m_manager.StopBgm(), "没有 BGM 时应当返回 false");
        }

        /// <summary>BGM 可以被 `Stop(handle)` 停掉（句柄统一，调用方不用记两套 API）。</summary>
        [Test]
        public void Stop_WithBgmHandle_StopsBgm()
        {
            AudioVoiceHandle handle = m_manager.PlayBgm(m_clip);

            Assert.IsTrue(m_manager.Stop(handle));
            Assert.IsFalse(m_manager.CurrentBgm.IsValid);
        }

        /// <summary>改 BGM 分组音量会落到 BGM 播放器上。</summary>
        [Test]
        public void SetBusVolume_AffectsBgmSource()
        {
            m_manager.PlayBgm(m_clip, volume: 0.5f);

            m_manager.SetBusVolume(AudioBus.Bgm, 0.5f);

            Assert.AreEqual(0.25f, m_manager.Host.GetComponent<AudioSource>().volume, Tolerance);
        }

        // ====================================================================
        //  配置校验
        // ====================================================================

        /// <summary>并发上限必须为正。</summary>
        [Test]
        public void MaxConcurrentVoices_RejectsNonPositive()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => m_manager.MaxConcurrentVoices = 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => m_manager.MaxConcurrentVoices = -1);
        }

        /// <summary>不能把上限降到比正在播的还少（那会立刻违反不变式）。</summary>
        [Test]
        public void MaxConcurrentVoices_RejectsValueBelowActiveCount()
        {
            m_probe.Playing = true;
            m_manager.MaxConcurrentVoices = 3;
            PlaySound();
            PlaySound();

            Assert.Throws<InvalidOperationException>(() => m_manager.MaxConcurrentVoices = 1);
        }

        /// <summary>min/max 距离的不变式从两个方向都挡住。</summary>
        [Test]
        public void Distances_EnforceMinLessThanMax()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => m_manager.Min3DDistance = 30f);
            Assert.Throws<ArgumentOutOfRangeException>(() => m_manager.Min3DDistance = -1f);
            Assert.Throws<ArgumentOutOfRangeException>(() => m_manager.Max3DDistance = 1f);

            m_manager.Min3DDistance = 2f;
            m_manager.Max3DDistance = 40f;

            Assert.AreEqual(2f, m_manager.Min3DDistance, Tolerance);
            Assert.AreEqual(40f, m_manager.Max3DDistance, Tolerance);
        }

        /// <summary>探针不允许被设成 null（否则 Tick 会 NRE）。</summary>
        [Test]
        public void Probe_RejectsNull()
        {
            Assert.Throws<ArgumentNullException>(() => m_manager.Probe = null);
        }

        /// <summary>预热会先建好播放器，避免首次播放卡一下。</summary>
        [Test]
        public void Prewarm_CreatesIdleVoices()
        {
            m_manager.Prewarm(3);

            Assert.AreEqual(3, m_manager.IdleVoiceCount);
            Assert.AreEqual(3, m_manager.CreatedVoiceCount);
            Assert.AreEqual(0, m_manager.ActiveVoiceCount);
        }

        /// <summary>预热数量超过并发上限是配置错误，当场报出来。</summary>
        [Test]
        public void Prewarm_RejectsCountAboveCap()
        {
            m_manager.MaxConcurrentVoices = 2;

            Assert.Throws<ArgumentOutOfRangeException>(() => m_manager.Prewarm(3));
        }

        // ====================================================================
        //  宿主
        // ====================================================================

        /// <summary>宿主名字固定，便于在 Hierarchy 里一眼定位。</summary>
        [Test]
        public void Host_HasExpectedName_AndOwnsVoiceRoot()
        {
            Assert.IsNotNull(m_manager.Host);
            Assert.AreEqual(AudioManager.HostName, m_manager.Host.name);
            Assert.IsNotNull(m_manager.VoiceRoot);
            Assert.AreSame(m_manager.Host.transform, m_manager.VoiceRoot.parent);
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        /// <summary>播一个音效（默认 Sfx / Normal / 2D）。</summary>
        /// <param name="volume">单次音量。</param>
        /// <param name="priority">优先级。</param>
        /// <param name="bus">音量分组。</param>
        /// <returns>播放句柄。</returns>
        private AudioVoiceHandle PlaySound(float volume = 1f,
                                           AudioPriority priority = AudioPriority.Normal,
                                           AudioBus bus = AudioBus.Sfx)
        {
            return m_manager.Play(AudioPlayRequest.Sound2D(m_clip, volume, priority, 1f, bus));
        }

        /// <summary>推进两帧，跨过"起播帧不判定"，让播完的播放器回池。</summary>
        private void TickToRecycle()
        {
            m_manager.Tick();
            m_manager.Tick();
        }

        /// <summary>取当前处于启用状态的播放器（= 正在播的那些）。</summary>
        /// <returns>播放器组件列表。</returns>
        private List<AudioSource> ActiveSources()
        {
            List<AudioSource> result = new List<AudioSource>();

            if (m_manager.VoiceRoot == null)
            {
                return result;
            }

            AudioSource[] all = m_manager.VoiceRoot.GetComponentsInChildren<AudioSource>(true);

            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].gameObject.activeSelf)
                {
                    result.Add(all[i]);
                }
            }

            return result;
        }

        /// <summary>取唯一一个正在播的播放器，多于一个就失败。</summary>
        /// <returns>播放器组件。</returns>
        private AudioSource SingleActiveSource()
        {
            List<AudioSource> sources = ActiveSources();
            Assert.AreEqual(1, sources.Count, "期望恰好有一个正在播的播放器");
            return sources[0];
        }
    }
}
