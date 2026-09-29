// ============================================================================
//  LockstepSnapshot —— 把**锁步世界**转成 `WorldSnapshot`（M4-S4 S4-e）
//  项目：3D联网战斗Demo   对应：`Docs\27` §三十五 的 35.2
//
//  ---------------------------------------------------------------------------
//  一、⚠️ 为什么要有这个适配器
//  ---------------------------------------------------------------------------
//  状态同步那条链的表现层接缝是 `SnapshotView.Apply(WorldSnapshot)`（`Game\Net\SnapshotView.cs`）。
//  锁步的世界是 `WorldState`（`Shared\Sim`，**普通数据**）。两者形状不同 ——
//  把锁步也**转成 `WorldSnapshot` 再交给同一个 `SnapshotView`** ⇒ **两种模式共用一套渲染**
//  （这正是"别另起一套渲染"的正解：另起一套就是两处会不一致的表现逻辑）。
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 为什么放 `Game\Net\`（**不许放 `Shared\Sim\`** —— 规则 W22）
//  ---------------------------------------------------------------------------
//  这个文件吃 `WorldSnapshot` / `EntitySnapshot`（**protobuf 类型**）。
//  `Shared\Sim\` 是**通配进 `NBC.Shared`** 的，而 **`NBC.Shared` 不引用 `NBC.Protocol`**
//  ⇒ 放那儿会**把整个仓库编不过**（撞过两次：`CS0234 命名空间"NBC"中不存在"Protocol"`）。
//  `Game\Net\`（`NBC.Game`）**同时**引用了 `NBC.Protocol` 与 `NBC.Shared` ✓，且**探针也通配编它** ✓。
//
//  ---------------------------------------------------------------------------
//  三、⚠️ 四条硬约束
//  ---------------------------------------------------------------------------
//  ① **只读**：绝不写回 `WorldState`、绝不调 `Step`（表现层反向影响确定性 = 最坏的耦合）。
//  ② **帧号必须单调递增**：`SnapshotView.Apply` 里 `if (snapshot.ServerTick <= ServerTick) { StaleIgnored++; return; }`
//     ⇒ 喂一个不递增（或重复）的帧号，快照会被**静静丢掉**，表现为"**场景里看不见东西**"的
//     **假故障**（不报错、不涨 `Applied`）。⇒ 这里用 `frame` 直接当 `server_tick`，且**由调用方保证递增**
//     （`LockstepClient.LastSteppedFrame` 本来就是单调的）。
//  ③ **每帧不许分配**：`target.Entities` 与里面的 `EntitySnapshot` **都复用**（见 `Pool`）。
//  ④ **`owner_player_id` 要正确**：客户端靠它认出"哪个是我"（M4-S1b 加的第 10 号字段）。
//     猜错的后果是"我的调试工具在操纵别人的角色"，**静默且看起来很正常**。
// ============================================================================

using System.Collections.Generic;
using NBC.Protocol;
using NBC.Shared;
using NBC.Shared.Sim;

namespace NBC.Game.Net
{
    /// <summary>锁步世界 → `WorldSnapshot`（**只读**、**复用缓冲**、帧号单调）。</summary>
    public sealed class LockstepSnapshotAdapter
    {
        /// <summary>复用的 `EntitySnapshot` 池（避免每帧 `new`）。</summary>
        private readonly List<EntitySnapshot> m_pool = new List<EntitySnapshot>();

        /// <summary>复用的目标快照（调用方也可以自己传一个）。</summary>
        private readonly WorldSnapshot m_target = new WorldSnapshot();

        /// <summary>上一次喂出去的帧号（用于**断言单调**、便于排查）。</summary>
        private int m_lastFrame = -1;

        /// <summary>因为帧号**不递增**而被我挡下的次数（正常应恒为 0；非 0 就是调用方在乱喂）。</summary>
        private long m_nonMonotonic;

        /// <summary>我挡下过几次不递增的帧号（正常应恒为 0）。</summary>
        public long NonMonotonicBlocked
        {
            get { return m_nonMonotonic; }
        }

        /// <summary>上一次转换用的帧号。</summary>
        public int LastFrame
        {
            get { return m_lastFrame; }
        }

        /// <summary>
        /// 把锁步世界写进 `target`（**target 会被清空重填**，里面的消息**复用池子**）。
        /// </summary>
        /// <param name="world">锁步世界（只读；可以为 null ⇒ 只清空）。</param>
        /// <param name="frame">帧号（⚠️ **必须单调递增**；见文件头 ②）。</param>
        /// <param name="ownerPlayerId">"我"的玩家编号（写进 `owner_player_id`；0 = 无主）。</param>
        /// <param name="target">目标快照（复用同一个对象最省）。</param>
        /// <returns>真的填进去了返回 true；帧号不递增被挡下返回 false。</returns>
        public bool ApplyTo(WorldState? world, int frame, long ownerPlayerId, WorldSnapshot target)
        {
            if (target == null)
            {
                return false;
            }

            // ② 帧号单调：不递增就**挡下**（否则会被 `SnapshotView` 静静丢掉 ⇒ 假故障）
            if (frame <= m_lastFrame)
            {
                m_nonMonotonic++;
                return false;
            }

            target.Entities.Clear();

            if (world == null)
            {
                target.ServerTick = frame;
                m_lastFrame = frame;
                return true;
            }

            SimEntity[] snapshot = world.SnapshotSorted();      // ⚠️ 按 Id 升序 ⇒ 与登记顺序无关

            EnsurePool(snapshot.Length);

            for (int i = 0; i < snapshot.Length; i++)
            {
                EntitySnapshot e = m_pool[i];

                e.EntityId = snapshot[i].Id;
                e.ConfigId = 0;                                  // 锁步这边还没有配置表（留给以后）
                e.Kind = 0;                                      // 0 = 英雄（锁步里都是玩家）
                e.Hp = snapshot[i].Hp;
                e.MaxHp = snapshot[i].MaxHp;

                // ⚠️ 位置：`FixVector3` 是**米**（定点），快照是**毫米**（整数）⇒ 显式换算、不用浮点
                e.PosXMm = MetersToMm(snapshot[i].Position.X);
                e.PosZMm = MetersToMm(snapshot[i].Position.Z);    // M3 起就在平面上跑，Y 轴留空

                e.FacingDeg = 0;                                 // 锁步还没做朝向
                e.Alive = snapshot[i].Hp > 0;

                // ④ `owner_player_id`：实体编号 == 玩家编号（S4-c 的简化）⇒ 直接写；0 = 无主
                e.OwnerPlayerId = snapshot[i].Id == (int)ownerPlayerId ? ownerPlayerId : 0;

                target.Entities.Add(e);
            }

            // ② 单调：用**这一帧的**帧号（不是世界内部的 Tick —— 那个可能已经被 Step 推过）
            target.ServerTick = frame;
            m_lastFrame = frame;
            return true;
        }

        /// <summary>把池子撑到至少 `count` 个（只增不减 ⇒ 稳态下不再分配）。</summary>
        /// <param name="count">需要的条数。</param>
        private void EnsurePool(int count)
        {
            while (m_pool.Count < count)
            {
                m_pool.Add(new EntitySnapshot());
            }
        }

        /// <summary>米（`Fix64` 定点）→ 毫米（整数）。⚠️ **全整数运算**，不引入浮点。</summary>
        /// <param name="meters">米。</param>
        /// <returns>毫米。</returns>
        private static int MetersToMm(Fix64 meters)
        {
            return (int)(meters * Fix64.FromInt(1000));
        }
    }
}
