# 02 · 中文不显示（方框 / 豆腐块）与 TMP 字体

> 现象归类：**第 ① 层（资产/导入）为主，混着第 ④ 层（引擎字体图集）**
> 本篇是 2026-09-23 项目里真实排掉的一整轮问题，**三层坑叠在一起**，是"排查要分层"的典型例子。

---

## 一、现象

UI 上中文全是 **□ □ □**（方框），英文正常。改字号、换颜色都没用。

---

## 二、先确认是哪一层（**关键：不要在"字体"这一个词上打转**）

中文方框只有三种可能，**必须一个个排除**：

| 可能 | 一句话判据 | 本项目实测结论 |
| --- | --- | --- |
| **① 用错了字体对象**（IMGUI 里最常见） | 我设的字体**真的生效了吗**？ | IMGUI 的 `GUI.skin.font` 会被**每个 GUIStyle 自己的 font 覆盖** → 只设 skin 是**假生效** |
| **② 字体文件里根本没有中文字形** | 这个字体资产里**有多少字形**？ | `.otf` 交给 TMP 动态字体 → 加载到 **0 个字形**；换成 `.ttf` 才有 |
| **③ 字体资产是"空图集"** | 图集**真的生成出来了吗**？ | 用脚本 `TMP_FontAsset.CreateFontAsset` 造的资产：`m_CompleteImageSize: 0`、`0×0` → 什么都渲染不出来 |

---

## 三、定位步骤（照做）

### 步骤 1：确认你设的字体到底有没有生效（排除 ①）

```csharp
// IMGUI：给"每一个 GUIStyle"设字体，而不是 GUI.skin.font
GUIStyle style = new GUIStyle(GUI.skin.label);
style.font = myFont;          // ✅ 生效
// GUI.skin.font = myFont;    // ❌ 会被 label/button 各自的 font 覆盖
```

> 📌 **教训（本项目真实踩过）**：当时我在日志里打了"字体已设置"就以为成功了 ——
> **那是假成功信号**。判据应当是"**屏幕上真的显示出来了**"，不是"我调了那个 API"。

### 步骤 2：数一数字体资产里到底有多少字形（排除 ②③）

```csharp
// 在编辑器里跑（Tools 菜单 / 临时 ExecuteMethod 都行）
TMP_FontAsset font = /* 你的资产 */;
Debug.Log($"字形数 = {font.glyphTable.Count}，图集 = {font.atlasTexture.width}×{font.atlasTexture.height}");

// 图集是懒生成的（Dynamic 模式）：可以主动烘一次再数
font.TryAddCharacters("中文测试一二三");
Debug.Log($"烘完字形数 = {font.glyphTable.Count}");
```

**判据**：

| 你看到的 | 说明 | 怎么办 |
| --- | --- | --- |
| `字形数 = 0` | 字体文件**没给出**这些字形（`.otf`/子集化字体常见） | 换一份确认带中文的 `.ttf`；或用 TMP 的 Font Asset Creator 从系统字体烘 |
| `图集 0×0` / `m_CompleteImageSize = 0` | 图集**没生成**（脚本造资产的典型失败） | 别用脚本造，改用 **TMP Font Asset Creator**（或抄一份已验证可用的资产） |
| 字形数正常但屏幕还是方框 | 回到步骤 1（大概率是 ①：用错了字体对象） | —— |

### 步骤 3：直接看资产文件的原始字段（**不看界面，看数据**）

```powershell
# .asset 是 YAML：直接搜关键字段，比在 Inspector 里翻快得多
Select-String -Path 'Client\Assets\_Project\Art\Fonts\*SDF.asset' -Pattern 'm_CompleteImageSize|m_UsedGlyphRects|m_AtlasWidth|m_AtlasHeight|m_IsMultiAtlasTexturesEnabled'
```

**实测对照（本项目）**：

| 资产 | `m_CompleteImageSize` | 结果 |
| --- | --- | --- |
| 脚本生成的（错） | `0`，且 `m_UsedGlyphRects: []` | 渲染不出来 |
| 抄来的可用资产（对） | 非 0，`m_UsedGlyphRects` 有内容，1024×1024，71 个字形 | 中文正常显示 |

---

## 四、根因

1. **IMGUI 的字体是"每个 GUIStyle 一份"**：`GUI.skin.font` 只是默认值，具体控件样式会覆盖它。
2. **TMP 动态字体靠"运行时按需取字形"**：字体文件里没有的字形，取不到就是方框 —— 而 `.otf` 在 TMP 的动态加载路径上可能一个都取不到（本项目实测 0 个）。
3. **TMP 字体资产 = 字体源 + 一张（或多张）图集**：图集没烘出来，字形表就是空的 —— **光有字体文件不够**。

---

## 五、解决方案（本项目最终做法）

| 做法 | 说明 |
| --- | --- |
| **换 `.ttf`**（Alibaba PuHuiTi 3.0，免费商用） | 实测 TMP 动态加载能取到字形；`.otf` 不行 |
| **抄一份"已验证可用"的字体资产**（从另一个工程连 `.ttf` + `SDF.asset` + `.meta` 一起拷，**保留 guid**） | 比"用脚本重新造"可靠得多：造资产的坑（图集为空）很难自己发现 |
| **打开多图集**（`m_IsMultiAtlasTexturesEnabled: true`） | 中文字形多，一张 1024² 装不下时会自动开新图集，不会悄悄丢字 |
| **面板字体显式指定**（不依赖 TMP 默认字体链） | 少一层"兜底链"，出问题时定位更快 |
| **记住包体代价**：`.ttf` 8.5 MB + SDF 2.1 MB | M5 做**字符子集化**（只保留用到的字）——这正是 `Docs\05` C15 登记的依赖 |

---

## 六、防复发（已落地）

1. `Tools\...\ChineseFontSetupMenu.cs`：菜单一键做两件事（① 缺资产才建 ② 设默认 + 2 个兜底）。
2. **`TMP Settings.asset` / `LiberationSans SDF.asset` 里 3 处陈旧 guid 已改到新资产** —— 这类"指向已删资产"的引用是**静默**的，所以列进了本手册第 ① 层的检查项。
3. 留档：`Docs\24-中文字体接入.md`（含"下次先查什么"的判据）。

---

## 七、一句话讲清（面试版）

> 中文变方框我不会去调字号，而是按三层查：**① 字体对象有没有真的生效**（IMGUI 里要设到 `GUIStyle` 上，
> `GUI.skin.font` 会被覆盖）；**② 字体文件里有没有这些字形**（数 `glyphTable`；`.otf` 在 TMP 动态路径上可能是 0）；
> **③ 图集有没有烘出来**（看 `m_CompleteImageSize` 与 `m_UsedGlyphRects`）。
> 判据都是**数字**，不是"我觉得设对了"——我第一版就是拿"日志说设置成功"当证据，那是**假成功信号**。
