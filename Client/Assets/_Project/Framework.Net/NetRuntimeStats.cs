// 这个文件里有：
//   NetSyncMode（枚举）：当前用哪种同步方式跑，离线 / 状态同步 / 帧同步。
//   NetStatsSnapshot（结构）：一次读取拿到的全部统计，保证各数字来自同一时刻。
//   NetRuntimeStats（静态类）：累加收发字节并按 1 秒滑动窗口算带宽，保存模式 / RTT / 帧号供 HUD 只读。

using System;
using System.Diagnostics;

namespace NBC.Framework.Net
{
    // 主要功能：标记当前用哪种同步方式跑，离线 / 状态同步 / 帧同步。
    // 主要数据成员：Offline（值 0，无会话）、StateSync（值 1，服务端按 tick 广播快照）、Lockstep（值 2，按帧广播、客户端自己步进）。
    // 主要外部方法：无，纯枚举，值由 NetRuntimeStats.SetMode 写入。
    // 谁在用它：Game 的 RuntimeNetDriver 与 Editor 的 NetDebugWindow 写，NetHudPanel 读来显示模式。
    /// <summary>运行时同步方式，与 Editor 窗口的「锁步 / 快照」开关一一对应。</summary>
    public enum NetSyncMode
    {
        /// <summary>没有连接（或已断开）；值 0。</summary>
        Offline = 0,

        /// <summary>状态同步：服务端按 tick 广播 WorldSnapshot；值 1。</summary>
        StateSync = 1,

        /// <summary>帧同步：服务端按帧广播 LockstepFrame，客户端自己步进；值 2。</summary>
        Lockstep = 2
    }

    // 主要功能：把一次读取的全部统计打包成一个不可变值，避免面板分几次读、数字来自不同时刻。
    // 主要数据成员：Mode、TotalBytesOut / TotalBytesIn（累计字节）、BytesPerSecondOut / BytesPerSecondIn（字节每秒）、RttMs（毫秒）、Frame（帧号）。
    // 主要外部方法：构造函数（只赋值）与七个只读字段。
    // 谁在用它：NetRuntimeStats.Snapshot 造出来，NetHudPanel 读来填四行文本。
    /// <summary>一次读取拿到的全部统计，各字段来自同一时刻。</summary>
    public readonly struct NetStatsSnapshot
    {
        /// <summary>当前同步模式。</summary>
        public readonly NetSyncMode Mode;
        /// <summary>自上次 Reset 起累计发送的业务字节数，单位字节。</summary>
        public readonly long TotalBytesOut;
        /// <summary>自上次 Reset 起累计接收的业务字节数，单位字节。</summary>
        public readonly long TotalBytesIn;
        /// <summary>上行带宽，字节每秒，取最近 1 秒滑动窗口。</summary>
        public readonly float BytesPerSecondOut;
        /// <summary>下行带宽，字节每秒，取最近 1 秒滑动窗口。</summary>
        public readonly float BytesPerSecondIn;
        /// <summary>往返延迟，毫秒；小于 0 表示未知。</summary>
        public readonly int RttMs;
        /// <summary>当前帧号（语义随模式变，见 NetRuntimeStats.SetFrame）；小于 0 表示未知。</summary>
        public readonly int Frame;

        /// <summary>按传入值打包一次统计，不做单位换算，也不钳位。</summary>
        /// <param name="mode">当前同步模式。</param>
        /// <param name="totalBytesOut">自上次 Reset 起累计发送的业务字节数。</param>
        /// <param name="totalBytesIn">自上次 Reset 起累计接收的业务字节数。</param>
        /// <param name="bytesPerSecondOut">上行带宽，字节每秒。</param>
        /// <param name="bytesPerSecondIn">下行带宽，字节每秒。</param>
        /// <param name="rttMs">往返延迟，毫秒；小于 0 表示未知。</param>
        /// <param name="frame">当前帧号；小于 0 表示未知。</param>
        // 功能：把一次读取的结果固定成值类型，出去之后谁也不能再改。
        public NetStatsSnapshot(NetSyncMode mode, long totalBytesOut, long totalBytesIn,
            float bytesPerSecondOut, float bytesPerSecondIn, int rttMs, int frame)
        {
            Mode = mode;
            TotalBytesOut = totalBytesOut;
            TotalBytesIn = totalBytesIn;
            BytesPerSecondOut = bytesPerSecondOut;
            BytesPerSecondIn = bytesPerSecondIn;
            RttMs = rttMs;
            Frame = frame;
        }
    }

    // 主要功能：累加收发字节、用 4 个采样点算最近 1 秒的带宽、保存模式 / RTT / 帧号，供 HUD 只读。
    // 主要数据成员：SlotsOut / SlotsIn（4 槽环形采样，字节）、m_currentOut / m_currentIn（当前段，字节）、m_totalOut / m_totalIn（累计，字节）、m_lastSampleTicks（Stopwatch 时间戳）、m_mode / m_rttMs / m_frame（会话状态）。
    // 主要外部方法：AddSent / AddReceived（统计点调）、SetMode / SetRtt / SetFrame / Reset（建会话方调）、Snapshot（HUD 调）。
    // 谁在用它：CountingTransport 写字节；Game 的 RuntimeNetDriver 与 Editor 的 NetDebugWindow 写模式 / RTT / 帧号并 Reset；NetHudPanel 读 Snapshot。
    // 注意：本程序集 noEngineReferences=true ⇒ 只能用 Stopwatch，不能用 Time.unscaledTime。
    public static class NetRuntimeStats
    {
        // 采样点数：4 个槽 × 0.25 秒 = 1 秒窗口
        private const int SlotCount = 4;

        // 单个采样槽的时长，秒；0.25
        private const double SlotSeconds = 0.25;

        // 整个滑动窗口的时长，秒；由 SlotCount × SlotSeconds 算出，固定 1 秒
        private const float WindowSeconds = SlotCount * (float)SlotSeconds;

        // 收发可能在不同线程被调，所有静态字段共用这把锁（临界区很小，不值得上无锁结构）
        private static readonly object Gate = new object();

        // 上行采样槽，环形使用；每格存那 0.25 秒内发送的字节数
        private static readonly int[] SlotsOut = new int[SlotCount];
        // 下行采样槽，环形使用；每格存那 0.25 秒内接收的字节数
        private static readonly int[] SlotsIn = new int[SlotCount];

        // 下一个要覆盖的槽位索引，范围 0..SlotCount-1
        private static int m_cursor;

        // 当前这一段（还没入槽）累计的发送字节数，单位字节
        private static int m_currentOut;
        // 当前这一段（还没入槽）累计的接收字节数，单位字节
        private static int m_currentIn;

        // 自上次 Reset 起累计的发送字节数，不随窗口清空
        private static long m_totalOut;
        // 自上次 Reset 起累计的接收字节数，不随窗口清空
        private static long m_totalIn;

        // 上次推进窗口时的 Stopwatch 时间戳（Stopwatch tick，不是毫秒）；0 表示还没开始
        private static long m_lastSampleTicks;

        // 当前同步模式；默认 Offline
        private static NetSyncMode m_mode = NetSyncMode.Offline;
        // 最近一次往返延迟，毫秒；-1 表示未知
        private static int m_rttMs = -1;
        // 当前帧号（状态同步 = 快照 tick，帧同步 = 已步进帧）；-1 表示未知
        private static int m_frame = -1;

        /// <summary>记一笔发送字节，并把采样窗口推进到当前时刻。</summary>
        /// <param name="bytes">本次发送的业务字节数（不含传输层头）；小于等于 0 直接忽略。</param>
        // 功能：累计一笔发送字节，顺带把 1 秒滑动窗口推到现在。
        // 入参：bytes 本次发送的业务字节数；<= 0 视为没有业务数据
        // 返回：无
        // 步骤：
        //   1) 先在锁外挡掉非正数：没有字节可记，没必要抢临界区
        //   2) 整段在 Gate 内做：Advance 与累加必须一起完成，否则游标可能与当前段错位
        //   3) 先 Advance 再累加，保证这一笔落在当前这一格里
        // 上游：CountingTransport.Send，每发一个 payload 调一次
        // 下游：Advance
        public static void AddSent(int bytes)
        {
            if (bytes <= 0)
            {
                return;
            }

            lock (Gate)
            {
                Advance();
                m_currentOut += bytes;
                m_totalOut += bytes;
            }
        }

        /// <summary>记一笔接收字节，并把采样窗口推进到当前时刻。</summary>
        /// <param name="bytes">本次收到的业务字节数（不含传输层头）；小于等于 0 直接忽略。</param>
        // 功能：累计一笔接收字节，顺带把 1 秒滑动窗口推到现在。
        // 入参：bytes 本次收到的业务字节数；<= 0 视为没有业务数据
        // 返回：无
        // 步骤：
        //   1) 同 AddSent，先在锁外挡掉非正数
        //   2) 在 Gate 内先 Advance 再累加，走下行通道
        // 上游：CountingTransport.OnInnerFrameReceived，每收到一帧调一次
        // 下游：Advance
        public static void AddReceived(int bytes)
        {
            if (bytes <= 0)
            {
                return;
            }

            lock (Gate)
            {
                Advance();
                m_currentIn += bytes;
                m_totalIn += bytes;
            }
        }

        /// <summary>记录当前同步模式，供 HUD 显示。</summary>
        /// <param name="mode">当前模式：Offline / StateSync / Lockstep。</param>
        // 功能：把当前同步模式写进统计，Snapshot 与 HUD 都读它。
        // 入参：mode 当前模式
        // 返回：无
        // 步骤：
        //   1) 先 Advance 把窗口对齐到现在，写状态不会让窗口停在旧时刻
        //   2) 在 Gate 内改 m_mode，保证 Snapshot 取到的是一致值
        // 上游：RuntimeNetDriver（握手成功、状态变化、每帧推送）、Editor 的 NetDebugWindow
        // 下游：Snapshot
        public static void SetMode(NetSyncMode mode)
        {
            lock (Gate)
            {
                Advance();
                m_mode = mode;
            }
        }

        /// <summary>记录最近一次往返延迟。</summary>
        /// <param name="rttMs">往返延迟，毫秒；小于 0 表示未知，HUD 会显示 RTT --。</param>
        // 功能：把最近一次 RTT 记进统计，单位毫秒。
        // 入参：rttMs 往返延迟（毫秒，<0 表示未知）
        // 返回：无
        // 步骤：先 Advance 对齐窗口，再在 Gate 内写 m_rttMs
        // 上游：RuntimeNetDriver 每帧、Editor 的 NetDebugWindow
        // 下游：Snapshot
        public static void SetRtt(int rttMs)
        {
            lock (Gate)
            {
                Advance();
                m_rttMs = rttMs;
            }
        }

        /// <summary>记录当前帧号，语义随同步模式不同。</summary>
        /// <param name="frame">状态同步填快照 tick，帧同步填已步进帧；小于 0 表示未知。</param>
        // 功能：把当前帧号记进统计，两种模式下含义不同。
        // 入参：frame 当前帧号（状态同步 = 快照 tick；帧同步 = 已步进帧；<0 表示未知）
        // 返回：无
        // 步骤：先 Advance 对齐窗口，再在 Gate 内写 m_frame
        // 上游：RuntimeNetDriver 每帧、Editor 的 NetDebugWindow
        // 下游：Snapshot
        public static void SetFrame(int frame)
        {
            lock (Gate)
            {
                Advance();
                m_frame = frame;
            }
        }

        /// <summary>把统计恢复成初始状态：字节与窗口清零、模式回离线、RTT 与帧号回 -1。</summary>
        // 功能：连接前与断开时把统计抹回初始值，避免上一局的数据串到下一局。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 四个采样槽、游标与当前段清零，带宽立刻读成 0
        //   2) 累计总量清零，HUD 的累计字节从头算
        //   3) 上次采样时刻回 0，下一次 Advance 重新开窗
        //   4) 模式回 Offline、RTT 与帧号回 -1，HUD 显示离线与 --
        // 上游：RuntimeNetDriver 的建连与断开会话路径、Editor 的 NetDebugWindow
        // 下游：Snapshot
        public static void Reset()
        {
            lock (Gate)
            {
                Array.Clear(SlotsOut, 0, SlotCount);
                Array.Clear(SlotsIn, 0, SlotCount);
                m_cursor = 0;
                m_currentOut = 0;
                m_currentIn = 0;
                m_totalOut = 0;
                m_totalIn = 0;
                m_lastSampleTicks = 0;
                m_mode = NetSyncMode.Offline;
                m_rttMs = -1;
                m_frame = -1;
            }
        }

        /// <summary>取一次一致的全部统计，供 HUD 显示。</summary>
        /// <returns>模式、累计与每秒字节、RTT、帧号的一次快照；带宽是最近一秒的滑动窗口值。</returns>
        public static NetStatsSnapshot Snapshot()
        {
            // 功能：先推进采样窗口，再把四个槽求和换算成带宽。
            // 步骤：
            //   1) Advance 让槽位与真实时间对齐（面板不调也不会卡住）
            //   2) 4 槽求和 ÷ 1 秒 = B/s
            //   3) 在同一把锁里打包，保证七个字段来自同一时刻
            // 上游：NetHudPanel 每 0.25 秒一次
            // 下游：无
            lock (Gate)
            {
                Advance();

                float outBps = Sum(SlotsOut) / WindowSeconds;
                float inBps = Sum(SlotsIn) / WindowSeconds;

                return new NetStatsSnapshot(m_mode, m_totalOut, m_totalIn, outBps, inBps, m_rttMs, m_frame);
            }
        }

        // 功能：把一个采样槽数组求和，得到最近 1 秒该方向的字节总量。
        // 入参：slots 采样槽数组，每个元素单位字节
        // 返回：数组内字节之和，单位字节
        // 步骤：从 0 到 Length 顺序累加；槽定长 4 个，不做并行或提前退出
        // 上游：Snapshot
        // 下游：无
        private static int Sum(int[] slots)
        {
            int sum = 0;

            for (int i = 0; i < slots.Length; i++)
            {
                sum += slots[i];
            }

            return sum;
        }

        // 功能：按真实时间推进四个采样槽，保证带宽读数是最近一秒的。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 首次调用只记时刻，不开窗（否则第一次就会算出一个假的满窗）
        //   2) 不足一个槽（0.25 秒）直接返回，省掉取整与循环
        //   3) 算经过了多少个 0.25 秒
        //   4) 超过一整个窗口 ⇒ 直接清空、不追历史（落后太多时逐槽推进没有意义），时刻对齐到现在
        //   5) 否则逐个推进：当前段入槽、当前清零、游标前进一格
        // 上游：AddSent / AddReceived / SetMode / SetRtt / SetFrame / Snapshot
        // 下游：SlotsOut、SlotsIn
        // 注意：调用者必须已持有 Gate，本方法内部不加锁。
        private static void Advance()
        {
            long now = Stopwatch.GetTimestamp();

            if (m_lastSampleTicks == 0L)
            {
                m_lastSampleTicks = now;
                return;
            }

            double elapsed = (now - m_lastSampleTicks) / (double)Stopwatch.Frequency;

            if (elapsed < SlotSeconds)
            {
                return;
            }

            int steps = (int)(elapsed / SlotSeconds);

            if (steps >= SlotCount)
            {
                Array.Clear(SlotsOut, 0, SlotCount);
                Array.Clear(SlotsIn, 0, SlotCount);
                m_cursor = 0;
                m_currentOut = 0;
                m_currentIn = 0;
                m_lastSampleTicks = now;
                return;
            }

            for (int i = 0; i < steps; i++)
            {
                SlotsOut[m_cursor] = m_currentOut;
                SlotsIn[m_cursor] = m_currentIn;
                m_currentOut = 0;
                m_currentIn = 0;
                m_cursor = (m_cursor + 1) % SlotCount;
            }

            m_lastSampleTicks += (long)(steps * SlotSeconds * Stopwatch.Frequency);
        }
    }
}
