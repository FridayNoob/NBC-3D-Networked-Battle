# A9 · UI 框架（层引用 · 面板基类 · 管理器 · 加载遮罩）

> **留档说明**：本文按 `Docs\00` §15.3.1 留档，写法遵循 **§15.3.1.1「说人话」**：
> **专业词可以随便用，但每个词第一次出现当场解释；代码示例尽量给、越详细越好；语言自然。**
>
> A9 原先分三次做（层引用+面板基类 / 管理器 / 加载遮罩），
> 收关后**合并成这一篇** —— 一个模块只留一篇，免得复习时到处翻。

| 项 | 位置 |
| --- | --- |
| **代码** | `Client\Assets\_Project\Framework.UI\`（`UILayer` / `UILayers` / `BasePanel` / `UIManager` / `LoadingMaskPanel` / `LoadingMaskController` + `NBC.Framework.UI.asmdef`） |
| **测试** | `Tests\EditMode\Framework\`：`UILayersTests`(10) + `BasePanelTests`(14) + `UIManagerTests`(25) + `LoadingMaskPanelTests`(5) + `LoadingMaskControllerTests`(12) = **66 条** |
| **明细** | `Docs\06` §三 FW-10、§四 P-08 / P-09 / P-11 / P-12；`Docs\02` **决策 7**；`Docs\16` §10.2.11~§10.2.13 |
| **收关** | 2026-09-21，EditMode **228/228** 全绿 |

---

# ① 问题是什么

## 先看一个具体场景

你要做一个"背包"界面。做法通常是：在 Unity 编辑器里把界面拼出来（一个面板、几个按钮、几个格子），然后**存成一个预制体**。

> **预制体（prefab）**：存起来的界面模板，可以反复实例化。

拼好之后写个脚本挂在上面。脚本得干两件事：**把界面上的按钮、图片、文字找出来**（不然没法给按钮加点击、给文字赋值），以及**让按钮点下去有反应**。

原框架的 `BasePanel` 帮你做了第 1 件事——自动把面板里所有按钮/图片/文字收集起来，你按名字取。这个便利真的好用，所以**保留**。但它有四个毛病，前两个最要命，因为**它们出错的时候不报错**。

## 毛病一：四个 UI 层靠"按名字找"定位，找不到也不吭声

UI 界面不是平面，是**分层**的。原框架分了四层：

| 层 | 放什么 |
| --- | --- |
| `Bot`（最底下） | 背景、血条这类一直显示的 |
| `Mid`（中间） | 普通功能面板：背包、设置 |
| `Top`（上面） | 要盖住普通面板的：确认框 |
| `System`（最上面） | 加载遮罩、断线提示 |

原框架怎么找这四层？就四行：

```csharp
bot    = canvas.Find("Bot");
mid    = canvas.Find("Mid");
top    = canvas.Find("Top");
system = canvas.Find("System");
```

`Find` 是 Unity 的"按名字找子节点"。问题在于：**它找不到的时候，不报错，直接返回 `null`（空）。**

于是有这么一条很隐蔽的连环反应：

```
有人把 "Mid" 改名叫 "Middle"
    ↓
Find("Mid") 返回 null
    ↓
GetLayerFather 返回 null
    ↓
SetParent(null)        ← 面板被挂到"场景根节点"
    ↓
面板不在 Canvas 底下了 → 界面不显示
    ↓
现象：进游戏一片空白，Console 干干净净
```

这叫**静默失败**——出错了，但没有任何人告诉你。排查起来极其痛苦：你会去怀疑美术资源、分辨率、相机，**就是不怀疑"名字被改了"**。

## 毛病二：子类写个 `Awake`，所有按钮全哑

先解释 `Awake`：它是 Unity 的**生命周期回调**之一——"这个对象被创建出来时，Unity 会自动帮你调一次这个方法"。你不用自己调。

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

注意那个 `virtual`——意思是"**子类可以覆写这个方法**"。你写个背包面板继承它：

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

C# **没有任何机制强制你调用 `base.Awake()`**。而 Unity 是"**按名字找 `Awake`**"来调的：它发现子类也声明了一个，就**只调子类那个**，基类那个**根本不会被执行**。

结果：`controlDic` 永远是空的 → `GetControl<Button>("BtnUse")` 全返回 `null` → 按钮监听没接上 → **按钮一个都点不动，Console 依然干干净净**。

## 毛病三：关面板是"删掉"，而且删得不干净

原版：

```csharp
public void HidePanel(string panelName)
{
    panelDic[panelName].HideMe();
    GameObject.Destroy(panelDic[panelName].gameObject);   // ← 这一帧**末尾**才真删
    panelDic.Remove(panelName);                            // ← 但这里**当场**就移除了
}
```

`Destroy` 不是立刻生效的，它把销毁**排到这一帧结束**。而字典是当场移除的。所以同一帧里"关掉再打开"：

```
HidePanel("Bag")  → Destroy 排队，字典已清
ShowPanel("Bag")  → 字典里没有 → 重新走一遍异步加载
                    而且加载完成时，那个**旧的、还没被删掉的实例**可能还在场景里
```

## 毛病四：名字打错了，要等很久才崩

```csharp
Button btn = GetControl<Button>("BtnUse");   // 名字打错 → 返回 null
btn.onClick.AddListener(OnUse);              // ← 这里才崩：NullReferenceException
```

崩是崩了，但报错说的是"**第 15 行空引用**"，而不是"没有叫 `BtnUse` 的按钮，你是不是想写 `BtnConfirm`"。你得自己回预制体里一个个名字对。

## 另外还有第五件事：切场景要显示进度

玩家点"开始战斗"之后，要等好几秒。这几秒里总得给人看点东西——于是有了**加载遮罩**：一块盖在最上面的"正在加载 42%"。

A5 已经把进度广播出来了（`SceneEvents.ProgressChanged`），所以 A9 要做的就是**把它接到遮罩上**。

---

# ② 最小例子

看一眼"改之前"和"改之后"，就明白这一整块在干嘛了。

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

**用起来**（游戏层的写法）：

```csharp
// 显示一个面板（需要时才加载，加载过就回池复用）
UIManager.Instance.ShowPanel("BagPanel", UILayer.Mid, panel =>
{
    // 面板好了，这里可以拿它做事
    ((BagPanel)panel).RefreshItems();
});

// 关掉（收起来，不销毁）
UIManager.Instance.HidePanel("BagPanel");

// 加载遮罩：装一次就一直在，自动跟着场景进度走
var mask = new LoadingMaskController(UIManager.Instance);
mask.Attach();
```

一句话总结：

> **把"层在哪"从代码搬到资源上；让所有"找不到"都变成"当场说清缺什么"；
> 关面板是收起来而不是删掉。**

---

# ③ 原版错在哪 —— 完整代码

管理器那一侧的原代码：

```csharp
public enum E_UI_Layer { Bot, Mid, Top, System, }

public class UIManager : BaseManager<UIManager>
{
    public Dictionary<string, BasePanel> panelDic = new Dictionary<string, BasePanel>();  // ← public 容器
    private Transform bot, mid, top, system;

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

    public void ShowPanel<T>(string panelName, E_UI_Layer layer = E_UI_Layer.Mid, UnityAction<T> callBack = null)
    {
        if (panelDic.ContainsKey(panelName))          // ← 只挡"已经在显示"，挡不住"正在加载"
        {
            panelDic[panelName].ShowMe();
            if (callBack != null) callBack(panelDic[panelName] as T);
            return;
        }

        ResMgr.GetInstance().LoadAsync<GameObject>("UI/" + panelName, (obj) =>
        {
            Transform father = bot;
            switch(layer) { /* 挑层 */ }

            obj.transform.SetParent(father);          // ← 没传 worldPositionStays:false

            (obj.transform as RectTransform).offsetMax = Vector2.zero;   // ← as 不检查
            (obj.transform as RectTransform).offsetMin = Vector2.zero;

            T panel = obj.GetComponent<T>();           // ← 可能是 null
            if (callBack != null) callBack(panel);
            panel.ShowMe();                            // ← panel 为 null 就 NRE
            panelDic.Add(panelName, panel);            // ← 同帧第二次走到这里会抛重复键
        });
    }
}
```

几处要解释：

**`as` 是什么？** `obj.transform as RectTransform` 意思是"如果它真的是 `RectTransform` 就给我，**不是的话给我 `null`**"。和 `(RectTransform)obj.transform` 不同——后者不是就**抛异常**。这个 `as` 是静默失败的来源之一。

**`public Dictionary<...> panelDic` 为什么不好？** 它把内部容器直接敞开给外面。任何人拿到 `UIManager` 就能 `panelDic.Clear()`，绕过所有管理逻辑（审计里的 **P-11**）。

**`SetParent(father)` 少了什么？** `SetParent` 有个可选参数 `worldPositionStays`，默认 `true`——意思是"帮我保持世界坐标"。对 UI 来说这会让 Unity 为了维持世界位置去**反算 `localScale`**，界面就被拉伸或缩小了。UI 必须传 `false`。

---

# ④ 改造后的做法

## 4.0 先说程序集：为什么多了一个 `NBC.Framework.UI`

这一轮冒出一个**必须查手册才能确定**的问题。

UI 要用 Unity 的 `Button` 这些类型，而它们属于 **UGUI**。Unity 官方手册
[Referencing assemblies](https://docs.unity3d.com/6/Documentation/Manual/assembly-definitions-referencing.html) 写得很明确：

> 用 asmdef 建的自定义程序集，**只会自动引用"预编译程序集（也就是插件 dll）"**；
> 引用关系图里**没有**"自定义程序集 → 另一个自定义程序集"这条线。
> "To use code from another assembly, **you must add a reference** to the other assembly."

> **asmdef**：Assembly Definition，Unity 里把一批脚本划成一个程序集的配置文件。
> 本项目用它的**引用关系**机械保证"框架层不可能引用业务层"。

而 UGUI 是**包程序集**，不是预编译 dll。**所以要用 `Button`，就必须显式写一行引用。**

但本项目的守卫是「`NBC.Framework` 的 `references` **必须为空**」（FW-12 的机械保证）。两条撞了。

**你 2026-09-21 的决定：UI 单独成一个程序集 `NBC.Framework.UI`。** 好处：

- `NBC.Framework` 的空引用列表**一个字不改**，守卫继续有效
- "底座零依赖、可整包导出复用"这句话继续成立
- 只有 `NBC.Framework.UI` 允许碰 UGUI

> ⚠️ 顺带一条自律：**不许为了让自己代码过而改守卫。**
> 这次是**先查手册确认事实 → 把冲突报给你 → 拿到决定才动结构**，
> 动的是"代码往哪放"，**不是守卫本身**。

## 4.1 `UILayers` —— 层的引用

### 先说"拖引用"这件事

Unity 里如果你这么写：

```csharp
[SerializeField] private Transform m_mid;
```

`[SerializeField]` 的意思是：**"这个私有字段请帮我存进资源文件，并且在 Inspector 面板上显示出来。"**

> **Inspector**：你点中一个物体时，Unity 右边显示它所有属性的那个面板。

于是你可以在编辑器里**用鼠标把 Canvas 底下的 `Mid` 节点拖到这一格里**。最大好处是：**节点叫什么名字完全不影响代码**。你改名成 `Middle`，引用还在。

四个字段：

```csharp
[SerializeField] private Transform m_bot;
[SerializeField] private Transform m_mid;
[SerializeField] private Transform m_top;
[SerializeField] private Transform m_system;
```

### 关键是"报错信息"

然后写个方法专门回答：**配置有没有问题、问题在哪。**

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

有个设计点请你特别注意：**它返回的是"一句话"（`string`），不是 `bool`。**

为什么？因为 `bool` 只能告诉你"**错了**"，而这句话能告诉你"**错在哪、该去改什么**"。

这就是这一整块存在的意义：**我们不是要"检测到错误"，我们要"让错误自己说出来"。**

三种错误都检查，是因为它们**现象一模一样**（面板被挂错地方 → 界面不显示），但原因完全不同。只报一句"配置错了"，你还是得自己找。

漏填那条：

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

没技巧，就是把三件事说清楚：**哪个字段、怎么填、不填会怎样**。另外两种也是同样写法。

### 一个让你别扭但正确的细节

`GetLayerName` 遇到不存在的层时**抛异常**，不返回兜底字符串：

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

**这一条是被测试抓出来的。** 我第一版写的是兜底：

```csharp
default: return "Unknown(" + (int)layer + ")";   // ← 错
```

兜底（fallback）的意思是"万一落到没预料的情况，给个总比没有好的值"。看着挺周到，后果是：

```
传进来一个根本不存在的层 99
    ↓ 我返回字符串 "Unknown(99)"
    ↓ 这行字流进日志
你看到：「[UILayers] 取不到 «Unknown(99)» 层」
    ↓ 你去找一个根本不存在的、叫 Unknown(99) 的层
```

**错误信息本身在骗你。** 规矩很简单：**野值只可能来自写错的代码，那就当场炸掉，把真正的调用点暴露出来。**

### 还有一条细分规矩：两种"不行"要区别对待

| 什么情况 | 怎么办 | 为什么 |
| --- | --- | --- |
| 层合法，只是**你没在 Inspector 里拖** | `TryGet` 返回 `false`，**不炸** | 这是**配置问题**。你可能想把"四格都忘了拖"一次收集齐再报，所以这里不能炸 |
| 层**根本不存在**（传了 `(UILayer)99`） | **抛异常** | 这是**编程错误**。悄悄返回 `false` 等于让 bug 藏得更深 |

## 4.2 `BasePanel` —— 框架不碰 `Awake`

### 先试了 `sealed`，没用

> **`sealed`**：C# 关键字，"这个类不许被继承 / 这个方法不许被覆写"。

因为 Unity 找 `Awake` 用的是**反射**。

> **反射**：运行时"按名字去问一个类型有没有某个方法"，而不是编译时定死。

Unity 问"这个对象身上有没有叫 `Awake` 的方法"，找到**最派生**（最靠近子类）那个就调。C# 的 `sealed`/`private` **管不了**——子类完全可以再声明一个同名的。

### 所以改成"框架不依赖它"

> **显式调用**：我自己写一行代码去调它，不指望 Unity 帮我调。

```csharp
panel.Initialize();      // ← UIManager 在实例化面板之后自己写这一行
```

```csharp
public void Initialize()
{
    if (m_initialized) return;   // 已经初始化过就直接返回（面板要反复用，不能接两遍线）
    m_initialized = true;

    CollectControls();           // 1. 一次遍历，收集所有控件
    OnInit();                    // 2. 调用子类的钩子
}
```

`OnInit()` 是留给子类的**钩子**（hook——"框架在某个时刻留给你插自己代码的口子"）：

```csharp
protected virtual void OnInit()
{
    // 子类要写"面板创建时的初始化"，覆写这里 —— 别写 Awake
}
```

这么改之后：子类**爱怎么写 `Awake` 就怎么写**，框架不靠它；而且 `Initialize()` 是个**普通公开方法，测试里可以直接调**。

第二点比听起来重要得多：**EditMode 测试里 `Awake` 根本不会被调用**（编辑模式没有播放循环）。换句话说——**如果框架依赖 `Awake`，这组测试一条都跑不起来**；反过来说，**这组测试能跑，本身就是"不依赖 Awake"的证据**。

### 收集控件：一次遍历全拿到

```csharp
public void CollectControls()
{
    m_controls.Clear();

    // GetComponentsInChildren<UIBehaviour>(true) 的意思：
    //   从我自己开始往下把所有子节点翻一遍，把所有"UIBehaviour 及其子类"的组件拿给我。
    //   参数 true = 连隐藏（未激活）的节点也要。
    //
    // 按钮、图片、文字、勾选框、滑动条…… 它们**全都继承自 UIBehaviour**，
    // 所以这一次遍历，就把原版 7 次扫描要找的东西一次全拿到了。
    UIBehaviour[] all = GetComponentsInChildren<UIBehaviour>(true);

    for (int i = 0; i < all.Length; i++)
    {
        UIBehaviour control = all[i];
        if (control == null) continue;

        string controlName = control.gameObject.name;   // 按 GameObject 名字建索引

        // 同一个名字下可能挂好几个组件（一个按钮既有 Button 又有 Image），
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

        WireControl(control, controlName);              // 顺手把按钮/勾选框接上线
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
        // AddListener 的意思："以后这个按钮被点了，就调这个方法"。
        // 这里放的是 lambda（匿名函数），它把"哪个按钮"的名字一起带上，
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

`Initialize` 重复调用**不会把线接两遍**（有 `m_initialized` 挡着）。这条很实在：接两遍的话按钮点一次会走两次逻辑——真实项目里极常见。有测试专门守它。

### 取控件：两个方法，脾气不一样

```csharp
// 脾气好的：找不到返回 null（和原版一致，适合"有没有都行"的场合）
protected T GetControl<T>(string controlName) where T : UIBehaviour

// 脾气差的：找不到当场抛异常，并把可用的名字列出来
protected T RequireControl<T>(string controlName) where T : UIBehaviour
```

`RequireControl` 抛出来长这样：

```
[BasePanel:ShopPanel] 找不到叫 «BtnTypo» 的 Button。
这个面板里现有这些控件：BtnStart(Image/Button), ImgIcon(Image)
（请核对大小写与空格 —— 索引按 GameObject 名字精确匹配。）
```

**这就是"名字打错要等很久才崩"的修法**：错误发生在**你写错的那一行**，而且不用回预制体翻。

### 一条机械守卫

测试里加了一条，用反射检查 `BasePanel` 里**有没有** `Awake`：

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

这叫**机械守卫**——"**不靠我记得别这么写，而是写错了就自动红**"。

## 4.3 `UIManager` —— 加载 / 回池 / 栈

### 职责

```csharp
UIManager.Instance.ShowPanel("BagPanel", UILayer.Mid, onShown, onFailed);
UIManager.Instance.HidePanel("BagPanel");    // 收起来（回池，不销毁）
UIManager.Instance.ClosePanel("BagPanel");   // 真的关掉（销毁 + 释放素材句柄）
```

### 关面板 = 收起来（P-09 的结构性修法）

```csharp
public bool HidePanel(string panelName)
{
    BasePanel panel;
    if (!m_visible.TryGetValue(panelName, out panel)) return false;

    m_visible.Remove(panelName);
    panel.HideMe();
    panel.gameObject.SetActive(false);      // ← 停用，但**不销毁**

    m_pooled[panelName] = panel;            // ← 进池，下次复用
    return true;
}
```

**为什么这比"加个标记去挡"更彻底？** 因为原版的毛病来自"**销毁是延迟的**"这个时间窗口。
**既然根本不销毁，那个窗口就不存在了** —— 不需要额外的标记，也不需要记住"要检查标记"。

### 同名只有一条加载任务

```csharp
// 正在加载中的面板：名字 → 那一条加载（连同等它的所有回调）
private readonly Dictionary<string, PendingLoad> m_pending = new Dictionary<string, PendingLoad>();
```

第二次请求同一个面板时：

```csharp
PendingLoad pending;
if (m_pending.TryGetValue(panelName, out pending))
{
    // 不另起一条，把自己的回调挂到**已经在跑的那一条**上
    AddCallback(pending, onShown, onFailed);
    return;
}
```

**这就是原版"同帧连点两下 = 加载两遍 + 字典重复键报错"的修法。**

### 挂层：把 P-12 补上

```csharp
private void AttachToLayer(GameObject instance, string panelName, UILayer layer)
{
    // P-12 的修法：`as` 转换**必须检查**。
    RectTransform rect = instance.transform as RectTransform;

    if (rect == null)
    {
        throw new InvalidOperationException(
            "[UIManager] 面板 «" + panelName + "» 的根节点不是 RectTransform（实际类型：" +
            instance.transform.GetType().Name + "）。\n" +
            "⚠️ 常见原因：把一个 3D 物体或一个**空 GameObject**当成了 UI 预制体。");
    }

    Transform parent = m_layers.Get(layer);   // 配置有问题时抛它那句人话

    // ⚠️ `worldPositionStays: false` —— UI 必须用这个。
    rect.SetParent(parent, false);

    rect.localPosition = Vector3.zero;
    rect.localScale = Vector3.one;
    rect.offsetMax = Vector2.zero;
    rect.offsetMin = Vector2.zero;
}
```

### UI 栈

```csharp
UIManager.Instance.PushPanel("BagPanel");   // 标记"它可以被返回键关掉"
UIManager.Instance.PopPanel();              // 关掉栈顶
UIManager.Instance.HandleBack();            // 游戏层把返回键绑到这里
```

⚠️ **框架不读键盘**（FW-12）。键盘从哪来是游戏层的事，框架只提供"返回"这个动作。

⚠️ `PushPanel` 要求面板**正在显示**，否则抛异常并说明原因：栈的语义是"这个面板可以被关掉"，把没显示的面板压进去只会让返回键失灵。

## 4.4 加载遮罩 —— 面板与策略分开

### 拆成两个类

| 类 | 管什么 |
| --- | --- |
| `LoadingMaskPanel` | **长什么样**：进度条（`Image.fillAmount`）、文字 |
| `LoadingMaskController` | **什么时候出现**：听 A5 的场景事件，决定显示/收起/写什么 |

**为什么要拆？** 一句话：**"面板长什么样"是美术的事，"什么时候出现"是逻辑的事。**
拆开之后换一套遮罩皮肤不用改逻辑；而且逻辑**可以脱离任何 UI 预制体单独测**
（那 12 条用例一个真实 UI 资源都不需要）。

### 控制器怎么接 A5

```csharp
public void Attach()
{
    if (m_attached) return;
    m_attached = true;

    m_events.AddEventListener(SceneEvents.ProgressChanged, m_onProgress);
    m_events.AddEventListener(SceneEvents.LoadSucceeded, m_onSucceeded);
    m_events.AddEventListener(SceneEvents.LoadFailed, m_onFailed);
}
```

⚠️ **三个回调必须存成字段**。因为 `EventCenter.RemoveEventListener` 是按**委托相等性**匹配的——
如果你每次现写 `HandleProgress`，移除时匹配不上，**监听会一直留着**。那就是审计里的 **P-05（订阅不退订）**。

### 事件 → 行为

| 收到什么 | 做什么 |
| --- | --- |
| `ProgressChanged` | 遮罩没出现过就请求显示；已经显示就直接更新进度 |
| `LoadSucceeded` | 收起遮罩 |
| `LoadFailed` | 遮罩**留着**，把错误写上去让玩家看见 |

### 一个容易漏的细节

遮罩**自己也是异步加载的**。所以"进度事件比遮罩先到"是常态：

```csharp
private void HandleProgress(SceneProgressInfo info)
{
    LastProgress = info.Progress;
    LastLocation = info.Location;

    if (!m_requested)
    {
        m_requested = true;
        m_ui.ShowPanel(m_panelName, UILayer.System, OnMaskShown, OnMaskFailed);
        return;                  // ← 还在加载，先把进度记着
    }

    ApplyProgress();             // 已经显示了，直接更新
}

private void OnMaskShown(BasePanel panel)
{
    // 遮罩终于出来了：把**最新**那个进度补上（而不是第一次收到的那个）
    ((LoadingMaskPanel)panel).SetProgress(LastProgress, LastLocation);
}
```

### 两个可选控件

`LoadingMaskPanel` 找控件用的是 `GetControl`（找不到返回 `null`）而**不是** `RequireControl`（找不到抛异常）：

```csharp
protected override void OnInit()
{
    m_progressBar  = GetControl<Image>(ProgressBarControlName);
    m_progressText = GetControl<Text>(ProgressTextControlName);
    SetProgress(0f, null);
}
```

**这是框架里少数故意不 fail-fast 的地方**：遮罩的样式是美术决定的，只有进度条、或只有文字，都是合法预制体。
所以有一条测试专门钉住"一个控件都没有也不许崩"，防止以后有人"顺手"改成 `RequireControl`。

## 4.5 ⚠️ 为什么全部用回调，而不是 `async/await`

**这一节是 A9 最值得记住的东西。**

我第一版 `UIManager` 内部用的是 `async/await`，写到一半发现一个问题：

> C# 的 `await` 在恢复执行时，会把"后续代码"**投递回它当初捕获的 `SynchronizationContext`**（同步上下文）。
> Unity 编辑器/播放模式里装了 `UnitySynchronizationContext`，它的"投递"要靠**帧循环**才会被执行。
> 而 **EditMode 测试没有帧循环** ——
> 于是续体被投递进队列，**却永远没人来跑它**，测试会**卡死**。

而本框架既有的异步风格是**回调 + 句柄**（`AssetHandle.OnComplete`、`SceneLoader.Tick`）。
回调是**在完成的那一刻被直接调用**的，不经过任何上下文投递 —— 所以在 EditMode 里**确定性地能跑**。

**结论：内部一律用回调。** `ShowPanelAsync` 只是包在外面的一个便利层：

```csharp
public Task<BasePanel> ShowPanelAsync(string panelName, UILayer layer = UILayer.Mid)
{
    TaskCompletionSource<BasePanel> source = new TaskCompletionSource<BasePanel>();

    ShowPanel(panelName, layer,
              panel => source.TrySetResult(panel),
              error => source.TrySetException(error));

    return source.Task;
}
```

因为它在回调触发的那一刻就 `SetResult` 了，所以外面 `await` 到的是一个**已经完成的任务**，同样不会卡。

> **这也顺便解释了本框架为什么一直是"回调 + 句柄"风格，而不是 `async/await` 风格**
> —— 不是为了复古，是因为**回调在编辑器里可以被确定性地测**。

---

# ⑤ 自测 6 题

先自己想，答案在下面。

**1.** `canvas.Find("Mid")` 找不到时会发生什么？为什么会一路变成"界面空白、Console 干净"？
**2.** 为什么把 `Awake` 改成 `sealed` 挡不住子类？Unity 到底怎么找到 `Awake` 的？
**3.** `DescribeProblem()` 为什么返回"一句话"而不是 `bool`？
**4.** **关面板"收起来"比"销毁"好在哪里？** 为什么说它是"结构性修法"而不是"打补丁"？
**5.** 原版扫 7 遍树，现在扫 1 遍——为什么 1 遍就够？
**6.** 为什么框架内部坚持用**回调**而不是 `async/await`？（提示：想 EditMode 有没有帧循环）

<details>
<summary>点开看答案</summary>

**1.** 返回 `null` **而不报错**，这个 `null` 一路传下去最后落到 `SetParent(null)`——面板被挂到**场景根节点**，不在 Canvas 下所以不渲染。全程没报错，所以 Console 干净。

**2.** 因为 Unity 是**反射**（运行时按名字找方法）找 `Awake`，找到**最派生**那个就调。子类完全可以再声明一个同名的。`sealed`/`private` 是**编译期**约束，管不到"运行时按名字找"。

**3.** `bool` 只能告诉你"**错了**"，这句话能告诉你"**错在哪、该去改什么**"。目的不是"检测到错误"，而是"**让错误自己说出来**"。

**4.** 原版的毛病来自"`Destroy` 延迟到帧末、字典当场清"这个**时间窗口**。打补丁的思路是"加个标记去挡住同帧重复请求"，但那样你必须**记得检查标记**、而且窗口还在。
不销毁的话，**那个窗口根本不存在**——不需要标记，也没地方可忘。

**5.** 因为**所有控件类型都继承自 `UIBehaviour`**。问一次"你身上所有 `UIBehaviour` 及其子类"，就把按钮、图片、文字、勾选框一次性全拿到了。原版是**按具体类型问 7 次**，所以扫 7 遍。

**6.** 因为 `await` 恢复时要把续体**投递回同步上下文**，而 Unity 的同步上下文要靠**帧循环**驱动，
**EditMode 测试没有帧循环** → 续体会永远没人跑，测试**卡死**。
回调是在完成那一刻**直接调用**的，不经过投递，所以确定性可测。

</details>

---

# ⑥ 30 秒验证

Unity → `Window / General / Test Runner` → **EditMode** → **Run All**：

| 测试类 | 条数 |
| --- | --- |
| `UILayersTests` | 10 |
| `BasePanelTests` | 14 |
| `UIManagerTests` | 25 |
| `LoadingMaskPanelTests` | 5 |
| `LoadingMaskControllerTests` | 12 |
| **合计（EditMode 总数）** | **228/228** |

**最值得单独看的五条**：

| 用例 | 它在证明什么 |
| --- | --- |
| `MissingLayer_IsReportedByItsName` | 报错**点名说是哪一层** —— FW-10 的核心 |
| `BasePanel_DoesNotDeclareAwake` | **机械守卫**：`BasePanel` 里不许有 `Awake` |
| `HidePanel_PutsPanelIntoPoolWithoutDestroyingIt` | **P-09 的核心**：关面板是收起来，不是销毁 |
| `ShowPanel_WhileLoading_HooksOntoSameLoad` | 同名只有一条加载任务（原版会同帧加载两遍） |
| `Detach_StopsReacting` | **P-05**：订阅真的能退掉（委托要存字段） |

**负向对照**（证明测试真的在测东西，W9）：

在 `BasePanel.cs` 里临时加一个空的 `private void Awake() { }` →
`BasePanel_DoesNotDeclareAwake` 应该**立刻变红**。看到红再删掉。

不做这一步，你只知道"测试是绿的"，不知道"**测试有没有用**"。

---

# ⑦ 一分钟面试版

> UI 这块我改了四个问题，前两个是重点，因为**它们出错的时候不报错**。
>
> 第一个：原框架四个 UI 层靠"按名字找"定位，`Transform.Find` 找不到返回 `null` 而不报错。
> 这个 `null` 一路传下去最后面板被挂到场景根节点，界面一片空白、Console 干干净净。
> 我改成在 Canvas 预制体上挂组件、四层引用**直接拖进去**；加载时检查
> 漏填 / 两层指同一节点 / 拖了别的 Canvas 三种情况，**当场报错并说清是哪一格**。
>
> 第二个：原框架把"找控件"写在 `Awake` 里，子类写个 `Awake` 忘调基类，
> 所有按钮全失灵且不报错。我先试 `sealed` 封死，**没用** ——
> Unity 是反射按名字找 `Awake`，找到最派生那个就调，编译期约束管不到。
> 所以改成**框架根本不碰 `Awake`**，由管理器显式调 `Initialize()`。
> 顺带把 7 次全树扫描合并成 1 次 —— 所有控件都继承自 `UIBehaviour`，问一次基类全拿到。
>
> 第三个：关面板原本是"销毁"，而 `Destroy` 要等帧末、字典却当场清，
> 于是同帧"关掉再打开"会重复加载、甚至拿到没删掉的旧实例。
> 我改成**收起来回池、不销毁** —— 那个时间窗口直接不存在了。
> 同一个面板名同时只会有一条加载任务，第二次请求挂到同一条上。
>
> 第四个：切场景的加载遮罩。我把它拆成**面板**和**控制器**两个类 ——
> 面板管长什么样、控制器管什么时候出现，这样换皮肤不改逻辑，逻辑也能脱离预制体单独测。
>
> 一个踩坑值得说：我最初在管理器内部用了 `async/await`，后来意识到
> **`await` 会把续体投递回 Unity 的同步上下文，而 EditMode 测试没有帧循环去跑它**，
> 测试会卡死。所以整个框架内部坚持**回调 + 句柄**风格 —— 回调是完成那一刻直接调用的，
> 不经过投递，**在编辑器里可以被确定性地测**。

---

# 附：术语速查

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
| **机械守卫** | 用测试断言"某个东西不许存在/必须存在"，写错了就自动红 |
| **asmdef** | Assembly Definition，Unity 里把一批脚本划成一个程序集的配置 |
| **包程序集** | 由 Unity 包（如 UGUI）编译出来的程序集，**必须显式引用** |
| **同步上下文（SynchronizationContext）** | "await 之后回到哪个线程"的机制。Unity 的它要靠帧循环驱动 |
| **回调驱动 vs await 驱动** | 前者在完成那一刻直接调用（可确定性测试），后者要把续体投递回上下文 |
| **`worldPositionStays`** | `SetParent` 的参数。UI 必须传 `false`，否则会被反算缩放 |
