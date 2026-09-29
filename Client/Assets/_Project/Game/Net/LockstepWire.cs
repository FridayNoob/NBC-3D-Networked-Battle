// ============================================================================
//  LockstepWire —— 锁步消息的**协议 ↔ 普通数据**转换（M4-S4 S4-c 收尾，方案 C′）
//  项目：3D联网战斗Demo
//
//  ---------------------------------------------------------------------------
//  一、⚠️ 为什么放 `Game\Net\`，而不是 `Shared\Sim\`（规则 W22 的教训）
//  ---------------------------------------------------------------------------
//  这两个函数吃的是 `LockstepStartEntity` / `LockstepFrameInput` —— **protobuf 类型**。
//  · `Shared\Sim\` 是**通配进 `NBC.Shared`** 的，而 **`NBC.Shared` 不引用 `NBC.Protocol`**
//    ⇒ 放那儿会**把整个仓库编不过**（第一版撞过：`CS0234 命名空间"NBC"中不存在"Protocol"`）。
//  · `Game\Net\`（`NBC.Game`）**同时**引用了 `NBC.Protocol` 与 `NBC.Shared` ✓，
//    而且**探针也通配编 `Game\Net\**`** ✓ ⇒ **两边都能用、又碰不到 `NBC.Shared`**。
//
//  这也顺带回答了一个更一般的判据（见 §33.5）：
//      **"能被自动化驱动" = 类型可见（谁编得到）+ 有公开的输入入口（怎么喂进去）**。
//      转换函数放这里 ⇒ 探针能**逐字段**验它（可见）；`NetSession` 的输入入口是
//      `ITransport` 这个**公开契约**（可喂）—— 两半都齐了。
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 两处"显式转换"是有意的（别让它变成"看起来能自动转"）
//  ---------------------------------------------------------------------------
//  · `entity_id`：wire 上是 **`int64`**，内存里 `SimEntity.Id` 是 **`int`**
//    ⇒ 这里写 `(int)` 并在注释里点明；将来 Id 真超范围时，应当在**这里**报错，
//      而不是让它**静默窄化**（两端会算出不同实体）。
//  · 位置：wire 上是三个 **`int64` raw**（`Fix64.RawValue`）⇒ 用 `Fix64.FromRaw` **显式**装回定点。
// ============================================================================

using System.Collections.Generic;
using NBC.Protocol;
using NBC.Shared;
using NBC.Shared.Sim;

namespace NBC.Game.Net
{
    /// <summary>锁步消息的协议 ↔ 普通数据转换（**只做转换，不做判断**）。</summary>
    public static class LockstepWire
    {
        /// <summary>把一个开局实体转成**普通数据**。</summary>
        /// <param name="raw">协议实体（不能为 null）。</param>
        /// <returns>普通实体。</returns>
        public static SimEntity ToPlainEntity(LockstepStartEntity raw)
        {
            SimEntity e;
            e.Id = (int)raw.EntityId;       // int64 → int：**显式**（见文件头二）
            e.Position = new FixVector3(
                Fix64.FromRaw(raw.PosXRaw),
                Fix64.FromRaw(raw.PosYRaw),
                Fix64.FromRaw(raw.PosZRaw));
            e.Hp = raw.Hp;
            e.MaxHp = raw.MaxHp;                        // ⚠️ 哈希读它，别漏
            e.AttackReadyTick = raw.AttackReadyTick;    // ⚠️ 哈希读它，别漏
            return e;
        }

        /// <summary>把一整条开局消息的实体表转成**普通数据**（顺序照原样，排序由运行器负责）。</summary>
        /// <param name="start">开局消息（不能为 null）。</param>
        /// <returns>普通实体表。</returns>
        public static List<SimEntity> ToPlainEntities(LockstepStart start)
        {
            var entities = new List<SimEntity>(start.Entities.Count);

            for (int i = 0; i < start.Entities.Count; i++)
            {
                entities.Add(ToPlainEntity(start.Entities[i]));
            }

            return entities;
        }

        /// <summary>把一条帧输入转成**普通数据**（`Missing` 必须带过来 —— 缺人要看得见）。</summary>
        /// <param name="raw">协议输入（不能为 null）。</param>
        /// <returns>普通输入。</returns>
        public static FrameInput ToPlainInput(LockstepFrameInput raw)
        {
            FrameInput f;
            f.PlayerId = raw.PlayerId;
            f.MoveDirection = new FixVector3(
                Fix64.FromRaw(raw.MoveXRaw),
                Fix64.FromRaw(raw.MoveYRaw),
                Fix64.Zero);                            // 本片是平面移动：Z 恒 0
            f.Buttons = raw.Buttons;
            f.TargetEntityId = raw.TargetEntityId;
            f.Missing = raw.Missing;
            return f;
        }

        /// <summary>把一整帧的输入转成**普通数据**。</summary>
        /// <param name="frame">帧消息（不能为 null）。</param>
        /// <returns>普通输入表。</returns>
        public static List<FrameInput> ToPlainInputs(LockstepFrame frame)
        {
            var inputs = new List<FrameInput>(frame.Inputs.Count);

            for (int i = 0; i < frame.Inputs.Count; i++)
            {
                inputs.Add(ToPlainInput(frame.Inputs[i]));
            }

            return inputs;
        }
    }
}
