// 这个文件里有：
//   DamagePopup（静态类）：在世界坐标处飘一个伤害数字（自己上浮 + 淡出 + 销毁）。
//   DamagePopupView（组件）：挂在飘字物体上，负责那 1.2 秒的动画。

using TMPro;
using UnityEngine;

namespace NBC.Game.UI
{
    // 主要功能：把「伤害数字」做成一次性的世界空间文本，按伤害大小变色变粗大，1.2 秒后自己销毁。
    // 主要数据成员：无（全部是方法内局部状态）。
    // 主要外部方法：Show（唯一入口）。
    // 谁在用它：SnapshotSceneBinder（怪物掉血）、RuntimeNetDriver（玩家掉血）。
    // 注意：不做对象池 —— 同屏数字个位数，Instantiate + Destroy 足够，且不容易泄漏。
    public static class DamagePopup
    {
        // 飘字存活时间，秒
        private const float LifetimeSeconds = 1.2f;

        // 上浮速度，米/秒
        private const float RiseMetersPerSecond = 1.2f;

        // 伤害分档的阈值（单位：点）；小于第一档是最小的白色数字
        private const int MediumDamage = 20;
        private const int HeavyDamage = 50;

        // 每条飘字的随机水平偏移上限，米；避免连续命中时数字完全叠在一起
        private const float ScatterRadius = 0.25f;

        // 是否已经为「缺默认字体」告过警；只报一次，避免刷屏
        private static bool m_fontWarned;

        // 功能：在世界坐标处飘一个伤害数字（内部显示成 "-amount"）。
        // 入参：worldPosition 世界坐标（一般取受击者头顶）；amount 伤害点数，必须为正
        // 返回：无
        // 步骤：
        //   1) 伤害 <= 0 直接忽略（治疗/无效帧不飘）
        //   2) 没有 TMP 默认字体就只告警一次并放弃（否则会创建出看不见的文本）
        //   3) 建物体 + TextMeshPro，按伤害分档设颜色与字号
        //   4) 挂上 DamagePopupView，由它负责上浮淡出与销毁
        // 上游：SnapshotSceneBinder（怪物受击）、RuntimeNetDriver（玩家受击）
        // 下游：DamagePopupView
        // 注意：位置用世界坐标；飘字是纯表现，不参与任何战斗结算
        public static void Show(Vector3 worldPosition, int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            if (TMP_Settings.defaultFontAsset == null)
            {
                if (!m_fontWarned)
                {
                    m_fontWarned = true;
                    Debug.LogWarning("[伤害飘字] TMP 没有默认字体资产，伤害数字不会显示（请检查 TMP Settings）。");
                }

                return;
            }

            GameObject go = new GameObject("伤害飘字");
            go.transform.position = worldPosition + new Vector3(
                Random.Range(-ScatterRadius, ScatterRadius),
                0f,
                Random.Range(-ScatterRadius, ScatterRadius));

            TextMeshPro text = go.AddComponent<TextMeshPro>();
            text.text = "-" + amount;
            text.alignment = TextAlignmentOptions.Center;
            text.fontSize = ResolveFontSize(amount);
            text.color = ResolveColor(amount);
            text.raycastTarget = false;

            go.AddComponent<DamagePopupView>().Initialize(RiseMetersPerSecond, LifetimeSeconds);
        }

        // 功能：按伤害大小给字号。
        // 入参：amount 伤害点数
        // 返回：字号（点）
        // 步骤：分档判断，越重越大
        // 上游：Show
        // 下游：TextMeshPro.fontSize
        private static float ResolveFontSize(int amount)
        {
            if (amount >= HeavyDamage)
            {
                return 48f;
            }

            return amount >= MediumDamage ? 38f : 30f;
        }

        // 功能：按伤害大小给颜色。
        // 入参：amount 伤害点数
        // 返回：颜色
        // 步骤：小伤害偏白、中伤害橙、重伤害红
        // 上游：Show
        // 下游：TextMeshPro.color
        private static Color ResolveColor(int amount)
        {
            if (amount >= HeavyDamage)
            {
                return new Color(1f, 0.30f, 0.30f);      // 红
            }

            return amount >= MediumDamage
                ? new Color(1f, 0.70f, 0.28f)            // 橙
                : new Color(1f, 0.96f, 0.84f);           // 近白
        }
    }

    // 主要功能：驱动一个飘字物体的上浮与淡出，到期自毁。
    // 主要数据成员：m_riseSpeed（米/秒）、m_lifetime（秒）、m_elapsed（已存在时间）、m_text（自己身上的 TMP）。
    // 主要外部方法：Initialize（由 DamagePopup.Show 调）。
    // 谁在用它：DamagePopup.Show。
    // 注意：不需要 Update 也能活，但用了 Transform + 颜色两件事，放 Update 里最直白。
    public sealed class DamagePopupView : MonoBehaviour
    {
        // 上浮速度，米/秒
        private float m_riseSpeed = 1.2f;

        // 总存活时间，秒
        private float m_lifetime = 1.2f;

        // 已经存活了多久，秒
        private float m_elapsed;

        // 自己身上的文本；淡出要改它的 alpha
        private TextMeshPro m_text;

        // 功能：初始化（由 Show 调，构造时没有参数）。
        // 入参：riseSpeed 上浮速度（米/秒）；lifetime 存活时间（秒）
        // 返回：无
        // 步骤：记参数 + 抓一次 TMP 引用
        // 上游：DamagePopup.Show
        // 下游：Update
        public void Initialize(float riseSpeed, float lifetime)
        {
            m_riseSpeed = riseSpeed;
            m_lifetime = lifetime > 0.01f ? lifetime : 0.01f;
            m_text = GetComponent<TextMeshPro>();
        }

        // 功能：每帧上浮 + 后半程淡出，时间到自毁。
        // 入参：无（引擎每帧调）
        // 返回：无
        // 步骤：
        //   1) 累加时间
        //   2) 上浮（只动 Y）
        //   3) 从一半时间开始线性淡出到 0
        //   4) 超过存活时间就 Destroy 自己
        // 上游：引擎
        // 下游：Destroy
        private void Update()
        {
            m_elapsed += Time.deltaTime;
            transform.position += Vector3.up * (m_riseSpeed * Time.deltaTime);

            if (m_text != null)
            {
                float half = m_lifetime * 0.5f;
                float alpha = m_elapsed <= half ? 1f : Mathf.Clamp01(1f - ((m_elapsed - half) / half));
                Color color = m_text.color;
                color.a = alpha;
                m_text.color = color;
            }

            if (m_elapsed >= m_lifetime)
            {
                Destroy(gameObject);
            }
        }
    }
}
