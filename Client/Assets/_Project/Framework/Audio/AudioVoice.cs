// ============================================================================
//  NBC.Framework.Audio · 单个音效播放器（池里的"零件"）
//  对应需求：FW-M08（3D 音效池 + 回池）
//  缺陷编号：FW-09 问题①（音效对象不复用）
//
//  ---------------------------------------------------------------------------
//  它和原版的区别，就一句：**它会被回收，不会被销毁**
//  ---------------------------------------------------------------------------
//      原版：PlaySound → soundObj.AddComponent<AudioSource>() → 播完 Destroy(组件)
//      这里：池里取出一个 AudioVoice → 配好参数 Play → 播完 StopAndReset() 回池
//
//  ⚠️ 回池时**只停止并清空参数，不销毁 GameObject / AudioSource**
//     —— 这正是"组件创建/销毁抖动"的解法。
//
//  ---------------------------------------------------------------------------
//  为什么闲置时要 SetActive(false)
//  ---------------------------------------------------------------------------
//  两个好处：① 不参与场景里的音频更新（省一点）；② 在 Hierarchy 里一眼看得出
//  "现在有几个播放器真的在工作"。代价是每次播放要多一次 SetActive —— 可以忽略。
// ============================================================================

using UnityEngine;

namespace NBC.Framework.Audio
{
    /// <summary>
    /// 一个可复用的音效播放器：一个 GameObject 挂一个 <see cref="AudioSource"/>。
    /// <para>
    /// **不由构造方直接 new 来播放** —— 请通过 <c>AudioManager.Play</c> 走对象池，
    /// 否则就退化成原版那种"用完就扔"了。
    /// </para>
    /// </summary>
    public sealed class AudioVoice
    {
        /// <summary>宿主对象。回池后**保留**，只停不销毁。</summary>
        private GameObject m_go;

        /// <summary>播放器组件。回池后**保留**。</summary>
        private AudioSource m_source;

        /// <summary>
        /// 创建一个播放器（通常由对象池的工厂调用）。
        /// </summary>
        /// <param name="name">对象名，便于在 Hierarchy 里定位。</param>
        /// <param name="parent">父节点；传 null 则挂在场景根。</param>
        public AudioVoice(string name, Transform parent)
        {
            m_go = new GameObject(string.IsNullOrEmpty(name) ? "AudioVoice" : name);

            if (parent != null)
            {
                m_go.transform.SetParent(parent, false);
            }

            m_source = m_go.AddComponent<AudioSource>();

            // `playOnAwake` 必须在拿到组件后立刻关掉：否则将来给它赋了 clip 又 SetActive(true)，
            // 会在我们不希望的时刻自动播一次。
            m_source.playOnAwake = false;

            // 闲置状态：不参与场景音频更新。
            m_go.SetActive(false);
        }

        /// <summary>播放器编号（由 <c>AudioManager</c> 发放，从 1 开始；0 表示当前未在播放）。</summary>
        public int Id { get; internal set; }

        /// <summary>发号顺序。池满时用来实现"**同优先级抢占最老的**"。</summary>
        public long Sequence { get; internal set; }

        /// <summary>本次播放的优先级。</summary>
        public AudioPriority Priority { get; internal set; }

        /// <summary>本次播放所属的音量分组。</summary>
        public AudioBus Bus { get; internal set; }

        /// <summary>开始播放时的逻辑帧号。用来避免"刚播就被判为播完"。</summary>
        public int StartFrame { get; internal set; }

        /// <summary>
        /// 本次播放的**单次音量**（不含分组音量）。
        /// <para>
        /// ⚠️ 必须存下来：分组音量是**随时可调**的，调整时要重新算
        /// `分组音量 × 单次音量`。不存这个数就只能拿当前最终音量去乘，**会越调越小**。
        /// </para>
        /// </summary>
        public float RequestVolume { get; internal set; }

        /// <summary>宿主对象（调试 / 测试用）。</summary>
        public GameObject Target
        {
            get { return m_go; }
        }

        /// <summary>播放器组件（调试 / 测试用）。</summary>
        public AudioSource Source
        {
            get { return m_source; }
        }

        /// <summary>
        /// 宿主是否仍然有效。
        /// <para>
        /// ⚠️ Unity 的 `Object` 重载了 `==`：**被销毁的对象与 null 比较会返回 true**（"伪 null"）。
        /// 所以这里能测出"宿主被外部销毁了"，而不是拿到一个假引用继续用。
        /// </para>
        /// </summary>
        public bool IsAlive
        {
            get { return m_go != null && m_source != null; }
        }

        /// <summary>
        /// 配置并开始播放。
        /// </summary>
        /// <param name="request">播放请求。</param>
        /// <param name="finalVolume">算好的最终音量（分组音量 × 单次音量）。</param>
        /// <param name="minDistance">3D 最小距离（此距离内不再衰减）。</param>
        /// <param name="maxDistance">3D 最大距离（超出后听不到）。</param>
        public void Apply(in AudioPlayRequest request, float finalVolume, float minDistance, float maxDistance)
        {
            if (!IsAlive)
            {
                return;
            }

            m_go.SetActive(true);

            m_source.clip = request.Clip;
            m_source.loop = request.Loop;
            m_source.volume = finalVolume;
            m_source.pitch = request.Pitch;

            if (request.Is3D)
            {
                m_source.spatialBlend = 1f;
                m_source.rolloffMode = AudioRolloffMode.Linear;
                m_source.minDistance = minDistance;
                m_source.maxDistance = maxDistance;

                // 世界坐标直接赋值：宿主在原点、无缩放，所以父子变换不会引入偏差。
                m_go.transform.position = request.Position;
            }
            else
            {
                m_source.spatialBlend = 0f;
            }

            m_source.Play();
        }

        /// <summary>
        /// 只改音量（分组音量变化时，正在播的音效要立刻跟着变）。
        /// </summary>
        /// <param name="finalVolume">算好的最终音量。</param>
        public void SetVolume(float finalVolume)
        {
            if (m_source != null)
            {
                m_source.volume = finalVolume;
            }
        }

        /// <summary>
        /// 停止并清空参数，准备回池。
        /// <para>
        /// ⚠️ **不销毁任何东西** —— 这是与原版 <c>GameObject.Destroy(source)</c> 的关键区别。
        /// </para>
        /// <para>
        /// 顺带把 <see cref="Id"/> 归零：这样**已经发出去的旧句柄会自动变成无效**，
        /// 调用方拿着上一轮的句柄来 <c>Stop</c> 时不会误伤新播放的音效。
        /// </para>
        /// </summary>
        public void StopAndReset()
        {
            if (m_source != null)
            {
                m_source.Stop();
                m_source.clip = null;
                m_source.loop = false;
                m_source.spatialBlend = 0f;
            }

            if (m_go != null)
            {
                m_go.SetActive(false);
            }

            Id = 0;
            Sequence = 0L;
            Priority = AudioPriority.Low;
            Bus = AudioBus.Sfx;
            StartFrame = 0;
            RequestVolume = 1f;
        }

        /// <summary>
        /// 真正销毁宿主对象。**只在池被清空 / 管理器销毁时调用**。
        /// </summary>
        public void Dispose()
        {
            DestroyObject(m_go);
            m_go = null;
            m_source = null;
        }

        /// <summary>调试文本。</summary>
        /// <returns>可读描述。</returns>
        public override string ToString()
        {
            return "AudioVoice#" + Id + "(" + (m_go != null ? m_go.name : "<destroyed>")
                   + " bus=" + Bus + " prio=" + Priority + ")";
        }

        /// <summary>
        /// 按运行模式选择正确的销毁方式。
        /// <para>
        /// ⚠️ **EditMode 下不能调 `Object.Destroy`** —— Unity 会报
        /// "Destroy may not be called from edit mode! Use DestroyImmediate instead."，
        /// 而且这条错误会污染测试结果（日志断言会因此失败）。
        /// </para>
        /// </summary>
        /// <param name="go">要销毁的对象。</param>
        private static void DestroyObject(GameObject go)
        {
            if (go == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Object.Destroy(go);
            }
            else
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
