// ============================================================================
//  SpawnLayout / LockstepRoster —— 锁步的**开局实体表**（M4-S4 S4-c 收尾）
//  项目：3D联网战斗Demo   对应：`Docs\27` §三十三 的 33.3
//
//  ---------------------------------------------------------------------------
//  一、⚠️ 为什么这两样必须放共享层（而不是写在 Host 里）
//  ---------------------------------------------------------------------------
//  "从成员表生成开局实体表"这件事**必须逐位可复现**（开局哈希靠它），
//  而只要它写在 `Program.cs` 里，就**只能靠跑真服务端验** ⇒ 验不动、也做不出变异。
//  放这里（只依赖 BCL + `Shared\Sim`）⇒ 探针能直接驱动、能改坏、能当场红。
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 三条确定性要求（本文件存在的全部理由）
//  ---------------------------------------------------------------------------
//  ① **按 `player_id` 升序**生成：与"谁先入房 / 谁先发输入"**无关**（§29.2 判据②）。
//  ② **出生点按排序后的序号分配**（槽位 0,1,2…），不是"按到达顺序"。
//  ③ **成员表里有重号 / 非正数时要有明确处理**：非正数（游客/未登录）**跳过**，
//     重号**去重** —— 否则同一局里会出现两个同 id 实体，而哈希按 id 排序后
//     两端的"第二个同 id"落点不同 ⇒ 第 0 帧就分歧。
//
//  ---------------------------------------------------------------------------
//  三、⚠️ 现在**不碰配置表**
//  ---------------------------------------------------------------------------
//  "按地图/房间配置定阵容与出生点"留给 S4-d。那时只需把 `SpawnLayout` 换成"读配置"，
//  **锁步这条链一行都不用动**（因为契约只是"给我一张实体表"）。
// ============================================================================

using System.Collections.Generic;

namespace NBC.Shared.Sim
{
    /// <summary>固定的出生点表（**整数米**，引擎无关）。</summary>
    public static class SpawnLayout
    {
        /// <summary>最多几个槽位（与 `NetContract.MaxRoomMembers` 一致）。</summary>
        public const int MaxSlots = 4;

        /// <summary>开局血量（共享常量：两端必须同一个值）。</summary>
        public const int SpawnHp = 100;

        /// <summary>槽位的 X（米）。</summary>
        private static readonly int[] s_x = { 0, 5, -5, 0 };

        /// <summary>槽位的 Z（米）。</summary>
        private static readonly int[] s_z = { 0, 0, 0, 5 };

        /// <summary>取第 `slot` 个出生点（**按序号**取，不按到达顺序）。</summary>
        /// <param name="slot">槽位（0 起；超出范围时**取模**而不是抛 —— 房间人数有上限，取模是兜底）。</param>
        /// <returns>出生点（米）。</returns>
        public static FixVector3 PointAt(int slot)
        {
            int index = slot < 0 ? 0 : slot % MaxSlots;
            return new FixVector3(s_x[index], 0, s_z[index]);
        }
    }

    /// <summary>把"房间里的成员"变成"锁步的初始实体表"（**纯函数**）。</summary>
    public static class LockstepRoster
    {
        /// <summary>
        /// 生成开局实体表。
        /// <para>⚠️ 见文件头第二节：**按 `player_id` 升序**、出生点按序号、非正数跳过、重号去重。</para>
        /// </summary>
        /// <param name="members">成员编号（**顺序无所谓**；可以为 null = 空阵容）。</param>
        /// <returns>实体表（按 `id` 升序；可直接喂给 `LockstepService.StartRoom`）。</returns>
        public static List<SimEntity> Build(IReadOnlyList<long> members)
        {
            var ids = new List<long>();

            if (members != null)
            {
                for (int i = 0; i < members.Count; i++)
                {
                    long id = members[i];

                    // ① 非正数（游客 / 未登录）**跳过**：他们没有玩家档案，也没有实体
                    if (id <= 0)
                    {
                        continue;
                    }

                    // ② **去重**（重号会让两端对"第二个同 id"的落点产生分歧）
                    bool seen = false;

                    for (int k = 0; k < ids.Count; k++)
                    {
                        if (ids[k] == id)
                        {
                            seen = true;
                            break;
                        }
                    }

                    if (!seen)
                    {
                        ids.Add(id);
                    }
                }
            }

            // ③ **按 id 升序**（不许依赖调用方给的顺序）—— 判据②的落点
            ids.Sort();

            var entities = new List<SimEntity>(ids.Count);

            for (int i = 0; i < ids.Count; i++)
            {
                // ⚠️ wire 上是 int64，内存里 `SimEntity.Id` 是 int ⇒ **显式**转换并说明：
                //    本 Demo 的玩家编号远小于 int 上限；真要超了，这里应当**报错**而不是静默窄化。
                SimEntity entity;
                entity.Id = (int)ids[i];
                entity.Position = SpawnLayout.PointAt(i);
                entity.Hp = SpawnLayout.SpawnHp;
                entity.MaxHp = SpawnLayout.SpawnHp;
                entity.AttackReadyTick = 0;
                entities.Add(entity);
            }

            return entities;
        }

        /// <summary>
        /// **规范化成员名单**：非正数**跳过**、重号**去重**、**按 id 升序**。
        /// <para>⚠️ 抽出来是为了**只此一份**：队伍分配（`LockstepTeams`）与实体表（`Build`）
        /// 必须用**同一个**顺序规则 —— 各写一份就是两处会不一致的地方。</para>
        /// </summary>
        /// <param name="members">成员编号（顺序无所谓；可以为 null = 空）。</param>
        /// <returns>规范化后的编号表（升序、去重、全为正）。</returns>
        public static List<long> SortIds(IReadOnlyList<long> members)
        {
            var ids = new List<long>();

            if (members != null)
            {
                for (int i = 0; i < members.Count; i++)
                {
                    long id = members[i];

                    // ① 非正数（游客 / 未登录）**跳过**
                    if (id <= 0)
                    {
                        continue;
                    }

                    // ② **去重**
                    bool seen = false;

                    for (int k = 0; k < ids.Count; k++)
                    {
                        if (ids[k] == id)
                        {
                            seen = true;
                            break;
                        }
                    }

                    if (!seen)
                    {
                        ids.Add(id);
                    }
                }
            }

            // ③ **按 id 升序**（不许依赖调用方给的顺序）
            ids.Sort();
            return ids;
        }
    }
}
