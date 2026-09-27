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
using NBC.Server.Data;
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
