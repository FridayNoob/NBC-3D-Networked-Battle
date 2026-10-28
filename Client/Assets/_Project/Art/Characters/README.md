# 第三方角色模型（不进仓库）

## 为什么这个目录被忽略

R002 用的是 **Unity-chan**（Unity 官方日系角色）。它的许可是 **Unity-Chan License**：
允许在作品里使用（含商用，需署名），但**不得单独再分发模型数据**。
把模型文件 push 到公开仓库 = 再分发 ⇒ 所以 `.gitignore` 只忽略本目录内容、保留本说明文件。

## 谁来跑这个项目，怎么补上模型

1. 打开 Unity-chan 官方站（搜 `Unity-chan` → 官方页面 / Unity Asset Store 的 Unity-chan 包），下载模型与动画。
2. 解压/导入到本目录：`Assets/_Project/Art/Characters/UnityChan/`
3. Unity 会自动导入 FBX 与动画；把角色拖进 `Battle.unity`，删掉原来的 Capsule。
4. 角色模型上确认有 `Animator` 组件，Controller 用 Unity-chan 自带的（或按 `03-方案设计.md` 新建一个只含 Idle/Walk 的）。
5. 保留 `PlayerMove`（它管位移）；确认 `Animator.applyRootMotion = false`。

## 许可要点（务必遵守）

| 项 | 要求 |
| --- | --- |
| 署名 | 作品里需标注 Unity-chan 的版权信息（具体写法见官方许可页） |
| 再分发 | **不得单独再分发模型数据** ⇒ 不要提交到仓库、不要单独打包发给别人 |
| 商业使用 | 按 Unity-Chan License 条款执行（本项目为个人练习，风险最低） |

⚠️ 如果哪天要换成**米哈游**官方那套 MMD 模型（《绝区零》18 体）：那是**非商用 + 明确禁止再分发**，
同样只能放本地、**不能进仓库**，而且要先做 PMX → FBX → Humanoid 的转换（详见 `Docs/新项目文档/R002-走路动画/01-技术预研.md` 的对比表）。
