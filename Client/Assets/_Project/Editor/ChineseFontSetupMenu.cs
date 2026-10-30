// 这个文件里有：
//   ChineseFontSetupMenu（静态类）：TMP 中文接入的编辑器菜单，负责生成并检查字体资产、设默认字体、加兜底
// 背景与已知局限见 Docs\24-中文字体接入.md。

using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace NBC.EditorTools
{
    // 主要功能：两个编辑器菜单，1) 检查或生成中文 TMP 动态字体资产，2) 把中文字体接成默认字体与三层兜底。
    // 主要数据成员：SourceFontPath（源 ttf 的工程内路径）、OutputPath（字体资产路径）、SamplingPointSize / AtlasPadding / AtlasSize（图集参数，单位 px）。
    // 主要外部方法：没有 public 方法，入口是两个 [MenuItem] 私有方法 CreateFontAsset / SetAsDefaultAndFallback。
    // 谁在用它：只由 Unity 菜单 Tools/NBC/UI 手动触发，本程序集内没有代码调用。
    public static class ChineseFontSetupMenu
    {
        public const string SourceFontPath = "Assets/_Project/Art/Fonts/AlibabaPuHuiTi-3-55-Regular.ttf";   // 源字体在工程里的相对路径；注意：必须是 .ttf，换成 .otf 时 TMP 的动态字形加载不出字形，屏幕全是方框

        public const string OutputFolder = "Assets/_Project/Art/Fonts";   // 字体资产输出目录，工程内相对路径，不存在时由生成流程创建

        public const string OutputPath = OutputFolder + "/AlibabaPuHuiTi-3-55-Regular SDF.asset";   // TMP 字体资产路径；注意：仓库里这份是从其它工程整份搬来的现成资产，不是本脚本生成的

        private const string LatinFontAssetPath =
            "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";   // TMP 自带的拉丁字体资产路径，只往它的兜底列表里加中文字体

        private const string TmpSettingsPath = "Assets/TextMesh Pro/Resources/TMP Settings.asset";   // TMP 设置资产路径，默认字体与项目级兜底都存在它里面

        private const int SamplingPointSize = 64;   // 采样点大小，单位 px；中文建议 60~90，太大则图集装不下几个字

        private const int AtlasPadding = 5;   // 字形间距，单位 px，SDF 留边用；太小会出现描边断裂

        private const int AtlasSize = 2048;   // 图集边长，单位 px；2048 起步，装满后 TMP 会自动再开一张

        // 功能：生成 Dynamic 模式的中文字体资产写到 OutputPath，资产已存在时直接跳过。
        // 入参：无（菜单项，路径取自 SourceFontPath / OutputFolder / OutputPath）。
        // 返回：无；失败只打日志，不抛异常。
        // 步骤：
        //   1) 先看 OutputPath 有没有资产，有就直接返回 —— 本脚本的生成路径实测会产出图集为空的资产，不如用现成那份
        //   2) 加载源字体，缺了就报错退出（多半是 Unity 还没导入完）
        //   3) 按 Dynamic + 2048 图集 + 64 采样造资产，返回 null 就提示改用官方 Font Asset Creator
        //   4) 先删同路径旧资产再 CreateAsset，否则会留下一个指着旧字体的资产
        //   5) 图集与材质要作为子资产写进同一个文件，否则重开工程引用会丢，字全部不显示
        //   6) SetDirty + SaveAssets + Refresh 落盘，并在日志里提示接着跑 ②
        // 注意：动态模式要求源字体文件随包发布，图集显存会随字形增长（中文常用字约 3500 个）；这套生成路径在 EditMode 里没法验证，所以每步都打日志，跑完看 Console。
        // 上游：Unity 菜单 Tools/NBC/UI/①
        // 下游：TMP_FontAsset.CreateFontAsset、AssetDatabase 与 TMP 的编辑器侧写入
        [MenuItem("Tools/NBC/UI/① 生成中文字体资产（TMP，Dynamic）")]
        private static void CreateFontAsset()
        {
            TMP_FontAsset existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(OutputPath);

            if (existing != null)
            {
                Debug.Log("[中文字体] 已经有可用的字体资产，**不需要重建**：\n  " + OutputPath + "\n" +
                          "  （图集状态：模式 " + existing.atlasPopulationMode + "）\n" +
                          "直接跑 `Tools/NBC/UI/② 设为 TMP 默认字体 + 加为兜底字体` 即可。\n" +
                          "⚠️ 真的要重建：先手动删掉那个 .asset，再点本菜单。");
                return;
            }

            Font source = AssetDatabase.LoadAssetAtPath<Font>(SourceFontPath);

            if (source == null)
            {
                Debug.LogError(
                    "[中文字体] 找不到源字体：\n  " + SourceFontPath + "\n" +
                    "请确认文件在工程里（`Assets/_Project/Art/Fonts/` 下应当有那份 .otf），" +
                    "并且 Unity 已经导入完（导入中会有一个短暂的进度条）。");
                return;
            }

            TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(
                source, SamplingPointSize, AtlasPadding, GlyphRenderMode.SDFAA,
                AtlasSize, AtlasSize, AtlasPopulationMode.Dynamic, true);

            if (asset == null)
            {
                Debug.LogError("[中文字体] TMP 没能从 " + SourceFontPath + " 造出字体资产（CreateFontAsset 返回 null）。\n" +
                               "可以改用官方窗口：`Window → TextMeshPro → Font Asset Creator`（步骤见 Docs\\24）。");
                return;
            }

            asset.name = Path.GetFileNameWithoutExtension(OutputPath);

            Directory.CreateDirectory(OutputFolder);

            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(OutputPath) != null)
            {
                AssetDatabase.DeleteAsset(OutputPath);
            }

            AssetDatabase.CreateAsset(asset, OutputPath);

            if (asset.atlasTextures != null && asset.atlasTextures.Length > 0 && asset.atlasTextures[0] != null)
            {
                asset.atlasTextures[0].name = asset.name + " Atlas";
                AssetDatabase.AddObjectToAsset(asset.atlasTextures[0], asset);
            }

            if (asset.material != null)
            {
                asset.material.name = asset.name + " Material";
                AssetDatabase.AddObjectToAsset(asset.material, asset);
            }

            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(
                "[中文字体] ✅ 已生成：" + OutputPath + "\n" +
                "  源字体 = " + SourceFontPath + "（**必须是 .ttf**，见该常量上的说明）\n" +
                "  模式 = Dynamic（按需加字形，配置表里的新字也能显示）\n" +
                "  图集 = " + AtlasSize + "×" + AtlasSize + "，采样 " + SamplingPointSize + "px，允许多图集\n" +
                "下一步：跑 `Tools/NBC/UI/② 设为 TMP 默认字体 + 加为兜底字体`，" +
                "**已有的文字才会一起变中文**（资产是重建的，引用要重接）。");
        }

        // 功能：把中文字体接成 TMP 默认字体与三层兜底，让新建和已摆好的文字都用上中文字形。
        // 入参：无（菜单项，路径取自 OutputPath / TmpSettingsPath / LatinFontAssetPath）。
        // 返回：无；缺资产或字段时打警告并跳过对应那一步，不抛异常。
        // 步骤：
        //   1) 先确认 OutputPath 有资产，没有就报错并提示先跑 ①，后面每一步都靠它
        //   2) 写 TMP Settings 的 m_defaultFontAsset —— 这只影响以后新建的 TMP 组件
        //   3) 再把中文字体加进 LiberationSans 的兜底列表，已有的预制体一个字都不用动
        //   4) 最后往 TMP Settings 的 m_fallbackFontAssets 加项目级兜底，三层齐了才不留缝
        //   5) 只有真改过才 SaveAssets，避免无谓的资产重写
        // 注意：某一层缺资产或字段时只跳过那一步，其它层照做；兜底里已有的会跳过，菜单可以反复点。
        // 上游：Unity 菜单 Tools/NBC/UI/②
        // 下游：SerializedObject 以及 TMP_Settings / TMP_FontAsset 的资产写入
        [MenuItem("Tools/NBC/UI/② 设为 TMP 默认字体 + 加为兜底字体")]
        private static void SetAsDefaultAndFallback()
        {
            TMP_FontAsset chinese = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(OutputPath);

            if (chinese == null)
            {
                Debug.LogError(
                    "[中文字体] 还没生成字体资产：\n  " + OutputPath + "\n" +
                    "先跑一次 `Tools/NBC/UI/① 生成中文字体资产（TMP，Dynamic）`。");
                return;
            }

            bool changed = false;

            Object settings = AssetDatabase.LoadAssetAtPath<Object>(TmpSettingsPath);

            if (settings == null)
            {
                Debug.LogWarning("[中文字体] 找不到 TMP Settings（" + TmpSettingsPath + "），跳过\"设为默认\"。\n" +
                                 "（那份资产由 TMP 自己生成；缺了就先用兜底字体那条路，效果一样。）");
            }
            else
            {
                SerializedObject serialized = new SerializedObject(settings);
                SerializedProperty property = serialized.FindProperty("m_defaultFontAsset");

                if (property == null)
                {
                    Debug.LogWarning("[中文字体] TMP Settings 里没有 `m_defaultFontAsset` 字段（TMP 版本不同？），跳过。");
                }
                else
                {
                    property.objectReferenceValue = chinese;
                    serialized.ApplyModifiedProperties();
                    EditorUtility.SetDirty(settings);
                    changed = true;
                    Debug.Log("[中文字体] ✅ 已设为 TMP 默认字体（以后新建的文字默认用它）。");
                }
            }

            TMP_FontAsset latin = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(LatinFontAssetPath);

            if (latin == null)
            {
                Debug.LogWarning("[中文字体] 找不到 " + LatinFontAssetPath + "，跳过\"加兜底\"。");
            }
            else
            {
                if (latin.fallbackFontAssetTable == null)
                {
                    latin.fallbackFontAssetTable = new System.Collections.Generic.List<TMP_FontAsset>();
                }

                if (latin.fallbackFontAssetTable.Contains(chinese))
                {
                    Debug.Log("[中文字体] 兜底列表里已经有它了，不重复加。");
                }
                else
                {
                    latin.fallbackFontAssetTable.Add(chinese);
                    EditorUtility.SetDirty(latin);
                    changed = true;
                    Debug.Log("[中文字体] ✅ 已把中文字体加进 LiberationSans 的**兜底列表** —— " +
                              "已有预制体里的文字**不用改**，缺字形时会自动来这里找。");
                }
            }

            if (changed)
            {
                AssetDatabase.SaveAssets();
            }

            if (settings != null)
            {
                SerializedObject serialized = new SerializedObject(settings);
                SerializedProperty fallback = serialized.FindProperty("m_fallbackFontAssets");

                if (fallback == null || !fallback.isArray)
                {
                    Debug.LogWarning("[中文字体] TMP Settings 里没有 `m_fallbackFontAssets` 数组，跳过项目级兜底。");
                }
                else
                {
                    bool already = false;

                    for (int i = 0; i < fallback.arraySize; i++)
                    {
                        if (fallback.GetArrayElementAtIndex(i).objectReferenceValue == chinese)
                        {
                            already = true;
                            break;
                        }
                    }

                    if (already)
                    {
                        Debug.Log("[中文字体] 项目级兜底列表里已经有它了，不重复加。");
                    }
                    else
                    {
                        fallback.InsertArrayElementAtIndex(fallback.arraySize);
                        fallback.GetArrayElementAtIndex(fallback.arraySize - 1).objectReferenceValue = chinese;
                        serialized.ApplyModifiedProperties();
                        EditorUtility.SetDirty(settings);
                        changed = true;
                        Debug.Log("[中文字体] ✅ 已加进 **TMP Settings 的项目级兜底列表**。");
                    }
                }
            }

            if (changed)
            {
                AssetDatabase.SaveAssets();
            }

            Debug.Log("[中文字体] 完成。回到播放模式看一眼中文是否正常（任务名/条件描述/按钮）。\n" +
                      "⚠️ 动态模式是**按需加字形**：第一次显示某个字时会现场烤进图集，" +
                      "所以字可能有一瞬间的延迟（之后走缓存）。\n" +
                      "⚠️ 想确认它真的在加字形：看那份 .asset 里 `m_UsedGlyphRects` 是不是空数组 ——" +
                      "**空 = 一个字形都没加进去**（那就是字体文件或模式的问题，不是接线问题）。");
        }
    }
}
