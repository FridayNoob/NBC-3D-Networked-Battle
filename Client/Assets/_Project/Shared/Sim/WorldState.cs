// 这个文件里有：
//   SimEntity（结构体）：帧同步世界里的一个实体，只有整数与定点字段，没有引用类型。
//   WorldState（类）：一局的世界状态，确定性实体集合加整数 tick 时钟。

using System.Collections.Generic;
using NBC.Shared.Battle;

namespace NBC.Shared.Sim
{
    // 主要功能：帧同步世界里一个实体的纯数据载体 —— 编号、位置、血量、可攻击帧，全部是整数与定点。
    // 主要数据成员：Id（唯一键，个）、Position（位置，米）、Hp 与 MaxHp（血，点）、AttackReadyTick（帧号，tick）。
    // 主要外部方法：无（只有字段，读写由 WorldState 与 NBC.Game 的转换代码负责）。
    // 谁在用它：WorldState 存它；NBC.Game 的 LockstepWire 从协议填它，LockstepSnapshotAdapter 读它转成表现快照。
    // 注意：字段清单与喂哈希的顺序是契约 —— WorldStateHash 逐字段混入，加字段必须同步改哈希，否则状态变了哈希没变、分歧会被静默漏掉。
    /// <summary>帧同步世界里的一个实体：编号、位置、血量与可攻击帧，全是整数与定点。</summary>
    public struct SimEntity
    {
        /// <summary>实体编号，唯一键（排序、查找、哈希都靠它）；取值任意 int，唯一性由调用方保证，协议里是 int64，进来时显式收窄成 int。</summary>
        public int Id;

        /// <summary>位置，单位米（FixVector3 定点，1 = 1 米）；协议与战斗规则给的是毫米，跨层要先除以 1000。</summary>
        public FixVector3 Position;

        /// <summary>当前血量，单位点；0 或以下即视为死亡（推进时跳过，表现层 Alive 为 false）。</summary>
        public int Hp;

        /// <summary>最大血量，单位点；开局等于 Hp，带伤出生时才可能与 Hp 不同。</summary>
        public int MaxHp;

        /// <summary>下一次可攻击的帧号，单位 tick（WorldState.Tick 达到它才允许攻击；0 表示随时可打）。</summary>
        public int AttackReadyTick;
    }

    // 主要功能：持有帧同步一局的实体集合与逻辑帧号，负责增删查改，并提供按 Id 升序的两种遍历快照，让两端遍历顺序完全一致。
    // 主要数据成员：m_entities（实体集合，个；本 Demo 只有个位数）、m_tick（当前逻辑帧号，单位 tick）。
    // 主要外部方法：Add / Remove / IndexOf / GetAt / SetAt / SnapshotSorted / CopySortedTo，以及只读的 Tick 与 Count。
    // 谁在用它：同程序集的 WorldStep 推进一帧、WorldStateHash 拍哈希、LockstepClient 建局；NBC.Game 的 LockstepSnapshotAdapter 调 CopySortedTo 转表现快照；服务端与联调探针用同一份源码。
    // 注意：实体集合必须是 List 而不是 Dictionary 或 HashSet —— 后两者的遍历顺序取决于哈希布局与插入历史，两端会算出不同结果而且不报错，表现成「打着打着两边不一样了」。
    // 注意：List.Sort 不是稳定排序，这里能用它只因为 Id 是唯一键、不存在相同键；真要处理重复键得自己写稳定排序。
    // 注意：位置是定点米（Fix64）、时间是整数 tick；协议与战斗规则用毫米和 tick，跨层只在需要时用 FromInt(毫米) / FromInt(1000) 换算一次，别把毫米直接塞进来。
    /// <summary>一局的世界状态：确定性实体集合加整数 tick 时钟，同一状态在两端必须得到同一份结果。</summary>
    public sealed class WorldState
    {
        // 实体集合；存储顺序只由增删决定，遍历不要直接用它，一律走 SnapshotSorted 或 CopySortedTo（见类头）。
        private readonly List<SimEntity> m_entities = new List<SimEntity>();

        // 当前逻辑帧号（单位 tick），只由 SetTick 推进。
        private int m_tick;

        /// <summary>当前逻辑帧号（单位 tick）；哈希会读它，回退会让两端对不上。</summary>
        // 功能：原样返回存着的帧号，读它不会推进时钟。
        public int Tick
        {
            get { return m_tick; }
        }

        /// <summary>当前实体个数（单位个）；这是存储条数，不构成遍历顺序。</summary>
        // 功能：直接返回存储条数，不排序也不分配。
        public int Count
        {
            get { return m_entities.Count; }
        }

        /// <summary>把逻辑帧号直接设成给定值，供推进后回写；不做单调校验。</summary>
        /// <param name="tick">新的逻辑帧号，单位 tick（负数也照收）。</param>
        // 功能：改写世界时钟到给定帧号。
        // 入参：tick 新的逻辑帧号，单位 tick（不做钳制，负数也照收）
        // 返回：无
        // 步骤：
        //   1) 直接赋值 —— 时钟由推进方决定，这里不猜也不纠正
        // 注意：帧号参与哈希，一端回退一端不回退就会对不上；单调性由调用方（WorldStep 或建局流程）保证。
        // 上游：WorldStep.Step（推进后回写）、LockstepClient.ApplyStart（写入开局帧）、探针
        // 下游：无
        public void SetTick(int tick)
        {
            m_tick = tick;
        }

        /// <summary>登记一个实体：编号唯一，重复登记返回 false；登记顺序不影响任何结果。</summary>
        /// <param name="id">实体编号（唯一键，重复直接拒绝）。</param>
        /// <param name="position">出生位置，单位米（定点）。</param>
        /// <param name="hp">开局血量，单位点，同时写成最大血量。</param>
        /// <returns>true 表示登记成功；false 表示该编号已存在，世界不变。</returns>
        // 功能：按编号登记一个新实体，初始满血、随时可攻击。
        // 入参：id 实体编号（唯一键）；position 出生位置（米）；hp 开局血量（点，同时当最大血量）
        // 返回：true 表示登记成功；false 表示编号已存在，世界一动不动
        // 步骤：
        //   1) 先查编号在不在 —— 重复登记会让同一编号出现两份，按 Id 排序的快照就不再唯一，必须挡住
        //   2) 造实体：MaxHp 取 hp、AttackReadyTick 取 0（开局全员满血、随时可打）
        //   3) 追加到表尾 —— 存储顺序无所谓，遍历一律走排序快照
        // 注意：带伤出生或开局带冷却时不能只用这个入口，调用方必须再用 GetAt 加 SetAt 把真实的 MaxHp 与 AttackReadyTick 写回，否则第 0 帧哈希两端就不一致。
        // 上游：LockstepClient.ApplyStart（建局）、服务端 LockstepService.StartRoom、探针
        // 下游：IndexOf
        public bool Add(int id, FixVector3 position, int hp)
        {
            if (IndexOf(id) >= 0)
            {
                return false;
            }

            var entity = new SimEntity();
            entity.Id = id;
            entity.Position = position;
            entity.Hp = hp;
            entity.MaxHp = hp;
            entity.AttackReadyTick = 0;

            m_entities.Add(entity);
            return true;
        }

        /// <summary>按编号找下标；没找到返回 -1。线性查找，顺序完全确定。</summary>
        /// <param name="id">实体编号。</param>
        /// <returns>下标，0 起；没有这个编号返回 -1。</returns>
        // 功能：在实体集合里按编号线性找下标，给增删改查定位用。
        // 入参：id 实体编号（找不到不算错误）
        // 返回：下标（0 起）；没有这个编号返回 -1
        // 步骤：
        //   1) 从前往后逐条比 Id —— 实体只有个位数，线性查找比字典更确定，也不受哈希布局影响
        // 注意：复杂度是 O(n)，且结果依赖存储顺序；只能拿它定位，不能拿它决定遍历顺序。
        // 上游：同程序集的 Add / Remove / WorldStep；探针与建局流程也直接调
        // 下游：无
        public int IndexOf(int id)
        {
            for (int i = 0; i < m_entities.Count; i++)
            {
                if (m_entities[i].Id == id)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>按编号摘掉一个实体；编号不存在返回 false。给探针一个真能减少实体数的入口。</summary>
        /// <param name="id">实体编号。</param>
        /// <returns>true 表示真的摘掉了一条；false 表示没有这个编号，世界不变。</returns>
        // 功能：按编号把实体从集合里删掉，供需要「实体变少」的场合使用。
        // 入参：id 实体编号（找不到不算错误）
        // 返回：true 表示真的删掉了一条；false 表示没有这个编号，世界不变
        // 步骤：
        //   1) 先 IndexOf 定位，找不到直接返回 false
        //   2) RemoveAt 摘掉，后面的元素前移 —— 存储顺序变了，但遍历一律走排序快照，结果不受影响
        // 注意：存在的理由是让表现层那条路真能遇到「实体变少」：只验「长大」不验「缩后重涨」，复用缓冲与零分配的坑就露不出来；换一个世界对象会顺带换适配器，测到的就不是同一条路径。
        // 注意：删完别缓存下标，按下标读取加写回必须在同一次同步内完成。
        // 上游：联调探针（真改实体数）；确定性内核当前不删实体
        // 下游：IndexOf
        public bool Remove(int id)
        {
            int index = IndexOf(id);

            if (index < 0)
            {
                return false;
            }

            m_entities.RemoveAt(index);
            return true;
        }

        /// <summary>按下标读一个实体（按当前存储顺序，不是 Id 顺序）。</summary>
        /// <param name="index">存储下标，0 起；越界由 List 自己抛。</param>
        /// <returns>实体的副本：结构体，改它不会写回世界，要写回得用 SetAt。</returns>
        // 功能：按存储下标读一个实体，给「先读出来改几个字段再写回」的用法提供入口。
        // 入参：index 存储下标（0 起，越界由 List 抛异常）
        // 返回：实体的副本（结构体，改了不会写回世界，必须再 SetAt）
        // 步骤：
        //   1) 直接按存储下标取 —— 这里刻意不排序，要排序版请走 SnapshotSorted
        // 注意：下标是存储顺序，会随 Remove 变化，别跨帧存；遍历全部实体也不要拿它当顺序。
        // 上游：同程序集的 WorldStep、LockstepClient.ApplyStart；探针
        // 下游：无
        public SimEntity GetAt(int index)
        {
            return m_entities[index];
        }

        /// <summary>按下标覆盖一个实体（整个结构体一起换）。</summary>
        /// <param name="index">存储下标，0 起；越界由 List 自己抛。</param>
        /// <param name="entity">新值（整体覆盖，不是逐字段合并）。</param>
        // 功能：把某个存储位置上的实体整体换成新值。
        // 入参：index 存储下标（0 起，越界由 List 抛）；entity 新实体（整体覆盖）
        // 返回：无
        // 步骤：
        //   1) 直接按下标整体赋值 —— 结构体按值拷贝，逐字段改副本不会影响世界，必须整块换回来
        // 注意：写回时 Id 必须保持不变，否则唯一键被破坏，排序快照与哈希都会跟着乱。
        // 上游：同程序集的 WorldStep（移动、扣血后写回）、LockstepClient.ApplyStart（恢复 MaxHp 与 AttackReadyTick）；探针
        // 下游：无
        public void SetAt(int index, SimEntity entity)
        {
            m_entities[index] = entity;
        }

        /// <summary>取一份按 Id 升序的快照；任何遍历全部实体的地方都必须用它。</summary>
        /// <returns>新数组（调用方可以随便改，不影响世界）；没有实体时是空数组。</returns>
        // 功能：把实体集合拷成一份按 Id 升序的新数组，作为唯一的确定性遍历顺序。
        // 入参：无
        // 返回：新数组（调用方可以随便改，不影响世界）；没有实体时是空数组而不是 null
        // 步骤：
        //   1) 先 ToArray 拷一份 —— 排序不能动到世界本身的存储顺序
        //   2) 用 Array.Sort 加 CompareById 按 Id 升序排；Id 是唯一键，所以 Sort 的非稳定性在这里无害
        // 注意：每次调用都 new 一个数组；表现层每帧都要一份的场合请改用 CopySortedTo，别为省分配去改本方法 —— 它被 WorldStep / WorldStateHash / MatchRules 用着，是已验过的确定性内核。
        // 上游：同程序集的 WorldStep / WorldStateHash / MatchRules；探针
        // 下游：CompareById
        public SimEntity[] SnapshotSorted()
        {
            SimEntity[] snapshot = m_entities.ToArray();

            System.Array.Sort(snapshot, CompareById);

            return snapshot;
        }

        /// <summary>把全部实体按 Id 升序填进调用方给的 List：与 SnapshotSorted 同一序列，但不新分配数组。</summary>
        /// <param name="target">目标列表，会被清空重填；为 null 时什么都不做并返回 0。</param>
        /// <returns>填进去的实体个数，单位个；target 为 null 时为 0。</returns>
        // 功能：用插入排序把实体按 Id 升序填进调用方复用的 List，做到稳态零分配。
        // 入参：target 目标列表（会被清空重填；为 null 时直接返回 0）
        // 返回：填进去的实体个数（单位个）；target 为 null 时为 0
        // 步骤：
        //   1) target 为 null 直接返回 0，调用方不必自己判空
        //   2) 先 Clear 再逐条插入 —— 得到的序列与 SnapshotSorted 完全相同
        //   3) 每条从后往前找插入位置：实体只有个位数，比先拷一份再排序更省，而且不分配、天然稳定
        // 注意：List 的容量在 warm-up 那几帧就涨到位，稳态下 Insert 只搬元素、不分配；代价是容量会停在历史最大实体数。
        // 注意：不许为了这条去改 SnapshotSorted 的行为 —— 只新增、不动老的，那条路径是已验过的确定性内核。
        // 注意：Id 是唯一键，按 Id 升序是全序，插入排序与 Array.Sort 得到的是同一个序列，不存在相同键谁在前的问题。
        // 上游：NBC.Game 的 LockstepSnapshotAdapter 每帧取一份表现快照；探针
        // 下游：无
        public int CopySortedTo(List<SimEntity> target)
        {
            if (target == null)
            {
                return 0;
            }

            target.Clear();

            for (int i = 0; i < m_entities.Count; i++)
            {
                SimEntity entity = m_entities[i];
                int at = target.Count;

                while (at > 0 && target[at - 1].Id > entity.Id)
                {
                    at--;
                }

                target.Insert(at, entity);
            }

            return target.Count;
        }

        // 功能：按 Id 升序比较两个实体，静态方法因此不捕获、不分配。
        private static int CompareById(SimEntity a, SimEntity b)
        {
            return a.Id.CompareTo(b.Id);
        }
    }
}
