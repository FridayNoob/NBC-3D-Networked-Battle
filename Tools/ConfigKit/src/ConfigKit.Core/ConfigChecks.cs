// ============================================================================
//  ConfigKit · 一次跑完所有检查（ConfigChecks）
//  对应：Docs\17-配置表规范.md
//
//  ---------------------------------------------------------------------------
//  为什么要有这个"总入口"
//  ---------------------------------------------------------------------------
//  检查现在分两类：
//      · **单表**：`ValidationEngine`（表头/类型/范围/外键/唯一性…）
//      · **跨表**：`CrossTableChecks`（任务要的东西副本/掉落给不给得出来）
//
//  它们的调用点有两个：**导出流水线**（`ExportPipeline.Run`）和**自测**
//  （`ConfigKit.SelfTest` 的 `Validate`）。如果两处各写一份"要跑哪些检查"，
//  迟早出现"流水线查了、自测没查"（或反过来）—— 那时**自测就是假绿**。
//
//  ⚠️ 这正是本项目的老规矩：**真实检查与工具的真实判定必须走同一个来源**
//     （写两遍匹配逻辑，迟早不一致）。所以顺序与清单只写在这里一处。
// ============================================================================

namespace NBC.ConfigKit
{
    /// <summary>把所有检查**按同一个顺序**跑一遍。</summary>
    public static class ConfigChecks
    {
        /// <summary>
        /// 跑全部检查（单表 → 跨表）。
        /// <para>顺序有讲究：**先单表、后跨表**。单表发现"类型错/列缺失"时，
        /// 跨表检查会拿到读不出来的值 ⇒ 报一堆连锁误报（本项目为"有结构错误时不再校验数据"专门立过规矩）。</para>
        /// </summary>
        /// <param name="set">表集合。</param>
        /// <param name="policy">项目策略。</param>
        /// <param name="diagnostics">诊断收集器。</param>
        public static void RunAll(ConfigSet set, ConfigPolicy policy, DiagnosticBag diagnostics)
        {
            if (set == null || diagnostics == null)
            {
                return;
            }

            ConfigPolicy actual = policy ?? ConfigPolicy.CreateDefault();

            // ① 单表（表头 / 类型 / 范围 / 外键 / 唯一性 …）
            new ValidationEngine(actual).Validate(set, diagnostics);

            // ② 跨表（任务 ↔ 副本 ↔ 掉落 ↔ 事件源）
            CrossTableChecks.Run(set, actual, diagnostics);
        }
    }
}
