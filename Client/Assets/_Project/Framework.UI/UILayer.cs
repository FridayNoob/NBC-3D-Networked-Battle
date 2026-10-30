// 这个文件里有：
//   UILayer（枚举）：UI 层级，数值越大渲染越靠上层

namespace NBC.Framework.UI
{
    // 主要功能：只描述 UI 分为哪几层、谁盖住谁，给面板挂载、遮罩置顶与加载顺序提供公共词汇。
    // 主要数据成员：Bot / Mid / Top / System 四个层级值，依次为 0 到 3，越大越靠上层。
    // 主要外部方法：无，纯枚举，没有成员方法。
    // 谁在用它：UIManager 与 UILayers 组件，以及 Game / Boot 里指定面板层级的业务代码。
    // 注意：「有哪几层」是 UI 的公共词汇，所以放在这里而不是塞进 UIManager：
    //   面板要知道自己能挂在哪，加载遮罩要知道自己要被抬到最上面，业务层要指定面板放哪；
    //   塞进管理器会让所有调用方都去依赖管理器。
    // 注意：顺序有含义，数值越大越靠前，Bot 在最底下（背景、HUD），System 在最上面（弹窗、加载遮罩、断线提示）；
    //   UILayers 组件靠这个顺序做人话报错，不要随便调换顺序。
    /// <summary>UI 层级，数值越大越靠上层。</summary>
    public enum UILayer
    {
        /// <summary>最底层：背景与主界面 HUD，层级值最小。</summary>
        Bot = 0,

        /// <summary>中间层：普通功能面板（背包、设置），原框架的默认层。</summary>
        Mid = 1,

        /// <summary>上层：盖住普通面板的界面（角色详情、确认框）。</summary>
        Top = 2,

        /// <summary>最顶层：加载遮罩、断线提示与系统级弹窗，永远盖在所有东西上面。</summary>
        System = 3
    }
}
