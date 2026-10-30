// 这个文件里有：
//   本文件只有程序集特性：给两个测试程序集打开内部可见性，没有任何类型声明。
//
// 为什么需要它：
//   框架里有些成员故意不是 public，业务层不该碰但测试必须能碰，典型例子是
//   SingletonRegistry.ResetAll()，它把单例静态状态清干净，供测试做用例之间的隔离；
//   一旦放开成 public，业务代码就可能随手调用，把正在使用的单例清掉。
//   改成 public 会污染 API 表面，加 #if UNITY_EDITOR 的公开包装会让条件编译分支越滚越多，
//   所以选成员保持 internal、只对指定测试程序集开洞，代价是程序集改名时这里必须同步改。
//
// 注意：下面的程序集名必须与 Tests\EditMode\Tests_EditMode.asmdef 和
//   Tests\PlayMode\Tests_PlayMode.asmdef 里的 name 完全一致；改名后若这里没跟上，
//   编译器不会报错，只会在运行时报「无法访问 internal 成员」。
//   两个测试程序集都要开：EditMode 测纯逻辑，PlayMode 测 MonoBehaviour 生命周期，
//   这两类测试的边界见 Docs\06 的 9.7 节。

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("NBC.Tests.EditMode")]
[assembly: InternalsVisibleTo("NBC.Tests.PlayMode")]
