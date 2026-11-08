// 这个文件里有：
//   BTPreviewKind（枚举）：预览求值器的配方种类（决定这个节点在预览里怎么算状态）。
//   BTCompareOp（枚举）：整数/浮点比较的运算符。
//   BTParamEntry（类）：SO 里的一个参数定义（可序列化）。
//   BTNodeEntry（类）：SO 里的一个节点定义（类型名、分类、子节点上限、预览配方、参数表）。
//   BTNodeDefinitionAsset（ScriptableObject）：整份节点定义集合 —— 节点定义以它为准。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NBC.EditorTools.BehaviorTree
{
    // 预览求值器配方：同一类节点共用一套求值器，避免"每个节点写一个类/一个 switch"
    public enum BTPreviewKind
    {
        // 没配配方 → 预览显示灰色（未实现）
        None = 0,

        // 恒成功（如 Patrol / Idle / BasicAttack）
        AlwaysSuccess = 1,

        // 恒运行中（如 Chase / ReturnHome）
        AlwaysRunning = 2,

        // 读黑板整数键，与常量或参数比较（如 IsDead / HPBelow / HasTarget）
        IntCompare = 3,

        // 读黑板浮点键，与常量或参数比较（如 AttackReady）
        FloatCompare = 4,

        // 读黑板布尔键（如 InAttackRange / HomeFar）
        Flag = 5,

        // 自己的计时器（如 Wait）
        Timed = 6,

        // 组合节点的三种遍历方式
        CompositeSelector = 7,

        // 顺序：遇到 Failure 就失败
        CompositeSequence = 8,

        // 并行：全成功才成功
        CompositeParallel = 9
    }

    // 比较运算符
    public enum BTCompareOp
    {
        // 小于
        Less = 0,

        // 小于等于
        LessOrEqual = 1,

        // 大于
        Greater = 2,

        // 大于等于
        GreaterOrEqual = 3,

        // 等于
        Equal = 4,

        // 不等于
        NotEqual = 5
    }

    // 一个参数定义（SO 里可编辑）
    [Serializable]
    public sealed class BTParamEntry
    {
        // 参数键（JSON 里 Params 的键）
        public string Key = string.Empty;

        // 属性面板显示名
        public string Label = string.Empty;

        // 参数类型（决定控件与 JSON 原生类型）
        public BTParamType Type = BTParamType.Int;

        // 默认值（字符串形式，按类型解析）
        public string Default = "0";
    }

    // 一个节点定义（SO 里可编辑）
    [Serializable]
    public sealed class BTNodeEntry
    {
        // 节点类型名（JSON 的 NodeType；发布后不许改名）
        public string TypeName = string.Empty;

        // 左栏与节点标题显示名
        public string DisplayName = string.Empty;

        // 分类（左栏分组、节点配色）
        public BTNodeCategory Category = BTNodeCategory.Action;

        // 子节点上限：-1 = 不限；0 = 叶子
        public int MaxChildren;

        // 预览配方
        public BTPreviewKind PreviewKind = BTPreviewKind.None;

        // 预览时读的黑板键（如 hp / targetId / skillCd / inRange / homeFar）
        public string PreviewKey = string.Empty;

        // 比较运算符（IntCompare / FloatCompare 用）
        public BTCompareOp CompareOp = BTCompareOp.Less;

        // 阈值参数键（非空则从节点参数取阈值；空则用 CompareConstInt / CompareConstFloat）
        public string CompareParamKey = string.Empty;

        // 整数比较常量（CompareParamKey 为空时用）
        public int CompareConstInt;

        // 浮点比较常量（CompareParamKey 为空时用）
        public float CompareConstFloat;

        // 预览成功时写回黑板的键（如 CastSkill 写 skillCd）；空 = 不写
        public string WriteBackKey = string.Empty;

        // 写回值：从参数取（优先）
        public string WriteBackParamKey = string.Empty;

        // 写回值：常量（WriteBackParamKey 为空时用）
        public float WriteBackConstFloat;

        // 参数表
        public List<BTParamEntry> Params = new List<BTParamEntry>();
    }

    // 主要功能：整份节点定义集合 —— 编辑器左栏、属性面板、预览配方都从这里读；策划可在此增改。
    // 主要数据成员：Nodes（节点定义列表）。
    // 主要外部方法：无（纯数据；加载与校验在 BTNodeRegistry）。
    // 谁在用它：BTNodeRegistry 启动加载；策划在 Inspector 里维护。
    // 注意：TypeName 一经发布不可改名（会破坏已导出的 JSON）；改定义不需要改代码。
    [CreateAssetMenu(fileName = "BTNodeDefinitions", menuName = "AI工具/行为树节点定义")]
    public sealed class BTNodeDefinitionAsset : ScriptableObject
    {
        // 全部节点定义（顺序即左栏显示顺序）
        public List<BTNodeEntry> Nodes = new List<BTNodeEntry>();
    }
}
