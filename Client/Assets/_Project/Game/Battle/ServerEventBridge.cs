// 这个文件里有：
//   ServerEventBridge（类）：把服务端下发的伤害 / 死亡 / 掉落事件翻译成事件中心里的战斗事件，让联机战斗的结果能推进任务

using System;
using NBC.Framework;
using NBC.Game.Net;
using NBC.Protocol;

namespace NBC.Game.Battle
{
    // 主要功能：把服务端下发的伤害 / 死亡 / 掉落三类事件，翻译成事件中心里的 BattleEvents 事件，任务系统因此不用认识网络。
    // 主要数据成员：m_session（事件来源，也是判掉落归属的依据）、m_disposed（是否已退订）、五个转发计数（long，0 起，只增不减）。
    // 主要外部方法：Dispose（退订全部事件）、IsDisposed、HitsForwarded / MonstersDied / HeroesDied / DropsClaimed / DropsOfOthers（只读计数）。
    // 谁在用它：Editor 的 NetDebugWindow（每次连上重建一座、关窗口时 Dispose，并把计数打到看板）；EditMode 测试直接构造它核对映射。
    // 注意：任务模块至今不认识网络，只认 EventCenter 里的事件，所以接这条链路不用改 QuestRuntime / ConditionTracker 一行。
    // 注意：死亡按 kind 分派成 MonsterDied / HeroDied 两个事件，不能合成一个带 kind 的让订阅方自己判：合成后「玩家死了」会被当成「击杀了一个目标 0」，而 0 = 任意目标，于是任何击杀任务都会静默涨进度（理由见 BattleEvents.cs 文件头）。
    // 注意：掉落只在归属是本会话玩家时才发事件，归属判断放在产生事件的地方；别人的战利品若也算成「我获得了」，收集类任务同样会被静默推进。
    // 注意：本类住在 Battle 而不是 Net，因为它认识 BattleEvents 与 EventCenter（两者都要 UnityEngine），而 Game\Net 是引擎无关子集，Server\_net-probe 会直接把那一层源码编进 .NET 8 控制台探针，多一个引擎依赖探针就 CS0234 编不过 —— 判「能不能进探针」要看整条依赖链。
    // 注意：它订阅 NetSession 的 C# 事件，必须有人 Dispose；订阅不退订就是「对象已经没人用了，回调还在跑」，ConditionEventBridge 踩过同一个坑。
    /// <summary>把服务端事件翻译成事件中心里的游戏事件（伤害 / 死亡 / 掉落）；用完必须 Dispose 退订。</summary>
    public sealed class ServerEventBridge : IDisposable
    {
        // 服务端事件的来源，也是判掉落归属的依据（PlayerId）；构造函数挡过 null，之后不会为空
        private readonly NetSession m_session;

        // 是否已经 Dispose 过；初值 false，退订后恒为 true，重复 Dispose 因此无害
        private bool m_disposed;

        /// <summary>用给定会话造一座桥并立即订阅三个服务端事件；session 为 null 时抛 ArgumentNullException。</summary>
        public ServerEventBridge(NetSession session)
        {
            // 功能：保存会话并订阅伤害 / 死亡 / 掉落三个服务端事件，构造完就开始转发。
            // 入参：session 网络会话（事件的来源），不能为 null，否则抛 ArgumentNullException。
            // 步骤：
            //   1) 先挡 null：桥没有来源就什么都转发不了，早炸比运行期空引用好查
            //   2) 存进只读字段，之后不再换会话
            //   3) 一次订上三个事件，构造即接通，调用方不用再调一个 Start
            // 返回：无（构造函数；抛异常时对象不会建出来）。
            // 上游：Editor 的 NetDebugWindow，每次建立连接后重建一座。
            // 下游：NetSession.DamageReceived / DeathReceived / DropReceived。
            // 注意：订阅写在构造函数里，拿不到「半接通」的桥；代价是必须有人 Dispose，否则回调一直挂在会话上。
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

        // 功能：是否已 Dispose（三个服务端事件都已退订）；测试用它断言 Dispose 生效，重复调用不会把它变回 false。
        public bool IsDisposed
        {
            get { return m_disposed; }
        }

        /// <summary>转发出去的伤害事件条数。</summary>
        public long HitsForwarded { get; private set; }

        /// <summary>转发出去的怪物死亡事件条数。</summary>
        public long MonstersDied { get; private set; }

        /// <summary>转发出去的英雄死亡事件条数。</summary>
        public long HeroesDied { get; private set; }

        /// <summary>转发出去的归我的掉落事件条数。</summary>
        public long DropsClaimed { get; private set; }

        /// <summary>收到但不归我的掉落条数；单独记一笔，让过滤看得见而不是静默丢。</summary>
        public long DropsOfOthers { get; private set; }

        /// <summary>退订三个服务端事件并标记为已释放；可重复调用。</summary>
        public void Dispose()
        {
            // 功能：退订伤害 / 死亡 / 掉落三个服务端事件，让这座桥不再转发任何东西。
            // 入参：无。
            // 步骤：
            //   1) 已 Dispose 就直接返回，保证重复调用无害（关窗口与重连两条路径都会调）
            //   2) 先置位再退订：IsDisposed 立刻为 true，再次进来走上面的提前返回
            //   3) 逐个 -= 掉构造函数里订的三个回调，与订阅的三个一一对应
            // 返回：无。
            // 上游：Editor 的 NetDebugWindow，关闭窗口或重连时调用。
            // 下游：NetSession.DamageReceived / DeathReceived / DropReceived。
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;

            m_session.DamageReceived -= OnDamage;
            m_session.DeathReceived -= OnDeath;
            m_session.DropReceived -= OnDrop;
        }

        // 功能：把一条服务端伤害事件转成 BattleEvents.DamageDealt 发到事件中心。
        // 入参：damage 服务端的伤害事件（攻击者与目标的实例编号、本次实际扣血点数、剩余血量点数，都是服务端算好的值）。
        // 步骤：
        //   1) 先记数再转发：计数口径是「收到几条」，与下游有没有订、处理成不成功无关
        //   2) 原样透传四个字段发一次 DamageDealt，每次扣血一条，不合并（飘字与统计都靠这条）
        // 返回：无。
        // 上游：NetSession 收到 DamageEvent 时回调。
        // 下游：EventCenter.Trigger → DamageDealt 的订阅方（飘字 / 统计）。
        private void OnDamage(DamageEvent damage)
        {
            HitsForwarded++;

            EventCenter.Instance.Trigger(BattleEvents.DamageDealt,
                new DamageDealtPayload(damage.AttackerId, damage.TargetId, damage.Applied, damage.RemainingHp));
        }

        // 功能：按死亡事件的 kind 分派成 MonsterDied 或 HeroDied 发到事件中心，并各自计数。
        // 入参：death 服务端的死亡事件（kind 1 = 怪物、0 = 英雄；实体实例编号、配置编号、击杀者实例编号）。
        // 步骤：
        //   1) 先判 kind 是否等于 1（EBattleAgentKind.Monster），是就发 MonsterDied 并立刻 return，避免继续落到英雄分支重复发一条
        //   2) 不是 1 的按英雄发 HeroDied，两个事件各自计数，订阅方拿到就是明确的「怪死了」或「英雄死了」
        // 返回：无。
        // 上游：NetSession 收到 DeathEvent 时回调。
        // 下游：EventCenter.Trigger → MonsterDied / HeroDied 的订阅方（任务条件桥等）。
        // 注意：分派成两个事件而不是发一个带 kind 的让订阅方自己判，是为了让「玩家死了」不可能被当成「击杀目标 0」，理由见 BattleEvents.cs 文件头。
        // 注意：kind 不等于 1 的一律按英雄处理（服务端目前只发 0 / 1）；将来加第三方阵营要在这里补分支，否则会被当成英雄死亡。
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

        // 功能：只有归属是本会话玩家的掉落才转成 BattleEvents.ItemDropped，别人的只计数不发事件。
        // 入参：drop 服务端的掉落事件（物品编号、数量、服务端算好的归属玩家 id）。
        // 步骤：
        //   1) 先比归属：WinnerPlayerId 与 m_session.PlayerId 不等就是别人的战利品，只加 DropsOfOthers 后返回，不发事件也不加 DropsClaimed
        //   2) 相等才加 DropsClaimed，再发 ItemDropped（物品编号 + 数量）
        // 返回：无。
        // 上游：NetSession 收到 DropEvent 时回调。
        // 下游：EventCenter.Trigger → ItemDropped 的订阅方（收集类任务条件）。
        // 注意：归属判断放在这里而不是交给订阅方，订阅方拿到的就一定是「我的」；别人的掉落如果也发出去，收集 N 个的任务会被别人的战利品静默推进。
        // 注意：DropsOfOthers 是故意留的「看得见」计数，过滤掉不等于静默丢，看板上能看出收到了多少条不归我的掉落。
        private void OnDrop(DropEvent drop)
        {
            if (drop.WinnerPlayerId != m_session.PlayerId)
            {
                DropsOfOthers++;
                return;
            }

            DropsClaimed++;

            EventCenter.Instance.Trigger(BattleEvents.ItemDropped,
                new ItemDroppedPayload(drop.ItemId, drop.Count));
        }
    }
}
