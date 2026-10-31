// 这个文件里有：
//   AudioVoice（类）：池化的单个音效播放器，复用同一套宿主与 AudioSource，播完只停不销毁。

using UnityEngine;

namespace NBC.Framework.Audio
{
    // 主要功能：把「一个宿主 GameObject + 一个 AudioSource」包成可复用的播放单元：播放前配好参数，播完只停止并清空参数回池，全程不销毁组件。
    // 主要数据成员：m_go（宿主对象）、m_source（播放器组件）；本次播放状态 Id（编号，0 表示未在播放）、Sequence（发号顺序，越大越新，0 表示未占用）、Priority（本次优先级）、Bus（音量分组）、StartFrame（起始逻辑帧号，帧）、RequestVolume（单次音量，0..1 线性值，不含分组音量）。
    // 主要外部方法：Apply / SetVolume / StopAndReset / Dispose，以及 Id / Sequence / Priority / Bus / StartFrame / RequestVolume / Target / Source / IsAlive 属性。
    // 谁在用它：AudioManager 通过 ObjectPool 建播放器池，播放时取出、播完 StopAndReset 回池，池清空或销毁时对闲置播放器 Dispose；构造方不要自己 new 来播，否则就退化成用完就扔。
    // 注意：回池只停播放并清空参数，不销毁 GameObject 与 AudioSource —— 反复创建、销毁组件的抖动正是靠这一点消掉的。
    // 注意：闲置时宿主 SetActive(false)：不参与场景音频更新，Hierarchy 里也能一眼看出几个播放器在工作；代价是每次播放多一次 SetActive，可以忽略。
    /// <summary>池化的单个音效播放器：复用同一套宿主与 AudioSource，播放前配参数，播完只停不销毁。</summary>
    public sealed class AudioVoice
    {
        // 宿主对象；回池后保留，正常路径只有 Dispose 才销毁，被外部连根销毁时 IsAlive 会变 false
        private GameObject m_go;

        // 播放器组件；与宿主同生命周期，回池后保留，复用它播放而不是重新 AddComponent
        private AudioSource m_source;

        /// <summary>创建一个播放器：建宿主、挂一个 AudioSource、关掉自动播放，并先置为闲置。</summary>
        /// <param name="name">对象名，便于在 Hierarchy 里定位；空或 null 时用 AudioVoice。</param>
        /// <param name="parent">父节点，null 表示挂在场景根。</param>
        public AudioVoice(string name, Transform parent)
        {
            // 功能：构造一个闲置的播放器，等池来取用。
            // 入参：name 对象名（空或 null 时用 AudioVoice）；parent 父节点（null 表示不挂父级）
            // 返回：无（构造函数）
            // 步骤：
            //   1) 先建 GameObject；有父节点时用 SetParent(parent, false) 挂上去 —— 传 false 保持本地坐标（原点、缩放 1），不把父级偏移折进本地坐标，后续直接赋世界坐标即可
            //   2) 再 AddComponent<AudioSource>，一个播放器只配一个组件，复用就复用这一个
            //   3) 拿到组件后立刻关掉 playOnAwake，顺序不能拖到赋 clip 之后
            //   4) 最后 SetActive(false) 进入闲置：不参与场景音频更新，也方便在 Hierarchy 里数出正在工作的播放器
            // 上游：AudioManager 的池工厂 CreateVoice
            // 下游：无
            // 注意：playOnAwake 必须在第一次 SetActive(true) 之前关掉；否则将来赋了 clip 再激活，会在不希望的时刻自动播一次。
            m_go = new GameObject(string.IsNullOrEmpty(name) ? "AudioVoice" : name);

            if (parent != null)
            {
                m_go.transform.SetParent(parent, false);
            }

            m_source = m_go.AddComponent<AudioSource>();

            m_source.playOnAwake = false;

            m_go.SetActive(false);
        }

        /// <summary>播放器编号，int；由 AudioManager 发放、从 1 起递增，0 表示当前未在播放。</summary>
        public int Id { get; internal set; }

        /// <summary>发号顺序，long；每轮播放递增，越大越新，0 表示未占用，池满时按它抢同优先级里最老的。</summary>
        public long Sequence { get; internal set; }

        /// <summary>本次播放的优先级，AudioPriority；决定池满时谁被抢占，回池后复位为 Low。</summary>
        public AudioPriority Priority { get; internal set; }

        /// <summary>本次播放所属的音量分组，AudioBus；分组音量是 0..1 线性值，回池后复位为 Sfx。</summary>
        public AudioBus Bus { get; internal set; }

        /// <summary>开始播放时的逻辑帧号，帧（不是秒）；用来避免刚播一帧就被判为播完。</summary>
        public int StartFrame { get; internal set; }

        /// <summary>本次播放的单次音量，0..1 线性值，不含分组音量。</summary>
        // 注意：必须把原值存下来：分组音量随时可调，重算最终音量要用「分组音量 × 单次音量」；若拿当前最终音量去乘，会越调越小。
        public float RequestVolume { get; internal set; }

        /// <summary>宿主对象，调试与测试用；回池后仍然有效，除非被外部销毁。</summary>
        // 功能：交出宿主引用，只读，不转移所有权。
        public GameObject Target
        {
            get { return m_go; }
        }

        /// <summary>播放器组件，调试与测试用；与宿主同生命周期。</summary>
        // 功能：交出 AudioSource 引用，参数仍由本类负责配置。
        public AudioSource Source
        {
            get { return m_source; }
        }

        /// <summary>宿主是否仍然有效；false 表示 GameObject 或 AudioSource 已被外部销毁。</summary>
        // 功能：同时判宿主与组件是否还在，被外部销毁时返回 false。
        // 注意：Unity 的 Object 重载了 ==，被销毁的对象与 null 比较会返回 true（「伪 null」），所以这里问的是「对象还活着吗」，不是「引用是不是 null」。
        public bool IsAlive
        {
            get { return m_go != null && m_source != null; }
        }

        /// <summary>配好本次播放的参数（clip、循环、音量、音高、3D 距离），激活宿主并开始播放。</summary>
        /// <param name="request">播放请求；Clip 为 null 时不会出声，AudioManager 在入口已挡掉。</param>
        /// <param name="finalVolume">最终音量，0..1 线性值，通常是分组音量 × 单次音量；静音时传 0。</param>
        /// <param name="minDistance">3D 最小距离，米（此距离内不再衰减）。</param>
        /// <param name="maxDistance">3D 最大距离，米（超出后听不到，不应小于 minDistance）。</param>
        public void Apply(in AudioPlayRequest request, float finalVolume, float minDistance, float maxDistance)
        {
            // 功能：把请求里的 clip、循环、音量、音高与 3D 距离写进播放器，激活宿主并开始播放。
            // 入参：request 播放请求（Clip 为 null 时不会出声，AudioManager 在入口已挡）；finalVolume 最终音量，0..1 线性值（静音时是 0）；minDistance 3D 最小距离，米；maxDistance 3D 最大距离，米
            // 返回：无
            // 步骤：
            //   1) 先确认宿主还活着：被外部销毁就直接返回，避免操作已销毁的对象
            //   2) 再 SetActive(true)：闲置的宿主是关闭的，关闭状态下 Play 不会出声
            //   3) 写 clip、loop、volume、pitch；pitch 是倍率，1 为原速、2 为高八度
            //   4) 3D 声音再设 spatialBlend = 1、线性衰减与远近距，并直接赋世界坐标；2D 声音把 spatialBlend 设回 0，免得上一次 3D 的空间化设置残留
            //   5) 最后 Play：参数全部就位后再播，避免第一帧用上一轮的音量与音高
            // 上游：AudioManager.Play（音效）
            // 下游：AudioSource 的 Play
            // 注意：3D 位置直接赋世界坐标，前提是宿主挂在原点的 Voices 节点下且无缩放；父级一旦带位移或缩放，声音位置就会偏。
            // 注意：静音与并发上限都不在这里判断：分组静音只体现为传进来的 finalVolume 为 0，最多能同时响几个由 AudioManager 管。
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

                // 直接赋世界坐标：宿主在原点且无缩放，父子变换不会引入偏差。
                m_go.transform.position = request.Position;
            }
            else
            {
                m_source.spatialBlend = 0f;
            }

            m_source.Play();
        }

        /// <summary>在播放中直接改播放器音量；分组音量或静音变化时立刻跟上。</summary>
        /// <param name="finalVolume">重新算好的最终音量，0..1 线性值（分组音量 × 单次音量，静音时为 0）。</param>
        public void SetVolume(float finalVolume)
        {
            // 功能：只改音量，让正在播的播放器跟上分组音量或静音的调整。
            // 入参：finalVolume 重新算好的最终音量，0..1 线性值（静音时为 0）
            // 返回：无
            // 步骤：先判组件是否还在，再只写 volume；不动 clip、音高与播放状态，所以调整音量不会打断播放
            // 上游：AudioManager.ApplyBusVolume（SetBusVolume / SetBusMuted 之后调用）
            // 下游：AudioSource.volume
            // 注意：传进来的必须是「分组音量 × 单次音量」重算的结果，不能拿旧的最终音量再乘一次，否则会越调越小。
            if (m_source != null)
            {
                m_source.volume = finalVolume;
            }
        }

        /// <summary>停止播放并清空本轮参数，让播放器可以回池复用。</summary>
        public void StopAndReset()
        {
            // 功能：停止播放、清空 clip 与空间化设置、关闭宿主，并把播放状态复位成闲置。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 先 Stop 并清 clip、关 loop、spatialBlend 归零 —— 下一轮即使忘了重配 3D，也不会沿用这一轮的空间化
            //   2) 再把宿主 SetActive(false) 进入闲置，不参与场景音频更新
            //   3) 最后把 Id、Sequence 归零，Priority / Bus / StartFrame / RequestVolume 复位到默认值，保证下一轮从干净状态开始
            // 上游：AudioManager.RecycleVoice（Stop、StopAll、播完回收与抢占都经它）
            // 下游：无
            // 注意：不销毁 GameObject 与 AudioSource —— 这是与用完就 Destroy 的关键区别，池化复用全靠它。
            // 注意：Id 归零是有意的：已经发出去的旧句柄会因此失效，调用方拿上一轮的句柄来 Stop 时不会误伤新播放的音效。
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

        /// <summary>真正销毁宿主对象；只在池被清空或管理器销毁时调用。</summary>
        public void Dispose()
        {
            // 功能：销毁宿主并断开两个引用，让播放器彻底消失。
            // 入参：无
            // 返回：无
            // 步骤：先销毁宿主（挂在上面的 AudioSource 会一起被销毁），再把字段置 null，避免之后拿到伪 null 继续用
            // 上游：AudioManager.DisposeVoice（池的销毁器），池 Clear / Dispose 时对闲置播放器调用
            // 下游：DestroyObject
            // 注意：调用后本对象不可再用：IsAlive 返回 false，Apply 直接返回，SetVolume 与 StopAndReset 也不会再抛异常。
            DestroyObject(m_go);
            m_go = null;
            m_source = null;
        }

        /// <summary>调试文本：编号、对象名、分组与优先级。</summary>
        /// <returns>可读描述；宿主已销毁时对象名用固定占位文本代替。</returns>
        public override string ToString()
        {
            // 功能：拼一行调试文本，带上编号、对象名、分组与优先级。
            // 入参：无
            // 返回：可读字符串；宿主已销毁时对象名用占位文本代替
            // 步骤：先判宿主是否还在再取 name —— 已销毁的宿主再取 name 会抛 MissingReferenceException，所以用占位符兜住
            // 上游：日志、测试失败信息
            // 下游：无
            return "AudioVoice#" + Id + "(" + (m_go != null ? m_go.name : "<destroyed>")
                   + " bus=" + Bus + " prio=" + Priority + ")";
        }

        // 功能：按运行模式选销毁方式：播放中用 Destroy，编辑器（不在播放）里用 DestroyImmediate。
        // 入参：go 要销毁的宿主对象；null 或已被销毁时直接返回
        // 返回：无
        // 步骤：
        //   1) 先挡空引用 / 伪 null：Unity 的 == 能识别已销毁对象，重复销毁没有意义
        //   2) 播放模式交给 Object.Destroy，延迟到帧末销毁，符合 Unity 生命周期
        //   3) 非播放模式用 Object.DestroyImmediate，否则 Unity 会报错
        // 上游：Dispose
        // 下游：Object.Destroy / Object.DestroyImmediate
        // 注意：EditMode 下不能调 Object.Destroy —— Unity 会报「Destroy may not be called from edit mode! Use DestroyImmediate instead.」，这条错误会污染测试结果，日志断言会因此失败。
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
