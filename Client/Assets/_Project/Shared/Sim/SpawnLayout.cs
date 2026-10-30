// 这个文件里有：
//   SpawnLayout（静态类）：固定的出生点表，按槽位序号给出出生点坐标与开局血量。
//   LockstepRoster（静态类）：把成员名单规范化，再生成锁步的开局实体表，顺序只由 id 决定。

using System.Collections.Generic;

namespace NBC.Shared.Sim
{
    // 主要功能：按槽位序号提供固定的出生点坐标与开局血量，两端取到的值必须完全一样。
    // 主要数据成员：MaxSlots（槽位上限，个）、SpawnHp（开局血量，点）、s_x 与 s_z（各槽位的 X 与 Z，米）。
    // 主要外部方法：PointAt（取第 n 个出生点）；其余成员都是常量。
    // 谁在用它：LockstepRoster.Build 按排序后的序号取点与血量；联调探针（Server\_net-probe）直接拿 PointAt 与 SpawnHp 断言开局表。
    // 注意：坐标是整数米（1 = 1 米），与 SimEntity.Position 同一单位；规则层与协议的距离用毫米，跨层时先除以 1000 换成米，别把毫米直接塞进来。
    /// <summary>固定的出生点表：坐标与开局血量，引擎无关，两端同值。</summary>
    public static class SpawnLayout
    {
        /// <summary>槽位上限，单位个；取值 4，必须与 NetContract.MaxRoomMembers 一致，改一边就得改另一边。</summary>
        public const int MaxSlots = 4;

        /// <summary>开局血量，单位点；取值 100，当前血与最大血都取它，两端必须是同一个数。</summary>
        public const int SpawnHp = 100;

        // 槽位的 X 坐标，单位米；下标即槽位序号 0..3，取值 { 0, 5, -5, 0 }，与 s_z 同序
        private static readonly int[] s_x = { 0, 5, -5, 0 };

        // 槽位的 Z 坐标，单位米；下标即槽位序号 0..3，取值 { 0, 0, 0, 5 }，与 s_x 同序
        private static readonly int[] s_z = { 0, 0, 0, 5 };

        /// <summary>取第 slot 个出生点（按序号取，与谁先入房无关）；槽位序号 0 起，负数与超上限会被钳进 0..MaxSlots-1。</summary>
        /// <returns>出生点，单位米（整数坐标，Y 恒为 0）。</returns>
        // 功能：按槽位序号取固定的出生点，Y 恒为 0。
        // 入参：slot 槽位序号（0 起；允许负数与超上限，本方法自己兜底，不抛异常）
        // 返回：出生点，单位米；同一 slot 在两端恒等
        // 步骤：
        //   1) 先把负数钳到 0 —— C# 里负数取模仍为负，直接取模会越界
        //   2) 再对 MaxSlots 取模 —— 人数超上限时回到第 0 个点，这是兜底而不是报错
        //   3) X 与 Z 用同一个下标取 —— 两张表一旦错位不会报错，只会把人生错地方
        // 上游：LockstepRoster.Build（按排序后的序号 i 逐个取）
        // 下游：s_x 与 s_z
        public static FixVector3 PointAt(int slot)
        {
            int index = slot < 0 ? 0 : slot % MaxSlots;
            return new FixVector3(s_x[index], 0, s_z[index]);
        }
    }

    // 主要功能：规范化房间成员名单，再把它变成锁步的开局实体表 —— 顺序只由 id 决定，与谁先入房、谁先发输入都无关。
    // 主要数据成员：无（纯函数，只有局部集合）。
    // 主要外部方法：Build（生成开局实体表）、SortIds（规范化成员名单）。
    // 谁在用它：服务端 RoomSetup 建房时调 Build，结果直接交给 LockstepService.StartRoom；MatchRules.cs 里的 LockstepTeams 调 SortIds 让队伍分配与实体表同一顺序；联调探针（Server\_net-probe）直接调 Build 与 PointAt 验开局表。
    // 注意：本类是纯函数 —— 不读时钟、不读全局、不查配置表；一读就会因为「什么时候调」不同而让两端算出不同结果。
    // 注意：之所以放共享层：开局哈希逐位依赖它，写在 Host 里只能跑真服务端才验得出来，探针驱动不了、也做不出变异。
    /// <summary>把房间成员名单变成锁步的初始实体表（纯函数，顺序只由 id 决定）。</summary>
    public static class LockstepRoster
    {
        /// <summary>生成锁步的开局实体表：按 player_id 升序分配出生点，非正数跳过、重号去重；成员名单为 null 时按空阵容处理。</summary>
        /// <returns>实体表，按 id 升序；空名单返回空表；Id 已收窄为 int，可直接交给 LockstepService.StartRoom。</returns>
        // 功能：把成员名单整理成升序去重的 id，再逐个生成带出生点与满血的实体。
        // 入参：members 成员编号（顺序无所谓，可以为 null = 空阵容；非正数跳过、重号去重）
        // 返回：实体表，按 id 升序；空名单返回空表而不是 null
        // 步骤：
        //   1) 非正数（游客 / 未登录）跳过 —— 他们没有玩家档案与实体，留着会让实体表多出 id 0
        //   2) 线性查重 —— 名单只有几个人，比 HashSet 更确定，也不受哈希布局影响
        //   3) 最后才排序 —— 前面两步都不依赖顺序，排序放末尾才能保证落点只由 id 决定
        //   4) 按下标 i 取第 i 个槽位 —— 所以谁先入房都不影响落点
        //   5) 按 id 顺序填满血与可攻击 tick —— 开局全员满血、随时可攻击
        // 注意：1) 到 3) 是 SortIds 的内联副本（本方法没调它）；改跳过 / 去重 / 升序任一规则时两处必须一起改 —— 队伍走 SortIds、实体表走这里，两处顺序一旦错开，第 0 帧哈希就分歧。
        // 注意：wire 上是 int64、SimEntity.Id 是 int，这里是显式收窄；本 Demo 的玩家编号远小于 int 上限，真要超了应当报错而不是静默截断。
        // 上游：服务端 RoomSetup（建房）、联调探针
        // 下游：SpawnLayout.PointAt、SpawnLayout.SpawnHp
        public static List<SimEntity> Build(IReadOnlyList<long> members)
        {
            var ids = new List<long>();

            if (members != null)
            {
                for (int i = 0; i < members.Count; i++)
                {
                    long id = members[i];

                    if (id <= 0)
                    {
                        continue;
                    }

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

            ids.Sort();

            var entities = new List<SimEntity>(ids.Count);

            for (int i = 0; i < ids.Count; i++)
            {
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

        /// <summary>规范化成员名单：非正数跳过、重号去重、按 id 升序；成员名单为 null 时按空名单处理。</summary>
        /// <returns>规范化后的编号表（升序、去重、全为正）；null 与空名单都返回空表。</returns>
        // 功能：把成员编号整理成升序、去重、全为正的名单，让队伍与实体表共用同一个顺序。
        // 入参：members 成员编号（顺序无所谓；可以为 null = 空名单）
        // 返回：一张新表，升序、去重、全为正；null 与空名单都返回空表
        // 步骤：
        //   1) 非正数（游客 / 未登录）跳过 —— 他们既没有玩家档案也没有实体
        //   2) 线性查重 —— 重号会让两端对「第二个同 id」的落点产生分歧
        //   3) 排序放在最后 —— 结果不许依赖调用方给的顺序
        // 注意：抽出来就是为了只此一份 —— 队伍分配（LockstepTeams）与实体表必须同一个顺序规则，各写一份就是两处会不一致的地方；Build 里目前是内联副本，改规则要一起改。
        // 注意：ids 已去重，所以 List.Sort 不稳定这件事不构成问题（不存在相同键）；一旦允许重号，就得自己写稳定排序。
        // 上游：MatchRules.cs 里的 LockstepTeams.BuildTeamIndex
        // 下游：无
        public static List<long> SortIds(IReadOnlyList<long> members)
        {
            var ids = new List<long>();

            if (members != null)
            {
                for (int i = 0; i < members.Count; i++)
                {
                    long id = members[i];

                    if (id <= 0)
                    {
                        continue;
                    }

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

            ids.Sort();
            return ids;
        }
    }
}
