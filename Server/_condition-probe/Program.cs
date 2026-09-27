// ============================================================================
//  共享层纯逻辑 · .NET 8 实跑探针（条件系统 + 伤害结算）
//  项目：3D联网战斗Demo   对应：M2-A1（条件系统）、M2-B1（伤害结算）
//
//  ---------------------------------------------------------------------------
//  为什么要有这个东西（一句话）
//  ---------------------------------------------------------------------------
//  `Shared\` 是**双端共享**的：Unity 侧编 Mono/IL2CPP，服务端编 .NET 8。
//  "双端共享"这句话**不能靠声称**，得在第二种运行时上真跑一遍。
//  这个探针就是那次实跑（服务端要用的能力，先在服务端环境里验一遍）。
//
//  ⚠️ 它不是替代品：权威测试在
//     `Tests\EditMode\Game\ConditionTrackerTests.cs` 与 `DamageMathTests.cs`。
//     探针覆盖的是"纯逻辑语义"，覆盖不到配置资产 / 事件中心 / 编辑器行为。
//
//  ⚠️ 它抓到过真问题：M2-A 里我写错的"重入用例"就是它先红出来的
//     （回调里喂的是同一个条件的事件，永远不会重入 —— 用例看着在测重入，其实空过）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NBC.Framework.Net;
using NBC.Framework.Net.Adapter;
using NBC.Shared.Battle;
using NBC.Shared.Condition;
using NBC.Shared.Net;

namespace NBC.ConditionProbe
{
    /// <summary>探针入口。</summary>
    public static class Program
    {
        /// <summary>通过数。</summary>
        private static int s_passed;

        /// <summary>失败数。</summary>
        private static int s_failed;

        /// <summary>跑全部用例。</summary>
        /// <returns>全绿返回 0，否则返回 1。</returns>
        public static int Main()
        {
            Console.WriteLine("=== 共享层纯逻辑 · .NET 运行时实跑探针 ===");
            Console.WriteLine("运行时：" + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            Console.WriteLine("源码位置：Client\\Assets\\_Project\\Shared\\（与服务端是同一份文件）");
            Console.WriteLine();
            Console.WriteLine("【一】条件系统（Shared\\Condition\\）");

            AccumulatesProgress();
            ClampsOvershoot();
            MetFiresExactlyOnce();
            AfterMetDoesNotAccumulate();
            NotifyReturnsMetCount();
            AnyTargetMatchesEverything();
            SpecificTargetIgnoresOthers();
            DifferentEventTypeDoesNotMatch();
            ResetClearsProgress();
            NoResetKeepsProgress();
            NoResetWhenAlreadyMetFiresImmediately();
            SubscribeAfterRegisterMissesImmediateNotification();
            OwnershipMapMustBeReadyBeforeRegister();
            DuplicateRegisterThrows();
            ZeroRequiredCountThrows();
            ZeroCountThrows();
            UnknownEventValueThrows();
            NegativeTargetThrows();
            ProgressChangedFiresBeforeMet();
            ProgressChangedFiresForEveryAdvance();
            ReentrantNotifyWorks();
            UnregisterKeepsProgress();
            UnregisteredQueriesReturnFalse();
            MetCountReportsMet();

            Console.WriteLine();
            Console.WriteLine("【二】伤害结算（Shared\\Battle\\   —— PVP 帧同步要靠它两端算出同一个数）");

            DamageNormal();
            DamageZeroIsNotLethal();
            DamageExactLethal();
            DamageOverkillClampsToZero();
            DamageOnCorpseIsNotLethal();
            DamageNegativeHpThrows();
            DamageNegativeDamageThrows();
            DamageNeverGoesNegative();


            Console.WriteLine();
            Console.WriteLine("【三】分帧（Shared\\Net\\  —— TCP 粘包/拆包，两端同一份代码）");

            FrameRoundTrip();
            FrameStickyPackets();
            FrameByteByByte();
            FrameZeroLength();
            FrameExactLimit();
            FrameOversizeIsFatal();
            FrameLittleEndianPrefix();

            Console.WriteLine();
            Console.WriteLine("【四】真 socket（NBC.Framework.Net\\TcpTransport —— 127.0.0.1 上开真端口）");

            TcpRoundTrip();
            TcpStickyInOneWrite();
            TcpHalfFrameWaits();
            TcpPeerCloseIsDetected();
            TcpConnectRefusedIsReported();
            TcpUnreachableDoesNotHang();
            TcpOversizePrefixIsFatal();
            TcpSendBeforeConnectThrows();

            Console.WriteLine();
            Console.WriteLine("通过 " + s_passed + "，失败 " + s_failed + "。");
            Console.WriteLine(s_failed == 0 ? "结果：✅ 全绿" : "结果：❌ 有红");

            return s_failed == 0 ? 0 : 1;
        }

        // ====================================================================
        //  三、分帧（M3-S2）
        // ====================================================================

        /// <summary>造一段可辨认的载荷。</summary>
        /// <param name="length">长度。</param>
        /// <returns>载荷。</returns>
        private static byte[] Payload(int length)
        {
            byte[] result = new byte[length];

            for (int i = 0; i < length; i++)
            {
                result[i] = (byte)((i * 7 + 3) & 0xFF);
            }

            return result;
        }

        /// <summary>包一帧再拆回来。</summary>
        private static void FrameRoundTrip()
        {
            byte[] payload = Payload(32);
            byte[] frame = FrameCodec.Encode(payload);

            FrameDecoder decoder = new FrameDecoder();
            decoder.Append(frame, 0, frame.Length);

            byte[] received;
            string error;
            bool ok = decoder.TryDequeue(out received, out error) && Same(payload, received);

            Check("分帧：包一帧再拆回来一致", ok, error ?? "内容不一致");
        }

        /// <summary>粘包：两帧一次喂进来。</summary>
        private static void FrameStickyPackets()
        {
            byte[] a = Payload(16);
            byte[] b = Payload(8);

            List<byte> stream = new List<byte>();
            stream.AddRange(FrameCodec.Encode(a));
            stream.AddRange(FrameCodec.Encode(b));

            FrameDecoder decoder = new FrameDecoder();
            decoder.Append(stream.ToArray(), 0, stream.Count);

            byte[] first;
            byte[] second;
            bool ok = decoder.TryDequeue(out first) && Same(a, first)
                   && decoder.TryDequeue(out second) && Same(b, second)
                   && !decoder.TryDequeue(out first);

            Check("分帧：粘包（两帧一次喂）按顺序取出", ok, "顺序或内容不对");
        }

        /// <summary>拆包：一个字节一个字节喂。</summary>
        private static void FrameByteByByte()
        {
            byte[] payload = Payload(64);
            byte[] frame = FrameCodec.Encode(payload);

            FrameDecoder decoder = new FrameDecoder();
            byte[] received = null;
            bool ok = true;

            for (int i = 0; i < frame.Length; i++)
            {
                decoder.Append(frame, i, 1);

                if (i < frame.Length - 1 && decoder.TryDequeue(out received))
                {
                    ok = false;   // 没喂完就出帧 = 边界算错了
                    break;
                }
            }

            ok = ok && decoder.TryDequeue(out received) && Same(payload, received);

            Check("分帧：拆包（逐字节喂）不出早、内容对", ok, "提前出帧或内容不一致");
        }

        /// <summary>0 长度帧必须合法（protobuf 空消息就是 0 字节）。</summary>
        private static void FrameZeroLength()
        {
            byte[] frame = FrameCodec.Encode(null);

            FrameDecoder decoder = new FrameDecoder();
            decoder.Append(frame, 0, frame.Length);

            byte[] received;
            bool ok = decoder.TryDequeue(out received) && received != null && received.Length == 0;

            Check("分帧：0 长度帧合法（空 protobuf 消息）", ok, "0 长度帧被丢掉了");
        }

        /// <summary>正好等于上限：合法。</summary>
        private static void FrameExactLimit()
        {
            FrameDecoder decoder = new FrameDecoder(16);
            byte[] payload = Payload(16);
            byte[] frame = FrameCodec.Encode(payload);

            decoder.Append(frame, 0, frame.Length);

            byte[] received;
            Check("分帧：正好等于上限 -> 合法", decoder.TryDequeue(out received) && Same(payload, received),
                "边界多算了一个字节");
        }

        /// <summary>超过上限：判违规并给原因。</summary>
        private static void FrameOversizeIsFatal()
        {
            FrameDecoder decoder = new FrameDecoder(64);

            // 撒谎的长度前缀：0x000186A0 = 100000
            byte[] bad = { 0xA0, 0x86, 0x01, 0x00 };
            decoder.Append(bad, 0, bad.Length);

            byte[] frame;
            string error;
            bool rejected = !decoder.TryDequeue(out frame, out error);
            bool explained = error != null && error.Contains("超过上限") && decoder.FatalReason != null;

            Check("分帧：超长帧判违规并说明原因", rejected && explained, error ?? "没有给原因");
        }

        /// <summary>长度前缀是小端（不依赖宿主字节序）。</summary>
        private static void FrameLittleEndianPrefix()
        {
            byte[] frame = FrameCodec.Encode(Payload(0x0102));

            bool ok = frame[0] == 0x02 && frame[1] == 0x01 && frame[2] == 0x00 && frame[3] == 0x00
                   && FrameCodec.ReadLength(frame, 0) == 0x0102;

            Check("分帧：长度前缀是小端（手写，不用 BitConverter）", ok,
                "前缀字节 = " + frame[0] + " " + frame[1] + " " + frame[2] + " " + frame[3]);
        }

        /// <summary>两段字节是否完全相同。</summary>
        /// <param name="a">甲。</param>
        /// <param name="b">乙。</param>
        /// <returns>相同返回 true。</returns>
        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        // ====================================================================
        //  用例
        // ====================================================================

        /// <summary>喂事件会累加进度。</summary>
        private static void AccumulatesProgress()
        {
            Fixture f = new Fixture();
            f.Tracker.Register(4001, Kill(6001, 3), true);

            f.Tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            ConditionProgress progress;
            f.Tracker.TryGetProgress(4001, out progress);

            Check("累加进度", progress.Current == 1 && progress.Required == 3 && !progress.IsMet,
                "期望 1/3，实际 " + progress);
        }

        /// <summary>超额完成要钳位。</summary>
        private static void ClampsOvershoot()
        {
            Fixture f = new Fixture();
            f.Tracker.Register(4001, Kill(6001, 3), true);

            f.Tracker.Notify(EConditionEvent.KillMonster, 6001, 100);

            ConditionProgress progress;
            f.Tracker.TryGetProgress(4001, out progress);

            Check("钳位（100 只不会变成 100/3）", progress.Current == 3, "期望 3，实际 " + progress.Current);
        }

        /// <summary>达成只通知一次。</summary>
        private static void MetFiresExactlyOnce()
        {
            Fixture f = new Fixture();
            f.Tracker.Register(4001, Kill(6001, 3), true);

            f.Tracker.Notify(EConditionEvent.KillMonster, 6001, 1);
            f.Tracker.Notify(EConditionEvent.KillMonster, 6001, 1);
            int met = f.Tracker.Notify(EConditionEvent.KillMonster, 6001, 1);
            int metAgain = f.Tracker.Notify(EConditionEvent.KillMonster, 6001, 10);

            Check("达成只通知一次", met == 1 && metAgain == 0 && f.MetKeys.Count == 1,
                "第三次 Returns=" + met + "，第四次 Returns=" + metAgain + "，通知次数=" + f.MetKeys.Count);
        }

        /// <summary>达成之后进度不再涨（钳位的直接后果）。</summary>
        private static void AfterMetDoesNotAccumulate()
        {
            Fixture f = new Fixture();
            f.Tracker.Register(4001, Kill(6001, 2), true);

            f.Tracker.Notify(EConditionEvent.KillMonster, 6001, 2);
            f.Tracker.Notify(EConditionEvent.KillMonster, 6001, 5);

            ConditionProgress progress;
            f.Tracker.TryGetProgress(4001, out progress);

            Check("达成后不再累计", progress.Current == 2, "期望 2，实际 " + progress.Current);
        }

        /// <summary>一次事件让两条条件达成时返回 2。</summary>
        private static void NotifyReturnsMetCount()
        {
            Fixture f = new Fixture();
            f.Tracker.Register(1, Kill(6001, 1), true);
            f.Tracker.Register(2, Kill(0, 1), true);

            int met = f.Tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            Check("一次事件达成两条 -> 返回 2", met == 2, "实际 " + met);
        }

        /// <summary>`targetId == 0` = 任意目标。</summary>
        private static void AnyTargetMatchesEverything()
        {
            Fixture f = new Fixture();
            f.Tracker.Register(1, Kill(0, 2), true);

            f.Tracker.Notify(EConditionEvent.KillMonster, 9999, 1);
            f.Tracker.Notify(EConditionEvent.KillMonster, 1, 1);

            Check("任意目标：两个不同目标各算一次", f.MetKeys.Count == 1, "达成次数 " + f.MetKeys.Count);
        }

        /// <summary>指定目标不关心别的目标。</summary>
        private static void SpecificTargetIgnoresOthers()
        {
            Fixture f = new Fixture();
            f.Tracker.Register(4001, Kill(6001, 3), true);

            f.Tracker.Notify(EConditionEvent.KillMonster, 6002, 5);

            ConditionProgress progress;
            f.Tracker.TryGetProgress(4001, out progress);

            Check("指定目标：打别的怪不涨进度", progress.Current == 0, "实际 " + progress.Current);
        }

        /// <summary>事件类型不同不匹配。</summary>
        private static void DifferentEventTypeDoesNotMatch()
        {
            Fixture f = new Fixture();
            f.Tracker.Register(4001, Kill(6001, 3), true);

            f.Tracker.Notify(EConditionEvent.CollectItem, 6001, 5);

            Check("事件类型不同不匹配", !f.Tracker.IsMet(4001) && f.ChangedKeys.Count == 0,
                "进度变化事件发了 " + f.ChangedKeys.Count + " 次");
        }

        /// <summary>任务语义：接取时清零。</summary>
        private static void ResetClearsProgress()
        {
            Fixture f = new Fixture();
            f.Store.SetProgress(4001, 2);

            f.Tracker.Register(4001, Kill(6001, 3), true);

            ConditionProgress progress;
            f.Tracker.TryGetProgress(4001, out progress);

            Check("resetProgress=true -> 清零（任务：接了才计数）", progress.Current == 0,
                "实际 " + progress.Current);
        }

        /// <summary>成就语义：保留进度。</summary>
        private static void NoResetKeepsProgress()
        {
            Fixture f = new Fixture();
            f.Store.SetProgress(4001, 2);

            f.Tracker.Register(4001, Kill(6001, 3), false);

            ConditionProgress progress;
            f.Tracker.TryGetProgress(4001, out progress);

            Check("resetProgress=false -> 保留进度（成就：一直生效）",
                progress.Current == 2 && f.MetKeys.Count == 0,
                "进度 " + progress.Current + "，通知 " + f.MetKeys.Count + " 次");
        }

        /// <summary>不清零且已达成 -> 注册时立刻通知（成就登录即解锁）。</summary>
        private static void NoResetWhenAlreadyMetFiresImmediately()
        {
            Fixture f = new Fixture();
            f.Store.SetProgress(4001, 3);

            f.Tracker.Register(4001, Kill(6001, 3), false);

            Check("不清零且已达成 -> 注册时立刻通知", f.MetKeys.Count == 1, "通知 " + f.MetKeys.Count + " 次");
        }

        /// <summary>
        /// **"先订阅、后登记"是硬要求** —— 订阅晚了就**收不到**那次"注册即达成"的通知。
        /// <para>
        /// ⚠️ 这条为什么值得单独立一个用例（2026-09-26 写 M4-S2 成就时补）：
        /// `AchievementRuntime` 的构造函数里 `Register(..., false)` 会**重入**到自己的
        /// `OnConditionMet`。如果那边先把"条件 → 成就"的归属表填好再登记，回调就能认出
        /// 这是自己的条件；**填反了**（先登记、后填表，或者先登记后订阅），
        /// 回调查不到归属 → 安静 `return` → **成就永远不解锁，而且一行日志都没有**。
        /// 这是一个"看起来像玄学"的 bug，所以它依赖的那条语义必须有能跑的用例钉着。
        /// </para>
        /// <para>与上一条正好是一对：那条说"注册时会通知"，这条说"**晚订阅就收不到**"。</para>
        /// </summary>
        private static void SubscribeAfterRegisterMissesImmediateNotification()
        {
            InMemoryConditionProgressStore store = new InMemoryConditionProgressStore();
            store.SetProgress(4001, 3);

            ConditionTracker tracker = new ConditionTracker(store);
            int metCount = 0;

            tracker.Register(4001, Kill(6001, 3), false);       // ← 通知在这一行**当场**就发出去了
            tracker.ConditionMet += (key, progress) => metCount++;

            Check("订阅晚了 -> 收不到那次『注册即达成』的通知（⇒ 必须先订阅、再登记）",
                metCount == 0, "居然收到了 " + metCount + " 次");
        }

        /// <summary>
        /// 模拟"归属表没准备好就登记"的后果：回调认不出自己的条件 → **静默丢弃**。
        /// <para>这不是在测 `ConditionTracker`，而是把 `AchievementRuntime` 类头警告的那个
        /// **顺序坑**做成本探针里能跑的一段 —— 于是"顺序为什么重要"不靠嘴说。</para>
        /// </summary>
        private static void OwnershipMapMustBeReadyBeforeRegister()
        {
            var handled = new List<string>();
            var owners = new Dictionary<int, int>();

            InMemoryConditionProgressStore store = new InMemoryConditionProgressStore();
            store.SetProgress(4009, 2);

            ConditionTracker tracker = new ConditionTracker(store);

            tracker.ConditionMet += (key, progress) =>
            {
                int ownerId;

                if (owners.TryGetValue(key, out ownerId))
                {
                    handled.Add("owner:" + ownerId);
                }

                // ⚠️ 认不出来就**安静丢弃** —— 就是这一步让成就永远不解锁且毫无痕迹
            };

            // ❌ 错误顺序：先登记（当场回调），后填归属表
            tracker.Register(4009, Kill(6001, 2), false);
            owners[4009] = 9001;
            bool missedWhenLate = handled.Count == 0;

            // ✅ 正确顺序：先把归属表填好，再登记
            handled.Clear();
            owners.Clear();
            owners[4009] = 9001;                                // ① 先记归属
            tracker.Unregister(4009);
            tracker.Register(4009, Kill(6001, 2), false);        // ② 再登记 → 回调找得到

            Check("归属表要在 Register 之前就绪（否则当场回调会安静丢弃）",
                missedWhenLate && handled.Count == 1 && handled[0] == "owner:9001",
                "晚填表漏掉=" + missedWhenLate + "；先填表处理 " + handled.Count + " 次：" +
                string.Join(" | ", handled));
        }

        /// <summary>重复登记要报错。</summary>
        private static void DuplicateRegisterThrows()
        {
            Fixture f = new Fixture();
            f.Tracker.Register(4001, Kill(6001, 3), true);

            Check("重复登记同一个条件 -> 抛异常",
                Throws<InvalidOperationException>(() => f.Tracker.Register(4001, Kill(6001, 3), true)),
                "没有抛异常");
        }

        /// <summary>`requiredCount = 0` 要报错。</summary>
        private static void ZeroRequiredCountThrows()
        {
            Check("需要数量 0 -> 构造就抛异常",
                Throws<ArgumentException>(() => new ConditionDef(EConditionEvent.KillMonster, 1, 0)),
                "没有抛异常");
        }

        /// <summary>`count = 0` 要报错。</summary>
        private static void ZeroCountThrows()
        {
            Fixture f = new Fixture();
            f.Tracker.Register(4001, Kill(6001, 3), true);

            Check("count = 0 -> 抛异常",
                Throws<ArgumentOutOfRangeException>(
                    () => f.Tracker.Notify(EConditionEvent.KillMonster, 6001, 0)),
                "没有抛异常");
        }

        /// <summary>枚举强转出来的非法值要报错。</summary>
        private static void UnknownEventValueThrows()
        {
            Fixture f = new Fixture();
            f.Tracker.Register(4001, Kill(6001, 3), true);

            Check("枚举非法值 -> 抛异常",
                Throws<ArgumentOutOfRangeException>(
                    () => f.Tracker.Notify((EConditionEvent)99, 6001, 1)),
                "没有抛异常");
        }

        /// <summary>负目标编号要报错。</summary>
        private static void NegativeTargetThrows()
        {
            Fixture f = new Fixture();
            f.Tracker.Register(4001, Kill(6001, 3), true);

            Check("负目标编号 -> 抛异常",
                Throws<ArgumentOutOfRangeException>(
                    () => f.Tracker.Notify(EConditionEvent.KillMonster, -1, 1)),
                "没有抛异常");
        }

        /// <summary>先发"进度变了"，再发"达成了"。</summary>
        private static void ProgressChangedFiresBeforeMet()
        {
            // ⚠️ 这里刻意**不用 Fixture**：Fixture 自己也挂了记录器，
            //    两边都往同一个列表里写，"顺序"就分不清是谁写的了。
            ConditionTracker tracker = new ConditionTracker(new InMemoryConditionProgressStore());
            List<string> order = new List<string>();

            tracker.ProgressChanged += (key, progress) => order.Add("changed:" + progress);
            tracker.ConditionMet += (key, progress) => order.Add("met:" + progress);
            tracker.Register(1, Kill(6001, 1), true);

            tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            Check("顺序：先 ProgressChanged 再 ConditionMet",
                order.Count == 2 && order[0] == "changed:1/1" && order[1] == "met:1/1",
                string.Join(" | ", order));
        }

        /// <summary>每次推进都发进度事件。</summary>
        private static void ProgressChangedFiresForEveryAdvance()
        {
            Fixture f = new Fixture();
            f.Tracker.Register(4001, Kill(6001, 3), true);

            f.Tracker.Notify(EConditionEvent.KillMonster, 6001, 1);
            f.Tracker.Notify(EConditionEvent.KillMonster, 6001, 1);
            f.Tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            Check("每次推进都发 ProgressChanged（追踪条要 1/3、2/3、3/3）",
                f.ChangedKeys.Count == 3, "实际 " + f.ChangedKeys.Count + " 次");
        }

        /// <summary>回调里再喂事件（发奖励 -> 奖励里有物品 -> 又是一次 CollectItem）。</summary>
        private static void ReentrantNotifyWorks()
        {
            Fixture f = new Fixture();
            bool reentered = false;

            // 条件 1：捡到任意物品一次（由**重入的那次** Notify 完成）
            f.Tracker.Register(1, new ConditionDef(EConditionEvent.CollectItem, 0, 1), true);

            // 条件 2：杀一只 6001（由**第一次** Notify 完成，它的回调里再喂一次事件）
            f.Tracker.Register(2, Kill(6001, 1), true);

            f.Tracker.ConditionMet += (key, progress) =>
            {
                if (key == 2 && !reentered)
                {
                    reentered = true;
                    f.Tracker.Notify(EConditionEvent.CollectItem, 7002, 1);
                }
            };

            f.Tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            Check("回调里重入 Notify 能正常结算",
                reentered && f.Tracker.IsMet(1) && f.Tracker.IsMet(2),
                "重入=" + reentered + "，条件1达成=" + f.Tracker.IsMet(1) +
                "，条件2达成=" + f.Tracker.IsMet(2));
        }

        /// <summary>注销不动进度。</summary>
        private static void UnregisterKeepsProgress()
        {
            Fixture f = new Fixture();
            f.Tracker.Register(4001, Kill(6001, 3), true);
            f.Tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            bool removed = f.Tracker.Unregister(4001);

            Check("注销后不再跟踪，但进度留在存放处",
                removed && !f.Tracker.IsRegistered(4001) && f.Store.GetProgress(4001) == 1,
                "removed=" + removed + "，store=" + f.Store.GetProgress(4001));
        }

        /// <summary>没登记过的条件：查询返回 false 而不是抛异常。</summary>
        private static void UnregisteredQueriesReturnFalse()
        {
            Fixture f = new Fixture();
            ConditionProgress progress;

            Check("没登记过 -> TryGetProgress/IsMet 返回 false",
                !f.Tracker.TryGetProgress(123, out progress) && !f.Tracker.IsMet(123),
                "没有返回 false");
        }

        /// <summary>MetCount 报告已达成条数。</summary>
        private static void MetCountReportsMet()
        {
            Fixture f = new Fixture();
            f.Tracker.Register(1, Kill(6001, 1), true);
            f.Tracker.Register(2, Kill(6002, 2), true);

            f.Tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            Check("MetCount 报告已达成条数",
                f.Tracker.RegisteredCount == 2 && f.Tracker.MetCount == 1,
                "登记 " + f.Tracker.RegisteredCount + "，达成 " + f.Tracker.MetCount);
        }

        // ====================================================================
        //  二、伤害结算（共享层，PVP 帧同步要靠它两端算出同一个数）
        // ====================================================================

        /// <summary>普通一下。</summary>
        private static void DamageNormal()
        {
            DamageOutcome outcome = DamageMath.Resolve(100, 30);

            Check("伤害：扣 30 剩 70、不致死",
                outcome.Applied == 30 && outcome.RemainingHp == 70 && !outcome.IsLethal,
                outcome.ToString());
        }

        /// <summary>0 伤害不致死（`0 >= 0` 那个经典边界）。</summary>
        private static void DamageZeroIsNotLethal()
        {
            DamageOutcome outcome = DamageMath.Resolve(50, 0);

            Check("伤害：0 伤害不致死",
                outcome.Applied == 0 && outcome.RemainingHp == 50 && !outcome.IsLethal,
                outcome.ToString());
        }

        /// <summary>伤害正好等于剩余血 -> 致死。</summary>
        private static void DamageExactLethal()
        {
            DamageOutcome outcome = DamageMath.Resolve(80, 80);

            Check("伤害：正好打死 -> 致死且剩 0",
                outcome.Applied == 80 && outcome.RemainingHp == 0 && outcome.Overkill == 0 && outcome.IsLethal,
                outcome.ToString());
        }

        /// <summary>过量伤害：实扣 = 剩余血，多出来的记进 Overkill。</summary>
        private static void DamageOverkillClampsToZero()
        {
            DamageOutcome outcome = DamageMath.Resolve(10, 999);

            Check("伤害：过量 -> 实扣 10、剩 0、过量 989",
                outcome.Applied == 10 && outcome.RemainingHp == 0 && outcome.Overkill == 989 && outcome.IsLethal,
                outcome.ToString());
        }

        /// <summary>对 0 血的目标不判致死（尸体不能"再死一次"）。</summary>
        private static void DamageOnCorpseIsNotLethal()
        {
            DamageOutcome outcome = DamageMath.Resolve(0, 999);

            Check("伤害：对 0 血不判致死（否则击杀数会凭空多）",
                outcome.Applied == 0 && outcome.RemainingHp == 0 && outcome.Overkill == 0 && !outcome.IsLethal,
                outcome.ToString());
        }

        /// <summary>负 HP 要报错（说明上一处忘了钳位）。</summary>
        private static void DamageNegativeHpThrows()
        {
            Check("伤害：负 HP -> 抛异常",
                Throws<ArgumentOutOfRangeException>(() => DamageMath.Resolve(-1, 10)),
                "没有抛异常");
        }

        /// <summary>负伤害要报错，不静默当成 0。</summary>
        private static void DamageNegativeDamageThrows()
        {
            Check("伤害：负伤害 -> 抛异常",
                Throws<ArgumentOutOfRangeException>(() => DamageMath.Resolve(100, -5)),
                "没有抛异常");
        }

        /// <summary>连打 100 下：HP 永不变负，致死只发生一次（不变量）。</summary>
        private static void DamageNeverGoesNegative()
        {
            int hp = 30;
            int lethalCount = 0;
            bool everNegative = false;

            for (int i = 0; i < 100; i++)
            {
                DamageOutcome outcome = DamageMath.Resolve(hp, 7);
                hp = outcome.RemainingHp;

                if (hp < 0)
                {
                    everNegative = true;
                }

                if (outcome.IsLethal)
                {
                    lethalCount++;
                }
            }

            Check("伤害：连打 100 下 HP 永不为负、致死只发生一次",
                !everNegative && hp == 0 && lethalCount == 1,
                "hp=" + hp + "，致死次数=" + lethalCount + "，出现过负数=" + everNegative);
        }

        // ====================================================================
        //  四、真 socket（M3-S2b）—— `NBC.Framework.Net.Adapter.TcpTransport`
        //
        //  ⚠️ 这一节是**唯一**能在我（AI）手里真跑网络代码的地方：
        //     它把 `Client\Assets\_Project\Framework.Net\` 的源码直接编进来，
        //     在 127.0.0.1 上开真端口收发。Unity 侧的 NUnit 用例是权威版，
        //     这里只是"改完当天就能拿到证据"。
        // ====================================================================

        /// <summary>回环往返：发出去的字节 = [4 字节小端长度][载荷]。</summary>
        private static void TcpRoundTrip()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            try
            {
                using (TcpTransport transport = new TcpTransport())
                {
                    transport.Connect("127.0.0.1", port);
                    bool connected = PumpUntil(transport, delegate { return transport.IsConnected; }, 3000);

                    listener.Server.Poll(3000000, SelectMode.SelectRead);
                    TcpClient peer = listener.AcceptTcpClient();

                    byte[] payload = Payload(24);
                    transport.Send(payload);

                    byte[] expected = FrameCodec.Encode(payload);
                    byte[] got = ReadExact(peer.GetStream(), expected.Length, 3000);

                    Check("TCP：连上后发一条，对面收到的正是 [长度][载荷]",
                        connected && Same(expected, got),
                        "connected=" + connected + "，收到 " + got.Length + " 字节（期望 " + expected.Length + "）");
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>粘包：对面一次写三条，本端必须交三条（含 0 长度帧）。</summary>
        private static void TcpStickyInOneWrite()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            try
            {
                using (TcpTransport transport = new TcpTransport())
                {
                    List<byte[]> got = new List<byte[]>();
                    transport.FrameReceived += got.Add;

                    transport.Connect("127.0.0.1", port);
                    PumpUntil(transport, delegate { return transport.IsConnected; }, 3000);

                    listener.Server.Poll(3000000, SelectMode.SelectRead);
                    TcpClient peer = listener.AcceptTcpClient();
                    NetworkStream stream = peer.GetStream();

                    byte[] a = FrameCodec.Encode(Payload(5));
                    byte[] b = FrameCodec.Encode(new byte[0]);
                    byte[] c = FrameCodec.Encode(Payload(9));
                    stream.Write(a, 0, a.Length);
                    stream.Write(b, 0, b.Length);
                    stream.Write(c, 0, c.Length);
                    stream.Flush();

                    PumpUntil(transport, delegate { return got.Count >= 3; }, 3000);

                    Check("TCP：三条粘在一起 → 交出三条（0 长度也算一条）",
                        got.Count == 3 && Same(got[0], Payload(5)) && got[1].Length == 0 && Same(got[2], Payload(9)),
                        "收到 " + got.Count + " 条");
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>拆包：先给长度前缀，不能提前交帧；补齐后才交。</summary>
        private static void TcpHalfFrameWaits()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            try
            {
                using (TcpTransport transport = new TcpTransport())
                {
                    List<byte[]> got = new List<byte[]>();
                    transport.FrameReceived += got.Add;

                    transport.Connect("127.0.0.1", port);
                    PumpUntil(transport, delegate { return transport.IsConnected; }, 3000);

                    listener.Server.Poll(3000000, SelectMode.SelectRead);
                    TcpClient peer = listener.AcceptTcpClient();
                    NetworkStream stream = peer.GetStream();

                    byte[] payload = Payload(12);
                    byte[] frame = FrameCodec.Encode(payload);

                    stream.Write(frame, 0, NetContract.FrameLengthPrefixBytes);
                    stream.Flush();

                    DateTime until = DateTime.UtcNow.AddMilliseconds(200);

                    while (DateTime.UtcNow < until)
                    {
                        transport.Pump();
                        Thread.Sleep(1);
                    }

                    int afterPrefix = got.Count;

                    stream.Write(frame, NetContract.FrameLengthPrefixBytes,
                                 frame.Length - NetContract.FrameLengthPrefixBytes);
                    stream.Flush();

                    PumpUntil(transport, delegate { return got.Count >= 1; }, 3000);

                    Check("TCP：只到长度前缀不交帧，补齐后才是一条",
                        afterPrefix == 0 && got.Count == 1 && Same(got[0], payload),
                        "前缀阶段交了 " + afterPrefix + " 条，补齐后 " + got.Count + " 条");
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>对端关闭（FIN）：必须报出来（原因里说清是对端关的）。</summary>
        private static void TcpPeerCloseIsDetected()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            try
            {
                string reason = null;

                using (TcpTransport transport = new TcpTransport())
                {
                    transport.Closed += delegate (string r) { reason = r; };

                    transport.Connect("127.0.0.1", port);
                    PumpUntil(transport, delegate { return transport.IsConnected; }, 3000);

                    listener.Server.Poll(3000000, SelectMode.SelectRead);
                    TcpClient peer = listener.AcceptTcpClient();
                    peer.Close();

                    PumpUntil(transport, delegate { return transport.State == ETransportState.Closed; }, 3000);
                }

                Check("TCP：对端关闭被认出来（不是一直以为还连着）",
                    reason != null && reason.Contains("对端关闭"), "原因=" + (reason ?? "(没报)"));
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>连一个没人听的端口：必须报人话原因（并不卡在 Connecting）。</summary>
        private static void TcpConnectRefusedIsReported()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            string reason = null;

            using (TcpTransport transport = new TcpTransport())
            {
                transport.Closed += delegate (string r) { reason = r; };
                transport.Connect("127.0.0.1", port);
                PumpUntil(transport, delegate { return transport.State == ETransportState.Closed; }, 3000);
            }

            Check("TCP：端口没人听 → 报人话原因而不是卡住",
                reason != null && reason.Contains("连接"), "原因=" + (reason ?? "(没报)"));

            Console.WriteLine("      · 本机实测原因：" + reason);
        }

        /// <summary>不可达地址：必须在超时/报错后关闭（并**打印实际走的那条路**）。</summary>
        private static void TcpUnreachableDoesNotHang()
        {
            string reason = null;
            DateTime start = DateTime.UtcNow;

            using (TcpTransport transport = new TcpTransport(300))
            {
                transport.Closed += delegate (string r) { reason = r; };
                transport.Connect("10.255.255.1", 9);
                PumpUntil(transport, delegate { return transport.State == ETransportState.Closed; }, 5000);
            }

            long ms = (long)(DateTime.UtcNow - start).TotalMilliseconds;

            Check("TCP：连不可达地址会关闭（超时或路由报错），不永远停在连接中",
                reason != null && reason.Contains("连接"), "原因=" + (reason ?? "(没报)"));

            // 如实打印：本机走的是"超时"还是"网络不可达"，决定 Unity 那条用例覆盖了哪条分支
            Console.WriteLine("      · 本机实测原因：" + reason + "（耗时 " + ms + "ms）");
        }

        /// <summary>撒谎的长度前缀（超上限）：判协议违规断开。</summary>
        private static void TcpOversizePrefixIsFatal()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            try
            {
                string reason = null;

                using (TcpTransport transport = new TcpTransport())
                {
                    transport.Closed += delegate (string r) { reason = r; };

                    transport.Connect("127.0.0.1", port);
                    PumpUntil(transport, delegate { return transport.IsConnected; }, 3000);

                    listener.Server.Poll(3000000, SelectMode.SelectRead);
                    TcpClient peer = listener.AcceptTcpClient();
                    NetworkStream stream = peer.GetStream();

                    byte[] liar = FrameCodec.Encode(Payload(NetContract.MaxFrameBytes + 1));

                    try
                    {
                        stream.Write(liar, 0, NetContract.FrameLengthPrefixBytes);
                        stream.Flush();
                    }
                    catch (Exception)
                    {
                        // 本端可能先断开了：不影响这条用例要验的事
                    }

                    PumpUntil(transport, delegate { return transport.State == ETransportState.Closed; }, 3000);
                }

                Check("TCP：长度前缀超过上限 → 判协议违规断开（照着攒内存就完了）",
                    reason != null && reason.Contains("超过上限"), "原因=" + (reason ?? "(没报)"));
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>没连上就发：抛异常，不静默丢。</summary>
        private static void TcpSendBeforeConnectThrows()
        {
            bool threw;

            using (TcpTransport transport = new TcpTransport())
            {
                threw = Throws<InvalidOperationException>(delegate { transport.Send(Payload(4)); });
            }

            Check("TCP：没连上就 Send → 抛异常（不是静默丢）", threw, "没抛");
        }

        /// <summary>反复 `Pump` 直到条件成立（有界）。</summary>
        /// <param name="transport">传输。</param>
        /// <param name="condition">条件。</param>
        /// <param name="timeoutMs">上限毫秒。</param>
        /// <returns>条件成立返回 true。</returns>
        private static bool PumpUntil(TcpTransport transport, Func<bool> condition, int timeoutMs)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < until)
            {
                transport.Pump();

                if (condition())
                {
                    return true;
                }

                Thread.Sleep(1);
            }

            transport.Pump();
            return condition();
        }

        /// <summary>从对面的流里读够指定字节（有界）。</summary>
        /// <param name="stream">流。</param>
        /// <param name="count">字节数。</param>
        /// <param name="timeoutMs">上限毫秒。</param>
        /// <returns>读到的字节。</returns>
        private static byte[] ReadExact(NetworkStream stream, int count, int timeoutMs)
        {
            byte[] buffer = new byte[count];
            int read = 0;
            stream.ReadTimeout = timeoutMs;

            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);

                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            byte[] result = new byte[read];
            Buffer.BlockCopy(buffer, 0, result, 0, read);
            return result;
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        /// <summary>造一条"击杀某目标 N 次"的条件。</summary>
        /// <param name="targetId">目标编号。</param>
        /// <param name="required">需要数量。</param>
        /// <returns>条件定义。</returns>
        private static ConditionDef Kill(int targetId, int required)
        {
            return new ConditionDef(EConditionEvent.KillMonster, targetId, required);
        }

        /// <summary>断言一次。</summary>
        /// <param name="name">用例名。</param>
        /// <param name="ok">通过了吗。</param>
        /// <param name="detail">细节（失败时必须有用）。</param>
        private static void Check(string name, bool ok, string detail)
        {
            if (ok)
            {
                s_passed++;
                Console.WriteLine("  ✅ " + name);
                return;
            }

            s_failed++;
            Console.WriteLine("  ❌ " + name + "   —— " + detail);
        }

        /// <summary>断言这段代码会抛指定异常。</summary>
        /// <typeparam name="T">异常类型。</typeparam>
        /// <param name="action">代码。</param>
        /// <returns>抛了指定异常返回 true。</returns>
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
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>一套干净的夹具（存放处 + 条件系统 + 记录器）。</summary>
        private sealed class Fixture
        {
            /// <summary>进度存放处。</summary>
            public readonly InMemoryConditionProgressStore Store = new InMemoryConditionProgressStore();

            /// <summary>被测对象。</summary>
            public readonly ConditionTracker Tracker;

            /// <summary>记录：达成的条件编号。</summary>
            public readonly List<int> MetKeys = new List<int>();

            /// <summary>记录：进度变了的条件编号。</summary>
            public readonly List<int> ChangedKeys = new List<int>();

            /// <summary>记录：通知顺序（`changed:x/y` / `met:x/y`）。</summary>
            public readonly List<string> Order = new List<string>();

            /// <summary>造夹具并挂上记录器。</summary>
            public Fixture()
            {
                Tracker = new ConditionTracker(Store);
                Tracker.ConditionMet += (key, progress) =>
                {
                    MetKeys.Add(key);
                    Order.Add("met:" + progress);
                };
                Tracker.ProgressChanged += (key, progress) =>
                {
                    ChangedKeys.Add(key);
                    Order.Add("changed:" + progress);
                };
            }
        }
    }
}
