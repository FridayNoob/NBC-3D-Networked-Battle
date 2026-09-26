// ============================================================================
//  NBC.Server.Game —— BOSS 大脑（**用 A10 的状态机驱动**，M3-S6b）
//  项目：3D联网战斗Demo
//  对应：需求 §9.2（怪物 AI）、`Docs\00` 例外 E5 / `Docs\10-M0` 架构裁决 A1~A3、Docs\25 §四 S6b
//
//  ---------------------------------------------------------------------------
//  一、这是"**服务端跑 AI**"那半边的样本（另一半在 M4）
//  ---------------------------------------------------------------------------
//  `Docs\10-M0` 的架构裁决写着 **"AI 分两套实现"**：
//      · **权威 AI**（决定血量/位置/掉落的）→ 必须在**服务端**跑 → 只能纯 C#（这就是本文件）
//      · **表现/客户端 AI**（巡逻表演、演出）→ 可以在客户端用插件（行为树等）
//  D14 实验已经**实测**过：依赖引擎对象的东西（Behavior Designer / NavMesh）一碰就抛 `ECall`，
//  进不了服务端 —— 所以权威 AI 不是"想不想用插件"的问题，是**用不了**。
//
//  ---------------------------------------------------------------------------
//  二、状态机是 A10 那一份（**同一份源码**），不是这里另写一个
//  ---------------------------------------------------------------------------
//  `NBC.Framework.Fsm.StateMachine<S>` 由 `NBC.Server.Game.csproj` 用 `<Compile Include>` 直接编进来。
//  ⚠️ 顺带发现了 `Docs\25` §二 那句话**只对了一半**（已回填）：
//     状态机自己那 5 个文件是纯 C#，但它通过 `StateBase.OnEvent(EventId)` 引的 **`EventId` 不是**
//     （那句 `Debug.LogWarning`）；靠 `UnityEngineDebugShim.cs` 垫过去。
//     ⇒ 判据：**判"能不能进服务端"要看整条依赖链**（D14 结论的第二层）。
//
//  ---------------------------------------------------------------------------
//  三、状态机的时间是**逻辑帧号**（A10 的硬要求）
//  ---------------------------------------------------------------------------
//  `_fsm.Tick(frame)` 里的 frame 传的是**世界的逻辑帧号**（`DungeonBattle.Tick`），
//  **不是** `Time.deltaTime`、也不是真实毫秒 —— 这样 AI 的行为是**可复现**的
//  （同样的输入序列 → 同样的结果），M4 做帧同步时这条是前提。
//
//  ---------------------------------------------------------------------------
//  四、状态里只做"判定 + 调用"，世界改动一律回到 `DungeonBattle`
//  ---------------------------------------------------------------------------
//  状态类里**不许**直接改 `Hp` / 直接写坐标 —— 只能调 `battle.ApplyMove(...)` / `battle.TryBasicAttack(...)`。
//  判据与 M2-C 的装配层一致：**规则一处实现**（否则"BOSS 打人"和"英雄打人"会各有一套数值口径）。
//
//  ---------------------------------------------------------------------------
//  五、决策表（M3 的全部 AI 就这四条，故意做小）
//  ---------------------------------------------------------------------------
//      **仇恨范围外没有目标**    → **Idle**（站着；离家了就**走回出生点**＝leash）
//      没有活着的英雄          → **Idle**（站着）
//      有英雄但超出普攻射程    → **Chase**（朝最近的英雄走）
//      在射程内                → **Attack**（普攻；冷却中会被 `TryBasicAttack` 拒掉，状态不用管）
//
//  ⚠️ **仇恨范围**（`DungeonBattle.BossAggroRangeMm` = 2200mm）是 2026-09-26 补的，
//     起因是负责人实测"老是被 BOSS 打死、没法测"：原来这里无脑取"最近的活英雄"，
//     BOSS 会**全图追人**（90mm/帧）并以 60 点/0.5 秒把 1200 血的英雄 10 秒磨死，
//     ⇒ "先清小怪再打 BOSS"这条最基本的副本打法**根本不存在**。
//     现在超出范围就丢目标 → 回 Idle → 走回出生点，交不交战由玩家决定。
//     ⚠️ TODO(M4+)：BOSS 技能（`Skill` 表已有 damage / hitFrames）、狂暴阶段、仇恨切换**还没做**。
// ============================================================================

using NBC.Framework.Fsm;

namespace NBC.Server.Game;

/// <summary>BOSS 的状态（就三个，故意做小）。</summary>
public enum EBossState
{
    /// <summary>没有目标：站着。</summary>
    Idle = 0,

    /// <summary>追最近的英雄。</summary>
    Chase = 1,

    /// <summary>够得着：打。</summary>
    Attack = 2,
}

/// <summary>BOSS 大脑：用 A10 的状态机每帧做决策（见文件头第五节）。</summary>
public sealed class BossBrain
{
    private readonly StateMachine<EBossState> _fsm = new StateMachine<EBossState>();
    private readonly BattleEntity _boss;

    /// <summary>当前世界（`OnUpdate` 里要用；每帧由 `Think` 塞进来，所以进来前可能为 null）。</summary>
    private DungeonBattle? _battle;

    /// <summary>当前目标（每帧重算：**打最近的活英雄**；没有目标是合法的，所以可为 null）。</summary>
    private BattleEntity? _target;

    /// <summary>建一个大脑（并把三个状态注册好、直接开跑）。</summary>
    /// <param name="boss">它驱动的那个单位。</param>
    public BossBrain(BattleEntity boss)
    {
        _boss = boss;

        // 出生点 = 它的"家"（leash 的目标点）。构造时抓一次就够 —— 之后它自己会走。
        HomeXmm = boss.PosXmm;
        HomeZmm = boss.PosZmm;

        _fsm.Register(EBossState.Idle, new IdleState(this));
        _fsm.Register(EBossState.Chase, new ChaseState(this));
        _fsm.Register(EBossState.Attack, new AttackState(this));
        _fsm.Start(EBossState.Idle);
    }

    /// <summary>它的"家"（出生点）X —— 脱离仇恨后走回这里。</summary>
    public int HomeXmm { get; }

    /// <summary>它的"家"（出生点）Z。</summary>
    public int HomeZmm { get; }

    /// <summary>它驱动的单位。</summary>
    public BattleEntity Boss => _boss;

    /// <summary>当前状态。</summary>
    public EBossState State => _fsm.Current;

    /// <summary>当前状态名（日志/调试面板直接用）。</summary>
    public string StateName => _fsm.CurrentName;

    /// <summary>已经切换过多少次状态（排查"AI 抖不抖"用）。</summary>
    public int TransitionCount { get; private set; }

    /// <summary>
    /// 想一帧：更新目标 → 推进状态机（决策与动作都在状态的 `OnUpdate` 里）。
    /// <para>⚠️ **目标在仇恨范围外时视为没有目标**（`_target = null`）—— 这就是 leash 的实现方式：
    /// 状态机那边只认"有没有目标"，于是 Chase/Attack 会自动退回 Idle 并**走回出生点**
    /// （见 `DungeonBattle.BossAggroRangeMm` 的注释：没有它 BOSS 会全图追人，副本没法玩）。</para>
    /// </summary>
    /// <param name="battle">世界。</param>
    public void Think(DungeonBattle battle)
    {
        _battle = battle;

        BattleEntity? nearest = battle.NearestAliveHero(_boss);

        if (nearest != null
            && DungeonBattle.DistanceMm(_boss, nearest) > DungeonBattle.BossAggroRangeMm)
        {
            nearest = null;     // 够不着 → 当没看见（而不是"永远锁定"）
        }

        _target = nearest;

        // ⚠️ 帧号用**逻辑帧**（`DungeonBattle.Tick`），不是真实时间 —— 见文件头第三节
        int frame = (int)battle.Tick;

        _fsm.Tick(frame);
    }

    /// <summary>
    /// 没目标时：离家超过松弛量就**走回出生点**，已经在家/在松弛范围内就站着（免得原地抖）。
    /// </summary>
    internal void ReturnHome()
    {
        DungeonBattle? battle = _battle;

        if (battle == null)
        {
            return;
        }

        if (DungeonBattle.DistanceMmToPoint(_boss, HomeXmm, HomeZmm) <= DungeonBattle.BossLeashSlackMm)
        {
            return;
        }

        battle.MoveTowardsPoint(_boss, HomeXmm, HomeZmm);
    }

    /// <summary>记一次状态切换（状态入口处调，便于排查抖动）。</summary>
    internal void NoteTransition()
    {
        TransitionCount++;
    }

    /// <summary>当前目标（可能为 null）。</summary>
    internal BattleEntity? Target => _target;

    /// <summary>当前世界（`Think` 之前为 null）。</summary>
    internal DungeonBattle? Battle => _battle;

    /// <summary>站着不动（没有目标时）——**离家了就回家**。</summary>
    private sealed class IdleState : StateBase<EBossState>
    {
        private readonly BossBrain _brain;

        /// <summary>建状态。</summary>
        /// <param name="brain">大脑。</param>
        public IdleState(BossBrain brain)
        {
            _brain = brain;
        }

        /// <summary>名字（调试面板用）。</summary>
        public override string Name => "站桩";

        /// <summary>每帧：有目标（且在仇恨范围内）就转去追；没目标就先回出生点。</summary>
        /// <param name="tick">帧信息。</param>
        public override void OnUpdate(in StateTick tick)
        {
            if (_brain.Target != null)
            {
                _brain.NoteTransition();
                _brain._fsm.TryChangeState(EBossState.Chase);
                return;
            }

            _brain.ReturnHome();
        }
    }

    /// <summary>追最近的英雄。</summary>
    private sealed class ChaseState : StateBase<EBossState>
    {
        private readonly BossBrain _brain;

        /// <summary>建状态。</summary>
        /// <param name="brain">大脑。</param>
        public ChaseState(BossBrain brain)
        {
            _brain = brain;
        }

        /// <summary>名字。</summary>
        public override string Name => "追击";

        /// <summary>每帧：没目标就站桩；够得着就打；否则朝目标走一格。</summary>
        /// <param name="tick">帧信息。</param>
        public override void OnUpdate(in StateTick tick)
        {
            BattleEntity? target = _brain.Target;
            DungeonBattle? battle = _brain.Battle;

            if (target == null || battle == null)
            {
                _brain._fsm.TryChangeState(EBossState.Idle);
                return;
            }

            if (battle.InAttackRange(_brain.Boss, target))
            {
                _brain.NoteTransition();
                _brain._fsm.TryChangeState(EBossState.Attack);
                return;
            }

            battle.MoveTowards(_brain.Boss, target);
        }
    }

    /// <summary>够得着就打。</summary>
    private sealed class AttackState : StateBase<EBossState>
    {
        private readonly BossBrain _brain;

        /// <summary>上一次被拒的原因（排查用；成功时是空串）。</summary>
        public string LastRefusalReason { get; private set; } = string.Empty;

        /// <summary>建状态。</summary>
        /// <param name="brain">大脑。</param>
        public AttackState(BossBrain brain)
        {
            _brain = brain;
        }

        /// <summary>名字。</summary>
        public override string Name => "攻击";

        /// <summary>进入时打第一下（"够着了就打"，不用等下一帧）。</summary>
        /// <param name="previous">来源状态。</param>
        public override void OnEnter(EBossState previous)
        {
            Strike();
        }

        /// <summary>每帧：没目标就站桩；目标跑出射程就继续追；否则接着打（冷却会被拒）。</summary>
        /// <param name="tick">帧信息。</param>
        public override void OnUpdate(in StateTick tick)
        {
            BattleEntity? target = _brain.Target;
            DungeonBattle? battle = _brain.Battle;

            if (target == null || battle == null)
            {
                _brain._fsm.TryChangeState(EBossState.Idle);
                return;
            }

            if (!battle.InAttackRange(_brain.Boss, target))
            {
                _brain.NoteTransition();
                _brain._fsm.TryChangeState(EBossState.Chase);
                return;
            }

            Strike();
        }

        /// <summary>打一下（判定与数值全在 `DungeonBattle` 里，见文件头第四节）。</summary>
        private void Strike()
        {
            BattleEntity? target = _brain.Target;
            DungeonBattle? battle = _brain.Battle;

            if (target == null || battle == null)
            {
                return;
            }

            // 判定与数值全在 `DungeonBattle` 里；被拒（冷却/射程）不影响状态机怎么走
            string reason;
            battle.TryBasicAttack(_brain.Boss, target.Id, out reason);
            LastRefusalReason = reason;
        }
    }
}
