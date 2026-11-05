// 这个文件里有：
//   MonsterColliderTool（编辑器工具）：按 SkinnedMeshRenderer 的包围盒，批量给选中的 prefab 生成 BoxCollider + Rigidbody。

using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace NBC.Editor
{
    // 主要功能：给选中的 prefab 自动加「贴合模型、底面水平」的 BoxCollider 与 Rigidbody。
    // 主要数据成员：无（纯工具类，一次执行完就结束）。
    // 主要外部方法：BuildForSelection（菜单入口）、TryBuildForAsset（单个 prefab，便于复用与测试）。
    // 谁在用它：编辑器菜单 Tools/NBC/怪物/生成 BoxCollider（选中 prefab）；负责人配怪物 prefab 时用。
    // 注意：已有 Collider 或 Rigidbody 的 prefab 一律跳过 —— 只补缺的，绝不覆盖手工调过的模型。
    // 注意：根节点若带倾斜（FBX 的轴修正很常见），碰撞体会挂到一个反向旋转的子物体上，保证世界空间里是水平的；否则斜盒子的最低点只是「一个角」，怪物会悬空。
    public static class MonsterColliderTool
    {
        // 判定「根节点是否倾斜」的角度阈值，度；只允许绕 Y 旋转（转身），pitch/roll 超过它就当作倾斜
        private const float TiltThresholdDegrees = 0.5f;

        // 倾斜时放碰撞体的子物体名字；同名子物体存在就复用，避免重复执行时越加越多
        private const string ColliderRootName = "NBC-ColliderRoot";

        // 功能：菜单入口 —— 遍历 Project 里选中的 prefab，逐个生成碰撞体与刚体，最后打汇总。
        // 入参：无（读 Selection.objects）
        // 返回：无
        // 步骤：
        //   1) 逐个数选中对象，非 .prefab 资产记一次「跳过」
        //   2) 每个 prefab 单独 try/catch：一个失败不影响其它
        //   3) 成功/跳过都打一行日志（便于定位到具体是哪个 prefab）
        //   4) 结尾打「成功 N，跳过 M」
        // 上游：编辑器菜单
        // 下游：TryBuildForAsset
        [MenuItem("Tools/NBC/怪物/生成 BoxCollider（选中 prefab）")]
        public static void BuildForSelection()
        {
            UnityEngine.Object[] selected = Selection.objects;

            if (selected == null || selected.Length == 0)
            {
                Debug.LogWarning("[R016] 没有选中任何东西：请先在 Project 里选中 prefab。");
                return;
            }

            int built = 0;
            int skipped = 0;

            for (int i = 0; i < selected.Length; i++)
            {
                string path = AssetDatabase.GetAssetPath(selected[i]);

                if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log("[R016] " + selected[i].name + "：跳过（不是 prefab 资产）");
                    skipped++;
                    continue;
                }

                try
                {
                    string message;

                    if (TryBuildForAsset(path, out message))
                    {
                        built++;
                    }
                    else
                    {
                        skipped++;
                    }

                    Debug.Log("[R016] " + selected[i].name + "：" + message);
                }
                catch (Exception e)
                {
                    skipped++;
                    Debug.LogError("[R016] " + selected[i].name + "：处理失败 —— " + e.Message);
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[R016] 完成：成功 " + built + "，跳过 " + skipped + "。");
        }

        // 功能：给一个 prefab 资产生成碰撞体与刚体（已有则跳过）。
        // 入参：prefabPath prefab 资产路径；message 输出一句人话结果
        // 返回：true = 本次真的生成了；false = 跳过（原因在 message）
        // 步骤：
        //   1) 打开 prefab 内容
        //   2) 根上已有 Collider 或 Rigidbody ⇒ 跳过（只补缺的）
        //   3) 收集 Renderer（含隐藏子物体），跳过零尺寸的
        //   4) 求世界轴对齐包围盒（Renderer.bounds 本身就是世界轴对齐）
        //   5) 根倾斜 ⇒ 在反向旋转的子物体上放碰撞体；不倾斜 ⇒ 直接放根上
        //   6) 根上加 Rigidbody（开重力、冻结旋转、非运动学）
        // 上游：BuildForSelection
        // 下游：PrepareCollider / AddRigidbody、PrefabUtility.EditPrefabContentsScope
        // 注意：包围盒必须在**世界空间**求 —— 转到根本地再求 AABB 会跟着根一起斜，那就是怪物悬空的真正原因
        public static bool TryBuildForAsset(string prefabPath, out string message)
        {
            using (PrefabUtility.EditPrefabContentsScope scope = new PrefabUtility.EditPrefabContentsScope(prefabPath))
            {
                GameObject root = scope.prefabContentsRoot;

                bool hasCollider = root.GetComponent<Collider>() != null;
                bool hasBody = root.GetComponent<Rigidbody>() != null;

                // 两个都齐 ⇒ 已经是配好的 prefab，整体跳过（不覆盖手工调过的参数）
                if (hasCollider && hasBody)
                {
                    message = "跳过（Collider 与 Rigidbody 都已存在）";
                    return false;
                }

                Bounds worldBounds;
                int rendererCount = CollectWorldBounds(root, out worldBounds);

                if (rendererCount == 0)
                {
                    message = "跳过（没有可用的 Renderer，可能是全部未激活或尺寸为零）";
                    return false;
                }

                float sizeX = worldBounds.size.x;
                float sizeY = worldBounds.size.y;
                float sizeZ = worldBounds.size.z;
                string where;

                if (hasCollider)
                {
                    // 已有碰撞体：不动它，只补刚体
                    where = "Collider 已存在（未改动），只补了 Rigidbody";
                }
                else if (IsTilted(root))
                {
                    GameObject holder = EnsureHorizontalHolder(root);
                    PrepareCollider(holder, worldBounds);
                    where = "BoxCollider 挂在 " + ColliderRootName + "（根节点带倾斜，保证底面水平）";
                }
                else
                {
                    PrepareCollider(root, worldBounds);
                    where = "BoxCollider 在根节点";
                }

                if (!hasBody)
                {
                    AddRigidbody(root);
                }

                string hint = string.Empty;

                if (hasBody && !root.GetComponent<Rigidbody>().freezeRotation)
                {
                    hint = "；注意：已有刚体的 freezeRotation=false，落地可能被撞翻，建议勾上";
                }

                message = "已生成 " + where + "，尺寸 " + Fmt(sizeX) + " × " + Fmt(sizeY) + " × " + Fmt(sizeZ) + " 米" + hint;
                return true;
            }
        }
        // 功能：把根下所有 Renderer 的世界包围盒并起来，跳过零尺寸的。
        // 入参：root prefab 根；bounds 输出的世界包围盒
        // 返回：参与计算的 Renderer 数（0 = 没有可用的）
        // 步骤：
        //   1) 取全部 Renderer（true = 含未激活），但逐个数时零尺寸的跳过（未激活会给出零盒）
        //   2) 第一个有效盒作为起点，后续 Encapsulate
        // 上游：TryBuildForAsset
        // 下游：PrepareCollider
        private static int CollectWorldBounds(GameObject root, out Bounds bounds)
        {
            SkinnedMeshRenderer[] skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            bounds = new Bounds(root.transform.position, Vector3.zero);
            bool hasBounds = false;

            // 蒙皮网格优先：用**顶点**求 AABB。
            // ⚠️ 不能用 Renderer.bounds —— 它对蒙皮网格给的是「动画包围盒」，轴系与网格作者坐标可能不一致
            //    （狼的网格是 Z-up 写的：bounds 出来是 0.56×1.78×2.21，而真实网格是 0.50×2.04×0.98），
            //    照着 bounds 建盒子会比模型大一圈，怪物就会悬空。
            for (int i = 0; i < skinned.Length; i++)
            {
                SkinnedMeshRenderer renderer = skinned[i];
                Mesh mesh = renderer.sharedMesh;

                if (mesh == null)
                {
                    continue;
                }

                Vector3[] verts = mesh.vertices;
                Matrix4x4 matrix = renderer.transform.localToWorldMatrix;

                for (int v = 0; v < verts.Length; v++)
                {
                    Vector3 world = matrix.MultiplyPoint3x4(verts[v]);

                    if (!hasBounds)
                    {
                        bounds = new Bounds(world, Vector3.zero);
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(world);
                    }
                }
            }

            if (hasBounds)
            {
                int count = skinned.Length;
                return count;
            }

            // 没有蒙皮网格就退回普通 Renderer 的 bounds（静态模型够用）
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            int used = 0;

            for (int i = 0; i < renderers.Length; i++)
            {
                Bounds current = renderers[i].bounds;

                if (current.size.x <= 0.0001f && current.size.y <= 0.0001f && current.size.z <= 0.0001f)
                {
                    continue;
                }

                if (used == 0)
                {
                    bounds = current;
                }
                else
                {
                    bounds.Encapsulate(current);
                }

                used++;
            }

            return used;
        }
        // 功能：判断根节点相对世界是否倾斜（绕 Y 转不算倾斜）。
        // 入参：root prefab 根
        // 返回：true = pitch/roll 超过阈值
        // 步骤：把本地 up 转到世界，与世界上方比夹角
        // 上游：TryBuildForAsset
        // 下游：无
        private static bool IsTilted(GameObject root)
        {
            Vector3 worldUp = root.transform.TransformDirection(Vector3.up);
            return Vector3.Angle(worldUp, Vector3.up) > TiltThresholdDegrees;
        }

        // 功能：取或建一个「世界空间不旋转」的挂载子物体。
        // 入参：root prefab 根
        // 返回：挂载物体
        // 步骤：
        //   1) 先按名字找，找到就复用（重复执行不会越加越多）
        //   2) 没有就新建：局部位置零、局部旋转 = 根旋转的逆（这样世界旋转是单位）、局部缩放 1
        // 上游：TryBuildForAsset
        // 下游：PrepareCollider
        private static GameObject EnsureHorizontalHolder(GameObject root)
        {
            Transform existing = root.transform.Find(ColliderRootName);

            if (existing != null)
            {
                return existing.gameObject;
            }

            GameObject holder = new GameObject(ColliderRootName);
            holder.transform.SetParent(root.transform, false);
            holder.transform.localPosition = Vector3.zero;
            holder.transform.localRotation = Quaternion.Inverse(root.transform.rotation);
            holder.transform.localScale = Vector3.one;
            return holder;
        }

        // 功能：在指定物体上创建（或复用）BoxCollider，并让它正好包住世界包围盒。
        // 入参：host 挂碰撞体的物体；worldBounds 世界包围盒
        // 返回：无
        // 步骤：
        //   1) 没有 BoxCollider 就加一个
        //   2) 中心：世界中心转到 host 的本地坐标
        //   3) 尺寸：世界尺寸除以 host 的 lossyScale（有缩放时不能直接照抄）
        // 上游：TryBuildForAsset
        // 下游：无
        private static void PrepareCollider(GameObject host, Bounds worldBounds)
        {
            BoxCollider box = host.GetComponent<BoxCollider>();

            if (box == null)
            {
                box = host.AddComponent<BoxCollider>();
            }

            Transform transform = host.transform;
            Vector3 scale = transform.lossyScale;

            box.center = transform.InverseTransformPoint(worldBounds.center);
            box.size = new Vector3(
                worldBounds.size.x / Mathf.Max(Mathf.Abs(scale.x), 0.0001f),
                worldBounds.size.y / Mathf.Max(Mathf.Abs(scale.y), 0.0001f),
                worldBounds.size.z / Mathf.Max(Mathf.Abs(scale.z), 0.0001f));

            EditorUtility.SetDirty(box);
        }

        // 功能：给根加刚体（开重力、冻结旋转、非运动学）。
        // 入参：root prefab 根
        // 返回：无
        // 步骤：没有就加，加了就配参数
        // 上游：TryBuildForAsset
        // 下游：Unity 物理
        // 注意：冻结旋转是为了「只让它竖直落到地面」，否则一落地就被碰撞翻倒
        private static void AddRigidbody(GameObject root)
        {
            Rigidbody body = root.GetComponent<Rigidbody>();

            if (body == null)
            {
                body = root.AddComponent<Rigidbody>();
            }

            body.useGravity = true;
            body.isKinematic = false;
            body.freezeRotation = true;
            EditorUtility.SetDirty(body);
        }

        // 功能：把尺寸格式化成两位小数。
        // 入参：value 米
        // 返回：字符串
        private static string Fmt(float value)
        {
            return value.ToString("F2");
        }
    }
}
