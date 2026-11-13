// 这个文件里有：
//   SceneFlow（MonoBehaviour 单例）：唯一的场景加载入口 —— 走 YooAsset 资源层，失败退回 SceneManager。
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
    // 主要功能：切场景（优先 YooAsset，资源层没就绪时退回 SceneManager），并防止重复点。
    // 主要数据成员：IsLoading（是否正在加载）、Progress（0..1）。
    // 主要外部方法：LoadAsync（加载并回调结果）、Instance（拿单例）。
    // 谁在用它：Login 的按钮、调试快捷键、以后的传送门/副本入口。
    // 注意：YooAsset 地址错了会抛异常，这里接住并回报错误，不让它变成静默黑屏。
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
        // 步骤：
        //   1) 正在加载就直接告诉调用方"忙"
        //   2) 资源层就绪 ⇒ 走 AssetManager.LoadSceneAsync（YooAsset）
        //   3) 没就绪 ⇒ 退回 SceneManager.LoadSceneAsync（编辑器直接运行也安全）
        // 上游：Login 按钮 / SceneFlowDebugKeys
        // 下游：AssetManager.LoadSceneAsync、SceneManager.LoadSceneAsync
        public void LoadAsync(GameScene scene, Action<bool, string> onDone = null)
        {
            if (IsLoading)
            {
                onDone?.Invoke(false, "正在加载中，请稍候");
                return;
            }

            StartCoroutine(LoadRoutine(scene, onDone));
        }

        // 功能：加载协程（YooAsset 优先）。
        // 入参：scene 场景编号；onDone 回调
        // 返回：迭代器
        // 步骤：
        //   1) 资源层可用 ⇒ 拆场景句柄，轮询进度，完成后释放句柄
        //   2) 资源层不可用 ⇒ 用 SceneManager 加载
        //   3) 任何异常都记进 LastError 并回报
        // 上游：LoadAsync
        // 下游：GameSceneAddress、SceneManager
        private IEnumerator LoadRoutine(GameScene scene, Action<bool, string> onDone)
        {
            IsLoading = true;
            Progress = 0f;
            LastError = string.Empty;

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
                    error = "YooAsset 加载失败：" + ex.Message;
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
                    error = handle.Error ?? string.Empty;
                    handle.Dispose();
                }
            }
            else
            {
                // 资源层没初始化（例如直接打开 World 场景调试）：用 Unity 自带加载，保证流程能走
                AsyncOperation operation = SceneManager.LoadSceneAsync(GameSceneAddress.NameOf(scene), LoadSceneMode.Single);

                if (operation == null)
                {
                    error = "SceneManager 加载失败（场景是否在 Build Settings 里？）";
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
                }
            }

            LastError = error;
            IsLoading = false;

            if (!ok)
            {
                Debug.LogError("[SceneFlow] 加载 " + address + " 失败：" + error);
            }

            onDone?.Invoke(ok, error);
        }
    }

    // 主要功能：开发期快捷键切场景（F1 Login / F2 World / F3 PVP / F4 Battle / F5 Test）。
    // 主要数据成员：无。
    // 主要外部方法：Unity 的 Update。
    // 谁在用它：挂在任意场景里手动测试；正式版本移除。
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
