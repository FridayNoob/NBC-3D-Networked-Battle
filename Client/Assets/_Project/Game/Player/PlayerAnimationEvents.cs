// 这个文件里有：
//   PlayerAnimationEvents（类）：动画事件接收器 —— 你在 Animation 窗口里给关键帧挂 Event 之后，
//   Unity 会在这个物体上调同名方法，这里统一把"动作到了哪一拍"变成日志与可订阅的事件。

using System;
using UnityEngine;

namespace NBC.Game.Player
{
    // 主要功能：接收美术动画里的关键帧事件（起手 / 命中 / 落脚），只做表现层通知，不碰网络与判定。
    // 主要数据成员：m_logEvents（是否打日志）、m_brain（用来确认自己挂在玩家身上）、m_frameSource（播报当前动作名）。
    // 主要外部方法：OnSkillStart / OnSkillHit / OnAttackHit / OnFootstep（都被 Animation Event 按名字调用）。
    // 谁在用它：Unity 动画事件（配置在 clip 的关键帧上）；将来的特效、音效、顿帧脚本订阅这里的 public 事件。
    // 注意：动画事件只在客户端跑、按动画时间走（受 timeScale 与卡顿影响），
    //       所以它绝对不能用来决定"伤害什么时候结算" —— 那个由服务端的 hitFrames 决定（见 R043b）。
    [DisallowMultipleComponent]
    public sealed class PlayerAnimationEvents : MonoBehaviour
    {
        // 是否在 Console 里打一条日志（调试期开着，正式发布可关）
        [SerializeField] private bool m_logEvents = true;

        // 缓存的玩家大脑（在父级上找；找不到说明挂错物体了，会告警一次）
        private PlayerBrain m_brain;

        // 技能起手事件的订阅者（index = 1~3，对应 Q/E/R）
        public event Action<int> SkillStarted;

        // 技能命中事件的订阅者（skillId = 技能编号，例如 2001）
        public event Action<int> SkillHit;

        // 普攻命中事件的订阅者（combo = 1~3，对应三段连击）
        public event Action<int> AttackHit;

        // 落脚事件的订阅者（将来接脚步声用）
        public event Action Footstep;

        // 功能：缓存玩家大脑，并检查自己是不是挂在了 Animator 所在的那个物体上。
        // 入参：无
        // 返回：无
        // 步骤：1) 从父级找 PlayerBrain 2) 找不到就打一条告警（动画事件是按 GameObject 派发的，挂错就收不到）
        // 上游：Unity 生命周期
        // 下游：PlayerBrain（只读）
        private void Awake()
        {
            m_brain = GetComponentInParent<PlayerBrain>();

            if (m_brain == null && m_logEvents)
            {
                Debug.LogWarning("[动画事件] 这个物体是 " + name + "，父级上没有 PlayerBrain —— " +
                                 "确认它挂在了带 Animator 的模型物体上（动画事件只会派发给 Animator 所在的 GameObject）");
            }
        }

        // 功能：技能起手（建议加在技能 clip 的第 1 帧或挥出去之前）。
        // 入参：index 技能序号（1~3，对应 Q/E/R）
        // 返回：无
        // 步骤：打日志 + 触发 SkillStarted
        // 上游：动画事件 OnSkillStart
        // 下游：特效/音效脚本（订阅 SkillStarted）
        public void OnSkillStart(int index)
        {
            Log("技能 " + index + " 起手");

            SkillStarted?.Invoke(index);
        }

        // 功能：技能命中（建议加在刀真正挥到的那一帧，和 Skill.csv 的 hitFrames 对齐）。
        // 入参：skillId 技能编号（例如 2001；同一个技能可以加多个事件表示多段）
        // 返回：无
        // 步骤：打日志 + 触发 SkillHit
        // 上游：动画事件 OnSkillHit
        // 下游：订阅 SkillHit 的特效脚本（刀光、命中火花）
        public void OnSkillHit(int skillId)
        {
            Log("技能 " + skillId + " 命中帧");

            SkillHit?.Invoke(skillId);
        }

        // 功能：普攻命中（三段连击各自的那一帧）。
        // 入参：combo 连击序号（1~3）
        // 返回：无
        // 步骤：打日志 + 触发 AttackHit
        // 上游：动画事件 OnAttackHit
        // 下游：订阅 AttackHit 的特效脚本
        public void OnAttackHit(int combo)
        {
            Log("普攻第 " + combo + " 段命中帧");

            AttackHit?.Invoke(combo);
        }

        // 功能：脚落地（给将来的脚步声留的接口）。
        // 入参：无
        // 返回：无
        // 步骤：打日志 + 触发 Footstep
        // 上游：动画事件 OnFootstep
        // 下游：音频脚本（订阅 Footstep）
        public void OnFootstep()
        {
            Log("落脚");

            Footstep?.Invoke();
        }

        // 功能：统一打日志（m_logEvents 关掉时什么都不做）。
        // 入参：message 中文描述
        // 返回：无
        // 步骤：拼前缀后 Debug.Log
        // 上游：上面四个事件方法
        // 下游：Console
        private void Log(string message)
        {
            if (!m_logEvents)
            {
                return;
            }

            Debug.Log("[动画事件] " + message + "（" + name + "）");
        }
    }
}
