// 这个文件里有：
//   GameObjectPool（类）：GameObject 专用池，在泛型池之上补激活状态、父子关系与销毁方式。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NBC.Framework
{
    // 主要功能：在泛型池之上补三件只有 Unity 才有的事 —— 闲置时失活、进池与取出时维护父子关系、按播放 / 编辑模式选择销毁方式。
    // 主要数据成员：m_pool（真正干活的泛型池）、m_factory（造新对象的工厂）、m_providedRoot / m_root（池根）、m_name（池名，字符串）。
    // 主要外部方法：Spawn / Despawn / DespawnAll / Clear / GetStats / Dispose，以及 PoolName / Root / CountInPool / CountInUse 四个只读属性。
    // 谁在用它：Game / Boot 侧按需跨程序集构造并持有，谁创建谁 Dispose；PlayMode 的池用例也直接构造。
    // 注意：池根不设 DontDestroyOnLoad，切场景时它会随场景销毁，闲置栈里就会留下已销毁对象的引用，取出时靠有效性校验剔除。
    // 注意：自建的池根只在 Dispose 时销毁；要用跨场景的池，请自己传一个挂在 DontDestroyOnLoad 节点下的根进来。
    /// <summary>GameObject 专用池：在泛型池之上补上激活状态、父子关系与销毁方式。</summary>
    public sealed class GameObjectPool : IPoolStatsSource, IDisposable
    {
        // 真正干活的泛型池，对象进出、容量上限与统计都委托给它；非 null，构造后不变
        private readonly ObjectPool<GameObject> m_pool;

        // 使用者给的工厂，只负责造一个新的出来；非 null（构造时已挡空），预热与补货都走它
        private readonly Func<GameObject> m_factory;

        // 使用者指定的池根；null 表示由池自建，非 null 时 Dispose 不销毁它
        private readonly Transform m_providedRoot;

        // 池名，统计面板靠它区分多个池；非空，空名在构造时被替换成 GameObjectPool
        private readonly string m_name;

        // 实际在用的池根，首次访问 Root 时才创建；null 表示还没建
        private Transform m_root;

        // 是否已释放；true 之后 Spawn / Despawn / DespawnAll 会抛 ObjectDisposedException
        private bool m_disposed;

        /// <summary>构造一个 GameObject 池；构造期间可能立刻预热，预热会经工厂建出闲置对象。</summary>
        /// <param name="name">池名，用于统计面板区分；空则记作 GameObjectPool，同一场景内应唯一。</param>
        /// <param name="factory">创建新 GameObject 的工厂，不允许为 null。</param>
        /// <param name="maxSize">闲置容量上限；小于等于 0 表示不限制。</param>
        /// <param name="initialSize">预热数量；0 或负数表示不预热，且会被 maxSize 截断。</param>
        /// <param name="root">池根节点；传 null 则自建一个名为 Pool_池名 的节点。</param>
        public GameObjectPool(string name, Func<GameObject> factory, int maxSize = 0,
                              int initialSize = 0, Transform root = null)
        {
            // 功能：存下池名、工厂与池根，再把对象进出交给泛型池托管。
            // 入参：name 池名，空则用 GameObjectPool；factory 造对象的工厂，null 会抛 ArgumentNullException；maxSize 闲置上限，小于等于 0 表示不限；initialSize 预热数量，0 表示不预热；root 池根，null 表示自建
            // 返回：无（构造函数）
            // 步骤：
            //   1) 先挡 null 工厂：没有工厂就造不出对象，宁可在这里就抛
            //   2) 池名兜底，空名会让统计面板分不清是哪个池
            //   3) 先给 m_factory / m_name / m_providedRoot 赋值，再创建泛型池 —— 它的构造函数里可能立刻预热，预热经 CreateIdleObject 读这三个字段，顺序反了会读到 null
            //   4) 创建泛型池，把本类的工厂、销毁方式与有效性校验传进去
            // 上游：需要池的系统，谁创建谁负责 Dispose
            // 下游：CreateIdleObject、DestroyObject、IsAlive、PoolRegistry.Register
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            m_name = string.IsNullOrEmpty(name) ? "GameObjectPool" : name;
            m_factory = factory;
            m_providedRoot = root;

            m_pool = new ObjectPool<GameObject>(
                m_name,
                CreateIdleObject,
                maxSize,
                initialSize,
                DestroyObject,
                IsAlive);
        }

        /// <summary>池名，用于在统计面板上区分同一个场景里的多个池。</summary>
        public string PoolName
        {
            // 功能：返回构造时定下的池名。
            get { return m_name; }
        }

        /// <summary>池根节点；首次访问时才创建，避免建了池却没用也留下空节点。</summary>
        public Transform Root
        {
            get
            {
                // 功能：返回池根，没有就建一个；使用者传了根就直接用那个。
                // 入参：无
                // 返回：池根 Transform，非 null
                // 步骤：
                //   1) 缓存命中直接返回，避免每次访问都重新判空建节点
                //   2) 有使用者传的根就用它，池不拥有它
                //   3) 否则新建名为 Pool_池名 的节点并缓存
                // 上游：Despawn / CreateIdleObject，以及想把对象挂到池下的外部代码
                // 下游：m_providedRoot、新建的 GameObject
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

        /// <summary>当前闲置在池里的对象数，个，≥ 0。</summary>
        public int CountInPool
        {
            // 功能：转给泛型池读闲置栈的长度。
            get { return m_pool.CountInPool; }
        }

        /// <summary>当前已被取出、尚未归还的对象数，个，≥ 0。</summary>
        public int CountInUse
        {
            // 功能：转给泛型池读在外对象的数量。
            get { return m_pool.CountInUse; }
        }

        /// <summary>取出一个对象：挂到指定父节点下并激活。</summary>
        /// <param name="parent">取出后的父节点；null 表示放到场景根。</param>
        /// <returns>可用的 GameObject，不会是 null。</returns>
        public GameObject Spawn(Transform parent = null)
        {
            // 功能：从池里取一个对象，先挂到 parent 下再激活，交给调用方使用。
            // 入参：parent 取出后的父节点，null 表示放到场景根
            // 返回：可用的 GameObject，不会为 null；池空时由工厂新建，工厂返回 null 会抛异常
            // 步骤：
            //   1) 先查释放态，已释放的池不允许再取
            //   2) 向泛型池要一个对象，复用或新建；代理会顺手剔除已销毁的残留
            //   3) 先 SetParent(parent, false) 再 SetActive(true)：保留本地变换，激活时 OnEnable 读到的已经是新的父节点
            //   4) 返回
            // 上游：需要显示或复用对象的系统
            // 下游：ObjectPool<GameObject>.Get、CreateIdleObject（池空时）
            // 注意：取出后对象的使用权归调用方，用完必须 Despawn，只取不还等于泄漏，池会一直按在用计数。
            ThrowIfDisposed();

            GameObject go = m_pool.Get();

            go.transform.SetParent(parent, false);
            go.SetActive(true);
            return go;
        }

        /// <summary>归还一个对象：失活、挂回池根，再交还泛型池；池满时会被直接销毁。</summary>
        /// <param name="go">要归还的对象；null（含已销毁）会被忽略。</param>
        public void Despawn(GameObject go)
        {
            // 功能：把用完的对象收回来：失活并挂回池根，再放回闲置栈；池满时底层会直接销毁它。
            // 入参：go 要归还的对象，null 或已销毁（Unity 伪 null）直接忽略
            // 返回：无
            // 步骤：
            //   1) 先查释放态，已释放的池不再接收归还
            //   2) 用 go == null 挡掉空引用与已销毁对象，不用 ReferenceEquals，否则识别不了 Unity 的伪 null
            //   3) 先 SetActive(false)：闲置对象不能还在跑 Update 或渲染
            //   4) 再挂回池根：任何进池的对象都挂根，不只是回收来的，否则新建补货的对象会散落在场景根
            //   5) 最后交给泛型池登记，由它做重复归还检查与上限处理
            // 上游：取过对象的系统，用完即还；DespawnAll 也走这里
            // 下游：Root、ObjectPool<GameObject>.Release、DestroyObject（池满时）
            // 注意：重复归还会被底层拒收并报错，不会进池两次；但对象此时已被失活并挂回池根，属于调用方的错。
            // 注意：池满时对象会被直接销毁而不是留在池里；之后再 Spawn 拿到的是工厂新造的，别假设归还过的对象一定还在。
            ThrowIfDisposed();

            if (go == null)
            {
                return;
            }

            go.SetActive(false);

            Transform root = Root;
            if (root != null)
            {
                go.transform.SetParent(root, false);
            }

            m_pool.Release(go);
        }

        /// <summary>把当前在外的对象全部收回池中，用于战斗结束、切场景前统一收尾。</summary>
        public void DespawnAll()
        {
            // 功能：把当前所有在外的对象逐个 Despawn，做统一收尾。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 先向泛型池要一份在外对象的拷贝，不能边遍历边归还，归还会改在外的集合
            //   2) 逐个 Despawn，复用同一套失活、挂根与上限逻辑
            // 上游：战斗结束、切场景前的收尾
            // 下游：ObjectPool<GameObject>.CopyInUseTo、Despawn
            // 注意：只收在外的对象，不碰闲置栈；要连闲置的一起清，用 Clear。
            ThrowIfDisposed();

            List<GameObject> buffer = new List<GameObject>();
            m_pool.CopyInUseTo(buffer);

            for (int i = 0; i < buffer.Count; i++)
            {
                Despawn(buffer[i]);
            }
        }

        /// <summary>销毁池中闲置的对象并清空闲置栈；在外对象不在清理范围内。</summary>
        public void Clear()
        {
            // 功能：把闲置栈里的对象真正销毁，腾出内存；在外的对象不动。
            // 入参：无
            // 返回：无
            // 步骤：直接转给泛型池的 Clear(true)，由它逐个弹栈并调 DestroyObject
            // 上游：切场景、长时间不用池时的内存回收
            // 下游：ObjectPool<GameObject>.Clear、DestroyObject
            // 注意：只清闲置对象；在外的对象所有权还在调用方手上，池无权销毁，要一并处理请先 DespawnAll 再 Clear。
            m_pool.Clear(true);
        }

        /// <summary>取一份池的统计快照，给调试面板与用例断言用。</summary>
        public PoolStats GetStats()
        {
            // 功能：转给泛型池出一份统计快照，累计与峰值都由它维护。
            return m_pool.GetStats();
        }

        /// <summary>释放池：清掉闲置对象、注销统计登记，并销毁自建的池根节点。</summary>
        public void Dispose()
        {
            // 功能：释放池，清闲置对象、从统计名单注销，再销毁自建的池根。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 已释放则直接返回，重复 Dispose 是安全的
            //   2) 先置 m_disposed，再让泛型池 Dispose，置位后本类的取用入口都会抛，避免释放途中再进出
            //   3) 泛型池负责销毁闲置对象并从 PoolRegistry 注销
            //   4) 只销毁自建的根：使用者传进来的根不归池管，销毁它可能连带别人的对象
            // 上游：创建池的系统，谁创建谁 Dispose
            // 下游：ObjectPool<GameObject>.Dispose、DestroyObject
            // 注意：在外对象不会被销毁，只是不再由池管理；调用方得先 DespawnAll，否则它们留在场景里，且之后归还给已释放的池会抛。
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;
            m_pool.Dispose();

            if (m_providedRoot == null && m_root != null)
            {
                DestroyObject(m_root.gameObject);
                m_root = null;
            }
        }

        private GameObject CreateIdleObject()
        {
            // 功能：造一个新对象并当场置成闲置态，让预热出来的对象天然是失活且挂在池根下的。
            // 入参：无
            // 返回：新建的闲置对象；工厂返回 null 时也返回 null，由泛型池抛错
            // 步骤：
            //   1) 调使用者工厂造一个，可能返回 null，先挡住避免后面空引用
            //   2) 先失活，避免对象在进池前跑一帧 Update
            //   3) 再挂到池根，根为 null 时不挂，保持原层级
            // 上游：ObjectPool<GameObject> 的 Get（池空时）与 Prewarm（预热）
            // 下游：m_factory、Root
            // 注意：预热直接走工厂、不经过 Get，所以 IPoolable.OnSpawned 不会被回调。
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

        private static bool IsAlive(GameObject go)
        {
            // 功能：用 Unity 的伪 null 判断对象是否还活着，供泛型池剔除已销毁的残留。
            // 入参：go 待校验的对象，可能已被场景卸载销毁
            // 返回：true 表示仍可用；false 表示已销毁，泛型池会丢掉它、不算复用
            // 步骤：直接比 go != null —— 已销毁的 Unity 对象与 null 相等，这就是伪 null 的用法
            // 上游：ObjectPool<GameObject>.Get
            // 下游：无
            // 注意：别改成 ReferenceEquals，那样识别不了销毁；也别在这里访问 go 的成员，已销毁对象一访问就抛。
            return go != null;
        }

        private static void DestroyObject(GameObject go)
        {
            // 功能：销毁一个 GameObject，播放模式用 Destroy，编辑模式用 DestroyImmediate。
            // 入参：go 要销毁的对象，null 或已销毁直接返回
            // 返回：无
            // 步骤：
            //   1) 先挡 null 与已销毁，避免重复销毁报错
            //   2) 按 Application.isPlaying 选销毁方式，编辑模式下 Destroy 会被拒绝并报错，必须 DestroyImmediate
            // 上游：ObjectPool<GameObject> 的 Clear / Dispose（池满丢弃也走这里）、本类 Dispose 销毁自建池根
            // 下游：UnityEngine.Object.Destroy / DestroyImmediate
            // 注意：Destroy 延迟到帧末生效，同帧内对象仍能取到；编辑模式没有这一帧，所以只能 DestroyImmediate。
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

        private void ThrowIfDisposed()
        {
            // 功能：统一的释放态闸门，已释放的池再取用就在这里抛异常。
            // 入参：无
            // 返回：无；未释放时正常返回，已释放时抛 ObjectDisposedException
            // 步骤：判 m_disposed，是则带上池名抛，便于定位是哪个池
            // 上游：Spawn / Despawn / DespawnAll
            // 下游：无
            // 注意：Clear / GetStats 没有走这道闸门，释放后调它们不会抛（闲置栈已空），但别依赖这一点。
            if (m_disposed)
            {
                throw new ObjectDisposedException("GameObjectPool:" + m_name);
            }
        }
    }
}
