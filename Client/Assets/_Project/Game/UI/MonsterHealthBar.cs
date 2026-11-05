// 这个文件里有：
//   MonsterHealthBar（组件）：怪物头顶的血条（底 + 填充两个 Sprite，始终朝向相机）。

using UnityEngine;

namespace NBC.Game.UI
{
    // 主要功能：给怪物槽位画一条头顶血条 —— 比例由外部喂，掉血就短，死了随槽位一起隐藏。
    // 主要数据成员：m_fill（填充条 Transform）、m_maxHp（满血值）、m_ratio（当前比例）。
    // 主要外部方法：Bind（建条）、SetRatio（喂比例）。
    // 谁在用它：SnapshotSceneBinder 建槽位时 Bind，每帧 SetRatio。
    // 注意：不用 UI Canvas —— 两个 SpriteRenderer 更便宜、不依赖 UI 程序集，也不需要 EventSystem。
    public sealed class MonsterHealthBar : MonoBehaviour
    {
        // 血条离地面高度，米（按狼约 1 米高定的；其它体型靠父节点缩放也能用）
        private const float HeightMeters = 1.6f;

        // 血条宽度，米
        private const float BarWidth = 0.9f;

        // 血条高度，米
        private const float BarHeight = 0.12f;

        // 填充条（改它的 localScale.x 表示比例）
        private Transform m_fill;

        // 满血值；<=0 时按 1 处理，避免除零
        private int m_maxHp = 1;

        // 当前比例（0..1）
        private float m_ratio = 1f;

        // 全项目共用一张 1×1 白色 Sprite（只为血条生成一次）
        private static Sprite s_whiteSprite;

        // 是否已经为「找不到相机」告过警；只报一次
        private static bool s_cameraWarned;

        // 功能：建出血条（底黑 + 填充红），并设成满血。
        // 入参：maxHp 满血值（<=0 按 1 处理）
        // 返回：无
        // 步骤：
        //   1) 记满血值
        //   2) 建底条（深灰、不透明）
        //   3) 建填充条（红），左对齐缩放
        //   4) 比例设 1
        // 上游：SnapshotSceneBinder.EnsureSlot
        // 下游：SetRatio、Update（朝向相机）
        // 注意：血条挂在槽位根上，**不要**挂到带刚体的子物体上（否则会跟着物理抖）
        public void Bind(int maxHp)
        {
            m_maxHp = maxHp > 0 ? maxHp : 1;

            Sprite sprite = GetWhiteSprite();

            // 底条：居中，整条宽
            GameObject background = new GameObject("血条底");
            background.transform.SetParent(transform, false);
            background.transform.localPosition = new Vector3(0f, HeightMeters, 0f);
            background.transform.localScale = new Vector3(BarWidth, BarHeight, 1f);

            SpriteRenderer back = background.AddComponent<SpriteRenderer>();
            back.sprite = sprite;
            back.color = new Color(0.08f, 0.08f, 0.08f, 0.85f);
            back.sortingOrder = 100;

            // 填充条：原点在左端，靠 scale.x 表示比例
            GameObject fill = new GameObject("血条填充");
            fill.transform.SetParent(transform, false);
            fill.transform.localPosition = new Vector3(0f, HeightMeters, 0f);
            fill.transform.localScale = new Vector3(BarWidth, BarHeight * 0.7f, 1f);

            SpriteRenderer bar = fill.AddComponent<SpriteRenderer>();
            bar.sprite = sprite;
            bar.color = new Color(0.85f, 0.18f, 0.18f, 1f);
            bar.sortingOrder = 101;

            m_fill = fill.transform;
            SetRatio(1f);
        }

        // 功能：设置血条比例。
        // 入参：ratio 0..1（外部保证；这里仍会钳位）
        // 返回：无
        // 步骤：
        //   1) 钳到 0..1，没变化就直接返回（避免每帧写 Transform）
        //   2) 改填充条 scale.x，并把左端对齐：位置左移 (1-ratio) * 宽度 / 2
        // 上游：SnapshotSceneBinder 每帧
        // 下游：Transform.localScale / localPosition
        public void SetRatio(float ratio)
        {
            float clamped = Mathf.Clamp01(ratio);

            if (Mathf.Abs(clamped - m_ratio) < 0.001f || m_fill == null)
            {
                m_ratio = clamped;
                return;
            }

            m_ratio = clamped;

            Vector3 scale = m_fill.localScale;
            m_fill.localScale = new Vector3(BarWidth * clamped, scale.y, scale.z);

            Vector3 position = m_fill.localPosition;
            m_fill.localPosition = new Vector3(-(BarWidth * (1f - clamped)) * 0.5f, position.y, position.z);
        }

        // 功能：让血条始终正对相机（billboard）。
        // 入参：无（引擎每帧调）
        // 返回：无
        // 步骤：没有相机就告警一次并跳过；有就把自己的旋转设成相机的旋转
        // 上游：引擎
        // 下游：Transform.rotation
        private void Update()
        {
            Camera camera = Camera.main;

            if (camera == null)
            {
                if (!s_cameraWarned)
                {
                    s_cameraWarned = true;
                    Debug.LogWarning("[怪物血条] 场景里没有 MainCamera，血条不会朝向镜头（功能不受影响）。");
                }

                return;
            }

            transform.rotation = camera.transform.rotation;
        }

        // 功能：取共用的 1×1 白色 Sprite（第一次调用时生成并缓存）。
        // 入参：无
        // 返回：Sprite（永不为 null）
        // 步骤：
        //   1) 已经生成过就直接返回
        //   2) 建一张 1×1 白纹理，设成 Point 过滤避免模糊
        //   3) 用它创建 Sprite 并缓存
        // 上游：Bind
        // 下游：SpriteRenderer.sprite
        private static Sprite GetWhiteSprite()
        {
            if (s_whiteSprite != null)
            {
                return s_whiteSprite;
            }

            Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, Color.white);
            texture.filterMode = FilterMode.Point;
            texture.Apply();

            s_whiteSprite = Sprite.Create(texture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
            return s_whiteSprite;
        }
    }
}
