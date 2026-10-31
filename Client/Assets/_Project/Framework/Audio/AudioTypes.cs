// 这个文件里有：
//   AudioBus（枚举）：音量分组，按用途分成背景音乐 / 游戏音效 / 界面音效三组
//   AudioPriority（枚举）：播放优先级，数值越大越重要，只在播放池满时决定谁被抢占
//   AudioPlayRequest（结构体）：一次播放请求的参数包，只带参数不带播放状态
//   AudioEndReason（枚举）：播放器离开播放列表的原因全集，供调用方按原因释放素材句柄
//   AudioVoiceHandle（结构体）：一次播放的句柄，编号从 1 起、只增不复用，0 是无效哨兵

using System;
using UnityEngine;

namespace NBC.Framework.Audio
{
    // 主要功能：把音量按用途分成背景音乐 / 游戏音效 / 界面音效三组，各组独立调音量、独立静音；最终音量 = 分组音量 × 单次请求音量，两者都是 0..1 线性值，静音时为 0。
    // 主要数据成员：无，枚举值本身就是分组编号，取值 0..2，AudioManager 直接拿它当分组数组下标。
    // 主要外部方法：无。
    // 谁在用它：AudioManager 的 SetBusVolume / SetBusMuted / GetBusVolume / EffectiveVolume 按它取下标；AudioPlayRequest.Bus 与 AudioVoice.Bus 存它；EditMode 测试直接传值。
    // 注意：按用途分组而不是按对象分组 —— 玩家要调的是「音乐太吵」和「音效太吵」两件事，界面音效也不该混进游戏音效里。
    /// <summary>音量分组：背景音乐 / 游戏音效 / 界面音效，各组独立调音量与静音。</summary>
    public enum AudioBus
    {
        /// <summary>背景音乐；编号 0，AudioManager 的分组数组下标与它一一对应。</summary>
        Bgm = 0,

        /// <summary>游戏音效（打击、技能、脚步等）；编号 1，不指定分组时的默认值。</summary>
        Sfx = 1,

        /// <summary>界面音效（按钮、弹窗）；编号 2，要播 UI 音效必须显式传它，否则会落到 Sfx。</summary>
        Ui = 2
    }

    // 主要功能：给一次播放请求标重要程度，只在播放池已满、要腾位置时起作用；数值越大越重要，同级不抢。
    // 主要数据成员：无，枚举值是优先级档位，取值 0..3。
    // 主要外部方法：无。
    // 谁在用它：AudioPlayRequest.Priority 与 AudioVoice.Priority 存它；AudioManager.RentVoice 在池满时按它决定抢谁或丢谁；EditMode 测试构造各档优先级验证抢占顺序。
    // 注意：抢占条件是「被抢者优先级严格低于新请求」，同级不抢 —— 否则两个同优先级音效会互相踩，表现是密集打击音时后一个把前一个吃掉，比丢音效更难查。
    // 注意：Critical 永远不会被抢占（没有比它更高的档位），这正是它存在的意义：剧情语音不该被打击音吃掉。
    /// <summary>播放优先级：只在播放池满时决定抢占谁、丢弃谁，数值越大越重要。</summary>
    public enum AudioPriority
    {
        /// <summary>可有可无（环境音、远处的杂音）；数值 0，池满时最先被抢占，也是播放器回池后的复位值。</summary>
        Low = 0,

        /// <summary>默认档；数值 1，池满时只输给 High 与 Critical。</summary>
        Normal = 1,

        /// <summary>重要反馈（玩家受击、技能命中）；数值 2，池满时可抢占 Low 与 Normal。</summary>
        High = 2,

        /// <summary>绝不能被抢占（剧情语音、胜负提示）；数值 3，池满时只会丢弃新请求，不会抢它。</summary>
        Critical = 3
    }

    // 主要功能：把「播什么、走哪个分组、多大声、多高音、是不是 3D、摆在哪」打包成一次请求交给 AudioManager.Play；纯参数，不含播放中的状态。
    // 主要数据成员：Clip（音频素材）、Bus（音量分组）、Priority（优先级）、Loop（是否循环）、Volume（单次音量，0..1 线性值）、Pitch（音调倍率）、Is3D（是否 3D）、Position（3D 世界坐标，米）；全为 readonly。
    // 主要外部方法：构造函数与 Music / Sound2D / Sound3D 三个静态工厂；Equals / GetHashCode / ToString。
    // 谁在用它：AudioManager.Play 的入参、AudioVoice.Apply 按它配播放器；EditMode 与 PlayMode 测试用 Sound2D / Sound3D 构造（含 Clip 为 null 的非法用例）；PlayBgm 直接配 AudioSource，不经过请求。
    // 注意：做成 readonly struct 是为每次播放传值不产生堆分配，并让日志与测试能安全持有；它不是长期状态，别当播放句柄存下来。
    // 注意：Clip 不允许为 null，AudioManager.Play 入口会 fail-fast 抛 ArgumentNullException；素材要先加载好再传进来。
    /// <summary>一次播放请求的参数包：素材、分组、优先级、音量、音高、3D 开关与坐标。</summary>
    public readonly struct AudioPlayRequest : IEquatable<AudioPlayRequest>
    {
        /// <summary>要播的音频素材；不允许为 null，AudioManager.Play 会在入口挡掉。</summary>
        public readonly AudioClip Clip;

        /// <summary>走哪个音量分组；取值 AudioBus 的 0..2，分组音量与单次音量相乘得最终音量。</summary>
        public readonly AudioBus Bus;

        /// <summary>优先级；取值 0..3，只在池满时参与比较，数值越大越重要。</summary>
        public readonly AudioPriority Priority;

        /// <summary>是否循环；true 时播到结尾自动重来，不会因为播完被回收。</summary>
        public readonly bool Loop;

        /// <summary>单次音量，0..1 线性值；不是最终音量，最终音量还要乘分组音量（AudioManager 会再钳一次 0..1）。</summary>
        public readonly float Volume;

        /// <summary>音调倍率，1 为原速、2 为高八度、0.5 为低八度；没有范围校验，按原值交给 AudioSource。</summary>
        public readonly float Pitch;

        /// <summary>是否 3D 空间音效；true 时按 Position 摆放并做距离衰减，false 是 2D（BGM、UI 音效）。</summary>
        public readonly bool Is3D;

        /// <summary>3D 音效的世界坐标，单位米；仅 Is3D 为 true 时有意义，2D 请求传 Vector3.zero。</summary>
        public readonly Vector3 Position;

        /// <summary>构造一次播放请求；八个参数按字段原样保存，不做校验。</summary>
        public AudioPlayRequest(AudioClip clip, AudioBus bus, AudioPriority priority, bool loop,
                                float volume, float pitch, bool is3D, Vector3 position)
        {
            // 功能：把八个参数存进只读字段，得到一个不可变的播放请求。
            // 入参：clip 音频素材（不允许 null，这里不查，由 AudioManager.Play 挡）；bus 音量分组 0..2；priority 优先级 0..3；loop 是否循环；volume 单次音量 0..1 线性值；pitch 音调倍率（1 为原速）；is3D 是否 3D；position 3D 世界坐标，米
            // 返回：无（构造函数）
            // 步骤：逐个字段搬值，顺序与声明一致 —— 校验（Clip 非空、音量钳制）统一放在 AudioManager.Play 与 EffectiveVolume 里，构造只做数据搬运，避免同一规则散落两处
            // 上游：Music / Sound2D / Sound3D 三个工厂，以及测试与业务直接 new
            // 下游：无
            Clip = clip;
            Bus = bus;
            Priority = priority;
            Loop = loop;
            Volume = volume;
            Pitch = pitch;
            Is3D = is3D;
            Position = position;
        }

        /// <summary>背景音乐请求：2D、Bgm 分组、Critical 优先级、音调原速、默认循环。</summary>
        /// <param name="clip">音频素材，不允许为 null。</param>
        /// <param name="volume">单次音量，0..1 线性值；还会再乘 Bgm 分组音量。</param>
        public static AudioPlayRequest Music(AudioClip clip, float volume = 1f, bool loop = true)
        {
            // 功能：按 BGM 的约定拼请求：2D、Bgm 分组、Critical 优先级、音调原速、默认循环。
            // 注意：真正的单路 BGM 用 AudioManager.PlayBgm（直接配 AudioSource，不吃这个请求）；把 Music 的结果交给 Play 会当成普通音效进池，只是优先级为 Critical。
            return new AudioPlayRequest(clip, AudioBus.Bgm, AudioPriority.Critical, loop,
                                        volume, 1f, false, Vector3.zero);
        }

        /// <summary>2D 音效请求：Sfx 分组、Normal 优先级、不循环、音调原速、位置为零。</summary>
        /// <param name="clip">音频素材，不允许为 null。</param>
        /// <param name="volume">单次音量，0..1 线性值。</param>
        /// <param name="bus">音量分组，默认 Sfx。</param>
        public static AudioPlayRequest Sound2D(AudioClip clip, float volume = 1f,
                                               AudioPriority priority = AudioPriority.Normal,
                                               float pitch = 1f, AudioBus bus = AudioBus.Sfx)
        {
            // 功能：拼一个 2D 音效请求，参数原样带进请求，不循环、位置为零。
            // 注意：UI 音效要显式传 AudioBus.Ui，否则会落到 Sfx，被当成游戏音效一起调音量。
            return new AudioPlayRequest(clip, bus, priority, false, volume, pitch, false, Vector3.zero);
        }

        /// <summary>3D 音效请求：Sfx 分组、Normal 优先级、按世界坐标摆放并做距离衰减。</summary>
        /// <param name="clip">音频素材，不允许为 null。</param>
        /// <param name="position">世界坐标，单位米。</param>
        /// <param name="volume">单次音量，0..1 线性值。</param>
        /// <param name="loop">是否循环，持续环境音用 true。</param>
        public static AudioPlayRequest Sound3D(AudioClip clip, Vector3 position, float volume = 1f,
                                               AudioPriority priority = AudioPriority.Normal,
                                               float pitch = 1f, bool loop = false)
        {
            // 功能：拼一个 3D 音效请求，Sfx 分组、Normal 优先级，按世界坐标摆放并走距离衰减。
            // 注意：远近衰减用 AudioManager 的全局最小 / 最大距离（默认 1 米 / 30 米），请求本身不带距离参数。
            return new AudioPlayRequest(clip, AudioBus.Sfx, priority, loop, volume, pitch, true, position);
        }

        /// <summary>逐字段比较两个请求是否完全相同，浮点用精确比较。</summary>
        public bool Equals(AudioPlayRequest other)
        {
            // 功能：八个字段一一比较，全部相同才算同一个请求。
            // 注意：Volume 与 Pitch 是 float.Equals 精确比较，经累加、缩放算出来的值与字面量常有末位误差，会判为不等；它只适合测试与字典键，不适合做业务判重。
            return Clip == other.Clip && Bus == other.Bus && Priority == other.Priority
                   && Loop == other.Loop && Volume.Equals(other.Volume) && Pitch.Equals(other.Pitch)
                   && Is3D == other.Is3D && Position.Equals(other.Position);
        }

        /// <summary>装箱比较：是请求类型才转给强类型 Equals。</summary>
        public override bool Equals(object obj)
        {
            // 功能：先判类型再转给强类型 Equals，别的类型一律不等。
            return obj is AudioPlayRequest && Equals((AudioPlayRequest)obj);
        }

        /// <summary>组合哈希：素材、分组、优先级、循环、音量、音高。</summary>
        public override int GetHashCode()
        {
            // 功能：把六个字段按 397 乘加组合成一个哈希，给字典与集合用。
            // 注意：Is3D 与 Position 没参与组合；相等请求的哈希必然一致，不等的请求也可能撞哈希，只影响分桶效率，不影响正确性。
            int h = Clip != null ? Clip.GetHashCode() : 0;
            h = (h * 397) ^ (int)Bus;
            h = (h * 397) ^ (int)Priority;
            h = (h * 397) ^ Loop.GetHashCode();
            h = (h * 397) ^ Volume.GetHashCode();
            h = (h * 397) ^ Pitch.GetHashCode();
            return h;
        }

        /// <summary>调试文本：素材名、分组、优先级、循环、音量，3D 时再带坐标。</summary>
        public override string ToString()
        {
            // 功能：拼一行请求摘要给日志与测试失败信息，素材为 null 时用固定占位符。
            return "AudioPlayRequest(clip=" + (Clip != null ? Clip.name : "<null>") + " bus=" + Bus
                   + " prio=" + Priority + " loop=" + Loop + " vol=" + Volume
                   + (Is3D ? " pos=" + Position : " 2D") + ")";
        }
    }

    // 主要功能：说明一个播放器为什么离开播放列表 —— 播完、被抢占、被停掉、宿主丢失，四者是全集，保证每种结束都能通知到调用方。
    // 主要数据成员：无，枚举值就是原因，取值 0..3。
    // 主要外部方法：无。
    // 谁在用它：AudioManager.VoiceEnded 事件的第二个参数，AudioManager 在每个结束出口按原因派发；句柄的使用方按原因决定是否释放素材句柄；EditMode 测试逐个原因断言。
    // 注意：这里收的是「离开播放列表」的全集，不是「播完」：只在自然播完时通知的话，被抢占与被停掉的音效会漏通知，为该音效加载的素材句柄就泄漏了。
    /// <summary>播放器离开播放列表的原因：播完 / 被抢占 / 被停掉 / 宿主丢失，用于让调用方在每个出口释放素材句柄。</summary>
    public enum AudioEndReason
    {
        /// <summary>正常播完（探针报告没在播了）；编号 0。</summary>
        Finished = 0,

        /// <summary>池满，被优先级严格更高的新请求抢走；编号 1。</summary>
        Preempted = 1,

        /// <summary>被显式停掉（Stop / StopAll / BGM 被换掉）；编号 2。</summary>
        Stopped = 2,

        /// <summary>宿主被外部销毁，或随场景一起没了；编号 3。</summary>
        // 注意：这时碰它的 AudioSource 会抛 MissingReferenceException；AudioManager 先把这一路从播放列表摘掉并还给池，失效的播放器在下次取用时被丢弃。
        Lost = 3
    }

    // 主要功能：给一次播放发一张凭据，用来停掉 / 查询 / 在结束回调里对上具体那一路；只有编号，不持有播放器引用。
    // 主要数据成员：Id（播放器编号，从 1 起递增，0 表示无效）、None（Id 为 0 的无效句柄）。
    // 主要外部方法：构造函数、IsValid 属性、Equals / GetHashCode / ToString 与 == / != 运算符。
    // 谁在用它：AudioManager 的 Play / PlayBgm 返回它，IsActive / Stop 收它，VoiceEnded 回调用它指出是哪一路结束；EditMode 与 PlayMode 测试直接比较、构造。
    // 注意：这里的 0 是无效哨兵（编号从 1 起发），与输入层的 InputActionId 正好相反 —— 那个 0 是合法索引，不能拿来判空。
    // 注意：编号只增不减、永不复用；复用会让「停掉上一首」变成「停掉刚播的这首」（ABA 问题）。
    /// <summary>一次播放的句柄：编号从 1 起、0 为无效；只增不复用，避免旧句柄误伤新播放。</summary>
    public readonly struct AudioVoiceHandle : IEquatable<AudioVoiceHandle>
    {
        /// <summary>无效句柄（Id 为 0）：池满丢弃请求、或没拿到播放器时返回它。</summary>
        public static readonly AudioVoiceHandle None = default;

        /// <summary>播放器编号，从 1 起递增、永不复用；0 表示没拿到句柄。</summary>
        public readonly int Id;

        /// <summary>用编号构造句柄；不校验，0 与负数都按无效处理。</summary>
        public AudioVoiceHandle(int id)
        {
            // 功能：把编号存进句柄，不做校验。
            // 入参：id 播放器编号，正常由 AudioManager 从 1 起发放；0 或负数表示无效，测试里也会故意传越界编号验证空操作
            // 返回：无（构造函数）
            // 步骤：直接写 Id —— 校验交给 IsValid 与 AudioManager 的查找，构造本身不抛异常，调用方传错值也能安全走到「无效」分支
            // 上游：AudioManager 发号处（Play / PlayBgm / VoiceEnded 回调）与测试
            // 下游：无
            Id = id;
        }

        /// <summary>句柄是否有效：Id 大于 0 为 true。</summary>
        // 功能：按 Id 是否大于 0 判断这是不是一个真实发放过的句柄。
        // 注意：有效不等于还在播 —— 音效结束后句柄本身仍然有效，要知道现在是否还在播得问 AudioManager.IsActive。
        public bool IsValid
        {
            get { return Id > 0; }
        }

        /// <summary>按编号比较两个句柄是否指向同一路播放。</summary>
        public bool Equals(AudioVoiceHandle other)
        {
            // 功能：只比 Id，编号相同就是同一路。
            return Id == other.Id;
        }

        /// <summary>装箱比较：是句柄类型才转给强类型 Equals。</summary>
        public override bool Equals(object obj)
        {
            // 功能：先判类型再转给强类型 Equals，别的类型一律不等。
            return obj is AudioVoiceHandle && Equals((AudioVoiceHandle)obj);
        }

        /// <summary>直接以编号作哈希。</summary>
        public override int GetHashCode()
        {
            // 功能：返回 Id 当哈希，编号唯一，分布天然均匀。
            return Id;
        }

        /// <summary>调试文本：有效时是 AudioVoice 加编号，无效时是固定占位文本。</summary>
        public override string ToString()
        {
            // 功能：输出便于日志辨认的短文本，无效句柄有固定写法。
            return IsValid ? "AudioVoice#" + Id : "AudioVoice<None>";
        }

        /// <summary>相等运算符，按编号比较。</summary>
        public static bool operator ==(AudioVoiceHandle left, AudioVoiceHandle right)
        {
            // 功能：按编号判相等，转给 Equals。
            return left.Equals(right);
        }

        /// <summary>不等运算符，按编号比较。</summary>
        public static bool operator !=(AudioVoiceHandle left, AudioVoiceHandle right)
        {
            // 功能：按编号判不等，转给 Equals 再取反。
            return !left.Equals(right);
        }
    }
}
