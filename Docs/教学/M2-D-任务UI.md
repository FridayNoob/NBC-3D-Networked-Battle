# M2-D · 任务 UI（写"能测的界面"）

> **对应代码**
> · 视图模型（纯 C#）：`Client\Assets\_Project\Game\UI\QuestPanelModel.cs`
> · 面板（MonoBehaviour）：`Client\Assets\_Project\Game\UI\QuestPanel.cs`
> · 演示壳子里的可选绑定：`Client\Assets\_Project\Boot\M2DemoBehaviour.cs`
>
> **对应测试**：`Tests\EditMode\Game\QuestPanelModelTests.cs`（12 条）、`QuestPanelTests.cs`（10 条）
>
> **对应文档**：`Docs\22-M2开工清单.md` D 组、A9 的 `Docs\教学\A9-UI框架.md`

---

## ① 问题是什么（不用代码）

任务系统已经能跑了，但玩家**看不见**：他得去 Console 里翻日志才知道自己接到了什么、还差几只怪。

要做的事一句话：**给玩家一块能看、能点的界面**。

听上去简单，但界面代码有个老问题：

> **只要它长在 `MonoBehaviour` 上，就极难测** —— 要预制体、要 Canvas、要帧循环、要"点一下"。

于是多年来的常态是"UI 靠手点验一遍，逻辑靠感觉"。这一轮要解决的就是这件事。

---

## ② 最小例子（先看差别）

```csharp
// ❌ 常见的写法：逻辑和摆放缠在一起
void Refresh()
{
    foreach (Transform child in listRoot) Destroy(child.gameObject);   // 摆放
    foreach (var q in QuestMgr.Actives)                                // 逻辑
    {
        var row = Instantiate(template, listRoot);
        row.Find("Text").GetComponent<Text>().text = q.Name + " " + q.Progress;  // 摆放 + 拼接
        row.GetComponent<Button>().onClick.AddListener(() => QuestMgr.Submit(q.Id)); // 逻辑
    }
}
```

这段能跑。但"进度怎么拼""哪些任务能交付"这些**规则**全都嵌在界面代码里，
于是它只能靠"跑起来点一下"来验。

```csharp
// ✅ 本项目的做法：两层
// 第一层（纯 C#，能测）：算什么、显示什么文字、按钮叫什么
m_model.CopyTrackingRows(rows);      // rows[i].Title / .Detail / .ActionName / .ActionEnabled
m_model.LastMessage;                 // "已经接过" / "还没做完"

// 第二层（MonoBehaviour，只负责摆）：
m_panel.Bind(m_model);               // 把上面的结果铺到 Text/Button 上
```

于是"点接取会发生什么""条件没满时按钮该不该能按""失败原因显示成什么"
**全都在 EditMode 里测**（12 条），界面那一半只剩"控件名字对不对、按钮接没接上线"（10 条）。

---

## ③ 需求要什么

`Docs\22` D 组（也就是 M2 验收的 V6）：

| # | 要求 | 判据 |
| --- | --- | --- |
| 1 | **接取列表**：能接的任务 + 接取按钮 | 行数与配置一致，点一下真的接上 |
| 2 | **追踪列表**：已接任务 + 逐条条件进度（`2/3`） | 进度变化会自动重画 |
| 3 | **交付按钮**：条件全满才能按 | 未满时灰掉；满时按下真的交付 |
| 4 | **失败原因要看得见** | 点了但被拒绝时，界面上显示原因（不是只进 Console） |

⚠️ 明确不做（M2 的最小闭环）：**滚动条（ScrollRect）、排序/筛选、任务详情弹窗、动效**。

---

## ④ 做法与关键决定

### 4.1 沿用 A9 的面板规矩，一条都不新发明

| 规矩 | 为什么 |
| --- | --- |
| 控件**按名字索引**（`RequireControl<T>("Title")`） | 名字错了**当场抛异常并列出"现有这些控件"** —— A9 那句"让错误自己说出来" |
| **不写 `Awake`** | A9 的硬约定：**EditMode 下 Unity 根本不调用 `Awake`**，依赖它面板一条都测不了 |
| `OnClick(按钮名)` 只做转发 | 动作与文案都在模型那一层 |
| 初始化入口是**公开方法** `Initialize()` | 就是为了让测试能自己搭一个面板来跑 |

📌 最后一条的直接回报：`QuestPanelTests` 里**没有预制体**——
`new GameObject` + `AddComponent` + 摆几个控件 + `Initialize()` 就能测。

### 4.2 决定一：**按钮名里带任务编号**（一个契约）

一屏上会有"接取 A / 接取 B / 交付 C"好几个按钮，而 `OnClick` 只给你**一个按钮名**。
所以约定：

```
Accept_3004   -> 接取 3004
Submit_3004   -> 交付 3004
```

⚠️ **为什么不维护"第几个按钮对应哪个任务"的下标映射**：
列表顺序一变（或者某条被过滤掉、某条完成被移除），下标就会**悄悄错位**，
表现是"**点接取 A 结果接了 B**"。名字带编号之后，**错位在结构上就不可能**。

> 这是本项目一贯的偏好：**能用结构表达的约束，不要用约定和记性去维护。**

### 4.3 决定二：**失败原因留在 `LastMessage` 上给 UI 显示**

```csharp
m_model.Accept(3004);          // 第二次
m_model.LastMessage;           // "任务 3004 已经接过了。"
```

延续 M2-A 那条"**让错误自己说出来**"：玩家点了按钮却什么都没发生，
是最难排查的一类问题（"我点了呀" → "后台说不能点" → **中间没有任何东西告诉玩家**）。

### 4.4 决定三：条件没满时**把按钮变灰**，而不是让它点了被拒绝

```csharp
row.ActionEnabled = tracking.State == EQuestState.Completed;
```

"灰掉"和"点了被拒绝"都能防错，但**灰掉更好**：
玩家还没点就知道现在不能交。被拒绝那条路仍然保留（防御性），原因照样显示。

### 4.5 决定四：容器用 `VerticalLayoutGroup`，不是空物体

容器要能被 `BasePanel.CollectControls` 收集到，而它只收 `UIBehaviour`。
空 GameObject 不是 `UIBehaviour`，所以**不能**当容器。

选 `VerticalLayoutGroup` 而不是"随便挂个 `Image` 占位"，因为它**本来就该有竖排布局**——
**不是为了能被收集才硬加一个组件**。这类"顺手用一个语义正确的类型"的习惯，
能让代码在半年后还读得懂。

### 4.6 决定五：行里的文字**按组件找，不按名字 Find**

```csharp
m_rowTemplateLabel = m_rowTemplate.GetComponentInChildren<Text>(true);
```

子物体叫什么名字是**搭预制体的人随手定的**，代码不该依赖它。
（这正是 FW-10 的病根：原框架 `Canvas.Find("Bot")` 硬编码。）

---

## ⑤ 自测 5 问（先自己答，再看折叠答案）

<details><summary><b>问 1</b>：为什么面板要拆成"模型 + 面板"两层？</summary>

因为**逻辑要能测，而摆放测不了**（或者说代价极高）。

- 模型层是纯 C#：算出"显示什么文字、按钮叫什么、能不能按"—— EditMode 12 条用例覆盖
- 面板层是 MonoBehaviour：把结果摆到 `Text`/`Button` 上 —— 只剩"控件名对不对、监听接没接"

附带好处：**换皮肤只重写摆放那一半**（和 `LoadingMaskPanel`/`LoadingMaskController` 同一个套路）。
</details>

<details><summary><b>问 2</b>：为什么按钮名要带任务编号，而不是面板里维护下标映射？</summary>

下标映射的失效方式是**静默错位**：列表顺序一变、或某条被过滤掉，
"第 2 个按钮"对应的就不再是同一个任务了，表现是"点接取 A 结果接了 B"。

按钮名带编号（`Accept_3004`）之后，"哪个按钮对应哪个任务"**在结构上就不可能错**，
而且它顺带成了两层之间的显式契约（模型产生名字、面板原样用、`OnClick` 解析）。
</details>

<details><summary><b>问 3</b>：动态 `Instantiate` 出来的按钮，为什么必须自己 `AddListener`？</summary>

因为 `BasePanel.CollectControls()` 是在 `Initialize()` 时**扫一次**，
并且接的是 `() => OnClick(那个 GameObject 的名字)` —— **名字是收集那一刻的**。

克隆体既不在那次扫描里，也不会带上模板的监听。漏了它的现象是
**"按钮点下去没反应，而且不报错"**（最难查的一类）。
</details>

<details><summary><b>问 4</b>：容器为什么必须挂 `VerticalLayoutGroup`，挂个 `Image` 行不行？</summary>

挂 `Image` 也能被收集到（它也是 `UIBehaviour`），**能跑**。
但 `VerticalLayoutGroup` 是**语义正确**的类型：列表容器本来就该竖排。
选组件时优先挑"本来就该有的那个"，别为了凑合收集条件随便挂一个 ——
半年后读代码的人会知道这个组件是干嘛的。
</details>

<details><summary><b>问 5</b>：这一轮报的"编译报错"，为什么你自己的编译闸门没抓到？</summary>

因为闸门是把**所有代码编进一个程序集**，它检查的是"API 在不在、语法合不合法"，
**查不出 asmdef 级的程序集边界问题**（"某程序集看不见某个类型"）。

这次的根因：`M2DemoBehaviour` 在 `NBC.Boot` 里调了 `QuestPanel` 的
`IsInitialized` / `Initialize()` —— 这两个是**基类 `BasePanel` 的成员**，
声明在 `NBC.Framework.UI`，而 `NBC.Boot` 只引用了 `NBC.Game`。
C# 要求"用到哪个程序集声明的成员，就得引用那个程序集"（CS0012）。

📌 **教训**：**闸门绿不等于 Unity 绿**。改 asmdef、或跨程序集用类型时，
**必须有一次 Unity 编译兜底**。这和"报告方法不该改数据"是同一族：
**工具能证明什么，要说清楚。**
</details>

---

## ⑥ 你 30 秒能做的验证

| # | 做什么 | 应该看到什么 |
| --- | --- | --- |
| 1 | Test Runner 跑全量 EditMode | **567 全绿**（M2-D 贡献 22 条） |
| 2 | 按 `Docs\22` D 组的清单搭一次预制体，把 `M2Demo` 上的**任务面板**字段拖上它 | Play 后界面上出现两条可接任务 + 接取按钮 |
| 3 | 点"接取" → 打 3 只狼 → 看追踪列表 | 条件从 `0/3` 变成 `1/3 → 2/3 → 3/3`，**自动刷新**；满后"交付"按钮由灰变亮 |
| 4 | **负向对照**：条件没满时点交付（或者把按钮的 Interactable 强制打开去点） | 状态不变，**`Message` 那行显示"还没做完"**（不是只进 Console） |

---

## ⑦ 一分钟面试版（可以直接背）

> "M2 的任务 UI 我按'**能测的界面**'来做，拆成两层：
> 一层是**纯 C# 的视图模型**（算什么、显示什么文字、按钮叫什么），
> 一层是 MonoBehaviour 面板（只负责摆）。
> 好处很直接：**规则全在 EditMode 里测**（12 条），界面那一半只剩
> '控件名对不对、按钮接没接上线'（10 条），而且不用预制体 —— 测试自己 `new GameObject` 摆控件，
> 这靠的是我们框架把面板初始化做成**公开方法**（EditMode 下 Unity 不调 `Awake`）。
>
> 有三个决定我比较满意：
> ① **按钮名里带任务编号**（`Accept_3004`）。我一开始想用'第几个按钮对应哪个任务'的下标映射，
>    那更脆 —— 列表顺序一变就**静默错位**，表现是'点接取 A 结果接了 B'。
>    名字带编号之后，错位在结构上就不可能。
> ② **失败原因留在模型上给 UI 显示**（'已经接过''还没做完'）：玩家点了没反应是最难查的问题。
> ③ **条件没满时把交付按钮变灰**，而不是让它点了被拒绝。
>
> 还踩了一个 UI 特有的坑：**动态 `Instantiate` 出来的按钮不会带上模板的点击监听**
> （框架是在初始化时按那一刻的名字接线的），漏了它的现象是'按钮点了没反应且不报错'。
>
> 另外这一轮还暴露了我工具链的一个**盲区**：我的编译闸门是把所有代码编进一个程序集，
> 所以它抓不到 **asmdef 级的程序集边界错误** —— 这次 `NBC.Boot` 用了 `QuestPanel`，
> 而 `Initialize()` 是基类 `BasePanel` 的成员（在 `NBC.Framework.UI`），没引用就编不过。
> **闸门绿不等于 Unity 绿**，这类改动必须有一次 Unity 编译兜底。"

---

## ⑧ 本轮踩的坑（如实记）

### 坑 1 · 我自检抓到两个真 bug（都在"我不能跑 Unity"的那一半）

**① `RebuildRows` 清错了范围**

面板要铺两份列表（可接 / 已接），所以 `RebuildRows` 被调用**两次**。
第一版里它调的是"清空**全局**生成列表"——于是第二次调用把**第一份列表刚生成的行也清掉**了。

症状会非常像玄学："**可接任务不显示**"（其实是刚建出来就被删了）。
✅ 改成"**只清目标容器里现有的行**"。

**② 测试里持有旧行的引用去比对"有没有被重画"**

重画是**销毁并重建**行，旧引用变成已销毁对象，访问它直接抛 `MissingReferenceException`。
✅ 改成"触发事件后**重新取行**看内容"。

📌 这两条都值得记：**UI 的"重建式刷新"有一个固定陷阱 —— 刷新之后，所有旧引用都失效了。**

### 坑 2 · 编译闸门的盲区（asmdef 级错误）

见 ⑤ 问 5。一句话：**闸门是单程序集，抓不到程序集边界问题**；
`NBC.Boot` 缺 `NBC.Framework.UI` 引用 → CS0012。

### 坑 3 · 闸门抓到的 CS0649（一个"意图没写明白"的警告）

```csharp
[SerializeField] private QuestPanel m_questPanel;   // CS0649：代码从不赋值
```

序列化字段是 **Unity** 赋值的，编译器不知道 → 报"从未对字段赋值"。
本项目的闸门要求 **0 警告**，所以显式 `= null` 消掉（不影响 Inspector 上的值）。

📌 这正是"0 警告"纪律的价值：它逼你把**编译器看不懂的意图**写明白。

---

## ⑨ 本轮的数字

| 项 | 值 |
| --- | --- |
| 新增代码 | 视图模型 1 文件 + 面板 1 文件（+ `QuestRuntime.CopyOffers`） |
| 新增 EditMode 用例 | **22**（视图模型 12 / 面板 10） |
| EditMode 总数 | **567** —— 负责人实跑 **全绿** |
| 编译 | Unity **编译通过**；闸门 0 警告 0 错误 |
| 预制体 | ⏳ 待你按 `Docs\22` D 组清单搭一次（不搭也能跑，左上角有 OnGUI） |
