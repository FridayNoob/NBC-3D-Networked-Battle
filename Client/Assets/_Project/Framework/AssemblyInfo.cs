// ============================================================================
//  NBC.Framework · 程序集级特性
//
//  InternalsVisibleTo：让 EditMode 测试可以访问 internal 成员。
//
//  为什么需要它？
//    框架里有些东西【故意】不是 public：业务层不该碰，但测试必须能碰。
//    典型例子是 SingletonRegistry.ResetAll() —— 它是"把单例静态状态清干净"的开关，
//    测试用它做用例之间的隔离；但如果把它 public 出去，业务代码就可能随手调用，
//    把正在使用的单例清掉。
//
//  三种做法的取舍（面试可以讲）：
//    ① 把成员改成 public          —— 最省事，但污染了 API 表面，业务可能误用。
//    ② 加一个 #if UNITY_EDITOR 的公开包装 —— 只在编辑器可见，但条件编译分支越多越难维护。
//    ③ InternalsVisibleTo（本项目选的）—— 成员保持 internal，只对指定测试程序集开洞。
//       代价是：程序集一旦改名，这里要跟着改，而且编译器【不会】报错，
//       只会在运行时报"无法访问 internal 成员"。所以下面的程序集名必须与
//       Tests/EditMode/Tests_EditMode.asmdef 里的 "name" 完全一致。
//
//  对应的 asmdef 名：NBC.Tests.EditMode、NBC.Tests.PlayMode
//  （两个测试程序集都要开：EditMode 测纯逻辑，PlayMode 测 MonoBehaviour 生命周期，
//    见 Docs\06 §9.7 —— 这两类测试的边界是 2026-09-20 用实验定下来的，不是猜的。）
// ============================================================================

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("NBC.Tests.EditMode")]
[assembly: InternalsVisibleTo("NBC.Tests.PlayMode")]
