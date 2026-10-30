// 这个文件里有：
//   SnapshotSceneBinder（类）：最小视图接管，把 SnapshotView 的实体快照分发成场景几何体，并让相机跟随我的英雄
//   Slot（类）：一个单位在场景里的物体替身，缓存物体与 Transform，并记录最后被看到的帧号

using System.Collections.Generic;
using NBC.Game.Net;
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

        // 表现层读的那一份数据；null = 还没绑定，LateUpdate 直接返回
        private SnapshotView m_view;

        // 我的玩家编号；0 = 未知，此时不认英雄也不跟随
        private long m_ownerPlayerId;

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

            binder.Bind(view, ownerPlayerId);
            return binder;
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

            if (m_camera != null)
            {
                m_cameraFollow = m_cameraCreated || playing;
                return;
            }

            Camera existing = Camera.main;

            if (existing != null)
            {
                m_camera = existing;
                m_cameraCreated = false;

                m_cameraFollow = playing;

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

        // 功能：每帧把快照里的每个实体搬到对应几何体上，并驱动相机跟随。
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
        // 下游：EnsureSlot、HideGone、FollowHero
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

        // 功能：把本帧没被看到的单位隐藏起来。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 遍历所有槽位，SeenPush 不等于本帧序号就是本帧没出现
        //   2) 只给还显示着的调 SetActive(false)，避免每帧重复写
        //   3) 注意：只隐藏不销毁，单位可能只是这一帧不在，留着下次复用更省
        // 上游：LateUpdate 每帧末尾
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

            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "单位 " + entityId;
            go.transform.SetParent(transform, false);

            slot = new Slot(go);
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
            }
        }
    }
}
