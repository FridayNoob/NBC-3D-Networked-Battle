// 这个文件里有：
//   PlayerStateCatalog（静态类）：状态值到「层 / 上层标识 / 入口」的换算，以及动作编号常量 PlayerActionId

namespace NBC.Game.Player
{
    // 主要功能：把 EPlayerState 的分段数值翻译成层归属、上层标识与入口状态。
    // 主要数据成员：无字段，全是静态换算（数值区间就是唯一真源）。
    // 主要外部方法：LayerOf / UpperOf / EntryOf / IsUpper / BlocksMovement。
    // 谁在用它：PlayerStateMachine 注册状态机与判断「现在能不能移动」时。
    public static class PlayerStateCatalog
    {
        // 功能：算一个状态属于哪一层。
        // 入参：state 是任意状态值（含三个上层标识本身）。
        // 返回：Locomotion / Action / Reaction。
        // 步骤：直接按数值区间判 —— 小于 100 是移动层，小于 200 是动作层，其余是反应层。
        // 上游/下游：PlayerStateMachine.构造函数、ApplyDesired 调用；不调用别人。
        public static EPlayerLayer LayerOf(EPlayerState state)
        {
            int value = (int)state;
            if (value < 100)
            {
                return EPlayerLayer.Locomotion;
            }

            if (value < 200)
            {
                return EPlayerLayer.Action;
            }

            return EPlayerLayer.Reaction;
        }

        // 功能：把层换算成状态机上层要用的标识值。
        // 入参：layer 是三层之一。
        // 返回：Locomotion / Action / Reaction 三个上层标识。
        // 步骤：一一对应，不做别的判断（层枚举的顺序就是标识值的顺序）。
        // 上游/下游：PlayerStateMachine 切层时调用。
        public static EPlayerState UpperOf(EPlayerLayer layer)
        {
            switch (layer)
            {
                case EPlayerLayer.Action:
                    return EPlayerState.Action;
                case EPlayerLayer.Reaction:
                    return EPlayerState.Reaction;
                default:
                    return EPlayerState.Locomotion;
            }
        }

        // 功能：给出某一层的入口状态（第一次进这层时停在哪个下层状态）。
        // 入参：layer 是三层之一。
        // 返回：移动层给 Idle，动作层给 Attack1，反应层给 HitLeft。
        // 步骤：只做固定映射；入口状态必须与 PlayerStateMachine 里第一个注册的下层一致，否则 HierarchicalStateMachine.Start 会抛异常。
        // 上游/下游：PlayerStateMachine.构造函数调用。
        public static EPlayerState EntryOf(EPlayerLayer layer)
        {
            switch (layer)
            {
                case EPlayerLayer.Action:
                    return EPlayerState.Attack1;
                case EPlayerLayer.Reaction:
                    return EPlayerState.HitLeft;
                default:
                    return EPlayerState.Idle;
            }
        }

        // 功能：判断一个值是不是「上层标识」而不是真正的下层状态。
        // 入参：state 是状态值。
        // 返回：true 表示它是 Locomotion / Action / Reaction 之一。
        // 步骤：与三个已知标识直接比较。
        // 上游/下游：PlayerStateMachine 的参数发布与日志过滤使用。
        public static bool IsUpper(EPlayerState state)
        {
            return state == EPlayerState.Locomotion
                || state == EPlayerState.Action
                || state == EPlayerState.Reaction;
        }

        // 功能：判断这个状态下角色还能不能自己走动。
        // 入参：state 是当前下层状态。
        // 返回：true 表示可以走（只有移动层可以），false 表示动作/反应期间必须站住。
        // 步骤：先求层，再与该层比较 —— 不要在别处复制这套判断。
        // 上游/下游：PlayerStateMachine.Tick 写 IPlayerMotion.MovementEnabled 时调用。
        public static bool BlocksMovement(EPlayerState state)
        {
            return LayerOf(state) != EPlayerLayer.Locomotion;
        }
    }

    // 主要功能：动画参数 ActionId 的取值表 —— 控制器里的 Float 参数按这些数字选动作。
    // 主要数据成员：常量（0 无动作、1~3 攻击、4~6 技能、7 冲刺、8 格挡、9 姿势、10 受击、11 硬直）。
    // 主要外部方法：OfAttack / OfSkill 把「第几段」换成 ActionId。
    // 谁在用它：PlayerStateMachine 设置 context.ActionId；动画控制器按同值判断。
    public static class PlayerActionId
    {
        // 没有动作在播
        public const float None = 0f;

        // 攻击第 1 段
        public const float Attack1 = 1f;

        // 攻击第 2 段
        public const float Attack2 = 2f;

        // 攻击第 3 段
        public const float Attack3 = 3f;

        // 技能 1
        public const float Skill1 = 4f;

        // 技能 2
        public const float Skill2 = 5f;

        // 技能 3
        public const float Skill3 = 6f;

        // 冲刺
        public const float Dash = 7f;

        // 格挡
        public const float Block = 8f;

        // 姿势 / 表情
        public const float Pose = 9f;

        // 受击
        public const float Hit = 10f;

        // 硬直
        public const float Stun = 11f;

        // 功能：把攻击段号换成 ActionId。
        // 入参：comboIndex 取 1~3，超出范围按 1 处理（调用方自己保证正确）。
        // 返回：1~3 的浮点值。
        // 步骤：直接整数转换；不查表，避免多一份数据要维护。
        // 上游/下游：PlayerStateMachine.NotifyAttack 使用（R035b 连击）。
        public static float OfAttack(int comboIndex)
        {
            if (comboIndex <= 1)
            {
                return Attack1;
            }

            return comboIndex >= 3 ? Attack3 : Attack2;
        }

        // 功能：把技能序号换成 ActionId。
        // 入参：skillIndex 取 1~3，超出范围按 1 处理。
        // 返回：4~6 的浮点值。
        // 步骤：先归一到 1~3，再加 3。
        // 上游/下游：PlayerStateMachine.NotifySkill 使用（R035b）。
        public static float OfSkill(int skillIndex)
        {
            int index = skillIndex < 1 ? 1 : (skillIndex > 3 ? 3 : skillIndex);
            return Skill1 + (index - 1);
        }
    }
}
