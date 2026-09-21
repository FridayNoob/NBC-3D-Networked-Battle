// ============================================================================
//  NBC.Framework.Scenes · 场景相关的事件定义
//  对应需求：FW-M06（场景管理：进度事件）
//
//  用 A3 的 `EventId` 声明式写法 —— 拼错编译不过，而不是运行期静默不触发。
//  各模块在自己的文件里声明自己那一组，不去挤同一个 enum。
// ============================================================================

using NBC.Framework;

namespace NBC.Framework.Scenes
{
    /// <summary>场景加载进度（只在**进度真的变化**时广播）。</summary>
    public readonly struct SceneProgressInfo
    {
        /// <summary>场景地址。</summary>
        public readonly string Location;

        /// <summary>进度 0..1。</summary>
        public readonly float Progress;

        /// <summary>构造。</summary>
        public SceneProgressInfo(string location, float progress)
        {
            Location = location;
            Progress = progress;
        }

        /// <summary>调试文本。</summary>
        public override string ToString()
        {
            return Location + " " + (Progress * 100f).ToString("F0") + "%";
        }
    }

    /// <summary>场景加载失败（含超时）。</summary>
    public readonly struct SceneFailureInfo
    {
        /// <summary>场景地址。</summary>
        public readonly string Location;

        /// <summary>失败原因。</summary>
        public readonly string Error;

        /// <summary>构造。</summary>
        public SceneFailureInfo(string location, string error)
        {
            Location = location;
            Error = error;
        }

        /// <summary>调试文本。</summary>
        public override string ToString()
        {
            return Location + " 失败：" + Error;
        }
    }

    /// <summary>场景事件标识。</summary>
    public static class SceneEvents
    {
        /// <summary>进度变化。参数 <see cref="SceneProgressInfo"/>。</summary>
        public static readonly EventId ProgressChanged = EventId.Declare("Scene.ProgressChanged");

        /// <summary>加载成功。参数 <c>string</c>（场景地址）。</summary>
        public static readonly EventId LoadSucceeded = EventId.Declare("Scene.LoadSucceeded");

        /// <summary>加载失败或超时。参数 <see cref="SceneFailureInfo"/>。</summary>
        public static readonly EventId LoadFailed = EventId.Declare("Scene.LoadFailed");
    }
}
