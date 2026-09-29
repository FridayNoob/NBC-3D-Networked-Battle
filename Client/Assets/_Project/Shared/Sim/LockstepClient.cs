// ============================================================================
//  LockstepClient —— 客户端的**锁步运行器**（M4-S4 S4-c 收尾）
//  项目：3D联网战斗Demo   对应：`Docs\27` §三十三 的 33.4
//
//  ---------------------------------------------------------------------------
//  一、⚠️ 为什么是纯逻辑、以及**为什么只吃普通数据**（规则 W22）
//  ---------------------------------------------------------------------------
//  它是帧同步的**报警器**：客户端自己 `Step` 一遍，再拿自己的哈希与服务端广播里的
//  `server_hash` 比。这段必须能**被直接驱动、被改坏、当场变红** ⇒ 放 `Shared\Sim\`。
//
//  ⚠️ **W22：`Shared\Sim\` 是通配进 `NBC.Shared` 的，而 `NBC.Shared` 不引用 `NBC.Protocol`**
//     ⇒ 放这里的运行器**只能吃普通数据**（`SimEntity` / `FrameInput`），
//        **不许**出现 `LockstepStart` / `LockstepFrame` 这些生成类型。
//        协议转换留在 `NetSession`（它两边都引得到）。
//     第一版我按"直接收消息"写 ⇒ 把 `NBC.Shared` 编坏了 ⇒ **整个仓库跟着红**。
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 四条写死的语义（每条都是一类**静默**错误）
//  ---------------------------------------------------------------------------
//  ① **开局先自算一遍哈希**：建完世界立刻算，与 `initialHash` 比。不等 ⇒ **不许开始**、
//     报一条**帧 0** 的分歧、计数。不这么做的话起点就不一样，后面每帧都报分歧 ⇒ **真因被淹没**。
//  ② **未开局收到帧 ⇒ 忽略 + 计数**（不是"先跑起来"）：拿空世界 `Step` 只会算出满屏假分歧。
//  ③ **同一帧只 `Step` 一次；帧号倒退不接受**（都计数）：重复 `Step` 会让帧数与服务端
//     **悄悄错开**（§三十二 变异 M 验过的形状）。
//  ④ **分歧要带"两个哈希"与帧号**：只说"不一致"等于没说。
// ============================================================================

using System;
using System.Collections.Generic;

namespace NBC.Shared.Sim
{
    /// <summary>一条分歧报告（**带上两个哈希**，否则排查不动）。</summary>
    public readonly struct LockstepDesync
    {
        /// <summary>帧号（开局那次就是开局的 `frame`，通常 0）。</summary>
        public readonly int Frame;

        /// <summary>我算出来的哈希。</summary>
        public readonly ulong Mine;

        /// <summary>服务端给的哈希。</summary>
        public readonly ulong Server;

        /// <summary>true = 开局那一次（`initialHash` 对不上）。</summary>
        public readonly bool AtStart;

        /// <summary>造一条报告。</summary>
        /// <param name="frame">帧号。</param>
        /// <param name="mine">我的哈希。</param>
        /// <param name="server">服务端的哈希。</param>
        /// <param name="atStart">是不是开局那次。</param>
        public LockstepDesync(int frame, ulong mine, ulong server, bool atStart)
        {
            Frame = frame;
            Mine = mine;
            Server = server;
            AtStart = atStart;
        }

        /// <summary>一句人话。</summary>
        /// <returns>例：`第 12 帧分歧：我 123 vs 服务端 456`。</returns>
        public override string ToString()
        {
            return (AtStart ? "**开局**分歧（帧 " + Frame + "）" : "第 " + Frame + " 帧分歧") +
                   "：我 " + Mine + " vs 服务端 " + Server;
        }
    }

    /// <summary>客户端的锁步运行器（引擎无关、**只吃普通数据** ⇒ 探针能直驱、能变异）。</summary>
    public sealed class LockstepClient
    {
        /// <summary>我这份世界（未开局时为 null）。</summary>
        private WorldState? m_world;

        /// <summary>开局了没有。</summary>
        private bool m_started;

        /// <summary>已经 `Step` 到哪一帧。</summary>
        private int m_lastSteppedFrame = -1;

        /// <summary>`Step` 了几次（**必须 == 接受过的帧数**）。</summary>
        private int m_stepsExecuted;

        /// <summary>分歧次数。</summary>
        private int m_desyncs;

        /// <summary>未开局时被忽略的帧数。</summary>
        private int m_ignoredBeforeStart;

        /// <summary>被拒绝的帧数（重复 / 倒退 / 重复开局）。</summary>
        private int m_rejectedFrames;

        /// <summary>最近一条分歧（`HasDesync` 为 false 时无意义）。</summary>
        private LockstepDesync m_lastDesync;

        /// <summary>报出一条分歧（`NetSession` 把它转成对外事件）。</summary>
        public event Action<LockstepDesync>? DesyncDetected;

        /// <summary>开局了没有（`initialHash` 对不上时为 false）。</summary>
        public bool Started
        {
            get { return m_started; }
        }

        /// <summary>我这份世界（未开局时为 null）。</summary>
        public WorldState? World
        {
            get { return m_world; }
        }

        /// <summary>已经 `Step` 到哪一帧。</summary>
        public int LastSteppedFrame
        {
            get { return m_lastSteppedFrame; }
        }

        /// <summary>`Step` 了几次。</summary>
        public int StepsExecuted
        {
            get { return m_stepsExecuted; }
        }

        /// <summary>分歧次数。</summary>
        public int DesyncCount
        {
            get { return m_desyncs; }
        }

        /// <summary>有没有过分歧。</summary>
        public bool HasDesync
        {
            get { return m_desyncs > 0; }
        }

        /// <summary>最近一条分歧（带帧号 + 两哈希）。</summary>
        public LockstepDesync LastDesync
        {
            get { return m_lastDesync; }
        }

        /// <summary>未开局时被忽略的帧数（**要看得见**，否则"没反应"没法解释）。</summary>
        public int IgnoredBeforeStart
        {
            get { return m_ignoredBeforeStart; }
        }

        /// <summary>被拒绝的帧数（重复 / 倒退 / 重复开局）。</summary>
        public int RejectedFrames
        {
            get { return m_rejectedFrames; }
        }

        /// <summary>一句人话（排查用）。</summary>
        /// <returns>例：`锁步客户端：已开局、推进 12 帧、分歧 0、开局前忽略 0、拒 1`。</returns>
        public string Describe()
        {
            return "锁步客户端：" + (m_started ? "已开局" : "**未开局**") +
                   "、推进 " + m_stepsExecuted + " 帧、分歧 " + m_desyncs +
                   "、开局前忽略 " + m_ignoredBeforeStart + "、拒 " + m_rejectedFrames;
        }

        /// <summary>
        /// 收开局：**按 `entity_id` 排序**建世界、`frame` 设成世界 `Tick`，
        /// **先自己算一遍哈希**与 `initialHash` 比。
        /// <para>⚠️ 普通数据入参（见文件头一 / 规则 W22）：协议转换在 `NetSession` 做。</para>
        /// </summary>
        /// <param name="frame">开局帧号（世界 `Tick` 就取它）。</param>
        /// <param name="initialHash">服务端给的开局哈希。</param>
        /// <param name="entities">初始实体（顺序无所谓）。</param>
        /// <returns>开局成功（哈希也对得上）返回 true。</returns>
        public bool ApplyStart(int frame, ulong initialHash, IReadOnlyList<SimEntity> entities)
        {
            if (entities == null)
            {
                m_rejectedFrames++;
                return false;
            }

            // ⚠️ 重复开局：**不重建世界**（重建会把已推进的帧全抹掉，而且不报错）
            if (m_started)
            {
                m_rejectedFrames++;
                return false;
            }

            var sorted = new List<SimEntity>(entities.Count);

            for (int i = 0; i < entities.Count; i++)
            {
                sorted.Add(entities[i]);
            }

            // ⚠️ **按 id 升序**再建（不许依赖到达顺序）—— §29.2 判据②在客户端这一侧的落点
            sorted.Sort(CompareEntityById);

            var world = new WorldState();

            for (int i = 0; i < sorted.Count; i++)
            {
                // ⚠️ `Add` 只吃 (id, pos, hp)，它会把 `MaxHp = hp`、`AttackReadyTick = 0`。
                //    所以**必须**把带来的真实值写回去，否则"带伤出生 / 开局给冷却"时两端的
                //    起点就不同 ⇒ 第 0 帧哈希不同（这正是 `LockstepStart` 带那两个字段的理由）。
                world.Add(sorted[i].Id, sorted[i].Position, sorted[i].Hp);

                int index = world.IndexOf(sorted[i].Id);
                SimEntity fixedUp = world.GetAt(index);
                fixedUp.MaxHp = sorted[i].MaxHp;
                fixedUp.AttackReadyTick = sorted[i].AttackReadyTick;
                world.SetAt(index, fixedUp);
            }

            world.SetTick(frame);

            ulong mine = WorldStateHash.Compute(world);

            if (mine != initialHash)
            {
                // ⚠️ 起点就不一样 ⇒ **不许开始**（见文件头 ①）
                m_desyncs++;
                m_world = null;
                m_started = false;
                m_lastDesync = new LockstepDesync(frame, mine, initialHash, true);
                DesyncDetected?.Invoke(m_lastDesync);
                return false;
            }

            m_world = world;
            m_started = true;
            m_lastSteppedFrame = frame - 1;
            return true;
        }

        /// <summary>
        /// 收一帧：用广播里的输入 `Step` 一次，再拿自己的哈希与 `serverHash` 比。
        /// <para>⚠️ 未开局 ⇒ 忽略 + 计数；重复/倒退帧 ⇒ 拒绝 + 计数（**都不 `Step`**）。</para>
        /// </summary>
        /// <param name="frame">帧号。</param>
        /// <param name="inputs">这一帧各玩家的输入（普通数据，含 `Missing`）。</param>
        /// <param name="serverHash">服务端算出来的该帧哈希。</param>
        /// <returns>真的推进了这一帧返回 true。</returns>
        public bool ApplyFrame(int frame, IReadOnlyList<FrameInput> inputs, ulong serverHash)
        {
            // ② 未开局 ⇒ 忽略 + 计数（不是"先跑起来"）
            if (!m_started || m_world == null)
            {
                m_ignoredBeforeStart++;
                return false;
            }

            // ③ 重复 / 倒退 ⇒ 拒绝 + 计数（**绝不 `Step`**）
            if (frame <= m_lastSteppedFrame)
            {
                m_rejectedFrames++;
                return false;
            }

            var stepInputs = new List<SimInput>(inputs == null ? 0 : inputs.Count);

            if (inputs != null)
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    SimInput input;
                    input.EntityId = (int)inputs[i].PlayerId;      // 见协议里那条"显式转换"的约定
                    input.MoveDirection = inputs[i].MoveDirection;
                    input.AttackTargetId = inputs[i].TargetEntityId;
                    stepInputs.Add(input);
                }
            }

            WorldStep.Step(m_world, stepInputs, 1);

            m_lastSteppedFrame = frame;
            m_stepsExecuted++;

            ulong mine = WorldStateHash.Compute(m_world);

            if (mine != serverHash)
            {
                // ④ 分歧要**带上两个哈希**（见文件头 ④）
                m_desyncs++;
                m_lastDesync = new LockstepDesync(frame, mine, serverHash, false);
                DesyncDetected?.Invoke(m_lastDesync);
            }

            return true;
        }

        /// <summary>按实体编号比较（静态 = 不捕获、不分配）。</summary>
        /// <param name="a">左。</param>
        /// <param name="b">右。</param>
        /// <returns>比较结果。</returns>
        private static int CompareEntityById(SimEntity a, SimEntity b)
        {
            return a.Id.CompareTo(b.Id);
        }
    }
}
