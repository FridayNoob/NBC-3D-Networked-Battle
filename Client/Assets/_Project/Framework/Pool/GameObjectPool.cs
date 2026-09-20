// ============================================================================
//  NBC.Framework · GameObject 专用池
//  替代：唐老师框架 Pool/PoolMgr.cs 的 PoolMgr（原 74 行）
//  缺陷编号：FW-04（O(n) 取出）、FW-05（无上限）、FW-09 关联（音频"回池"要用它）
//            + 审计新增 P-04（Clear 不销毁对象，池内对象成场景孤儿）
//            + 审计新增 P-10（新建的对象没有挂到池根下）
//  完整记录：Docs/06-框架改造记录.md §三、§四
//
//  ---------------------------------------------------------------------------
//  一、它比 ObjectPool<GameObject> 多做了什么
//  ---------------------------------------------------------------------------
//  纯 C# 池只管"复用引用"，而 GameObject 还要管三件只有 Unity 才有的事：
//    ① **激活状态**：闲置时必须 SetActive(false)（否则还在跑 Update、还在渲染）
//    ② **父子关系**：闲置时挂到池根下，取出时挂到使用者给的父节点下
//       —— 原版只在**回收**时改父（`PoolMgr.cs:100-114`），
//          新建补货的对象（`PoolMgr.cs:85-89`）压根没设父，直接丢在场景根（P-10）
//    ③ **销毁方式**：编辑器模式下必须用 DestroyImmediate，播放模式下用 Destroy
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 池根的生死，是这类池最容易踩的坑
//  ---------------------------------------------------------------------------
//  池默认会自建一个名为 `Pool_<池名>` 的根节点。**它不在场景切换时保留**（没有
//  DontDestroyOnLoad）—— 这是刻意的：池里的对象本来就属于当前场景，跟着场景一起释放才对。
//
//  但由此带来一个必须防住的情况：**场景卸载后，池的 Stack 里会留下一堆已销毁的对象**。
//  直接取出来用就会炸在离原因很远的地方。所以本类给 `ObjectPool<GameObject>` 传了
//  一个有效性校验（`go != null`，利用 Unity 的伪 null），取用时会把这类残留挑出来丢掉。
//
//  如果某个池确实需要跨场景（例如全局的特效池），请自己创建一个
//  `DontDestroyOnLoad` 的父节点并通过构造参数传进来。
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NBC.Framework
{
    /// <summary>
    /// GameObject 对象池：在 <see cref="ObjectPool{T}"/> 之上补上激活状态、父子关系与销毁方式。
    /// </summary>
    public sealed class GameObjectPool : IPoolStatsSource, IDisposable
    {
        /// <summary>真正干活的那个泛型池。</summary>
        private readonly ObjectPool<GameObject> m_pool;

        /// <summary>使用者提供的工厂（只负责"造一个新的出来"）。</summary>
        private readonly Func<GameObject> m_factory;

        /// <summary>使用者指定的池根；为 null 时自建。</summary>
        private readonly Transform m_providedRoot;

        /// <summary>池名。</summary>
        private readonly string m_name;

        /// <summary>实际使用的池根（自建时延迟创建）。</summary>
        private Transform m_root;

        private bool m_disposed;

        /// <summary>
        /// 构造一个 GameObject 池。
        /// </summary>
        /// <param name="name">池名，用于统计面板区分；同一个场景内应唯一。</param>
        /// <param name="factory">创建新 GameObject 的工厂，不允许为 null。</param>
        /// <param name="maxSize">闲置容量上限；小于等于 0 表示不限制。</param>
        /// <param name="initialSize">预热数量；0 表示不预热。</param>
        /// <param name="root">池根节点；传 null 则自建一个 `Pool_&lt;池名&gt;`。</param>
        public GameObjectPool(string name, Func<GameObject> factory, int maxSize = 0,
                              int initialSize = 0, Transform root = null)
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            m_name = string.IsNullOrEmpty(name) ? "GameObjectPool" : name;
            m_factory = factory;
            m_providedRoot = root;

            // 注意赋值顺序：ObjectPool 的构造函数里可能立刻触发预热，
            // 而预热会调用 CreateIdleObject()，那里要读 m_factory / m_name / m_providedRoot。
            // 所以这几个字段必须**在创建 ObjectPool 之前**就绪。
            m_pool = new ObjectPool<GameObject>(
                m_name,
                CreateIdleObject,
                maxSize,
                initialSize,
                DestroyObject,
                IsAlive);
        }

        /// <summary>池名。</summary>
        public string PoolName
        {
            get { return m_name; }
        }

        /// <summary>池根节点。会用的时候才创建，避免"建了个空池也留下一个节点"。</summary>
        public Transform Root
        {
            get
            {
                if (m_root == null)
                {
                    if (m_providedRoot != null)
                    {
                        m_root = m_providedRoot;
                    }
                    else
                    {
                        GameObject rootGo = new GameObject("Pool_" + m_name);
                        m_root = rootGo.transform;
                    }
                }

                return m_root;
            }
        }

        /// <summary>当前闲置对象数。</summary>
        public int CountInPool
        {
            get { return m_pool.CountInPool; }
        }

        /// <summary>当前在外对象数。</summary>
        public int CountInUse
        {
            get { return m_pool.CountInUse; }
        }

        /// <summary>
        /// 取出一个对象：激活并挂到指定父节点下。
        /// </summary>
        /// <param name="parent">取出后的父节点；null 表示放在场景根。</param>
        /// <returns>可用的 GameObject，不会是 null。</returns>
        public GameObject Spawn(Transform parent = null)
        {
            ThrowIfDisposed();

            GameObject go = m_pool.Get();

            go.transform.SetParent(parent, false);
            go.SetActive(true);
            return go;
        }

        /// <summary>
        /// 归还一个对象：失活并挂回池根下。
        /// </summary>
        /// <param name="go">要归还的对象；null（含已销毁）会被忽略。</param>
        public void Despawn(GameObject go)
        {
            ThrowIfDisposed();

            // Unity 伪 null：已销毁的对象在这里也会被识别成 null
            if (go == null)
            {
                return;
            }

            go.SetActive(false);

            // P-10 的修法：**任何**进池的对象都要挂到池根下，
            // 而不是只有"回收的"才挂 —— 否则新建补货的对象会散落在场景根。
            Transform root = Root;
            if (root != null)
            {
                go.transform.SetParent(root, false);
            }

            m_pool.Release(go);
        }

        /// <summary>
        /// 把所有**在外**的对象全部收回池中。
        /// 用途：战斗结束、切场景前统一收尾。
        /// 原版没有这个能力（也就无从"清点"），见审计 P-04。
        /// </summary>
        public void DespawnAll()
        {
            ThrowIfDisposed();

            List<GameObject> buffer = new List<GameObject>();
            m_pool.CopyInUseTo(buffer);

            for (int i = 0; i < buffer.Count; i++)
            {
                Despawn(buffer[i]);
            }
        }

        /// <summary>
        /// 清理池中闲置的对象（真的销毁）。
        /// 需要连在外的对象一起处理时，先 <see cref="DespawnAll"/> 再 <see cref="Clear"/>。
        /// </summary>
        public void Clear()
        {
            m_pool.Clear(true);
        }

        /// <summary>取统计快照。</summary>
        public PoolStats GetStats()
        {
            return m_pool.GetStats();
        }

        /// <summary>
        /// 释放池：清掉闲置对象、注销统计登记，并销毁自建的池根节点。
        /// </summary>
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;
            m_pool.Dispose();

            // 自建的池根由自己负责销毁；使用者传进来的根不归池管
            if (m_providedRoot == null && m_root != null)
            {
                DestroyObject(m_root.gameObject);
                m_root = null;
            }
        }

        /// <summary>
        /// 给 ObjectPool 用的工厂：造一个出来，并立刻置为"闲置"状态。
        /// 这样预热出来的对象天然就是失活 + 挂在池根下的。
        /// </summary>
        private GameObject CreateIdleObject()
        {
            GameObject go = m_factory();
            if (go == null)
            {
                return null;
            }

            go.SetActive(false);

            Transform root = Root;
            if (root != null)
            {
                go.transform.SetParent(root, false);
            }

            return go;
        }

        /// <summary>有效性校验：利用 Unity 的伪 null 识别"已被销毁"的残留对象。</summary>
        private static bool IsAlive(GameObject go)
        {
            return go != null;
        }

        /// <summary>
        /// 销毁一个 GameObject。
        /// 编辑器模式（未播放）下必须用 DestroyImmediate —— `Destroy` 在编辑模式下会报错。
        /// </summary>
        private static void DestroyObject(GameObject go)
        {
            if (go == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(go);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>已释放的池不允许再操作。</summary>
        private void ThrowIfDisposed()
        {
            if (m_disposed)
            {
                throw new ObjectDisposedException("GameObjectPool:" + m_name);
            }
        }
    }
}
