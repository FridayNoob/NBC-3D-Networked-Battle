// ============================================================================
//  LockstepScheduler —— 锁步调度：收输入 → 到点广播一帧 → 推进一次（M4-S4 S4-c）
//  项目：3D联网战斗Demo   对应：`Docs\27` §二十九 S4-c、§三十二
//
//  ---------------------------------------------------------------------------
//  为什么放 `Shared\Sim\`（引擎无关）
//  ---------------------------------------------------------------------------
//  这样它的**纯逻辑部分**能被探针**直接驱动**（不开 socket 就能跑、能变异）；
//  socket 那一层（收到消息 → 调 `Submit`；`AdvanceTick` 的结果 → 发广播）留到下一轮。
//  本文件**只依赖 BCL + `Shared\Sim` 里的东西**（不引 UnityEngine / 网络）。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 三条语义（**写死在这里**，将来排查全靠它们）
//  ---------------------------------------------------------------------------
//  ① **缺输入**：第 N 帧在 `ServerTick >= N + Delay` 时广播。届时**还没交**的玩家
//     一律填**默认输入（不动不打）**，并把那条的 `Missing` 置 **true**。
//     ⇒ "等多久算缺" = **固定输入延迟 `Delay` 个 tick**（默认 2），**到点之后不再等**；
//        到点之后**再来的输入一律作废**（帧已经广播出去了，改不了）。
//     ⚠️ 为什么要有 `Missing`：缺人必须**看得见**，否则客户端只会觉得"他怎么不动"。
//
//  ② **去重与乱序**：同一 `(帧, 玩家)` 的**第一条有效输入为准**，后来的**忽略**（并计数）。
//     **第 N+2 帧的输入先到 ⇒ 缓冲起来，不许提前用**（帧号单调）。
//     已经广播过的帧再收到输入 ⇒ 作废（计数）。
//
//  ③ **同一帧只推进一次**：只有 `AdvanceTick` 到点才 `Step`，`lastSteppedFrame` 单调 +1。
//     ⚠️ `Submit` **从不**推进世界 —— 这正是那道闸。
//     ⚠️ **锁步最经典的静默 bug 就是破了这一条**：收到重复输入就再 `Step` 一次
//        ⇒ 两边的帧数**悄悄错开**（一个有 100 帧、一个 101 帧），而对账要跑很久才发现。
//        探针专门盯 `StepsExecuted`（它必须 == 广播过的帧数）。
//
//  ---------------------------------------------------------------------------
//  ④ 玩家的 `player_id` **同时当作实体编号**用（本 Demo 一人一实体）
//  ---------------------------------------------------------------------------
//  这是刻意的简化：让"谁发的输入"与"动哪个实体"是同一个映射，
//  少一层"玩家→实体"的表就少一处会不同步的地方。将来一人多实体时再引入映射。
// ============================================================================

using System.Collections.Generic;

namespace NBC.Shared.Sim
{
    /// <summary>一个玩家对某一帧的输入（**调度器的权威记录**）。</summary>
    public struct FrameInput
    {
        /// <summary>玩家编号（= 实体编号，见文件头 ④）。</summary>
        public long PlayerId;

        /// <summary>移动方向（定点）。</summary>
        public FixVector3 MoveDirection;

        /// <summary>按键位掩码。</summary>
        public int Buttons;

        /// <summary>要打的目标（0 = 不攻击）。</summary>
        public int TargetEntityId;

        /// <summary>⚠️ true = 这位玩家这一帧**没发**输入，填的是默认值。</summary>
        public bool Missing;
    }

    /// <summary>某一帧的广播内容（服务端把这份发出去）。</summary>
    public sealed class LockstepFramePlan
    {
        /// <summary>帧号。</summary>
        public int Frame;

        /// <summary>这一帧各玩家的输入（**顺序 = 名册顺序**，与 `Roster` 一致 ⇒ 确定）。</summary>
        public readonly List<FrameInput> Inputs = new List<FrameInput>();

        /// <summary>服务端推进这一帧之后的世界哈希（客户端拿它对账）。</summary>
        public ulong ServerHash;

        /// <summary>这一帧有几个人缺输入。</summary>
        public int MissingCount
        {
            get
            {
                int n = 0;

                for (int i = 0; i < Inputs.Count; i++)
                {
                    if (Inputs[i].Missing)
                    {
                        n++;
                    }
                }

                return n;
            }
        }
    }

    /// <summary>锁步调度器（引擎无关、探针可直驱）。</summary>
    public sealed class LockstepScheduler
    {
        /// <summary>默认的固定输入延迟（tick）。</summary>
        public const int DefaultInputDelayTicks = 2;

        /// <summary>世界（唯一那份权威状态）。</summary>
        private readonly WorldState m_world;

        /// <summary>固定输入延迟（tick）—— 见文件头 ①。</summary>
        private readonly int m_delay;

        /// <summary>名册（**顺序固定**：广播里输入的顺序就按它，保证可对账）。</summary>
        private readonly List<long> m_roster = new List<long>();

        /// <summary>还没到点广播的输入（帧号 → 该帧的输入；**只按键查找，从不遍历**）。</summary>
        private readonly List<PendingFrame> m_pending = new List<PendingFrame>();

        /// <summary>服务端自己的时钟（tick）。</summary>
        private int m_serverTick;

        /// <summary>已经广播/推进到哪一帧（−1 = 一帧都还没有）。</summary>
        private int m_lastSteppedFrame = -1;

        /// <summary>累计真的推进了几帧（**变异要盯的就是它** —— 见文件头 ③）。</summary>
        private int m_stepsExecuted;

        /// <summary>累计采纳的输入条数。</summary>
        private int m_accepted;

        /// <summary>累计被忽略的重复输入。</summary>
        private int m_duplicates;

        /// <summary>累计作废的过期输入（该帧已经广播过了）。</summary>
        private int m_stale;

        /// <summary>未被采纳的输入总数（= 重复 + 过期；**必须看得见**）。</summary>
        private int m_rejected;

        /// <summary>造一个调度器。</summary>
        /// <param name="world">世界（不能为 null）。</param>
        /// <param name="rosterOrder">名册（玩家编号，**顺序即广播顺序**；不能为 null）。</param>
        /// <param name="delayTicks">固定输入延迟（&lt; 0 会被钳到 0）。</param>
        public LockstepScheduler(WorldState world, IReadOnlyList<long> rosterOrder, int delayTicks)
        {
            m_world = world;
            m_delay = delayTicks < 0 ? 0 : delayTicks;

            if (rosterOrder != null)
            {
                for (int i = 0; i < rosterOrder.Count; i++)
                {
                    m_roster.Add(rosterOrder[i]);
                }
            }
        }

        /// <summary>名册（**顺序即广播顺序**）。</summary>
        public IReadOnlyList<long> Roster
        {
            get { return m_roster; }
        }

        /// <summary>服务端时钟。</summary>
        public int ServerTick
        {
            get { return m_serverTick; }
        }

        /// <summary>已经广播/推进到哪一帧（−1 = 还没有）。</summary>
        public int LastSteppedFrame
        {
            get { return m_lastSteppedFrame; }
        }

        /// <summary>真的推进了几帧。⚠️ 它必须 == 广播过的帧数（见文件头 ③）。</summary>
        public int StepsExecuted
        {
            get { return m_stepsExecuted; }
        }

        /// <summary>被丢掉的输入条数（重复 + 过期）。</summary>
        public int RejectedInputs
        {
            get { return m_rejected; }
        }

        /// <summary>一句人话（排查用）。</summary>
        /// <returns>例：`锁步：钟 5、推进 3 帧、采纳 6 条、丢 2 条（重复 1 / 过期 1）`。</returns>
        public string Describe()
        {
            return "锁步：钟 " + m_serverTick + "、推进 " + m_stepsExecuted + " 帧、采纳 " + m_accepted +
                   " 条、丢 " + m_rejected + " 条（重复 " + m_duplicates + " / 过期 " + m_stale + "）";
        }

        /// <summary>
        /// 交一条输入（**从不推进世界** —— 见文件头 ③）。
        /// <para>⚠️ 语义见文件头 ②：第一条有效输入为准；未来的帧缓冲起来；已广播的帧作废。</para>
        /// </summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="frame">这是第几帧的输入。</param>
        /// <param name="moveDirection">移动方向（定点）。</param>
        /// <param name="buttons">按键位掩码。</param>
        /// <param name="targetEntityId">目标（0 = 不攻击）。</param>
        /// <param name="reason">没被采纳时的原因（被采纳时是空串）。</param>
        /// <returns>被采纳返回 true。</returns>
        public bool Submit(long playerId, int frame, FixVector3 moveDirection,
                           int buttons, int targetEntityId, out string reason)
        {
            reason = string.Empty;

            if (playerId <= 0)
            {
                reason = "玩家编号必须 > 0（游客没有实体）";
                m_rejected++;
                return false;
            }

            // ② 已经广播过的帧 ⇒ 作废（**绝不**回头改，也**绝不**因此再 Step 一次）
            if (frame <= m_lastSteppedFrame)
            {
                m_stale++;
                m_rejected++;
                reason = "第 " + frame + " 帧已经广播过了（当前已推进到 " + m_lastSteppedFrame + "）";
                return false;
            }

            PendingFrame bucket = FindOrCreate(frame);

            // ② 同一 (帧, 玩家) 的第一条为准 ⇒ 后来的忽略
            if (bucket.Has(playerId))
            {
                m_duplicates++;
                m_rejected++;
                reason = "第 " + frame + " 帧里玩家 " + playerId + " 已经有输入了（重复的忽略）";
                return false;
            }

            FrameInput input;
            input.PlayerId = playerId;
            input.MoveDirection = moveDirection;
            input.Buttons = buttons;
            input.TargetEntityId = targetEntityId;
            input.Missing = false;

            bucket.Add(input);
            m_accepted++;
            return true;
        }

        /// <summary>
        /// 服务端时钟走一格；**到点就广播并推进一帧**。
        /// <para>⚠️ 见文件头 ①：第 N 帧在 `ServerTick >= N + Delay` 时才广播（之后不再等）。</para>
        /// </summary>
        /// <param name="plan">要发出去的这一帧（没到点时是 null）。</param>
        /// <returns>这一格真的推进了一帧返回 true。</returns>
        public bool AdvanceTick(out LockstepFramePlan plan)
        {
            // ⚠️ `null!` 是刻意的：`TryXxx(out T)` 模式里 out 参数必须先赋一个值，而这里
            //    「没到点就返回 false」，plan 的 null 只在返回 false 时有意义（调用方先判 bool）。
            plan = null!;
            m_serverTick++;

            int next = m_lastSteppedFrame + 1;

            // ① 还没到点 ⇒ 继续等（这就是"固定输入延迟"）
            if (m_serverTick < next + m_delay)
            {
                return false;
            }

            // ③ **同一帧只推进一次**：只有走到这里才会 Step，而 m_lastSteppedFrame 单调 +1
            plan = BuildAndStep(next);
            return true;
        }

        /// <summary>把第 frame 帧凑齐（缺的填默认 + `Missing`）、推进世界一次、算哈希。</summary>
        /// <param name="frame">帧号。</param>
        /// <returns>广播内容。</returns>
        private LockstepFramePlan BuildAndStep(int frame)
        {
            var plan = new LockstepFramePlan();
            plan.Frame = frame;

            PendingFrame? bucket = Find(frame);

            // ⚠️ **按名册顺序**产出（不是按输入到达顺序）⇒ 广播内容确定，可逐帧对账
            for (int i = 0; i < m_roster.Count; i++)
            {
                long playerId = m_roster[i];

                FrameInput input;

                if (bucket != null && bucket.TryGet(playerId, out input))
                {
                    plan.Inputs.Add(input);
                    continue;
                }

                // ① 缺输入 ⇒ 默认（不动不打）+ **Missing = true**（缺人必须看得见）
                input.PlayerId = playerId;
                input.MoveDirection = FixVector3.Zero;
                input.Buttons = 0;
                input.TargetEntityId = 0;
                input.Missing = true;
                plan.Inputs.Add(input);
            }

            // 把这一帧的输入转成 `WorldStep` 要的形状，然后**推进一次**
            var stepInputs = new List<SimInput>(plan.Inputs.Count);

            for (int i = 0; i < plan.Inputs.Count; i++)
            {
                SimInput si;
                si.EntityId = (int)plan.Inputs[i].PlayerId;
                si.MoveDirection = plan.Inputs[i].MoveDirection;
                si.AttackTargetId = plan.Inputs[i].TargetEntityId;
                stepInputs.Add(si);
            }

            WorldStep.Step(m_world, stepInputs, 1);

            plan.ServerHash = WorldStateHash.Compute(m_world);

            m_lastSteppedFrame = frame;
            m_stepsExecuted++;

            // 这一帧广播出去了 ⇒ 缓冲可以丢了（之后再来的一律作废）
            // ⚠️ 判空：`Find` 可能返回 null（没有这一帧的缓冲 = 谁都没发过输入）—— 那也要能推进。
            if (bucket != null)
            {
                m_pending.Remove(bucket);
            }

            return plan;
        }

        /// <summary>找某一帧的缓冲。</summary>
        /// <param name="frame">帧号。</param>
        /// <returns>缓冲；没有则 null。</returns>
        private PendingFrame? Find(int frame)
        {
            for (int i = 0; i < m_pending.Count; i++)
            {
                if (m_pending[i].Frame == frame)
                {
                    return m_pending[i];
                }
            }

            return null;
        }

        /// <summary>找某一帧的缓冲，没有就建。</summary>
        /// <param name="frame">帧号。</param>
        /// <returns>缓冲。</returns>
        private PendingFrame FindOrCreate(int frame)
        {
            PendingFrame? found = Find(frame);

            if (found != null)
            {
                return found;
            }

            var created = new PendingFrame();
            created.Frame = frame;
            m_pending.Add(created);
            return created;
        }

        /// <summary>某一帧的输入缓冲（**List + 线性查找**：与"遍历顺序"无关）。</summary>
        private sealed class PendingFrame
        {
            /// <summary>帧号。</summary>
            public int Frame;

            /// <summary>这一帧收到的输入。</summary>
            private readonly List<FrameInput> m_inputs = new List<FrameInput>();

            /// <summary>这个玩家这一帧交过了没有。</summary>
            /// <param name="playerId">玩家编号。</param>
            /// <returns>交过返回 true。</returns>
            public bool Has(long playerId)
            {
                FrameInput ignored;
                return TryGet(playerId, out ignored);
            }

            /// <summary>取某个玩家的输入。</summary>
            /// <param name="playerId">玩家编号。</param>
            /// <param name="input">输入。</param>
            /// <returns>有则 true。</returns>
            public bool TryGet(long playerId, out FrameInput input)
            {
                for (int i = 0; i < m_inputs.Count; i++)
                {
                    if (m_inputs[i].PlayerId == playerId)
                    {
                        input = m_inputs[i];
                        return true;
                    }
                }

                input = default(FrameInput);
                return false;
            }

            /// <summary>收下一条输入。</summary>
            /// <param name="input">输入。</param>
            public void Add(FrameInput input)
            {
                m_inputs.Add(input);
            }
        }
    }
}
