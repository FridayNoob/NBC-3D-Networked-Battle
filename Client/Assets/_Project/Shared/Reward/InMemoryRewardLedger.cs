// 这个文件里有：
//   InMemoryRewardLedger（类）：把已发奖励记在进程内存里的台账实现，单机与 EditMode 测试用

using System.Collections.Generic;

namespace NBC.Shared.Reward
{
    /// <summary>内存版台账实现：记录只在进程内存里，进程一结束就没了。</summary>
    // 主要功能：用两个集合在进程内存里记住哪些奖励已发过，并留下发放流水，能回答「发了几次、分别是谁」。
    // 主要数据成员：m_granted（已发记录的键集合，键 = 种类数字:发布者编号）、m_history（按发生顺序的发放流水，元素 = 键→奖励编号）。
    // 主要外部方法：HasGranted / MarkGranted（接口实现，查与记）、Count / Clear（断言笔数与测试收尾）、ToString（转一句人话）。
    // 谁在用它：BattleSession 在调用方没给台账时兜底 new 一个；EditMode 的 AchievementRuntimeTests 直接 new 出来断言发了几笔；服务端真持久化不用它，用 NBC.Server.Data 里那份带 reward_granted 表的实现。
    // 注意：同一进程里模拟第二局够用，服务端重启后的防重复发奖不够用 —— 进程一没记录就清空，「重启不重复发奖」就失效，那种场景必须换持久化实现。
    // 注意：它不是占位符，而是能被断言的那个实现 —— 验收线是「重启之后没有重复发奖」，所以它必须回答得出「到底发过几次」；只断言 MarkGranted 被调用过不算数，调用过不等于次数对。
    // 注意：正因为「能不能用它」必须显式决定，它被做成构造函数的必填参数、不给默认值 —— 让「我没接台账」在编译期就看得见，而不是运行期才发现行为不对。
    public sealed class InMemoryRewardLedger : IRewardLedger
    {
        // 已发记录的键集合；键 =「种类数字:发布者编号」（如 2:9001），只增不删，只有 Clear 才整体清空
        private readonly HashSet<string> m_granted = new HashSet<string>();

        // 发放流水，按发生顺序；元素 =「键→奖励编号」（如 2:9001→5001），一笔一条，供断言发了几次、分别是谁
        private readonly List<string> m_history = new List<string>();

        /// <summary>流水里有几笔；注意这是「记账次数」，不是「发奖次数」。</summary>
        // 功能：把流水条数报出去，供测试断言这一局记了几笔。
        // 返回：已记录的不同（种类，编号）组合数 —— 同一组重复 MarkGranted 不会累加
        // 上游：AchievementRuntimeTests 的断言
        // 下游：m_history
        public int Count
        {
            get { return m_history.Count; }
        }

        /// <summary>这份奖励发过没有；true 表示台账里已有记录。</summary>
        // 功能：按（种类，编号）拼出键，查集合里有没有这一笔。
        // 入参：kind 来源种类（任务 / 成就）；ownerId 发布者编号（取自 Quest / Achievement 表主键）
        // 返回：true 表示记过账（发过了）；false 表示没记录 —— 可能该补发，也可能条件还没达成
        // 步骤：
        //   1) 拼键只走 KeyOf，与 MarkGranted 同一处拼法 —— 两处各拼一遍一旦不一致，查的与记的就不是同一个键
        //   2) 命中即已发；空台账只能答「都没记过」，不能当作「全都发过」
        // 上游：AchievementRuntime.Unlock（发奖前查）
        // 下游：KeyOf、m_granted
        public bool HasGranted(ERewardOwnerKind kind, int ownerId)
        {
            return m_granted.Contains(KeyOf(kind, ownerId));
        }

        /// <summary>记下这份奖励刚刚发过；同一组（种类，编号）重复调用只算一次。</summary>
        // 功能：把「刚刚发了这份奖励」写进台账，并把这一笔追加到流水。
        // 入参：kind 来源种类；ownerId 发布者编号；rewardId 奖励编号（只随流水留痕供排查，不参与查重）
        // 返回：无；重复调用没有副作用
        // 步骤：
        //   1) 先拼键，与 HasGranted 共用 KeyOf，保证查与记认的是同一个键
        //   2) 用 Add 的返回值判新旧：false 表示本来就有，此时不报错也不重复记 —— 台账的契约是幂等，重复标记是调用方的正常可能（比如重试），不是错误
        //   3) 只有新键才追加流水，Count 与 ToString 数出来的才是实际记上的笔数
        // 上游：AchievementRuntime.Unlock（发奖之后调用，先 Grant 再记）
        // 下游：KeyOf、m_granted、m_history
        // 注意：记账必须排在发奖之后 —— 反过来的话发奖一旦抛异常，就成了「记了账没发奖」，玩家永远拿不到这份奖励。
        public void MarkGranted(ERewardOwnerKind kind, int ownerId, int rewardId)
        {
            string key = KeyOf(kind, ownerId);

            if (m_granted.Add(key))
            {
                m_history.Add(key + "→" + rewardId);
            }
        }

        /// <summary>清空台账与流水，让同一个实例可以当作全新的一局继续用。</summary>
        // 功能：把已发集合与流水一起清掉。
        // 入参：无
        // 返回：无
        // 步骤：两个集合都清 —— 只清已发集合的话，Count 与 ToString 还留着上一局的流水
        // 上游：目前没有调用方 —— 原文用途是测试收尾，留作复用同一个实例的入口
        // 下游：m_granted、m_history
        public void Clear()
        {
            m_granted.Clear();
            m_history.Clear();
        }

        /// <summary>转成一句人话，例如「已发 2 笔：2:9001→5001、1:3001→5002」。</summary>
        // 功能：把流水拼成一句可读的汇总，排查时直接打印。
        // 返回：空台账是「已发 0 笔：（无）」；否则各笔用「、」连起来
        // 上游：目前没有直接调用方 —— 排查时手动打印用
        // 下游：m_history
        public override string ToString()
        {
            return "已发 " + m_history.Count + " 笔：" +
                   (m_history.Count == 0 ? "（无）" : string.Join("、", m_history));
        }

        // 功能：把（种类，编号）拼成台账的键。
        // 入参：kind 来源种类；ownerId 发布者编号
        // 返回：键，形如「2:9001」，种类在前、编号在后
        // 步骤：
        //   1) 种类取 (int)kind 而不是枚举名 —— 数据库那边 owner_kind 存的就是这个数，两边用同一个数字，排查时对得上
        //   2) 两个数用冒号隔开，免得种类与编号直接连起来时，不同的（种类，编号）拼出同一个键
        // 上游：HasGranted、MarkGranted
        // 下游：无
        // 注意：拼法只有这一处，别再在别处拼一遍 —— 两处一旦不一致，查的与记的就不是同一个键。
        private static string KeyOf(ERewardOwnerKind kind, int ownerId)
        {
            return ((int)kind) + ":" + ownerId;
        }
    }
}
