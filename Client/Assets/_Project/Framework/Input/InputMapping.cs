// ============================================================================
//  NBC.Framework.Input · 键位映射表（"可配置"的落点）
//  对应需求：FW-M07 的"**键位映射可配置**"
//
//  ---------------------------------------------------------------------------
//  为什么映射表属于"实现类"而不是接口
//  ---------------------------------------------------------------------------
//  "哪个物理键对应哪个动作"是**后端相关**的信息：
//    · 旧输入系统：`KeyCode.W`
//    · 新输入系统：`InputAction` 资产里的绑定（还可能是手柄轴）
//  所以 `IInputSource` 的接口里不出现 `KeyCode`，把绑定留在实现侧。
//
//  ---------------------------------------------------------------------------
//  两种配置方式
//  ---------------------------------------------------------------------------
//    ① **代码**：`InputMapping.CreateDefault()` 然后 `Bind(...)` / `Rebind(...)`
//    ② **Editor**：`InputMappingAsset`（ScriptableObject）—— 在 Inspector 里改键位，
//       这才是"可配置"该有的样子，也顺带演示了需求 8 的 SO 用法
//
//  ⚠️ 重复绑定会被查出来：两个动作绑同一个键时 `Validate()` 会报出来。
//     否则表现是"按 W 同时前进和放技能"，很难查。
// ============================================================================

using System.Collections.Generic;
using UnityEngine;

namespace NBC.Framework.Input
{
    /// <summary>
    /// 键位映射表：逻辑动作 / 移动方向 → 物理按键。**旧输入后端专用**。
    /// </summary>
    public sealed class InputMapping
    {
        /// <summary>动作 → 按键。</summary>
        private readonly Dictionary<InputActionId, KeyCode> m_actions = new Dictionary<InputActionId, KeyCode>();

        /// <summary>前进键。</summary>
        public KeyCode MoveForward { get; set; } = KeyCode.W;

        /// <summary>后退键。</summary>
        public KeyCode MoveBackward { get; set; } = KeyCode.S;

        /// <summary>左移键。</summary>
        public KeyCode MoveLeft { get; set; } = KeyCode.A;

        /// <summary>右移键。</summary>
        public KeyCode MoveRight { get; set; } = KeyCode.D;

        /// <summary>当前已绑定的动作数量。</summary>
        public int ActionCount
        {
            get { return m_actions.Count; }
        }

        /// <summary>绑定一个动作到某个键（已存在则覆盖）。</summary>
        /// <param name="action">动作标识。</param>
        /// <param name="key">物理按键。</param>
        public void Bind(InputActionId action, KeyCode key)
        {
            m_actions[action] = key;
        }

        /// <summary>取某个动作绑定的键。</summary>
        /// <param name="action">动作标识。</param>
        /// <param name="key">绑定的键；返回 false 时是 <see cref="KeyCode.None"/>。</param>
        /// <returns>是否绑定过。</returns>
        public bool TryGetKey(InputActionId action, out KeyCode key)
        {
            return m_actions.TryGetValue(action, out key);
        }

        /// <summary>遍历所有已绑定的动作（调试面板 / SO 序列化用）。</summary>
        /// <param name="buffer">接收结果的列表（会先清空）。</param>
        public void CopyBindings(List<KeyValuePair<InputActionId, KeyCode>> buffer)
        {
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

        /// <summary>
        /// 检查有没有"一个键绑了多个动作"。
        /// <para>
        /// 不检查的话，表现是"按 W 同时触发前进和技能1"，而且**不报错** ——
        /// 正是本项目最讨厌的那类静默错误。
        /// </para>
        /// </summary>
        /// <param name="conflict">第一个冲突的键；没有冲突时是 <see cref="KeyCode.None"/>。</param>
        /// <returns>有冲突返回 false。</returns>
        public bool Validate(out KeyCode conflict)
        {
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

            // 移动键也不能和动作键撞
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

        /// <summary>
        /// 默认映射表：**只给四个方向键的默认值**（WASD）。
        /// <para>
        /// ⚠️ **动作绑定不在这里** —— "技能1 是哪个键"是**游戏层**的概念，
        /// 由 `NBC.Game` 声明 `InputActionId` 并绑定（或由 Editor 里的 `InputMappingAsset` 配）。
        /// 框架里写死"技能1=数字键1"就等于把业务烧进框架 —— 那正是 FW-12 要修的病。
        /// </para>
        /// <para>⚠️ 有默认值**不等于硬编码**：关键在于**能不能被覆盖**。
        /// 原版把 WASD 烧在 `CheckKeyCode` 里、连覆盖的机会都没有。</para>
        /// </summary>
        /// <returns>默认映射表。</returns>
        public static InputMapping CreateDefault()
        {
            return new InputMapping();
        }
    }
}
