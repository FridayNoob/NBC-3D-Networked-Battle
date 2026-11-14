// 这个文件里有：
//   PlayerVerticalMotion（纯逻辑类）：跳跃的竖直速度、离地高度与落地判定，不依赖 UnityEngine

using System;

namespace NBC.Game.Player
{
    // 主要功能：按重力积分算出「离地多高、竖直速度多少、什么时候落地」，供状态机与电机共用。
    // 主要数据成员：m_gravity（重力，米/秒²）、Height（离地高度，米）、VerticalSpeed（竖直速度，米/秒）、Grounded、JustLanded。
    // 主要外部方法：Jump 起跳、Step 推进一帧、ResetToGround 强制落地。
    // 谁在用它：PlayerJumpMotor 每帧推进；EditMode 测试直接 new 出来断言高度与滞空时间。
    // 注意：纯 C#，不引用 UnityEngine —— 数学用 System.Math，保证 EditMode 无场景也能跑。
    public sealed class PlayerVerticalMotion
    {
        // 默认重力，米/秒²（地球重力；跳 0.9 米约 0.86 秒滞空）
        public const float DefaultGravity = 9.8f;

        // 认为「已经贴地」的容差，米（浮点积分会留下极小的正高度，不清零会一直离地）
        private const float LandEpsilon = 0.0005f;

        // 重力加速度，米/秒²
        private readonly float m_gravity;

        // 竖直速度，米/秒（上升为正、下落为负）
        public float VerticalSpeed { get; private set; }

        // 离地高度，米（0 表示站在地面上）
        public float Height { get; private set; }

        // 是否踩在地面上
        public bool Grounded { get; private set; }

        // 本次 Step 是否刚落地（只有落地那一帧为 true）
        public bool JustLanded { get; private set; }

        // 功能：建一个竖直运动器。
        // 入参：gravityMetersPerSecondSquared 重力加速度，米/秒²，必须大于 0。
        // 返回：实例，初始状态是站在地面上。
        // 步骤：校验重力后保存；高度与速度都是 0。
        // 上游/下游：PlayerJumpMotor 构造；测试直接 new。
        public PlayerVerticalMotion(float gravityMetersPerSecondSquared = DefaultGravity)
        {
            if (gravityMetersPerSecondSquared <= 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(gravityMetersPerSecondSquared),
                    "[PlayerVerticalMotion] 重力必须大于 0。");
            }

            m_gravity = gravityMetersPerSecondSquared;
            Grounded = true;
        }

        // 功能：起跳，按目标高度反算初速度。
        // 入参：heightMeters 目标跳高，米，必须大于 0。
        // 返回：true 表示起跳成功；false 表示已经离地或高度非法（调用方不用报错）。
        // 步骤：1) 只允许从地面起跳 2) 用 v = 根号(2 g h) 反算初速度 3) 清零高度、置空中。
        // 上游/下游：PlayerJumpMotor.TryJump 调用。
        public bool Jump(float heightMeters)
        {
            if (!Grounded || heightMeters <= 0f)
            {
                return false;
            }

            VerticalSpeed = (float)Math.Sqrt(2d * m_gravity * heightMeters);
            Height = 0f;
            Grounded = false;
            JustLanded = false;
            return true;
        }

        // 功能：推进一帧的竖直运动。
        // 入参：deltaSeconds 帧时长，秒；小于等于 0 时只清标记、不做积分。
        // 返回：无。
        // 步骤：1) 先清刚落地标记 2) 站着就保持 0 3) 空中先减速度、再按新速度加高度 4) 高度归零那帧判落地。
        // 上游/下游：PlayerJumpMotor.Step 调用；下游 PlayerJumpMotor 把 Height 写到 transform。
        public void Step(float deltaSeconds)
        {
            JustLanded = false;
            if (deltaSeconds <= 0f)
            {
                return;
            }

            if (Grounded)
            {
                VerticalSpeed = 0f;
                Height = 0f;
                return;
            }

            VerticalSpeed -= m_gravity * deltaSeconds;
            Height += VerticalSpeed * deltaSeconds;

            if (Height <= LandEpsilon && VerticalSpeed <= 0f)
            {
                Height = 0f;
                VerticalSpeed = 0f;
                Grounded = true;
                JustLanded = true;
            }
        }

        // 功能：强制回到地面（传送、重生、脚下没地面时兜底）。
        // 入参：无。
        // 返回：无。
        // 步骤：高度与竖直速度清零、置落地、清刚落地标记。
        // 上游/下游：PlayerJumpMotor 在地面探测失败时调用。
        public void ResetToGround()
        {
            Height = 0f;
            VerticalSpeed = 0f;
            Grounded = true;
            JustLanded = false;
        }
    }
}
