// 这个文件里有：
//   SceneFlow（MonoBehaviour 单例）：唯一的场景加载入口 —— 优先 YooAsset，失败自动退回 SceneManager。
//   SceneFlowDebugKeys（MonoBehaviour）：开发用快捷键（F1~F5）直接切场景。
//
// 为什么要单例：加载过程是异步的，需要有东西开着协程；业务只调 SceneFlow.Instance.LoadAsync(...)。

using System;
using System.Collections;
using NBC.Framework.Asset;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NBC.Game.GameFlow
{
    // 主要功能：切场景（YooAsset 优先、失败自动退回 SceneManager），并防止重复点。
    // 主要数据成员：IsLoading（是否正在加载）、Progress（0..1）、LastPath（这次走的哪条路）。
    // 主要外部方法：LoadAsync（加载并回调结果）、Instance（拿单例）。
    // 谁在用它：Login 的按钮、调试快捷键、以后的传送门/副本入口。
    // 注意：两条路都必须能走通 —— 只走 YooAsset 时，收集器没配好就会"按了没反应"。
    public sealed class SceneFlow : MonoBehaviour
    {
        // 单例（第一次访问时创建，切场景不销毁）
        private static SceneFlow s_instance;

        // 是否正在加载
        public bool IsLoading { get; private set; }

        // 最近一次加载进度（0..1）
        public float Progress { get; private set; }

        // 最近一次错误（空串表示没出错）
        public string LastError { get; private set; } = string.Empty;

        // 最近一次实际走的路径："YooAsset" 或 "SceneManager"（排查用）
        public string LastPath { get; private set; } = string.Empty;

        // 功能：拿单例（没有就建一个常驻对象）。
        // 入参：无
        // 返回：单例
        // 步骤：先找场景里的 → 没有就新建并 DontDestroyOnLoad
        // 上游：业务调用
        // 下游：Awake
        public static SceneFlow Instance
        {
            get
            {
                if (s_instance != null)
                {
                    return s_instance;
                }

                SceneFlow existing = FindObjectOfType<SceneFlow>();

                if (existing != null)
                {
                    s_instance = existing;
                    return s_instance;
                }

                GameObject go = new GameObject("[SceneFlow]");
                s_instance = go.AddComponent<SceneFlow>();
                return s_instance;
            }
        }

        // 功能：避免重复单例。
        // 入参：无
        // 返回：无
        // 步骤：已有别的实例就销毁自己
        // 上游：Unity
        // 下游：无
        private void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // 功能：异步加载一个场景。
        // 入参：scene 场景编号；onDone 完成回调（参数：成功？、错误信息）
        // 返回：无（协程跑，回调通知）
        // 步骤：正在加载就拒绝；否则起协程
        // 上游：Login 按钮 / SceneFlowDebugKeys
        // 下游：LoadRoutine
        public void LoadAsync(GameScene scene, Action<bool, string> onDone = null)
        {
            if (IsLoading)
            {
                onDone?.Invoke(false, "正在加载中，请稍候");
                return;
            }

            StartCoroutine(LoadRoutine(scene, onDone));
        }

        // 功能：加载协程（先 YooAsset，失败退 SceneManager）。
        // 入参：scene 场景编号；onDone 回调
        // 返回：迭代器
        // 步骤：
        //   1) 资源层可用 ⇒ 走 YooAsset；失败（抛异常或 handle.Error）不放弃，继续第 2 步
        //   2) 资源层不可用或第 1 步失败 ⇒ 用 SceneManager 加载
        //   3) 两条都失败才报错
        // 上游：LoadAsync
        // 下游：AssetManager.LoadSceneAsync、SceneManager.LoadSceneAsync
        private IEnumerator LoadRoutine(GameScene scene, Action<bool, string> onDone)
        {
            IsLoading = true;
            Progress = 0f;
            LastError = string.Empty;
            LastPath = string.Empty;

            string address = GameSceneAddress.AddressOf(scene);
            bool ok = false;
            string error = string.Empty;

            if (AssetManager.Instance != null && AssetManager.Instance.IsInitialized)
            {
                ISceneHandle handle = null;

                try
                {
                    handle = AssetManager.Instance.LoadSceneAsync(address, LoadSceneMode.Single);
                }
                catch (Exception ex)
                {
                    error = "YooAsset 抛异常：" + ex.Message;
                }

                if (handle != null)
                {
                    while (!handle.IsDone)
                    {
                        Progress = handle.Progress;
                        yield return null;
                    }

                    Progress = 1f;
                    ok = string.IsNullOrEmpty(handle.Error);
                    error = ok ? string.Empty : "YooAsset：" + handle.Error;
                    handle.Dispose();

                    if (ok)
                    {
                        LastPath = "YooAsset";
                    }
                }
            }

            if (!ok)
            {
                // 兜底：YooAsset 没配好（地址缺失/收集器没覆盖）时用 SceneManager，避免"按了没反应"
                string fallbackError = string.Empty;
                AsyncOperation operation = null;

                try
                {
                    operation = SceneManager.LoadSceneAsync(GameSceneAddress.NameOf(scene), LoadSceneMode.Single);
                }
                catch (Exception ex)
                {
                    fallbackError = ex.Message;
                }

                if (operation == null)
                {
                    if (string.IsNullOrEmpty(fallbackError))
                    {
                        fallbackError = "场景是否在 Build Settings 里？";
                    }

                    error = string.IsNullOrEmpty(error) ? fallbackError : error + " / 兜底也失败：" + fallbackError;
                }
                else
                {
                    while (!operation.isDone)
                    {
                        Progress = operation.progress;
                        yield return null;
                    }

                    Progress = 1f;
                    ok = true;
                    LastPath = "SceneManager";
                    error = string.Empty;
                }
            }

            LastError = error;
            IsLoading = false;

            if (!ok)
            {
                Debug.LogError("[SceneFlow] 加载 " + address + " 失败：" + error);
            }
            else
            {
                Debug.Log("[SceneFlow] 已加载 " + address + "（路径：" + LastPath + "）");
            }

            onDone?.Invoke(ok, error);
        }
    }

    // 主要功能：开发期快捷键切场景（F1 Login / F2 World / F3 PVP / F4 Battle / F5 Test）。
    // 主要数据成员：m_enabled（是否启用）。
    // 主要外部方法：Unity 的 Update。
    // 谁在用它：挂在 Login/World 场景里手动测试；正式版本移除。
    // 注意：只做"按键 → SceneFlow.LoadAsync"，不含任何业务判断。
    public sealed class SceneFlowDebugKeys : MonoBehaviour
    {
        // 是否启用快捷键（关掉就不会误触）
        [SerializeField]
        private bool m_enabled = true;

        // 功能：每帧查快捷键。
        // 入参：无
        // 返回：无
        // 步骤：F1~F5 分别加载五个场景
        // 上游：Unity
        // 下游：SceneFlow.LoadAsync
        private void Update()
        {
            if (!m_enabled)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.F1)) { SceneFlow.Instance.LoadAsync(GameScene.Login); }
            if (Input.GetKeyDown(KeyCode.F2)) { SceneFlow.Instance.LoadAsync(GameScene.World); }
            if (Input.GetKeyDown(KeyCode.F3)) { SceneFlow.Instance.LoadAsync(GameScene.PvpRoom); }
            if (Input.GetKeyDown(KeyCode.F4)) { SceneFlow.Instance.LoadAsync(GameScene.Battle); }
            if (Input.GetKeyDown(KeyCode.F5)) { SceneFlow.Instance.LoadAsync(GameScene.Test); }
        }
    }
}
