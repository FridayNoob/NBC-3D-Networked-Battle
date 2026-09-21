# A4 · 公共 Mono 宿主（`MonoManager`）

> **留档说明**：按 `Docs\00` §15.3.1 留档，写法遵循 §15.3.1.1。
> ⚠️ 这是**补写**的（A4 当时在对话里讲过，没留文字）。

| 项 | 位置 |
| --- | --- |
| **代码** | `Framework\Mono\`：`MonoManager.cs` / `TimerHandle.cs` |
| **测试** | PlayMode **18** 条 |
| **明细** | `Docs\06` §十二 |
| **收关** | 2026-09-20 |

---

# ① 问题是什么

**只有继承 `MonoBehaviour` 的类才能拿到 `Update` / 协程。**
但框架里很多东西**不该**是 MonoBehaviour：

- 事件中心、资源管理器、对象池 —— 它们是纯逻辑，挂到 GameObject 上毫无意义
- 而且它们需要**跨场景常驻**，普通对象反而更好控制生命周期

可它们又需要"每帧做点事"（比如定时器、延时调用）。

**解法**：造**一个** MonoBehaviour 当"公共宿主"，纯逻辑对象把回调注册上去：

```csharp
MonoManager.Instance.AddUpdateListener(OnUpdate);
```

一句话：**用一个 MonoBehaviour，换一堆纯 C# 对象的"每帧能力"。**

---

# ② 最小例子

```csharp
// 每帧
MonoManager.Instance.AddUpdateListener(OnUpdate);
MonoManager.Instance.RemoveUpdateListener(OnUpdate);   // ← 一定要成对

// 定时器
TimerHandle h = MonoManager.Instance.CallLater(1.5f, () => Debug.Log("1.5 秒后"));
MonoManager.Instance.Cancel(h);

// 协程
Coroutine c = MonoManager.Instance.Run(MyRoutine());
MonoManager.Instance.Stop(c);

// 暂停（见 §4.3 的确切语义）
MonoManager.Instance.IsPaused = true;
```

---

# ③ 原版错在哪（2 条）

原框架是两个类：`MonoMgr`（纯逻辑门面）+ `MonoController`（MonoBehaviour 宿主）。

## P-13：宿主"已创建但还没受保护"的一帧窗口

```csharp
// MonoMgr 的构造函数
public MonoMgr()
{
    GameObject obj = new GameObject("MonoController");   // ← 这里创建
    controller = obj.AddComponent<MonoController>();
}

// MonoController 的 Start
private void Start()
{
    DontDestroyOnLoad(gameObject);                        // ← 这里才保护
}
```

**构造函数在第一次访问时执行，`Start` 要等 Unity 的下一个时机才跑。**
所以中间存在**一帧**：

```
宿主对象已经建好了
    ↓
但还没被 DontDestroyOnLoad 保护
    ↓
如果这一帧里发生了场景切换 → 宿主被销毁
    ↓
门面里的引用变成"伪 null" → 之后每次 AddUpdateListener 都静默失效
```

**而且这个窗口极难触发**，所以它会在最不巧的时候出现。

## P-05：构造函数里订阅，全工程**没有一处**退订

```csharp
public MonoMgr()
{
    controller.AddUpdateListener(Update);   // ← 订阅
}
```

全工程 grep `RemoveUpdateListener` —— **只有定义体，没有任何调用**。

因为 `updateEvent` 是 C# `event`（**强引用**），订阅不退订就意味着：
**订阅者永远不会被回收**。当前因为单例永驻所以不漏，但这个模式**一旦被业务代码复制就是泄漏源**。

> ⚠️ 这条的危害不是"现在漏了"，而是"**它是个会被抄走的坏榜样**"。

---

# ④ 做法

## 4.1 宿主与门面**合并**

```csharp
public sealed class MonoManager : SingletonAutoMono<MonoManager>
{
    // 门面本身就是那个 MonoBehaviour
}
```

好处：`DontDestroyOnLoad` 在**创建那一刻**就能调（A1 里 `SingletonAutoMono` 已经保证了这点），
**一帧窗口直接消失**。

> 原版之所以分成两个类，大概是想"把逻辑和 Unity 解耦"。
> 但那两个类**本来就是一对一的**，拆开只带来了那个时间窗口，没带来任何好处。

## 4.2 订阅 / 退订成对，并且**能看见**

```csharp
public void AddUpdateListener(UnityAction action);
public void RemoveUpdateListener(UnityAction action);
// LateUpdate / FixedUpdate 同样成对

// 三个计数接口
public int UpdateListenerCount { get; }
public int LateUpdateListenerCount { get; }
public int FixedUpdateListenerCount { get; }
```

**为什么要暴露计数？**
因为"记得退订"靠自觉是不可靠的。把监听数接到性能面板（E1）上之后 ——
**"对称"从"靠自觉"变成了"屏幕上有个数字在涨"**。

这是本项目反复用的一个手法：**能测量的东西才管得住。**

另外 `OnBeforeDestroy` 里会清空全部回调，不留尾巴。

## 4.3 ⚠️ `IsPaused` 的确切定义（需求只写了"可暂停"，必须写死）

`FW-M03` 只写了"可暂停"三个字。**"暂停"到底暂停什么？** 必须写死，否则每个人理解不同。

**本项目的定义**：`IsPaused = true` 时，

| 什么 | 停不停 |
| --- | --- |
| 定时器（`CallLater` / `CallEvery`） | ✅ **停** |
| 延时的东西 | ✅ **停** |
| **每帧回调（`AddUpdateListener`）** | ❌ **照常执行** |
| 协程 | ❌ 不受影响（它用 Unity 自己的时间） |

**为什么每帧回调不一起停？** 因为它们是"**游戏的帧驱动**"。
真要冻住整个游戏，应该用 `Time.timeScale = 0`（那是 Unity 的机制，物理、动画、协程都会跟着停）。

所以这个类里的 `IsPaused` 只负责"**时间相关的东西**"，语义单一、可预期。

> 📌 **面试可以讲的点**：需求里一个含糊的词（"可暂停"），
> 落地时必须**把边界一条条列出来**。不列的话，第一个用它的人就会按自己的理解实现，
> 第二个人的理解不同，然后就出现"暂停了但某个东西还在动"的怪现象。

## 4.4 派发语义：和 A3 **保持一致**（但手段不同）

A3 事件中心那两条契约（派发中增删监听的行为）在这里同样适用 —— **保持一致，少一条要记的规则**。

区别在于实现手段：

| | A3 事件中心 | A4 MonoManager |
| --- | --- | --- |
| 数据结构 | 委托链（`UnityAction` 的 `+=`） | `List<UnityAction>` **手动遍历** |
| 逐回调容错 | 必须调 `GetInvocationList()` → **每次分配数组** | 直接在循环里 `try/catch` → **零额外分配** |
| 结果 | **不做**容错（异常中断本次派发） | **做**容错（一个回调抛异常不影响其它） |

> ⚠️ **这处不一致是"取舍取决于数据结构"，不是随手定的。**
> 已记录为"已知的不一致"：如果将来更看重一致性，可以把事件中心也改成 List 存储，
> 但那会增加它自身的复杂度，当前不值得。

## 4.5 不再提供过时的 API

原版有 `StartCoroutine(string methodName, object value)` 这种重载。
它是 Unity 的**过时 API**（用字符串方法名，编译期不检查、性能也差），基本没人用。

**本项目不再提供它** —— 并在文档里写明理由。
「不提供」也是一种设计决定，比"提供了但没人用"更清楚。

---

# ⑤ 自测 4 题

**1.** P-13 那个"一帧窗口"具体是什么？为什么难触发？
**2.** 为什么要把"监听者数量"暴露出来？
**3.** `IsPaused = true` 时每帧回调为什么**不**停？
**4.** 为什么 A4 做逐回调容错，而 A3 不做？

<details>
<summary>点开看答案</summary>

**1.** 构造函数里把宿主 GameObject 建出来了，但 `DontDestroyOnLoad` 要等 `Start` 才调。
   中间这一帧里宿主"已存在但未受保护"，如果这时发生场景切换，宿主被销毁，
   门面里的引用变成伪 null → 之后所有注册**静默失效**。
   难触发是因为它要求"恰好在这一帧里切场景"。

**2.** 因为"记得退订"靠自觉不可靠。把计数接到性能面板上之后，
   **"对称"从"靠自觉"变成"屏幕上有个数字在涨"** —— 能测量的东西才管得住。

**3.** 因为每帧回调是"**游戏的帧驱动**"。要冻住整个游戏应该用 `Time.timeScale = 0`。
   这个 `IsPaused` 只管"时间相关的东西"，语义单一才好预期。

**4.** 因为**数据结构不同**：A4 用的是 `List<UnityAction>` 手动遍历，循环里 `try/catch` **零额外分配**；
   A3 用的是委托链，要逐监听者容错必须调 `GetInvocationList()`，那会**每次派发分配一个数组** ——
   在每帧几十次的热路径上是稳定 GC 压力。**取舍取决于数据结构，不是随手定的。**
</details>

---

# ⑥ 30 秒验证

Test Runner → **PlayMode** → `MonoManagerTests` **18** 条绿。

**为什么全在 PlayMode？** 这个类存在的意义就是"每帧被调用"——
`Update` / `LateUpdate` / `FixedUpdate`、定时器推进、协程。**这些只有在播放模式下才真的会发生。**
（EditMode 下连 `Awake` 都不调用，见 A1 的 5 对照实验。）

⚠️ **PlayMode 测试会真的进播放模式并临时建/销毁对象，跑完不要保存场景。**

---

# ⑦ 一分钟面试版

> 框架里很多东西需要"每帧做点事"，但它们不该是 MonoBehaviour（纯逻辑、而且要跨场景常驻）。
> 所以我做了个**公共宿主**：一个 MonoBehaviour，纯 C# 对象把回调注册上去。
>
> 原版有两个问题。第一个是**宿主"已创建但还没受保护"的一帧窗口**：
> 构造函数里建了 GameObject，但 `DontDestroyOnLoad` 要等 `Start` 才调，
> 中间这一帧如果切场景，宿主被销毁、门面里的引用变成伪 null，之后所有注册**静默失效**。
> 我的做法是**把宿主和门面合并** —— 门面本身就是那个 MonoBehaviour，
> 创建那一刻就 `DontDestroyOnLoad`，窗口直接消失。
>
> 第二个是**订阅不退订**：原版在构造函数里 `AddUpdateListener`，全工程 grep
> `RemoveUpdateListener` **一个调用都没有**。现在订阅退订成对，而且我把**监听者数量**暴露出来
> 接到性能面板上 —— **"对称"从"靠自觉"变成"屏幕上有个数字在涨"**。
>
> 另外我把 `IsPaused` 的语义写死了：暂停时**定时器停、每帧回调不停**。
> 因为每帧回调是游戏的帧驱动，要冻住整个游戏应该用 `Time.timeScale = 0`。
> 需求里"可暂停"只有三个字，**落地时必须把边界一条条列出来**，
> 否则第一个人按自己理解实现、第二个人理解不同，就会出现"暂停了但某个东西还在动"。
>
> 还有个细节：**A4 做逐回调容错，A3 事件中心不做** —— 因为数据结构不同。
> A4 用 `List` 手动遍历，循环里 try/catch 零开销；A3 用委托链，要容错就得
> `GetInvocationList()`，那会每次派发分配一个数组。**取舍取决于数据结构，不是随手定的。**

---

# 附：术语速查

| 术语 | 一句白话 |
| --- | --- |
| **宿主（host）** | 那个真正挂在 GameObject 上、负责被 Unity 每帧调用的对象 |
| **门面（facade）** | 给外部用的统一入口；背后可能是好几个对象 |
| **`DontDestroyOnLoad`** | 让对象在场景切换时不被销毁。**只对根对象生效** |
| **伪 null** | Unity 里被销毁的对象与 `null` 比较返回 true |
| **强引用** | 普通引用。只要还持有，对象就不会被回收 |
| **逐回调容错** | 一个回调抛异常不影响其它回调继续执行 |
| **`Time.timeScale`** | Unity 的时间缩放。设为 0 会让物理、动画、协程都停 |
