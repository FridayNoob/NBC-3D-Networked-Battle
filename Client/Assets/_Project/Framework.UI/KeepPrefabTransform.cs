// 这个文件里有：
//   KeepPrefabTransform（组件）：挂在面板根上，表示「这个面板保留 prefab 里摆好的位置与尺寸」。

using UnityEngine;

namespace NBC.Framework.UI
{
    // 主要功能：给 UIManager 一个开关 —— 见到这个组件就**不把面板的 RectTransform 归零**。
    // 主要数据成员：无（纯标记，没有字段也没有方法）。
    // 主要外部方法：无。
    // 谁在用它：UIManager.AttachToLayer 检查；HUD 类小面板（如 HpBarPanel）的 prefab 根上挂着它。
    // 注意：只有「固定在屏幕某处、不想铺满」的面板才挂；全屏面板别挂，否则会停在 prefab 摆的位置。
    [DisallowMultipleComponent]
    public sealed class KeepPrefabTransform : MonoBehaviour
    {
    }
}
