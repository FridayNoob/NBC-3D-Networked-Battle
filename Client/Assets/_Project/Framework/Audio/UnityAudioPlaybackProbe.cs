// ============================================================================
//  NBC.Framework.Audio · IAudioPlaybackProbe 的真实运行实现
//  对应需求：FW-M08
//
//  ⚠️ 这里的 `source != null` 不是多余的防御 —— Unity 的 `Object` 重载了 `==`，
//     被销毁的对象与 null 比较会返回 true（"伪 null"）。
//     原版 `MusicMgr.Update` 缺的正是这个检查（P-07）。
// ============================================================================

using UnityEngine;

namespace NBC.Framework.Audio
{
    /// <summary>
    /// 真实运行用的探针：直接问 <see cref="AudioSource.isPlaying"/>。
    /// </summary>
    public sealed class UnityAudioPlaybackProbe : IAudioPlaybackProbe
    {
        /// <summary>
        /// 查询播放状态。已销毁的播放器一律视为"没在播"，让调用方回收它。
        /// </summary>
        /// <param name="source">目标播放器。</param>
        /// <returns>true 表示还在播。</returns>
        public bool IsPlaying(AudioSource source)
        {
            return source != null && source.isPlaying;
        }
    }
}
