// ============================================================================
//  NBC.Framework.UI · UI 层的引用持有者（挂在 Canvas 预制体上）
//  对应需求：FW-M09
//  缺陷编号：FW-10（原版用 `canvas.Find("Bot")` 找层）
//
//  ---------------------------------------------------------------------------
//  原版错在哪（`UI/UIManager.cs:43-46`）
//  ---------------------------------------------------------------------------
//      bot    = canvas.Find("Bot");
//      mid    = canvas.Find("Mid");
//      top    = canvas.Find("Top");
//      system = canvas.Find("System");
//
//  `Transform.Find` 找不到时**返回 null 而不报错**。后果链条：
//      改名字 → Find 返回 null → `GetLayerFather` 返回 null
//      → `SetParent(null)` 把面板挂到**场景根节点**
//      → 面板不在 Canvas 下 → **不渲染**
//  → 玩家的体感是"**界面一片空白，Console 干干净净**"。
//
//  ⚠️ 这属于**静默失败**，比崩溃难查得多 —— 崩溃至少告诉你哪一行。
//     需求文档原文写的是"改名即崩"，**不准确**（见 Docs/06 §三 FW-10 的措辞修正）。
//
//  ---------------------------------------------------------------------------
//  修法：把"层在哪"从代码搬到资源上，并且**找不到就当场报错、还要说清缺哪个**
//  ---------------------------------------------------------------------------
//  · 四个层改成**序列化字段**（在 Inspector 里拖），代码里不再出现 "Bot" 这种字符串
//  · `DescribeProblem()` 把"哪个字段空了 / 谁和谁重了 / 谁不是我的子节点"
//    写成**一句人话**，`UIManager` 加载 Canvas 时立刻检查并抛异常
//
//  ⚠️ 为什么不是"找一个数组按顺序拖"：**顺序本身就是含义**，
//     拖反了不会报错 —— 那只是把"静默错位"从代码里搬到了 Inspector 里。
//     具名字段拖错了，一眼就能看出来。
// ============================================================================

using System;
using UnityEngine;

namespace NBC.Framework.UI
{
    /// <summary>
    /// 四个 UI 层节点的引用。**挂在 Canvas 预制体的根节点上。**
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UILayers : MonoBehaviour
    {
        /// <summary>层数量（<see cref="UILayer"/> 的取值个数）。</summary>
        public const int LayerCount = 4;

        /// <summary>最底层。</summary>
        [SerializeField]
        private Transform m_bot;

        /// <summary>中间层（普通面板的默认层）。</summary>
        [SerializeField]
        private Transform m_mid;

        /// <summary>上层。</summary>
        [SerializeField]
        private Transform m_top;

        /// <summary>最顶层（加载遮罩、系统弹窗）。</summary>
        [SerializeField]
        private Transform m_system;

        /// <summary>层的名字，**只用于报错和日志**。</summary>
        /// <param name="layer">层。</param>
        /// <returns>名字。</returns>
        public static string GetLayerName(UILayer layer)
        {
            switch (layer)
            {
                case UILayer.Bot: return "Bot";
                case UILayer.Mid: return "Mid";
                case UILayer.Top: return "Top";
                case UILayer.System: return "System";
                default: return "Unknown(" + (int)layer + ")";
            }
        }

        /// <summary>
        /// 取某一层的节点。
        /// <para>⚠️ 配置有问题时**抛异常并说明缺什么**，不返回 null。</para>
        /// </summary>
        /// <param name="layer">层。</param>
        /// <returns>该层的节点。</returns>
        public Transform Get(UILayer layer)
        {
            Transform result;
            if (TryGet(layer, out result))
            {
                return result;
            }

            throw new InvalidOperationException(
                "[UILayers] 取不到 «" + GetLayerName(layer) + "» 层。\n" + DescribeProblem());
        }

        /// <summary>
        /// 试着取某一层的节点。**配置有问题时也返回 false**（不抛异常），
        /// 供调用方自己决定怎么处理。
        /// </summary>
        /// <param name="layer">层。</param>
        /// <param name="layerRoot">取到的节点；失败时是 null。</param>
        /// <returns>是否取到。</returns>
        public bool TryGet(UILayer layer, out Transform layerRoot)
        {
            layerRoot = RawGet(layer);

            if (layerRoot == null)
            {
                return false;
            }

            // 拖到别的 Canvas 下面去了：面板会被挂到那边，界面会错位。
            // 这和不填一样是配置错误，所以一并挡住。
            if (!layerRoot.IsChildOf(transform))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 检查配置，返回**一句人话**说明问题；一切正常时返回 null。
        /// </summary>
        /// <returns>问题描述；正常时 null。</returns>
        public string DescribeProblem()
        {
            string missing = DescribeMissing();
            if (missing != null)
            {
                return missing;
            }

            string duplicated = DescribeDuplicated();
            if (duplicated != null)
            {
                return duplicated;
            }

            return DescribeNotChild();
        }

        /// <summary>配置是否完全正确。</summary>
        public bool IsValid
        {
            get { return DescribeProblem() == null; }
        }

        /// <summary>
        /// 手动指定某一层的节点。
        /// <para>
        /// ⚠️ **正常用法是在 Inspector 里拖**。这个方法给两种场合：
        /// ① 测试里手工搭出一个 UILayers；
        /// ② 运行时代码动态搭 Canvas（少见）。
        /// </para>
        /// </summary>
        /// <param name="layer">层。</param>
        /// <param name="layerRoot">节点；传 null 表示清空。</param>
        public void Bind(UILayer layer, Transform layerRoot)
        {
            switch (layer)
            {
                case UILayer.Bot:
                    m_bot = layerRoot;
                    return;
                case UILayer.Mid:
                    m_mid = layerRoot;
                    return;
                case UILayer.Top:
                    m_top = layerRoot;
                    return;
                case UILayer.System:
                    m_system = layerRoot;
                    return;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(layer), layer, "[UILayers] 未知的层。");
            }
        }

        /// <summary>按枚举取值（**不做任何校验**）。</summary>
        /// <param name="layer">层。</param>
        /// <returns>对应字段；没填时 null。</returns>
        private Transform RawGet(UILayer layer)
        {
            switch (layer)
            {
                case UILayer.Bot: return m_bot;
                case UILayer.Mid: return m_mid;
                case UILayer.Top: return m_top;
                case UILayer.System: return m_system;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(layer), layer, "[UILayers] 未知的层。");
            }
        }

        /// <summary>找出"哪个字段是空的"。</summary>
        /// <returns>问题描述；都填了则 null。</returns>
        private string DescribeMissing()
        {
            for (int i = 0; i < LayerCount; i++)
            {
                UILayer layer = (UILayer)i;

                if (RawGet(layer) == null)
                {
                    return "[UILayers] Canvas 预制体上的 UILayers 组件里，«" + GetLayerName(layer) +
                           "» 这一格是空的。\n" +
                           "请把 Canvas 下面的 " + GetLayerName(layer) + " 节点拖到 UILayers 的 " +
                           GetLayerName(layer) + " 字段里。\n" +
                           "（漏填的后果是：面板会被挂到场景根节点上，界面一片空白且不报错。）";
                }
            }

            return null;
        }

        /// <summary>找出"两个层指向同一个节点"。</summary>
        /// <returns>问题描述；不重复则 null。</returns>
        private string DescribeDuplicated()
        {
            for (int i = 0; i < LayerCount; i++)
            {
                for (int j = i + 1; j < LayerCount; j++)
                {
                    UILayer a = (UILayer)i;
                    UILayer b = (UILayer)j;

                    if (RawGet(a) == RawGet(b))
                    {
                        return "[UILayers] «" + GetLayerName(a) + "» 和 «" + GetLayerName(b) +
                               "» 指向了同一个节点（" + RawGet(a).name + "）。\n" +
                               "四个层必须是四个**不同**的节点，否则两层会互相遮挡、层级管理失效。";
                    }
                }
            }

            return null;
        }

        /// <summary>找出"拖了不属于这个 Canvas 的节点"。</summary>
        /// <returns>问题描述；都不是则 null。</returns>
        private string DescribeNotChild()
        {
            for (int i = 0; i < LayerCount; i++)
            {
                UILayer layer = (UILayer)i;
                Transform node = RawGet(layer);

                if (!node.IsChildOf(transform))
                {
                    return "[UILayers] «" + GetLayerName(layer) + "» 指向的节点（" + node.name +
                           "）不是这个 Canvas 的子节点。\n" +
                           "面板会被挂到别的 Canvas 下面，位置和层级都会错。";
                }
            }

            return null;
        }
    }
}
