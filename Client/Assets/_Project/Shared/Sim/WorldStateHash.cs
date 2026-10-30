// 这个文件里有：
//   WorldStateHash（静态类）：把一局世界状态的实体与帧号压成确定性的 64 位哈希，供两端逐帧对账

namespace NBC.Shared.Sim
{
    // 主要功能：按固定字段顺序、全整数运算，把一局世界状态（实体表 + 帧号）算成 FNV-1a 64 位哈希（非密码学哈希，只做一致性对账），供双端逐帧比对。
    // 主要数据成员：FnvOffsetBasis（偏移基数，ulong 常量）、FnvPrime（质数，ulong 常量），取值写死，改了历史哈希就不再可比。
    // 主要外部方法：Compute（算一局哈希）、Mix（把一个整数混进哈希）。
    // 谁在用它：同程序集的 LockstepClient 与 LockstepScheduler 逐帧对账；服务端 NBC.Server.Game 的服务与 Server 下的探针跨程序集调 Compute（本文件由双端同一份源码编译，且不引用 NBC.Protocol）。
    /// <summary>把一局世界状态的实体与帧号拍成确定性的 64 位哈希；同一状态在两端必须得到同一个值。</summary>
    public static class WorldStateHash
    {
        private const ulong FnvOffsetBasis = 14695981039346656037UL;   // FNV-1a 64 位的偏移基数，哈希从这里起步；取值固定，改了与历史哈希不再可比

        private const ulong FnvPrime = 1099511628211UL;               // FNV-1a 64 位的质数，每混一个字节乘一次；必须是固定的奇数常量

        /// <summary>算一局世界状态的确定性哈希；同一状态在两端必须得到同一个值。</summary>
        /// <param name="world">要拍哈希的世界；传 null 时返回偏移基数（空哈希）。</param>
        /// <returns>64 位 FNV-1a 哈希；null 世界返回偏移基数。</returns>
        // 功能：把世界的实体与帧号按固定顺序拍成 64 位 FNV-1a 哈希，两端逐帧比对这个值就能发现分歧。
        // 入参：world 要拍哈希的世界（可为 null，null 时返回偏移基数）
        // 返回：64 位哈希；同一状态恒得同一个值，不同状态理论上不同
        // 步骤：
        //   1) world 为 null 直接返回偏移基数，调用方不必自己判空
        //   2) 用 SnapshotSorted 取按 Id 升序的快照，哈希因此与实体登记顺序无关
        //   3) 先喂实体个数（个），否则「少一个实体」与「多一个字段」可能撞在一起
        //   4) 逐个实体按固定顺序喂：Id、Position 的 X/Y/Z 定点原始整数（位置单位米，取 RawValue）、Hp 与 MaxHp（点）、AttackReadyTick（tick）
        //   5) 最后喂 Tick（tick），这样「少走一帧」即使停在同一状态也一定被发现
        // 上游：LockstepClient / LockstepScheduler 逐帧对账；服务端的开局哈希与逐帧 ServerHash
        // 下游：Mix
        // 注意：全程只走整数（Fix64 取 RawValue，不做任何浮点转换）——一个 double 中间量就能让看起来相同的两个状态哈希不同，而 Shared\Sim 在 Check-FloatFree 的扫描范围里。
        // 注意：遍历一律走 SnapshotSorted，别改成存储顺序 —— 否则登记顺序会改变哈希，顺序无关性那条用例会红。
        // 注意：字段顺序与字段清单是契约：将来加字段（速度、朝向等）必须一并加进来，否则状态变了哈希没变、分歧被静默漏掉；也别改用反射或序列化，字段顺序与两端行为都不保证一致。
        public static ulong Compute(WorldState world)
        {
            ulong hash = FnvOffsetBasis;

            if (world == null)
            {
                return hash;
            }

            SimEntity[] snapshot = world.SnapshotSorted();

            hash = Mix(hash, snapshot.Length);

            for (int i = 0; i < snapshot.Length; i++)
            {
                SimEntity entity = snapshot[i];

                hash = Mix(hash, entity.Id);
                hash = Mix(hash, entity.Position.X.RawValue);
                hash = Mix(hash, entity.Position.Y.RawValue);
                hash = Mix(hash, entity.Position.Z.RawValue);
                hash = Mix(hash, entity.Hp);
                hash = Mix(hash, entity.MaxHp);
                hash = Mix(hash, entity.AttackReadyTick);
            }

            hash = Mix(hash, world.Tick);

            return hash;
        }

        // 功能：把一个整数按 8 个字节混进当前哈希（FNV-1a：先异或一个字节，再乘质数）。
        // 入参：hash 当前哈希；value 要混进去的整数（按 64 位原样喂，负数先按位转 ulong）
        // 返回：混完后的新哈希
        // 步骤：
        //   1) 把 value 按位转成 ulong，避免负数符号扩展
        //   2) 从低位到高位数满 8 个字节：异或最低字节、乘质数、右移 8 位
        //   3) 高位补出来的 0 字节也要喂完，否则短值和高位为 0 的值会混成同一段字节
        // 上游：Compute（本程序集内）
        // 下游：无
        // 注意：改这里的字节数或字节顺序等于换算法，两端版本不一致会直接对不上；目前仓库里没有外部调用点，只有 Compute 在用。
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
