// ============================================================================
//  诊断实验（PlayMode 版）：Unity 在什么形状下会调用 Awake？
//  建立日期：2026-09-20
//
//  ---------------------------------------------------------------------------
//  为什么会有这个 PlayMode 版本
//  ---------------------------------------------------------------------------
//  第一版实验跑在 EditMode，结果是 **A = B = C = D = E = 0**，
//  连"普通 MonoBehaviour + private Awake"这个基线都是 0。
//
//  这说明 EditMode 下 `AddComponent` 根本不触发 Awake（编辑模式没有玩家循环），
//  于是那次实验**对"泛型基类"这个问题完全没有信息量** ——
//  五条全 0 是同一个原因造成的，对照失去了对照的意义。
//
//  这是做对照实验时最容易犯的错误：**基线塌了，所有组都被同一个因素压平**。
//  正确做法不是"从 0 里读出结论"，而是换一个基线能立起来的环境重做 —— 也就是这里。
//
//  ---------------------------------------------------------------------------
//  判读表（这次才有效）
//  ---------------------------------------------------------------------------
//   A = 1（基线立起来了）之后：
//     C = 1               -> 泛型基类的 Awake 正常，SingletonMono<T> 的写法没问题
//     C = 0 且 D = 1      -> 泛型基类丢 private Awake，改 protected virtual 可解
//     C = 0 且 E = 1      -> 用"非泛型宿主 + 泛型中间层"的写法（我更推荐，理由见下）
//     C = 0 且 D = 0 且 E = 0 -> 泛型继承体系整体不可用，需要换设计（显式 Init）
//
//  为什么更想选 E 而不是 D：
//     D 用 protected virtual，子类一旦覆写 Awake 又忘记 base.Awake()，注册就静默失效
//     （这正是原框架 P-08 的病）。E 把 Awake 藏进【非泛型】类里，泛型层只放逻辑。
//
//  ⚠️ 本用例【不做断言】，只把数字打到 Console。跑完把 5 行 [DIAG-PM] 发回来。
//     不知道正确答案时写一个"必然通过"的断言，等于自欺。
// ============================================================================

using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NBC.Tests.PlayMode
{
    /// <summary>5 个对照共用一个计数器，每次实验前清零。</summary>
    internal static class PmDiagCounter
    {
        public static int Value;
    }

    // ---- A 基线：普通 MonoBehaviour，private Awake ---------------------------------
    public class PmDiagA_Plain : MonoBehaviour
    {
        private void Awake()
        {
            PmDiagCounter.Value++;
        }
    }

    // ---- B 非泛型基类持有 private Awake -------------------------------------------
    public class PmDiagB_Base : MonoBehaviour
    {
        private void Awake()
        {
            PmDiagCounter.Value++;
        }
    }

    public class PmDiagB_Child : PmDiagB_Base
    {
    }

    // ---- C 泛型基类持有 private Awake（= 当前 SingletonMono<T> 的写法） -------------
    public abstract class PmDiagC_GenericBase<T> : MonoBehaviour where T : PmDiagC_GenericBase<T>
    {
        private void Awake()
        {
            PmDiagCounter.Value++;
        }
    }

    public class PmDiagC_Child : PmDiagC_GenericBase<PmDiagC_Child>
    {
    }

    // ---- D 泛型基类持有 protected virtual Awake -----------------------------------
    public abstract class PmDiagD_GenericBase<T> : MonoBehaviour where T : PmDiagD_GenericBase<T>
    {
        protected virtual void Awake()
        {
            PmDiagCounter.Value++;
        }
    }

    public class PmDiagD_Child : PmDiagD_GenericBase<PmDiagD_Child>
    {
    }

    // ---- E 非泛型宿主持有 private Awake，泛型只做中间层（候选修法） ----------------
    public abstract class PmDiagE_NonGenericHost : MonoBehaviour
    {
        private void Awake()
        {
            PmDiagCounter.Value++;
        }
    }

    public abstract class PmDiagE_Generic<T> : PmDiagE_NonGenericHost where T : PmDiagE_Generic<T>
    {
    }

    public class PmDiagE_Child : PmDiagE_Generic<PmDiagE_Child>
    {
    }

    // ============================================================================
    //  实验本体
    // ============================================================================

    [TestFixture]
    public class AwakeDispatchDiagnosticPlayMode
    {
        [UnityTest]
        public IEnumerator DIAGNOSTIC_WhichShapesGetAwake_InPlayMode()
        {
            yield return Report<PmDiagA_Plain>("A", "普通 MonoBehaviour + private Awake（基线）");
            yield return Report<PmDiagB_Child>("B", "非泛型基类 + private Awake");
            yield return Report<PmDiagC_Child>("C", "泛型基类 + private Awake（当前 SingletonMono 写法）");
            yield return Report<PmDiagD_Child>("D", "泛型基类 + protected virtual Awake");
            yield return Report<PmDiagE_Child>("E", "非泛型宿主 + 泛型中间层 + private Awake（候选修法）");
        }

        private static IEnumerator Report<T>(string tag, string label) where T : Component
        {
            PmDiagCounter.Value = 0;

            GameObject go = new GameObject("PmDiag_" + tag);
            T component = go.AddComponent<T>();

            // 等一帧：让 PlayMode 的生命周期彻底走完（Awake / OnEnable 都在这一帧内）
            yield return null;

            bool attached = component != null;
            int calls = PmDiagCounter.Value;

            Debug.Log("[DIAG-PM] " + tag + " | Awake 调用次数 = " + calls +
                      " | 组件挂上了 = " + attached + " | " + label);

            // PlayMode 里不用 DestroyImmediate（Unity 明确不推荐），延迟销毁即可，
            // 因为计数已经在销毁之前读完了。
            Object.Destroy(go);
            yield return null;
        }
    }
}
