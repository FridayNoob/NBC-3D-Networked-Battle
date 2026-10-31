// 这个文件里有：
//   IAudioPlaybackProbe（接口）：判断一路音频播完没有的注入点，把播放状态变成可替换的答案。

using UnityEngine;

namespace NBC.Framework.Audio
{
    // 主要功能：回答「这一路音频播完了没有」；这是测试缝，不是为将来换后端留的抽象，唯一目的是让播完回池的逻辑可测。
    // 主要数据成员：无，纯契约接口，不持有任何状态。
    // 主要外部方法：IsPlaying(AudioSource)，实现方决定怎么回答。
    // 谁在用它：AudioManager.Tick 每帧回收音效前问一次；UnityAudioPlaybackProbe 是真机实现，Tests 里的 FakePlaybackProbe 是测试实现。
    /// <summary>判断一路播放器是否还在播；跨程序集的注入点，测试可用假实现替换。</summary>
    public interface IAudioPlaybackProbe
    {
        // 功能：回答某个播放器是否还在播，供调用方决定是否把它回收复用。
        // 入参：source 目标播放器，允许为 null 或已被销毁。
        // 返回：true 还在播；false 已停止、已销毁，或环境根本判断不了（EditMode、无音频设备）。
        // 步骤：
        //   1) 先判 null 与伪 null —— Unity 的 Object 重载了相等运算，已销毁对象与 null 比较也会成立，不先判就会抛 MissingReferenceException
        //   2) 再读 isPlaying —— 只有它为 true 才当作还在播
        //   3) 其余一律返回 false —— 宁可让调用方回收，也不让死掉的播放器一直占着并发名额
        // 上游：AudioManager.Tick；测试与真机实现
        // 下游：无，实现方直接读 AudioSource.isPlaying
        // 注意：EditMode 与没有音频设备的环境里 isPlaying 恒为 false 且不推进，探针只能报没在播；真机是否真的出声只能靠耳朵验，别拿它当出声证明。
        // 注意：探针不负责静音开关 —— 静音只是把音量降到 0，isPlaying 仍为 true，所以静音期间音效不会被提前回收。
        // 注意：并发上限与回池归 AudioManager 管，探针只回答状态；把还在播的误判成播完，池会立刻复用这个播放器，声音就被掐掉。
        /// <summary>查询一路播放器是否还在播；传 null 或已销毁的播放器必须返回 false，不能抛异常。</summary>
        /// <param name="source">目标播放器，允许为 null 或已被销毁。</param>
        /// <returns>true 表示还在播；false 表示已停止、已销毁或无法判断。</returns>
        bool IsPlaying(AudioSource source);
    }
}
