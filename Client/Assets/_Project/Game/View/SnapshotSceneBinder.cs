// ============================================================================
//  SnapshotSceneBinder —— 「一条最小视图接管」：把 `SnapshotView` 分发到 3D 物体
//  项目：3D联网战斗Demo   对应：`Docs\27` §三十五 的 35.5
//
//  ---------------------------------------------------------------------------
//  一、它为什么长这样（**如实交代边界**）
//  ---------------------------------------------------------------------------
//  ⭐ 复用的是**状态同步链那一层**：`Game\Net\SnapshotView.cs`（客户端唯一的世界数据副本）。
//     它自己就写着「**不建场景物体**：把"数据"变成"GameObject"是表现层的事」——
//     本类就是那个"表现层的事"，**没有另起一套数据/渲染管线**，只把同一份数据搬到 Transform 上。
//
//  ⚠️ 本轮**不许开 Unity、不许碰 `.unity` / `.prefab` / `.meta`**（硬约束）——
//     而"把组件挂进场景"必须改场景资产。所以这里走的是**代码接管**：
//     组件由**运行时** `AddComponent` 建出来（调试窗里点一个按钮，只在播放模式生效），
//     **一个 Unity 资产都没动**。
//     ⇒ **还差的一步**：把它预置进场景（或做成 Prefab / 由启动链装配）—— 那需要改 `.unity` 资产，
//       属于"要你来做的那一步"，写在 `Docs\27` §35.5 的「还差哪一步」里。
//
//  ---------------------------------------------------------------------------
//  二、它**只做三件事**（多一件都说明它抄了逻辑层的活）
//  ---------------------------------------------------------------------------
//      ① 每个单位一个 Unity 自带几何体（Capsule），**按 id 复用**、不每帧 new；
//      ② 把 `EntitySnapshot.PosXMm / PosZMm`（**毫米**）换成 Transform 的米（1/1000）；
//      ③ 死掉的单位隐藏（`Alive == false` ⇒ `SetActive(false)`）。
//
//  ⚠️ **只读** `SnapshotView`：不改它的任何字段、不 `Apply`、不写回世界。
//  ⚠️ **每帧不分配**：`Dictionary` 查表 + 复用 `Slot`；只有"第一次见到的单位"才新建物体。
//  ⚠️ 相机（本轮修正）：场景里**一台都没有** ⇒ 自建一台；场景里**已经有主相机** ⇒
//     **播放模式下接管它**（跟着我的英雄走 —— 否则默认场景的相机很可能根本拍不到英雄，
//     负责人点 Play 后"什么都看不见"）。Play 里的改动**不落盘**（不改 `.unity`/`.meta`，退出即复原）；
//     ⚠️ **非播放模式一台相机都不建、都不动**（编辑器里的相机状态是资产的一部分 ——
//        上一版"自建"那条分支没有 `Application.isPlaying` 判据，将来把 binder 预置进场景时，
//        编辑模式下一次 `Bind` 就会 `new GameObject` + `AddComponent<Camera>` 弄脏场景）。
//     ⚠️ "跟不跟"**每次 `Bind` 都重算**：上一版只算一次 ⇒ 若第一次 `Bind` 发生在非播放模式，
//        进 Play 之后 `m_cameraFollow` 永远是 false ⇒ 永不跟随（点了 Play 还是看不见人）。
//     认人靠 `SnapshotView.FindHero(ownerPlayerId)` —— 也就是 `owner_player_id` 那个字段（M4-S1b）。
// ============================================================================

using System.Collections.Generic;
using NBC.Game.Net;
using NBC.Protocol;
using UnityEngine;

namespace NBC.Game.View
{
    /// <summary>最小视图接管：`SnapshotView` ⇒ 一堆几何体（**只读数据、不碰逻辑**）。</summary>
    [DisallowMultipleComponent]
    public sealed class SnapshotSceneBinder : MonoBehaviour
    {
        /// <summary>英雄比其它单位大多少（没有材质/贴图，只能靠大小把"我"分开）。</summary>
        private const float HeroScale = 1.4f;

        /// <summary>毫米 ⇒ 米（表现层**唯一**一次单位换算；数据层全是整数毫米）。</summary>
        private const float MmToUnit = 0.001f;

        /// <summary>表现层读的那一份（**只读**）。</summary>
        private SnapshotView m_view;

        /// <summary>「我」的玩家编号（`owner_player_id` 靠它认人）。</summary>
        private long m_ownerPlayerId;

        /// <summary>实例编号 ⇒ 物体（**复用**；不每帧 new）。</summary>
        private readonly Dictionary<int, Slot> m_slots = new Dictionary<int, Slot>();

        /// <summary>本帧的序号（给 `Slot.SeenPush` 用：没被看到的就是"消失了"）。</summary>
        private int m_pushCount;

        /// <summary>一共推了多少帧（界面看得见"它到底有没有在跑"）。</summary>
        private int m_pushedFrames;

        /// <summary>一共建过多少个物体。</summary>
        private int m_created;

        /// <summary>跟上我的那个相机（null = 还没决定用哪台）。</summary>
        private Camera m_camera;

        /// <summary>相机是不是**我建的**（自建的那台当然跟着走；场景里原有的只有播放模式才接管）。</summary>
        private bool m_cameraCreated;

        /// <summary>
        /// 这台相机**归我跟随**了吗。
        /// <para>⚠️ 自建的那台：一直是 true。</para>
        /// <para>⚠️ 场景里**已有**的主相机：只有**播放模式**才 true（进 Play 接管 ⇒ 跟着英雄走；
        /// 退出 Play 一切复原，因为一个场景资产都没改）。**非播放模式一台相机都不动。**</para>
        /// </summary>
        private bool m_cameraFollow;

        /// <summary>已经接上了吗（没接上 ⇒ `LateUpdate` 什么都不做，不报错）。</summary>
        public bool IsBound
        {
            get { return m_view != null; }
        }

        /// <summary>推过多少帧。</summary>
        public int PushedFrames
        {
            get { return m_pushedFrames; }
        }

        /// <summary>建过多少个物体（稳态不再涨 —— 涨了说明 id 在变）。</summary>
        public int ObjectsCreated
        {
            get { return m_created; }
        }

        /// <summary>
        /// 找到场景里已有的那个接管组件；没有就**在运行时建一个**（播放模式才行）。
        /// <para>⚠️ 这就是"代码接管、不动场景资产"的落点：一个 `GameObject` + 一个组件，全在内存里。</para>
        /// </summary>
        /// <param name="view">表现层那份数据（`PresentationFeed.View`）。</param>
        /// <param name="ownerPlayerId">我的玩家编号。</param>
        /// <returns>接管组件（建不出来返回 null）。</returns>
        public static SnapshotSceneBinder Ensure(SnapshotView view, long ownerPlayerId)
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
                    return null;        // 编辑器里不建（建了也不会跑到 `LateUpdate`，只会让人误会）
                }

                var host = new GameObject("NBC-视图接管(运行时建)");
                binder = host.AddComponent<SnapshotSceneBinder>();
            }

            binder.Bind(view, ownerPlayerId);
            return binder;
        }

        /// <summary>绑到一份数据上（重复调用 = 换绑定，不重复建物体）。</summary>
        /// <param name="view">表现层那份数据（**只读**）。</param>
        /// <param name="ownerPlayerId">我的玩家编号。</param>
        public void Bind(SnapshotView view, long ownerPlayerId)
        {
            m_view = view;
            m_ownerPlayerId = ownerPlayerId;

            // ⚠️ 相机"跟不跟"**每次 Bind 都重算**（不是只算一次）：
            //    上一版只在第一次 Bind 时算 ⇒ 那次若在非播放模式，进 Play 之后永远不跟随。
            EnsureCamera();
        }

        /// <summary>
        /// 决定"用哪台相机、跟不跟"（**非播放模式一台都不建、都不动**）。
        /// <para>⚠️ 两条硬约束：</para>
        /// <list type="bullet">
        /// <item>**非播放模式**：场景里没有相机 ⇒ **什么都不建**（`new GameObject` / `AddComponent` 会弄脏编辑器场景）。</item>
        /// <item>**每次 Bind 都重算 `m_cameraFollow`**：自建的那台一直跟；场景里那台只在播放模式跟。</item>
        /// </list>
        /// </summary>
        private void EnsureCamera()
        {
            bool playing = Application.isPlaying;

            if (m_camera != null)
            {
                // 已经定下用哪台了 ⇒ 只重算"跟不跟"（自建的永远跟；场景里原有的只在播放模式跟）
                m_cameraFollow = m_cameraCreated || playing;
                return;
            }

            Camera existing = Camera.main;

            if (existing != null)
            {
                m_camera = existing;
                m_cameraCreated = false;

                // ⭐ 场景里**已经有**主相机 ⇒ **播放模式下接管它**（跟着我的英雄走）。
                //    为什么这条是安全的：Play 里的改动**不落盘** —— 不改 `.unity`、不碰 `.meta`、
                //    退出 Play 就复原；反过来，不接管的话默认场景的主相机很可能**根本看不到英雄**
                //    （负责人点 Play 后"什么东西都没有"）。
                //    ⚠️ **非播放模式一台相机都不动**（编辑器里的相机状态是资产的一部分）。
                m_cameraFollow = playing;

                if (m_cameraFollow)
                {
                    Debug.Log("[NBC 视图接管] 接管了已有相机（仅播放模式）：场景里原本就有一台主相机（"
                              + existing.name + "），进 Play 之后它跟着我的英雄走；退出 Play 一切复原" +
                              "（一个场景资产、一个 .meta 都没改）。");
                }

                return;
            }

            // 场景里一台相机都没有（或用都不是 MainCamera tag）
            if (!playing)
            {
                // ⚠️ **非播放模式不建任何东西**：编辑器里建出来的物体属于场景，会弄脏资产。
                //    等进 Play 之后下一次 `Bind` 再建（`Bind` 每次都会走到这里）。
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

        /// <summary>每帧把数据分发到物体（**只读** `m_view`；不 new 任何东西，除非出现新单位）。</summary>
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
                Slot slot = EnsureSlot(e.EntityId);

                slot.SeenPush = m_pushCount;

                // 死了就藏起来（快照/锁步两边都是 `Alive` 字段，语义一致）
                if (slot.Go.activeSelf != e.Alive)
                {
                    slot.Go.SetActive(e.Alive);
                }

                if (!e.Alive)
                {
                    continue;
                }

                bool mine = m_ownerPlayerId != 0 && e.OwnerPlayerId == m_ownerPlayerId;
                float scale = mine ? HeroScale : 1f;

                slot.Transform.localPosition = new Vector3(e.PosXMm * MmToUnit, scale * 0.5f, e.PosZMm * MmToUnit);
                slot.Transform.localScale = new Vector3(scale, scale, scale);
            }

            HideGone();

            if (m_cameraFollow)
            {
                FollowHero();
            }

            m_pushedFrames++;
        }

        /// <summary>这一帧没出现过的单位 ⇒ 藏起来（**不销毁**：它可能只是这一帧不在，复用更省）。</summary>
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

        /// <summary>相机跟"我的英雄"（靠 `owner_player_id` 认人，见 `SnapshotView.FindHero`）。</summary>
        private void FollowHero()
        {
            EntitySnapshot hero = m_view.FindHero(m_ownerPlayerId);

            if (hero == null)
            {
                return;     // 死了/还没我的英雄 ⇒ 镜头不动（别乱晃）
            }

            Vector3 at = new Vector3(hero.PosXMm * MmToUnit, 0f, hero.PosZMm * MmToUnit);

            m_camera.transform.position = new Vector3(at.x, at.y + 6f, at.z - 8f);
            m_camera.transform.LookAt(at);
        }

        /// <summary>按实体编号取（没有就建一个几何体 —— **只有第一次**会分配）。</summary>
        /// <param name="entityId">实例编号。</param>
        /// <returns>槽位。</returns>
        private Slot EnsureSlot(int entityId)
        {
            Slot slot;

            if (m_slots.TryGetValue(entityId, out slot))
            {
                return slot;
            }

            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "单位 " + entityId;
            go.transform.SetParent(transform, false);

            slot = new Slot(go);
            m_slots.Add(entityId, slot);
            m_created++;
            return slot;
        }

        /// <summary>一句人话（窗口里显示"它到底接上了没有、推了几帧、建了几个物体"）。</summary>
        /// <returns>例：`已接 4 个物体（建 4 次）、推 120 帧、相机 自建 1 台（跟随我的英雄）`。</returns>
        public string Describe()
        {
            return (IsBound ? "已接 " : "**未绑定** ") + m_slots.Count + " 个物体（建 " + m_created +
                   " 次）、推 " + m_pushedFrames + " 帧、相机 " + DescribeCamera();
        }

        /// <summary>相机那一句（⚠️ 窗口里的字就是这里的字 —— 改这里等于改字段值）。</summary>
        /// <returns>例：`接管场景里已有那台（仅播放模式，跟随我的英雄）`。</returns>
        private string DescribeCamera()
        {
            if (m_camera == null)
            {
                // ⚠️ 非播放模式**一台都不建** —— 这一句要说清"不是坏了，是编辑器里刻意不建"
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

        /// <summary>一个单位在场景里的替身（**复用**，不每帧新建）。</summary>
        private sealed class Slot
        {
            /// <summary>物体。</summary>
            public readonly GameObject Go;

            /// <summary>它的 Transform（每帧都要写位置 ⇒ 缓存一份，省一次组件查找）。</summary>
            public readonly Transform Transform;

            /// <summary>最后一次被看到的帧序号（与 `m_pushCount` 比 ⇒ 判"这一帧还在不在"）。</summary>
            public int SeenPush;

            /// <summary>建一个槽位。</summary>
            /// <param name="go">物体。</param>
            public Slot(GameObject go)
            {
                Go = go;
                Transform = go.transform;
            }
        }
    }
}
