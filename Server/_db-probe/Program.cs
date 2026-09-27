// ============================================================================
//  NBC 数据库探针（M4-S3）
//  项目：3D联网战斗Demo
//
//  ---------------------------------------------------------------------------
//  它回答两个不同的问题（**都很重要，但只有第一个能离线回答**）
//  ---------------------------------------------------------------------------
//      一、**写回缓存的逻辑对不对**（脏集合并 / 单飞 / Remove 写 0 / Load 合并 /
//          失败后哪些键该重新标脏）
//          → 用假 DAO，**不需要 MySQL、不需要密码**，随时能跑。
//
//      二、**SQL 对不对、数据真的落库了吗**
//          → 需要真 MySQL + 密码。
//
//  ⚠️ 为什么非要拆成两块：这两类错误的"表现"完全不同 ——
//     逻辑错会表现为"偶尔进度回退/多算"（难复现），SQL 错表现为"根本没写进去"（好查）。
//     混在一起测，一旦挂了要先猜是哪一类。
//
//  ⚠️ 第二块**没密码就明确跳过并说明怎么给**（见 `MySqlSection`）——
//     不这么做的话，"探针全绿"可能其实是"第二块一条都没跑"。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using NBC.Server.Core;          // M4-S3：`ELoginRejection` / `LoginResult`
using NBC.Server.Data;
using NBC.Server.Game;
using NBC.Shared.Condition;
using NBC.Shared.Auth;          // M4-S3：密码摘要配方（**双端唯一实现**）
using NBC.Shared.Reward;

namespace NBC.DbProbe
{
    /// <summary>数据库探针主程序。</summary>
    public static class Program
    {
        /// <summary>通过的用例数。</summary>
        private static int s_passed;

        /// <summary>失败的用例数。</summary>
        private static int s_failed;

        /// <summary>入口。</summary>
        /// <returns>0 = 全绿，1 = 有失败。</returns>
        public static async Task<int> Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            Console.WriteLine("================================================================================");
            Console.WriteLine("  NBC 数据库探针（M4-S3 进度持久化）");
            Console.WriteLine("================================================================================");

            Console.WriteLine();
            Console.WriteLine("【一】写回缓存的逻辑（假 DAO —— 不需要 MySQL）");

            PlayerScopedStoreRejectsZero();
            MissingKeyReadsZero();
            SetIsVisibleImmediatelyWithoutIo();
            FlushWritesLatestValueOnly();
            FlushIsIdempotent_AbsoluteValue();
            RemoveMarksZeroDirty();
            LoadTakesTheLargerValue();
            LoadDoesNotClobberNewerInMemory();
            ConcurrentFlushIsSingleFlight();
            MarkUnflushed_DoesNotOverwriteNewerValue();
            MarkUnflushed_RestoresWhenStillSame();
            NoDirtyDataMeansNoSql();
            ConnectionStringHidesNothing_ButDescribeHidesPassword();
            ProviderMismatchThrows();

            Console.WriteLine();
            Console.WriteLine("【二】真 MySQL（需要凭据；没有就明确跳过）");

            await MySqlSection().ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("【三】连不上时的报错形状（DB-10 —— **故意连不上**，不需要真密码）");

            await ErrorMessageSection().ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("【四】台账（`IRewardLedger` —— **单调集合**，比进度表简单的那一半）");

            LedgerIsMonotonic();
            LedgerFailureKeepsRecord();

            Console.WriteLine();
            Console.WriteLine("【五】真库：台账往返（需要凭据）");

            await LedgerMySqlSection().ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("【六】真库：战绩落库 SRV-13（记录 + 明细 + 档案累计，一次事务）");

            await BattleRecordSection().ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("【七】M4-S3：账号登录（SRV-06 真正的登录 —— 身份属于账号，不属于连接）");

            PasswordRecipeSection();
            await LoginMySqlSection().ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("【八】SRV-17a：**游客不落库**（负数是游客 ⇒ 不会污染任何账号的档案）");

            await GuestDoesNotPolluteSection().ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("【九】M4-S3 服务端权威：成就进度**落库** + **重启后还在** + **不重复发奖**");

            await AchievementAuthoritySection().ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("通过 " + s_passed + "，失败 " + s_failed + "。");
            Console.WriteLine(s_failed == 0 ? "结果：✅ 全绿" : "结果：❌ 有失败");
            return s_failed == 0 ? 0 : 1;
        }

        // ====================================================================
        //  一、写回缓存的逻辑（假 DAO）
        // ====================================================================

        /// <summary>玩家编号 ≤ 0 要当场报错（否则所有玩家共用一个 0 号存档）。</summary>
        private static void PlayerScopedStoreRejectsZero()
        {
            var fake = new FakeDao();

            Check("玩家编号 = 0 -> 抛异常（防串档）",
                Throws<ArgumentOutOfRangeException>(() => new CachingConditionProgressStore(fake, 0)),
                "没抛异常");
        }

        /// <summary>没记录过的条件读出来是 0（接口契约：读不到是**正常状态**）。</summary>
        private static void MissingKeyReadsZero()
        {
            using (var store = new CachingConditionProgressStore(new FakeDao(), 1))
            {
                Check("没记录过的条件 -> 读 0（不是异常）", store.GetProgress(4009) == 0,
                    "读到了 " + store.GetProgress(4009));
            }
        }

        /// <summary>`SetProgress` 之后**立刻**能读到（内存权威），而且**一次 IO 都没有**。</summary>
        private static void SetIsVisibleImmediatelyWithoutIo()
        {
            var fake = new FakeDao();

            using (var store = new CachingConditionProgressStore(fake, 1))
            {
                store.SetProgress(4009, 7);

                Check("Set 之后立刻可读（内存权威）", store.GetProgress(4009) == 7,
                    "读到 " + store.GetProgress(4009));
                Check("Set 不碰数据库（DB-03：主循环里不做同步查询）", fake.UpsertCalls == 0,
                    "居然写了 " + fake.UpsertCalls + " 次库");
            }
        }

        /// <summary>同一个键 Set 多次，只有**最后一次**的值会落库。</summary>
        private static void FlushWritesLatestValueOnly()
        {
            var fake = new FakeDao();

            using (var store = new CachingConditionProgressStore(fake, 1))
            {
                store.SetProgress(4009, 1);
                store.SetProgress(4009, 2);
                store.SetProgress(4009, 3);

                Check("三次 Set -> 脏集只有 1 条（合并）", store.DirtyCount == 1,
                    "脏集 " + store.DirtyCount + " 条");

                store.FlushAsync().GetAwaiter().GetResult();

                Check("落库的是**最后一次**的值（3）",
                    fake.Stored.Count == 1 && fake.Stored[0] == 3,
                    "落了 " + fake.Stored.Count + " 行：" + string.Join(",", fake.Stored));
            }
        }

        /// <summary>写的是**绝对值** ⇒ 同一批数据写两次结果相同（幂等，重试安全）。</summary>
        private static void FlushIsIdempotent_AbsoluteValue()
        {
            var fake = new FakeDao();

            using (var store = new CachingConditionProgressStore(fake, 1))
            {
                store.SetProgress(4009, 5);
                store.FlushAsync().GetAwaiter().GetResult();

                Check("落库后库里是 5", fake.ValueOf(4009) == 5, "库里是 " + fake.ValueOf(4009));

                // 模拟"重试"：把同一批再标脏一次并再落一次
                store.MarkUnflushed(new List<ConditionProgressRow>
                {
                    new ConditionProgressRow { player_id = 1, condition_key = 4009, progress = 5 }
                });
                store.FlushAsync().GetAwaiter().GetResult();

                // ⚠️ 判据是**库里的最终状态**，不是"写了几次"。
                //    第一版我断言 `Stored.Count == 1`，探针当场红了 —— 那是**断言写错了**：
                //    `Stored` 记的是"每一次落库的值"，重试当然会有第二次调用。
                //    "幂等"的定义是**重复执行后状态不变**，不是"没有重复执行"。
                //    （这正是"判据要盯着结果、不盯着中间物"那条 —— 同 M1-B5「调用过 ≠ 内容对」。）
                Check("同一批写两次 -> 库里的值仍然是 5（幂等 = 重复执行后状态不变）",
                    fake.ValueOf(4009) == 5,
                    "库里是 " + fake.ValueOf(4009));
            }
        }

        /// <summary>
        /// `Remove` 必须把"删除"也记进脏集（用 0 表示）。
        /// <para>⚠️ 不记的话：内存删了、库里还在 ⇒ 下次登录又读回来了，
        /// 表现是"我明明重置过，怎么又有了"——只在重启后出现的悬案。</para>
        /// </summary>
        private static void RemoveMarksZeroDirty()
        {
            var fake = new FakeDao();

            using (var store = new CachingConditionProgressStore(fake, 1))
            {
                store.SetProgress(4009, 9);
                store.FlushAsync().GetAwaiter().GetResult();

                bool removed = store.Remove(4009);

                Check("Remove 返回 true（本来有）", removed, "返回了 false");
                Check("Remove 之后读 0", store.GetProgress(4009) == 0, "读到 " + store.GetProgress(4009));
                Check("Remove 会把 0 标脏（否则重启后进度会复活）", store.DirtyCount == 1,
                    "脏集 " + store.DirtyCount + " 条（应当是 1）");

                store.FlushAsync().GetAwaiter().GetResult();
                Check("落库之后**库里**的值变成 0（库里也真的删掉了）",
                    fake.ValueOf(4009) == 0,
                    "库里是 " + fake.ValueOf(4009) + "（写过的值依次为：" + string.Join(",", fake.Stored) + "）");
            }
        }

        /// <summary>`Load` 把库里的进度并进内存（成就"跨局累计"靠的就是这条）。</summary>
        private static void LoadTakesTheLargerValue()
        {
            var fake = new FakeDao();
            fake.Seed(1, 4009, 6);      // 假装"上次已经打了 6 只"

            using (var store = new CachingConditionProgressStore(fake, 1))
            {
                int loaded = store.LoadAsync().GetAwaiter().GetResult();

                Check("Load 读回来 1 条", loaded == 1, "读回 " + loaded + " 条");
                Check("库里的进度进了内存（6）", store.GetProgress(4009) == 6,
                    "读到 " + store.GetProgress(4009));
            }
        }

        /// <summary>
        /// `Load` **不能**把"这局刚打的、还没落库的"进度抹掉。
        /// <para>⚠️ 这是重复进副本（同一个进程里再 Load 一次）时会踩的坑。</para>
        /// </summary>
        private static void LoadDoesNotClobberNewerInMemory()
        {
            var fake = new FakeDao();
            fake.Seed(1, 4009, 2);      // 库里是旧的 2

            using (var store = new CachingConditionProgressStore(fake, 1))
            {
                store.SetProgress(4009, 9);                 // 内存里已经是新的 9（还没落库）
                store.LoadAsync().GetAwaiter().GetResult(); // 再读一次库

                Check("Load 不会用库里的旧值覆盖内存里的新值",
                    store.GetProgress(4009) == 9, "读到 " + store.GetProgress(4009) + "（应当是 9）");
            }
        }

        /// <summary>
        /// **单飞**：并发两次 `FlushAsync`，第二次不能真的再写一遍。
        /// <para>⚠️ 理由不是省事，是**正确性**：两次写若乱序落地，
        /// 后落的那次会把**旧的**绝对值写进库 —— 静默的数据回退。</para>
        /// </summary>
        private static void ConcurrentFlushIsSingleFlight()
        {
            var fake = new FakeDao { UpsertDelayMs = 60 };

            using (var store = new CachingConditionProgressStore(fake, 1))
            {
                store.SetProgress(4009, 4);

                Task<int> first = store.FlushAsync();
                Task<int> second = store.FlushAsync();      // 第一次还在飞 → 这一次应当直接返回 0

                Task.WaitAll(first, second);

                Check("第二次 Flush 不真写（单飞）", first.Result == 1 && second.Result == 0,
                    "第一次写 " + first.Result + " 行，第二次写 " + second.Result + " 行");
                Check("总共只落了一次库", fake.UpsertCalls == 1, "落了 " + fake.UpsertCalls + " 次");
            }
        }

        /// <summary>
        /// 落库失败后重新标脏时，**不能**把"取走之后又被改过"的键标回旧值。
        /// <para>这是本片最容易写错、后果最隐蔽的一处：**静默数据回退**。</para>
        /// <para>⚠️ 这一版**真的让一次落库在飞**（慢 DAO + 单飞语义），而不是手搓一个
        /// "假装在飞"的列表 —— 第一版就是手搓的，于是脏集根本没被取走，
        /// 断言 `DirtyCount == 0` 必然红。**"模拟状态"必须真的到达那个状态**，
        /// 否则测的是我脑子里的模型，不是代码。</para>
        /// </summary>
        private static void MarkUnflushed_DoesNotOverwriteNewerValue()
        {
            // 慢一点，制造"一次还在飞"的窗口
            var fake = new FakeDao { UpsertDelayMs = 80 };

            using (var store = new CachingConditionProgressStore(fake, 1))
            {
                store.SetProgress(4009, 3);

                // 启动一次落库：它的**同步段**会立刻把脏集取走（进 await 之前）
                Task<int> inFlight = store.FlushAsync();

                Check("落库一开始，脏集就被取空了（锁里只拷贝、不持锁做 IO）",
                    store.DirtyCount == 0, "脏集还有 " + store.DirtyCount + " 条");

                store.SetProgress(4009, 8);     // 飞的过程中主循环又改了 → 现在是 8
                inFlight.Wait();

                List<ConditionProgressRow> takenBatch = fake.LastBatch;

                Check("这一批确实是旧值 3（取走的是当时的快照）",
                    takenBatch.Count == 1 && takenBatch[0].progress == 3,
                    "取走的是 " + (takenBatch.Count > 0 ? takenBatch[0].progress.ToString() : "(空)"));

                // 落库失败 → 调用方要把这一批重新标脏
                store.MarkUnflushed(takenBatch);

                // 判据看**可观察行为**：下一次落库会写什么。
                // （不去读内部字段 —— 那样测试会绑死在实现细节上。）
                fake.ClearLastBatch();
                store.FlushAsync().GetAwaiter().GetResult();

                Check("MarkUnflushed 不会把新值（8）标回旧值（3）",
                    fake.LastBatch.Count == 1 && fake.LastBatch[0].progress == 8,
                    "下一次落库写的是 " +
                    (fake.LastBatch.Count > 0 ? fake.LastBatch[0].progress.ToString() : "(空)") + "（应当是 8）");
            }
        }

        /// <summary>对照片：值**没变过**时，`MarkUnflushed` 要真的把它标脏（否则丢数据）。</summary>
        private static void MarkUnflushed_RestoresWhenStillSame()
        {
            var fake = new FakeDao();

            using (var store = new CachingConditionProgressStore(fake, 1))
            {
                store.SetProgress(4009, 3);
                store.FlushAsync().GetAwaiter().GetResult();     // 脏集清空

                store.MarkUnflushed(new List<ConditionProgressRow>
                {
                    new ConditionProgressRow { player_id = 1, condition_key = 4009, progress = 3 }
                });

                Check("值没变过 -> MarkUnflushed 真的标脏（不会丢数据）", store.DirtyCount == 1,
                    "脏集 " + store.DirtyCount + " 条（应当是 1）");
            }
        }

        /// <summary>没有脏数据时 `FlushAsync` **不应当发 SQL**（空转的连接也是开销）。</summary>
        private static void NoDirtyDataMeansNoSql()
        {
            var fake = new FakeDao();

            using (var store = new CachingConditionProgressStore(fake, 1))
            {
                int written = store.FlushAsync().GetAwaiter().GetResult();

                Check("没脏数据 -> 写 0 行且不发 SQL", written == 0 && fake.UpsertCalls == 0,
                    "写了 " + written + " 行、发了 " + fake.UpsertCalls + " 次");
            }
        }

        /// <summary>连接串要能用；但 `Describe()`/`ToString()` **绝不能打印密码**。</summary>
        private static void ConnectionStringHidesNothing_ButDescribeHidesPassword()
        {
            var options = new DatabaseOptions
            {
                Host = "127.0.0.1",
                Port = 3306,
                Database = "nbc_db",
                User = "root",
                Password = "sup3r-s3cret"
            };

            string connectionString = options.BuildConnectionString();
            string described = options.Describe();

            Check("连接串里有密码（要连得上库）", connectionString.Contains("sup3r-s3cret"),
                "连接串里没有密码");
            Check("**Describe() 不打印密码**（日志是最容易漏密码的地方）",
                !described.Contains("sup3r-s3cret"), "Describe() 里出现了明文密码：" + described);
            Check("Describe() 只说密码设了没有", described.Contains("已设"), described);
            Check("ToString() 同样不打印密码（顺手 ToString 几乎必然发生）",
                !options.ToString().Contains("sup3r-s3cret"), options.ToString());
        }

        /// <summary>提供者不是 MySql 时要**当场报错**，不能静默按 MySql 连。</summary>
        private static void ProviderMismatchThrows()
        {
            var options = new DatabaseOptions { Provider = "PostgreSQL" };

            Check("不支持的提供者 -> 抛异常（不静默走错）",
                Throws<InvalidOperationException>(() => options.BuildConnectionString()),
                "没抛异常");
        }

        // ====================================================================
        //  二、真 MySQL
        // ====================================================================

        /// <summary>
        /// 真库那一块。
        /// <para>⚠️ 没凭据时**明确说清为什么跳过、怎么给凭据**（`Docs\11` 的做法：
        /// 密码走环境变量，不进 Git）。</para>
        /// </summary>
        /// <returns>任务。</returns>
        private static async Task MySqlSection()
        {
            var options = new DatabaseOptions();
            options.ApplyPasswordFromEnvironment();

            if (string.IsNullOrEmpty(options.Password))
            {
                Console.WriteLine("  ⏭️  **跳过**（没给数据库密码）—— 这不是「通过」，是「没跑」。");
                Console.WriteLine("      给密码的方式（二选一）：");
                Console.WriteLine("        ① PowerShell: $env:NBC_DB_PASSWORD = \"你的 root 密码\"  然后重跑本探针");
                Console.WriteLine("        ② 直接在 `DatabaseOptions.Password` 里给（**别写进仓库**）");
                Console.WriteLine("      ⚠️ 这两条用例问的是「数据真的落库了吗」，假数据替代不了（需求 §13.1 第 2 条）。");
                return;
            }

            Console.WriteLine("  目标：" + options.Describe());

            var factory = new DbConnectionFactory(options);
            PingResult ping = await factory.PingAsync().ConfigureAwait(false);

            if (!ping.Ok)
            {
                // ⚠️ 连不上时**照样算一条通过的用例**：它验的是 DB-10
                //    "数据库不可用时给出人话提示 + 服务端仍能启动"。
                Console.WriteLine("  ⚠️ 连不上（下面这条用例验的正是 DB-10 的错误提示）：");
                string pingReason = ping.Reason ?? "(没有原因，这本身就不对)";
                Console.WriteLine("     " + pingReason.Replace("\n", "\n     "));
                Check("DB-10：连不上时给的是「人话 + 该怎么办」而不是裸异常",
                    pingReason.Contains("该怎么办") || pingReason.Contains("查三件事") ||
                    pingReason.Contains("密码没给") || pingReason.Contains("密码不对") ||
                    pingReason.Contains("不存在的库") || pingReason.Contains("连不上"),
                    "消息没有可执行指引：" + pingReason);
                return;
            }

            Check("真库：连得上", true, null);

            var dao = new ConditionProgressDao(factory);

            // 用 test01 的 player_id（`Docs\08` 的初始数据里一定有）
            const long playerId = 1;

            // ---- 建表检查：表不存在就**说清跑哪个脚本**，不要在这里自己建表 ----
            try
            {
                await dao.LoadByPlayerAsync(playerId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  ❌ 表读不了：" + ex.Message);
                Check("真库：`condition_progress` 表存在", false,
                    "先跑迁移脚本（**幂等、不会清库**）：\n" +
                    "     cmd> mysql -u root -p --default-character-set=utf8mb4 < \"Docs\\08b-数据库迁移-M4S3-进度表.sql\"");
                return;
            }

            Check("真库：`condition_progress` 表存在（迁移脚本跑过了）", true, null);

            // ---- 清掉这个玩家的旧进度（让本用例可重复跑）----
            await dao.DeleteByPlayerAsync(playerId).ConfigureAwait(false);

            // ---- ① 写 ----
            using (var store = new CachingConditionProgressStore(dao, playerId))
            {
                store.SetProgress(4009, 4);
                int written = await store.FlushAsync().ConfigureAwait(false);

                Check("真库：落库写进了 1 行", written == 1, "写了 " + written + " 行");
            }

            // ---- ② 读回（**注意：这里刻意新建一个 store，模拟"服务端重启"**）----
            using (var fresh = new CachingConditionProgressStore(dao, playerId))
            {
                await fresh.LoadAsync().ConfigureAwait(false);

                Check("真库：**重启后**进度还在（这正是持久化的意义）",
                    fresh.GetProgress(4009) == 4,
                    "读回来是 " + fresh.GetProgress(4009) + "（应当是 4）");
            }

            // ---- ③ 改新值再落库，验证是覆盖而不是累加 ----
            using (var store = new CachingConditionProgressStore(dao, playerId))
            {
                await store.LoadAsync().ConfigureAwait(false);
                store.SetProgress(4009, 10);
                await store.FlushAsync().ConfigureAwait(false);
            }

            using (var fresh = new CachingConditionProgressStore(dao, playerId))
            {
                await fresh.LoadAsync().ConfigureAwait(false);

                Check("真库：写的是**绝对值**（10 覆盖 4，不是变成 14）",
                    fresh.GetProgress(4009) == 10,
                    "读回来是 " + fresh.GetProgress(4009) + "（应当是 10）");
            }

            // ---- ④ 收尾：把测试数据删掉，别留在库里 ----
            int cleaned = await dao.DeleteByPlayerAsync(playerId).ConfigureAwait(false);
            Check("真库：收尾把测试数据删干净", cleaned == 1, "删了 " + cleaned + " 行");
        }

        // ====================================================================
        //  三、连不上时的报错形状（DB-10）
        // ====================================================================

        /// <summary>
        /// **故意连不上**，验 DB-10 那条"给出明确的错误提示，而不是抛一个看不懂的异常"。
        /// <para>⚠️ 这一块**不需要真密码**，所以它永远跑得起来 ——
        /// "错误路径"是最容易只在嘴上说、从不验证的地方，而它恰恰是**用户唯一会看到的东西**。</para>
        /// <para>⚠️ 这里打的是**本机真 MySQL**（可能连得上也可能连不上），
        /// 所以断言的是"消息形状"而不是"一定失败"：</para>
        /// <list type="bullet">
        ///   <item>密码错 + 服务在跑 → 必须出现"密码不对"与`NBC_DB_PASSWORD`</item>
        ///   <item>端口没人听 → 必须出现"连不上"与三条排查步骤（**且不能是"密码不对"**）</item>
        /// </list>
        /// </summary>
        /// <returns>任务。</returns>
        private static async Task ErrorMessageSection()
        {
            // ---- ① 密码故意写错（连本机真 MySQL）----
            var wrongPassword = new DatabaseOptions { Password = "definitely-not-the-password" };
            PingResult wrong = await new DbConnectionFactory(wrongPassword).PingAsync().ConfigureAwait(false);

            if (wrong.Ok)
            {
                Check("① 本机 MySQL 居然接受了这个假密码 —— 跳过这条（不假装通过）", true,
                    "本机 root 密码恰好就是 `definitely-not-the-password`？换一个再跑。");
            }
            else
            {
                // ⚠️ 可空注解：`Reason` 声明成 `string?`，编译器**看不到**
                //    "`!Ok` 就一定非空"这条契约。落成局部变量，判据一眼可读。
                string wrongReason = wrong.Reason ?? string.Empty;

                Check("① 密码错时给的是「密码不对」+ 怎么给密码，而不是裸 `using password`",
                    wrongReason.Contains("密码不对") &&
                    wrongReason.Contains(DatabaseOptions.PasswordEnvironmentVariable),
                    "消息形状不对：\n" + wrongReason);
                Check("① 翻译类错误时**不吞掉原始异常**（原文在消息里，证据不能丢）",
                    wrongReason.Contains("原始错误"),
                    "消息里没有原始 MySQL 错误：\n" + wrongReason);
                Check("③ 报错消息里不打印密码（日志/截图都会外流）",
                    !wrongReason.Contains("definitely-not-the-password"),
                    "报错消息里出现了明文密码：\n" + wrongReason);
            }

            // ---- ② 端口没人听（一个几乎不可能被占用的端口）----
            var deadPort = new DatabaseOptions { Port = 3999, Password = "whatever" };
            PingResult dead = await new DbConnectionFactory(deadPort).PingAsync().ConfigureAwait(false);
            string deadReason = dead.Reason ?? string.Empty;

            Check("② 端口没人听 -> 报「连不上」，并给出三条排查步骤",
                !dead.Ok && deadReason.Contains("连不上") && deadReason.Contains("Get-Service"),
                "消息形状不对：\n" + (dead.Ok ? "(居然连上了)" : deadReason));

            Check("② 「连不上」**不能**被误报成「密码不对」（这两种原因要分开）",
                !deadReason.Contains("密码不对"),
                "把连接问题说成了密码问题：\n" + deadReason);
        }

        // ====================================================================
        //  四、台账（纯逻辑）
        // ====================================================================

        /// <summary>
        /// 台账是**单调**的：重复 `MarkGranted` 不产生第二条，`HasGranted` 一旦为真永远为真。
        /// <para>⚠️ 这条看着平凡，但它正是"台账不需要进度表那套值比较"的依据 ——
        /// 所以值得写成用例（一个被依赖的性质，就该被钉住）。</para>
        /// </summary>
        private static void LedgerIsMonotonic()
        {
            var fake = new FakeLedgerDao();

            using (var ledger = new MySqlRewardLedger(fake, 1))
            {
                Check("台账：没记过 -> HasGranted = false",
                    !ledger.HasGranted(ERewardOwnerKind.Achievement, 9001), "居然是 true");

                ledger.MarkGranted(ERewardOwnerKind.Achievement, 9001, 5001);
                ledger.MarkGranted(ERewardOwnerKind.Achievement, 9001, 5001);
                ledger.MarkGranted(ERewardOwnerKind.Achievement, 9001, 5001);

                Check("台账：重复标记 -> 内存里只有一条", ledger.Count == 1, "内存里 " + ledger.Count + " 条");
                Check("台账：重复标记 -> 脏集也只有一条", ledger.DirtyCount == 1, "脏集 " + ledger.DirtyCount + " 条");

                Check("台账：任务与成就是**两个编号空间**",
                    !ledger.HasGranted(ERewardOwnerKind.Quest, 9001),
                    "同编号的任务被算成发过了（串了）");
            }
        }

        /// <summary>
        /// **落库失败时那一批必须回到脏集**（否则"发了奖但台账没落库"永久丢失 ⇒ 下次重启再发一遍）。
        /// <para>⚠️ 这是台账与进度 store 最关键的差别：`MarkGranted` 是"内存里已经有它就不再标脏"，
        /// 所以脏集一旦被取走又没写成功，**那个键再也不会被标脏** —— 必须由 `FlushAsync` 自己补回。</para>
        /// <para>⚠️ 判据用**"下一次落库会不会带上它"**（可观察行为），不去读内部字段。</para>
        /// </summary>
        private static void LedgerFailureKeepsRecord()
        {
            var fake = new FakeLedgerDao { FailNextUpsert = true };

            using (var ledger = new MySqlRewardLedger(fake, 1))
            {
                ledger.MarkGranted(ERewardOwnerKind.Achievement, 9001, 5001);

                bool threw = false;

                try
                {
                    ledger.FlushAsync().GetAwaiter().GetResult();
                }
                catch (DatabaseUnavailableException)
                {
                    threw = true;
                }

                Check("台账：落库失败要**抛**（不吞掉，调用方才能知道）", threw, "没有抛");
                Check("台账：失败之后那一批**回到脏集**（否则永久丢失）", ledger.DirtyCount == 1,
                    "脏集 " + ledger.DirtyCount + " 条（应当是 1）");

                // 修好数据库，再落一次 → 应当真的带上那一条
                fake.FailNextUpsert = false;
                fake.LastBatch.Clear();
                ledger.FlushAsync().GetAwaiter().GetResult();

                Check("台账：重试时**真的写上了**那一条（不是空写）",
                    fake.LastBatch.Count == 1 && fake.LastBatch[0].owner_id == 9001,
                    "重试写的是 " + (fake.LastBatch.Count > 0 ? fake.LastBatch[0].owner_id.ToString() : "(空)"));
                Check("台账：写完脏集清空", ledger.DirtyCount == 0, "脏集 " + ledger.DirtyCount + " 条");
            }
        }

        // ====================================================================
        //  五、真库：台账
        // ====================================================================

        /// <summary>真库台账往返：写 → **新建实例（模拟重启）** → 读回 → 收尾。</summary>
        /// <returns>任务。</returns>
        private static async Task LedgerMySqlSection()
        {
            var options = new DatabaseOptions();
            options.ApplyPasswordFromEnvironment();

            if (string.IsNullOrEmpty(options.Password))
            {
                Console.WriteLine("  ⏭️  **跳过**（没给数据库密码）—— 同【二】，这不是「通过」是「没跑」。");
                return;
            }

            var factory = new DbConnectionFactory(options);
            PingResult ping = await factory.PingAsync().ConfigureAwait(false);

            if (!ping.Ok)
            {
                Console.WriteLine("  ⏭️  连不上，跳过（DB-10 的形状已由【三】验过）：");
                Console.WriteLine("     " + (ping.Reason ?? string.Empty).Replace("\n", "\n     "));
                return;
            }

            var dao = new RewardLedgerDao(factory);
            const long playerId = 1;

            try
            {
                await dao.LoadByPlayerAsync(playerId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Check("真库：`reward_granted` 表存在", false,
                    "先跑迁移脚本：Docs\\08b-数据库迁移-M4S3-进度表.sql\n     " + ex.Message);
                return;
            }

            Check("真库：`reward_granted` 表存在", true, null);

            await dao.DeleteByPlayerAsync(playerId).ConfigureAwait(false);

            // ① 写
            using (var ledger = new MySqlRewardLedger(dao, playerId))
            {
                ledger.MarkGranted(ERewardOwnerKind.Achievement, 9001, 5001);
                await ledger.FlushAsync().ConfigureAwait(false);
            }

            // ② 读回（**新建实例 = 模拟重启**）
            using (var fresh = new MySqlRewardLedger(dao, playerId))
            {
                int loaded = await fresh.LoadAsync().ConfigureAwait(false);

                Check("真库台账：**重启后**还记得发过（这是防重复发奖的全部依据）",
                    loaded == 1 && fresh.HasGranted(ERewardOwnerKind.Achievement, 9001),
                    "读回 " + loaded + " 条，HasGranted=" + fresh.HasGranted(ERewardOwnerKind.Achievement, 9001));
            }

            // ③ 重复标记 + 再落库 → **不能**变成两条（幂等，靠主键 + ON DUPLICATE KEY UPDATE）
            using (var ledger = new MySqlRewardLedger(dao, playerId))
            {
                await ledger.LoadAsync().ConfigureAwait(false);
                ledger.MarkGranted(ERewardOwnerKind.Achievement, 9001, 5001);

                Check("真库台账：重启后重复标记**不该**产生新脏数据（内存里已经有了）",
                    ledger.DirtyCount == 0, "脏集 " + ledger.DirtyCount + " 条（应当是 0）");
            }

            // ④ 唯一性：直接问库里有几行
            IReadOnlyList<RewardGrantedRow> all = await dao.LoadByPlayerAsync(playerId).ConfigureAwait(false);
            Check("真库台账：库里**恰好一行**（主键去重生效）", all.Count == 1, "库里有 " + all.Count + " 行");

            // ⑤ 收尾
            int cleaned = await dao.DeleteByPlayerAsync(playerId).ConfigureAwait(false);
            Check("真库台账：收尾把测试数据删干净", cleaned == 1, "删了 " + cleaned + " 行");
        }

        // ====================================================================
        //  六、真库：战绩落库（SRV-13）
        // ====================================================================

        /// <summary>
        /// 一局战绩真的写进库了吗（`battle_record` + `battle_player_detail` + `player_profile` 累计）。
        /// <para>⚠️ 本块**会真改 `player_profile` 的累计列**，收尾时**按同样数量减回去** ——
        /// 所以它必须记录"写之前是多少"，而不是"减一个期望值"（那样一旦中途失败就会**永久改坏**演示数据）。</para>
        /// <para>⚠️ 本块**不测强制失败回滚**：要让事务在"记录已插、明细将插"之间失败需要一个
        /// 故障注入接缝（DAO 目前没有）。⇒ 如实记为**未覆盖**，而不是假装覆盖了。</para>
        /// </summary>
        /// <returns>任务。</returns>
        private static async Task BattleRecordSection()
        {
            var options = new DatabaseOptions();
            options.ApplyPasswordFromEnvironment();

            if (string.IsNullOrEmpty(options.Password))
            {
                Console.WriteLine("  ⏭️  **跳过**（没给数据库密码）—— 同【二】，这不是「通过」是「没跑」。");
                return;
            }

            var factory = new DbConnectionFactory(options);
            PingResult ping = await factory.PingAsync().ConfigureAwait(false);

            if (!ping.Ok)
            {
                Console.WriteLine("  ⏭️  连不上，跳过（DB-10 的形状已由【三】验过）。");
                return;
            }

            // ---- ① 房号转换（纯函数，先验它）----
            Check("房号 `r12` → 12（`battle_record.room_id` 是 BIGINT）",
                BattleRecordDao.ToNumericRoomId("r12") == 12, "得到 " + BattleRecordDao.ToNumericRoomId("r12"));
            Check("房号 `7` → 7", BattleRecordDao.ToNumericRoomId("7") == 7, "得到 " + BattleRecordDao.ToNumericRoomId("7"));
            Check("房号 `abc` → 稳定哈希（同样输入永远同一结果，不可逆）",
                BattleRecordDao.ToNumericRoomId("abc") == BattleRecordDao.ToNumericRoomId("abc") &&
                BattleRecordDao.ToNumericRoomId("abc") != 0,
                "得到 " + BattleRecordDao.ToNumericRoomId("abc"));

            var dao = new BattleRecordDao(factory);

            // ---- ② 记录写之前的累计（收尾要用它**精确减回去**）----
            const long knownPlayer = 1;         // `Docs\08` 种了 1~4
            const long unknownPlayer = 999999;  // 一定没有档案

            PlayerTotals before = await ReadTotalsAsync(factory, knownPlayer).ConfigureAwait(false);

            Check("前置：player 1 有档案（能读到期累计）", before.Exists,
                "读不到 player_profile.player_id = 1；先跑一次 Docs\\08-数据库脚本.sql");

            if (!before.Exists)
            {
                return;
            }

            // ---- ③ 造一份草稿：一个**有档案**的玩家 + 一个**没档案**的玩家 ----
            var draft = new BattleRecordDraft
            {
                RoomId = "r12",
                MapId = 1001,
                SyncModeCode = 1,
                RandomSeed = 12345,
                StartTick = 0,
                EndTick = 90,
                TickIntervalMs = 33,
                IsWin = true,
            };

            draft.Players.Add(new BattlePlayerDraft(
                new BattlePlayerStat
                {
                    PlayerId = knownPlayer,
                    HeroConfigId = 1001,
                    Kill = 3,
                    Death = 1,
                    DamageDealt = 777,
                    DamageTaken = 123,
                },
                isWin: true));

            draft.Players.Add(new BattlePlayerDraft(
                new BattlePlayerStat
                {
                    PlayerId = unknownPlayer,
                    HeroConfigId = 1001,
                    Kill = 1,
                    Death = 0,
                    DamageDealt = 5,
                    DamageTaken = 0,
                },
                isWin: true));

            Check("草稿的 result_json 是**合法 JSON 形状**（含原始房号）",
                draft.ToResultJson().Contains("\"room\":\"r12\"") && draft.ToResultJson().EndsWith("]}"),
                draft.ToResultJson());

            // ---- ④ 写 ----
            BattleRecordWriteResult written = await dao.SaveAsync(draft).ConfigureAwait(false);

            Check("真库：写进去了（拿到 record_id）", written.RecordId > 0, "recordId = " + written.RecordId);
            Check("真库：明细**只给有档案的那个玩家**（跳过 1 个）",
                written.DetailRows == 1 && written.SkippedUnknownPlayers == 1 && written.SkippedGuests == 0,
                "明细 " + written.DetailRows + " 条、跳过(有 id 无档案) " + written.SkippedUnknownPlayers +
                " 个、游客 " + written.SkippedGuests + " 个");
            Console.WriteLine("      · " + written);

            // ---- ⑤ 读回来核对 ----
            BattleRecordRow? row = await ReadRecordAsync(factory, written.RecordId).ConfigureAwait(false);

            Check("真库：battle_record 读得回来", row != null, "读不到 record_id = " + written.RecordId);

            if (row != null)
            {
                Check("真库：room_id 是 12（不是 0、也不是 r12）", row.room_id == 12, "room_id = " + row.room_id);
                Check("真库：map_id / seed / 起止帧 / 时长都对",
                    row.map_id == 1001 && row.random_seed == 12345 &&
                    row.start_tick == 0 && row.end_tick == 90 && row.duration_ms == 90 * 33,
                    $"map={row.map_id} seed={row.random_seed} tick={row.start_tick}..{row.end_tick} ms={row.duration_ms}");
                Check("真库：game_mode = 1（PVE）、sync_mode = 1（状态同步）",
                    row.game_mode == 1 && row.sync_mode == 1,
                    "game_mode=" + row.game_mode + " sync_mode=" + row.sync_mode);
                Check("真库：result_json 里留着**原始房号**（数字不可逆时的补救）",
                    row.result_json != null && row.result_json.Contains("\"room\": \"r12\""),
                    row.result_json ?? "(null)");
            }

            int details = await CountDetailsAsync(factory, written.RecordId).ConfigureAwait(false);
            Check("真库：明细**恰好 1 条**（没档案那个没写进去）", details == 1, "库里有 " + details + " 条");

            // ---- ⑥ 档案累计真的涨了吗（SRV-13 的后半句）----
            PlayerTotals after = await ReadTotalsAsync(factory, knownPlayer).ConfigureAwait(false);

            Check("真库：player_profile 的累计**加上去了**（kill +3、death +1、win +1）",
                after.Kill == before.Kill + 3 && after.Death == before.Death + 1 &&
                after.Win == before.Win + 1 && after.Lose == before.Lose,
                $"before(k={before.Kill},d={before.Death},w={before.Win},l={before.Lose}) " +
                $"after(k={after.Kill},d={after.Death},w={after.Win},l={after.Lose})");

            // ---- ⑦ 收尾：**精确减回去**，并删掉记录（外键：先删明细再删记录）----
            await RestoreTotalsAsync(factory, knownPlayer, before, after).ConfigureAwait(false);
            int removedDetails = await DeleteDetailsAsync(factory, written.RecordId).ConfigureAwait(false);
            int removedRecord = await DeleteRecordAsync(factory, written.RecordId).ConfigureAwait(false);

            PlayerTotals restored = await ReadTotalsAsync(factory, knownPlayer).ConfigureAwait(false);

            Check("收尾：测试数据删干净（明细 + 记录）",
                removedDetails == 1 && removedRecord == 1,
                "删了明细 " + removedDetails + " 条、记录 " + removedRecord + " 条");
            Check("收尾：档案累计**恢复原值**（不留下测试痕迹）",
                restored.Kill == before.Kill && restored.Death == before.Death &&
                restored.Win == before.Win && restored.Lose == before.Lose,
                $"期望(k={before.Kill},d={before.Death},w={before.Win},l={before.Lose}) " +
                $"实际(k={restored.Kill},d={restored.Death},w={restored.Win},l={restored.Lose})");
        }

        // ====================================================================
        //  八、SRV-17a：游客不落库
        // ====================================================================

        /// <summary>
        /// **真库**：一局里既有游客（负数 id）又有登录玩家时 ——
        /// 游客的战绩**不能**记到任何账号头上。
        ///
        /// <para>
        /// ⚠️ 这条盯的是一个**真实发生过的数据污染**：游客以前的编号是 1、2、3…
        /// （`PlayerProfileSlots` 发的"空闲档案槽位"），而那恰好是 `player_profile` 里
        /// **真实账号的档案 id** ⇒ 库里那几条战绩记录全是游客打的，却记在 test01/test02 名下。
        /// </para>
        /// <para>
        /// 判据挑的是**最不会骗人的那个**：给游客一份"杀 99 个"的假战绩，
        /// 然后看登录玩家的 `total_kill` **只涨了他自己那 2 个**。
        /// （如果只断言"明细 1 条"，一个"把游客的 kill 加到了别人头上"的实现照样能过。）
        /// </para>
        /// </summary>
        /// <returns>任务。</returns>
        private static async Task GuestDoesNotPolluteSection()
        {
            var options = new DatabaseOptions();
            options.ApplyPasswordFromEnvironment();

            if (string.IsNullOrEmpty(options.Password))
            {
                Console.WriteLine("  ⏭️  **跳过**（没给数据库密码）—— 同【二】。");
                return;
            }

            var factory = new DbConnectionFactory(options);
            PingResult ping = await factory.PingAsync().ConfigureAwait(false);

            if (!ping.Ok)
            {
                Console.WriteLine("  ⏭️  连不上，跳过。");
                return;
            }

            // ---- ⓵ 全局不变量：库里**永远**不该有非正数 player_id 的明细 ----
            int negativeRows = await CountNegativePlayerRowsAsync(factory).ConfigureAwait(false);

            Check("真库不变量：`battle_player_detail` 里没有 `player_id <= 0` 的行（游客不该出现在库里）",
                negativeRows == 0, "居然有 " + negativeRows + " 行");

            int negativeProgress = await CountGuestProgressRowsAsync(factory).ConfigureAwait(false);

            Check("真库不变量：`condition_progress` / `reward_granted` 里也没有非正数 player_id",
                negativeProgress == 0, "居然有 " + negativeProgress + " 行");

            // ---- ⓶ 造一份"游客 + 登录玩家"的草稿 ----
            const long guestPlayer = -1;
            const long realPlayer = 1;      // `Docs\08` 种了 1~4

            PlayerTotals before = await ReadTotalsAsync(factory, realPlayer).ConfigureAwait(false);

            if (!before.Exists)
            {
                Check("前置：player 1 有档案", false, "先跑一次 Docs\\08-数据库脚本.sql");
                return;
            }

            // 2~4 号的**写入前**快照（下面 ⓹ 要拿它对比，见那里的说明）
            var otherTotalsBefore = new Dictionary<long, PlayerTotals>();

            for (long other = 2; other <= 4; other++)
            {
                otherTotalsBefore[other] = await ReadTotalsAsync(factory, other).ConfigureAwait(false);
            }

            var draft = new BattleRecordDraft
            {
                RoomId = "r17a",
                MapId = 1001,
                SyncModeCode = 1,
                RandomSeed = 1717,
                StartTick = 0,
                EndTick = 60,
                TickIntervalMs = 33,
                IsWin = true,
            };

            // 游客：战绩很夸张（99 杀），这样"污染"一眼就能看出来
            draft.Players.Add(new BattlePlayerDraft(
                new BattlePlayerStat
                {
                    PlayerId = guestPlayer,
                    HeroConfigId = 1001,
                    Kill = 99,
                    Death = 9,
                    DamageDealt = 99999,
                    DamageTaken = 99999,
                },
                isWin: true));

            // 登录玩家：真的那 2 个击杀
            draft.Players.Add(new BattlePlayerDraft(
                new BattlePlayerStat
                {
                    PlayerId = realPlayer,
                    HeroConfigId = 1001,
                    Kill = 2,
                    Death = 0,
                    DamageDealt = 100,
                    DamageTaken = 0,
                },
                isWin: true));

            var dao = new BattleRecordDao(factory);
            BattleRecordWriteResult written = await dao.SaveAsync(draft).ConfigureAwait(false);

            Check("真库：写进去了（拿到 record_id）", written.RecordId > 0, "recordId = " + written.RecordId);

            Check("真库：游客被**单独计数**为「设计如此」，而不是混进「有 id 无档案」那个告警里",
                written.SkippedGuests == 1 && written.SkippedUnknownPlayers == 0 && written.DetailRows == 1,
                written.ToString());

            // ---- ⓷ 读回来：只有登录玩家那一条明细（顺带对 2~4 号做前后快照）----
            int details = await CountDetailsAsync(factory, written.RecordId).ConfigureAwait(false);
            Check("真库：明细**只有登录玩家那 1 条**（游客那条没写进去）",
                details == 1, "库里有 " + details + " 条");

            int guestDetails = await CountGuestDetailsAsync(factory, written.RecordId).ConfigureAwait(false);

            Check("真库：这条记录里**没有任何负数 player_id 的明细**",
                guestDetails == 0, "有 " + guestDetails + " 条负数明细");

            // ---- ⓸ ⭐ 最关键的一条：累计只涨"登录玩家自己那 2 个" ----
            PlayerTotals after = await ReadTotalsAsync(factory, realPlayer).ConfigureAwait(false);

            Check("真库⭐：登录玩家的 `total_kill` **只涨 2**（游客那 99 个**一个都没算到他头上**）",
                after.Kill == before.Kill + 2,
                $"before={before.Kill} after={after.Kill}（期望 +2；如果涨了 101，就是游客的战绩被记到账号头上了）");

            Check("真库：死亡/胜负也各按**自己那份**涨（游客的 9 死不算进来）",
                after.Death == before.Death && after.Win == before.Win + 1 && after.Lose == before.Lose,
                $"before(k={before.Kill},d={before.Death},w={before.Win}) " +
                $"after(k={after.Kill},d={after.Death},w={after.Win})");

            // ---- ⓹ 别人（2~4 号档案）一个都不许被动 ----
            //  ⚠️ 这里必须**前后各快照一次**再比，否则 `othersUntouched = true` 永远为真
            //     —— 那就是本项目的 W9/假绿那一族（"一个永远为真的判据等于没判据"）。
            bool othersUntouched = true;
            string othersDetail = string.Empty;

            for (long other = 2; other <= 4; other++)
            {
                PlayerTotals snapBefore = otherTotalsBefore[other];
                PlayerTotals snapAfter = await ReadTotalsAsync(factory, other).ConfigureAwait(false);

                othersDetail += $"{other}: before(k={snapBefore.Kill},d={snapBefore.Death},w={snapBefore.Win},l={snapBefore.Lose}) " +
                                $"after(k={snapAfter.Kill},d={snapAfter.Death},w={snapAfter.Win},l={snapAfter.Lose}); ";

                if (snapAfter.Kill != snapBefore.Kill || snapAfter.Death != snapBefore.Death ||
                    snapAfter.Win != snapBefore.Win || snapAfter.Lose != snapBefore.Lose)
                {
                    othersUntouched = false;
                }
            }

            Check("真库：其他账号（2~4）的累计**前后一模一样**（这一局只该碰 1 号）",
                othersUntouched, othersDetail.Trim());

            // ---- ⓺ 收尾：删记录（先删明细）+ 把 1 号的累计精确减回去 ----
            await RestoreTotalsAsync(factory, realPlayer, before, after).ConfigureAwait(false);
            await DeleteDetailsAsync(factory, written.RecordId).ConfigureAwait(false);
            await DeleteRecordAsync(factory, written.RecordId).ConfigureAwait(false);

            PlayerTotals restored = await ReadTotalsAsync(factory, realPlayer).ConfigureAwait(false);

            Check("收尾：档案累计**恢复原值**（不留下测试痕迹）",
                restored.Kill == before.Kill && restored.Death == before.Death &&
                restored.Win == before.Win && restored.Lose == before.Lose,
                $"期望(k={before.Kill}) 实际(k={restored.Kill})");

            // 收尾之后**再查一次**全局不变量：确保这一局没在库里留下负数行
            int negativeAfter = await CountNegativePlayerRowsAsync(factory).ConfigureAwait(false);

            Check("收尾：跑完之后库里仍然没有负数 player_id 的明细",
                negativeAfter == 0, "有 " + negativeAfter + " 行");
        }

        /// <summary>数一数 `battle_player_detail` 里有几行是非正数 `player_id`。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <returns>行数（正常永远是 0）。</returns>
        private static async Task<int> CountNegativePlayerRowsAsync(DbConnectionFactory factory)
        {
            const string sql = "SELECT COUNT(*) FROM `battle_player_detail` WHERE `player_id` <= 0";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql)).ConfigureAwait(false);
            }
        }

        /// <summary>数一数进度表与台账里有几行是非正数 `player_id`。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <returns>行数（正常永远是 0）。</returns>
        private static async Task<int> CountGuestProgressRowsAsync(DbConnectionFactory factory)
        {
            const string sql =
                "SELECT (SELECT COUNT(*) FROM `condition_progress` WHERE `player_id` <= 0) + " +
                "       (SELECT COUNT(*) FROM `reward_granted` WHERE `player_id` <= 0)";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql)).ConfigureAwait(false);
            }
        }

        /// <summary>数一条记录里有几行负数 `player_id` 的明细。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <param name="recordId">记录编号。</param>
        /// <returns>行数。</returns>
        private static async Task<int> CountGuestDetailsAsync(DbConnectionFactory factory, long recordId)
        {
            const string sql = "SELECT COUNT(*) FROM `battle_player_detail` " +
                               "WHERE `record_id` = @recordId AND `player_id` <= 0";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                return await connection.ExecuteScalarAsync<int>(
                    new CommandDefinition(sql, new { recordId })).ConfigureAwait(false);
            }
        }

        /// <summary>`player_profile` 的四个累计列。</summary>
        private readonly struct PlayerTotals
        {            /// <summary>有档案吗。</summary>
            public readonly bool Exists;

            /// <summary>累计击杀。</summary>
            public readonly int Kill;

            /// <summary>累计死亡。</summary>
            public readonly int Death;

            /// <summary>累计胜场。</summary>
            public readonly int Win;

            /// <summary>累计败场。</summary>
            public readonly int Lose;

            /// <summary>造一个。</summary>
            /// <param name="exists">有档案吗。</param>
            /// <param name="kill">击杀。</param>
            /// <param name="death">死亡。</param>
            /// <param name="win">胜。</param>
            /// <param name="lose">败。</param>
            public PlayerTotals(bool exists, int kill, int death, int win, int lose)
            {
                Exists = exists;
                Kill = kill;
                Death = death;
                Win = win;
                Lose = lose;
            }
        }

        /// <summary>`battle_record` 的一行（读回来核对用）。</summary>
        private sealed class BattleRecordRow
        {
            /// <summary>房号（数字）。</summary>
            public long room_id { get; set; }

            /// <summary>模式。</summary>
            public int game_mode { get; set; }

            /// <summary>同步模式。</summary>
            public int sync_mode { get; set; }

            /// <summary>副本。</summary>
            public int map_id { get; set; }

            /// <summary>种子。</summary>
            public int random_seed { get; set; }

            /// <summary>起始帧。</summary>
            public long start_tick { get; set; }

            /// <summary>结束帧。</summary>
            public long end_tick { get; set; }

            /// <summary>时长。</summary>
            public int duration_ms { get; set; }

            /// <summary>汇总 JSON。</summary>
            public string? result_json { get; set; }
        }

        /// <summary>读一个玩家的累计。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <param name="playerId">玩家编号。</param>
        /// <returns>累计。</returns>
        private static async Task<PlayerTotals> ReadTotalsAsync(DbConnectionFactory factory, long playerId)
        {
            const string sql = "SELECT `total_kill`, `total_death`, `total_win`, `total_lose` " +
                               "FROM `player_profile` WHERE `player_id` = @playerId";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                // ⚠️ 用**可变类**接 Dapper 的结果，不用 `readonly struct`：
                //    探针第一版直接映射到 `PlayerTotals`（readonly struct + 带参构造），
                //    Dapper **构造不出来** ⇒ 静默返回 `default` ⇒ "读不到档案"的**假失败**。
                //    📌 教训：ORM 映射的目标类型要**能无参构造 + 有可写属性**，
                //       否则它不报错、只给你一个默认值 —— 又一条"静默失败"。
                TotalsDto? dto = await connection.QueryFirstOrDefaultAsync<TotalsDto>(
                    new CommandDefinition(sql, new { playerId })).ConfigureAwait(false);

                return dto == null
                    ? new PlayerTotals(false, 0, 0, 0, 0)
                    : new PlayerTotals(true, dto.total_kill, dto.total_death, dto.total_win, dto.total_lose);
            }
        }

        /// <summary>Dapper 映射用的可变 DTO（见 `ReadTotalsAsync` 里的说明）。</summary>
        private sealed class TotalsDto
        {
            /// <summary>累计击杀。</summary>
            public int total_kill { get; set; }

            /// <summary>累计死亡。</summary>
            public int total_death { get; set; }

            /// <summary>累计胜场。</summary>
            public int total_win { get; set; }

            /// <summary>累计败场。</summary>
            public int total_lose { get; set; }
        }

        /// <summary>把累计**按差值精确减回去**（不是减一个期望值 —— 见本块的说明）。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="before">写之前。</param>
        /// <param name="after">写之后。</param>
        /// <returns>任务。</returns>
        private static async Task RestoreTotalsAsync(DbConnectionFactory factory, long playerId,
                                                    PlayerTotals before, PlayerTotals after)
        {
            const string sql = "UPDATE `player_profile` SET " +
                               "`total_kill` = `total_kill` - @Kill, `total_death` = `total_death` - @Death, " +
                               "`total_win` = `total_win` - @Win, `total_lose` = `total_lose` - @Lose " +
                               "WHERE `player_id` = @playerId";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                await connection.ExecuteAsync(new CommandDefinition(sql, new
                {
                    Kill = after.Kill - before.Kill,
                    Death = after.Death - before.Death,
                    Win = after.Win - before.Win,
                    Lose = after.Lose - before.Lose,
                    PlayerId = playerId,
                })).ConfigureAwait(false);
            }
        }

        /// <summary>读一条战绩记录。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <param name="recordId">记录编号。</param>
        /// <returns>行（读不到返回 null）。</returns>
        private static async Task<BattleRecordRow?> ReadRecordAsync(DbConnectionFactory factory, long recordId)
        {
            const string sql = "SELECT `room_id`, `game_mode`, `sync_mode`, `map_id`, `random_seed`, " +
                               "`start_tick`, `end_tick`, `duration_ms`, CAST(`result_json` AS CHAR) AS result_json " +
                               "FROM `battle_record` WHERE `record_id` = @recordId";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                return await connection.QueryFirstOrDefaultAsync<BattleRecordRow>(
                    new CommandDefinition(sql, new { recordId })).ConfigureAwait(false);
            }
        }

        /// <summary>数一条记录有几条明细。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <param name="recordId">记录编号。</param>
        /// <returns>条数。</returns>
        private static async Task<int> CountDetailsAsync(DbConnectionFactory factory, long recordId)
        {
            const string sql = "SELECT COUNT(*) FROM `battle_player_detail` WHERE `record_id` = @recordId";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                return await connection.ExecuteScalarAsync<int>(
                    new CommandDefinition(sql, new { recordId })).ConfigureAwait(false);
            }
        }

        /// <summary>删一条记录的全部明细（**外键要求先删它**）。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <param name="recordId">记录编号。</param>
        /// <returns>删了几条。</returns>
        private static async Task<int> DeleteDetailsAsync(DbConnectionFactory factory, long recordId)
        {
            const string sql = "DELETE FROM `battle_player_detail` WHERE `record_id` = @recordId";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                return await connection.ExecuteAsync(
                    new CommandDefinition(sql, new { recordId })).ConfigureAwait(false);
            }
        }

        /// <summary>删一条记录。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <param name="recordId">记录编号。</param>
        /// <returns>删了几条。</returns>
        private static async Task<int> DeleteRecordAsync(DbConnectionFactory factory, long recordId)
        {
            const string sql = "DELETE FROM `battle_record` WHERE `record_id` = @recordId";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                return await connection.ExecuteAsync(
                    new CommandDefinition(sql, new { recordId })).ConfigureAwait(false);
            }
        }

        // ====================================================================
        //  八、M4-S3：账号登录（**配方阳性对照** + 真库）
        // ====================================================================

        /// <summary>
        /// 配方本身（纯逻辑，不碰数据库）。
        ///
        /// <para>
        /// ⚠️ 这里是全项目**唯一**能独立验"摘要算法对不对"的地方：
        /// `_net-probe` 的假账号表**两端都用同一个 `PasswordDigest`**，
        /// 所以把配方改错它**照样全绿**（自己和自己对，永远对得上）。
        /// 真正的判据是下面那条**与库里种子数据对账**的阳性对照。
        /// </para>
        /// </summary>
        private static void PasswordRecipeSection()
        {
            string digest123456 = PasswordDigest.FromPassword("123456");

            Check("配方：SHA256(\"123456\") == 众所周知的那个值（一次证明 UTF-8 编码 / SHA256 / 小写十六进制三步都对）",
                digest123456 == "8d969eef6ecad3c29a3a629280e686cf0c3f5d5a86aff3ca12020c923adc6c92",
                "算出来是 " + digest123456);

            Check("配方：长度就是 64 个字符（`account.password_hash` 是 CHAR(64)，多一个都存不进去）",
                digest123456.Length == PasswordDigest.HexLength && PasswordDigest.HexLength == 64,
                "长度 " + digest123456.Length);

            Check("配方：`LooksLikeDigest` 认得它、也认得大写（`Look` 只管形状）",
                PasswordDigest.LooksLikeDigest(digest123456)
                && PasswordDigest.LooksLikeDigest(digest123456.ToUpperInvariant()),
                "自检失败");

            Check("配方：`LooksLikeDigest` 拒绝长度不对 / 非十六进制的输入",
                !PasswordDigest.LooksLikeDigest(null)
                && !PasswordDigest.LooksLikeDigest("")
                && !PasswordDigest.LooksLikeDigest("abc")
                && !PasswordDigest.LooksLikeDigest(new string('z', 64)),
                "自检失败");

            // ⚠️ 拼接顺序**是契约的一部分**：salt 在前还是摘要在前，结果完全不同
            Check("配方：拼接顺序敏感（salt+摘要 ≠ 摘要+salt —— 顺序写反了这条会红）",
                PasswordDigest.StoredHash("S", "D") != PasswordDigest.StoredHash("D", "S"),
                "两个顺序算出了同一个值");

            // 恒定时间比较：行为上必须与普通比较一致（否则上层判据全错）
            string a = digest123456;
            string b = string.Copy(a);
            string last = a.Substring(0, 63) + (a[63] == '0' ? '1' : '0');

            Check("恒定时间比较：完全一样 -> true",
                PasswordDigest.FixedTimeEquals(a, b), "返回了 false");

            Check("恒定时间比较：**只有最后一个字符不同** -> false（不是「看起来像就算过」）",
                !PasswordDigest.FixedTimeEquals(a, last), "返回了 true");

            Check("恒定时间比较：长度不同 -> false；null -> false",
                !PasswordDigest.FixedTimeEquals(a, a.Substring(0, 63))
                && !PasswordDigest.FixedTimeEquals(null, a)
                && !PasswordDigest.FixedTimeEquals(a, null),
                "自检失败");
        }

        /// <summary>真库：库里那 4 个种子账号 + 通过 `AccountDirectory` 登录。</summary>
        /// <returns>任务。</returns>
        private static async Task LoginMySqlSection()
        {
            var options = new DatabaseOptions();
            options.ApplyPasswordFromEnvironment();

            if (string.IsNullOrEmpty(options.Password))
            {
                Console.WriteLine("  ⏭️  **跳过**（没给数据库密码）—— 同【二】。");
                Console.WriteLine("         ⚠️ 这条跳过很贵：**配方与种子数据对账的阳性对照就在这一节里**。");
                return;
            }

            var factory = new DbConnectionFactory(options);
            PingResult ping = await factory.PingAsync().ConfigureAwait(false);

            if (!ping.Ok)
            {
                Console.WriteLine("  ⏭️  连不上，跳过。");
                return;
            }

            // ----------------------------------------------------------------
            //  ① 阳性对照：库里的 `password_hash` **必须**等于我们算出来的
            // ----------------------------------------------------------------
            List<AccountRow> rows = await ReadAccountsAsync(factory).ConfigureAwait(false);

            Check("真库：`account` 表里有种子账号（`Docs\\08` 种了 4 个 test01~test04）",
                rows.Count >= 4, "读到 " + rows.Count + " 个");

            int matched = 0;
            string mismatch = string.Empty;

            for (int i = 0; i < rows.Count; i++)
            {
                AccountRow row = rows[i];
                string mine = PasswordDigest.StoredHash(row.salt, PasswordDigest.FromPassword("123456"));

                if (mine == row.password_hash)
                {
                    matched++;
                }
                else
                {
                    mismatch = row.username + "：库里 " + row.password_hash + "，我算 " + mine;
                }
            }

            Check("真库**阳性对照**：每个种子账号的 password_hash == SHA256(salt + SHA256(\"123456\"))（配方一改这条必红）",
                rows.Count >= 4 && matched == rows.Count,
                matched + "/" + rows.Count + " 对得上" + (mismatch.Length == 0 ? "" : "；第一个不对的：" + mismatch));

            // ----------------------------------------------------------------
            //  ② 登录：账号 → **它自己的** player_id
            // ----------------------------------------------------------------
            AccountDirectory directory = AccountDirectory.Load(factory);

            Check("真库：`AccountDirectory` 一次 JOIN 把账号与档案都读进来了",
                directory.Count >= 4, directory.Describe());

            string digest = PasswordDigest.FromPassword("123456");
            LoginResult ok = directory.Login("test01", digest);

            Check("真库：test01 + 123456 登录成功，且拿到正数 player_id",
                ok.Accepted && ok.PlayerId > 0, ok.ToString());

            long? test01ProfileId = await ReadProfileIdAsync(factory, "test01").ConfigureAwait(false);

            Check("真库：登录拿到的 player_id **就是 test01 自己档案的 id**（不是「第几个连上来的」）",
                test01ProfileId != null && ok.PlayerId == test01ProfileId.Value,
                $"登录给的是 {ok.PlayerId}，库里 test01 的档案是 {(test01ProfileId == null ? "没查到" : test01ProfileId.Value.ToString())}");

            Check("真库：昵称来自**档案**（`player_profile.nickname`）",
                ok.Nickname == "测试玩家一", "昵称是 \"" + ok.Nickname + "\"");

            LoginResult again = directory.Login("test01", digest);

            Check("真库：同一个账号连第二次还是同一个 player_id（重连不换人）",
                again.Accepted && again.PlayerId == ok.PlayerId,
                $"第一次 {ok.PlayerId}，第二次 {again.PlayerId}");

            LoginResult upper = directory.Login("TEST01", digest);

            Check("真库：登录名**大小写不敏感**（跟库 `utf8mb4_general_ci` 的 UNIQUE 键保持一致）",
                upper.Accepted && upper.PlayerId == ok.PlayerId,
                "TEST01 的结果：" + upper.ToString());

            LoginResult wrong = directory.Login("test01", PasswordDigest.FromPassword("654321"));

            Check("真库：密码不对 → WrongPassword（且理由说的是「密码不对」）",
                !wrong.Accepted && wrong.Rejection == ELoginRejection.WrongPassword && wrong.Reason.Contains("密码"),
                wrong.ToString());

            LoginResult missing = directory.Login("查无此号", digest);

            Check("真库：账号不存在 → UnknownAccount（和「密码不对」分得开 —— 本项目的取舍）",
                !missing.Accepted && missing.Rejection == ELoginRejection.UnknownAccount,
                missing.ToString());

            LoginResult badShape = directory.Login("test01", "不是摘要");

            Check("真库：摘要形状不对 → BadRequest（**先自检形状**，省得把畸形输入当成「密码错」）",
                !badShape.Accepted && badShape.Rejection == ELoginRejection.BadRequest,
                badShape.ToString());

            Check("真库：登录结果与 Describe 里**都不含摘要/密码**（敏感信息没有出口）",
                !ok.ToString().Contains(digest) && !directory.Describe().Contains(digest)
                && !wrong.Reason.Contains(digest),
                "摘要泄漏了");

            // ----------------------------------------------------------------
            //  ③ "账号在、但没有档案" ⇒ NoProfile（**临时插一行**，跑完删掉）
            // ----------------------------------------------------------------
            const string tempAccount = "nbc_temp_noprofile";

            try
            {
                await InsertProfilelessAccountAsync(factory, tempAccount).ConfigureAwait(false);

                // ⚠️ 必须**重新 Load**：账号表是启动时读一次的（刻意的取舍），
                //    不重读就等于在验"服务端重启后才认得的东西"。
                AccountDirectory reloaded = AccountDirectory.Load(factory);
                LoginResult noProfile = reloaded.Login(tempAccount, PasswordDigest.FromPassword("temppw"));

                Check("真库：账号在、密码对、**但没有档案** → NoProfile（绝不能给一个假 id 糊过去）",
                    !noProfile.Accepted && noProfile.Rejection == ELoginRejection.NoProfile,
                    noProfile.ToString());

                Check("真库：没有档案的账号会被 `Describe` 报出来（否则只会被当成「密码错了」）",
                    reloaded.WithoutProfileCount == 1 && reloaded.Describe().Contains("没有档案"),
                    reloaded.Describe());

                Check("真库：密码不对时**不会**先暴露「这个账号没有档案」（先验密码，再看档案）",
                    reloaded.Login(tempAccount, PasswordDigest.FromPassword("错的")).Rejection
                        == ELoginRejection.WrongPassword,
                    "顺序反了：没验密码就说了档案的事");
            }
            finally
            {
                int removed = await DeleteAccountAsync(factory, tempAccount).ConfigureAwait(false);
                Console.WriteLine("      · 收尾：删掉临时账号 " + removed + " 行");
            }

            // ----------------------------------------------------------------
            //  ④ `last_login_at` 真的写进去了（AccountDao + LoginAuditWriter）
            // ----------------------------------------------------------------
            DateTime? beforeLoginAt = await ReadLastLoginAsync(factory, ok.AccountId).ConfigureAwait(false);

            try
            {
                var auditDao = new AccountDao(factory);

                using (var writer = new LoginAuditWriter(auditDao))
                {
                    writer.Submit(ok.AccountId);

                    bool drained = await writer.FlushAsync(3000).ConfigureAwait(false);

                    Check("真库：`LoginAuditWriter` 入队 1 次、写成功 1 次（没有失败）",
                        drained && writer.Submitted == 1 && writer.Written == 1 && writer.Failed == 0,
                        writer.DescribeStats() + "（drained=" + drained + "）");
                }

                DateTime? afterLoginAt = await ReadLastLoginAsync(factory, ok.AccountId).ConfigureAwait(false);

                Check("真库：`account.last_login_at` **真的被写了**（登录在库里留下了证据）",
                    afterLoginAt != null, "还是 null —— 审计没落库");
            }
            finally
            {
                // 收尾：把 last_login_at 还原成原样（**不要留下测试痕迹**）
                await RestoreLastLoginAsync(factory, ok.AccountId, beforeLoginAt).ConfigureAwait(false);
                DateTime? restored = await ReadLastLoginAsync(factory, ok.AccountId).ConfigureAwait(false);
                Console.WriteLine("      · 收尾：last_login_at 还原为 " +
                                  (restored == null ? "null" : restored.Value.ToString("yyyy-MM-dd HH:mm:ss")));
            }
        }

        /// <summary>一行账号（Dapper 映射用；列名与 SELECT 别名一致）。</summary>
        private sealed class AccountRow
        {
            /// <summary>`account_id`。</summary>
            public long account_id { get; set; }

            /// <summary>`username`。</summary>
            public string username { get; set; } = string.Empty;

            /// <summary>`password_hash`。</summary>
            public string password_hash { get; set; } = string.Empty;

            /// <summary>`salt`。</summary>
            public string salt { get; set; } = string.Empty;
        }

        /// <summary>读全部账号（**只看与配方有关的三列**）。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <returns>账号行。</returns>
        private static async Task<List<AccountRow>> ReadAccountsAsync(DbConnectionFactory factory)
        {
            const string sql = "SELECT `account_id`, `username`, `password_hash`, `salt` " +
                               "FROM `account` WHERE `username` LIKE 'test%' ORDER BY `account_id`";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                return (await connection.QueryAsync<AccountRow>(new CommandDefinition(sql)).ConfigureAwait(false))
                    .AsList();
            }
        }

        /// <summary>查某个账号的档案 id（**用 SQL 直接问库**，不经过被测代码）。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <param name="username">登录名。</param>
        /// <returns>档案 id；没有则是 null。</returns>
        private static async Task<long?> ReadProfileIdAsync(DbConnectionFactory factory, string username)
        {
            const string sql = "SELECT p.`player_id` FROM `account` a " +
                               "JOIN `player_profile` p ON p.`account_id` = a.`account_id` " +
                               "WHERE a.`username` = @username";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                return await connection.ExecuteScalarAsync<long?>(
                    new CommandDefinition(sql, new { username })).ConfigureAwait(false);
            }
        }

        /// <summary>插一个**没有档案**的账号（临时数据，收尾会删）。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <param name="username">登录名。</param>
        /// <returns>任务。</returns>
        private static async Task InsertProfilelessAccountAsync(DbConnectionFactory factory, string username)
        {
            const string salt = "0f1e2d3c4b5a6978";
            string hash = PasswordDigest.StoredHash(salt, PasswordDigest.FromPassword("temppw"));

            const string sql = "INSERT INTO `account` (`username`, `password_hash`, `salt`) " +
                               "VALUES (@username, @hash, @salt)";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(sql, new { username, hash, salt })).ConfigureAwait(false);
            }
        }

        /// <summary>删一个账号（收尾）。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <param name="username">登录名。</param>
        /// <returns>删了几行。</returns>
        private static async Task<int> DeleteAccountAsync(DbConnectionFactory factory, string username)
        {
            const string sql = "DELETE FROM `account` WHERE `username` = @username";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                return await connection.ExecuteAsync(
                    new CommandDefinition(sql, new { username })).ConfigureAwait(false);
            }
        }

        /// <summary>读 `last_login_at`。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <param name="accountId">账号编号。</param>
        /// <returns>时间；是 NULL 则为 null。</returns>
        private static async Task<DateTime?> ReadLastLoginAsync(DbConnectionFactory factory, long accountId)
        {
            const string sql = "SELECT `last_login_at` FROM `account` WHERE `account_id` = @accountId";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                return await connection.ExecuteScalarAsync<DateTime?>(
                    new CommandDefinition(sql, new { accountId })).ConfigureAwait(false);
            }
        }

        /// <summary>把 `last_login_at` 还原（收尾，**不留测试痕迹**）。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <param name="accountId">账号编号。</param>
        /// <param name="original">原来的值（null = 还原成 NULL）。</param>
        /// <returns>任务。</returns>
        private static async Task RestoreLastLoginAsync(DbConnectionFactory factory, long accountId, DateTime? original)
        {
            const string sql = "UPDATE `account` SET `last_login_at` = @original WHERE `account_id` = @accountId";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(sql, new { accountId, original })).ConfigureAwait(false);
            }
        }

        // ====================================================================
        //  九、M4-S3 服务端权威：成就进度落库 / 重启后还在 / 不重复发奖
        // ====================================================================

        /// <summary>
        /// **真库**：服务端权威判定的"跨局累计"是否真的成立。
        ///
        /// <para>
        /// 这一节验的是整件事**唯一无法用内存证明**的一条：
        /// 「**关掉服务端再起来，进度还在，而且不会重复发奖**」。
        /// 判据挑的是最不会骗人的那三个：
        /// ① 库里 `condition_progress` 真的多了那一行；
        /// ② **新造一个权威（= 模拟重启）** 之后，进度还是 1（**不是被 Register 覆盖成 0**）；
        /// ③ 重启后那条"已达成 ⇒ 当场回调"被**台账**挡住（`防重复 1 次`），台账行数**还是 1**。
        /// </para>
        /// </summary>
        /// <returns>任务。</returns>
        private static async Task AchievementAuthoritySection()
        {
            var options = new DatabaseOptions();
            options.ApplyPasswordFromEnvironment();

            if (string.IsNullOrEmpty(options.Password))
            {
                Console.WriteLine("  ⏭️  **跳过**（没给数据库密码）—— 同【二】。");
                return;
            }

            var factory = new DbConnectionFactory(options);
            PingResult ping = await factory.PingAsync().ConfigureAwait(false);

            if (!ping.Ok)
            {
                Console.WriteLine("  ⏭️  连不上，跳过。");
                return;
            }

            // ---- ⓵ 真表（`Configs\Design`）----
            QuestTables tables = LoadQuestTables();

            Check("权威：读到了真表（任务 / 成就 / 条件 / 奖励）",
                tables.AchievementCount >= 3 && tables.ConditionCount >= 8,
                tables.Describe());

            Check("权威：加载时**校验跨表引用**（成就 9002 的条件 4010 指向狼王 6003、奖励 5003）",
                tables.FindCondition(4010)?.Def.TargetId == 6003 &&
                tables.FindReward(5003)?.Exp == 500,
                "条件 4010 = " + (tables.FindCondition(4010)?.ToString() ?? "(没有)") +
                "；奖励 5003 = " + (tables.FindReward(5003)?.ToString() ?? "(没有)"));

            // ---- ⓶ 前置：把 player 1 的进度与台账清空（**先报清楚清了什么**）----
            const long playerId = 1;

            int progressBefore = await CountProgressAsync(factory, playerId).ConfigureAwait(false);
            int ledgerBefore = await CountLedgerAsync(factory, playerId).ConfigureAwait(false);

            Console.WriteLine("      · 前置：player 1 原有进度 " + progressBefore + " 条、台账 " + ledgerBefore + " 条 → 清空");

            await ClearProgressAsync(factory, playerId).ConfigureAwait(false);
            await ClearLedgerAsync(factory, playerId).ConfigureAwait(false);

            try
            {
                // ---- ⓷ 第一次"进游戏"：打一只狼王 → 解锁成就 9002 ----
                using (var first = NewAuthority(tables, factory, playerId, out var notes1))
                {
                    Task? loading;
                    bool tracked = first.Track(playerId, "测试玩家一", out loading);

                    Check("权威：追踪玩家 1（有档案 ⇒ 追踪；游客才不追踪）", tracked, "tracked=" + tracked);

                    if (loading != null)
                    {
                        await loading.ConfigureAwait(false);        // ⚠️ 等它读完（读不完就没有"跨局"可言）
                    }

                    first.ApplyFact(new ProgressFact(playerId, EConditionEvent.KillMonster, 6003, 1));

                    bool flushed = await first.FlushAllAsync(3000).ConfigureAwait(false);

                    Check("权威：冲库成功（不冲库 = 这一局的累计白打）", flushed, "FlushAllAsync 返回 false");

                    Check("权威：**服务端判定出解锁**（台账里有成就 9002）",
                        first.Describe().Contains("解锁 1 个"), first.Describe());

                    Check("权威：日志里说得出「谁解锁了哪个成就、奖励是多少」",
                        DescribeLines(notes1).Contains("9002") && DescribeLines(notes1).Contains("5003"),
                        DescribeLines(notes1));
                }

                // ---- ⓸ 读库对账 ----
                int progressNow = await CountProgressAsync(factory, playerId).ConfigureAwait(false);

                Check("真库：`condition_progress` 真的多了行（4010=击杀狼王 已达成、4011=任意怪 在累计）",
                    progressNow >= 2 &&
                    await ReadProgressAsync(factory, playerId, 4010).ConfigureAwait(false) == 1 &&
                    await ReadProgressAsync(factory, playerId, 4011).ConfigureAwait(false) == 1,
                    "进度行数 " + progressNow + "；4010=" +
                    await ReadProgressAsync(factory, playerId, 4010).ConfigureAwait(false) + "；4011=" +
                    await ReadProgressAsync(factory, playerId, 4011).ConfigureAwait(false));

                Check("真库：**「任意怪」条件（targetId=0）也吃到了这条事实**（服务端匹配语义与客户端一致）",
                    await ReadProgressAsync(factory, playerId, 4011).ConfigureAwait(false) == 1,
                    "条件 4011 = " + await ReadProgressAsync(factory, playerId, 4011).ConfigureAwait(false));

                Check("真库：`reward_granted` 真的多了那一行（成就 9002）",
                    await CountLedgerAsync(factory, playerId).ConfigureAwait(false) == 1,
                    "台账行数 " + await CountLedgerAsync(factory, playerId).ConfigureAwait(false));

                // ---- ⓹ ⭐ 模拟重启：新造一个权威，进度应当**从库里读回来** ----
                using (var second = NewAuthority(tables, factory, playerId, out var notes2))
                {
                    Task? loading;
                    second.Track(playerId, "测试玩家一", out loading);

                    if (loading != null)
                    {
                        await loading.ConfigureAwait(false);
                    }

                    // ⚠️ 这一条盯的是"LoadAsync 必须在 Register 之前"：
                    //    顺序写反 ⇒ 登记时读到 0 ⇒ 这里会是 0（而且冲库会把库里的 1 覆盖成 0）
                    Check("真库⭐：**重启后进度还在**（4010 仍是 1，不是被 Register 覆盖成 0）",
                        await ReadProgressAsync(factory, playerId, 4010).ConfigureAwait(false) == 1,
                        "条件 4010 = " + await ReadProgressAsync(factory, playerId, 4010).ConfigureAwait(false));

                    Check("真库⭐：**重启后不重复发奖**（台账挡住了那次「登录即解锁」的回调）",
                        second.Describe().Contains("防重复 1 次"),
                        second.Describe());

                    // 再打一只狼王 ⇒ **未达成的**条件接着累计（4011：1 → 2）。
                    // ⚠️ 为什么不用 4010 验"累计"：`ConditionTracker` 里**进度是钳位的**
                    //    ——「已经达成了：不再累计」（`ConditionTracker.cs:320`）。
                    //    所以 4010 会**停在 1**（那是设计，不是 bug）。要验"跨局累计"必须挑一个**还没达成**的条件。
                    second.ApplyFact(new ProgressFact(playerId, EConditionEvent.KillMonster, 6003, 1));
                    await second.FlushAllAsync(3000).ConfigureAwait(false);

                    Check("真库⭐：**第二局接着累计**（4011「任意怪」：1 → 2）—— 这才是「跨局累计」本身",
                        await ReadProgressAsync(factory, playerId, 4011).ConfigureAwait(false) == 2,
                        "条件 4011 = " + await ReadProgressAsync(factory, playerId, 4011).ConfigureAwait(false));

                    Check("真库：**已达成的条件停在需求值**（4010 还是 1 —— 进度钳位是设计，不是丢数据）",
                        await ReadProgressAsync(factory, playerId, 4010).ConfigureAwait(false) == 1,
                        "条件 4010 = " + await ReadProgressAsync(factory, playerId, 4010).ConfigureAwait(false));

                    Check("真库：台账**还是 1 行**（没有因为解锁过就再发一次）",
                        await CountLedgerAsync(factory, playerId).ConfigureAwait(false) == 1,
                        "台账行数 " + await CountLedgerAsync(factory, playerId).ConfigureAwait(false));

                    Check("权威：两次会话的日志都留下了痕迹（排查时有据可查）",
                        notes2.Count > 0, "第二次会话的说明条数 = " + notes2.Count);
                }

                // ---- ⓺ ⭐ "已达成但没记过台账" ⇒ **启动时必须补上**（这才是 `resetProgress:false` 的意义）----
                //  ⚠️ 这一条专门盯**次序**：`LoadAsync` 必须在 `Register` 之前。
                //     顺序写反的话，登记时读到的是 0 ⇒ 那条"已达成 ⇒ 当场回调"**不会触发**
                //     ⇒ 这个成就永远补不上（而进度看上去还是对的，所以最难发现）。
                await ClearLedgerAsync(factory, playerId).ConfigureAwait(false);
                await SetProgressAsync(factory, playerId, 4010, 1).ConfigureAwait(false);

                using (var third = NewAuthority(tables, factory, playerId, out var notes3))
                {
                    Task? loading;
                    third.Track(playerId, "测试玩家一", out loading);

                    if (loading != null)
                    {
                        await loading.ConfigureAwait(false);
                    }

                    // ⚠️ 必须**先冲库再查库**：台账是**写回缓存**（`MySqlRewardLedger`），
                    //    `MarkGranted` 只改内存，不冲库时库里当然还是 0 —— 本探针第一版就栽在这
                    //    （日志明明写着"解锁"，查库是 0 行）。这是"判据要挑对"的又一例。
                    await third.FlushAllAsync(3000).ConfigureAwait(false);

                    Check("真库⭐：**库里已达成、但台账没记过** ⇒ 启动时补发（`resetProgress:false` 要的就是它）",
                        await CountLedgerAsync(factory, playerId).ConfigureAwait(false) == 1,
                        "台账行数 " + await CountLedgerAsync(factory, playerId).ConfigureAwait(false) +
                        "；说明：" + DescribeLines(notes3));
                }
            }
            finally
            {
                // ---- ⓺ 收尾：把 player 1 的进度与台账清干净（**不留测试痕迹**）----
                int removedProgress = await ClearProgressAsync(factory, playerId).ConfigureAwait(false);
                int removedLedger = await ClearLedgerAsync(factory, playerId).ConfigureAwait(false);

                Console.WriteLine("      · 收尾：删掉进度 " + removedProgress + " 条、台账 " + removedLedger + " 条");

                Check("收尾：player 1 的进度与台账**都清空了**", 
                    await CountProgressAsync(factory, playerId).ConfigureAwait(false) == 0 &&
                    await CountLedgerAsync(factory, playerId).ConfigureAwait(false) == 0,
                    "还剩进度 " + await CountProgressAsync(factory, playerId).ConfigureAwait(false) +
                    " 条、台账 " + await CountLedgerAsync(factory, playerId).ConfigureAwait(false) + " 条");
            }
        }

        /// <summary>造一个**接了真库**的权威（每个会话一套，用来模拟重启）。</summary>
        /// <param name="tables">四张表。</param>
        /// <param name="factory">连接工厂。</param>
        /// <param name="playerId">玩家编号（只用于日志）。</param>
        /// <param name="notes">收到的说明（断言用）。</param>
        /// <returns>权威。</returns>
        private static AchievementAuthority NewAuthority(
            QuestTables tables, DbConnectionFactory factory, long playerId, out List<string> notes)
        {
            var collected = new List<string>();

            var authority = new AchievementAuthority(
                tables,
                pid => new CachingConditionProgressStore(new ConditionProgressDao(factory), pid),
                pid => new MySqlRewardLedger(new RewardLedgerDao(factory), pid));

            authority.Note += line => collected.Add(line);
            notes = collected;

            return authority;
        }

        /// <summary>读真表（`Configs\Design`）。</summary>
        /// <returns>四张表。</returns>
        private static QuestTables LoadQuestTables()
        {
            if (!NBC.Server.Game.ServerTables.TryResolveConfigDir(out string dir))
            {
                throw new InvalidOperationException("探针找不到配置表目录 `Configs\\Design`。");
            }

            QuestTables tables;
            string error;

            if (!QuestTables.TryLoad(dir, out tables, out error))
            {
                throw new InvalidOperationException("探针读不到任务/成就表（" + dir + "）：" + error);
            }

            return tables;
        }

        /// <summary>数一个玩家有几条进度。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <param name="playerId">玩家编号。</param>
        /// <returns>条数。</returns>
        private static async Task<int> CountProgressAsync(DbConnectionFactory factory, long playerId)
        {
            const string sql = "SELECT COUNT(*) FROM `condition_progress` WHERE `player_id` = @playerId";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                return await connection.ExecuteScalarAsync<int>(
                    new CommandDefinition(sql, new { playerId })).ConfigureAwait(false);
            }
        }

        /// <summary>数一个玩家有几条发奖台账。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <param name="playerId">玩家编号。</param>
        /// <returns>条数。</returns>
        private static async Task<int> CountLedgerAsync(DbConnectionFactory factory, long playerId)
        {
            const string sql = "SELECT COUNT(*) FROM `reward_granted` WHERE `player_id` = @playerId";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                return await connection.ExecuteScalarAsync<int>(
                    new CommandDefinition(sql, new { playerId })).ConfigureAwait(false);
            }
        }

        /// <summary>读一个条件的进度（**直接问库**，不经过被测代码）。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="conditionKey">条件编号。</param>
        /// <returns>进度（没记录时是 0）。</returns>
        private static async Task<int> ReadProgressAsync(DbConnectionFactory factory, long playerId, int conditionKey)
        {
            const string sql = "SELECT `progress` FROM `condition_progress` " +
                               "WHERE `player_id` = @playerId AND `condition_key` = @conditionKey";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                return await connection.ExecuteScalarAsync<int?>(
                    new CommandDefinition(sql, new { playerId, conditionKey })).ConfigureAwait(false) ?? 0;
            }
        }

        /// <summary>直接往库里写一条进度（**模拟"上一次会话留下的进度"**，绕过被测代码）。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="conditionKey">条件编号。</param>
        /// <param name="progress">进度（绝对值）。</param>
        /// <returns>任务。</returns>
        private static async Task SetProgressAsync(
            DbConnectionFactory factory, long playerId, int conditionKey, int progress)
        {
            const string sql =
                "INSERT INTO `condition_progress` (`player_id`, `condition_key`, `progress`) " +
                "VALUES (@playerId, @conditionKey, @progress) " +
                "ON DUPLICATE KEY UPDATE `progress` = @progress";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(sql, new { playerId, conditionKey, progress })).ConfigureAwait(false);
            }
        }

        /// <summary>清掉一个玩家的进度（收尾）。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <param name="playerId">玩家编号。</param>
        /// <returns>删了几条。</returns>
        private static async Task<int> ClearProgressAsync(DbConnectionFactory factory, long playerId)
        {
            const string sql = "DELETE FROM `condition_progress` WHERE `player_id` = @playerId";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                return await connection.ExecuteAsync(
                    new CommandDefinition(sql, new { playerId })).ConfigureAwait(false);
            }
        }

        /// <summary>清掉一个玩家的发奖台账（收尾）。</summary>
        /// <param name="factory">连接工厂。</param>
        /// <param name="playerId">玩家编号。</param>
        /// <returns>删了几条。</returns>
        private static async Task<int> ClearLedgerAsync(DbConnectionFactory factory, long playerId)
        {
            const string sql = "DELETE FROM `reward_granted` WHERE `player_id` = @playerId";

            using (MySqlConnection connection = await factory.OpenAsync().ConfigureAwait(false))
            {
                return await connection.ExecuteAsync(
                    new CommandDefinition(sql, new { playerId })).ConfigureAwait(false);
            }
        }

        /// <summary>把若干条说明拼成一行（断言失败时看得到）。</summary>
        /// <param name="lines">说明。</param>
        /// <returns>拼接结果。</returns>
        private static string DescribeLines(List<string> lines)
        {
            return lines.Count == 0 ? "(没有说明)" : string.Join(" | ", lines);
        }

        // ====================================================================
        //  小工具
        // ====================================================================

        /// <summary>断言一件事。</summary>
        /// <param name="label">用例名。</param>
        /// <param name="ok">成立吗。</param>
        /// <param name="detail">不成立时的说明。</param>
        private static void Check(string label, bool ok, string? detail)
        {
            if (ok)
            {
                s_passed++;
                Console.WriteLine("  ✅ " + label);
                return;
            }

            s_failed++;
            Console.WriteLine("  ❌ " + label);
            Console.WriteLine("      " + detail);
        }

        /// <summary>这段代码抛不抛指定异常。</summary>
        /// <typeparam name="T">异常类型。</typeparam>
        /// <param name="action">要跑的代码。</param>
        /// <returns>抛了返回 true。</returns>
        private static bool Throws<T>(Action action) where T : Exception
        {
            try
            {
                action();
                return false;
            }
            catch (T)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>内存版台账 DAO：不需要 MySQL，可以**故意让下一次写失败**。</summary>
        private sealed class FakeLedgerDao : IRewardLedgerDao
        {
            /// <summary>库里的台账行（键 = `种类:编号`）。</summary>
            private readonly Dictionary<string, RewardGrantedRow> m_rows = new Dictionary<string, RewardGrantedRow>();

            /// <summary>最近一次写入收到的那一批。</summary>
            private readonly List<RewardGrantedRow> m_lastBatch = new List<RewardGrantedRow>();

            /// <summary>让**下一次**写抛异常（验"失败要把那一批放回脏集"）。</summary>
            public bool FailNextUpsert { get; set; }

            /// <summary>最近一次写入的那一批。</summary>
            public List<RewardGrantedRow> LastBatch
            {
                get { return m_lastBatch; }
            }

            /// <summary>读全部。</summary>
            /// <param name="playerId">玩家编号。</param>
            /// <param name="cancellationToken">取消令牌。</param>
            /// <returns>行。</returns>
            public Task<IReadOnlyList<RewardGrantedRow>> LoadByPlayerAsync(
                long playerId, CancellationToken cancellationToken = default(CancellationToken))
            {
                var rows = new List<RewardGrantedRow>(m_rows.Values);
                return Task.FromResult<IReadOnlyList<RewardGrantedRow>>(rows);
            }

            /// <summary>写一批。</summary>
            /// <param name="playerId">玩家编号。</param>
            /// <param name="rows">行。</param>
            /// <param name="cancellationToken">取消令牌。</param>
            /// <returns>写了几行。</returns>
            public Task<int> InsertBatchAsync(
                long playerId, IReadOnlyList<RewardGrantedRow> rows,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                if (FailNextUpsert)
                {
                    FailNextUpsert = false;
                    throw new DatabaseUnavailableException(
                        "（假 DAO）故意让这一次写失败，用来验「失败之后那一批会不会回到脏集」。", null);
                }

                m_lastBatch.Clear();
                m_lastBatch.AddRange(rows);

                for (int i = 0; i < rows.Count; i++)
                {
                    m_rows[rows[i].owner_kind + ":" + rows[i].owner_id] = rows[i];
                }

                return Task.FromResult(rows.Count);
            }

            /// <summary>删掉一个玩家的台账。</summary>
            /// <param name="playerId">玩家编号。</param>
            /// <param name="cancellationToken">取消令牌。</param>
            /// <returns>删了几行。</returns>
            public Task<int> DeleteByPlayerAsync(long playerId, CancellationToken cancellationToken = default(CancellationToken))
            {
                int count = m_rows.Count;
                m_rows.Clear();
                return Task.FromResult(count);
            }
        }

        /// <summary>内存版 DAO：**不需要 MySQL**，只记"被要求写了什么"。</summary>
        private sealed class FakeDao : IConditionProgressDao
        {
            /// <summary>库里现有的值（条件编号 → 值）。</summary>
            private readonly Dictionary<int, int> m_store = new Dictionary<int, int>();

            /// <summary>按顺序记下每一次落库的值（诊断"到底写了什么"用）。</summary>
            public readonly List<int> Stored = new List<int>();

            /// <summary>**最近一次**落库收到的那一批（断言"下一次会写什么"用）。</summary>
            private readonly List<ConditionProgressRow> m_lastBatch = new List<ConditionProgressRow>();

            /// <summary>`UpsertBatchAsync` 被真调了几次。</summary>
            public int UpsertCalls { get; private set; }

            /// <summary>模拟写库耗时（用来制造"一次还在飞"的窗口）。</summary>
            public int UpsertDelayMs { get; set; }

            /// <summary>最近一次落库收到的那一批。</summary>
            public List<ConditionProgressRow> LastBatch
            {
                get { return new List<ConditionProgressRow>(m_lastBatch); }
            }

            /// <summary>清掉"最近一批"的记录（下一次落库重新记）。</summary>
            public void ClearLastBatch()
            {
                m_lastBatch.Clear();
            }

            /// <summary>
            /// 库里**当前**这个键的值（断"幂等""删除"这类性质要看**最终状态**）。
            /// <para>⚠️ 不要拿 <see cref="Stored"/> 的条数当判据：那记的是"写了几次"，
            /// 而幂等的定义是"重复执行后**状态不变**"。</para>
            /// </summary>
            /// <param name="key">条件编号。</param>
            /// <returns>值（没记录过是 0）。</returns>
            public int ValueOf(int key)
            {
                int value;
                return m_store.TryGetValue(key, out value) ? value : 0;
            }

            /// <summary>预置一条库里的数据。</summary>
            /// <param name="playerId">玩家编号（本假实现只认一个玩家）。</param>
            /// <param name="key">条件编号。</param>
            /// <param name="value">值。</param>
            public void Seed(long playerId, int key, int value)
            {
                m_store[key] = value;
            }

            /// <summary>读全部。</summary>
            /// <param name="playerId">玩家编号。</param>
            /// <param name="cancellationToken">取消令牌。</param>
            /// <returns>行。</returns>
            public Task<IReadOnlyList<ConditionProgressRow>> LoadByPlayerAsync(
                long playerId, CancellationToken cancellationToken = default(CancellationToken))
            {
                var rows = new List<ConditionProgressRow>();

                foreach (KeyValuePair<int, int> pair in m_store)
                {
                    rows.Add(new ConditionProgressRow
                    {
                        player_id = playerId,
                        condition_key = pair.Key,
                        progress = pair.Value
                    });
                }

                return Task.FromResult<IReadOnlyList<ConditionProgressRow>>(rows);
            }

            /// <summary>写回。</summary>
            /// <param name="playerId">玩家编号。</param>
            /// <param name="rows">脏行。</param>
            /// <param name="cancellationToken">取消令牌。</param>
            /// <returns>写了几行。</returns>
            public async Task<int> UpsertBatchAsync(
                long playerId, IReadOnlyList<ConditionProgressRow> rows,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                UpsertCalls++;
                m_lastBatch.Clear();
                m_lastBatch.AddRange(rows);

                if (UpsertDelayMs > 0)
                {
                    await Task.Delay(UpsertDelayMs, cancellationToken).ConfigureAwait(false);
                }

                for (int i = 0; i < rows.Count; i++)
                {
                    m_store[rows[i].condition_key] = rows[i].progress;
                    Stored.Add(rows[i].progress);
                }

                return rows.Count;
            }

            /// <summary>删掉全部。</summary>
            /// <param name="playerId">玩家编号。</param>
            /// <param name="cancellationToken">取消令牌。</param>
            /// <returns>删了几行。</returns>
            public Task<int> DeleteByPlayerAsync(long playerId, CancellationToken cancellationToken = default(CancellationToken))
            {
                int count = m_store.Count;
                m_store.Clear();
                return Task.FromResult(count);
            }
        }
    }
}
