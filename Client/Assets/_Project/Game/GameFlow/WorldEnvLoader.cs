// 这个文件里有：
//   WorldEnvLoader（MonoBehaviour）：把"环境子场景"叠加加载进来，并关掉它自带的相机/灯光/耳朵。
//
// 为什么要有它：环境包（StylizedEnvironment）自带一套相机 + 平行光 + AudioListener，
// 直接叠加会双相机重复渲染、双平行光过曝、两个 AudioListener 让 3D 音效定位错乱。

using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NBC.Game.GameFlow
{
    // 主要功能：叠加加载环境子场景，并把冲突的相机/灯光/音频监听器关掉。
    // 主要数据成员：m_envSceneName（环境场景名，必须在 Build Settings 里）、IsLoaded、DisabledCount。
    // 主要外部方法：LoadAsync（异步加载并处理冲突）。
    // 谁在用它：World 场景上的一个空物体（[WorldEnvLoader]）。
    // 注意：环境场景是美术包自带的，不要改它；冲突只用代码在运行时关掉。
    public sealed class WorldEnvLoader : MonoBehaviour
    {
        // 要叠加的环境场景名（必须与 Build Settings 里的场景同名）
        [SerializeField]
        private string m_envSceneName = "SampleScene";

        // 是否已经加载完成
        public bool IsLoaded { get; private set; }

        // 这次关掉了几个冲突组件（相机 / 灯光 / 音频监听器）
        public int DisabledCount { get; private set; }

        // 最近一次错误（空串表示没问题）
        public string LastError { get; private set; } = string.Empty;

        // 功能：场景启动后自动叠加环境。
        // 入参：无
        // 返回：无
        // 步骤：起协程加载
        // 上游：Unity（Start）
        // 下游：LoadRoutine
        private void Start()
        {
            StartCoroutine(LoadRoutine());
        }

        // 功能：异步叠加加载环境场景，加载完关掉冲突组件。
        // 入参：无
        // 返回：迭代器
        // 步骤：
        //   1) 场景名空 ⇒ 直接报错退出
        //   2) Additive 加载，轮询进度
        //   3) 成功后遍历该场景根物体，禁用相机/灯光/音频监听器
        // 上游：Start
        // 下游：DisableConflicts
        private IEnumerator LoadRoutine()
        {
            if (string.IsNullOrWhiteSpace(m_envSceneName))
            {
                LastError = "环境场景名为空";
                Debug.LogError("[WorldEnvLoader] 环境场景名为空，检查 Inspector");
                yield break;
            }

            AsyncOperation operation = null;

            try
            {
                operation = SceneManager.LoadSceneAsync(m_envSceneName, LoadSceneMode.Additive);
            }
            catch (System.Exception ex)
            {
                LastError = ex.Message;
            }

            if (operation == null)
            {
                if (string.IsNullOrEmpty(LastError))
                {
                    LastError = "环境场景加载失败（是否没加进 Build Settings？）";
                }

                Debug.LogError("[WorldEnvLoader] 加载 " + m_envSceneName + " 失败：" + LastError);
                yield break;
            }

            while (!operation.isDone)
            {
                yield return null;
            }

            Scene envScene = SceneManager.GetSceneByName(m_envSceneName);

            if (!envScene.IsValid() || !envScene.isLoaded)
            {
                LastError = "场景加载完成后仍无效：" + m_envSceneName;
                Debug.LogError("[WorldEnvLoader] " + LastError);
                yield break;
            }

            DisabledCount = DisableConflicts(envScene);
            IsLoaded = true;
            Debug.Log("[WorldEnvLoader] 已叠加环境 " + m_envSceneName + "，关掉冲突组件 " + DisabledCount + " 个");
        }

        // 功能：关掉环境场景里与我们冲突的组件。
        // 入参：envScene 已加载的环境场景
        // 返回：关掉的数量
        // 步骤：遍历根物体（含未激活）→ 找 Camera / Light / AudioListener → 禁用
        // 上游：LoadRoutine
        // 下游：无
        // 注意：只关不删；素材包文件保持原样，换包也不受影响
        private static int DisableConflicts(Scene envScene)
        {
            int count = 0;
            GameObject[] roots = envScene.GetRootGameObjects();

            for (int i = 0; i < roots.Length; i++)
            {
                Camera[] cameras = roots[i].GetComponentsInChildren<Camera>(true);

                for (int c = 0; c < cameras.Length; c++)
                {
                    if (cameras[c].enabled)
                    {
                        cameras[c].enabled = false;
                        count++;
                    }
                }

                AudioListener[] listeners = roots[i].GetComponentsInChildren<AudioListener>(true);

                for (int a = 0; a < listeners.Length; a++)
                {
                    if (listeners[a].enabled)
                    {
                        listeners[a].enabled = false;
                        count++;
                    }
                }

                Light[] lights = roots[i].GetComponentsInChildren<Light>(true);

                for (int l = 0; l < lights.Length; l++)
                {
                    if (lights[l].enabled && lights[l].type == LightType.Directional)
                    {
                        // 只关平行光：点光/聚光（火把、灯）留着当氛围，不会过曝
                        lights[l].enabled = false;
                        count++;
                    }
                }
            }

            return count;
        }
    }
}
