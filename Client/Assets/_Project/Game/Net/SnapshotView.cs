// ============================================================================
//  SnapshotView —— 客户端"按快照更新"的那一层（**纯 C#，不认识网络也不认识 Unity**）
//  项目：3D联网战斗Demo   对应：Docs\25 §四 S5、§三 D3/D4
//
//  ---------------------------------------------------------------------------
//  一、它是什么：客户端这边**唯一**的世界状态来源
//  ---------------------------------------------------------------------------
//  服务端权威（D3）落到客户端就是一句话：**客户端没有"自己那份血条"**。
//  它只有一个 `SnapshotView`，内容完全由服务端发来的快照决定：
//
//      服务端  DungeonBattle（真世界）──每 tick 一张全量快照──▶  客户端  SnapshotView（副本）
//
//  于是"客户端表现与服务端日志一致"这件事，就变成"**这个类有没有把快照照实存下来**" ——
//  可以被 EditMode 用例直接钉住（不需要开 Unity、不需要网络）。
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 为什么要有"旧快照直接丢掉"这条（TCP 明明是有序的）
//  ---------------------------------------------------------------------------
//  老实说：TCP 保证有序，所以**正常情况下这条分支永远走不到**。
//  那为什么还留着？因为它防的是**换了一条连接**的场景：
//  重连之后、或者对着另一个服务端进程，旧连接上迟到的快照可能比新连接的还新地到达调用方；
//  不丢的话，画面会**倒退**一下（怪物瞬移回去）。这种"偶发倒退"极难复现、极难查。
//
//  📌 但本项目刚立过一条规矩："**走不到的分支比没有分支更糟**"。
//     所以这里的处理方式不是"留一个没人测的分支"，而是：
//     **它必须能被测** —— `SnapshotViewTests` 里有一条专门喂一张旧快照，
//     断言 `StaleIgnored` 涨了、世界**没变**。能测 + 有价值，才允许存在。
//
//  ---------------------------------------------------------------------------
//  三、它不做什么（写清楚，免得被当成"没做完"）
//  ---------------------------------------------------------------------------
//  · **不插值、不预测**：M3 有意不做（D4）。所以 30Hz 快照直接套用，画面按 30Hz 跳。
//  · **不建场景物体**：把"数据"变成"GameObject"是表现层的事（S9 接），
//    这一层只保证"数据是对的"。分开之后，联机正确性与画面表现可以各自验证。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Protocol;

namespace NBC.Game.Net
{
    /// <summary>客户端的世界状态：只由服务端快照决定（见文件头）。</summary>
    public sealed class SnapshotView
    {
        /// <summary>当前世界的单位（顺序跟服务端快照一致）。</summary>
        private readonly List<EntitySnapshot> m_entities = new List<EntitySnapshot>();

        /// <summary>服务端帧号（-1 = 还没收到过任何快照）。</summary>
        public long ServerTick { get; private set; } = -1;

        /// <summary>收到并采纳了多少张快照。</summary>
        public long Applied { get; private set; }

        /// <summary>被丢掉的"旧快照"张数（见文件头第二节）。</summary>
        public long StaleIgnored { get; private set; }

        /// <summary>当前世界的单位（只读）。</summary>
        public IReadOnlyList<EntitySnapshot> Entities
        {
            get { return m_entities; }
        }

        /// <summary>单位数。</summary>
        public int EntityCount
        {
            get { return m_entities.Count; }
        }

        /// <summary>还活着的单位数。</summary>
        public int AliveCount
        {
            get
            {
                int count = 0;

                for (int i = 0; i < m_entities.Count; i++)
                {
                    if (m_entities[i].Alive)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>收到过快照吗。</summary>
        public bool HasWorld
        {
            get { return ServerTick >= 0; }
        }

        /// <summary>采纳了一张快照（世界变了）。</summary>
        public event Action<WorldSnapshot> Updated;

        /// <summary>
        /// 应用一张服务端快照。
        /// </summary>
        /// <param name="snapshot">快照（null 会被忽略 —— 一条 0 长度帧解出来就是 null）。</param>
        /// <returns>采纳了返回 true；因为是旧快照被丢掉返回 false。</returns>
        public bool Apply(WorldSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return false;
            }

            if (snapshot.ServerTick <= ServerTick)
            {
                // 见文件头第二节：不丢的话画面会倒退
                StaleIgnored++;
                return false;
            }

            m_entities.Clear();

            for (int i = 0; i < snapshot.Entities.Count; i++)
            {
                m_entities.Add(snapshot.Entities[i]);
            }

            ServerTick = snapshot.ServerTick;
            Applied++;

            Action<WorldSnapshot> handler = Updated;

            if (handler != null)
            {
                handler(snapshot);
            }

            return true;
        }

        /// <summary>按实例编号找单位（快照里的那一条）。</summary>
        /// <param name="entityId">实例编号。</param>
        /// <returns>快照条目或 null。</returns>
        public EntitySnapshot Find(int entityId)
        {
            for (int i = 0; i < m_entities.Count; i++)
            {
                if (m_entities[i].EntityId == entityId)
                {
                    return m_entities[i];
                }
            }

            return null;
        }

        /// <summary>清空（断开/重连时用）。</summary>
        public void Clear()
        {
            m_entities.Clear();
            ServerTick = -1;
        }

        /// <summary>一句人话（编辑器窗口/日志直接用）。</summary>
        /// <returns>描述。</returns>
        public string Describe()
        {
            if (!HasWorld)
            {
                return "还没有世界（没收到过快照）";
            }

            return "帧 " + ServerTick + "｜" + AliveCount + "/" + EntityCount + " 个活着";
        }
    }
}
