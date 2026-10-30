// 这个文件里有：
//   UILayers（类）：持有 Canvas 预制体上四个 UI 层节点的引用，配置有错时给出缺哪个 / 重了哪个 / 拖错哪个的人话说明。

using System;
using UnityEngine;

namespace NBC.Framework.UI
{
    // 主要功能：把 Bot / Mid / Top / System 四个层节点从代码搬到序列化字段，按层取父节点，并在配置有错时报出具体错在哪。
    // 主要数据成员：m_bot / m_mid / m_top / m_system（四个层的 Transform，Inspector 里拖）、LayerCount（固定 4）。
    // 主要外部方法：Get（取不到就抛）、TryGet（配置问题返回 false）、DescribeProblem / IsValid（检查）、Bind（测试手工指定）、GetLayerName（报错用名字）。
    // 谁在用它：UIManager 加载 Canvas 时检查并保存，打开面板时按层取父节点；EditMode 测试用 Bind 搭临时 Canvas。
    // 注意：原版用 Transform.Find 按名字找层，改名后 Find 返回 null 而不报错，面板被 SetParent(null)
    //   挂到场景根节点，界面一片空白且 Console 干净 —— 静默失败比崩溃难查；所以层改成序列化字段，
    //   缺了当场抛异常并说清缺哪个。
    // 注意：不要改成按顺序拖的数组 —— 顺序本身就是含义，拖反了不会报错，只是把静默错位从代码挪到
    //   Inspector；具名字段拖错了能一眼看出来。
    /// <summary>四个 UI 层节点的引用，挂在 Canvas 预制体的根节点上；配置有问题时报出缺哪个 / 重了哪个 / 拖错哪个，不静默返回 null。</summary>
    [DisallowMultipleComponent]
    public sealed class UILayers : MonoBehaviour
    {
        /// <summary>层数量，固定 4，必须与 UILayer 的取值个数一致（加层要两处一起改）。</summary>
        public const int LayerCount = 4;

        // 最底层节点（背景、主界面 HUD）；为空表示没配。
        [SerializeField]
        private Transform m_bot;

        // 中间层节点（普通面板的默认层）；为空表示没配。
        [SerializeField]
        private Transform m_mid;

        // 上层节点（盖住普通面板的详情、确认框）；为空表示没配。
        [SerializeField]
        private Transform m_top;

        // 最顶层节点（加载遮罩、系统弹窗）；为空表示没配。
        [SerializeField]
        private Transform m_system;

        /// <summary>把层枚举翻成日志与报错用的名字（Bot / Mid / Top / System）。</summary>
        /// <param name="layer">层；必须是 UILayer 的四个合法值之一。</param>
        /// <returns>该层的名字，与枚举成员同名。</returns>
        // 功能：把层枚举翻成四个固定名字，供日志与报错拼人话。
        // 入参：layer 层，只接受四个合法值
        // 返回：层名（Bot / Mid / Top / System）
        // 步骤：
        //   1) 四个合法值各给一个固定名字，与枚举成员同名，日志里才对得上
        //   2) 其余值当场抛异常，不做兜底字符串
        // 上游：Get 与 DescribeProblem 的报错拼装，以及测试断言
        // 下游：无
        // 注意：不能加 default 返回 Unknown(99) 这种兜底 —— 野值只可能来自写错的下标，兜底会把假名字
        //   一路带进日志；第一版就是因为只有这里不抛、三处断言里红了。
        public static string GetLayerName(UILayer layer)
        {
            switch (layer)
            {
                case UILayer.Bot: return "Bot";
                case UILayer.Mid: return "Mid";
                case UILayer.Top: return "Top";
                case UILayer.System: return "System";

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(layer), layer,
                        "[UILayers] 未知的层。只可能是 " +
                        "Bot(0) / Mid(1) / Top(2) / System(3) —— " +
                        "传进来一个野值说明某处算错了下标。");
            }
        }

        /// <summary>取某一层的节点；配置有问题时抛异常并说明缺什么，不返回 null。</summary>
        /// <param name="layer">层。</param>
        /// <returns>该层的 Transform，保证非空且是本 Canvas 的子节点。</returns>
        // 功能：取某一层的节点，取不到就带着问题描述抛异常。
        // 入参：layer 层
        // 返回：该层节点，保证非空且是当前 Canvas 的子节点
        // 步骤：
        //   1) 先用 TryGet 做完整校验，把空字段与拖错节点都挡在这里
        //   2) 校验不过就抛 InvalidOperationException，并带上 DescribeProblem 的人话
        // 上游：UIManager 打开面板时按层取父节点
        // 下游：TryGet、GetLayerName、DescribeProblem
        // 注意：不能改成配置错就返回 null —— SetParent(null) 会把面板挂到场景根节点，界面空白却不报错。
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

        /// <summary>试着取某一层的节点；层合法但没配好时返回 false，层本身非法时抛异常。</summary>
        /// <param name="layer">层。</param>
        /// <param name="layerRoot">取到的节点；返回 false 时为 null。</param>
        /// <returns>true 表示节点存在且是本 Canvas 的子节点；false 表示层合法但配置有问题。</returns>
        // 功能：取层节点但不抛配置异常，把「没配好」与「层非法」分开。
        // 入参：layer 层；layerRoot 取到的节点
        // 返回：true 取到且是本 Canvas 的子节点；false 层合法但没配好（空格子或拖了别人的节点）
        // 步骤：
        //   1) 先 RawGet 拿字段，空就直接 false，这是配置问题，由调用方决定怎么报
        //   2) 再查 IsChildOf：拖到别的 Canvas 下面与没填一样会让面板错位，一并挡掉
        //   3) 两步都过才返回 true
        // 上游：Get，以及测试断言「配置错不抛异常」
        // 下游：RawGet
        // 注意：层本身非法（如 (UILayer)99）由 RawGet 抛 ArgumentOutOfRangeException，不能改成静默 false ——
        //   那是编程错误，沉默只会让 bug 藏得更深。
        public bool TryGet(UILayer layer, out Transform layerRoot)
        {
            layerRoot = RawGet(layer);

            if (layerRoot == null)
            {
                return false;
            }

            if (!layerRoot.IsChildOf(transform))
            {
                return false;
            }

            return true;
        }

        /// <summary>检查四个层的配置，返回一句人话说明第一处问题；配置正确时返回 null。</summary>
        /// <returns>问题描述；正常时 null。</returns>
        // 功能：按「先缺、再重、最后不是子节点」的顺序检查配置，返回第一处问题的人话。
        // 入参：无
        // 返回：能直接给人看的问题描述；四层都合法时 null
        // 步骤：
        //   1) 先查空字段，漏填最常见也最容易修
        //   2) 再查重复，同一节点挂两个层会让两层互相遮挡
        //   3) 最后查子节点归属，拖到别的 Canvas 下会让位置与层级都错
        // 上游：UIManager 加载 Canvas 时检查并抛异常，IsValid 也走这里
        // 下游：DescribeMissing、DescribeDuplicated、DescribeNotChild
        // 注意：顺序不能换 —— 后两步都假定字段非空（DescribeNotChild 直接取 node.IsChildOf），换了会空引用。
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

        /// <summary>四个层是否都配好且都指向本 Canvas 的子节点。</summary>
        // 功能：把 DescribeProblem 的结果压成一个 bool，供测试或排查一眼判断。
        // 入参：无
        // 返回：true 配置完全正确；false 至少有一处问题
        // 步骤：
        //   1) 调 DescribeProblem，null 即有效
        //   2) 不缓存结果，Inspector 里的配置随时会变
        // 上游：测试与排查脚本
        // 下游：DescribeProblem
        public bool IsValid
        {
            get { return DescribeProblem() == null; }
        }

        /// <summary>手动指定某一层的节点；正常用法是在 Inspector 里拖，这里给测试与运行时动态搭 Canvas 用。</summary>
        /// <param name="layer">层。</param>
        /// <param name="layerRoot">节点；传 null 表示清空。</param>
        // 功能：按层改对应字段，供测试手工搭 UILayers 或运行时代码动态拼 Canvas。
        // 入参：layer 层；layerRoot 节点，null 表示清空
        // 返回：无
        // 步骤：按枚举分派到四个字段，未知层抛异常，与 GetLayerName 的口径一致
        // 上游：EditMode 测试手工搭 Canvas
        // 下游：无
        // 注意：Bind 不做子节点与重复校验，配错要等 DescribeProblem / Get 才发现；正常项目应在 Inspector 里拖。
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

        // 功能：按枚举直接读对应字段，不做任何校验。
        // 入参：layer 层
        // 返回：对应字段的 Transform；没填时 null
        // 步骤：按枚举分派；未知层抛异常，防止野值一路往下传
        // 上游：TryGet、DescribeMissing、DescribeDuplicated、DescribeNotChild
        // 下游：无
        // 注意：这是内部取值，子节点与重复校验由调用方负责，不要拿它当对外接口。
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

        // 功能：找出第一个空字段，拼一句带修复步骤的报错。
        // 入参：无
        // 返回：问题描述；四个字段都填了则 null
        // 步骤：按 0..LayerCount-1 遍历四层，遇到第一个 null 就返回，报错里写清缺哪层、该怎么拖
        // 上游：DescribeProblem
        // 下游：RawGet、GetLayerName
        // 注意：报错要带上「漏填的后果」，否则接手的人只看到一句「这一格是空的」，还得自己猜。
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

        // 功能：找出第一对指向同一个节点的层。
        // 入参：无
        // 返回：问题描述；四层互不相同则 null
        // 步骤：只比对 i 小于 j 的组合，避免同一对报两次；命中就报出两层名字与节点名
        // 上游：DescribeProblem
        // 下游：RawGet、GetLayerName
        // 注意：四个层必须是四个不同节点，否则两层互相遮挡、层级管理失效。
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

        // 功能：找出第一个不属于当前 Canvas 的层节点。
        // 入参：无
        // 返回：问题描述；四层都是本 Canvas 的子节点则 null
        // 步骤：遍历四层，对每个节点查 IsChildOf(transform)，第一个不满足就返回
        // 上游：DescribeProblem（只在前两步都通过后调用）
        // 下游：RawGet、GetLayerName
        // 注意：这里直接用 node.IsChildOf，前提是字段都已非空 —— 所以 DescribeProblem 必须先查空再调它。
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
