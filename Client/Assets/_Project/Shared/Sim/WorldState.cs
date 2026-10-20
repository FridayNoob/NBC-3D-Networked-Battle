// ============================================================================
//  WorldState —— 帧同步一局的**世界状态**（M4-S4 S4-b）
//  项目：3D联网战斗Demo   对应：`Docs\27` §二十九 S4-b、§三十一
//
//  ---------------------------------------------------------------------------
//  一、⚠️ 这个类型的全部难点只有一条：**遍历顺序必须确定**（§29.2 判据②）
//  ---------------------------------------------------------------------------
//  帧同步里 `Dictionary`/`HashSet` 的遍历顺序**不保证**稳定 —— 它取决于哈希布局、
//  插入历史、甚至运行时版本。同一份代码在两端跑，遍历顺序不同 ⇒ 结果不同，
//  而且**不报错**：表现为"打着打着两边不一样了"。
//
//  所以这里的实体集合用 **`List<T>` + 按 `Id` 稳定排序**：
//      · 存：`List`（数组语义，顺序完全由我们控制）
//      · 增/删：`Add` / `Remove`（**删**也不影响任何顺序语义 —— 遍历一律走排序快照）
//      · 取：`SnapshotSorted()` 给出按 Id 升序的快照 —— **谁遍历都用这一份**
//      · 取（不分配版）：`CopySortedTo(List<SimEntity>)` —— **同一个序列**，但不 new 数组
//        （只给"每帧都要一份"的表现层用；确定性内核仍然走 `SnapshotSorted()`，一字未改）
//      · 查：`IndexOf` 线性查找（实体数是个位数，线性比字典更**确定**）
//
//  ⚠️ 注意：`List<T>.Sort` **不是稳定排序**，所以不能拿它给"可能重号"的实体排序。
//     本类型的 `Id` 是**唯一键**，按 Id 排序时相同键不存在 ⇒ 用 `Sort` 是安全的。
//     （真要处理重复键，得自己写稳定排序 —— 这条写在这里免得以后踩。）
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 单位约定：位置用**米**（`Fix64`），速度用"毫米/ tick"换算过来的（米/tick）
//  ---------------------------------------------------------------------------
//  `Shared\Battle\BattleRules` 里距离是**整数毫米**（`BasicAttackRangeMm = 2000`），
//  时间是**整数 tick**（`BasicAttackCooldownTicks = 15`）—— 本片沿用同一套：
//      · 位置：`FixVector3`，单位**米**（1 = 1 米）
//      · 时间：**整数 tick**（**不引入"定点秒"** —— 少一种单位就少一处换算错）
//      · 换算只在需要时做一次：`range = FromInt(BasicAttackRangeMm) / FromInt(1000)`
// ============================================================================

using System.Collections.Generic;
using NBC.Shared.Battle;

namespace NBC.Shared.Sim
{
    /// <summary>帧同步世界里的一个实体（**纯数据**：整数与定点，没有引用类型字段）。</summary>
    public struct SimEntity
    {
        /// <summary>实体编号（**唯一键**：排序、查找、哈希都靠它）。</summary>
        public int Id;

        /// <summary>位置（米）。</summary>
        public FixVector3 Position;

        /// <summary>当前血量。</summary>
        public int Hp;

        /// <summary>最大血量。</summary>
        public int MaxHp;

        /// <summary>下一次可以攻击的 tick（<see cref="WorldState.Tick"/> 达到它才允许攻击）。</summary>
        public int AttackReadyTick;
    }

    /// <summary>一局的世界状态（确定性集合 + 整数 tick 时钟）。</summary>
    public sealed class WorldState
    {
        /// <summary>实体集合（⚠️ **List 而不是 Dictionary** —— 见文件头第一节）。</summary>
        private readonly List<SimEntity> m_entities = new List<SimEntity>();

        /// <summary>当前逻辑帧号（整数 tick）。</summary>
        private int m_tick;

        /// <summary>当前帧号。</summary>
        public int Tick
        {
            get { return m_tick; }
        }

        /// <summary>实体个数。</summary>
        public int Count
        {
            get { return m_entities.Count; }
        }

        /// <summary>把时钟往前拨（`Step` 用）。</summary>
        /// <param name="tick">新的帧号。</param>
        public void SetTick(int tick)
        {
            m_tick = tick;
        }

        /// <summary>登记一个实体（顺序**不影响**任何结果 —— 遍历一律走排序快照）。</summary>
        /// <param name="id">编号（唯一）。</param>
        /// <param name="position">位置（米）。</param>
        /// <param name="hp">血量。</param>
        /// <returns>成功登记返回 true；编号重复返回 false。</returns>
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

        /// <summary>按编号找下标；没有返回 −1。**线性查找**（实体少，且顺序完全确定）。</summary>
        /// <param name="id">编号。</param>
        /// <returns>下标或 −1。</returns>
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

        /// <summary>
        /// 摘掉一个实体（按编号；没有返回 false）。
        /// <para>⚠️ 为什么要它：表现层那条路**必须**能遇到"实体变少了"（`RepeatedField.RemoveAt` 那一段），
        /// 不然「每帧不分配」只验了"长大"、没验"缩后重涨"。给探针一个真能改实体数的入口比伪造一个世界对象可靠
        /// （换世界对象会顺带换适配器 ⇒ 测到的就不是同一条路径了）。</para>
        /// <para>⚠️ 顺序无关：任何"遍历全部实体"的地方都走 `SnapshotSorted()` / `CopySortedTo`。</para>
        /// </summary>
        /// <param name="id">编号。</param>
        /// <returns>真的摘掉了一个返回 true。</returns>
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

        /// <summary>读一个实体（**按当前存储顺序**；排序请用 `SnapshotSorted`）。</summary>
        /// <param name="index">下标。</param>
        /// <returns>实体。</returns>
        public SimEntity GetAt(int index)
        {
            return m_entities[index];
        }

        /// <summary>覆盖一个实体（按下标）。</summary>
        /// <param name="index">下标。</param>
        /// <param name="entity">新值。</param>
        public void SetAt(int index, SimEntity entity)
        {
            m_entities[index] = entity;
        }

        /// <summary>
        /// 按 `Id` 升序排好的一份快照 —— **任何"遍历全部实体"的地方都必须用它**
        /// （这是判据②的落点，别绕过）。
        /// </summary>
        /// <returns>新数组（调用方可以随便改，不影响世界）。</returns>
        public SimEntity[] SnapshotSorted()
        {
            SimEntity[] snapshot = m_entities.ToArray();

            // Id 是唯一键 ⇒ 不涉及"相同键的先后"，`Sort` 的非稳定性在这里无害（见文件头第一节）
            System.Array.Sort(snapshot, CompareById);

            return snapshot;
        }

        /// <summary>
        /// 按 `Id` 升序把全部实体**填进调用方给的 List** —— 语义与 `SnapshotSorted()` **完全相同**
        /// （同样的顺序、同样是一份只读快照），唯一区别是**不 new 数组**。
        ///
        /// <para>⚠️ 为什么要有这个重载：`SnapshotSorted()` 每次 `m_entities.ToArray()` 都会
        /// **new 一个 `SimEntity[]`**。表现层那种"每帧都要一份排好序的实体"的场合（30Hz ⇒ 每秒 30 个小数组）
        /// 就变成了一块**没必要的每帧分配**。调这个重载、自己持有一个 List 反复复用 ⇒ **稳态 0 分配**。</para>
        ///
        /// <para>⚠️ **不许**为了这条去改 `SnapshotSorted()` 的行为：它被确定性内核用着
        /// （`WorldStep` / `WorldStateHash` / `MatchRules`），改它就是动已验过的确定性。**只新增，不动老的。**</para>
        ///
        /// <para>⚠️ 顺序与 `SnapshotSorted()` 一致：`Id` 是唯一键 ⇒ 按 `Id` 升序是**全序**，
        /// 用插入排序与用 `Array.Sort` 得到的是**同一个序列**（不存在"相同键谁在前"的问题）。</para>
        /// </summary>
        /// <param name="target">目标（会被**清空重填**；为 null 时什么都不做，返回 0）。</param>
        /// <returns>填了几个实体。</returns>
        public int CopySortedTo(List<SimEntity> target)
        {
            if (target == null)
            {
                return 0;
            }

            target.Clear();

            // 插入排序：实体数是个位数 ⇒ 比"先拷一份再排"更省，而且**不分配**、**天然稳定**
            for (int i = 0; i < m_entities.Count; i++)
            {
                SimEntity entity = m_entities[i];
                int at = target.Count;

                while (at > 0 && target[at - 1].Id > entity.Id)
                {
                    at--;
                }

                // 容量在 warm-up 那几帧就涨到位 ⇒ 稳态下 `Insert` 只搬元素、不分配
                target.Insert(at, entity);
            }

            return target.Count;
        }

        /// <summary>按 Id 比较（静态方法 = 不捕获、不分配）。</summary>
        /// <param name="a">左。</param>
        /// <param name="b">右。</param>
        /// <returns>比较结果。</returns>
        private static int CompareById(SimEntity a, SimEntity b)
        {
            return a.Id.CompareTo(b.Id);
        }
    }
}
