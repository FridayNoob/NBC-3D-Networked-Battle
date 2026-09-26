// ============================================================================
//  NBC.Server.Game —— 战斗用的**确定性**随机数（M3-S7 掉落掷骰）
//  项目：3D联网战斗Demo
//  对应：需求 DET-04（"本局随机种子"）、`Docs\17` §4.1（比例用万分比）、Docs\25 §四 S7
//
//  ---------------------------------------------------------------------------
//  一、为什么不用 `System.Random`（这条是本节存在的全部理由）
//  ---------------------------------------------------------------------------
//  `System.Random` 的具体算法**在 .NET 版本之间变过**（.NET 6 换过一次实现，
//  为兼容又提供了 `Random.Shared` 与"指定种子"的不同行为）。于是：
//      · 同一段代码在 .NET Framework / .NET 6 / .NET 8 上，同一个种子**给不出同一串数**
//      · 更要命的是它在 Unity 的 Mono/IL2CPP 下又是另一套
//  而掉落是"**两端必须看到同一份**"的东西（S7 的验收就是这个），
//  一旦随机数不可复现，掉落在**单端测试里全绿**、联机时才表现为"两边掉的东西不一样" ——
//  又一个静默失败。
//  ⇒ 所以这里自己写一个**算法写死的**小 PRNG：32 位 xorshift，**不依赖任何库的版本**。
//
//  ---------------------------------------------------------------------------
//  二、它是什么（说人话）
//  ---------------------------------------------------------------------------
//  xorshift 就是"把数字自身异或 + 移位"：
//      x ^= x << 13;  x ^= x >> 17;  x ^= x << 5;
//  特点：**极快**（三次位运算）、**周期长**（2³²-1）、**完全确定**（同样的种子 → 同样的序列）。
//  代价：**它不是密码学安全的**（能被预测）—— 但掉落本来就不需要防预测，
//       真要防作弊（例如抽卡），那是服务端算完只发结果，客户端根本看不到序列。
//
//  ⚠️ 种子怎么来：M3 用"固定常数 ^ 房号散列"（同一房间的同一局可复现）。
//     真要做"历史对局复现"，种子必须**随房间创建一起记录下来**（M5+，和需求 DET-04 的 RandomSeed 对齐）。
// ============================================================================

namespace NBC.Server.Game;

/// <summary>确定性伪随机数（xorshift32）。见文件头：**为什么不能用 `System.Random`**。</summary>
public sealed class BattleRandom
{
    /// <summary>内部状态（**不能为 0**：xorshift 的状态一旦是 0 就永远输出 0）。</summary>
    private uint m_state;

    /// <summary>已经取过多少个数（排查"掷了几次"用）。</summary>
    public long DrawCount { get; private set; }

    /// <summary>用种子建一个。</summary>
    /// <param name="seed">种子；传 0 会被换成一个非零常数。</param>
    public BattleRandom(int seed)
    {
        uint value = unchecked((uint)seed);
        m_state = value == 0u ? 0x9E3779B9u : value;
    }

    /// <summary>取下一个 32 位无符号数。</summary>
    /// <returns>随机数。</returns>
    public uint NextUInt()
    {
        uint x = m_state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        m_state = x;
        DrawCount++;
        return x;
    }

    /// <summary>取 `[0, maxExclusive)` 里的整数（模运算；对掉落这种小范围足够）。</summary>
    /// <param name="maxExclusive">上界（不含）。</param>
    /// <returns>随机数；上界 ≤ 0 时返回 0。</returns>
    public int Next(int maxExclusive)
        => maxExclusive <= 0 ? 0 : (int)(NextUInt() % (uint)maxExclusive);

    /// <summary>取一个**万分比**掷骰结果（`0..9999`）—— 与 `Docs\17` §4.1 的概率口径一致。</summary>
    /// <returns>0..9999。</returns>
    public int NextPerTenThousand() => Next(10000);
}
