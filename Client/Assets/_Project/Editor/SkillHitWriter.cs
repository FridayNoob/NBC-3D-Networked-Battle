// 这个文件里有：
//   SkillHitWriter（静态类）：把动画片段上的 OnSkillHit 事件时间，换算成逻辑帧并写回 Skill.csv 的 hitFrames 列。
//   SkillHitRow（结构体）：扫描结果的一行 —— 技能编号、片段名、播放速度、事件时间、换算后的帧、是否冲突。
//   SkillClipSpeed（结构体）：片段 -> 控制器的播放速度映射，换算帧数时必须用它。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace NBC.Editor
{
    // 主要功能：扫描工程里的动画片段与控制器，把「OnSkillHit(技能编号)」事件按 30Hz 逻辑帧换算成 hitFrames 文本，
    //           并写回 Configs/Design/Skill.csv，供服务端按帧结算。
    // 主要数据成员：ClipsRoot（扫描根目录）、SkillCsvPath（目标表）、LogicTickRate（逻辑帧率，默认 30）。
    // 主要外部方法：Collect（扫描）、BuildCsvCells（生成某一技能该写的文本）、WriteBack（写文件）。
    // 谁在用它：AnimationEventEditorWindow 的「技能命中帧」一节；负责人加完事件点一下写回即可，不必手数帧。
    // 注意：事件时间用片段自己的时间轴（秒），片段若在控制器里加速播放，真实耗时 = 秒 / 速度，
    //       所以换算公式是 round(秒 × 30 / 速度) —— 速度 1.5 表示播得更快、帧号更小。
    internal static class SkillHitWriter
    {
        // 扫描动画片段的根目录（角色动画都放在这里，新增角色无需改代码）
        internal const string ClipsRoot = "Assets";

        // 目标配置表：相对仓库根
        internal const string SkillCsvPath = "Configs/Design/Skill.csv";

        // 服务端逻辑帧率（与 NetContract.TickRate 一致，这里写成常量避免编辑器引用运行时程序集）
        internal const int LogicTickRate = 30;

        // 承运的技能事件函数名（必须是 PlayerAnimationEvents 上的方法名）
        internal const string SkillHitFunction = "OnSkillHit";

        // 功能：扫描工程里所有带 OnSkillHit 的动画片段，换算成每个技能的命中帧。
        // 入参：无
        // 返回：扫描结果列表（可能为空）
        // 步骤：
        //   1) 遍历所有 .anim 与 .fbx，取出其中的 AnimationClip
        //   2) 从控制器里找每个片段对应的状态速度（找不到按 1 倍速）
        //   3) 逐个事件换算帧号；同一技能出现在多个片段时标记冲突
        // 上游：AnimationEventEditorWindow 的「扫描」按钮
        // 下游：WriteBack / OnGUI 的列表
        internal static List<SkillHitRow> Collect()
        {
            var rows = new List<SkillHitRow>();
            Dictionary<AnimationClip, float> speeds = CollectSpeeds();
            Dictionary<string, int> clipCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (string path in AssetDatabase.GetAllAssetPaths())
            {
                if (!path.StartsWith(ClipsRoot + "/", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) &&
                    !path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);

                foreach (UnityEngine.Object asset in assets)
                {
                    AnimationClip clip = asset as AnimationClip;

                    if (clip == null || string.IsNullOrEmpty(clip.name))
                    {
                        continue;
                    }

                    AnimationEvent[] events = UnityEditor.AnimationUtility.GetAnimationEvents(clip);
                    List<float> times = new List<float>();
                    int skillId = 0;

                    foreach (AnimationEvent item in events)
                    {
                        if (!string.Equals(item.functionName, SkillHitFunction, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        times.Add(item.time);

                        if (item.intParameter > 0)
                        {
                            skillId = item.intParameter;
                        }
                    }

                    if (times.Count == 0)
                    {
                        continue;
                    }

                    times.Sort();
                    float speed = 1f;

                    if (!speeds.TryGetValue(clip, out speed) || speed <= 0.01f)
                    {
                        speed = 1f;
                    }

                    var frames = new List<int>();

                    foreach (float time in times)
                    {
                        int frame = Mathf.RoundToInt(time * LogicTickRate / speed);
                        frames.Add(frame < 1 ? 1 : frame);
                    }

                    if (skillId <= 0)
                    {
                        rows.Add(new SkillHitRow(0, clip.name, path, speed, times, frames, "事件的 int 参数没填技能编号"));
                        continue;
                    }

                    string key = skillId.ToString(CultureInfo.InvariantCulture);
                    int existing;

                    if (!clipCounts.TryGetValue(key, out existing))
                    {
                        existing = 0;
                    }

                    clipCounts[key] = existing + 1;

                    string problem = existing == 0
                        ? string.Empty
                        : "同一个技能编号出现在多个片段里，先删掉多余的事件";

                    rows.Add(new SkillHitRow(skillId, clip.name, path, speed, times, frames, problem));
                }
            }

            rows.Sort((left, right) => left.SkillId.CompareTo(right.SkillId));
            return rows;
        }

        // 功能：把一条扫描结果换算成 Skill.csv 里 hitFrames 单元格的文本（单段不加引号，多段加引号）。
        // 入参：row 扫描结果
        // 返回：例如 "40" 或 "\"40,60,80\""；无帧返回空串
        // 步骤：1) 帧号去重升序 2) 拼逗号串 3) 多于一段才加引号（与配置表导出的写法一致）
        // 上游：AnimationEventEditorWindow 的「写回」按钮
        // 下游：WriteBack
        internal static string BuildCsvCells(SkillHitRow row)
        {
            if (row.Frames == null || row.Frames.Count == 0)
            {
                return string.Empty;
            }

            var unique = new List<int>();

            foreach (int frame in row.Frames)
            {
                if (!unique.Contains(frame))
                {
                    unique.Add(frame);
                }
            }

            unique.Sort();

            var text = new StringBuilder();

            for (int i = 0; i < unique.Count; i++)
            {
                if (i > 0)
                {
                    text.Append(',');
                }

                text.Append(unique[i].ToString(CultureInfo.InvariantCulture));
            }

            // 只返回单元格内容，不加引号 —— 加引号是 JoinCsvLine 的事，这里再加一次会变成三引号。
            return text.ToString();
        }

        // 功能：把扫描结果写回 Skill.csv 的 hitFrames 列，其余内容与 BOM 保持原样。
        // 入参：rows 扫描结果；dryRun 为 true 时只算不写
        // 返回：变更条数；失败时通过 message 说明原因
        // 步骤：
        //   1) 定位仓库根下的 Skill.csv，按 UTF-8（保 BOM）读整份文本
        //   2) 逐行找首列等于技能编号的数据行，替换第 5 列
        //   3) 有冲突或找不到技能时跳过并记录，最后统一返回
        // 上游：AnimationEventEditorWindow 的「写回 Skill.csv」
        // 下游：File.WriteAllText（UTF-8 带 BOM，供 ConfigKit 读取）
        internal static int WriteBack(List<SkillHitRow> rows, bool dryRun, out string message)
        {
            message = string.Empty;

            if (rows == null || rows.Count == 0)
            {
                message = "没有扫描结果：先给动画片段加上 OnSkillHit 事件。";
                return 0;
            }

            string fullPath = ResolveRepoPath(SkillCsvPath);

            if (string.IsNullOrEmpty(fullPath))
            {
                message = "找不到 " + SkillCsvPath + "（写回需要仓库根目录里有 Configs\\Design）。";
                return 0;
            }

            byte[] bytes = System.IO.File.ReadAllBytes(fullPath);
            bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            string text = new UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            var changes = new List<string>();
            var problems = new List<string>();
            int changed = 0;

            foreach (SkillHitRow row in rows)
            {
                if (row.SkillId <= 0)
                {
                    problems.Add(row.ClipName + "：" + row.Problem);
                    continue;
                }

                if (!string.IsNullOrEmpty(row.Problem))
                {
                    problems.Add(row.ClipName + "：" + row.Problem);
                    continue;
                }

                string cells = BuildCsvCells(row);

                if (string.IsNullOrEmpty(cells))
                {
                    continue;
                }

                bool found = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    string[] fields = SplitCsvLine(lines[i]);

                    if (fields.Length < 5 || !string.Equals(fields[0].Trim(), row.SkillId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                    {
                        continue;
                    }

                    found = true;

                    if (string.Equals(fields[4], cells, StringComparison.Ordinal))
                    {
                        changes.Add(row.SkillId + " 已经是 " + cells + "（未改）");
                        break;
                    }

                    changes.Add(row.SkillId + "：" + fields[4] + " → " + cells);
                    fields[4] = cells;
                    lines[i] = JoinCsvLine(fields);
                    changed++;
                    break;
                }

                if (!found)
                {
                    problems.Add("Skill.csv 里没有技能 " + row.SkillId);
                }
            }

            if (!dryRun && changed > 0)
            {
                string output = string.Join("\n", lines).TrimEnd('\n') + "\n";
                System.IO.File.WriteAllText(fullPath, output, new UTF8Encoding(hasBom));
                AssetDatabase.Refresh();
            }

            var report = new StringBuilder();

            foreach (string item in changes)
            {
                report.AppendLine(item);
            }

            if (problems.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("跳过：");

                foreach (string item in problems)
                {
                    report.AppendLine(item);
                }
            }

            message = report.ToString().TrimEnd();
            return changed;
        }

        // 功能：从控制器里收集「片段 → 播放速度」，用于把片段时间换算成真实逻辑帧。
        // 入参：无
        // 返回：字典；同一个片段被多个状态引用时取第一个非 0 速度
        // 步骤：1) 找所有 AnimatorController 2) 递归遍历状态机 3) 记下 state.speed
        // 上游：Collect
        // 下游：无
        private static Dictionary<AnimationClip, float> CollectSpeeds()
        {
            var speeds = new Dictionary<AnimationClip, float>();

            foreach (string guid in AssetDatabase.FindAssets("t:AnimatorController"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);

                if (path.StartsWith("Assets/", StringComparison.Ordinal) == false)
                {
                    continue;
                }

                AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);

                if (controller == null)
                {
                    continue;
                }

                foreach (AnimatorControllerLayer layer in controller.layers)
                {
                    CollectStateSpeeds(layer.stateMachine, speeds, 0);
                }
            }

            return speeds;
        }

        // 功能：递归遍历一个状态机，把状态上的片段与速度记进字典。
        // 入参：machine 状态机；speeds 结果字典；depth 递归深度（防御异常层级）
        // 返回：无
        // 步骤：1) 遍历本层状态 2) 有 motion 就记速度（已有则保留先记的） 3) 递归子状态机
        // 上游：CollectSpeeds
        // 下游：CollectStateSpeeds（自身递归）
        private static void CollectStateSpeeds(AnimatorStateMachine machine, Dictionary<AnimationClip, float> speeds, int depth)
        {
            if (machine == null || depth > 8)
            {
                return;
            }

            foreach (ChildAnimatorState child in machine.states)
            {
                AnimatorState state = child.state;

                if (state == null || state.motion == null)
                {
                    continue;
                }

                AnimationClip clip = state.motion as AnimationClip;

                if (clip == null || speeds.ContainsKey(clip))
                {
                    continue;
                }

                speeds[clip] = state.speed <= 0.01f ? 1f : state.speed;
            }

            foreach (ChildAnimatorStateMachine sub in machine.stateMachines)
            {
                CollectStateSpeeds(sub.stateMachine, speeds, depth + 1);
            }
        }

        // 功能：从工程目录往上找，定位仓库根下的相对路径。
        // 入参：relativePath 相对仓库根的路径（带 / 或 \）
        // 返回：完整路径；找不到返回空串
        // 步骤：1) 从 Assets 目录逐级上溯 2) 找到 Configs\Design 就当仓库根
        // 上游：WriteBack
        // 下游：无
        private static string ResolveRepoPath(string relativePath)
        {
            var dir = new System.IO.DirectoryInfo(Application.dataPath);

            for (int i = 0; i < 6; i++)
            {
                if (dir == null)
                {
                    break;
                }

                if (System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "Configs", "Design")))
                {
                    string candidate = System.IO.Path.Combine(dir.FullName, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    return System.IO.File.Exists(candidate) ? candidate : string.Empty;
                }

                dir = dir.Parent;
            }

            return string.Empty;
        }

        // 功能：按 CSV 规则切一行（支持单元格里的引号与逗号）。
        // 入参：line 一整行
        // 返回：字段数组
        // 步骤：1) 逐字符扫 2) 引号内的逗号不当分隔 3) 两个引号表示一个引号
        // 上游：WriteBack
        // 下游：JoinCsvLine
        private static string[] SplitCsvLine(string line)
        {
            var fields = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            cell.Append('"');
                            i++;
                        }
                        else
                        {
                            quoted = false;
                        }
                    }
                    else
                    {
                        cell.Append(c);
                    }

                    continue;
                }

                if (c == '"')
                {
                    quoted = true;
                }
                else if (c == ',')
                {
                    fields.Add(cell.ToString());
                    cell.Length = 0;
                }
                else
                {
                    cell.Append(c);
                }
            }

            fields.Add(cell.ToString());
            return fields.ToArray();
        }

        // 功能：把字段拼回一行；含逗号、引号或首尾空白的字段加引号。
        // 入参：fields 字段数组
        // 返回：CSV 行
        // 步骤：1) 逐个判断是否需要加引号 2) 引号内的引号写成两个 3) 逗号连接
        // 上游：WriteBack
        // 下游：无
        private static string JoinCsvLine(string[] fields)
        {
            var line = new StringBuilder();

            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0)
                {
                    line.Append(',');
                }

                string field = fields[i] ?? string.Empty;

                if (field.IndexOf(',') >= 0 || field.IndexOf('"') >= 0 || field.Trim().Length != field.Length)
                {
                    line.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
                }
                else
                {
                    line.Append(field);
                }
            }

            return line.ToString();
        }
    }

    // 主要功能：描述「一个技能片段上的命中事件」——技能编号、片段名、速度、事件时间、换算帧、问题说明。
    // 主要数据成员：SkillId（技能编号，来自事件 int 参数）、ClipName、ClipPath、Speed、Times（秒）、Frames（逻辑帧）、Problem。
    // 主要外部方法：无（纯数据）。
    // 谁在用它：SkillHitWriter.Collect 的返回值、AnimationEventEditorWindow 的列表。
    internal readonly struct SkillHitRow
    {
        // 技能编号（2001 等）；0 表示事件没填
        internal readonly int SkillId;

        // 动画片段名（给人看）
        internal readonly string ClipName;

        // 片段资源路径（排查时用）
        internal readonly string ClipPath;

        // 该片段在控制器里的播放速度（1.5 表示快放）
        internal readonly float Speed;

        // 事件时间，单位秒（片段时间轴）
        internal readonly List<float> Times;

        // 换算后的逻辑帧（30Hz）
        internal readonly List<int> Frames;

        // 不能写回的原因（空串表示可以写）
        internal readonly string Problem;

        internal SkillHitRow(int skillId, string clipName, string clipPath, float speed, List<float> times, List<int> frames, string problem)
        {
            SkillId = skillId;
            ClipName = clipName;
            ClipPath = clipPath;
            Speed = speed;
            Times = times;
            Frames = frames;
            Problem = problem ?? string.Empty;
        }
    }
}
