// 这个文件里有：
// MainMenuState：主菜单的纯逻辑状态（昵称规范化、按钮可用性、提示文案），不引用任何 UnityEngine 类型。

using System;

namespace NBC.Game.Menu
{
    /// <summary>
    /// 主菜单的纯逻辑状态。
    /// <para>主要功能：把「昵称怎么算合法」「什么时候能点开始」「按钮下方显示什么」三件事收在一起，方便单测。</para>
    /// <para>主要数据成员：m_playerName（规范化后的昵称）、m_connecting（连接中）、m_succeeded（已连上）、m_hint（提示）。</para>
    /// <para>主要外部方法：BeginConnect / Fail / Succeed 三个状态转移，CanStart / IsConnecting 两个查询。</para>
    /// <para>谁在用它：MainMenuController（读状态改界面）、EditMode 用例（直接断言状态）。</para>
    /// </summary>
    public sealed class MainMenuState
    {
        /// <summary>昵称留空时用的名字。</summary>
        public const string DefaultName = "玩家";

        /// <summary>昵称最多几个字（超出截断）。</summary>
        public const int MaxNameLength = 12;

        /// <summary>连接失败且没拿到服务端原因时的默认提示。</summary>
        public const string DefaultFailureHint = "连不上服务器，请确认服务端已启动（端口 7778）";

        private string m_playerName = DefaultName;
        private bool m_connecting;
        private bool m_succeeded;
        private string m_hint = string.Empty;

        /// <summary>规范化后的昵称（空输入会变成「玩家」）。</summary>
        public string PlayerName
        {
            get { return m_playerName; }
        }

        /// <summary>现在能不能点「开始游戏」（连接中或已连上都不行）。</summary>
        public bool CanStart
        {
            get { return !m_connecting && !m_succeeded; }
        }

        /// <summary>是否正在连接。</summary>
        public bool IsConnecting
        {
            get { return m_connecting; }
        }

        /// <summary>是否已经连上并准备切场景。</summary>
        public bool IsSucceeded
        {
            get { return m_succeeded; }
        }

        /// <summary>按钮下方的提示（空串表示不显示）。</summary>
        public string Hint
        {
            get { return m_hint; }
        }

        /// <summary>
        /// 把玩家输入的名字规范化。
        /// </summary>
        /// <param name="raw">输入框里的原始文本，允许 null。</param>
        /// <returns>去掉首尾空白与换行、长度 1~12 的名字；空输入返回「玩家」。</returns>
        /// <remarks>
        /// 把这件事放在纯逻辑里，是为了让「留空 / 只有空格 / 超长」三种情况都能用单测覆盖，
        /// 不必为了验一个 trim 去进 Play 模式。
        /// </remarks>
        public static string NormalizeName(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return DefaultName;
            }

            string cleaned = raw.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();

            if (cleaned.Length == 0)
            {
                return DefaultName;
            }

            if (cleaned.Length > MaxNameLength)
            {
                cleaned = cleaned.Substring(0, MaxNameLength);
            }

            return cleaned;
        }

        /// <summary>
        /// 记下昵称。
        /// </summary>
        /// <param name="raw">输入框里的原始文本。</param>
        /// <returns>规范化后的昵称。</returns>
        public string SetPlayerName(string raw)
        {
            m_playerName = NormalizeName(raw);
            return m_playerName;
        }

        /// <summary>
        /// 开始连接：按钮变灰、提示清空。
        /// </summary>
        /// <returns>这次调用是否真的开始了连接（已经在连接或已连上时返回 false）。</returns>
        public bool BeginConnect()
        {
            if (!CanStart)
            {
                return false;
            }

            m_connecting = true;
            m_succeeded = false;
            m_hint = string.Empty;
            return true;
        }

        /// <summary>
        /// 连接失败：恢复按钮、记下失败原因。
        /// </summary>
        /// <param name="reason">人话原因；为空时用默认文案。</param>
        public void Fail(string reason)
        {
            m_connecting = false;
            m_succeeded = false;
            m_hint = string.IsNullOrEmpty(reason) ? DefaultFailureHint : reason;
        }

        /// <summary>
        /// 连接成功：按钮保持不可点，提示改成「正在进入世界…」。
        /// </summary>
        public void Succeed()
        {
            m_connecting = false;
            m_succeeded = true;
            m_hint = "已连接，正在进入世界…";
        }
    }
}
