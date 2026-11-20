// 这个文件里有：
//   AnimationEventEditorWindow（类）：一个编辑器窗口，专门用来给动画片段加/删事件 ——
//   免得在 Animation 窗口那几百行属性里手滑改到关键帧数值。

using System;
using System.Collections.Generic;
using System.Reflection;
using NBC.Game.Player;
using UnityEditor;
using UnityEngine;

namespace NBC.Editor
{
    // 主要功能：列出选中动画片段的所有事件，并支持按「时间 + 函数名 + 参数」新增与逐条删除。
    // 主要数据成员：m_clip（当前编辑的片段）、m_timeSeconds（新事件时间，单位秒）、m_functionName（函数名）、
    //               m_intParameter / m_stringParameter / m_floatParameter（事件参数）。
    // 主要外部方法：Open（菜单入口）、OnGUI（绘制窗口）、AddEvent（写入片段）、RemoveEventAt（删除一条）。
    // 谁在用它：负责人手工给技能/攻击片段加动画事件；PlayerAnimationEvents 负责在运行时接收。
    // 注意：事件时间用的是「片段自己的时间轴」，不是真实秒数 —— 片段若在控制器里加速播放，事件会跟着加速。
    public sealed class AnimationEventEditorWindow : EditorWindow
    {
        // 候选函数名：从 PlayerAnimationEvents 里反射出来的、以 On 开头的公开方法
        private static readonly string[] FallbackFunctionNames =
        {
            "OnSkillStart",
            "OnSkillHit",
            "OnAttackHit",
            "OnFootstep"
        };

        // 当前编辑的动画片段（为空时窗口只显示提示）
        private AnimationClip m_clip;

        // 新事件的时间，单位秒（相对片段开头）
        private float m_timeSeconds;

        // 新事件的函数名（必须和接收脚本上的公开方法同名）
        private string m_functionName = "OnSkillHit";

        // 新事件的整型参数（技能序号 1~3、连击段数 1~3）
        private int m_intParameter = 1;

        // 新事件的字符串参数（本项目暂未使用）
        private string m_stringParameter = string.Empty;

        // 新事件的浮点参数（本项目暂未使用）
        private float m_floatParameter;

        // 窗口滚动位置
        private Vector2 m_scroll;

        // 预览用的目标物体（要拖入带 Animator 的模型，否则没有可采样的骨架）
        private GameObject m_previewTarget;

        // 预览时间，单位秒（拖动滑条时实时采样）
        private float m_previewTime;

        // 预览时假定的帧率，用来把秒换算成"第几帧"给人看
        private const float PreviewFrameRate = 30f;

        // 是否已经进入 AnimationMode（退出窗口时要还原场景姿势）
        private bool m_previewing;

        // 预览开始时记下的局部变换（AnimationMode 只在部分情况下自动还原，这里自己兜底）
        private Transform[] m_snapshotTransforms;

        // 预览开始时的局部位置
        private Vector3[] m_snapshotPositions;

        // 预览开始时的局部旋转
        private Quaternion[] m_snapshotRotations;

        // 预览开始时的局部缩放
        private Vector3[] m_snapshotScales;

        // 功能：菜单入口，打开窗口。
        // 入参：无
        // 返回：无
        // 步骤：1) 创建窗口 2) 给标题 3) 若当前选中了动画片段就直接带进来
        // 上游：Unity 菜单 Tools/NBC/动画/动画事件编辑器
        // 下游：OnGUI
        [MenuItem("Tools/NBC/动画/动画事件编辑器", priority = 230)]
        private static void Open()
        {
            AnimationEventEditorWindow window = GetWindow<AnimationEventEditorWindow>("动画事件");
            window.minSize = new Vector2(460f, 320f);
            window.TryUseSelection();
            window.Show();
        }

        // 功能：绘制整个窗口。
        // 入参：无
        // 返回：无
        // 步骤：1) 选片段 2) 显示片段信息 3) 画新增区 4) 画事件列表（可滚动）
        // 上游：Unity 编辑器每帧重绘
        // 下游：AddEvent / RemoveEventAt
        private void OnGUI()
        {
            DrawClipField();

            if (m_clip == null)
            {
                EditorGUILayout.HelpBox("选一个动画片段（.anim），或者先在 Project 里选中它再打开本窗口。", MessageType.Info);
                return;
            }

            DrawClipInfo();
            EditorGUILayout.Space();
            DrawPreview();
            EditorGUILayout.Space();
            DrawAddRow();
            EditorGUILayout.Space();
            DrawEventList();
        }

        // 功能：画片段选择框。
        // 入参：无
        // 返回：无
        // 步骤：EditorGUILayout.ObjectField，限制为 AnimationClip
        // 上游：OnGUI
        // 下游：m_clip
        private void DrawClipField()
        {
            EditorGUI.BeginChangeCheck();
            AnimationClip picked = (AnimationClip)EditorGUILayout.ObjectField("动画片段", m_clip, typeof(AnimationClip), false);

            if (!EditorGUI.EndChangeCheck())
            {
                return;
            }

            m_clip = picked;
        }

        // 功能：显示片段名、长度、事件数。
        // 入参：无
        // 返回：无
        // 步骤：读 m_clip.length 与 m_clip.events.Length 后显示
        // 上游：OnGUI
        // 下游：无
        private void DrawClipInfo()
        {
            EditorGUILayout.LabelField("名称", m_clip.name);
            EditorGUILayout.LabelField("长度（秒）", m_clip.length.ToString("F2"));
            EditorGUILayout.LabelField("已有事件数", m_clip.events.Length.ToString());

            if (!m_clip.isLooping)
            {
                EditorGUILayout.LabelField("循环", "否");
            }
            else
            {
                EditorGUILayout.LabelField("循环", "是");
            }
        }

        // 功能：画"预览带"——把片段在指定时间采一次样，直接看 Scene 视图里那一帧的姿势。
        // 入参：无
        // 返回：无
        // 步骤：1) 选目标物体 2) 时间滑条显示秒与帧号 3) 上一帧/下一帧微调 4) 拖动即采样 5) 点按钮把时间填给新增区
        // 上游：OnGUI
        // 下游：SamplePreview / StopPreview
        private void DrawPreview()
        {
            EditorGUILayout.LabelField("预览（拖滑条看画面，再锁定时间）", EditorStyles.boldLabel);

            m_previewTarget = (GameObject)EditorGUILayout.ObjectField("目标物体", m_previewTarget, typeof(GameObject), true);

            if (m_previewTarget == null)
            {
                EditorGUILayout.HelpBox("把场景里带 Animator 的模型拖进来（比如 [Player_Hero] 下面的 SwordGirl_FullBody）。", MessageType.Info);
                return;
            }

            float maxTime = Mathf.Max(0f, m_clip.length);
            float picked = EditorGUILayout.Slider("预览时间（秒）", m_previewTime, 0f, maxTime);

            if (!Mathf.Approximately(picked, m_previewTime))
            {
                m_previewTime = picked;
                SamplePreview(picked);
            }

            int frame = Mathf.RoundToInt(m_previewTime * PreviewFrameRate);
            EditorGUILayout.LabelField("当前帧", frame + "（" + PreviewFrameRate.ToString("F0") + " fps 口径）");

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("上一帧"))
            {
                m_previewTime = Mathf.Max(0f, m_previewTime - 1f / PreviewFrameRate);
                SamplePreview(m_previewTime);
            }

            if (GUILayout.Button("下一帧"))
            {
                m_previewTime = Mathf.Min(maxTime, m_previewTime + 1f / PreviewFrameRate);
                SamplePreview(m_previewTime);
            }

            if (GUILayout.Button("按 2 秒看"))
            {
                m_previewTime = Mathf.Min(2f, maxTime);
                SamplePreview(m_previewTime);
            }

            if (GUILayout.Button("锁定此时间"))
            {
                m_timeSeconds = Mathf.Clamp(m_previewTime, 0f, maxTime);
            }

            if (GUILayout.Button("退出预览"))
            {
                StopPreview();
            }

            EditorGUILayout.EndHorizontal();
        }

        // 功能：在目标物体上采样片段到指定时间（只改编辑器里的临时姿势，不动资产）。
        // 入参：time 采样时间，单位秒
        // 返回：无
        // 步骤：1) 进 AnimationMode 2) BeginSampling 3) SampleAnimation 4) EndSampling 5) 刷 Scene 视图
        // 上游：DrawPreview
        // 下游：Scene 视图
        private void SamplePreview(float time)
        {
            if (m_clip == null || m_previewTarget == null)
            {
                return;
            }

            if (!AnimationMode.InAnimationMode())
            {
                AnimationMode.StartAnimationMode();
                m_previewing = true;
                CaptureSnapshot();
            }

            AnimationMode.BeginSampling();
            m_clip.SampleAnimation(m_previewTarget, time);
            AnimationMode.EndSampling();
            SceneView.RepaintAll();
        }

        // 功能：退出预览，把场景里被采样出来的姿势还原。
        // 入参：无
        // 返回：无
        // 步骤：1) 若在 AnimationMode 就 StopAnimationMode 2) 清标记
        // 上游：DrawPreview 的按钮 / OnDisable
        // 下游：Unity 编辑器
        private void StopPreview()
        {
            RestoreSnapshot();

            if (AnimationMode.InAnimationMode())
            {
                AnimationMode.StopAnimationMode();
            }

            m_previewing = false;
            SceneView.RepaintAll();
        }

        // 功能：进入预览前，把目标物体整棵子树的局部变换记下来。
        // 入参：无
        // 返回：无
        // 步骤：1) 取全部 Transform 2) 分别拷位置/旋转/缩放
        // 上游：SamplePreview（第一次进入预览时）
        // 下游：RestoreSnapshot
        private void CaptureSnapshot()
        {
            if (m_previewTarget == null)
            {
                return;
            }

            m_snapshotTransforms = m_previewTarget.GetComponentsInChildren<Transform>(true);
            int count = m_snapshotTransforms.Length;
            m_snapshotPositions = new Vector3[count];
            m_snapshotRotations = new Quaternion[count];
            m_snapshotScales = new Vector3[count];

            for (int i = 0; i < count; i++)
            {
                Transform item = m_snapshotTransforms[i];
                m_snapshotPositions[i] = item.localPosition;
                m_snapshotRotations[i] = item.localRotation;
                m_snapshotScales[i] = item.localScale;
            }
        }

        // 功能：把预览期间被采样改掉的局部变换还原回去。
        // 入参：无
        // 返回：无
        // 步骤：1) 没有快照就返回 2) 逐个还原（跳过已被销毁的物体） 3) 清空快照
        // 上游：StopPreview
        // 下游：场景里的 Transform
        private void RestoreSnapshot()
        {
            if (m_snapshotTransforms == null)
            {
                return;
            }

            for (int i = 0; i < m_snapshotTransforms.Length; i++)
            {
                Transform item = m_snapshotTransforms[i];

                if (item == null)
                {
                    continue;
                }

                item.localPosition = m_snapshotPositions[i];
                item.localRotation = m_snapshotRotations[i];
                item.localScale = m_snapshotScales[i];
            }

            m_snapshotTransforms = null;
            m_snapshotPositions = null;
            m_snapshotRotations = null;
            m_snapshotScales = null;
        }

        // 功能：窗口关闭时确保退出预览，避免场景残留采样姿势。
        // 入参：无
        // 返回：无
        // 步骤：调 StopPreview
        // 上游：Unity 生命周期
        // 下游：StopPreview
        private void OnDisable()
        {
            if (!m_previewing)
            {
                return;
            }

            StopPreview();
        }

        // 功能：画新增一行（时间 / 函数 / 参数 / 添加按钮）。
        // 入参：无
        // 返回：无
        // 步骤：1) 时间滑条 2) 函数名下拉（也可手填） 3) 整型参数 4) 点按钮时做校验再写
        // 上游：OnGUI
        // 下游：AddEvent
        private void DrawAddRow()
        {
            EditorGUILayout.LabelField("新增事件", EditorStyles.boldLabel);

            m_timeSeconds = EditorGUILayout.FloatField("时间（秒）", m_timeSeconds);

            string[] names = GetFunctionNames();
            int current = Array.IndexOf(names, m_functionName);

            if (current < 0)
            {
                current = 0;
            }

            int picked = EditorGUILayout.Popup("函数名", current, names);
            m_functionName = names[picked];
            m_functionName = EditorGUILayout.TextField("函数名（可手填）", m_functionName);

            m_intParameter = EditorGUILayout.IntField("Int 参数", m_intParameter);
            m_stringParameter = EditorGUILayout.TextField("String 参数", m_stringParameter);
            m_floatParameter = EditorGUILayout.FloatField("Float 参数", m_floatParameter);

            if (GUILayout.Button("添加到片段", GUILayout.Height(28f)))
            {
                AddEvent();
            }
        }

        // 功能：画事件列表，每条带一个删除按钮。
        // 入参：无
        // 返回：无
        // 步骤：1) 取排好序的事件 2) 逐条显示时间/函数/参数 3) 删除按钮立即写回片段
        // 上游：OnGUI
        // 下游：RemoveEventAt
        private void DrawEventList()
        {
            EditorGUILayout.LabelField("现有事件", EditorStyles.boldLabel);

            AnimationEvent[] events = m_clip.events;

            if (events.Length == 0)
            {
                EditorGUILayout.HelpBox("这个片段还没有事件。", MessageType.None);
                return;
            }

            m_scroll = EditorGUILayout.BeginScrollView(m_scroll, GUILayout.Height(180f));

            for (int i = 0; i < events.Length; i++)
            {
                AnimationEvent item = events[i];
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(item.time.ToString("F3") + " 秒", GUILayout.Width(90f));
                EditorGUILayout.LabelField(item.functionName + "（Int " + item.intParameter + "）");

                if (GUILayout.Button("删除", GUILayout.Width(56f)))
                {
                    RemoveEventAt(i);
                    EditorGUILayout.EndHorizontal();
                    break;
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        // 功能：把当前输入写成一条事件。
        // 入参：无（读 m_timeSeconds / m_functionName / 三个参数）
        // 返回：无
        // 步骤：1) 校验函数名非空 2) 校验时间在 0~length 之间 3) 追加到列表 4) 标记脏并存盘
        // 上游：DrawAddRow 的按钮
        // 下游：AnimationClip.events / AssetDatabase.SaveAssets
        private void AddEvent()
        {
            if (string.IsNullOrWhiteSpace(m_functionName))
            {
                EditorUtility.DisplayDialog("动画事件", "函数名不能为空。", "好");
                return;
            }

            if (m_timeSeconds < 0f || m_timeSeconds > m_clip.length)
            {
                EditorUtility.DisplayDialog("动画事件", "时间要在 0 ~ " + m_clip.length.ToString("F2") + " 秒之间。", "好");
                return;
            }

            List<AnimationEvent> list = new List<AnimationEvent>(m_clip.events);
            AnimationEvent item = new AnimationEvent();
            item.time = m_timeSeconds;
            item.functionName = m_functionName;
            item.intParameter = m_intParameter;
            item.stringParameter = m_stringParameter == null ? string.Empty : m_stringParameter;
            item.floatParameter = m_floatParameter;
            list.Add(item);
            list.Sort(CompareByTime);
            m_clip.events = list.ToArray();
            Save();

            Debug.Log("[动画事件] 已添加：" + m_clip.name + " @ " + m_timeSeconds.ToString("F3") + " 秒 → " + m_functionName);
        }

        // 功能：删除第 index 条事件。
        // 入参：index 事件下标（0 起）
        // 返回：无
        // 步骤：1) 拷成列表 2) 移除 3) 写回并排序 4) 存盘
        // 上游：DrawEventList 的删除按钮
        // 下游：AnimationClip.events / AssetDatabase.SaveAssets
        private void RemoveEventAt(int index)
        {
            List<AnimationEvent> list = new List<AnimationEvent>(m_clip.events);
            AnimationEvent removed = list[index];
            list.RemoveAt(index);
            m_clip.events = list.ToArray();
            Save();

            Debug.Log("[动画事件] 已删除：" + m_clip.name + " 的 " + removed.functionName + "（" + removed.time.ToString("F3") + " 秒）");
        }

        // 功能：事件按时间升序比较（Unity 也要求事件按时间排）。
        // 入参：left / right 两条事件
        // 返回：负数表示 left 靠前
        // 步骤：直接比较 time
        // 上游：AddEvent
        // 下游：List.Sort
        private static int CompareByTime(AnimationEvent left, AnimationEvent right)
        {
            return left.time.CompareTo(right.time);
        }

        // 功能：把片段标脏并存盘。
        // 入参：无
        // 返回：无
        // 步骤：1) SetDirty 2) SaveAssets
        // 上游：AddEvent / RemoveEventAt
        // 下游：编辑器资产库
        private void Save()
        {
            EditorUtility.SetDirty(m_clip);
            AssetDatabase.SaveAssets();
        }

        // 功能：取候选函数名（优先反射 PlayerAnimationEvents 的 On 方法）。
        // 入参：无
        // 返回：函数名数组，至少包含 FallbackFunctionNames
        // 步骤：1) 反射公开实例方法 2) 过滤 On 前缀 3) 结果为空则用兜底表
        // 上游：DrawAddRow
        // 下游：Popup
        private static string[] GetFunctionNames()
        {
            List<string> names = new List<string>();
            MethodInfo[] methods = typeof(PlayerAnimationEvents).GetMethods(BindingFlags.Public | BindingFlags.Instance);

            for (int i = 0; i < methods.Length; i++)
            {
                if (!methods[i].Name.StartsWith("On", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!names.Contains(methods[i].Name))
                {
                    names.Add(methods[i].Name);
                }
            }

            if (names.Count == 0)
            {
                return FallbackFunctionNames;
            }

            names.Sort(StringComparer.Ordinal);
            return names.ToArray();
        }

        // 功能：打开窗口时若 Project 里选中了动画片段，就自动带进来。
        // 入参：无
        // 返回：无
        // 步骤：读 Selection.activeObject 是否为 AnimationClip
        // 上游：Open
        // 下游：m_clip
        private void TryUseSelection()
        {
            AnimationClip picked = Selection.activeObject as AnimationClip;

            if (picked == null)
            {
                return;
            }

            m_clip = picked;
            m_timeSeconds = Mathf.Clamp(m_timeSeconds, 0f, picked.length);
        }
    }
}
