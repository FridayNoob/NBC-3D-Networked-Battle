// 这个文件里有：
//   LockstepWire（静态类）：把锁步协议的实体与帧输入搬成 Shared.Sim 的纯数据（只转换，不判空、不校验范围）。

using System.Collections.Generic;
using NBC.Protocol;
using NBC.Shared;
using NBC.Shared.Sim;

namespace NBC.Game.Net
{
    // 主要功能：把协议里的开局实体与帧输入搬成 Shared.Sim 的纯数据，供锁步运行器使用。
    // 主要数据成员：无（静态类，不持有状态）。
    // 主要外部方法：ToPlainEntities / ToPlainInputs（批量入口）；ToPlainEntity / ToPlainInput（单个，批量与探针都直接用）。
    // 谁在用它：NetSession 收到锁步开局（HandleLockstepStart）与帧消息（HandleLockstepFrame）时调批量的两个；探针（Server\_net-probe 的 NetSession_LockstepWireUp）直接编本文件，四个方法都逐字段验。
    // 注意：本类必须留在 Game\Net —— NBC.Game 同时引用 NBC.Protocol 与 NBC.Shared；搬进 Shared\Sim 会让 NBC.Shared 编不过，因为 NBC.Shared 不引用 NBC.Protocol（CS0234）。
    public static class LockstepWire
    {
        // 功能：把一个开局实体从协议字段搬成 SimEntity，顺带完成编号与定点两处显式转换。
        // 入参：raw 协议实体（调用方保证非 null，本方法不判空）
        // 步骤：
        //   1) entity_id 是 int64、SimEntity.Id 是 int，这里显式窄化成 int
        //   2) 位置三个分量是 Q32.32 的 int64 raw，用 Fix64.FromRaw 装回定点（1 米 = 2^32 raw，位置单位是米）
        //   3) 血量、最大血、可攻击 tick 原样搬
        //   注意：将来 Id 真超 int 范围必须在这里报错，不能让它静默窄化，否则两端会算出不同实体
        //   注意：MaxHp 与 AttackReadyTick 都进世界哈希，漏搬不报错、只表现为两端慢慢对不上
        // 返回：转换后的普通实体，字段与协议一一对应
        // 上游：ToPlainEntities（本类）、探针 NetSession_LockstepWireUp
        // 下游：无
        public static SimEntity ToPlainEntity(LockstepStartEntity raw)
        {
            SimEntity e;
            e.Id = (int)raw.EntityId;
            e.Position = new FixVector3(
                Fix64.FromRaw(raw.PosXRaw),
                Fix64.FromRaw(raw.PosYRaw),
                Fix64.FromRaw(raw.PosZRaw));
            e.Hp = raw.Hp;
            e.MaxHp = raw.MaxHp;
            e.AttackReadyTick = raw.AttackReadyTick;
            return e;
        }

        // 功能：把整条开局消息里的实体表逐条转成普通实体。
        // 入参：start 开局消息（调用方保证非 null，本方法不判空）
        // 步骤：
        //   1) 按实体个数预分配列表，省掉扩容（每局一次，数量就是参战实体数）
        //   2) 按下标顺序逐条转，保持协议里的顺序
        //   注意：这里刻意不排序，排序由运行器负责；两边都排会把「顺序到底由谁定」藏起来
        // 返回：新列表，顺序与协议一致、长度等于实体数；没有实体时是空列表而不是 null
        // 上游：NetSession.HandleLockstepStart、探针
        // 下游：ToPlainEntity
        public static List<SimEntity> ToPlainEntities(LockstepStart start)
        {
            var entities = new List<SimEntity>(start.Entities.Count);

            for (int i = 0; i < start.Entities.Count; i++)
            {
                entities.Add(ToPlainEntity(start.Entities[i]));
            }

            return entities;
        }

        // 功能：把一条帧输入从协议字段搬成 FrameInput。
        // 入参：raw 协议输入（调用方保证非 null，本方法不判空）
        // 步骤：
        //   1) 玩家编号、按键位掩码、目标实体编号原样搬
        //   2) 移动方向的 X / Y 由 int64 raw 装回定点，Z 固定为 0（本片只有平面移动）
        //   3) Missing 原样带过来
        //   注意：Missing 必须带过来 —— 运行器靠它分辨这一帧谁没发，而不是把默认值当成正常输入（缺人要看得见）
        // 返回：转换后的普通输入，字段与协议一一对应
        // 上游：ToPlainInputs（本类）、探针 NetSession_LockstepWireUp
        // 下游：无
        public static FrameInput ToPlainInput(LockstepFrameInput raw)
        {
            FrameInput f;
            f.PlayerId = raw.PlayerId;
            f.MoveDirection = new FixVector3(
                Fix64.FromRaw(raw.MoveXRaw),
                Fix64.FromRaw(raw.MoveYRaw),
                Fix64.Zero);
            f.Buttons = raw.Buttons;
            f.TargetEntityId = raw.TargetEntityId;
            f.Missing = raw.Missing;
            return f;
        }

        // 功能：把一整帧的输入表逐条转成普通输入。
        // 入参：frame 帧消息（调用方保证非 null，本方法不判空）
        // 步骤：
        //   1) 按输入条数预分配列表
        //   2) 按下标顺序逐条转，保持协议顺序（本方法不按玩家编号重排）
        // 返回：新列表，顺序与协议一致、长度等于输入条数；没有人输入时是空列表而不是 null
        // 上游：NetSession.HandleLockstepFrame、探针
        // 下游：ToPlainInput
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
