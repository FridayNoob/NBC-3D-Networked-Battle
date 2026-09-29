// ============================================================================
//  WorldStateHash —— 世界状态的**确定性哈希**（M4-S4 S4-b）
//  项目：3D联网战斗Demo   对应：`Docs\27` §二十九 S4-b、§三十一
//
//  ---------------------------------------------------------------------------
//  一、它解决的唯一问题：**"两边一样吗"要能一眼答出来**
//  ---------------------------------------------------------------------------
//  帧同步最怕的不是"算错了"，而是"**两边算得不一样、却没人发现**"。
//  哈希把整个世界压成一个整数：**逐帧对比哈希**就能在**跑偏的那一帧**当场发现分歧。
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 三条写死的约束（有一条破了，哈希就失去意义）
//  ---------------------------------------------------------------------------
//  ① **全整数运算**：只用 `long`/`ulong`/`int` 与 `Fix64.RawValue`（**取原始整数**，
//     不做任何浮点转换）。⚠️ 一个 `double` 中间量就能让"看起来相同"的两个状态哈希不同。
//     （`Shared\Sim\` 在 `Check-FloatFree` 的扫描范围里 ⇒ 破了这条闸门会红。）
//
//  ② **不依赖遍历顺序**：一律喂 `world.SnapshotSorted()`（按 `Id` 升序）。
//     ⚠️ 直接用存储顺序 ⇒ **登记顺序**会改变哈希 ⇒ "顺序无关性"那条用例会红，
//        而它正是 §29.2 判据②的可观测形式。
//
//  ③ **字段顺序固定**：先喂 `Id`，再 `Position.X/Y/Z` 的 **RawValue**，再 `Hp`、`MaxHp`、
//     `AttackReadyTick`，最后喂 `Tick`。**"哪几个字段进哈希"是一个契约**：
//     将来加字段（速度/朝向/血量上限…）**必须**一并加进来，否则
//     "状态变了但哈希没变" ⇒ 分歧被**静默漏掉**。
//
//  算法选 **FNV-1a（64 位）**：全整数、无表、不依赖平台；它**不是密码学哈希**
//  （我们不防篡改，只做一致性对账），碰撞概率对"逐帧对比"这个用途足够。
// ============================================================================

namespace NBC.Shared.Sim
{
    /// <summary>把世界状态拍成一个确定性的 64 位哈希。</summary>
    public static class WorldStateHash
    {
        /// <summary>FNV-1a 64 位的偏移基数。</summary>
        private const ulong FnvOffsetBasis = 14695981039346656037UL;

        /// <summary>FNV-1a 64 位的质数。</summary>
        private const ulong FnvPrime = 1099511628211UL;

        /// <summary>
        /// 算一局世界的哈希。
        /// <para>⚠️ 见文件头第二节的三条约束 —— 尤其"别改遍历顺序、别漏字段"。</para>
        /// </summary>
        /// <param name="world">世界（不能为 null）。</param>
        /// <returns>哈希（64 位，**同一状态恒得同一个值**）。</returns>
        public static ulong Compute(WorldState world)
        {
            ulong hash = FnvOffsetBasis;

            if (world == null)
            {
                return hash;
            }

            // ⚠️ 约束②：按 Id 升序喂 —— 与登记顺序无关
            SimEntity[] snapshot = world.SnapshotSorted();

            // 先喂实体个数：否则"少一个实体"和"多一个字段"可能撞在一起
            hash = Mix(hash, snapshot.Length);

            for (int i = 0; i < snapshot.Length; i++)
            {
                SimEntity entity = snapshot[i];

                // ⚠️ 约束③：字段顺序固定，且逐个显式列出（**别用反射/序列化**：
                //    那样字段顺序会随版本变，而且反射在两端行为未必一致）
                hash = Mix(hash, entity.Id);
                hash = Mix(hash, entity.Position.X.RawValue);
                hash = Mix(hash, entity.Position.Y.RawValue);
                hash = Mix(hash, entity.Position.Z.RawValue);
                hash = Mix(hash, entity.Hp);
                hash = Mix(hash, entity.MaxHp);
                hash = Mix(hash, entity.AttackReadyTick);
            }

            // 帧号也进哈希：这样"少走一帧"一定会被发现（否则两个世界可能停在同一状态）
            hash = Mix(hash, world.Tick);

            return hash;
        }

        /// <summary>把一段整数混进哈希（FNV-1a：先异或一个字节，再乘质数）。</summary>
        /// <param name="hash">当前哈希。</param>
        /// <param name="value">要混进去的 64 位值。</param>
        /// <returns>新哈希。</returns>
        public static ulong Mix(ulong hash, long value)
        {
            ulong bits = unchecked((ulong)value);

            for (int i = 0; i < 8; i++)
            {
                hash ^= bits & 0xFFUL;
                hash *= FnvPrime;
                bits >>= 8;
            }

            return hash;
        }
    }
}
