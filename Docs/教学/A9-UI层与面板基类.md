# A9（第一部分）· UI 层引用 + 面板基类

> **留档说明**：本文按 `Docs\00` §15.3.1 留档。
> 写法遵循 **§15.3.1.1「说人话」**（2026-09-21 经负责人纠正后修订的版本）：
> **专业词可以随便用，但每个词第一次出现必须当场解释；代码示例尽量给、越详细越好；语言要自然。**
>
> A9 分两步做。本文只讲**第一部分**（`UILayers` + `BasePanel`），
> 第二部分（`UIManager` 异步加载 / 面板回池 / UI 栈 / 加载遮罩）做完再单独讲。

| 项 | 位置 |
| --- | --- |
| **代码** | `Client\Assets\_Project\Framework.UI\`（`UILayer.cs` / `UILayers.cs` / `BasePanel.cs` / `NBC.Framework.UI.asmdef`） |
| **测试** | `Tests\EditMode\Framework\UILayersTests.cs`（10 条）+ `BasePanelTests.cs`（14 条） |
| **明细** | `Docs\06-框架改造记录.md` §三 FW-10、§四 P-08；`Docs\02` **决策 7** |
| **收关** | 2026-09-21，EditMode **186/186** 全绿 |

---

# ① 问题是什么

## 先看一个具体场景

你要做一个"背包"界面。做法通常是：在 Unity 编辑器里把界面拼出来（一个面板、几个按钮、几个格子），然后**存成一个预制体**。

> **预制体（prefab）**：就是"存起来的界面模板"。你做好一个，可以反复实例化出很多份来用。原版框架里，每个 UI 面板就是一个预制体。

拼好之后，你要写一个脚本挂在这个预制体上。脚本里得干两件事：

1. **把界面上的按钮、图片、文字找出来**（不然你没法给按钮加点击、给文字赋值）
2. **让按钮点下去有反应**

原框架的 `BasePanel` 帮你做了第 1 件事：它自动把面板里所有按钮 / 图片 / 文字收集起来，你按名字取就行。这个便利是真的好用，所以我们**保留**它。

但它有四个毛病。前两个最要命，因为**它们出错的时候不报错**。

## 毛病一：四个 UI 层靠"按名字找"定位，找不到也不吭声

UI 界面不是一个平面，它是**分层**的。原框架分了四层：

| 层 | 放什么 |
| --- | --- |
| `Bot`（最底下） | 背景、血条这类一直显示的东西 |
| `Mid`（中间） | 普通功能面板：背包、设置 |
| `Top`（上面） | 要盖住普通面板的：确认框、角色详情 |
| `System`（最上面） | 加载遮罩、断线提示、系统弹窗 |

原框架怎么找这四层？就四行：

```csharp
bot    = canvas.Find("Bot");
mid    = canvas.Find("Mid");
top    = canvas.Find("Top");
system = canvas.Find("System");
```

`Find` 是 Unity 提供的"按名字找子节点"。问题在于：

> **它找不到的时候，不报错，直接返回 `null`（空）。**

于是就有这么一条很隐蔽的连环反应：

```
有人把 "Mid" 这个节点改名叫 "Middle"
    ↓
Find("Mid") 返回 null
    ↓
GetLayerFather 返回 null
    ↓
SetParent(null)          ← 面板被挂到了"场景根节点"
    ↓
面板不在 Canvas 底下了 → 界面不显示
    ↓
现象：进游戏一片空白，Console 干干净净
```

这种就叫**静默失败** —— 出错了，但没有任何人告诉你。排查起来极其痛苦，因为你会去怀疑美术资源、怀疑分辨率、怀疑相机，**就是不怀疑"名字被改了"**。

## 毛病二：子类写个 `Awake`，所有按钮就全哑了

先解释 `Awake`：它是 Unity 的**生命周期回调**之一。意思是"这个对象被创建出来的时候，Unity 会自动帮你调用一次这个方法"。你不用自己调，Unity 会调。

原框架把"找控件"写在了 `Awake` 里：

```csharp
public class BasePanel : MonoBehaviour
{
    private Dictionary<string, List<UIBehaviour>> controlDic = new Dictionary<string, List<UIBehaviour>>();

    protected virtual void Awake ()
    {
        FindChildrenControl<Button>();      // 找按钮
        FindChildrenControl<Image>();       // 找图片
        FindChildrenControl<Text>();        // 找文字
        FindChildrenControl<Toggle>();      // 找勾选框
        FindChildrenControl<Slider>();      // 找滑动条
        FindChildrenControl<ScrollRect>();  // 找滚动视图
        FindChildrenControl<InputField>();  // 找输入框
    }
}
```

注意那个 `virtual` —— 意思是"**子类可以覆写这个方法**"。

现在你写一个背包面板，继承它：

```csharp
public class BagPanel : BasePanel
{
    private void Awake()      // ← 我也想用 Awake 做点自己的初始化
    {
        // 忘了写 base.Awake();
        Debug.Log("背包面板创建了");
    }
}
```

C# 里**没有任何机制强制你调用 `base.Awake()`**。

而 Unity 是"**按名字找 `Awake`**"来调的：它发现子类也声明了一个 `Awake`，就**只调子类那一个**，基类那个**根本不会被执行**。

结果：

- `controlDic` 永远是空的
- `GetControl<Button>("BtnUse")` 全部返回 `null`
- 按钮的点击监听也没接上
- **现象：界面上按钮一个都点不动，而 Console 依然干干净净**

## 毛病三：名字打错了，要等很久才崩

```csharp
Button btn = GetControl<Button>("BtnUse");   // 名字打错 → 返回 null
btn.onClick.AddListener(OnUse);              // ← 这里才崩：NullReferenceException
```

崩是崩了，但报错说的是"**第 15 行空引用**"，而不是"没有叫 `BtnUse` 的按钮，你是不是想写 `BtnConfirm`"。你得自己回预制体里一个个名字对。

## 毛病四：为了找控件，整棵树被扫了 7 遍

那 7 行 `FindChildrenControl<T>()`，每一次都会把面板底下**所有子节点**走一遍。7 种类型 = **7 遍**。面板越复杂，浪费越大。

---

# ② 最小例子

看一眼"改之前"和"改之后"的差别，就明白这一整块在干嘛了。

**改之前**：层的名字写在代码里，找不到就用空的。

```csharp
Transform father = bot;              // bot 可能是 null
obj.transform.SetParent(father);     // 挂到 null 上 = 挂到场景根节点
```

**改之后**：层的引用拖在预制体上，找不到就当场报错，并且**说清是哪一格**。

```csharp
// 你会在 Console 里看到这样一行，而不是一片空白：
[UILayers] Canvas 预制体上的 UILayers 组件里，«Mid» 这一格是空的。
请把 Canvas 下面的 Mid 节点拖到 UILayers 的 Mid 字段里。
（漏填的后果是：面板会被挂到场景根节点上，界面一片空白且不报错。）
```

一句话总结：

> **把"层在哪"从代码搬到资源上，并且配错了当场喊出来。**

---

# ③ 原版错在哪 —— 完整代码

除了上面 `BasePanel` 那段，管理器那一侧的原代码是这样的：

```csharp
/// <summary>UI层级</summary>
public enum E_UI_Layer
{
    Bot,
    Mid,
    Top,
    System,
}

public class UIManager : BaseManager<UIManager>
{
    public Dictionary<string, BasePanel> panelDic = new Dictionary<string, BasePanel>();  // ← public 容器
    private Transform bot;
    private Transform mid;
    private Transform top;
    private Transform system;

    public RectTransform canvas;   // ← 也是 public

    public UIManager()
    {
        GameObject obj = ResMgr.GetInstance().Load<GameObject>("UI/Canvas");
        canvas = obj.transform as RectTransform;      // ← as 转换不检查
        GameObject.DontDestroyOnLoad(obj);

        bot    = canvas.Find("Bot");                  // ← 名字写死在代码里
        mid    = canvas.Find("Mid");
        top    = canvas.Find("Top");
        system = canvas.Find("System");
    }

    public Transform GetLayerFather(E_UI_Layer layer)
    {
        switch(layer)
        {
            case E_UI_Layer.Bot:    return this.bot;
            case E_UI_Layer.Mid:    return this.mid;
            case E_UI_Layer.Top:    return this.top;
            case E_UI_Layer.System: return this.system;
        }
        return null;                                   // ← 找不到就返回 null
    }
}
```

两个要解释的地方：

**`as` 是什么？** `obj.transform as RectTransform` 的意思是"如果它真的是 `RectTransform`，就给我；不是的话，**给我 `null`**"。和 `(RectTransform)obj.transform` 不同 —— 后者不是就**抛异常**。这个 `as` 也是静默失败的一个来源：类型不对时你不报错，只是默默拿到 null。

**`public Dictionary<...> panelDic` 为什么不好？** 因为它把内部容器直接敞开给外面。任何人拿到 `UIManager` 就能 `panelDic.Clear()`，绕过所有管理逻辑。这是审计里的 **P-11**。

---

# ④ 改造后的做法

## 第一块：`UILayers` —— 层的引用

### 先说"拖引用"这件事

Unity 里，如果你在脚本里这么写：

```csharp
[SerializeField] private Transform m_mid;
```

`[SerializeField]` 的意思是：**"这个私有字段请帮我存进资源文件，并且在 Inspector 面板上显示出来。"**

> **Inspector**：你点中一个物体时，Unity 右边显示它所有属性的那个面板。

于是你可以在编辑器里，**用鼠标把 Canvas 底下的 `Mid` 节点拖到这一格里**。

这么做最大的好处是：**节点叫什么名字，完全不影响代码。** 你把它改名成 `Middle`，引用还在。

四个字段：

```csharp
[SerializeField] private Transform m_bot;
[SerializeField] private Transform m_mid;
[SerializeField] private Transform m_top;
[SerializeField] private Transform m_system;
```

### 关键是"报错信息"

然后写一个方法，专门回答一个问题：**配置有没有问题、问题在哪。**

```csharp
public string DescribeProblem()
{
    string missing = DescribeMissing();        // 哪一格是空的？
    if (missing != null) return missing;

    string duplicated = DescribeDuplicated();  // 有两个格子指到同一个节点？
    if (duplicated != null) return duplicated;

    return DescribeNotChild();                 // 拖了别的 Canvas 的节点？
}
```

这里有一个设计点，我想让你特别注意：

> **它返回的是"一句话"（`string`），不是 `bool`。**

为什么？因为 `bool` 只能告诉你"**错了**"，而这句话能告诉你"**错在哪、该去改什么**"。

这就是这一整块存在的意义：**我们不是要"检测到错误"，我们要"让错误自己说出来"。**

三种错误都检查，是因为它们的**现象一模一样**（面板被挂错地方 → 界面不显示），但原因完全不同。只报一句"配置错了"，你还是得自己找。

漏填那条是这样写的：

```csharp
private string DescribeMissing()
{
    for (int i = 0; i < LayerCount; i++)
    {
        UILayer layer = (UILayer)i;              // 0、1、2、3 挨个查

        if (RawGet(layer) == null)
        {
            return "[UILayers] Canvas 预制体上的 UILayers 组件里，«" + GetLayerName(layer) +
                   "» 这一格是空的。\n" +
                   "请把 Canvas 下面的 " + GetLayerName(layer) + " 节点拖到 UILayers 的 " +
                   GetLayerName(layer) + " 字段里。\n" +
                   "（漏填的后果是：面板会被挂到场景根节点上，界面一片空白且不报错。）";
        }
    }

    return null;
}
```

这段没什么技巧，就是把三件事说清楚：**哪个字段、怎么填、不填会怎样**。

另外两种（两个格子指同一个节点、拖了别人的节点）也是同样的写法，这里不重复贴了，文件里都有。

### 一个让你别扭但正确的细节

`GetLayerName` 遇到不存在的层时，**抛异常**，不返回兜底字符串：

```csharp
public static string GetLayerName(UILayer layer)
{
    switch (layer)
    {
        case UILayer.Bot:    return "Bot";
        case UILayer.Mid:    return "Mid";
        case UILayer.Top:    return "Top";
        case UILayer.System: return "System";

        default:
            throw new ArgumentOutOfRangeException(
                nameof(layer), layer,
                "[UILayers] 未知的层。只可能是 Bot(0) / Mid(1) / Top(2) / System(3) —— " +
                "传进来一个野值说明某处算错了下标。");
    }
}
```

**这一条是被测试抓出来的。** 我第一版在这里写的是兜底：

```csharp
default: return "Unknown(" + (int)layer + ")";   // ← 错
```

兜底（fallback）的意思是"万一落到没预料的情况，给个总比没有好的值"。看着挺周到，但后果是：

```
传进来一个根本不存在的层 99
    ↓
我返回字符串 "Unknown(99)"
    ↓
这行字流进日志
    ↓
你看到：「[UILayers] 取不到 «Unknown(99)» 层」
    ↓
你去找一个根本不存在的、叫 Unknown(99) 的层
```

**错误信息本身在骗你。** 这和 A8 里那个"宿主被删了却报成正常播完"是同一族的病：**程序看着在正常运行，其实信息已经丢了。**

规矩很简单：**野值只可能来自写错的代码，那就当场炸掉，把真正的调用点暴露出来。**

### 还有一条细分规矩：两种"不行"要区别对待

| 什么情况 | 怎么办 | 为什么 |
| --- | --- | --- |
| 层是合法的，只是**你没在 Inspector 里拖** | `TryGet` 返回 `false`，**不炸** | 这是**配置问题**。你可能想把"四格都忘了拖"一次收集齐再一起报，所以这里不能炸 |
| 层**根本不存在**（传了 `(UILayer)99`） | **抛异常** | 这是**编程错误**。悄悄返回 `false`，等于让 bug 藏得更深 |

这条也有专门的测试钉着。

---

## 第二块：`BasePanel` —— 面板基类

### 核心决定：框架不再碰 `Awake`

我先试过一个看起来更直接的办法：把 `Awake` 改成 `sealed`。

> **`sealed`**：C# 关键字，意思是"这个类不许被继承 / 这个方法不许被覆写"。

**没用。**

原因是 Unity 找 `Awake` 的方式是**反射**。

> **反射**：在运行时"按名字去问一个类型有没有某个方法"，而不是在编译时定死。

Unity 问的是"这个对象身上有没有叫 `Awake` 的方法"，找到**最派生**（最靠近子类）的那一个就调。C# 的 `sealed`、`private` **管不了这件事** —— 因为子类完全可以再声明一个同名的 `Awake`，而 Unity 按名字找，找到的就是子类那个。

所以换了个思路：**框架不依赖 `Awake`，改成由 `UIManager` 显式调用。**

> **显式调用**：就是"我自己写一行代码去调它"，不指望 Unity 帮我调。

```csharp
// UIManager 把面板实例化出来之后，自己写这一行：
panel.Initialize();
```

`Initialize` 里面干三件事：

```csharp
public void Initialize()
{
    if (m_initialized) return;   // 已经初始化过就直接返回（面板要反复用，不能接两遍线）
    m_initialized = true;

    CollectControls();           // 1. 一次遍历，收集所有控件
    OnInit();                    // 2. 调用子类的钩子
}
```

`OnInit()` 是留给子类的**钩子**：

> **钩子（hook）**：框架在某个时刻留给你插自己代码的口子。

```csharp
protected virtual void OnInit()
{
    // 子类要写"面板创建时的初始化"，覆写这里 —— 别写 Awake
}
```

这么改之后：

- 子类**爱怎么写 `Awake` 就怎么写**，框架不靠它
- `Initialize()` 是个**普通公开方法**，测试里可以直接调

第二点比听起来重要得多。因为 **EditMode 测试里 `Awake` 根本不会被调用**（编辑模式没有播放循环）。

> 换句话说：**如果框架依赖 `Awake`，这一组测试一条都跑不起来。**
> 反过来说 —— **这组测试能跑起来，本身就是"不依赖 Awake"的证据。**

### 收集控件：一次遍历全拿到

```csharp
public void CollectControls()
{
    m_controls.Clear();

    // GetComponentsInChildren<UIBehaviour>(true) 的意思：
    //   从我自己开始，往下把所有子节点都翻一遍，
    //   把所有"UIBehaviour 及其子类"的组件都拿给我。
    //   参数 true = 连隐藏（未激活）的节点也要。
    //
    // 按钮、图片、文字、勾选框、滑动条…… 它们**全都继承自 UIBehaviour**，
    // 所以这一次遍历，就把原版 7 次扫描要找的东西一次全拿到了。
    UIBehaviour[] all = GetComponentsInChildren<UIBehaviour>(true);

    for (int i = 0; i < all.Length; i++)
    {
        UIBehaviour control = all[i];
        if (control == null) continue;

        string controlName = control.gameObject.name;   // 按 GameObject 的名字建索引

        // 同一个名字下可能挂了好几个组件（比如一个按钮既有 Button 又有 Image），
        // 所以每个名字对应的是一条"列表"，不是单个组件。
        List<UIBehaviour> bucket;
        if (m_controls.TryGetValue(controlName, out bucket))
        {
            bucket.Add(control);
        }
        else
        {
            m_controls.Add(controlName, new List<UIBehaviour> { control });
        }

        WireControl(control, controlName);              // 顺手把按钮 / 勾选框接上线
    }
}
```

**顺手接线**（这就是"按钮点下去有反应"的来源）：

```csharp
private void WireControl(UIBehaviour control, string controlName)
{
    Button button = control as Button;
    if (button != null)
    {
        // AddListener 的意思是："以后这个按钮被点了，就调这个方法"。
        // 这里放的是一个 lambda（匿名函数），它把"哪个按钮"的名字一起带上，
        // 所以子类只要写一个 OnClick，就能分辨是哪个按钮被点了。
        button.onClick.AddListener(() => OnClick(controlName));
        return;
    }

    Toggle toggle = control as Toggle;
    if (toggle != null)
    {
        toggle.onValueChanged.AddListener(value => OnValueChanged(controlName, value));
    }
}
```

**`Initialize` 重复调用不会把线接两遍**，因为有 `m_initialized` 挡着。这一条很实在：如果接了两遍，按钮点一次会走两次逻辑 —— 这种毛病在真实项目里极常见。有测试专门守它。

### 取控件：两个方法，脾气不一样

```csharp
// 脾气好的：找不到返回 null（和原版一致，适合"有没有都行"的场合）
protected T GetControl<T>(string controlName) where T : UIBehaviour

// 脾气差的：找不到当场抛异常，并把可用的名字列出来
protected T RequireControl<T>(string controlName) where T : UIBehaviour
```

`RequireControl` 抛出来的消息长这样：

```
[BasePanel:ShopPanel] 找不到叫 «BtnTypo» 的 Button。
这个面板里现有这些控件：BtnStart(Image/Button), ImgIcon(Image)
（请核对大小写与空格 —— 索引按 GameObject 名字精确匹配。）
```

**这就是"名字打错要等很久才崩"的修法**：错误发生在**你写错的那一行**，而且不用回预制体翻，报错里直接告诉你该改成什么。

### 一条机械守卫

我在测试里加了一条，用反射检查 `BasePanel` 里**有没有** `Awake`：

```csharp
[Test]
public void BasePanel_DoesNotDeclareAwake()
{
    MethodInfo awake = typeof(BasePanel).GetMethod(
        "Awake",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

    Assert.IsNull(awake, "BasePanel 不该声明 Awake ...");
}
```

`BindingFlags` 那串是"搜索条件"：实例方法、公开的、非公开的都要找；`DeclaredOnly` 表示**只看这个类自己声明的**（不含继承来的）。

这条叫**机械守卫** —— 意思是"**不靠我记得别这么写，而是写错了就自动红**"。

以后谁（包括我）想把初始化挪回 `Awake`，这条测试当场把他拦下来。这比写一句注释"注意不要用 Awake"可靠一百倍。

---

## 第三块：为什么多了一个程序集

这一轮还冒出一个**必须查手册才能确定**的问题，值得记下来。

UI 要用 Unity 的 `Button` 这些东西。而 Unity 官方手册
[Referencing assemblies](https://docs.unity3d.com/6/Documentation/Manual/assembly-definitions-referencing.html) 写得很明确：

> 用 asmdef 建的自定义程序集，**只会自动引用"预编译程序集（也就是插件 dll）"**；
> 引用关系图里**没有**"自定义程序集 → 另一个自定义程序集"这条线。
> "To use code from another assembly, **you must add a reference** to the other assembly."

> **asmdef**：Assembly Definition，Unity 里"把一批脚本划成一个程序集"的配置文件。
> 本项目用它的**引用关系**来机械保证"框架层不可能引用业务层"。

而 UGUI（就是 `Button` / `Toggle` 这些）是**包程序集**，不是预编译 dll。

**所以要用 `Button`，就必须显式写一行引用。**

但本项目的守卫是「`NBC.Framework` 的 `references` **必须为空**」。两条撞了。

**负责人 2026-09-21 决定：UI 单独成一个程序集 `NBC.Framework.UI`。** 好处是：

- `NBC.Framework` 的空引用列表**一个字不改**，守卫继续有效
- "底座零依赖、可整包导出复用"这句话继续成立
- 只有 `NBC.Framework.UI` 允许碰 UGUI

这条决定记在 `Docs\02-架构设计文档.md` **决策 7**。

> ⚠️ 顺带记一条自律：**不许为了让自己代码过而改守卫。**
> 这次是**先问、拿到批准**才动的结构，而且动的是"代码往哪放"，**不是守卫本身**。

---

# ⑤ 自测 5 题

先自己想，答案在下面。

**1.** `canvas.Find("Mid")` 找不到的时候会发生什么？为什么会一路变成"界面一片空白、Console 干干净净"？

**2.** 为什么把 `Awake` 改成 `sealed` 挡不住子类？Unity 到底是怎么找到 `Awake` 的？

**3.** `DescribeProblem()` 为什么返回"一句话"（`string`）而不是 `bool`？

**4.** `GetControl` 和 `RequireControl` 有什么区别？各自适合什么场合？

**5.** 原版扫 7 遍树，现在扫 1 遍。为什么 1 遍就够了？

<details>
<summary>点开看答案</summary>

**1.** `Find` 找不到时返回 `null` **而不报错**。这个 `null` 一路传下去，最后落到
`SetParent(null)` —— 面板被挂到**场景根节点**，不在 Canvas 底下，所以不渲染。
全程没有任何报错，所以 Console 是干净的。

**2.** 因为 Unity 是**反射**（运行时按名字找方法）来找 `Awake` 的，找到**最派生**那个就调。
子类完全可以再声明一个同名的 `Awake`，Unity 就只调它。
`sealed` / `private` 是**编译期**的约束，管不到"运行时按名字找"这件事。

**3.** 因为 `bool` 只能告诉你"**错了**"，而这句话能告诉你"**错在哪、该去改什么**"。
这一整块的目的不是"检测到错误"，而是"**让错误自己说出来**"。

**4.** `GetControl` 找不到返回 `null`，适合"这个控件有没有都行"的场合；
`RequireControl` 找不到**当场抛异常并列出可用名字**，适合"这个名字必须存在"的场合 ——
它把报错点从"后面用它的那一行"提前到了"写错名字的那一行"。

**5.** 因为**所有控件类型都继承自 `UIBehaviour`**。
问一次"你身上所有 `UIBehaviour` 及其子类"，就等于把按钮、图片、文字、勾选框……
一次性全拿到了。原版是**按具体类型问 7 次**，所以扫 7 遍。

</details>

---

# ⑥ 30 秒验证

Unity → `Window / General / Test Runner` → **EditMode** → **Run All**。

| 项 | 预期 |
| --- | --- |
| `UILayersTests` | **10** 条绿 |
| `BasePanelTests` | **14** 条绿 |
| EditMode 总数 | **186/186** |

**值得单独看的四条**：

| 用例 | 它在证明什么 |
| --- | --- |
| `MissingLayer_IsReportedByItsName` | 报错**点名说是哪一层** —— FW-10 的核心 |
| `BasePanel_DoesNotDeclareAwake` | **机械守卫**：`BasePanel` 里不许有 `Awake` |
| `Initialize_WorksEvenWhenSubclassDeclaresAwake` | **P-08 的修复证据**：测试用的面板**故意声明了 `Awake`**，初始化照样正常 |
| `Demand_ThrowsAndListsAvailableNames` | 名字打错当场报错，并列出可用的名字 |

**负向对照**（证明测试真的在测东西）：

在 `BasePanel.cs` 里临时加一个空的 `private void Awake() { }`，
`BasePanel_DoesNotDeclareAwake` 应该**立刻变红**。看到红再删掉。

不做这一步，你只知道"测试是绿的"，不知道"**测试有没有用**"。

---

# ⑦ 一分钟面试版

> UI 这块我改了四个问题，前两个是重点，因为它们**出错的时候不报错**。
>
> 第一个：原框架的四个 UI 层是靠"按名字找"定位的，`Transform.Find` 找不到时返回 `null`
> 而不报错。这个 `null` 一路传下去，最后面板被挂到场景根节点，界面一片空白，Console 干干净净。
> 我改成在 Canvas 预制体上挂一个组件，四个层的引用**直接拖进去**；加载时检查，
> 漏填、两个层指同一个节点、拖了别的 Canvas 的节点，三种情况都**当场报错，并说清是哪一格**。
>
> 第二个：原框架把"找控件"写在 `Awake` 里。子类只要也写一个 `Awake` 忘了调基类，
> 所有按钮就全失灵，而且不报错。我先试了用 `sealed` 封死，**没用** ——
> Unity 是反射按名字找 `Awake` 的，找到最派生那个就调，编译期的 `sealed` 管不到。
> 所以改成**框架根本不碰 `Awake`**，由管理器显式调 `Initialize()`。
> 顺带把原来 7 次全树扫描合并成 1 次 —— 因为所有控件都继承自 `UIBehaviour`，问一次基类全拿到。
>
> 另外两个：名字打错时原本要等用它才崩，现在有个 `RequireControl` 当场报错并把可用名字列出来；
> 还有一条测试用反射断言基类里不许出现 `Awake`，把"别用 Awake"从注释变成了机械守卫。
>
> 一个踩坑值得说：我一开始给"取层名"写了个兜底，遇到不存在的层返回 `"Unknown(99)"`。
> 测试当场抓出来了 —— 那个假名字会一路流进日志，让人去找一个根本不存在的层。
> **错误信息本身骗人，比崩溃更难查。** 改成直接抛异常了。

---

# 附：本节术语速查

| 术语 | 一句白话 |
| --- | --- |
| **预制体（prefab）** | 存起来的界面/物体模板，可以反复实例化 |
| **Inspector** | 点中一个物体时，Unity 右边显示它属性的面板 |
| **`[SerializeField]`** | 让 Unity 把这个私有字段存进资源、并显示在 Inspector 上 |
| **枚举（enum）** | 一组有名字的固定取值（如 `Bot` / `Mid` / `Top` / `System`） |
| **生命周期回调** | Unity 在特定时刻自动帮你调的方法，如 `Awake` / `Start` / `Update` |
| **反射** | 运行时"按名字去问一个类型有没有某个方法" |
| **`sealed`** | C# 关键字：不许被继承 / 不许被覆写 |
| **钩子（hook）** | 框架留下的口子，让你插自己的代码（如 `OnInit`） |
| **静默失败** | 出错了但没有任何人告诉你 |
| **兜底（fallback）** | 出意外时给一个"总比没有好"的默认值 —— 本项目里通常是错的 |
| **反射式机械守卫** | 用测试断言"某个东西不许存在"，写错了就自动红 |
| **asmdef** | Assembly Definition，Unity 里把一批脚本划成一个程序集的配置 |
| **包程序集** | 由 Unity 包（如 UGUI）编译出来的程序集，必须显式引用 |
