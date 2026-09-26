// ============================================================================
//  ConfigKit · 项目策略（ConfigPolicy）—— 「通用化」的关键
//  对应：Docs\17-配置表规范.md §二/§三/§四/§六/§七
//
//  ---------------------------------------------------------------------------
//  为什么这些规则不能写死在 Core 里
//  ---------------------------------------------------------------------------
//  "主键必须叫 id"、"float 必须标 view"、"空着必须报错" ——
//  这些全是**本项目的约定**，不是"配置表"这件事的本质。
//  另一个项目完全可能允许浮点、用 `key` 当主键列名、允许空单元格当默认值。
//
//  所以它们是 `ConfigPolicy` 的字段（**数据**），不是 Core 里的一句 `if`。
//  判据：**如果一条规则"别的项目可能不想要"，它就必须在这里。**
//
//  ⚠️ 用 public 字段而不是属性：这是**配置数据**，不是行为契约；
//     而且这样将来从 JSON 反序列化时最省事（System.Text.Json 直接认字段/属性都行，
//     但字段没有 getter/setter 的仪式感，配置类用它更直白）。
// ============================================================================

using System;
using System.Collections.Generic;

namespace NBC.ConfigKit
{
    /// <summary>浮点数的处置策略。</summary>
    public enum FloatPolicy
    {
        /// <summary>随便用。</summary>
        Allowed = 0,

        /// <summary>**可以用，但必须标 `view`**（本项目的选择：只允许出现在表现层）。</summary>
        MustBeMarkedView = 1,

        /// <summary>**一律不许用**（更严的项目）。</summary>
        Forbidden = 2
    }

    /// <summary>
    /// 一次导出所用的**项目策略**。默认值 = `Docs\17` 里冻结的那一套。
    /// </summary>
    public sealed class ConfigPolicy
    {
        // ====================================================================
        //  命名
        // ====================================================================

        /// <summary>表名规范：大驼峰、单数、只允许字母数字。</summary>
        public string TableNamePattern = "^[A-Z][A-Za-z0-9]*$";

        /// <summary>字段名规范：小驼峰英文。</summary>
        public string FieldNamePattern = "^[a-z][A-Za-z0-9]*$";

        /// <summary>主键列的名字（**必须叫这个**）。</summary>
        public string KeyColumnName = "id";

        /// <summary>主键的最小值（0 保留给"忘了赋值"，所以默认不许是 0）。</summary>
        public long MinKeyValue = 1L;

        // ====================================================================
        //  表头结构（五行）
        // ====================================================================

        /// <summary>字段名所在行（Excel 真实行号）。</summary>
        public int HeaderFieldRow = 1;

        /// <summary>中文注释所在行。</summary>
        public int HeaderCommentRow = 2;

        /// <summary>类型所在行。</summary>
        public int HeaderTypeRow = 3;

        /// <summary>校验规则所在行。</summary>
        public int HeaderRuleRow = 4;

        /// <summary>数据起始行（Excel 真实行号）。</summary>
        public int FirstDataRow = 5;

        /// <summary>整行忽略 / 整列忽略的前缀（`#`）。</summary>
        public string IgnorePrefix = "#";

        // ====================================================================
        //  空值策略
        // ====================================================================

        /// <summary>
        /// 这些写法**看着像"没有"，其实是字符串**，所以一律报错（`Docs\17` §六 规则 4）。
        /// <para>要表示"无"，**唯一的写法是留空**。</para>
        /// </summary>
        public string[] RejectedEmptyPlaceholders =
        {
            "-", "--", "null", "NULL", "Null", "无", "N/A", "n/a", "NA"
        };

        /// <summary>可空的标记后缀（`int?` / `ref:Hero?` / `string?`）。</summary>
        public string NullableSuffix = "?";

        // ====================================================================
        //  浮点
        // ====================================================================

        /// <summary>浮点策略（默认：只允许表现层，且必须标 `view`）。</summary>
        public FloatPolicy Float = FloatPolicy.MustBeMarkedView;

        /// <summary>`view` 标记的名字（写在规则行里）。</summary>
        public string ViewFlag = "view";

        // ====================================================================
        //  枚举成员（可选）
        // ====================================================================

        /// <summary>
        /// 已知枚举的成员名（枚举名 → 成员集合）。
        /// <para>
        /// ⚠️ **留空是允许的**：枚举定义在 C# 里（`Docs\17` §二），
        /// 工具在没被告知成员时**只检查"非空 + 像标识符"**，不检查成员是否存在。
        /// 这是**如实记录的局限**，不是遗漏 —— 想严格校验就把成员表填进来
        /// （或者将来由工具去扫 `enum` 的 C# 源码）。
        /// </para>
        /// </summary>
        public IDictionary<string, ISet<string>> KnownEnumMembers =
            new Dictionary<string, ISet<string>>(StringComparer.Ordinal);

        // ====================================================================
        //  跨表检查：当前**没有事件源**的事件类型（可选）
        // ====================================================================

        /// <summary>
        /// 这些事件类型**当前实现里没有事件源** —— 数据里一旦用到它们，对应的任务条件就是**死的**。
        /// <para>⚠️ 它是**实现状态**（"这个系统还没做"），不是数据关系，所以按 `ConfigPolicy` 的判据放在这里：
        /// 别的项目可能已经做了区域系统；本项目做完之后**把这一项删掉**即可，不用改 `CrossTableChecks` 一行。</para>
        /// <para>现在列的是 `ReachArea`：M3 的联机路径只发伤害/死亡/掉落三种事件
        /// （`AreaEntered` 只有 M2 的本地战斗会触发）——详见 `Docs\27` §八。</para>
        /// </summary>
        public string[] EventTypesWithoutSource = { "ReachArea" };

        // ====================================================================
        //  便捷构造
        // ====================================================================

        /// <summary>造一份"按 `Docs\17` 默认"的策略（**每次给新实例**，避免到处共享被改坏）。</summary>
        /// <returns>策略。</returns>
        public static ConfigPolicy CreateDefault()
        {
            return new ConfigPolicy();
        }
    }
}
