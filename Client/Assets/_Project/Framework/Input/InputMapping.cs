// 这个文件里有：
//   InputMapping（类）：旧输入后端的键位映射表，把逻辑动作与四个移动方向各自对应到物理按键

using System.Collections.Generic;
using UnityEngine;

namespace NBC.Framework.Input
{
    // 主要功能：保存动作 → 物理键与四个移动方向键，提供绑定、查键、遍历和重复绑定校验。
    // 主要数据成员：m_actions（动作 → 物理键）；MoveForward / MoveBackward / MoveLeft / MoveRight（默认 W / S / A / D）。
    // 主要外部方法：Bind / TryGetKey / CopyBindings / Validate / CreateDefault 与只读属性 ActionCount。
    // 谁在用它：LegacyInputSource 持有它并按它查键；Boot 的 M2DemoBehaviour 新建后绑定技能键；EditMode 的 InputManagerTests 直接构造验证。
    /// <summary>旧输入后端的键位映射表：逻辑动作与四个移动方向各自对应哪个物理按键，可改键、可查键、可校验重复绑定。</summary>
    public sealed class InputMapping
    {
        // 动作 → 物理键的字典；动作索引 0..31（InputCommand.ActionBits 是 32 位掩码），最多 32 条绑定。
        private readonly Dictionary<InputActionId, KeyCode> m_actions = new Dictionary<InputActionId, KeyCode>();

        /// <summary>前进键（KeyCode 枚举值），默认 W。</summary>
        public KeyCode MoveForward { get; set; } = KeyCode.W;

        /// <summary>后退键（KeyCode 枚举值），默认 S。</summary>
        public KeyCode MoveBackward { get; set; } = KeyCode.S;

        /// <summary>左移键（KeyCode 枚举值），默认 A。</summary>
        public KeyCode MoveLeft { get; set; } = KeyCode.A;

        /// <summary>右移键（KeyCode 枚举值），默认 D。</summary>
        public KeyCode MoveRight { get; set; } = KeyCode.D;

        /// <summary>当前已绑定的动作数量；取值 0..32，上限由动作索引 0..31 决定。</summary>
        public int ActionCount
        {
            get { return m_actions.Count; }
        }

        /// <summary>把一个动作绑到某个物理键，已绑定则覆盖。</summary>
        public void Bind(InputActionId action, KeyCode key)
        {
            // 功能：把一个动作绑到某个物理键，已有绑定就覆盖。
            // 入参：action 动作标识（索引 0..31）；key 物理按键，KeyCode.None 表示该动作不占键
            // 返回：无
            // 步骤：
            //   1) 按动作索引写入字典，同键已被别人占用也照写，冲突留给 Validate 查
            // 注意：这里不做唯一性检查，两个动作绑同一个键不会当场报错，改完键要调 Validate。
            // 上游 / 下游：上游是 Boot 的 M2DemoBehaviour 装配技能键与 EditMode 测试；下游只改字典。
            m_actions[action] = key;
        }

        /// <summary>查一个动作当前绑定的键；没绑定过返回 false。</summary>
        public bool TryGetKey(InputActionId action, out KeyCode key)
        {
            // 功能：查一个动作当前绑的是哪个键。
            // 入参：action 动作标识（索引 0..31）；key 输出绑定的键
            // 返回：绑定过返回 true；没绑定返回 false，key 为 KeyCode.None
            // 步骤：
            //   1) 直接查字典，不额外区分 KeyCode.None
            // 注意：绑成 KeyCode.None 也算已绑定并返回 true，判断没绑要看返回值而不是 key。
            // 上游 / 下游：上游是 LegacyInputSource 的按下 / 松开 / 按住查询与 EditMode 测试；下游是 Dictionary.TryGetValue。
            return m_actions.TryGetValue(action, out key);
        }

        /// <summary>把全部绑定复制到调用方列表，供调试面板或序列化遍历。</summary>
        public void CopyBindings(List<KeyValuePair<InputActionId, KeyCode>> buffer)
        {
            // 功能：把内部全部绑定复制到调用方列表。
            // 入参：buffer 接收副本的列表（会先清空）；传 null 时直接返回，不抛异常
            // 返回：无
            // 步骤：
            //   1) 先判 null，避免调用方没准备列表时炸掉
            //   2) 清空 buffer，避免上一次的结果混进来
            //   3) 逐个复制键值对，顺序按字典遍历序、不保证稳定
            // 注意：拷出的是副本，之后改字典不会同步到 buffer。
            // 上游 / 下游：预留给调试面板 / 序列化，目前无调用点；下游只操作传入的 List。
            if (buffer == null)
            {
                return;
            }

            buffer.Clear();
            foreach (KeyValuePair<InputActionId, KeyCode> pair in m_actions)
            {
                buffer.Add(pair);
            }
        }

        /// <summary>检查映射冲突：一个键不能同时绑多个动作，也不能撞四个移动方向键。</summary>
        public bool Validate(out KeyCode conflict)
        {
            // 功能：找出重复占用的键，避免一次按键触发多个行为。
            // 入参：conflict 输出第一个冲突键；无冲突时写 KeyCode.None
            // 返回：无冲突 true；有冲突 false，conflict 为那个键
            // 步骤：
            //   1) 先把 conflict 置 None，保证返回 false 时也有明确值
            //   2) 扫动作绑定并跳过 KeyCode.None，用「键 → 动作」表找重复键
            //   3) 再拿四个移动方向键回查这张表，动作键撞方向键也算冲突
            // 注意：四个移动键彼此重复（如前后同键）不会被查出；重复绑定也不会当场报错，只是这里返回 false。
            // 上游 / 下游：目前只有 EditMode 测试调用，运行时改键后要自己调一次；下游只读 m_actions 与四个方向键属性。
            conflict = KeyCode.None;

            Dictionary<KeyCode, InputActionId> seen = new Dictionary<KeyCode, InputActionId>();
            foreach (KeyValuePair<InputActionId, KeyCode> pair in m_actions)
            {
                if (pair.Value == KeyCode.None)
                {
                    continue;
                }

                InputActionId existing;
                if (seen.TryGetValue(pair.Value, out existing))
                {
                    conflict = pair.Value;
                    return false;
                }

                seen.Add(pair.Value, pair.Key);
            }

            KeyCode[] moveKeys = { MoveForward, MoveBackward, MoveLeft, MoveRight };
            for (int i = 0; i < moveKeys.Length; i++)
            {
                if (moveKeys[i] == KeyCode.None)
                {
                    continue;
                }

                if (seen.ContainsKey(moveKeys[i]))
                {
                    conflict = moveKeys[i];
                    return false;
                }
            }

            return true;
        }

        /// <summary>创建默认映射表：四个方向键为 W / S / A / D，动作绑定为空。</summary>
        public static InputMapping CreateDefault()
        {
            // 功能：创建默认键位的空映射表，只预置 WASD 四个方向键，不带任何动作绑定。
            // 入参：无
            // 返回：新实例；ActionCount 为 0，四个方向键为 W / S / A / D
            // 步骤：
            //   1) 直接构造，方向键取属性初始值，动作表保持为空（动作绑定交给游戏层）
            // 注意：动作键属游戏层概念，框架不替游戏决定技能键；有默认值不等于硬编码，关键是能被 Bind 覆盖。
            // 上游 / 下游：上游是 LegacyInputSource 的缺省构造、Boot 演示与 EditMode 测试；下游无。
            return new InputMapping();
        }
    }
}
