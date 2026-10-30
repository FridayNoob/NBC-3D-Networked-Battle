// 这个文件里有：
//   LockstepSnapshotAdapter（类）：把只读锁步世界转写成表现层共用的 WorldSnapshot，复用缓冲并要求帧号单调。

using System.Collections.Generic;
using NBC.Protocol;
using NBC.Shared;
using NBC.Shared.Sim;

namespace NBC.Game.Net
{
    // 主要功能：只读地把锁步 WorldState 转写成 WorldSnapshot（不写回、不调 Step），让锁步与状态同步两种模式共用一套 SnapshotView 渲染。
    // 主要数据成员：m_pool（EntitySnapshot 复用池，个）、m_sorted（按 Id 升序的实体表复用缓冲，个）、m_lastFrame（上一次帧号，初值 -1）、m_nonMonotonic（被挡下的次数，单位次，正常 0）。
    // 主要外部方法：ApplyTo、NonMonotonicBlocked、LastFrame。
    // 谁在用它：Game\Net 的 PresentationFeed.Refresh 每帧调用，Server 下的 _net-probe 探针也直接实例化做验收；只能放 NBC.Game（吃 protobuf 类型，而 NBC.Shared 不引用 NBC.Protocol，放过去会 CS0234 编不过）。
    public sealed class LockstepSnapshotAdapter
    {
        // EntitySnapshot 复用池；条数等于历史最大实体数（个），稳态不再增长。
        private readonly List<EntitySnapshot> m_pool = new List<EntitySnapshot>();

        // 按 Id 升序的实体表复用缓冲；条数等于当前世界实体数（个），由 WorldState.CopySortedTo 写入，避免每帧 new SimEntity[]。
        private readonly List<SimEntity> m_sorted = new List<SimEntity>();

        // 预留的默认目标快照（WorldSnapshot）；当前 ApplyTo 只写调用方传入的 target，本字段未被读取。
        private readonly WorldSnapshot m_target = new WorldSnapshot();

        // 上一次成功喂出的帧号（单位锁步帧）；初值 -1 表示还没喂过，之后非负。
        private int m_lastFrame = -1;

        // 因帧号不递增被挡下的累计次数（单位次）；正常恒为 0，非 0 说明调用方重发或乱序。
        private long m_nonMonotonic;

        // 功能：读被挡下的累计次数（正常应为 0）。
        public long NonMonotonicBlocked
        {
            get { return m_nonMonotonic; }
        }

        // 功能：读上一次成功喂出的帧号（-1 表示还没喂过）。
        public int LastFrame
        {
            get { return m_lastFrame; }
        }

        // 功能：把锁步世界重填进 target（实体逐项覆盖、对象复用），成功返回 true。
        // 入参：world 只读的锁步世界（null 表示只清空实体并记帧号）；frame 锁步帧号（必须严格大于上一次，否则被挡）；ownerPlayerId 是「我」的玩家编号（0 表示无主，只有实体编号等于它才写入）；target 目标快照（null 直接失败）。
        // 返回：true 表示已写入（world 为 null 时得到空快照也算成功）；false 表示 target 为 null，或帧号不递增被挡下（此时 target 与 m_lastFrame 都不动）。
        // 步骤：
        //   1) target 为 null 直接失败，避免后面空引用
        //   2) frame 不大于 m_lastFrame 就计数并挡下 —— SnapshotView.Apply 对不递增的 ServerTick 会静默丢弃（StaleIgnored 涨、Applied 不涨），表现成场景空白却不报错，所以先在这里拦住
        //   3) world 为 null 时把实体清到 0 并写入帧号，保持帧号单调
        //   4) 用 CopySortedTo 复用 m_sorted 取按 Id 升序的表：与 SnapshotSorted 是同一序列（Id 是唯一键），但不每帧 ToArray 产生小数组（30Hz 下每秒 30 个）
        //   5) 先把池子撑够，再按下标覆盖已有 EntitySnapshot，只有实体变多才 Add —— 不调 target.Entities.Clear()，它对非空 RepeatedField 每次分配 88 字节
        //   6) 逐项写实体：Id 与血量取世界值；Kind 写 0（英雄，锁步里都是玩家）、ConfigId 与 FacingDeg 暂写 0（还没有配置表与朝向）；Alive 按 Hp 大于 0 算
        //   7) 位置从米（定点）乘 1000 截断成毫米整数，只写 X/Z（锁步单位一直在平面上跑，Y 轴留空），全程不引浮点
        //   8) owner_player_id 只在实体编号等于玩家编号时写入，其余写 0 表示无主 —— 客户端靠它认出哪个是我，猜错会变成操纵别人的角色，而且静默且看起来很正常
        //   9) 用 ResizeEntities 从尾部摘掉多余实体，最后写 ServerTick 为这一帧的帧号（不是世界内部 Tick，后者可能已被 Step 推过）
        // 注意：每帧不得分配 —— 池子、排序表、目标快照都复用，任何一处 new 都会破坏这条约束；探针用 GC.GetAllocatedBytesForCurrentThread 的增量检查盯着。
        // 注意：帧号单调由调用方保证（LockstepClient.LastSteppedFrame 本来就单调），本类只挡不修。
        // 注意：实体编号当前等于玩家编号，这是本阶段的简化；以后身份与实体拆开时要改 8) 的判据。
        // 上游：PresentationFeed.Refresh（Game\Net）
        // 下游：WorldState.CopySortedTo、EnsurePool、ResizeEntities、MetersToMm；产出的 target 交给 SnapshotView.Apply
        public bool ApplyTo(WorldState? world, int frame, long ownerPlayerId, WorldSnapshot target)
        {
            if (target == null)
            {
                return false;
            }

            if (frame <= m_lastFrame)
            {
                m_nonMonotonic++;
                return false;
            }

            if (world == null)
            {
                ResizeEntities(target, 0);
                target.ServerTick = frame;
                m_lastFrame = frame;
                return true;
            }

            int count = world.CopySortedTo(m_sorted);

            EnsurePool(count);

            int existing = target.Entities.Count;

            for (int i = 0; i < count; i++)
            {
                EntitySnapshot e = m_pool[i];

                e.EntityId = m_sorted[i].Id;
                e.ConfigId = 0;
                e.Kind = 0;
                e.Hp = m_sorted[i].Hp;
                e.MaxHp = m_sorted[i].MaxHp;

                e.PosXMm = MetersToMm(m_sorted[i].Position.X);
                e.PosZMm = MetersToMm(m_sorted[i].Position.Z);

                e.FacingDeg = 0;
                e.Alive = m_sorted[i].Hp > 0;

                e.OwnerPlayerId = m_sorted[i].Id == (int)ownerPlayerId ? ownerPlayerId : 0;

                if (i < existing)
                {
                    target.Entities[i] = e;
                }
                else
                {
                    target.Entities.Add(e);
                }
            }

            ResizeEntities(target, count);

            target.ServerTick = frame;
            m_lastFrame = frame;
            return true;
        }

        // 功能：把 target.Entities 从尾部摘到只剩 count 条。
        // 入参：target 目标快照（调用方保证非 null）；count 要保留的条数（大于等于 0，通常等于当前世界实体数）。
        // 返回：无。
        // 步骤：
        //   1) 从末尾逐个 RemoveAt，直到条数等于 count（只减不增）
        // 注意：存在的唯一理由是 RepeatedField 的 Clear() 对非空字段每次分配 88 字节，每帧调一次就会破坏零分配约束。
        // 上游：ApplyTo（world 为 null 时清 0，正常转换后收尾）
        // 下游：无
        private static void ResizeEntities(WorldSnapshot target, int count)
        {
            while (target.Entities.Count > count)
            {
                target.Entities.RemoveAt(target.Entities.Count - 1);
            }
        }

        // 功能：把 EntitySnapshot 池撑到至少 count 个。
        // 入参：count 本次要用的条数（大于等于 0，等于世界实体数）。
        // 返回：无。
        // 步骤：
        //   1) 池子只增不减，缺几个就补几个 new EntitySnapshot，稳态下不再分配
        // 注意：池子大小会停在历史最大实体数，这是为稳态零分配付的内存代价。
        // 上游：ApplyTo
        // 下游：无
        private void EnsurePool(int count)
        {
            while (m_pool.Count < count)
            {
                m_pool.Add(new EntitySnapshot());
            }
        }

        // 功能：把定点米换算成整数毫米。
        // 入参：meters 位置坐标（单位米，Fix64 定点）。
        // 返回：毫米（整数，截断取整，负数向 0 截断）。
        // 步骤：
        //   1) 乘 1000 得到毫米（Fix64.FromInt 保持定点），再截断成 int
        // 注意：全定点与整数运算，不引入浮点，避免不同平台的浮点差异。
        // 上游：ApplyTo 写 PosXMm 与 PosZMm
        // 下游：无
        private static int MetersToMm(Fix64 meters)
        {
            return (int)(meters * Fix64.FromInt(1000));
        }
    }
}
