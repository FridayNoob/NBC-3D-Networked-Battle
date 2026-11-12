// 这个文件里有：
//   MonsterAiDebugView（组件）：把怪的三条判定画出来（攻击红 / 玩家检测白 / 接地蓝），并允许在 Inspector 覆盖长度。
//
// 真值来自 MonsterConfig（与服务端同源的生成配置）；覆盖值只在"还没写回"时用于预览，
// 写回由 MonsterAiDebugViewEditor 负责把三列写进 Configs/Design/Monster.csv。

using NBC.Game.Config;
using UnityEngine;

namespace NBC.Game.View
{
    // 主要功能：编辑期用 Gizmos、运行期用 LineRenderer，把攻击/检测/接地三条判定画在怪身上。
    // 主要数据成员：m_config（表）、m_monsterConfigId（写回哪一行）、三个覆盖值、三个子 Transform。
    // 主要外部方法：Bind（运行时由 Binder 调用）、Display* 只读属性、IsOverriding。
    // 谁在用它：怪物预制体（Forest Wolf）上的组件；SnapshotSceneBinder 负责 Bind。
    // 注意：覆盖值只影响"显示"，服务端判定永远读表；所以覆盖未写回时 Inspector 必须提示不一致。
    public sealed class MonsterAiDebugView : MonoBehaviour
    {
        // 三个子 Transform 的固定名字（缺失时自动创建）
        public const string AttackAnchorName = "Attack";
        public const string AggroAnchorName = "Aggro";
        public const string GroundAnchorName = "Ground";

        // 毫米 → 米
        private const float MmToUnit = 0.001f;

        // 圆的采样点数（越大越圆，48 足够）
        private const int CircleSegments = 48;

        // 线宽（米）
        private const float LineWidth = 0.02f;

        // 攻击判定颜色（红）
        public static readonly Color AttackColor = new Color(1f, 0.2f, 0.2f, 0.9f);

        // 玩家检测颜色（白）
        public static readonly Color AggroColor = new Color(1f, 1f, 1f, 0.8f);

        // 接地颜色（蓝）
        public static readonly Color GroundColor = new Color(0.25f, 0.55f, 1f, 0.9f);

        // 怪物配置表（Client/Assets/_Project/Configs/MonsterConfig.asset）
        [SerializeField]
        private MonsterConfig m_config;

        // 写回时用哪一行（Monster 表主键，6001 = 野狼）
        [SerializeField]
        private int m_monsterConfigId = 6001;

        // 攻击判定半径覆盖（毫米）；0 = 用表值
        [SerializeField]
        private int m_attackRangeMmOverride;

        // 玩家检测半径覆盖（毫米）；0 = 用表值
        [SerializeField]
        private int m_aggroRangeMmOverride;

        // 接地线长度覆盖（毫米）；0 = 用表值
        [SerializeField]
        private int m_groundLineMmOverride;

        // 攻击判定中心
        [SerializeField]
        private Transform m_attackAnchor;

        // 玩家检测中心
        [SerializeField]
        private Transform m_aggroAnchor;

        // 接地点
        [SerializeField]
        private Transform m_groundAnchor;

        // 运行期的三条线（懒创建）
        private LineRenderer m_attackLine;
        private LineRenderer m_aggroLine;
        private LineRenderer m_groundLine;

        // 圆顶点缓存（复用，避免每帧分配）
        private Vector3[] m_circleBuffer;

        // 怪物配置编号（可运行时改）
        public int MonsterConfigId
        {
            get { return m_monsterConfigId; }
            set { m_monsterConfigId = value; }
        }

        // 攻击半径覆盖值（毫米，0 = 用表值）
        public int AttackRangeMmOverride
        {
            get { return m_attackRangeMmOverride; }
            set { m_attackRangeMmOverride = value; }
        }

        // 检测半径覆盖值（毫米，0 = 用表值）
        public int AggroRangeMmOverride
        {
            get { return m_aggroRangeMmOverride; }
            set { m_aggroRangeMmOverride = value; }
        }

        // 接地线覆盖值（毫米，0 = 用表值）
        public int GroundLineMmOverride
        {
            get { return m_groundLineMmOverride; }
            set { m_groundLineMmOverride = value; }
        }

        // 表里的攻击半径（毫米）；读不到返回 0
        public int TableAttackRangeMm
        {
            get
            {
                Config_Monster row = ResolveRow();
                return row == null ? 0 : row.attackRangeMm;
            }
        }

        // 表里的检测半径（毫米）
        public int TableAggroRangeMm
        {
            get
            {
                Config_Monster row = ResolveRow();
                return row == null ? 0 : row.aggroRangeMm;
            }
        }

        // 表里的接地线长（毫米）
        public int TableGroundLineMm
        {
            get
            {
                Config_Monster row = ResolveRow();
                return row == null ? 0 : row.groundLineMm;
            }
        }

        // 显示用攻击半径：覆盖优先，否则表值
        public int DisplayAttackRangeMm
        {
            get { return m_attackRangeMmOverride > 0 ? m_attackRangeMmOverride : TableAttackRangeMm; }
        }

        // 显示用检测半径
        public int DisplayAggroRangeMm
        {
            get { return m_aggroRangeMmOverride > 0 ? m_aggroRangeMmOverride : TableAggroRangeMm; }
        }

        // 显示用接地线长
        public int DisplayGroundLineMm
        {
            get { return m_groundLineMmOverride > 0 ? m_groundLineMmOverride : TableGroundLineMm; }
        }

        // 是否有覆盖值（与表值不同）
        public bool IsOverriding
        {
            get
            {
                return (m_attackRangeMmOverride > 0 && m_attackRangeMmOverride != TableAttackRangeMm)
                       || (m_aggroRangeMmOverride > 0 && m_aggroRangeMmOverride != TableAggroRangeMm)
                       || (m_groundLineMmOverride > 0 && m_groundLineMmOverride != TableGroundLineMm);
            }
        }

        // 功能：运行时由 Binder 告知这是哪个怪型（决定读哪一行表）。
        // 入参：monsterConfigId Monster 表主键
        // 返回：无
        // 步骤：编号合法就记下
        // 上游：SnapshotSceneBinder.EnsureSlot
        // 下游：ResolveRow
        public void Bind(int monsterConfigId)
        {
            if (monsterConfigId > 0)
            {
                m_monsterConfigId = monsterConfigId;
            }
        }

        // 功能：清掉三个覆盖值（写回成功后调用，让显示回到表值）。
        // 入参：无
        // 返回：无
        // 步骤：三个覆盖值归零
        // 上游：MonsterAiDebugViewEditor
        // 下游：Display* 属性
        public void ClearOverrides()
        {
            m_attackRangeMmOverride = 0;
            m_aggroRangeMmOverride = 0;
            m_groundLineMmOverride = 0;
        }

        // 功能：在编辑期画三色 Gizmos。
        // 入参：无
        // 返回：无
        // 步骤：取三个锚点 → 红圈/白圈/蓝竖线
        // 上游：Unity 编辑器
        // 下游：DrawCircle
        private void OnDrawGizmos()
        {
            EnsureAnchors();

            Gizmos.color = AttackColor;
            DrawCircle(m_attackAnchor, DisplayAttackRangeMm);

            Gizmos.color = AggroColor;
            DrawCircle(m_aggroAnchor, DisplayAggroRangeMm);

            Gizmos.color = GroundColor;

            if (m_groundAnchor != null)
            {
                float length = DisplayGroundLineMm * MmToUnit;
                Gizmos.DrawLine(m_groundAnchor.position, m_groundAnchor.position + Vector3.down * length);
            }
        }

        // 功能：运行期每帧刷新三条 LineRenderer。
        // 入参：无
        // 返回：无
        // 步骤：确保线和缓冲 → 更新两个圆 → 更新接地竖线
        // 上游：Unity 每帧
        // 下游：LineRenderer.SetPositions
        private void LateUpdate()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            EnsureAnchors();
            EnsureRenderers();

            UpdateCircle(m_attackLine, m_attackAnchor, DisplayAttackRangeMm);
            UpdateCircle(m_aggroLine, m_aggroAnchor, DisplayAggroRangeMm);

            if (m_groundLine != null && m_groundAnchor != null)
            {
                float length = DisplayGroundLineMm * MmToUnit;
                m_groundLine.positionCount = 2;
                m_groundLine.SetPosition(0, m_groundAnchor.position);
                m_groundLine.SetPosition(1, m_groundAnchor.position + Vector3.down * length);
            }
        }

        // 功能：读表里的这一行（读不到返回 null）。
        // 入参：无
        // 返回：配置行；无表/无主键返回 null
        // 步骤：m_config.TryGet
        // 上游：Display* 属性
        // 下游：无
        private Config_Monster ResolveRow()
        {
            if (m_config == null)
            {
                return null;
            }

            Config_Monster row;
            return m_config.TryGet(m_monsterConfigId, out row) ? row : null;
        }

        // 功能：确保三个锚点存在（缺失就自动创建）。
        // 入参：无
        // 返回：无
        // 步骤：逐个检查，缺失则建空物体并挂到自身下
        // 上游：OnDrawGizmos / LateUpdate
        // 下游：CreateAnchor
        private void EnsureAnchors()
        {
            if (m_attackAnchor == null)
            {
                m_attackAnchor = CreateAnchor(AttackAnchorName, new Vector3(0f, 0f, 0.5f));
            }

            if (m_aggroAnchor == null)
            {
                m_aggroAnchor = CreateAnchor(AggroAnchorName, Vector3.zero);
            }

            if (m_groundAnchor == null)
            {
                m_groundAnchor = CreateAnchor(GroundAnchorName, Vector3.zero);
            }
        }

        // 功能：创建一个锚点（运行期也安全）。
        // 入参：name 名字；localPosition 本地位置
        // 返回：新 Transform
        // 步骤：查同名子物体 → 没有就建
        // 上游：EnsureAnchors
        // 下游：无
        private Transform CreateAnchor(string name, Vector3 localPosition)
        {
            Transform existing = transform.Find(name);

            if (existing != null)
            {
                return existing;
            }

            GameObject go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.identity;
            return go.transform;
        }

        // 功能：懒创建三条 LineRenderer。
        // 入参：无
        // 返回：无
        // 步骤：各自取或加组件 → 设置材质/宽度/颜色/世界坐标
        // 上游：LateUpdate
        // 下游：ConfigureLine
        private void EnsureRenderers()
        {
            if (m_attackLine == null && m_attackAnchor != null)
            {
                m_attackLine = ConfigureLine(m_attackAnchor, AttackColor);
            }

            if (m_aggroLine == null && m_aggroAnchor != null)
            {
                m_aggroLine = ConfigureLine(m_aggroAnchor, AggroColor);
            }

            if (m_groundLine == null && m_groundAnchor != null)
            {
                m_groundLine = ConfigureLine(m_groundAnchor, GroundColor);
            }
        }

        // 功能：给锚点配一条 LineRenderer。
        // 入参：anchor 锚点；color 颜色
        // 返回：LineRenderer
        // 步骤：取或加组件 → 材质/宽度/世界空间/颜色 → 关闭阴影与光照
        // 上游：EnsureRenderers
        // 下游：无
        private static LineRenderer ConfigureLine(Transform anchor, Color color)
        {
            LineRenderer line = anchor.GetComponent<LineRenderer>();

            if (line == null)
            {
                line = anchor.gameObject.AddComponent<LineRenderer>();
            }

            line.useWorldSpace = true;
            line.loop = false;
            line.widthMultiplier = LineWidth;
            line.numCornerVertices = 0;
            line.numCapVertices = 0;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.material = new Material(Shader.Find("Sprites/Default"));
            line.startColor = color;
            line.endColor = color;
            return line;
        }

        // 功能：把一条线更新成圆。
        // 入参：line 线；anchor 圆心；radiusMm 半径（毫米）
        // 返回：无
        // 步骤：缓冲不足就重建 → 逐点算圆 → SetPositions
        // 上游：LateUpdate
        // 下游：无
        private void UpdateCircle(LineRenderer line, Transform anchor, int radiusMm)
        {
            if (line == null || anchor == null)
            {
                return;
            }

            int count = CircleSegments + 1;

            if (m_circleBuffer == null || m_circleBuffer.Length != count)
            {
                m_circleBuffer = new Vector3[count];
            }

            float radius = radiusMm * MmToUnit;
            Vector3 center = anchor.position;

            for (int i = 0; i < count; i++)
            {
                float angle = (360f / CircleSegments) * i * Mathf.Deg2Rad;
                m_circleBuffer[i] = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            }

            line.positionCount = count;
            line.SetPositions(m_circleBuffer);
        }

        // 功能：画一个圆（Gizmos 版）。
        // 入参：anchor 圆心；radiusMm 半径（毫米）
        // 返回：无
        // 步骤：逐段 DrawLine 连成圆
        // 上游：OnDrawGizmos
        // 下游：无
        private static void DrawCircle(Transform anchor, int radiusMm)
        {
            if (anchor == null || radiusMm <= 0)
            {
                return;
            }

            float radius = radiusMm * MmToUnit;
            Vector3 center = anchor.position;
            Vector3 previous = center + new Vector3(radius, 0f, 0f);

            for (int i = 1; i <= CircleSegments; i++)
            {
                float angle = (360f / CircleSegments) * i * Mathf.Deg2Rad;
                Vector3 next = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                Gizmos.DrawLine(previous, next);
                previous = next;
            }
        }
    }
}
