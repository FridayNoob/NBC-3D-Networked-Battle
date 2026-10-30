// 这个文件里有：
//   NetSyncMode（枚举）、NetStatsSnapshot（结构）、NetRuntimeStats（静态统计）

using System;
using System.Diagnostics;

namespace NBC.Framework.Net
{
    // 当前同步模式。离线 = 没有会话；两个在线模式与 Editor 窗口的「锁步 / 快照」开关一一对应。
    public enum NetSyncMode
    {
        // 没有连接（或已断开）
        Offline = 0,

        // 状态同步：服务端按 tick 广播 WorldSnapshot
        StateSync = 1,

        // 帧同步：服务端按帧广播 LockstepFrame，客户端自己步进
        Lockstep = 2
    }

    // 一次读取拿到的全部统计（避免面板分几次读、数字来自不同时刻）
    public readonly struct NetStatsSnapshot
    {
        public readonly NetSyncMode Mode;
        public readonly long TotalBytesOut;
        public readonly long TotalBytesIn;
        public readonly float BytesPerSecondOut;
        public readonly float BytesPerSecondIn;
        public readonly int RttMs;
        public readonly int Frame;

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

    // 主要功能：累加收发字节、用 4 个采样点算 1 秒带宽、保存模式 / RTT / 帧号，供 HUD 只读。
    // 主要数据成员：4 个采样槽（收发各一组）、当前累计、累计总量、上次采样时刻。
    // 主要外部方法：AddSent / AddReceived（统计点调）、SetMode / SetRtt / SetFrame（建会话方调）、Reset、Snapshot（HUD 调）。
    // 谁在用它：CountingTransport（写字节）、Editor 窗口或运行时启动器（写模式/帧/RTT）、NetHudPanel（读）。
    // 注意：本程序集 noEngineReferences=true ⇒ 只能用 Stopwatch，不能用 Time.unscaledTime。
    public static class NetRuntimeStats
    {
        // 采样点数：4 个 × 0.25 秒 = 1 秒窗口
        private const int SlotCount = 4;

        // 每个采样点的时长（秒）
        private const double SlotSeconds = 0.25;

        // 整个窗口的时长（秒）
        private const float WindowSeconds = SlotCount * (float)SlotSeconds;

        // 收发可能在不同线程被调，统一加锁（临界区很小，不值得上无锁结构）
        private static readonly object Gate = new object();

        // 上行 / 下行的环形采样槽
        private static readonly int[] SlotsOut = new int[SlotCount];
        private static readonly int[] SlotsIn = new int[SlotCount];

        // 下一个要覆盖的槽位
        private static int m_cursor;

        // 当前这一段里累计的字节
        private static int m_currentOut;
        private static int m_currentIn;

        // 累计总量（不随窗口清空）
        private static long m_totalOut;
        private static long m_totalIn;

        // 上次采样的 Stopwatch 时刻（0 = 还没开始）
        private static long m_lastSampleTicks;

        // 会话状态
        private static NetSyncMode m_mode = NetSyncMode.Offline;
        private static int m_rttMs = -1;
        private static int m_frame = -1;

        // 功能：记一笔发送字节，并顺带推进采样窗口。
        // 入参：bytes 本次发送的业务字节数
        // 返回：无
        // 步骤：1) 推进采样窗口；2) 当前段与总量都加上
        // 上游：CountingTransport.Send
        // 下游：无
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

        // 功能：记一笔接收字节，并顺带推进采样窗口。
        // 入参：bytes 本次收到的业务字节数
        // 返回：无
        // 步骤：同 AddSent，走下行通道
        // 上游：CountingTransport.FrameReceived
        // 下游：无
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

        // 功能：记录当前同步模式。
        // 入参：mode 当前模式
        // 返回：无
        // 上游：建会话的一方（Editor 窗口 / 运行时启动器）
        // 下游：Snapshot
        public static void SetMode(NetSyncMode mode)
        {
            lock (Gate)
            {
                Advance();
                m_mode = mode;
            }
        }

        // 功能：记录最近一次往返延迟。
        // 入参：rttMs 往返延迟（毫秒，<0 表示未知）
        // 返回：无
        // 上游：建会话的一方
        // 下游：Snapshot
        public static void SetRtt(int rttMs)
        {
            lock (Gate)
            {
                Advance();
                m_rttMs = rttMs;
            }
        }

        // 功能：记录当前帧号（语义随模式不同）。
        // 入参：frame 当前帧号（状态同步 = 快照 tick；帧同步 = 已步进帧；<0 表示未知）
        // 返回：无
        // 上游：建会话的一方
        // 下游：Snapshot
        public static void SetFrame(int frame)
        {
            lock (Gate)
            {
                Advance();
                m_frame = frame;
            }
        }

        // 功能：把全部统计清零（连接前与断开时调）。
        // 入参：无
        // 返回：无
        // 步骤：清空槽位、当前段、总量与模式（连断开 / 换局都该调）
        // 上游：断开会话、换局
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

        // 功能：取一次一致的全部统计，供 HUD 显示。
        // 入参：无
        // 返回：一次一致的全部统计
        // 步骤：1) 先惰性推进窗口（保证数字是最新的）；2) 4 槽求和 ÷ 1 秒 = B/s；3) 打包返回
        // 上游：NetHudPanel（每 0.25 秒一次）
        // 下游：无
        public static NetStatsSnapshot Snapshot()
        {
            lock (Gate)
            {
                Advance();

                float outBps = Sum(SlotsOut) / WindowSeconds;
                float inBps = Sum(SlotsIn) / WindowSeconds;

                return new NetStatsSnapshot(m_mode, m_totalOut, m_totalIn, outBps, inBps, m_rttMs, m_frame);
            }
        }

        // 功能：把一个采样槽数组求和。
        // 入参：slots 采样槽
        // 返回：槽内字节之和
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
        //   1) 首次调用只记时刻，不开窗
        //   2) 算经过了多少个 0.25 秒
        //   3) 超过一整个窗口 ⇒ 直接清空（不追历史），时刻对齐到现在
        //   4) 否则逐个推进槽位：当前段入槽、当前清零、游标前进
        // 上游：AddSent / AddReceived / 各 Set / Snapshot
        // 下游：SlotsOut、SlotsIn
        // 注意：调用者必须已持有 Gate
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
