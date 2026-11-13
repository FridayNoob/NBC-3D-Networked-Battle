// 这个文件里有：
//   PlayerSpeedSmoother（纯逻辑）：把速度从当前值线性平滑到目标值，供动画混合树使用

using System;

namespace NBC.Game.Player
{
    // 主要功能：把「档位速度」变成「连续速度」——站到跑不是 0 直接跳 5，而是在固定时长内滑过去。
    // 主要数据成员：m_durationSeconds（从旧值滑到新值用多少秒）、m_from/m_to（本次滑动的起点与终点）、m_elapsed（已经滑了多久）。
    // 主要外部方法：Step 推进一帧并返回当前速度；Current 读当前值；IsSettled 判断是否已经到位。
    // 谁在用它：PlayerAnimatorPublisher.Apply 每帧喂 Speed 参数；EditMode 单测用固定步长验证曲线。
    // 注意：目标值中途又变了就从「当前值」重新起滑，不会跳回起点；这样连续按键不会出现速度突变。
    public sealed class PlayerSpeedSmoother
    {
        // 一次滑动的最短时长（秒）；小于等于 0 时按 0.0001 秒处理，避免除零
        private const float MinDurationSeconds = 0.0001f;

        // 目标变化小于这个值时认为没变，不重新起滑（避免浮点抖动反复重置）
        private const float TargetEpsilon = 0.0001f;

        // 一次滑动用多少秒（秒）
        private readonly float m_durationSeconds;

        // 本次滑动的起点速度（米每秒）
        private float m_from;

        // 本次滑动的终点速度（米每秒）
        private float m_to;

        // 本次滑动已经过去的时间（秒）
        private float m_elapsed;

        // 是否已经收到过第一个目标值
        private bool m_started;

        // 功能：记住滑动时长。
        // 入参：durationSeconds 是滑动时长，单位秒；小于等于 0 会被夹到 0.0001。
        // 返回：无。
        // 步骤：只把入参夹紧后存下。
        // 上游/下游：PlayerAnimatorPublisher.EnsureReady 创建。
        public PlayerSpeedSmoother(float durationSeconds)
        {
            m_durationSeconds = durationSeconds > MinDurationSeconds ? durationSeconds : MinDurationSeconds;
        }

        // 当前速度，单位米每秒
        public float Current { get; private set; }

        // 是否已经滑到目标值（第一次 Step 之前为 false）
        public bool IsSettled
        {
            get { return m_started && m_elapsed >= m_durationSeconds; }
        }

        // 功能：推进一帧，返回平滑后的速度。
        // 入参：target 是目标速度（米每秒）；deltaSeconds 是这一帧的时长（秒，小于 0 按 0 处理）。
        // 返回：当前速度（米每秒）。
        // 步骤：1) 第一次调用直接对齐目标，不滑 2) 目标变了就从当前值重新起滑 3) 按已滑时间做线性插值 4) 到时后精确等于目标。
        // 上游/下游：PlayerAnimatorPublisher.Apply 调用。
        public float Step(float target, float deltaSeconds)
        {
            if (!m_started)
            {
                m_started = true;
                m_from = target;
                m_to = target;
                m_elapsed = m_durationSeconds;
                Current = target;
                return Current;
            }

            if (Math.Abs(target - m_to) > TargetEpsilon)
            {
                m_from = Current;
                m_to = target;
                m_elapsed = 0f;
            }

            if (m_elapsed < m_durationSeconds)
            {
                m_elapsed += deltaSeconds > 0f ? deltaSeconds : 0f;
                if (m_elapsed > m_durationSeconds)
                {
                    m_elapsed = m_durationSeconds;
                }

                float t = m_elapsed / m_durationSeconds;
                Current = m_from + (m_to - m_from) * t;
            }
            else
            {
                Current = m_to;
            }

            return Current;
        }
    }
}
