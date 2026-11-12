// 这个文件里有：
//   SnapshotSceneBinder（类）：最小视图接管，把 SnapshotView 的实体快照分发成场景几何体，并让相机跟随我的英雄
//   Slot（类）：一个单位在场景里的物体替身，缓存物体与 Transform，并记录最后被看到的帧号

using System.Collections.Generic;
using NBC.Game.Net;
using NBC.Game.UI;
using NBC.Game.Player;
using NBC.Protocol;
using UnityEngine;

namespace NBC.Game.View
{
    // 主要功能：每帧把 SnapshotView 里的实体快照搬到场景几何体上，按实例编号复用物体、把毫米换成米、隐藏死亡单位，并让相机跟随我的英雄。
    // 主要数据成员：m_view（只读的数据副本）、m_ownerPlayerId（我的玩家编号，用来认英雄）、m_slots（实例编号到 Slot 的复用表）、m_camera / m_cameraCreated / m_cameraFollow（相机与是否跟随）。
    // 主要外部方法：Ensure（找到或运行时建接管组件）、Bind（换绑定并重算相机）、Describe（给调试窗的一句话）；IsBound / PushedFrames / ObjectsCreated 是只读属性。
    // 谁在用它：Editor 的 NetDebugWindow 按钮与 Game 的 RuntimeNetDriver 进房后调用 Ensure；每帧由 Unity 调 LateUpdate，内部再用 EnsureCamera / EnsureSlot / HideGone / FollowHero。
    [DisallowMultipleComponent]
    public sealed class SnapshotSceneBinder : MonoBehaviour
    {
        // 英雄相对普通单位的缩放倍数；1.4 表示大 40%，没有材质贴图时只能靠大小区分「我」
        private const float HeroScale = 1.4f;

        // 毫米换米的系数；1 毫米 = 0.001 米，表现层唯一一次单位换算（数据层全是整数毫米）
        private const float MmToUnit = 0.001f;

        // 追踪速度上限，米/秒；超过这个速度就不是「追」，而是瞬移或数据错乱，直接归位更安全
        private const float MaxChaseSpeed = 60f;

        // 没有刚体时（胶囊兜底）的摆放高度，米；胶囊高 2 ⇒ 半高 0.5 让它站在地面上
        private const float HalfHeight = 0.5f;

        // 怪物 controller 里的走路参数名（Bool）；狼的 controller 就是靠它从 Idle 切到 Walk
        private const string WalkParameter = "Walk";

        // 表现层读的那一份数据；null = 还没绑定，LateUpdate 直接返回
        private SnapshotView m_view;

        // 我的玩家编号；0 = 未知，此时不认英雄也不跟随
        private long m_ownerPlayerId;

        // 快照间隔，秒；默认 1/30（服务端 30Hz）。追踪速度 = 距离 / 这个值 ⇒ 刚好在下一条快照到达时追上
        [SerializeField]
        private float m_snapshotIntervalSeconds = 1f / 30f;

        // 瞬移阈值，米；目标离当前位置超过它就当作换局 / 复活 / 传送，直接归位而不是滑过去
        [SerializeField]
        private float m_teleportDistanceMeters = 10f;

        // 敌人用的模型 prefab；为空 = 回落成胶囊（可回退，R015）
        [SerializeField]
        private GameObject m_monsterPrefab;

        // 朝向偏移，度；模型前向不是 +Z 时用它纠正（R017）
        [SerializeField]
        private float m_facingOffsetDegrees = -90f;

        // 死亡后模型保留多久再隐藏，秒；给死亡动画留时间（R020）
        private const float DeathLingerSeconds = 1.0f;

        // 受击动画在 controller 里的参数名（狼是 Take Damage）
        private const string TakeDamageParameter = "Take Damage";

        // 死亡动画参数名（狼是 Die）
        private const string DieParameter = "Die";

        // 判定「在移动」的水平距离阈值，米；停下瞬间的距离抖动靠它挡掉
        [SerializeField]
        private float m_moveAnimationThreshold = 0.05f;

        // 实例编号到槽位的复用表；稳态容量等于见过的单位数，不每帧 new
        private readonly Dictionary<int, Slot> m_slots = new Dictionary<int, Slot>();

        // 本帧的帧序号，每帧加 1；与 Slot.SeenPush 比较判断单位这一帧还在不在
        private int m_pushCount;

        // 累计推进的帧数；从 0 起单调增，界面靠它判断到底有没有在跑
        private int m_pushedFrames;

        // 累计创建过的物体数；稳态不再增长，增长说明实例编号在变
        private int m_created;

        // 跟随用的相机；null = 还没决定用哪台
        private Camera m_camera;

        // 相机是否由本类自建；true = 运行时自建，false = 场景里原有的
        private bool m_cameraCreated;

        // 本次是否让相机跟随；每次 Bind 都重算，自建的一直跟、场景原有的只在播放模式跟
        private bool m_cameraFollow;

        // 是否已绑定数据；true = m_view 非空，LateUpdate 才会工作
        public bool IsBound
        {
            get { return m_view != null; }
        }

        // 累计推进的帧数；从 0 起，每跑一次 LateUpdate 加 1
        public int PushedFrames
        {
            get { return m_pushedFrames; }
        }

        // 累计创建过的物体数；稳态不再增长，增长说明实例编号在变
        public int ObjectsCreated
        {
            get { return m_created; }
        }

        /// <summary>取场景里已有的接管组件，没有则在播放模式下运行时建一个，并绑到给定数据；view 为空或建不出来时返回 null。</summary>
        // 功能：找到或新建一个 SnapshotSceneBinder，再把它绑到 view 与 ownerPlayerId 上。
        // 入参：view 表现层那份只读数据；ownerPlayerId 我的玩家编号（0 表示还没认出来）
        // 返回：接管组件；view 为空、或非播放模式且场景里没有现成组件时返回 null
        // 步骤：
        //   1) view 为空直接返回 null，后面每一步都依赖这份数据
        //   2) 先在场景里找现成组件，复用它才不会重复建物体
        //   3) 没有就只在播放模式建：非播放模式建了也跑不到 LateUpdate，还会弄脏编辑器场景
        //   4) 建一个空物体并 AddComponent，全在内存里，不碰 .unity / .prefab / .meta
        //   5) 最后统一走 Bind，让「找到」与「新建」两条路行为一致
        //   6) 注意：本轮没把 binder 预置进场景（那要改 .unity 资产），只在运行时查找或新建；做成 Prefab 或启动链装配也还没做
        // 上游：NetDebugWindow 的按钮、RuntimeNetDriver 进房后
        // 下游：Bind
        public static SnapshotSceneBinder Ensure(SnapshotView view, long ownerPlayerId)
        {
            return Ensure(view, ownerPlayerId, null);
        }

        // 功能：绑定数据、指定敌人模型 prefab（为空则用胶囊），并把组件装到场景里。
        // 入参：view 表现层数据；ownerPlayerId 我的玩家编号；monsterPrefab 敌人模型（可为 null）
        // 返回：装好的组件；view 为空时返回 null
        // 步骤：
        //   1) view 为空直接返回 null
        //   2) 已有实例就复用它，并把 prefab 记下来（Play 中换模型要退出重进才生效）
        //   3) 没有就建一个「NBC-视图接管(运行时建)」并挂上本组件，再记 prefab
        // 上游：RuntimeNetDriver.RefreshPresentation、Editor 的 NetDebugWindow
        // 下游：Bind、LateUpdate
        // 注意：prefab 传 null 时回落胶囊 —— 这是 R015 的回退路径，别把它当「没配置就报错」
        public static SnapshotSceneBinder Ensure(SnapshotView view, long ownerPlayerId, GameObject monsterPrefab)
        {
            if (view == null)
            {
                return null;
            }

            SnapshotSceneBinder binder = FindFirstObjectByType<SnapshotSceneBinder>();

            if (binder == null)
            {
                if (!Application.isPlaying)
                {
                    return null;
                }

                var host = new GameObject("NBC-视图接管(运行时建)");
                binder = host.AddComponent<SnapshotSceneBinder>();
            }

            // 记下模型：Play 中改这个字段不会让已有槽位变模型，退出重进才生效
            binder.m_monsterPrefab = monsterPrefab;

            binder.Bind(view, ownerPlayerId);
            return binder;
        }

        // 功能：只更新「我的玩家编号」，不重算相机（每帧同步用）。
        // 入参：ownerPlayerId 我的玩家编号（0 = 还不知道）
        // 返回：无
        // 步骤：直接赋值
        // 上游：RuntimeNetDriver 每帧（握手完成前是 0，完成后才有值）
        // 下游：LateUpdate 的「本地英雄跳过」判据
        // 注意：必须每帧同步 —— 绑定只发生一次，若那时编号还是 0，英雄就永远不会被跳过（会出现「跟着自己的第二个模型」）
        public void SetOwnerPlayerId(long ownerPlayerId)
        {
            m_ownerPlayerId = ownerPlayerId;
        }
        // 功能：换一份数据与玩家编号绑定，并重算相机用哪台、跟不跟。
        // 入参：view 表现层那份只读数据；ownerPlayerId 我的玩家编号（0 表示还没认出来）
        // 返回：无
        // 步骤：
        //   1) 先记数据与玩家编号，LateUpdate 与 FollowHero 都用它
        //   2) 每次 Bind 都重算相机：只算一次的话，首次落在非播放模式就永远不跟随
        //   3) 注意：不清 m_slots，重复绑定沿用已建物体；旧数据留下的槽位由下一帧 HideGone 隐藏
        // 上游：Ensure（新建与复用都会走这里）
        // 下游：EnsureCamera
        public void Bind(SnapshotView view, long ownerPlayerId)
        {
            m_view = view;
            m_ownerPlayerId = ownerPlayerId;

            EnsureCamera();
        }

        // 功能：定下跟随用哪台相机，并算出这次要不要跟随。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 已经定过相机就只重算 m_cameraFollow：自建的永远跟，场景原有的只在播放模式跟
        //   2) 没定过就先找 Camera.main，找到就接管它
        //   3) 注意：非播放模式只接管不新建、不移动，编辑器里的相机状态属于场景资产
        //   4) 播放模式才跟随（不接管的话默认场景的相机很可能拍不到英雄）；日志点明退出 Play 一切复原，因为一个场景资产都没改
        //   5) 没有带 MainCamera 标签的相机时，非播放模式直接放弃，等下次 Bind 在 Play 里再建
        //   6) 播放模式才自建一台，挂在接管组件下面，运行时建、不落盘
        // 上游：Bind（每次绑定都重算）
        // 下游：Camera.main、Debug.Log
        private void EnsureCamera()
        {
            bool playing = Application.isPlaying;

            // 场景里已经有专门的跟随相机（FollowCamera）时，本组件不写相机：
            // 两个脚本每帧抢写会让画面抖，而且这里用的是 30Hz 快照坐标，抖得更明显。
            bool dedicatedFollow = UnityEngine.Object.FindObjectOfType<FollowCamera>() != null;

            if (m_camera != null)
            {
                m_cameraFollow = m_cameraCreated || (playing && !dedicatedFollow);
                return;
            }

            Camera existing = Camera.main;

            if (existing != null)
            {
                m_camera = existing;
                m_cameraCreated = false;

                m_cameraFollow = playing && !dedicatedFollow;

                if (m_cameraFollow)
                {
                    Debug.Log("[NBC 视图接管] 接管了已有相机（仅播放模式）：场景里原本就有一台主相机（"
                              + existing.name + "），进 Play 之后它跟着我的英雄走；退出 Play 一切复原" +
                              "（一个场景资产、一个 .meta 都没改）。");
                }

                return;
            }

            if (!playing)
            {
                m_cameraFollow = false;
                return;
            }

            var go = new GameObject("NBC-表现相机(运行时建)");
            go.transform.SetParent(transform, false);
            m_camera = go.AddComponent<Camera>();
            m_cameraCreated = true;
            m_cameraFollow = true;

            Debug.Log("[NBC 视图接管] 场景里没有主相机 ⇒ **播放模式**下自建了一台（运行时建，不落盘）。");
        }

        // 功能：每帧把快照里的每个实体朝目标位置推进（匀速追踪，不再硬跳），并驱动相机跟随。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没绑数据就什么都不做，避免空引用
        //   2) 帧序号加 1，作为本帧「还在不在」的标记
        //   3) 逐个实体取槽位并记 SeenPush；注意只有第一次见到某个 id 才建物体，稳态不分配
        //   4) 按 Alive 显隐（快照与锁步两条链路共用这个字段、语义一致）：死了就藏起来并跳过位置更新
        //   5) 活着就把毫米换成米写位置；我的英雄放大到 1.4 倍，y 取缩放的一半把胶囊抬高一些，少埋进地面
        //   6) 收尾隐藏本帧没出现的旧物体，再让相机跟随，最后累计帧数
        // 上游：Unity 每帧调用；只读 m_view，不写回世界
        // 下游：EnsureSlot、MoveSlotTo（位置追踪）、HideGone、FollowHero
        // 注意：位置不再直接赋值 —— 30Hz 快照直接赋值会「每 33ms 跳一格」，改成朝目标匀速追（R006）
        private void LateUpdate()
        {
            if (m_view == null)
            {
                return;
            }

            m_pushCount++;

            IReadOnlyList<EntitySnapshot> entities = m_view.Entities;

            for (int i = 0; i < entities.Count; i++)
            {
                EntitySnapshot e = entities[i];

                // 本地英雄最先跳过：场景里已经有真模型（R014），连槽位都不该给它建。
                // ⚠️ 必须在 EnsureSlot 之前 —— 建完再藏会白造一个物体，而且它会在 HideGone 之前闪一帧。
                bool mine = m_ownerPlayerId != 0 && e.OwnerPlayerId == m_ownerPlayerId;

                if (mine)
                {
                    continue;
                }

                Slot slot = EnsureSlot(e.EntityId);

                // 把怪型告诉调试可视化组件（决定读哪一行表、以及写回哪一行）
                MonsterAiDebugView debugView = slot.Go.GetComponent<MonsterAiDebugView>();

                if (debugView != null)
                {
                    debugView.Bind(e.ConfigId);
                }

                slot.SeenPush = m_pushCount;

                ApplyCombatFeedback(slot, e);

                // 死亡后不立刻隐藏：给死亡动画留一点时间（R020）
                bool visible = e.Alive || (slot.DiedAt > 0f && Time.time - slot.DiedAt < DeathLingerSeconds);

                if (slot.Go.activeSelf != visible)
                {
                    slot.Go.SetActive(visible);
                }

                if (!e.Alive)
                {
                    continue;
                }

                float scale = 1f;


                // 目标位置（局部坐标）；y 取槽位自己的「脚底到轴心」偏移 —— 模型与胶囊的轴心差别很大，不能写死
                Vector3 target = new Vector3(e.PosXMm * MmToUnit, 0f, e.PosZMm * MmToUnit);

                DriveAnimation(slot, e.FacingDeg, target);
                SnapModelToGroundOnce(slot);
                MoveSlotTo(slot, target);
                slot.Transform.localScale = new Vector3(scale, scale, scale);
            }

            HideGone();

            if (m_cameraFollow)
            {
                FollowHero();
            }

            m_pushedFrames++;
        }

        // 功能：把本帧没被看到的单位隐藏起来。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 遍历所有槽位，SeenPush 不等于本帧序号就是本帧没出现
        //   2) 只给还显示着的调 SetActive(false)，避免每帧重复写
        //   3) 注意：只隐藏不销毁，单位可能只是这一帧不在，留着下次复用更省
        // 功能：按快照里的血量与存活状态，驱动血条、伤害飘字、受击与死亡动画。
        // 入参：slot 槽位；entity 这一帧的快照
        // 返回：无
        // 步骤：
        //   1) 第一次见到这个单位：只记基线（血量/存活/满血血条），不飘字
        //   2) 之后掉血 ⇒ 飘一个伤害数字 + 触发受击动画
        //   3) 血条比例跟着血量走
        //   4) 刚死的这一刻 ⇒ 触发死亡动画并记下时刻（隐藏由调用方延迟处理）
        // 上游：LateUpdate 遍历实体时
        // 下游：DamagePopup.Show、Animator.SetTrigger、MonsterHealthBar.SetRatio
        // 注意：首帧必须只记基线 —— 否则单位一进场就会飘一个「满血」数字
        private void ApplyCombatFeedback(Slot slot, EntitySnapshot entity)
        {
            if (slot.HealthBar == null)
            {
                slot.HealthBar = slot.Go.AddComponent<MonsterHealthBar>();
                slot.HealthBar.Bind(entity.MaxHp);
            }

            if (!slot.HpKnown)
            {
                slot.HpKnown = true;
                slot.LastHp = entity.Hp;
                slot.AliveKnown = entity.Alive;
                return;
            }

            if (entity.Hp < slot.LastHp)
            {
                int amount = slot.LastHp - entity.Hp;
                Vector3 popupAt = slot.Transform.position + Vector3.up * 1.4f;
                DamagePopup.Show(popupAt, amount);

                if (slot.HasTakeDamage)
                {
                    slot.Animator.SetTrigger(TakeDamageParameter);
                }
            }

            slot.LastHp = entity.Hp;

            if (entity.MaxHp > 0)
            {
                slot.HealthBar.SetRatio((float)entity.Hp / entity.MaxHp);
            }

            if (slot.AliveKnown && !entity.Alive)
            {
                slot.DiedAt = Time.time;

                if (slot.HasDie)
                {
                    slot.Animator.SetTrigger(DieParameter);
                }
            }

            slot.AliveKnown = entity.Alive;
        }
        // 功能：按「到目标的水平距离」驱动走路动画，并按快照朝向转身。
        // 入参：slot 槽位；facingDeg 快照里的朝向（度）；target 本帧目标位置（局部坐标）
        // 返回：无
        // 步骤：
        //   1) 算当前到目标的水平距离（只算 X/Z），超过阈值就算「在移动」
        //   2) 有 Animator 且 controller 有 Walk(Bool) ⇒ SetBool
        //   3) 没有 Walk 参数 ⇒ 只告警一次（不同怪物参数名可能不同，不能每帧刷屏）
        //   4) 按 FacingDeg + 偏移写 localRotation
        // 上游：LateUpdate 遍历实体时
        // 下游：Animator.SetBool、Transform.localRotation
        // 注意：用「到目标的距离」判移动，不用刚体速度 —— 速度有噪声，停下瞬间会抖
        // 注意：刚体若冻结了旋转（freezeRotation），写 transform 旋转是安全的（物理不会覆盖）
        private void DriveAnimation(Slot slot, int facingDeg, Vector3 target)
        {
            Vector3 current = slot.Transform.localPosition;
            float distance = Vector3.Distance(
                new Vector3(current.x, 0f, current.z),
                new Vector3(target.x, 0f, target.z));

            bool moving = distance > m_moveAnimationThreshold;

            if (slot.Animator != null)
            {
                if (slot.HasWalkParameter)
                {
                    slot.Animator.SetBool(WalkParameter, moving);
                }
                else if (!slot.WarnedMissingWalk)
                {
                    slot.WarnedMissingWalk = true;
                    Debug.LogWarning("[视图接管] 怪物 controller 里没有 Bool 参数 " + WalkParameter +
                                     "，这只怪不会播走路动画（位置与朝向仍正常）。", slot.Go);
                }
            }

            slot.Transform.localRotation = Quaternion.Euler(0f, facingDeg + m_facingOffsetDegrees, 0f);
        }
        // 功能：按**当前动画姿势**把 Model 容器整体上移/下移，让脚底正好落在地面（只做一次）。
        // 入参：slot 槽位
        // 返回：无
        // 步骤：
        //   1) 已经做过 / 找不到 Model 容器 ⇒ 返回
        //   2) 遍历 Model 下的 SkinnedMeshRenderer 与 MeshRenderer 求包围盒
        //   3) 按 minY 反向偏移 Model 的 localPosition
        // 上游：LateUpdate
        // 下游：Transform.localPosition
        // 注意：编辑期量的是**绑定姿势**，进 Play 后 Animator 摆姿势会让脚离地，所以必须运行时再对一次
        private void SnapModelToGroundOnce(Slot slot)
        {
            if (slot.GroundSnapped || slot.Transform == null)
            {
                return;
            }

            Transform model = slot.Transform.Find("Model");

            if (model == null)
            {
                slot.GroundSnapped = true;
                return;
            }

            Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
            bool hasBounds = false;
            Bounds bounds = default(Bounds);

            for (int i = 0; i < renderers.Length; i++)
            {
                // 只算模型网格；血条那两个 SpriteRenderer 不算
                if (!(renderers[i] is SkinnedMeshRenderer) && !(renderers[i] is MeshRenderer))
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = renderers[i].bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }
            }

            if (!hasBounds)
            {
                slot.GroundSnapped = true;
                return;
            }

            float minY = bounds.min.y;

            if (Mathf.Abs(minY) > 0.01f)
            {
                Vector3 local = model.localPosition;
                model.localPosition = new Vector3(local.x, local.y - minY, local.z);
            }

            slot.GroundSnapped = true;
        }
        // 功能：把槽位朝目标位置推进 —— 有刚体走物理速度，无刚体直接写位置；必要时直接归位。
        // 入参：slot 槽位；target 本帧快照给的目标位置（局部坐标，米，只用 X/Z）
        // 返回：无
        // 步骤：
        //   1) 动态刚体 ⇒ 交给 MoveBodyTowards（水平用 velocity，垂直给重力）
        //   2) 无刚体（胶囊兜底）⇒ 直接写 localPosition：首次落位 / 超距瞬移 / 按「距离 ÷ 快照间隔」推进
        // 上游：LateUpdate 遍历实体时
        // 下游：MoveBodyTowards、Transform.localPosition
        // 注意：有刚体时**绝对不能**再写 localPosition —— 物理在 FixedUpdate 接管变换，写了会被拉回，表现是「一顿一顿」
        private void MoveSlotTo(Slot slot, Vector3 target)
        {
            Vector3 current = slot.Transform.localPosition;

            if (slot.Body != null && !slot.Body.isKinematic)
            {
                MoveBodyTowards(slot, current, target);
                return;
            }

            Vector3 flatTarget = new Vector3(target.x, HalfHeight, target.z);

            if (!slot.Positioned)
            {
                slot.Positioned = true;
                slot.Transform.localPosition = flatTarget;
                return;
            }

            float distance = Vector3.Distance(new Vector3(current.x, 0f, current.z), new Vector3(flatTarget.x, 0f, flatTarget.z));

            if (distance >= m_teleportDistanceMeters || distance <= 0.0001f)
            {
                slot.Transform.localPosition = flatTarget;
                return;
            }

            float interval = m_snapshotIntervalSeconds > 0.001f ? m_snapshotIntervalSeconds : 0.001f;
            float speed = Mathf.Min(distance / interval, MaxChaseSpeed);
            Vector3 moved = Vector3.MoveTowards(
                new Vector3(current.x, 0f, current.z),
                new Vector3(flatTarget.x, 0f, flatTarget.z),
                speed * Time.deltaTime);

            slot.Transform.localPosition = new Vector3(moved.x, HalfHeight, moved.z);
        }

        // 功能：用刚体速度把实体朝目标推过去（水平），垂直留给重力。
        // 入参：slot 槽位（Body 非空且非运动学）；current 当前局部位置；target 目标局部位置（只用 X/Z）
        // 返回：无
        // 步骤：
        //   1) 首次落位 / 超距瞬移 ⇒ 直接设位置并把水平速度清零（不能让它滑过去）
        //   2) 到点（2 厘米内）⇒ 水平速度清零，避免原地抖
        //   3) 否则速度 = 方向 × min(距离 ÷ 快照间隔, 上限)，保留原有 Y 速度（重力算出来的）
        // 上游：MoveSlotTo
        // 下游：Rigidbody.velocity、Transform.localPosition
        // 注意：速度要转到**世界空间**再给刚体（领域数据是父节点的局部坐标，父节点带旋转时不能直接用）
        private void MoveBodyTowards(Slot slot, Vector3 current, Vector3 target)
        {
            Vector3 currentXZ = new Vector3(current.x, 0f, current.z);
            Vector3 targetXZ = new Vector3(target.x, 0f, target.z);
            float distance = Vector3.Distance(currentXZ, targetXZ);

            if (!slot.Positioned || distance >= m_teleportDistanceMeters)
            {
                slot.Positioned = true;
                slot.Transform.localPosition = new Vector3(target.x, current.y, target.z);
                slot.Body.velocity = new Vector3(0f, slot.Body.velocity.y, 0f);
                return;
            }

            if (distance <= 0.02f)
            {
                slot.Body.velocity = new Vector3(0f, slot.Body.velocity.y, 0f);
                return;
            }

            float interval = m_snapshotIntervalSeconds > 0.001f ? m_snapshotIntervalSeconds : 0.001f;
            float speed = Mathf.Min(distance / interval, MaxChaseSpeed);
            Vector3 localVelocity = (targetXZ - currentXZ) / distance * speed;
            Transform parent = slot.Transform.parent;
            Vector3 worldVelocity = parent != null ? parent.TransformDirection(localVelocity) : localVelocity;

            slot.Body.velocity = new Vector3(worldVelocity.x, slot.Body.velocity.y, worldVelocity.z);
        }

        // 功能：把这一帧没出现的槽位隐藏起来。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 遍历所有槽位，SeenPush 小于当前帧序号 ⇒ 这一帧没出现
        //   2) 只给还显示着的调 SetActive(false)，避免每帧重复写
        //   3) 注意：只隐藏不销毁，单位可能只是这一帧不在，留着下次复用更省        // 上游：LateUpdate 每帧末尾
        // 下游：GameObject.SetActive
        private void HideGone()
        {
            foreach (KeyValuePair<int, Slot> pair in m_slots)
            {
                Slot slot = pair.Value;

                if (slot.SeenPush != m_pushCount && slot.Go.activeSelf)
                {
                    slot.Go.SetActive(false);
                }
            }
        }

        // 功能：让相机每帧看向我的英雄。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 用 ownerPlayerId 找我的英雄，认人靠 owner_player_id 那个字段
        //   2) 找不到就返回：英雄死了或还没出现，镜头保持不动，免得乱晃
        //   3) 英雄位置从毫米换成米，y 取 0 作为注视点
        //   4) 相机放在英雄后上方（高 6 米、后 8 米），再 LookAt 注视点
        // 上游：LateUpdate 里 m_cameraFollow 为 true 时
        // 下游：SnapshotView.FindHero、Transform.LookAt
        private void FollowHero()
        {
            EntitySnapshot hero = m_view.FindHero(m_ownerPlayerId);

            if (hero == null)
            {
                return;
            }

            Vector3 at = new Vector3(hero.PosXMm * MmToUnit, 0f, hero.PosZMm * MmToUnit);

            m_camera.transform.position = new Vector3(at.x, at.y + 6f, at.z - 8f);
            m_camera.transform.LookAt(at);
        }

        // 功能：按实体编号取槽位，没有就新建一个胶囊体。
        // 入参：entityId 实体编号（协议里的实例编号）
        // 返回：该编号对应的槽位；已存在则直接复用
        // 步骤：
        //   1) 先查字典，命中就返回，稳态下这里不分配
        //   2) 没命中才 CreatePrimitive(Capsule)，并挂到本组件下面
        //   3) 包成 Slot 放进字典、累计创建数；只有第一次见到这个 id 才会走到这里
        // 上游：LateUpdate 遍历实体时
        // 下游：Slot 构造函数
        private Slot EnsureSlot(int entityId)
        {
            Slot slot;

            if (m_slots.TryGetValue(entityId, out slot))
            {
                return slot;
            }

            GameObject go;

            if (m_monsterPrefab != null)
            {
                // 有模型就用模型（R015）；局部位置先归零，位置随后由 MoveSlotTo 接管
                go = Instantiate(m_monsterPrefab, transform, false);
                go.name = "单位 " + entityId;
            }
            else
            {
                // 没配置模型就回落成胶囊 —— 这是可回退路径，不是错误
                go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name = "单位 " + entityId;
                go.transform.SetParent(transform, false);
            }

            slot = new Slot(go);

            // 记下实体编号：剑的碰撞盒 Overlap 到它时能反查是谁（R019）
            MonsterSlotTag tag = go.GetComponent<MonsterSlotTag>();

            if (tag == null)
            {
                tag = go.AddComponent<MonsterSlotTag>();
            }

            tag.EntityId = entityId;

            if (slot.Body != null)
            {
                // 刚体默认只按物理步（50Hz）更新视觉，不开插值的话 60fps 下会看到「一跳一跳」
                slot.Body.interpolation = RigidbodyInterpolation.Interpolate;
            }

            if (slot.Animator != null)
            {
                // 一定要关：狼的 controller 里有「With Root Motion」那套状态，开着会与 velocity 叠加把模型推飞
                slot.Animator.applyRootMotion = false;
            }

            m_slots.Add(entityId, slot);
            m_created++;
            return slot;
        }

        /// <summary>给调试窗一句人话，报告绑定状态、物体数、帧数与相机状态。</summary>
        // 功能：拼一句给调试窗看的状态摘要。
        // 入参：无
        // 返回：形如「已接 4 个物体（建 4 次）、推 120 帧、相机 …」的字符串
        // 步骤：
        //   1) 先按是否绑定选开头，再拼物体数、创建次数、帧数
        //   2) 相机那一段交给 DescribeCamera，保证显示与判定同源
        // 上游：NetDebugWindow
        // 下游：IsBound、DescribeCamera
        public string Describe()
        {
            return (IsBound ? "已接 " : "**未绑定** ") + m_slots.Count + " 个物体（建 " + m_created +
                   " 次）、推 " + m_pushedFrames + " 帧、相机 " + DescribeCamera();
        }

        // 功能：把相机状态说成一句人话。
        // 入参：无
        // 返回：无 / 自建 / 接管 / 沿用 四类文案之一
        // 步骤：
        //   1) 没有相机时，播放模式答「无」，非播放模式注明「不建相机」，否则会被当成坏了
        //   2) 自建的相机一定跟随
        //   3) 场景原有的相机按 m_cameraFollow 区分接管（Play）与沿用（非 Play）
        //   4) 注意：返回的字符串直接显示在调试窗里，改字等于改窗口文案
        // 上游：Describe
        // 下游：Application.isPlaying
        private string DescribeCamera()
        {
            if (m_camera == null)
            {
                return Application.isPlaying ? "无" : "无（非播放模式不建相机）";
            }

            if (m_cameraCreated)
            {
                return "自建 1 台（跟随我的英雄）";
            }

            return m_cameraFollow
                ? "接管场景里已有那台（仅播放模式，跟随我的英雄）"
                : "沿用场景里已有那台（非播放模式，不动它）";
        }

        // 主要功能：保存一个单位在场景里的物体与 Transform，并记录最后一次被看到的帧号。
        // 主要数据成员：Go（物体）、Transform（缓存）、SeenPush（最后被看到的帧号，与本帧序号比较）
        // 主要外部方法：构造函数 Slot(GameObject)，只缓存物体与 Transform
        // 谁在用它：SnapshotSceneBinder 的 m_slots；只在 LateUpdate / HideGone / EnsureSlot 内部使用
        private sealed class Slot
        {
            // 单位对应的场景物体（胶囊体），由 EnsureSlot 创建
            public readonly GameObject Go;

            // Go 的 Transform 缓存；每帧都要写位置，缓存一份省一次组件查找
            public readonly Transform Transform;

            // 最后一次被看到的帧序号；小于当前 m_pushCount 即本帧没出现
            public int SeenPush;

            // 是否已经落过位；首次出现要直接归位，不能从原点滑进来
            public bool Positioned;

            // 是否已经按**动画姿势**把模型对齐到地面（只做一次）
            public bool GroundSnapped;

            // 模型自带的刚体；非空表示 Y 由物理（重力）负责，本组件只写 X/Z
            public readonly Rigidbody Body;

            // 子物体上的 Animator；为空表示这个槽位没有动画
            public readonly Animator Animator;

            // controller 里有没有 Walk(Bool) 参数；不同怪物参数名可能不同，先查再设
            public readonly bool HasWalkParameter;

            // 是否已经为「缺 Walk 参数」告过警；只报一次，避免每帧刷屏
            public bool WarnedMissingWalk;

            // controller 里有没有受击/死亡触发参数（R020）
            public readonly bool HasTakeDamage;

            // controller 里有没有死亡触发参数（R020）
            public readonly bool HasDie;

            // 头顶血条；懒创建（满血值要等第一帧快照才知道）
            public MonsterHealthBar HealthBar;

            // 上一帧的血量；HpKnown 为 false 时只看不比较（新单位首帧不能飘满血数字）
            public int LastHp;

            // 是否已经有血量基线
            public bool HpKnown;

            // 上一帧是否活着；用来识别「刚死」这一刻
            public bool AliveKnown;

            // 死亡的时刻（Time.time）；0 = 还没死
            public float DiedAt;

            // 功能：缓存物体与它的 Transform。
            // 入参：go 这个单位对应的场景物体
            // 返回：无（构造）
            // 步骤：
            //   1) 记下物体，隐藏与显示都靠它
            //   2) 顺手取一次 Transform 存起来，LateUpdate 每帧都要用
            // 上游：EnsureSlot
            // 下游：无
            public Slot(GameObject go)
            {
                Go = go;
                Transform = go.transform;
                Body = go.GetComponent<Rigidbody>();
                Animator = go.GetComponentInChildren<Animator>(true);
                HasWalkParameter = HasBoolParameter(Animator, WalkParameter);
                HasTakeDamage = HasParameter(Animator, TakeDamageParameter, AnimatorControllerParameterType.Trigger);
                HasDie = HasParameter(Animator, DieParameter, AnimatorControllerParameterType.Trigger);
            }

            // 功能：判断 Animator 上有没有指定名字的 Bool 参数。
            // 入参：animator 目标动画器（可为空）；parameter 参数名
            // 返回：true = 存在且是 Bool
            // 步骤：空就返回 false；否则遍历 parameters 比对名字与类型
            // 上游：Slot 构造函数
            // 下游：LateUpdate 的 SetBool
            private static bool HasBoolParameter(Animator animator, string parameter)
            {
                return HasParameter(animator, parameter, AnimatorControllerParameterType.Bool);
            }

            // 功能：判断 Animator 上有没有指定名字与类型的参数。
            // 入参：animator 目标动画器（可为空）；parameter 参数名；type 期望类型
            // 返回：true = 存在且类型一致
            // 步骤：空就返回 false；否则遍历参数表比对名字与类型
            // 上游：Slot 构造函数（Walk / Take Damage / Die）
            // 下游：LateUpdate 的 SetBool / SetTrigger
            private static bool HasParameter(Animator animator, string parameter, AnimatorControllerParameterType type)
            {
                if (animator == null)
                {
                    return false;
                }

                AnimatorControllerParameter[] parameters = animator.parameters;

                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].name == parameter && parameters[i].type == type)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
