// ============================================================================
//  ServerEventBridge —— 「服务端发来的事件」 → 「游戏里的事件中心」
//  项目：3D联网战斗Demo   对应：M4-S1（把联机战斗的结果接进任务系统）
//
//  ---------------------------------------------------------------------------
//  它补的是哪条断口（M3 收关时如实画出来的那条虚线）
//  ---------------------------------------------------------------------------
//  M3 结束时，两条链路是**分开**的：
//
//      ① 本地战斗：BattleWorld → EventCenter → ConditionEventBridge → ConditionTracker → QuestRuntime
//      ② 联机战斗：服务端 → NetSession（只显示，事件到此为止）        ← **断在这里**
//
//  于是"联机打死怪"**不会**推进任务。本类就是那座缺失的桥：
//
//      服务端 DamageEvent ─┐
//      服务端 DeathEvent  ─┼→ ServerEventBridge → EventCenter → ConditionEventBridge → ConditionTracker
//      服务端 DropEvent   ─┘                      （已经存在，一行都不用改）
//
//  ⚠️ **任务模块至今不认识网络**：它只认 `EventCenter` 里的事件。
//     这就是为什么接一条新链路不用改 `QuestRuntime` / `ConditionTracker` 一行 ——
//     和 `ConditionEventBridge` 文件头那条"生产事件的模块完全不知道任务的存在"是同一个设计。
//
//  ---------------------------------------------------------------------------
//  三条映射规则（以及为什么这样映射）
//  ---------------------------------------------------------------------------
//      DamageEvent → BattleEvents.DamageDealt   （每次扣血一条；飘字/统计用）
//      DeathEvent  → BattleEvents.MonsterDied   （kind = 1）
//                  → BattleEvents.HeroDied      （kind = 0）
//      DropEvent   → BattleEvents.ItemDropped   （**只有归我的**才发，见下）
//
//  ⚠️ **死亡按 kind 分派成两个事件**，不是"发一个带 kind 的、让订阅方自己判"——
//     理由见 `BattleEvents.cs` 文件头：合并会让"玩家死了"被当成"击杀了一个目标 0"，
//     而 0 = 任意目标 ⇒ **任何**击杀任务都会涨进度（静默、且看起来很正常）。
//
//  ⚠️ **掉落只在"归我"时发事件**：`DropEvent.WinnerPlayerId` 是服务端算的归属（M3 = 击杀者）。
//     别人的战利品如果也当成"我获得了"，"收集 N 个物品"的任务就会被别人的掉落推进 ——
//     同样是静默的错。归属判断放在这里（产生事件的地方），订阅方拿到就是"我的"。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 它为什么住在 `Game\Battle\`，而不是 `Game\Net\`
//  ---------------------------------------------------------------------------
//  因为它认识 `BattleEvents` 与 `EventCenter` —— 两者都要 `UnityEngine`
//  （`EventId` 里有一句 `Debug.LogWarning`、`EventCenter` 用 `UnityAction`）。
//  而 `Game\Net\` 是**引擎无关子集**：`Server\_net-probe`（.NET 8 控制台探针）
//  直接 `<Compile Include="..\..\Client\Assets\_Project\Game\Net\**\*.cs" />` 编它，
//  所以那一层**不能出现任何引擎依赖**，否则探针当场编不过（2026-09-26 实测踩到：
//  我给 `NetSession` 加了 `using NBC.Game.Battle;` 引用 `EBattleAgentKind`，探针立刻 CS0234）。
//  ⇒ 判据同 D14 那条：**判"能不能进服务端 / 能不能进探针"要看整条依赖链**。
//
//  ---------------------------------------------------------------------------
//  生命周期（⚠️ 订阅了就必须退订）
//  ---------------------------------------------------------------------------
//  它订阅 `NetSession` 的 C# 事件，所以**必须有人 `Dispose`** ——
//  这是 A4/A8 与 `ConditionEventBridge` 都踩过的同一个坑：
//  "订阅不退订 = 对象已经没人用了，回调还在跑"。
// ============================================================================

using System;
using NBC.Framework;
using NBC.Game.Net;
using NBC.Protocol;

namespace NBC.Game.Battle
{
    /// <summary>把服务端事件翻译成游戏事件（M4-S1）。</summary>
    public sealed class ServerEventBridge : IDisposable
    {
        /// <summary>网络会话（事件的来源）。</summary>
        private readonly NetSession m_session;

        /// <summary>已经 Dispose 过没有（重复 Dispose 必须无害）。</summary>
        private bool m_disposed;

        /// <summary>造一座桥（**构造即接通**：订阅开始生效）。</summary>
        /// <param name="session">网络会话（不能为 null）。</param>
        /// <exception cref="ArgumentNullException">会话为 null 时抛。</exception>
        public ServerEventBridge(NetSession session)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session),
                    "[ServerEventBridge] 必须给一个 NetSession（事件的来源）。");
            }

            m_session = session;

            session.DamageReceived += OnDamage;
            session.DeathReceived += OnDeath;
            session.DropReceived += OnDrop;
        }

        /// <summary>已经 Dispose 没有。</summary>
        public bool IsDisposed
        {
            get { return m_disposed; }
        }

        /// <summary>转发出去的伤害事件条数（调试/看板用）。</summary>
        public long HitsForwarded { get; private set; }

        /// <summary>转发出去的**怪物**死亡事件条数。</summary>
        public long MonstersDied { get; private set; }

        /// <summary>转发出去的**英雄**死亡事件条数。</summary>
        public long HeroesDied { get; private set; }

        /// <summary>转发出去的"归我"的掉落事件条数。</summary>
        public long DropsClaimed { get; private set; }

        /// <summary>收到但**不是归我**的掉落条数（发事件时被过滤掉；留着是为了"看得见"而不是静默丢）。</summary>
        public long DropsOfOthers { get; private set; }

        /// <summary>退订全部事件（可以重复调用）。</summary>
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;

            m_session.DamageReceived -= OnDamage;
            m_session.DeathReceived -= OnDeath;
            m_session.DropReceived -= OnDrop;
        }

        /// <summary>一条伤害 → `DamageDealt`。</summary>
        /// <param name="damage">服务端事件。</param>
        private void OnDamage(DamageEvent damage)
        {
            HitsForwarded++;

            EventCenter.Instance.Trigger(BattleEvents.DamageDealt,
                new DamageDealtPayload(damage.AttackerId, damage.TargetId, damage.Applied, damage.RemainingHp));
        }

        /// <summary>一条死亡 → 按 `kind` 分派成 `MonsterDied` / `HeroDied`。</summary>
        /// <param name="death">服务端事件。</param>
        private void OnDeath(DeathEvent death)
        {
            if (death.Kind == (int)EBattleAgentKind.Monster)
            {
                MonstersDied++;

                EventCenter.Instance.Trigger(BattleEvents.MonsterDied,
                    new MonsterDiedPayload(death.EntityId, death.ConfigId, death.KillerId));

                return;
            }

            HeroesDied++;

            EventCenter.Instance.Trigger(BattleEvents.HeroDied,
                new HeroDiedPayload(death.EntityId, death.ConfigId, death.KillerId));
        }

        /// <summary>一条掉落 → **归我**才发 `ItemDropped`。</summary>
        /// <param name="drop">服务端事件。</param>
        private void OnDrop(DropEvent drop)
        {
            if (drop.WinnerPlayerId != m_session.PlayerId)
            {
                // 别人的战利品：**不发事件**（理由见文件头），但记一笔，免得看起来像"漏了"
                DropsOfOthers++;
                return;
            }

            DropsClaimed++;

            EventCenter.Instance.Trigger(BattleEvents.ItemDropped,
                new ItemDroppedPayload(drop.ItemId, drop.Count));
        }
    }
}
