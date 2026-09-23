// ============================================================================
//  ChineseFontSetupMenu —— 中文显示：把字体做成 TMP 字体资产，并接上
//  项目：3D联网战斗Demo   对应：M2-D 后续（中文乱码修复）、Docs\24-中文字体接入.md
//
//  ---------------------------------------------------------------------------
//  为什么必须有这一步（不是"记得配一下"那么简单）
//  ---------------------------------------------------------------------------
//  TextMeshPro 用的是**预烘焙的 SDF 字体资产**，而不是系统字体：
//  默认那个 `LiberationSans SDF` **只有拉丁字形** —— 中文在它里面**根本不存在**，
//  于是渲染成一排方框（俗称"豆腐块"）。这不是编码问题，也不是乱码，
//  而是**字体里没有那个字形**。
//
//  解决办法两条路：
//    · **Static（预烘焙）**：在 Font Asset Creator 里把用到的字全选出来烤进图集。
//      缺点是"字表之外的字"照样是方框 —— 而本项目的任务名/描述来自**配置表**（策划会改），
//      字表必然会变，所以这条路对本项目是**错的**。
//    · **Dynamic（动态）**：给一个源字体文件，TMP **按需把字形加进图集**。
//      ✅ 本项目选它（负责人的另一个工程 RPG2 也是这么配的，实测 `m_AtlasPopulationMode: 1`）。
//
//  ---------------------------------------------------------------------------
//  两个菜单，各自解决一半问题（**这一步最容易被漏**）
//  ---------------------------------------------------------------------------
//     ① 生成字体资产            —— 造出 `<字体名> SDF.asset`
//     ② 设为默认 + 加为兜底字体  —— **让已有的文字也变中文**
//
//  ⚠️ 为什么 ② 要做两件事：TMP 的文字组件上**存着一份字体资产的引用**。
//     只设"默认字体"**只影响以后新建的组件**，已经摆好的那几个仍然指着 LiberationSans。
//     把它们逐个改一遍很烦，所以顺手把我们的字体**加进 LiberationSans 的兜底列表** ——
//     于是 TMP 遇到 LiberationSans 里没有的字形时会**自动去兜底字体里找**，
//     已有预制体**一个字都不用动**。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 已知局限（如实记）
//  ---------------------------------------------------------------------------
//  · 动态模式要求**源字体文件必须在包里**（图集是按需生成的）→ 那 14.9 MB 的 .otf 会进包。
//    要省包体的话，正解是"只保留用到的字符做子集化"，那是打包优化阶段（M5）的事
//  · 图集 2048×2048 + 允许多图集：**字形缓存会随运行增长**（中文常用字约 3500 个）。
//    图集满了 TMP 会再开一张（多图集），不会丢字，但显存会涨
//  · 本脚本我**没法在 EditMode 里验证**（它调的是 TMP 的编辑器侧 API）——
//    所以菜单里每一步都打了日志，跑完看 Console 就知道成没成；
//    真出问题可以退回官方的 `Window → TextMeshPro → Font Asset Creator`（见 Docs\24）
// ============================================================================

using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace NBC.EditorTools
{
    /// <summary>中文字体接入（TMP 字体资产生成与接线）。</summary>
    public static class ChineseFontSetupMenu
    {
        /// <summary>
        /// 源字体（仓库里那份；见同目录的 `README-字体来源与授权.txt`）。
        /// <para>
        /// ⚠️ **必须是 `.ttf`，不能用 `.otf`**（2026-09-23 实测踩到两次）：
        /// ① 第一版用 `.otf`（14.9 MB）：字体资产建出来了、默认字体与兜底也都接上了，
        ///    但 TMP **一个字形都没加进去**（`m_UsedGlyphRects` 一直空）→ 屏幕上是方框。
        ///    TMP 的动态字形加载走 FreeType，**对 OTF（CFF 轮廓）支持不佳**。
        /// ② 换成 `.ttf` 后用本脚本的 `CreateFontAsset` 生成，**图集是空的**
        ///    （`Texture2D.m_CompleteImageSize: 0`、宽高 0）→ 屏幕上一个字都不显示。
        ///    所以现在**不再用脚本生成**，改用一份**经验证可用**的资产（见 <see cref="OutputPath"/>）。
        /// </para>
        /// </summary>
        public const string SourceFontPath = "Assets/_Project/Art/Fonts/AlibabaPuHuiTi-3-55-Regular.ttf";

        /// <summary>生成的 TMP 字体资产放在哪。</summary>
        public const string OutputFolder = "Assets/_Project/Art/Fonts";

        /// <summary>
        /// TMP 字体资产路径。
        /// <para>
        /// ⚠️ **这份不是本脚本生成的**，而是从负责人另一个工程 RPG2
        /// （`Assets/Fonts/AlibabaPuHuiTi-3-55-Regular/`）**整份搬过来的**，
        /// 连 `.meta` 一起搬所以 GUID 不变、里面的源字体引用也不会断。
        /// 它已经被验证过可用：Dynamic 模式 + 1024² 图集 + **图集里真的有数据**
        /// （`m_CompleteImageSize: 1048576`）+ 71 个已烘焙字形。
        /// </para>
        /// <para>仓库里只放这一份；脚本 ① 只在"资产不存在"时才去生成。</para>
        /// </summary>
        public const string OutputPath = OutputFolder + "/AlibabaPuHuiTi-3-55-Regular SDF.asset";

        /// <summary>TMP 自带的拉丁字体（要往它的兜底列表里塞我们的中文）。</summary>
        private const string LatinFontAssetPath =
            "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";

        /// <summary>TMP 的设置资产（默认字体写在它里面）。</summary>
        private const string TmpSettingsPath = "Assets/TextMesh Pro/Resources/TMP Settings.asset";

        /// <summary>采样点大小（中文建议 60~90；太大地图集装不下几个字）。</summary>
        private const int SamplingPointSize = 64;

        /// <summary>字形间距（SDF 需要留边，太小会出现描边断裂）。</summary>
        private const int AtlasPadding = 5;

        /// <summary>图集边长（2048 起步；不够 TMP 会自动再开一张）。</summary>
        private const int AtlasSize = 2048;

        /// <summary>① 生成中文字体资产（Dynamic 模式）。**资产已存在时不重建**。</summary>
        [MenuItem("Tools/NBC/UI/① 生成中文字体资产（TMP，Dynamic）")]
        private static void CreateFontAsset()
        {
            // ⚠️ 已经有一份可用资产时**不要重建**：仓库里那份是从 RPG2 搬来的、经验证可用；
            //    而本脚本的生成路径实测会产出"图集为空"的资产（见 SourceFontPath 上的说明）。
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

            // ⚠️ 先删旧资产：同一个路径已经存在资产时，`CreateAsset` 不一定会安静覆盖
            //    （而且换源字体必须重建 —— 否则会留下一个"指着旧字体"的资产）
            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(OutputPath) != null)
            {
                AssetDatabase.DeleteAsset(OutputPath);
            }

            AssetDatabase.CreateAsset(asset, OutputPath);

            // ⚠️ 图集与材质必须**作为子资产**存进同一个文件 —— 否则重新打开工程后引用会丢，
            //    字体资产变成"没有贴图的字体"（现象是全部不出字）。
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

        /// <summary>② 设为 TMP 默认字体，并加进拉丁字体的兜底列表。</summary>
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

            // -------- ① 设为 TMP 的默认字体（影响**以后新建**的 TMP 组件）--------
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

            // -------- ② 加进拉丁字体的兜底列表（影响**已经摆好**的文字）--------
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

            // -------- ③ 项目级兜底列表（TMP Settings）--------
            //  ⚠️ 前面那两条管的是"某个字体资产/新建组件"，而 `TMP_Settings.fallbackFontAssets`
            //     是**项目级**兜底：任何 TMP 文字找不到字形时都会来这里找。三条一起做才不留缝。
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
