// 这个文件里有：
//   UnityAudioPlaybackProbe（类）：真实运行用的播放探针，直接读 AudioSource.isPlaying 回答「这一路音频是否还在播」

using UnityEngine;

namespace NBC.Framework.Audio
{
    // 主要功能：实现 IAudioPlaybackProbe，用 AudioSource.isPlaying 回答「这一路音频是否还在播」，是 AudioManager 判定播放结束、回收播放器的依据。
    // 主要数据成员：无字段、无状态，不缓存播放器引用，也不记上次查询结果。
    // 主要外部方法：IsPlaying（唯一方法，问一次答一次）。
    // 谁在用它：AudioManager 的 m_probe 字段默认 new 它、Reset 时重建；PlayMode 测试也会直接 new 来验证空引用与已销毁对象的容忍度。
    /// <summary>真实运行用的播放探针：直接问 AudioSource.isPlaying，已销毁或为空的播放器一律视为没在播。</summary>
    public sealed class UnityAudioPlaybackProbe : IAudioPlaybackProbe
    {
        // 功能：回答这一路音频是否还在播；已销毁或为空的播放器一律当作「没在播」，让调用方去回收它。
        // 入参：source 目标播放器；允许为 null，也允许是被 Unity 销毁过的对象（伪 null）。
        // 返回：true 表示还在播；false 表示已停、还没开始或对象已失效，调用方据此回收播放器。
        // 步骤：
        //   1) 先判 source != null，再读 isPlaying；顺序不能反，已销毁对象一访问成员就抛 MissingReferenceException
        //   2) 两个条件都成立才返回 true，任一不成立都返回 false
        // 注意：source != null 不是多余的防御 —— Unity 的 Object 重载了 ==，被销毁的对象与 null 比较会返回 true（伪 null），少了它就会在已销毁的播放器上读 isPlaying；无音频设备（含 EditMode / 批处理模式）时 isPlaying 恒为 false 且不推进，探针会立刻报「没在播」，播放随即被当成正常结束回收。
        // 上游：AudioManager.Tick 每帧巡检时逐路调用，false 即判为 Finished 并回池；PlayMode 测试直接调。
        // 下游：无，只读 AudioSource 状态，不改任何东西。
        /// <summary>查询播放状态；已销毁或为空的播放器一律视为没在播。</summary>
        /// <param name="source">目标播放器；允许为 null，也允许是已销毁的对象。</param>
        /// <returns>true 表示还在播；false 表示已停、尚未开始或对象已失效。</returns>
        public bool IsPlaying(AudioSource source)
        {
            return source != null && source.isPlaying;
        }
    }
}
