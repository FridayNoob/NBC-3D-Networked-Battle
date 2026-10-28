// 这个文件里有：
//   NetHudPanel（类）：屏幕右上角的网络状态面板（模式 / 带宽 / RTT / 帧号）

using NBC.Framework.Net;
using NBC.Framework.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NBC.Game.UI
{
    // 主要功能：每 0.25 秒读一次 NetRuntimeStats，把模式、上下行带宽、RTT、帧号写成文本；F1 显隐。
    // 主要数据成员：四个文本控件、m_box（可见性容器）、m_elapsed（刷新计时）。
    // 主要外部方法：无，只有 Unity 的 Update 回调。
    // 谁在用它：UIManager.ShowPanel("NetHudPanel", UILayer.Bot) 打开；演示/联调时用。
    public sealed class NetHudPanel : BasePanel
    {
        // 模式文本的控件名（prefab 里的 GameObject 名字，必须一致）
        public const string ModeControl = "Text_Mode";

        // 带宽文本的控件名
        public const string SpeedControl = "Text_Speed";

        // RTT 文本的控件名
        public const string RttControl = "Text_Rtt";

        // 帧号文本的控件名
        public const string FrameControl = "Text_Frame";

        // 可见性容器（F1 隐藏它；隐藏自己会让 Update 停掉，F1 就再也按不回来）
        public const string BoxControl = "Box";

        // 刷新间隔（秒）：4 次/秒足够跟手，又不必每帧刷文本
        private const float RefreshInterval = 0.25f;

        // 显示 / 隐藏的快捷键
        private const KeyCode ToggleKey = KeyCode.F1;

        private TMP_Text m_mode;
        private TMP_Text m_speed;
        private TMP_Text m_rtt;
        private TMP_Text m_frame;

        // F1 切换的对象（没有就用自己）
        private GameObject m_box;

        // 距离上次刷新累积的时间
        private float m_elapsed;

        // 入参：无（BasePanel.Initialize 里调）
        // 返回：无
        // 步骤：1) 取四个必需文本；2) 取可选的 Box（取不到就整块面板一起隐藏）
        // 上游：BasePanel.Initialize
        // 下游：BasePanel.RequireControl / GetControl
        protected override void OnInit()
        {
            m_mode = RequireControl<TMP_Text>(ModeControl);
            m_speed = RequireControl<TMP_Text>(SpeedControl);
            m_rtt = RequireControl<TMP_Text>(RttControl);
            m_frame = RequireControl<TMP_Text>(FrameControl);

            // ⚠️ Box 只挂 RectTransform，**不是 UIBehaviour** ⇒ `CollectControls` 收不到它，
            //    `GetControl` 一定返回 null。第一版就是因此回退成"隐藏自己"，Update 停掉 ⇒ F1 再也按不回来。
            //    所以这里按层级找（`transform.Find`），只有真的找不到才退回自己。
            Transform box = transform.Find(BoxControl);

            m_box = box != null ? box.gameObject : gameObject;
        }

        // 入参：无（引擎每帧调）
        // 返回：无
        // 步骤：
        //   1) F1 切换显隐（只切 Box，UI 面板本身保持启用，否则读不到按键）
        //   2) 计时到 0.25 秒才刷新一次文本
        //   3) 读一次快照，四个字段分别写文本
        // 上游：Unity 引擎
        // 下游：NetRuntimeStats.Snapshot
        private void Update()
        {
            if (Input.GetKeyDown(ToggleKey) && m_box != null)
            {
                m_box.SetActive(!m_box.activeSelf);
            }

            m_elapsed += Time.unscaledDeltaTime;

            if (m_elapsed < RefreshInterval)
            {
                return;
            }

            m_elapsed = 0f;

            NetStatsSnapshot snapshot = NetRuntimeStats.Snapshot();

            m_mode.text = DescribeMode(snapshot.Mode);
            m_speed.text = FormatSpeed(snapshot);
            m_rtt.text = snapshot.RttMs < 0 ? "RTT --" : "RTT " + snapshot.RttMs + " ms";
            m_frame.text = snapshot.Frame < 0 ? "帧 --" : "帧 " + snapshot.Frame;
        }

        // 入参：mode 模式枚举
        // 返回：给人看的中文
        private static string DescribeMode(NetSyncMode mode)
        {
            switch (mode)
            {
                case NetSyncMode.StateSync:
                    return "状态同步";
                case NetSyncMode.Lockstep:
                    return "帧同步";
                default:
                    return "离线";
            }
        }

        // 入参：snapshot 统计快照
        // 入参：snapshot 统计快照
        // 返回：形如「↑ 1.2 KB/s  ↓ 340 B/s」的文本
        private static string FormatSpeed(NetStatsSnapshot snapshot)
        {
            return "↑ " + FormatRate(snapshot.BytesPerSecondOut) + "  ↓ " + FormatRate(snapshot.BytesPerSecondIn);
        }

        // 入参：bytesPerSecond 字节每秒
        // 返回：自适应单位的速率文本
        // 步骤：
        //   1) 小于 1 KB/s 用 B/s（否则只有心跳时会被四舍五入成 0.0 KB/s，看着像没在收发）
        //   2) 否则用 KB/s，保留一位小数
        // 上游：FormatSpeed
        // 下游：无
        // 注意：这个自适应是 2026-10-28 实测出来的 —— 只有心跳时约几十字节/秒，固定 KB/s 会显示 0.0
        private static string FormatRate(float bytesPerSecond)
        {
            if (bytesPerSecond < 1024f)
            {
                return Mathf.RoundToInt(bytesPerSecond).ToString() + " B/s";
            }

            float kb = bytesPerSecond / 1024f;
            return kb.ToString("0.0") + " KB/s";
        }
    }
}
