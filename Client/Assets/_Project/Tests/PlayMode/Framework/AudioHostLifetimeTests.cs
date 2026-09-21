// ============================================================================
//  M1-A8 · AudioManager 的 PlayMode 测试
//  对应验收：Docs/16-M1开工清单.md V1 / V2
//  对应记录：Docs/06-框架改造记录.md §三 FW-09（问题②）、§四 P-06
//
//  ---------------------------------------------------------------------------
//  为什么这几条非要在 PlayMode 写（EditMode 测不了）
//  ---------------------------------------------------------------------------
//    ① **`DontDestroyOnLoad` 只在播放模式下有意义** ——
//       而"宿主不随场景销毁"正是 FW-09 问题②的修法（原版没做 → P-06）。
//    ② **`AudioSource.isPlaying` 在 EditMode 下永远是 false 且不会推进**，
//       所以"真素材真的播完了、真的自动回池"这条**端到端**路径只能在 PlayMode 验证。
//       EditMode 那份用假探针测的是**逻辑**；这一份测的是**接线**。
//    ③ `MonoManager` 每帧驱动 `Tick` —— 这里**一次都不手动调 `Tick`**，
//       所以下面那条自动回收的断言同时证明了"驱动器接上了"。
//
//  ⚠️ 诚实边界：这个文件证明"`isPlaying` 变成 false 后播放器回池了"，
//     **不证明"声音真的从扬声器出来了"**。后者只能靠耳朵，见 `Docs/16` §10.2.8。
//
//  ⚠️ 本文件第 5 条用例**需要真实的音频设备**（编辑器里跑就行）。
//     如果它在无声卡的纯命令行环境里失败，那是环境问题，不是代码问题 —— 已在消息里写明。
// ============================================================================

using System.Collections;
using NBC.Framework;
using NBC.Framework.Audio;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NBC.Tests.PlayMode
{
    /// <summary>
    /// A8 音频管理在播放模式下的宿主生命周期与端到端回收测试。
    /// </summary>
    [TestFixture]
    public class AudioHostLifetimeTests
    {
        private AudioManager m_manager;
        private GameObject m_listener;

        /// <summary>重置单例、等一帧让上一轮的 Destroy 生效，再准备一个监听器。</summary>
        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (AudioManager.HasInstance)
            {
                AudioManager.DisposeInstance();
            }

            // 播放模式下 `Object.Destroy` 是**延迟到帧末**的，不等一帧的话
            // 上一轮的宿主还挂在场景里，会干扰"场景里到底有几个宿主"这类判断。
            yield return null;

            Assert.IsTrue(Application.isPlaying, "这个文件必须在播放模式下运行");

            m_manager = AudioManager.Instance;

            m_listener = new GameObject("nbc-pm-audio-listener");
            m_listener.AddComponent<AudioListener>();
        }

        /// <summary>销毁单例（连带宿主）与监听器。</summary>
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (AudioManager.HasInstance)
            {
                AudioManager.DisposeInstance();
            }

            if (m_listener != null)
            {
                UnityEngine.Object.Destroy(m_listener);
                m_listener = null;
            }

            yield return null;
        }

        // ====================================================================
        //  宿主生命周期（FW-09 问题② / P-06）
        // ====================================================================

        /// <summary>
        /// **P-06 的直接证据**：音频宿主必须落在 `DontDestroyOnLoad` 场景里。
        /// <para>
        /// ⚠️ 这里同时做了一个**对照**：一个普通 `new GameObject()` 必须在**别的**场景。
        /// 不做对照的话，"场景名是 DontDestroyOnLoad"这句断言可能对谁都成立，
        /// 那样它就没有证明力了。
        /// </para>
        /// </summary>
        [UnityTest]
        public IEnumerator Host_IsPlacedInDontDestroyOnLoadScene()
        {
            GameObject control = new GameObject("nbc-control-no-persist");

            try
            {
                yield return null;

                Assert.IsNotNull(m_manager.Host, "宿主应当已创建");
                Assert.AreEqual(AudioManager.HostName, m_manager.Host.name);
                Assert.AreEqual("DontDestroyOnLoad", m_manager.Host.scene.name,
                    "音频宿主必须放进 DontDestroyOnLoad 场景，否则切场景后被销毁 → 原版的 MissingReferenceException（P-06）");

                // 对照组：证明"在 DontDestroyOnLoad 场景里"不是自动成立的。
                Assert.AreNotEqual("DontDestroyOnLoad", control.scene.name,
                    "对照组失败了：普通对象也在 DontDestroyOnLoad 场景里，那上面那条断言就失去意义");
            }
            finally
            {
                UnityEngine.Object.Destroy(control);
            }
        }

        /// <summary>`DontDestroyOnLoad` 只对**根对象**生效，所以宿主不能有父节点。</summary>
        [UnityTest]
        public IEnumerator Host_IsARootObject()
        {
            yield return null;

            Assert.IsNull(m_manager.Host.transform.parent,
                "宿主必须是根对象，否则 DontDestroyOnLoad 不生效（Unity 会警告并忽略）");
            Assert.IsNotNull(m_manager.VoiceRoot, "音效父节点应当已创建");
            Assert.AreSame(m_manager.Host.transform, m_manager.VoiceRoot.parent,
                "音效挂在宿主下面，才能跟着宿主一起跨场景存活");
        }

        // ====================================================================
        //  真实探针
        // ====================================================================

        /// <summary>播放模式下必须是**真实**探针 —— 假探针只应出现在测试里。</summary>
        [Test]
        public void Probe_DefaultsToUnityImplementation()
        {
            Assert.IsInstanceOf<UnityAudioPlaybackProbe>(m_manager.Probe,
                "播放模式下没有注入假探针，应当是 UnityAudioPlaybackProbe");
        }

        /// <summary>真实探针对 null / 已销毁对象都要报告 false，而不是抛异常。</summary>
        [UnityTest]
        public IEnumerator UnityAudioPlaybackProbe_ToleratesNullAndDestroyedSources()
        {
            UnityAudioPlaybackProbe probe = new UnityAudioPlaybackProbe();

            Assert.IsFalse(probe.IsPlaying(null), "null 必须被容忍 —— 原版就是在这种地方抛的");

            GameObject go = new GameObject("nbc-probe-target");
            AudioSource source = go.AddComponent<AudioSource>();

            Assert.IsFalse(probe.IsPlaying(source), "没在播的播放器应当报告 false");

            UnityEngine.Object.Destroy(go);
            yield return null;

            Assert.IsTrue(source == null, "对象已被销毁（Unity 的伪 null）");
            Assert.IsFalse(probe.IsPlaying(source), "已销毁的播放器必须报告 false，不能抛异常");
        }

        // ====================================================================
        //  端到端：真素材播完 → 自动回池
        // ====================================================================

        /// <summary>
        /// 用**真**（非注入）的 `isPlaying`：短素材播完后，播放器自动回池。
        /// <para>
        /// ⚠️ 全程**不手动调 `Tick()`** —— 回收只能来自 `MonoManager` 每帧的驱动，
        /// 所以这条同时证明了驱动器接线正确。
        /// </para>
        /// </summary>
        [UnityTest]
        public IEnumerator Play_ShortClip_IsRecycledAutomatically()
        {
            // 0.05 秒 = 2205 采样 @ 44100Hz：足够短，让测试跑得快。
            AudioClip clip = AudioClip.Create("nbc-pm-short-clip", 2205, 1, 44100, false);

            try
            {
                AudioVoiceHandle handle = m_manager.Play(AudioPlayRequest.Sound2D(clip));

                Assert.IsTrue(handle.IsValid);
                Assert.AreEqual(1, m_manager.ActiveVoiceCount);

                // 等它自然播完（不手动 Tick）
                float deadline = Time.realtimeSinceStartup + 5f;
                while (m_manager.ActiveVoiceCount > 0 && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.AreEqual(0, m_manager.ActiveVoiceCount,
                    "0.05 秒的素材应当在几帧内播完并回池。若这里超时，先确认运行环境有可用音频设备");
                Assert.AreEqual(1, m_manager.FinishedCount, "应当被记为自然播完");
                Assert.AreEqual(1, m_manager.CreatedVoiceCount, "回池复用，创建数不该涨");
                Assert.IsFalse(m_manager.IsActive(handle), "播完后句柄应当失效");
            }
            finally
            {
                UnityEngine.Object.Destroy(clip);
            }
        }

        /// <summary>
        /// **循环音效永远不会被自动回收** —— 这是循环的前提，错了会表现为"背景风声播一半没了"。
        /// </summary>
        [UnityTest]
        public IEnumerator Play_LoopingClip_IsNotRecycledWhileLooping()
        {
            AudioClip clip = AudioClip.Create("nbc-pm-loop-clip", 2205, 1, 44100, false);

            try
            {
                m_manager.Play(AudioPlayRequest.Sound3D(clip, Vector3.zero, loop: true));

                // 远超素材长度（0.05 秒）的一段时间
                float deadline = Time.realtimeSinceStartup + 0.5f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.AreEqual(1, m_manager.ActiveVoiceCount,
                    "循环音效还在播，不该被当成'播完了'回收掉");
                Assert.AreEqual(0, m_manager.FinishedCount);

                m_manager.StopAll();

                Assert.AreEqual(0, m_manager.ActiveVoiceCount);
                Assert.AreEqual(1, m_manager.IdleVoiceCount, "停掉之后才回池");
            }
            finally
            {
                UnityEngine.Object.Destroy(clip);
            }
        }
    }
}
