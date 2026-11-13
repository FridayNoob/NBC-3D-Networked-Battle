// 这个文件里有：
//   SceneFlowDebugKeys（MonoBehaviour）：开发期快捷键切场景（Ctrl+Shift+1~5）。
//
// 为什么单独一个文件：Unity 要求"组件类名 = 文件名"，一个 .cs 里放两个 MonoBehaviour 时，
// 第二个会被当成"丢失的脚本"挂不上去（踩过：场景里只有 m_ClassName、没有 m_Script）。

using UnityEngine;

namespace NBC.Game.GameFlow
{
    // 主要功能：Ctrl+Shift+1~5 分别加载 Login/World/PVP_Room/Battle/Test。
    // 主要数据成员：m_enabled（是否启用）。
    // 主要外部方法：Unity 的 Update。
    // 谁在用它：挂在 Login/World 场景里手测；正式版本移除。
    // 注意：不用 F1~F5 —— 它们被 Unity 编辑器占用（F1 帮助 / F2 重命名），Play 时收不到。
    public sealed class SceneFlowDebugKeys : MonoBehaviour
    {
        // 是否启用快捷键（关掉就不会误触）
        [SerializeField]
        private bool m_enabled = true;

        // 功能：每帧查快捷键（Ctrl+Shift+1~5）。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) Ctrl 与 Shift 都按住才生效
        //   2) 1~5 分别加载五个场景
        //   3) 每次按键都打日志 —— 没日志就说明按键没进来
        // 上游：Unity
        // 下游：RequestLoad
        private void Update()
        {
            if (!m_enabled)
            {
                return;
            }

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            if (!ctrl || !shift)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.Alpha1)) { RequestLoad(GameScene.Login); }
            if (Input.GetKeyDown(KeyCode.Alpha2)) { RequestLoad(GameScene.World); }
            if (Input.GetKeyDown(KeyCode.Alpha3)) { RequestLoad(GameScene.PvpRoom); }
            if (Input.GetKeyDown(KeyCode.Alpha4)) { RequestLoad(GameScene.Battle); }
            if (Input.GetKeyDown(KeyCode.Alpha5)) { RequestLoad(GameScene.Test); }
        }

        // 功能：请求加载并打印结果（成功/失败/被拒绝都能看到）。
        // 入参：scene 目标场景
        // 返回：无
        // 步骤：打请求日志 → 调 SceneFlow → 回调里打结果
        // 上游：Update
        // 下游：SceneFlow.LoadAsync
        private static void RequestLoad(GameScene scene)
        {
            Debug.Log("[SceneFlowDebugKeys] 请求加载 " + scene);

            SceneFlow.Instance.LoadAsync(scene, (ok, error) =>
            {
                if (ok)
                {
                    Debug.Log("[SceneFlowDebugKeys] 已切换到 " + scene + "（路径：" + SceneFlow.Instance.LastPath + "）");
                }
                else
                {
                    Debug.LogWarning("[SceneFlowDebugKeys] 切换 " + scene + " 失败：" + error);
                }
            });
        }
    }
}