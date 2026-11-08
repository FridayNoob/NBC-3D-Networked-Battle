// 这个文件里有：
//   六个通用预览求值器：IntCompare / FloatCompare / Flag / AlwaysSuccess / AlwaysRunning / Timed。
//
// 【仅编辑器】这些求值器只服务"预览"，不代表服务端行为；同一份求值器被多个节点类型复用。

using System;
using System.Globalization;
using UnityEngine;

namespace NBC.EditorTools.BehaviorTree
{
    // 求值器公共小工具：比较运算符与参数读取
    internal static class BtPreviewMath
    {
        // 功能：按运算符比较两个数。
        // 入参：left 左值；right 右值；op 运算符
        // 返回：true = 成立
        // 步骤：switch 六个运算符
        // 上游：IntCompare / FloatCompare
        // 下游：无
        public static bool Compare(double left, double right, BTCompareOp op)
        {
            switch (op)
            {
                case BTCompareOp.Less:
                    return left < right;

                case BTCompareOp.LessOrEqual:
                    return left <= right;

                case BTCompareOp.Greater:
                    return left > right;

                case BTCompareOp.GreaterOrEqual:
                    return left >= right;

                case BTCompareOp.Equal:
                    return Math.Abs(left - right) < 0.000001d;

                default:
                    return Math.Abs(left - right) >= 0.000001d;
            }
        }

        // 功能：从节点参数里读一个数（缺省返回 fallback）。
        // 入参：ctx 上下文；key 参数键；fallback 缺省值
        // 返回：数值
        // 步骤：键为空或参数不存在 → fallback；否则 AsFloat
        // 上游：IntCompare / FloatCompare / Timed
        // 下游：无
        public static double ReadParam(BTPreviewContext ctx, string key, double fallback)
        {
            if (string.IsNullOrEmpty(key) || ctx.Data == null)
            {
                return fallback;
            }

            return ctx.Data.Params.TryGetValue(key, out BTParamValue value) ? value.AsFloat() : fallback;
        }
    }

    // 整数比较求值器（IsDead / HPBelow / HasTarget 共用）
    [BtPreviewKind(BTPreviewKind.IntCompare)]
    internal sealed class IntComparePreview : IBtPreviewNode
    {
        // 功能：读黑板整数键，与参数或常量比较。
        // 入参：ctx 上下文
        // 返回：成立 → Success；否则 Failure
        // 步骤：左值取黑板（按 PreviewKey）→ 右值取参数或常量 → 比较
        // 上游：BTPreviewNodeInstance.Tick
        // 下游：BtPreviewMath.Compare
        public BTPreviewStatus Tick(BTPreviewContext ctx)
        {
            int left = ctx.Blackboard.GetInt(ctx.Entry.PreviewKey);
            double right = string.IsNullOrEmpty(ctx.Entry.CompareParamKey)
                ? ctx.Entry.CompareConstInt
                : BtPreviewMath.ReadParam(ctx, ctx.Entry.CompareParamKey, ctx.Entry.CompareConstInt);

            bool ok = BtPreviewMath.Compare(left, right, ctx.Entry.CompareOp);
            ctx.Detail = ctx.Entry.PreviewKey + "=" + left + " " + ctx.Entry.CompareOp + " " + right + " → " + (ok ? "成立" : "不成立");
            return ok ? BTPreviewStatus.Success : BTPreviewStatus.Failure;
        }

        // 功能：无需重置（无状态）。
        // 入参：无
        // 返回：无
        // 步骤：空实现
        // 上游：BTPreviewRunner.Start
        // 下游：无
        public void Reset()
        {
        }
    }

    // 浮点比较求值器（AttackReady 等）
    [BtPreviewKind(BTPreviewKind.FloatCompare)]
    internal sealed class FloatComparePreview : IBtPreviewNode
    {
        // 功能：读黑板浮点键，与参数或常量比较。
        // 入参：ctx 上下文
        // 返回：成立 → Success；否则 Failure
        // 步骤：左值取黑板（按 PreviewKey）→ 右值取参数或常量 → 比较
        // 上游：BTPreviewNodeInstance.Tick
        // 下游：BtPreviewMath.Compare
        public BTPreviewStatus Tick(BTPreviewContext ctx)
        {
            double left = ctx.Blackboard.GetFloat(ctx.Entry.PreviewKey);
            double right = string.IsNullOrEmpty(ctx.Entry.CompareParamKey)
                ? ctx.Entry.CompareConstFloat
                : BtPreviewMath.ReadParam(ctx, ctx.Entry.CompareParamKey, ctx.Entry.CompareConstFloat);

            bool ok = BtPreviewMath.Compare(left, right, ctx.Entry.CompareOp);
            ctx.Detail = ctx.Entry.PreviewKey + "=" + left.ToString("0.###", CultureInfo.InvariantCulture) +
                         " " + ctx.Entry.CompareOp + " " + right + " → " + (ok ? "成立" : "不成立");
            return ok ? BTPreviewStatus.Success : BTPreviewStatus.Failure;
        }

        // 功能：无需重置（无状态）。
        // 入参：无
        // 返回：无
        // 步骤：空实现
        // 上游：BTPreviewRunner.Start
        // 下游：无
        public void Reset()
        {
        }
    }

    // 布尔开关求值器（InAttackRange / HomeFar）
    [BtPreviewKind(BTPreviewKind.Flag)]
    internal sealed class FlagPreview : IBtPreviewNode
    {
        // 功能：读黑板布尔键。
        // 入参：ctx 上下文
        // 返回：true → Success；false → Failure
        // 步骤：黑板没有该键时按 false 处理，并在 Detail 里说明
        // 上游：BTPreviewNodeInstance.Tick
        // 下游：BTBlackboard.TryGet
        public BTPreviewStatus Tick(BTPreviewContext ctx)
        {
            bool value = ctx.Blackboard.TryGet(ctx.Entry.PreviewKey, out BTParamValue raw) && raw.AsBool();
            ctx.Detail = ctx.Entry.PreviewKey + " = " + value;
            return value ? BTPreviewStatus.Success : BTPreviewStatus.Failure;
        }

        // 功能：无需重置（无状态）。
        // 入参：无
        // 返回：无
        // 步骤：空实现
        // 上游：BTPreviewRunner.Start
        // 下游：无
        public void Reset()
        {
        }
    }

    // 恒成功求值器（Patrol / Idle / BasicAttack / CastSkill）
    [BtPreviewKind(BTPreviewKind.AlwaysSuccess)]
    internal sealed class AlwaysSuccessPreview : IBtPreviewNode
    {
        // 功能：恒返回成功。
        // 入参：ctx 上下文
        // 返回：Success
        // 步骤：直接返回
        // 上游：BTPreviewNodeInstance.Tick
        // 下游：无
        public BTPreviewStatus Tick(BTPreviewContext ctx)
        {
            return BTPreviewStatus.Success;
        }

        // 功能：无需重置（无状态）。
        // 入参：无
        // 返回：无
        // 步骤：空实现
        // 上游：BTPreviewRunner.Start
        // 下游：无
        public void Reset()
        {
        }
    }

    // 恒运行中求值器（Chase / ReturnHome）
    [BtPreviewKind(BTPreviewKind.AlwaysRunning)]
    internal sealed class AlwaysRunningPreview : IBtPreviewNode
    {
        // 功能：恒返回运行中（表示"这一帧没结束"）。
        // 入参：ctx 上下文
        // 返回：Running
        // 步骤：直接返回
        // 上游：BTPreviewNodeInstance.Tick
        // 下游：无
        public BTPreviewStatus Tick(BTPreviewContext ctx)
        {
            return BTPreviewStatus.Running;
        }

        // 功能：无需重置（无状态）。
        // 入参：无
        // 返回：无
        // 步骤：空实现
        // 上游：BTPreviewRunner.Start
        // 下游：无
        public void Reset()
        {
        }
    }

    // 计时求值器（Wait）
    [BtPreviewKind(BTPreviewKind.Timed)]
    internal sealed class TimedPreview : IBtPreviewNode
    {
        // 已经过的秒数
        private float m_elapsed;

        // 功能：累加计时，到点成功。
        // 入参：ctx 上下文
        // 返回：未到点 → Running；到点 → Success（并清零，便于反复演示）
        // 步骤：读参数 seconds（缺省 1）→ 累加 DeltaSeconds → 比较
        // 上游：BTPreviewNodeInstance.Tick
        // 下游：BtPreviewMath.ReadParam
        public BTPreviewStatus Tick(BTPreviewContext ctx)
        {
            float need = (float)BtPreviewMath.ReadParam(ctx, "seconds", 1d);
            m_elapsed += ctx.DeltaSeconds;
            ctx.Detail = m_elapsed.ToString("0.0") + " / " + need.ToString("0.0") + " 秒";

            if (m_elapsed >= need)
            {
                m_elapsed = 0f;
                return BTPreviewStatus.Success;
            }

            return BTPreviewStatus.Running;
        }

        // 功能：清零计时。
        // 入参：无
        // 返回：无
        // 步骤：m_elapsed = 0
        // 上游：BTPreviewRunner.Start
        // 下游：无
        public void Reset()
        {
            m_elapsed = 0f;
        }
    }
}
