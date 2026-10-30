// 这个文件里有：
//   FrameInput（结构体）：一个玩家对某一帧的输入，调度器收下的这一份是权威记录。
//   LockstepFramePlan（类）：一帧的广播内容，帧号、按名册顺序排好的输入与推进后的世界哈希。
//   LockstepScheduler（类）：锁步调度，收输入、到点广播一帧并推进世界一次。
//   PendingFrame（类，LockstepScheduler 的内嵌私有类）：某一帧的输入缓冲，按到达顺序存、按玩家编号查。

using System.Collections.Generic;

namespace NBC.Shared.Sim
{
    // 主要功能：打包一个玩家对某一帧的输入（移动方向、按键、攻击目标），并标出这一帧是不是没交。
    // 主要数据成员：PlayerId（玩家编号，同时是实体编号，只收 > 0）、MoveDirection（定点方向，按单位向量语义、无单位）、Buttons（按键位掩码）、TargetEntityId（目标实体编号，0 = 不攻击）、Missing（true = 填的是默认值）。
    // 主要外部方法：无，纯数据；由 LockstepWire 从协议转进来，交给 LockstepScheduler 收下再广播。
    // 谁在用它：同程序集的 LockstepScheduler 与 LockstepClient；NBC.Game 的 LockstepWire、NetSession 与 NBC.Server.Game 的 LockstepService 跨程序集构造并读写它。
    /// <summary>一个玩家对某一帧的输入：往哪走、按了什么、打谁；调度器收下的这一份是权威记录。</summary>
    public struct FrameInput
    {
        /// <summary>玩家编号，同时当实体编号用（本 Demo 一人一实体）；只收 > 0，0 与负数没有实体。</summary>
        public long PlayerId;

        /// <summary>移动方向（FixVector3 定点），按单位向量语义传、无单位；零向量表示不动，Step 里会先归一化。</summary>
        public FixVector3 MoveDirection;

        /// <summary>按键位掩码（int），各端同一份含义；0 表示这一帧没按键，这一片还没有消费方，只是原样带着走。</summary>
        public int Buttons;

        /// <summary>这一帧要攻击的目标实体编号，正整数；0 = 不攻击。</summary>
        public int TargetEntityId;

        /// <summary>true = 这位玩家这一帧没交输入，填的是默认值（不动不打）；缺人必须看得见，别当成正常输入。</summary>
        public bool Missing;
    }

    // 主要功能：装一帧要广播的东西 —— 帧号、按名册顺序排好的各玩家输入、服务端推进这一帧之后的世界哈希。
    // 主要数据成员：Frame（帧号，从 0 起）、Inputs（条数 = 名册人数，顺序 = 名册顺序）、ServerHash（64 位 FNV-1a，客户端拿它对账）、MissingCount（缺输入的人数，0 = 全员到齐）。
    // 主要外部方法：MissingCount（只读属性）；其余是公开字段，由 BuildAndStep 填满后整份交出去。
    // 谁在用它：同程序集的 LockstepScheduler.BuildAndStep 产出、联调探针逐帧比对；NBC.Server.Game 的 LockstepService.Broadcast 跨程序集把它转成协议帧发出去。
    /// <summary>一帧的广播内容：帧号、按名册顺序的各玩家输入、服务端推进后的世界哈希。</summary>
    public sealed class LockstepFramePlan
    {
        /// <summary>第几帧，从 0 起；服务端时钟 >= 帧号 + 固定输入延迟 时它才会被广播（见 LockstepScheduler）。</summary>
        public int Frame;

        /// <summary>这一帧各玩家的输入，顺序 = 名册顺序而不是到达顺序，两端因此能逐帧对账；缺人的项 Missing 为 true。</summary>
        public readonly List<FrameInput> Inputs = new List<FrameInput>();

        /// <summary>服务端推进这一帧之后的世界哈希（64 位 FNV-1a，全整数运算）；客户端算出来与它不同就是分歧。</summary>
        public ulong ServerHash;

        // 功能：数这一帧有几个人缺输入（Inputs 里 Missing 为 true 的条数）。
        // 返回：缺输入的人数；0 = 全员到齐。
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

    // 主要功能：服务端侧的锁步调度 —— 收各玩家交来的帧输入，按固定延迟到点把该帧凑齐、广播出去，并让世界正好推进一次。
    // 主要数据成员：m_world（唯一权威世界，位置单位米、Fix64 定点）、m_delay（固定输入延迟，tick）、m_roster（名册，决定广播顺序）、m_pending（未到点的输入缓冲）、m_serverTick / m_lastSteppedFrame / m_stepsExecuted（时钟 / 末帧 / 推进次数，单位 tick 与帧）。
    // 主要外部方法：构造、Submit（收输入）、AdvanceTick（走一格，到点广播）、StepsExecuted / RejectedInputs（对账与排查）、DefaultInputDelayTicks（默认延迟）。
    // 谁在用它：NBC.Server.Game 的 LockstepService（StartRoom / Tick / HandleLockstepInput）跨程序集驱动，Server 的联调探针直接驱动验收；本文件只依赖 BCL 与 Shared\Sim（不引 UnityEngine、NBC.Protocol、网络），所以探针不开 socket 也能跑。
    /// <summary>服务端锁步调度器：收输入、到点广播一帧并推进世界一次；引擎无关，探针可直接驱动。</summary>
    public sealed class LockstepScheduler
    {
        /// <summary>默认的固定输入延迟，2 tick；服务端 Host 也按这个值开局，StartRoom 的参数可以另行覆盖。</summary>
        public const int DefaultInputDelayTicks = 2;

        // 唯一那份权威世界；位置单位米、定点 Fix64，整个调度期间推进的就是它
        private readonly WorldState m_world;

        // 固定输入延迟，tick；负数在构造时钳成 0（0 = 不等，每走一格就推进一帧）
        private readonly int m_delay;

        // 名册（玩家编号，> 0），列表顺序就是广播里输入的顺序，两端靠它逐帧对账
        private readonly List<long> m_roster = new List<long>();

        // 还没到点的输入缓冲，按帧号线性查找；某一帧广播完就把它的那份删掉
        private readonly List<PendingFrame> m_pending = new List<PendingFrame>();

        // 服务端自己的时钟，tick；每次 AdvanceTick 加一，与帧号差一个固定延迟
        private int m_serverTick;

        // 已经广播并推进到哪一帧；−1 = 一帧都还没有，之后只增（重复与倒退的输入由 Submit 挡掉）
        private int m_lastSteppedFrame = -1;

        // 累计真的推进了几帧；必须等于广播过的帧数，多一帧就说明同一帧被推进了两次
        private int m_stepsExecuted;

        // 累计采纳的输入条数（去重之后真正进了缓冲的）
        private int m_accepted;

        // 累计被忽略的重复输入条数（同一帧同一玩家的第二条起）
        private int m_duplicates;

        // 累计作废的过期输入条数（那一帧已经广播出去了）
        private int m_stale;

        // 未被采纳的输入总数，单位条；= 重复 + 过期 + 玩家编号非法，只在重复 / 迟到 / 编号非法时增长
        private int m_rejected;

        /// <summary>造一个调度器：绑定权威世界与名册，并把固定输入延迟定下来。</summary>
        /// <param name="world">唯一那份权威世界，不能为 null；整个调度期间推进的就是它。</param>
        /// <param name="rosterOrder">名册（玩家编号），顺序即广播顺序；null 当作空名册。</param>
        /// <param name="delayTicks">固定输入延迟（tick），小于 0 会被钳到 0。</param>
        // 功能：把权威世界、名册与固定输入延迟记下来，构造期间不动世界。
        // 入参：world 唯一那份权威世界（不能为 null，本类不判空）；rosterOrder 名册（可为 null，按空名册处理）；delayTicks 固定输入延迟，tick（负数钳成 0）
        // 返回：无
        // 步骤：
        //   1) 世界直接记引用、不拷贝 —— 整个调度期间要推进的就是这一份
        //   2) 延迟负数钳成 0 —— 负数会让「到点」立刻成立、每格都推进，与 0 的语义重复，钳住只留一条「0 = 不等」
        //   3) 名册逐个拷进自己的 List：调用方之后再改它，也影响不到广播顺序
        // 上游：NBC.Server.Game 的 LockstepService.StartRoom（跨程序集）、Server 下的联调探针
        // 下游：无
        // 注意：名册顺序就是广播里输入的顺序 —— 它不影响世界哈希（WorldStep 只按实体编号查找），但两端的报文与逐帧对账全按它来，所以这里拷一份，别让调用方开局后再改。
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

        // 功能：只读地名册（玩家编号，顺序即广播顺序）。
        // 返回：内部列表本身（不是拷贝）；调用方只读，强转成 List 再改会打乱广播顺序。
        public IReadOnlyList<long> Roster
        {
            get { return m_roster; }
        }

        // 功能：只读服务端时钟。
        // 返回：tick 数，从 0 起；第 N 帧在它 >= N + 固定输入延迟 时才广播。
        public int ServerTick
        {
            get { return m_serverTick; }
        }

        // 功能：只读已经广播并推进到哪一帧。
        // 返回：帧号；−1 = 一帧都还没有，之后只增。
        public int LastSteppedFrame
        {
            get { return m_lastSteppedFrame; }
        }

        /// <summary>真的推进了几帧；正常必须等于广播过的帧数，多一帧就说明同一帧被 Step 了两次、两边帧数已经悄悄错开。</summary>
        public int StepsExecuted
        {
            get { return m_stepsExecuted; }
        }

        /// <summary>被丢掉的输入条数 = 重复 + 过期 + 玩家编号非法；无人重复、无人迟到时应为 0。</summary>
        public int RejectedInputs
        {
            get { return m_rejected; }
        }

        // 功能：把时钟、推进帧数、采纳与丢弃条数拼成一句中文，排查时直接打进日志。
        // 返回：形如「锁步：钟 5、推进 3 帧、采纳 6 条、丢 2 条（重复 1 / 过期 1）」；纯给人看，不参与任何判定。
        // 步骤：
        //   1) 六个计数直接拼成字符串，只求人一眼能读，不做格式化
        // 上游：本程序集内暂无调用点（服务端的统计走 LockstepService.Describe），手动排查时可调
        // 下游：无
        public string Describe()
        {
            return "锁步：钟 " + m_serverTick + "、推进 " + m_stepsExecuted + " 帧、采纳 " + m_accepted +
                   " 条、丢 " + m_rejected + " 条（重复 " + m_duplicates + " / 过期 " + m_stale + "）";
        }

        /// <summary>交一条某帧的输入；只进缓冲，从不推进世界。</summary>
        /// <param name="playerId">玩家编号，必须为正数（同时当实体编号用）。</param>
        /// <param name="frame">这是第几帧的输入；已广播过的帧会被作废，未来的帧先缓冲。</param>
        /// <param name="moveDirection">移动方向（定点 FixVector3），零向量 = 不动。</param>
        /// <param name="buttons">按键位掩码，原样收下，这一片还没有消费方。</param>
        /// <param name="targetEntityId">攻击目标实体编号，0 = 不攻击。</param>
        /// <param name="reason">没被采纳的原因（中文，可直接进日志）；被采纳时是空串。</param>
        /// <returns>采纳返回 true；编号非法 / 帧已广播 / 重复输入返回 false。</returns>
        // 功能：把一位玩家对某一帧的输入收进缓冲，同一 (帧, 玩家) 只认第一条。
        // 入参：playerId 玩家编号（非正数直接拒）；frame 帧号（已推进过的帧直接作废）；moveDirection 移动方向（定点）；buttons 按键位掩码；targetEntityId 目标（0 = 不攻击）；reason 被拒原因（采纳时置空串）
        // 返回：true = 收进缓冲了；false = 被拒（reason 里写了为什么），对应计数已经加过
        // 步骤：
        //   1) 先把 reason 置空串，调用方不必判 null
        //   2) 玩家编号非正数直接拒 —— 游客没有实体，收下也推进不了谁
        //   3) 帧号 <= 已推进帧号就直接作废：那一帧已经广播出去，改不了，也绝不能因此再 Step 一次
        //   4) 找到（或新建）这一帧的缓冲；未来的帧先存着，不许提前用
        //   5) 同一 (帧, 玩家) 已经有输入 ⇒ 忽略后来这条并计数：第一条为准，否则两端会因到达顺序不同而分叉
        //   6) 填成 FrameInput（Missing = false）收进缓冲，采纳数加一
        // 上游：NBC.Server.Game 的 LockstepService.HandleLockstepInput（跨程序集）、Server 下的联调探针
        // 下游：FindOrCreate
        // 注意：本方法从不推进世界 —— 只有 AdvanceTick 到点才 Step；在这里顺手 Step 会让两端帧数悄悄错开，而对账要跑很久才发现。
        // 注意：未来的帧号不做上限检查 —— 离得太远的帧会一直留在缓冲里直到那一帧被广播（正常客户端按已推进帧号 + 固定提前量发，跑不到很前面）。
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

            if (frame <= m_lastSteppedFrame)
            {
                m_stale++;
                m_rejected++;
                reason = "第 " + frame + " 帧已经广播过了（当前已推进到 " + m_lastSteppedFrame + "）";
                return false;
            }

            PendingFrame bucket = FindOrCreate(frame);

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

        /// <summary>服务端时钟走一格；到点就广播并推进一帧。</summary>
        /// <param name="plan">到点时的广播内容；没到点返回 false 时它是 null，先判返回值再解引用。</param>
        /// <returns>这一格真的推进了一帧返回 true，还没到点返回 false。</returns>
        // 功能：把服务端时钟加一，到了「帧号 + 固定延迟」就把那一帧凑齐、推进世界一次并交出去。
        // 入参：无（plan 是 out 参数，见返回）
        // 返回：这一格真的推进了一帧返回 true；还没到点返回 false，此时 plan 是 null
        // 步骤：
        //   1) 先把 out 参数赋 null! —— TryXxx(out T) 要求先赋值，「没到点」时它只在返回 false 时有意义
        //   2) 时钟加一；待推进的帧号由 lastSteppedFrame + 1 推出，不另存，少一处会对不上的状态
        //   3) 还没到 next + 延迟 就返回 false：这就是固定输入延迟，到点之前一直等
        //   4) 到点才调 BuildAndStep，推进唯一的那一次
        // 上游：NBC.Server.Game 的 LockstepService.Tick（跨程序集）、Server 下的联调探针
        // 下游：BuildAndStep
        // 注意：同一帧只推进一次全靠这里 —— 只有走到第 4 步才会 Step，lastSteppedFrame 因此单调加一；绕开它去 Step，两端帧数就会悄悄错开。
        // 注意：到点就不再等 —— 缺席的玩家由 BuildAndStep 填默认输入并标 Missing，之后补来的输入按过期作废。
        public bool AdvanceTick(out LockstepFramePlan plan)
        {
            plan = null!;
            m_serverTick++;

            int next = m_lastSteppedFrame + 1;

            if (m_serverTick < next + m_delay)
            {
                return false;
            }

            plan = BuildAndStep(next);
            return true;
        }

        // 功能：把第 frame 帧凑齐（按名册顺序，缺的填「不动不打」并标 Missing）、推进世界一次、算哈希，然后丢掉这份缓冲。
        // 入参：frame 帧号（= 已推进帧号 + 1，由 AdvanceTick 保证到点）
        // 返回：这一帧的广播内容：帧号、按名册顺序的输入、推进之后的世界哈希
        // 步骤：
        //   1) 先取这一帧的缓冲；可能为 null（谁都没交输入），那也要能推进
        //   2) 按名册顺序逐个玩家产出输入 —— 不是按到达顺序，这样广播内容确定、可逐帧对账
        //   3) 该玩家这一帧有输入就原样放进去；没有就填默认值（零方向、0 按键、0 目标）并把 Missing 置 true：缺人必须看得见
        //   4) 再整体转成 WorldStep 要的 SimInput：玩家编号窄化成实体编号，按键位掩码丢掉（SimInput 没有这个字段，这一片只有移动与普攻）
        //   5) 推进世界 1 个 tick，再算哈希（哈希必须在 Step 之后算，客户端才算得对得上）
        //   6) 最后才写末帧与推进次数：先推进、后记账，中途抛异常也不会出现「记了没推」
        //   7) 这一帧已经广播 ⇒ 把它的缓冲从 m_pending 删掉；之后同帧再来的输入由 Submit 按过期作废
        // 上游：AdvanceTick（本程序集内的唯一入口）
        // 下游：Find、WorldStep.Step、WorldStateHash.Compute
        // 注意：PlayerId 是 long、SimEntity.Id 是 int，第 4 步是强制窄化 —— 玩家编号超过 int 上限会被截成另一个实体；现在的玩家编号远小于上限所以踩不到，将来放宽编号时要一起改。
        // 注意：顺序必须按名册来 —— 改成按到达顺序会让同一帧在两端的报文不一样，逐帧对账直接失真。
        // 注意：全程只用整数与 Fix64 定点（方向就是 FixVector3），别在这里引入 float / double：一个浮点中间量就能让看起来相同的两个状态算出不同哈希。
        private LockstepFramePlan BuildAndStep(int frame)
        {
            var plan = new LockstepFramePlan();
            plan.Frame = frame;

            PendingFrame? bucket = Find(frame);

            for (int i = 0; i < m_roster.Count; i++)
            {
                long playerId = m_roster[i];

                FrameInput input;

                if (bucket != null && bucket.TryGet(playerId, out input))
                {
                    plan.Inputs.Add(input);
                    continue;
                }

                input.PlayerId = playerId;
                input.MoveDirection = FixVector3.Zero;
                input.Buttons = 0;
                input.TargetEntityId = 0;
                input.Missing = true;
                plan.Inputs.Add(input);
            }

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

            if (bucket != null)
            {
                m_pending.Remove(bucket);
            }

            return plan;
        }

        // 功能：按帧号在缓冲里找某一帧。
        // 入参：frame 帧号
        // 返回：那一帧的缓冲；没有则 null（= 这一帧谁都没交过输入）
        // 步骤：
        //   1) 线性扫 m_pending：缓冲里通常只有几个未来帧，线性比字典少一处迭代顺序的隐患
        //   2) 命中就返回引用；没命中返回 null，调用方必须判空
        // 上游：BuildAndStep、FindOrCreate
        // 下游：无
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

        // 功能：找某一帧的缓冲，没有就建一个空的挂进缓冲表。
        // 入参：frame 帧号
        // 返回：那一帧的缓冲，一定非 null
        // 步骤：
        //   1) 先按帧号找，找到就复用 —— 同一帧不许出现两份缓冲
        //   2) 没找到才 new 一个、填好帧号、加进 m_pending
        // 上游：Submit
        // 下游：Find
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

        // 主要功能：攒某一帧收到的各玩家输入，等 AdvanceTick 到点再一起交出去。
        // 主要数据成员：Frame（帧号，>= 0）、m_inputs（这一帧已收的输入，按到达顺序存，不排序）。
        // 主要外部方法：Has / TryGet / Add（只被本文件的 LockstepScheduler 调用）。
        // 谁在用它：只有本文件的 LockstepScheduler —— Submit 往里放，BuildAndStep 取完就整份丢掉。
        private sealed class PendingFrame
        {
            // 帧号（>= 0）；由 FindOrCreate 建好时填，之后不再改
            public int Frame;

            // 这一帧收到的输入，按到达顺序存；查找一律按玩家编号线性扫，所以存储顺序不影响结果
            private readonly List<FrameInput> m_inputs = new List<FrameInput>();

            // 功能：问这位玩家这一帧交过输入没有（交过 true）；转给 TryGet，取到的值丢掉。
            public bool Has(long playerId)
            {
                FrameInput ignored;
                return TryGet(playerId, out ignored);
            }

            // 功能：按玩家编号取这一帧的输入。
            // 入参：playerId 玩家编号；input 取到的输入（没取到时是 default，PlayerId 为 0）
            // 返回：有则 true 并填好 input；没有则 false，input 是 default
            // 步骤：
            //   1) 按下标线性扫，命中就拷出去 —— 帧内玩家只有几个，线性比字典少一次分配
            //   2) 扫完都没有才写 default，避免调用方读到半个值
            // 上游：Has、LockstepScheduler.BuildAndStep
            // 下游：无
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

            // 功能：往这一帧的尾部追加一条输入；调用方（Submit）已用 Has 挡住重复，这里不再判重。
            public void Add(FrameInput input)
            {
                m_inputs.Add(input);
            }
        }
    }
}
