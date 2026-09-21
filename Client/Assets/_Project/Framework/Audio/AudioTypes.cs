// ============================================================================
//  NBC.Framework.Audio · 音频层的基础类型
//  对应需求：FW-M08（背景音乐 + 3D 音效池 + 音量分组 + 回池）
//  缺陷编号：FW-09（音效对象不复用 / 宿主未 DontDestroyOnLoad）、P-06、P-07
//  完整记录：Docs/06-框架改造记录.md §三 FW-09、§四 P-06 / P-07
//
//  ---------------------------------------------------------------------------
//  原版错在哪（FW-09，`Music/MusicMgr.cs` 真实代码）
//  ---------------------------------------------------------------------------
//      // :27-34  Update
//      for( int i = soundList.Count - 1; i >= 0; --i )
//          if( !soundList[i].isPlaying ) { GameObject.Destroy(soundList[i]); soundList.RemoveAt(i); }
//
//      // :97-101 / :105  PlaySound
//      soundObj = new GameObject();  soundObj.name = "Sound";          // 没有 DontDestroyOnLoad
//      ...
//      AudioSource source = soundObj.AddComponent<AudioSource>();      // 每次播放都新建组件
//
//  三条真实问题（措辞更正见 Docs/06 §三 FW-09 与 §5.2）：
//    ① **音效对象不复用**：每次播放 `AddComponent<AudioSource>()`，播完销毁组件。
//       没有池、没有复用、没有并发上限 —— 密集音效时组件创建/销毁抖动。
//    ② **宿主未 `DontDestroyOnLoad`**：`soundObj` / `BkMusic` 都是 `new GameObject()`。
//       切场景后对象被销毁，但字段与 `soundList` 仍持引用（Unity 伪 null）→
//       下次 `PlaySound` 往已销毁对象上 `AddComponent` → **`MissingReferenceException`**（P-06）。
//    ③ `Update` 直接访问 `soundList[i].isPlaying`，无有效性防御（P-07）。
//
//  ⚠️ 顺带更正一处**审计报告自己的错误**：它说 `GameObject.Destroy(AudioSource)` 是
//     "等价于销毁其 gameObject"。**Unity 官方文档相反** —— 传 Component 时
//     "removes the component from the GameObject and destroys it"，**宿主还活着**。
//     所以不存在"把共享宿主一起销毁"的连锁问题；真实问题就是上面三条。
//
//  ---------------------------------------------------------------------------
//  为什么用「音量分组」而不是一个全局音量（FW-M08）
//  ---------------------------------------------------------------------------
//  玩家要调的是"音乐太大 / 音效太吵"，这两件事必须**分开**。原版只有两个 float
//  （`bkValue` / `soundValue`），连 UI 音效都只能挤在 soundValue 里。
//  这里按**用途**分三组：BGM / SFX / UI，各组独立可调、可静音。
//  最终音量 = 分组音量 × 单次请求音量（两个都在 0..1，见 `AudioManager.EffectiveVolume`）。
// ============================================================================

using System;
using UnityEngine;

namespace NBC.Framework.Audio
{
    /// <summary>
    /// 音量分组。**按用途分，不是按对象分** —— 玩家想调的是"音乐"和"音效"，
    /// 而不是"第 3 号 AudioSource"。
    /// </summary>
    public enum AudioBus
    {
        /// <summary>背景音乐。</summary>
        Bgm = 0,

        /// <summary>游戏音效（打击、技能、脚步…）。</summary>
        Sfx = 1,

        /// <summary>界面音效（按钮、弹窗）。</summary>
        Ui = 2
    }

    /// <summary>
    /// 播放优先级。**只在"池满了"的时候起作用** —— 决定谁被抢占、谁被丢弃。
    /// <para>
    /// ⚠️ 枚举值有大小意义：数值越大越重要。抢占规则见 <c>AudioManager</c>。
    /// </para>
    /// </summary>
    public enum AudioPriority
    {
        /// <summary>可有可无（环境音、远处的杂音）。最先被抢占。</summary>
        Low = 0,

        /// <summary>默认。</summary>
        Normal = 1,

        /// <summary>重要反馈（玩家受击、技能命中）。</summary>
        High = 2,

        /// <summary>绝不能被抢占（剧情语音、胜负提示）。</summary>
        Critical = 3
    }

    /// <summary>
    /// 一次播放请求。**纯参数，不含状态** —— 播放中的状态在 <c>AudioVoice</c> 里。
    /// <para>
    /// 之所以做成 <c>readonly struct</c>：它是**每次播放传一次**的参数包，
    /// 走堆分配没有必要；而且值语义让它能安全地被日志 / 测试持有。
    /// </para>
    /// </summary>
    public readonly struct AudioPlayRequest : IEquatable<AudioPlayRequest>
    {
        /// <summary>要播的音频素材。**不允许为 null**（<c>Play</c> 会 fail-fast）。</summary>
        public readonly AudioClip Clip;

        /// <summary>走哪个音量分组。</summary>
        public readonly AudioBus Bus;

        /// <summary>优先级（只在池满时起作用）。</summary>
        public readonly AudioPriority Priority;

        /// <summary>是否循环。</summary>
        public readonly bool Loop;

        /// <summary>单次音量（0..1），会与分组音量相乘。</summary>
        public readonly float Volume;

        /// <summary>音调倍率（1 = 原速）。</summary>
        public readonly float Pitch;

        /// <summary>
        /// 是否 3D 空间音效。
        /// <para>true 时按 <see cref="Position"/> 摆放播放器；false 时是 2D（UI 音效、BGM）。</para>
        /// </summary>
        public readonly bool Is3D;

        /// <summary>3D 音效的世界坐标。仅当 <see cref="Is3D"/> 为 true 时有意义。</summary>
        public readonly Vector3 Position;

        /// <summary>构造一个播放请求。</summary>
        /// <param name="clip">音频素材。</param>
        /// <param name="bus">音量分组。</param>
        /// <param name="priority">优先级。</param>
        /// <param name="loop">是否循环。</param>
        /// <param name="volume">单次音量（0..1）。</param>
        /// <param name="pitch">音调倍率。</param>
        /// <param name="is3D">是否 3D 空间音效。</param>
        /// <param name="position">3D 音效的世界坐标。</param>
        public AudioPlayRequest(AudioClip clip, AudioBus bus, AudioPriority priority, bool loop,
                                float volume, float pitch, bool is3D, Vector3 position)
        {
            Clip = clip;
            Bus = bus;
            Priority = priority;
            Loop = loop;
            Volume = volume;
            Pitch = pitch;
            Is3D = is3D;
            Position = position;
        }

        /// <summary>
        /// 背景音乐：2D、默认循环、走 <see cref="AudioBus.Bgm"/>。
        /// </summary>
        /// <param name="clip">音频素材。</param>
        /// <param name="volume">单次音量（0..1）。</param>
        /// <param name="loop">是否循环。</param>
        /// <returns>播放请求。</returns>
        public static AudioPlayRequest Music(AudioClip clip, float volume = 1f, bool loop = true)
        {
            return new AudioPlayRequest(clip, AudioBus.Bgm, AudioPriority.Critical, loop,
                                        volume, 1f, false, Vector3.zero);
        }

        /// <summary>
        /// 2D 音效（UI 音效、无方向的反馈音）。
        /// <para>默认走 <see cref="AudioBus.Sfx"/>；UI 音效请显式传 <see cref="AudioBus.Ui"/>。</para>
        /// </summary>
        /// <param name="clip">音频素材。</param>
        /// <param name="volume">单次音量（0..1）。</param>
        /// <param name="priority">优先级。</param>
        /// <param name="pitch">音调倍率。</param>
        /// <param name="bus">音量分组。</param>
        /// <returns>播放请求。</returns>
        public static AudioPlayRequest Sound2D(AudioClip clip, float volume = 1f,
                                               AudioPriority priority = AudioPriority.Normal,
                                               float pitch = 1f, AudioBus bus = AudioBus.Sfx)
        {
            return new AudioPlayRequest(clip, bus, priority, false, volume, pitch, false, Vector3.zero);
        }

        /// <summary>
        /// 3D 音效：按世界坐标摆放，带距离衰减。
        /// </summary>
        /// <param name="clip">音频素材。</param>
        /// <param name="position">世界坐标。</param>
        /// <param name="volume">单次音量（0..1）。</param>
        /// <param name="priority">优先级。</param>
        /// <param name="pitch">音调倍率。</param>
        /// <param name="loop">是否循环（例如持续的环境音）。</param>
        /// <returns>播放请求。</returns>
        public static AudioPlayRequest Sound3D(AudioClip clip, Vector3 position, float volume = 1f,
                                               AudioPriority priority = AudioPriority.Normal,
                                               float pitch = 1f, bool loop = false)
        {
            return new AudioPlayRequest(clip, AudioBus.Sfx, priority, loop, volume, pitch, true, position);
        }

        /// <summary>逐字段比较。</summary>
        /// <param name="other">另一个请求。</param>
        /// <returns>是否完全相同。</returns>
        public bool Equals(AudioPlayRequest other)
        {
            return Clip == other.Clip && Bus == other.Bus && Priority == other.Priority
                   && Loop == other.Loop && Volume.Equals(other.Volume) && Pitch.Equals(other.Pitch)
                   && Is3D == other.Is3D && Position.Equals(other.Position);
        }

        /// <summary>逐字段比较。</summary>
        /// <param name="obj">另一个对象。</param>
        /// <returns>是否为相同的请求。</returns>
        public override bool Equals(object obj)
        {
            return obj is AudioPlayRequest && Equals((AudioPlayRequest)obj);
        }

        /// <summary>组合哈希。</summary>
        /// <returns>哈希值。</returns>
        public override int GetHashCode()
        {
            int h = Clip != null ? Clip.GetHashCode() : 0;
            h = (h * 397) ^ (int)Bus;
            h = (h * 397) ^ (int)Priority;
            h = (h * 397) ^ Loop.GetHashCode();
            h = (h * 397) ^ Volume.GetHashCode();
            h = (h * 397) ^ Pitch.GetHashCode();
            return h;
        }

        /// <summary>调试文本。</summary>
        /// <returns>可读描述。</returns>
        public override string ToString()
        {
            return "AudioPlayRequest(clip=" + (Clip != null ? Clip.name : "<null>") + " bus=" + Bus
                   + " prio=" + Priority + " loop=" + Loop + " vol=" + Volume
                   + (Is3D ? " pos=" + Position : " 2D") + ")";
        }
    }

    /// <summary>
    /// 一个播放器**为什么**离开了播放列表。
    /// <para>
    /// ⚠️ 为什么要分这么细：调用方通常要在"音效结束时"**释放它加载素材时拿到的资源句柄**
    /// （见 `Docs/06` §13.7：素材加载与播放是分开的）。如果只在"自然播完"时通知，
    /// 那么**被抢占**和**被停掉**的音效就会漏掉通知 → 资源句柄泄漏。
    /// 所以这里是"**离开播放列表**"的全集，不是"播完"。
    /// </para>
    /// </summary>
    public enum AudioEndReason
    {
        /// <summary>正常播完（探针报告"没在播了"）。</summary>
        Finished = 0,

        /// <summary>池满，被更高优先级的音效**抢占**了。</summary>
        Preempted = 1,

        /// <summary>被显式停掉（`Stop` / `StopAll` / BGM 被换掉）。</summary>
        Stopped = 2,

        /// <summary>宿主被外部销毁（或随场景一起没了）。**这原本会抛 `MissingReferenceException`**。</summary>
        Lost = 3
    }

    /// <summary>
    /// 一次播放的句柄。用来停掉 / 查询某个正在播放的音效。
    /// <para>
    /// ⚠️ **这里 <c>0</c> 就是"无效"哨兵**（`Id` 从 1 开始发号）——
    /// 和 A7 的 <see cref="NBC.Framework.Input.InputActionId"/> **正好相反**：
    /// 那个的 0 是**合法索引**，不能拿来判空。两个类型的取舍不同，原因也不同：
    /// 这里句柄是"外部发放的凭据"，天然需要一个"没拿到"的表示；那里是"稠密下标"，0 必须可用。
    /// </para>
    /// <para>
    /// ⚠️ <c>Id</c> **只增不减、永不复用** —— 复用会让"停掉上一首"变成"停掉刚播的这首"（ABA 问题）。
    /// </para>
    /// </summary>
    public readonly struct AudioVoiceHandle : IEquatable<AudioVoiceHandle>
    {
        /// <summary>无效句柄（没拿到播放器 / 被丢弃时返回它）。</summary>
        public static readonly AudioVoiceHandle None = default;

        /// <summary>播放器编号，从 1 开始。</summary>
        public readonly int Id;

        /// <summary>构造句柄。</summary>
        /// <param name="id">播放器编号，必须大于 0。</param>
        public AudioVoiceHandle(int id)
        {
            Id = id;
        }

        /// <summary>句柄是否有效。</summary>
        public bool IsValid
        {
            get { return Id > 0; }
        }

        /// <summary>按编号比较。</summary>
        /// <param name="other">另一个句柄。</param>
        /// <returns>是否相同。</returns>
        public bool Equals(AudioVoiceHandle other)
        {
            return Id == other.Id;
        }

        /// <summary>按编号比较。</summary>
        /// <param name="obj">另一个对象。</param>
        /// <returns>是否为相同的句柄。</returns>
        public override bool Equals(object obj)
        {
            return obj is AudioVoiceHandle && Equals((AudioVoiceHandle)obj);
        }

        /// <summary>编号的哈希值。</summary>
        /// <returns>哈希值。</returns>
        public override int GetHashCode()
        {
            return Id;
        }

        /// <summary>调试文本。</summary>
        /// <returns>可读描述。</returns>
        public override string ToString()
        {
            return IsValid ? "AudioVoice#" + Id : "AudioVoice<None>";
        }

        /// <summary>相等比较。</summary>
        /// <param name="left">左操作数。</param>
        /// <param name="right">右操作数。</param>
        /// <returns>是否相等。</returns>
        public static bool operator ==(AudioVoiceHandle left, AudioVoiceHandle right)
        {
            return left.Equals(right);
        }

        /// <summary>不等比较。</summary>
        /// <param name="left">左操作数。</param>
        /// <param name="right">右操作数。</param>
        /// <returns>是否不相等。</returns>
        public static bool operator !=(AudioVoiceHandle left, AudioVoiceHandle right)
        {
            return !left.Equals(right);
        }
    }
}
