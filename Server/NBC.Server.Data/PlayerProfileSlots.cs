// ============================================================================
//  PlayerProfileSlots —— `IPlayerSlotProvider` 的实现（**把 1~4 当"槽位"复用**）
//  项目：3D联网战斗Demo   对应：SRV-06 的最小可用版、`Docs\27` §14.2
//
//  ---------------------------------------------------------------------------
//  它在做什么（一句话）
//  ---------------------------------------------------------------------------
//      启动时：把 `player_profile.player_id` 全部读进内存
//      握手时：**领一个最小的空闲 id**（不是"一路往上加"）
//      断开时：**还回去**
//
//  ⇒ 于是反复连接会复用 1~4，而不会涨到 5、6、7……
//     （涨上去的后果见 `IPlayerSlotProvider` 文件头：战绩明细会被外键拒绝、**静默跳过**。）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么"领最小的空闲"而不是"随便挑一个空闲"
//  ---------------------------------------------------------------------------
//  对外表现（哪个人是"玩家 1"）在**同一个场景下要可复现**：
//  调试时"我先进去，所以我总是玩家 1"，比"每次随机"省掉一整类困惑。
//  ⇒ 确定性优先（同 D2 那条：能确定的就别交给随机）。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 构造时吃一个"id 列表"而不是直接吃 DAO
//  ---------------------------------------------------------------------------
//  ⇒ 于是这个类**不需要数据库就能测**（`_db-probe` 里那几条逻辑用例）。
//     "从库里读 id"是另一件事（`LoadAsync`），由 `PlayerProfileDao` 负责。
//     分工和 `CachingConditionProgressStore` / `IConditionProgressDao` 一样：
//     **接缝验逻辑，探针验 SQL。**
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NBC.Server.Core;

namespace NBC.Server.Data
{
    /// <summary>把玩家档案当"槽位"分配的提供者（见文件头）。</summary>
    public sealed class PlayerProfileSlots : IPlayerSlotProvider
    {
        /// <summary>全部槽位（升序）。</summary>
        private readonly List<long> m_all = new List<long>();

        /// <summary>空闲的槽位（升序；领的时候取第一个）。</summary>
        private readonly List<long> m_free = new List<long>();

        /// <summary>已经发出去、还没还回来的（用来忽略"还一个不是我们发的 id"）。</summary>
        private readonly HashSet<long> m_claimed = new HashSet<long>();

        /// <summary>保护上面三个集合（握手与断开可能不在同一线程）。</summary>
        private readonly object m_gate = new object();

        /// <summary>累计发放过几次（**统计用**：它比"当前占用"更能说明"复用有没有生效"）。</summary>
        private int m_claimCount;

        /// <summary>累计"没有空闲槽位"几次（> 0 说明并发人数超过了档案数）。</summary>
        private int m_exhaustedCount;

        /// <summary>
        /// 用一份玩家编号列表造一个槽位池。
        /// </summary>
        /// <param name="profileIds">
        /// `player_profile.player_id` 列表（**可以为 null / 空** —— 那表示"没有库"，
        /// 于是每次 `TryClaim` 都返回 false，调用方降级）。
        /// </param>
        public PlayerProfileSlots(IReadOnlyList<long>? profileIds)
        {
            if (profileIds != null)
            {
                for (int i = 0; i < profileIds.Count; i++)
                {
                    long id = profileIds[i];

                    if (id > 0 && !m_all.Contains(id))
                    {
                        m_all.Add(id);
                    }
                }
            }

            m_all.Sort();
            m_free.AddRange(m_all);
        }

        /// <summary>一共有几个槽位（= 有几个玩家档案）。</summary>
        public int SlotCount
        {
            get { return m_all.Count; }
        }

        /// <summary>当前占用几个。</summary>
        public int BusyCount
        {
            get
            {
                lock (m_gate)
                {
                    return m_claimed.Count;
                }
            }
        }

        /// <summary>当前空闲几个。</summary>
        public int FreeCount
        {
            get
            {
                lock (m_gate)
                {
                    return m_free.Count;
                }
            }
        }

        /// <summary>累计发放过几次。</summary>
        public int ClaimCount
        {
            get { return m_claimCount; }
        }

        /// <summary>累计"没有空闲槽位"几次。</summary>
        public int ExhaustedCount
        {
            get { return m_exhaustedCount; }
        }

        /// <summary>领一个最小的空闲槽位。</summary>
        /// <param name="playerId">领到的玩家编号。</param>
        /// <returns>领到了返回 true（没有空闲 ⇒ false，调用方降级）。</returns>
        public bool TryClaim(out long playerId)
        {
            lock (m_gate)
            {
                if (m_free.Count == 0)
                {
                    m_exhaustedCount++;
                    playerId = 0;
                    return false;
                }

                // 取最小的：确定性优先（见文件头）
                playerId = m_free[0];
                m_free.RemoveAt(0);
                m_claimed.Add(playerId);
                m_claimCount++;
                return true;
            }
        }

        /// <summary>还一个槽位（**不是这里发出去的 id 要无害**）。</summary>
        /// <param name="playerId">玩家编号。</param>
        public void Release(long playerId)
        {
            lock (m_gate)
            {
                // ⚠️ 只还"我们发出去过的"：传进来一个 0（还没握手就断开）或者一个
                //    降级路径发的编号时，**不能**把它塞进空闲表 —— 那会让
                //    一个不存在的 id 被后面的握手领走（又变成外键拒绝）。
                if (!m_claimed.Remove(playerId))
                {
                    return;
                }

                m_free.Add(playerId);
                m_free.Sort();      // 保持升序：下一次领到的仍是最小的
            }
        }

        /// <summary>一句人话。</summary>
        /// <returns>例如 `档案槽位 4 个（占用 1、空闲 3；累计发放 6 次）`。</returns>
        public string Describe()
        {
            if (m_all.Count == 0)
            {
                return "档案槽位：**没有**（没接数据库 ⇒ 玩家编号退回「第几个连上来的」计数）";
            }

            var sb = new StringBuilder();
            sb.Append("档案槽位 ").Append(m_all.Count.ToString(CultureInfo.InvariantCulture))
              .Append(" 个（占用 ").Append(BusyCount.ToString(CultureInfo.InvariantCulture))
              .Append("、空闲 ").Append(FreeCount.ToString(CultureInfo.InvariantCulture))
              .Append("；累计发放 ").Append(m_claimCount.ToString(CultureInfo.InvariantCulture)).Append(" 次");

            if (m_exhaustedCount > 0)
            {
                sb.Append("；**没有空闲 ").Append(m_exhaustedCount.ToString(CultureInfo.InvariantCulture))
                  .Append(" 次**");
            }

            sb.Append('）');
            return sb.ToString();
        }
    }
}
