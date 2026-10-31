// 这个文件里有：
//   EReconnectAction（枚举）：策略给出的下一步动作。
//   EReconnectPhase（枚举）：当前处于重连的哪个阶段。
//   NetReconnector（类）：断线重连的策略 —— 退避、次数上限、什么时候放弃。

using System;

namespace NBC.Game.Net
{
    // 主要功能：告诉调用方「现在该做什么」。
    // 主要数据成员：None / Wait / Connect / JoinRoom / GiveUp。
    // 主要外部方法：无（调用方 switch 它）。
    // 谁在用它：RuntimeNetDriver 每帧取一次并执行。
    public enum EReconnectAction
    {
        // 什么都不用做
        None = 0,

        // 还在等退避时间
        Wait = 1,

        // 该重新发起连接了
        Connect = 2,

        // 连上了但还没进房，该发进房请求
        JoinRoom = 3,

        // 放弃重连（次数用完 / 房间已不存在）
        GiveUp = 4,

        // 先把当前连接断掉（连接卡死超时时用；调用方调 Disconnect，下一帧才允许再 Connect）
        Reset = 5,
    }

    // 主要功能：描述重连进行到哪一步，给日志与 HUD 用。
    // 主要数据成员：Idle / Waiting / Connecting / Joining / Recovered / GaveUp。
    // 主要外部方法：无。
    // 谁在用它：RuntimeNetDriver（打日志、写统计）、NetHudPanel（显示）。
    public enum EReconnectPhase
    {
        // 没在重连（正常在线或从未连过）
        Idle = 0,

        // 等退避到点
        Waiting = 1,

        // 已发起 Connect，等握手
        Connecting = 2,

        // 已在线，等进房结果
        Joining = 3,

        // 重连成功、已回到房间
        Recovered = 4,

        // 已放弃
        GaveUp = 5,
    }

    // 主要功能：断线后按退避表驱动「重连 → 回原房」，用完次数或房间没了就放弃。
    // 主要数据成员：m_phase / m_attempt / m_waitSeconds / m_lastRoomId / m_intentional / m_hasJoinedOnce。
    // 主要外部方法：Tick（核心决策）、NoteJoined、NoteIntentionalDisconnect、Reset。
    // 谁在用它：RuntimeNetDriver 每帧调 Tick 并执行返回的动作。
    // 注意：本类不依赖 UnityEngine —— 时间以秒为单位由调用方喂进来，所以 _net-probe 能用假时钟直接测退避表。
    // 注意：只有「曾经进过房」之后断线才会重连；首次连接失败不算断线（那属于连接问题，不是本需求的场景）。
    public sealed class NetReconnector
    {
        // 退避表（秒）：第 1..MaxAttempts 次重试各等多久；超出表长就沿用最后一档
        private static readonly float[] BackoffSeconds = { 0.5f, 1f, 2f, 4f, 4f };

        // 单次连接尝试最多等多久（秒）：超时说明这条连接卡住了，必须先 Disconnect 才能再 Connect
        private const float AttemptTimeoutSeconds = 3f;

        // 最多重试几次（0 表示不限，但这里固定用上限，避免无限打服务端）
        private readonly int m_maxAttempts;

        // 副本编号：回房时带上（房号为空时才用得上）
        private readonly int m_dungeonId;

        // 当前阶段
        private EReconnectPhase m_phase = EReconnectPhase.Idle;

        // 已重试次数（1 表示正在做第 1 次重试）
        private int m_attempt;

        // 距离下次重试还剩多少秒
        private float m_waitSeconds;

        // 断线前所在房号（空 = 没记下，回房时让服务端安排）
        private string m_lastRoomId = string.Empty;

        // 是否曾经成功进过房（没进过房就不做重连）
        private bool m_hasJoinedOnce;

        // 是否主动断开（主动断开不许重连）
        private bool m_intentional;

        // 本次连接尝试已经等了多少秒（超过 AttemptTimeoutSeconds 就重置连接）
        private float m_attemptElapsed;

        // 放弃原因（给调用方写日志 / HUD）
        private string m_lastReason = string.Empty;

        // 功能：建一个重连策略，配置次数上限与副本编号。
        // 入参：maxAttempts 最多重试次数（<= 0 时按 1 处理，不能无限）；dungeonId 副本编号（回房时带上）
        // 返回：无（构造）
        // 步骤：直接赋值，阶段停在 Idle
        // 上游：RuntimeNetDriver.Start
        // 下游：Tick 用它算退避
        public NetReconnector(int maxAttempts = 5, int dungeonId = 1001)
        {
            m_maxAttempts = maxAttempts > 0 ? maxAttempts : 1;
            m_dungeonId = dungeonId;
        }

        // 功能：当前阶段。
        // 返回：EReconnectPhase
        public EReconnectPhase Phase
        {
            get { return m_phase; }
        }

        // 功能：已重试次数（0 = 没在重连）。
        // 返回：次数
        public int Attempt
        {
            get { return m_attempt; }
        }

        // 功能：放弃原因（只在 GaveUp 时有意义）。
        // 返回：一句人话；没放弃过就是空串
        public string LastReason
        {
            get { return m_lastReason; }
        }

        // 功能：断线前所在的房号，调用方回房时用它。
        // 返回：房号；没记下就是空串
        public string LastRoomId
        {
            get { return m_lastRoomId; }
        }

        // 功能：副本编号。
        // 返回：构造时给的副本编号
        public int DungeonId
        {
            get { return m_dungeonId; }
        }

        // 功能：记下当前所在房号（每次成功进房都调），供断线后回房用。
        // 入参：roomId 房号（空串表示没进房，不覆盖旧值）
        // 返回：无
        // 步骤：
        //   1) 房号为空就直接返回（别把有用信息冲掉）
        //   2) 记下房号、标记「进过房」、把阶段回到 Idle（重连成功后要停止重试）
        // 上游：RuntimeNetDriver 检测到 InRoom 时
        // 下游：Tick 里的回房动作
        public void NoteJoined(string roomId)
        {
            if (string.IsNullOrEmpty(roomId))
            {
                return;
            }

            m_lastRoomId = roomId;
            m_hasJoinedOnce = true;
            m_attempt = 0;
            m_waitSeconds = 0f;
            m_phase = EReconnectPhase.Idle;
            m_lastReason = string.Empty;
        }

        // 功能：标记「玩家主动断开」，之后不再自动重连。
        // 入参：无
        // 返回：无
        // 步骤：置标记、清阶段
        // 上游：RuntimeNetDriver 在主动 Disconnect / 停止播放前
        // 下游：Tick 直接返回 None
        public void NoteIntentionalDisconnect()
        {
            m_intentional = true;
            m_phase = EReconnectPhase.Idle;
            m_attempt = 0;
            m_waitSeconds = 0f;
        }

        // 功能：清掉所有状态（主动断开后重新开始一局时用）。
        // 入参：无
        // 返回：无
        // 步骤：全部归零（房号保留 —— 同一个房再进很常见；要换房请新建对象）
        // 上游：RuntimeNetDriver.Start
        // 下游：Tick
        public void Reset()
        {
            m_intentional = false;
            m_hasJoinedOnce = false;
            m_attempt = 0;
            m_waitSeconds = 0f;
            m_phase = EReconnectPhase.Idle;
            m_lastReason = string.Empty;
        }

        // 功能：标记「放弃」并记下原因（房间不存在、已满、或锁步局已开局时由调用方设置）。
        // 入参：reason 一句人话
        // 返回：无
        // 步骤：置阶段与原因，次数清零
        // 上游：RuntimeNetDriver（收到 ErrorResponse 时）
        // 下游：Tick 之后返回 GiveUp
        public void NoteGiveUp(string reason)
        {
            m_phase = EReconnectPhase.GaveUp;
            m_lastReason = string.IsNullOrEmpty(reason) ? "未知原因" : reason;
            m_attempt = 0;
            m_waitSeconds = 0f;
        }

        // 功能：每帧问一次「下一步做什么」。
        // 入参：isOnline 会话是否在线；inRoom 是否已在房间；deltaSeconds 本帧真实时间（秒）；reason 输出放弃原因
        // 返回：EReconnectAction 下一步动作
        // 步骤：
        //   1) 已放弃 / 主动断开 / 从没进过房 ⇒ None（不介入）
        //   2) 已在房间 ⇒ 回到 Idle 并返回 None（正常在线）
        //   3) 还没在线：若是第 1 次进入重连，先记一次尝试并开始等退避
        //   4) 退避没到点 ⇒ Wait；到点了 ⇒ Connect
        //   5) 已在线但还没进房 ⇒ JoinRoom
        // 上游：RuntimeNetDriver.Update
        // 下游：调用方执行返回的动作
        // 注意：进房被拒（房间不存在/满）时不要在这里猜，由调用方调 NoteGiveUp —— 本类看不到服务端回执
        public EReconnectAction Tick(ESessionState state, bool inRoom, float deltaSeconds, out string reason)
        {
            reason = m_lastReason;

            if (m_phase == EReconnectPhase.GaveUp || m_intentional || !m_hasJoinedOnce)
            {
                return EReconnectAction.None;
            }

            float dt = deltaSeconds > 0f ? deltaSeconds : 0f;

            // 已连上：在房里就正常，不在房里就回房
            if (state == ESessionState.Online)
            {
                m_attemptElapsed = 0f;

                if (inRoom)
                {
                    m_phase = EReconnectPhase.Idle;
                    m_attempt = 0;
                    m_waitSeconds = 0f;
                    return EReconnectAction.None;
                }

                if (m_attempt == 0)
                {
                    m_attempt = 1;
                }

                m_phase = EReconnectPhase.Joining;
                return EReconnectAction.JoinRoom;
            }

            // 连接进行中（Connecting / Handshaking）：不要重复 Connect —— 那会抛「已经在连」
            if (state == ESessionState.Connecting || state == ESessionState.Handshaking)
            {
                m_attemptElapsed += dt;

                if (m_attemptElapsed < AttemptTimeoutSeconds)
                {
                    return EReconnectAction.Wait;
                }

                // 卡太久：先让调用方把连接断掉，下一帧状态回到 Disconnected 才允许重连。
                // ⚠️ 阶段必须留在 Connecting（不能置 Waiting）：Connecting 表示「这次尝试还没结算」，
                //    下一帧才会走「尝试失败 ⇒ 计一次 + 重新退避」那一支；置 Waiting 会被当成「正在等退避」而立刻重连。
                m_attemptElapsed = 0f;
                return EReconnectAction.Reset;
            }

            // 到这里状态是 Disconnected / Failed：上一次尝试结束了（成功会走上面的 Online 分支）
            if (m_phase == EReconnectPhase.Connecting)
            {
                if (m_attempt >= m_maxAttempts)
                {
                    NoteGiveUp("重连 " + m_attempt + " 次都没成功");
                    reason = m_lastReason;
                    return EReconnectAction.GiveUp;
                }

                m_attempt++;
                m_waitSeconds = DelayFor(m_attempt);
                m_phase = EReconnectPhase.Waiting;
                m_attemptElapsed = 0f;
                return EReconnectAction.Wait;
            }

            // 还没开始重连：记第一次尝试并开始退避
            if (m_phase == EReconnectPhase.Idle || m_phase == EReconnectPhase.Recovered)
            {
                m_attempt = 1;
                m_waitSeconds = DelayFor(m_attempt);
                m_phase = EReconnectPhase.Waiting;
                return EReconnectAction.Wait;
            }

            // 正在等退避
            m_waitSeconds -= dt;

            if (m_waitSeconds > 0f)
            {
                return EReconnectAction.Wait;
            }

            m_phase = EReconnectPhase.Connecting;
            m_attemptElapsed = 0f;
            return EReconnectAction.Connect;
        }
        // 功能：取第 attempt 次重试要等多久。
        // 入参：attempt 第几次（从 1 起）
        // 返回：秒数（超出表长沿用最后一档）
        // 步骤：下标钳到表范围内再取值
        // 上游：Tick
        // 下游：无
        private static float DelayFor(int attempt)
        {
            int index = attempt - 1;

            if (index < 0)
            {
                index = 0;
            }

            if (index >= BackoffSeconds.Length)
            {
                index = BackoffSeconds.Length - 1;
            }

            return BackoffSeconds[index];
        }
    }
}
