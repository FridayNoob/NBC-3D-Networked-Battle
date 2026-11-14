// 这个文件里有：
//   NbcTestRunnerCallbacks（回调类）：把 EditMode 测试结果写进 Temp/nbc-test-results.txt
//   NbcTestRunner（静态入口）：按 AGENTS.md 4.3 由 AI 通过 MCP 调用，跑一次 EditMode 全量测试

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace NBC.EditorTools
{
    // 主要功能：实现 TestRunnerApi 的回调，统计通过/失败并把结果落盘，供 AI 读文件而不是读编辑器 UI。
    // 主要数据成员：m_passed / m_failed / m_skipped（计数）、m_failures（失败详情）、m_startedAt（开始时间）。
    // 主要外部方法：RunStarted / TestFinished / RunFinished（ICallbacks 契约）、WriteResult（写文件）。
    // 谁在用它：NbcTestRunner.RunEditMode 注册；AI 通过 MCP 触发后轮询结果文件。
    public sealed class NbcTestRunnerCallbacks : ICallbacks
    {
        // 结果文件路径（相对 Unity 工程根；Temp 不进版本库）
        public const string ResultPath = "Temp/nbc-test-results.txt";

        private int m_passed;
        private int m_failed;
        private int m_skipped;
        private readonly List<string> m_failures = new List<string>();
        private DateTime m_startedAt;

        // 功能：测试开始，清零计数并写 running 状态。
        // 入参：testsToRun 本次要跑的全部用例。
        // 返回：无。
        // 步骤：清计数、记开始时间、写文件。
        // 上游/下游：TestRunnerApi 调用；下游 WriteResult。
        public void RunStarted(ITestAdaptor testsToRun)
        {
            m_passed = 0;
            m_failed = 0;
            m_skipped = 0;
            m_failures.Clear();
            m_startedAt = DateTime.UtcNow;
            WriteResult("running", 0d);
        }

        // 功能：单条用例开始（接口要求实现，本工具不统计单条耗时）。
        // 入参：test 用例。
        // 返回：无。
        // 步骤：空实现。
        // 上游/下游：TestRunnerApi 调用。
        public void TestStarted(ITestAdaptor test)
        {
        }

        // 功能：单条用例结束，累计计数。
        // 入参：result 用例结果。
        // 返回：无。
        // 步骤：跳过测试套件节点；按状态累加；失败时记 FullName 与消息。
        // 上游/下游：TestRunnerApi 调用。
        public void TestFinished(ITestResultAdaptor result)
        {
            if (result.Test.IsSuite)
            {
                return;
            }

            switch (result.TestStatus)
            {
                case TestStatus.Passed:
                    m_passed++;
                    break;
                case TestStatus.Failed:
                    m_failed++;
                    m_failures.Add(result.Test.FullName + " :: " + result.Message);
                    break;
                default:
                    m_skipped++;
                    break;
            }
        }

        // 功能：全部结束，写 finished 状态与耗时。
        // 入参：result 汇总结果。
        // 返回：无。
        // 步骤：算耗时秒数后写文件。
        // 上游/下游：TestRunnerApi 调用。
        public void RunFinished(ITestResultAdaptor result)
        {
            double duration = (DateTime.UtcNow - m_startedAt).TotalSeconds;
            WriteResult("finished", duration);
        }

        // 功能：把当前计数写成 key=value 文本。
        // 入参：state 状态；durationSeconds 耗时秒。
        // 返回：无。
        // 步骤：拼行、UTF-8 无 BOM 写文件（机器读，纯 ASCII 键）。
        // 上游/下游：三个回调调用。
        private void WriteResult(string state, double durationSeconds)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("state=" + state);
            sb.AppendLine("passed=" + m_passed);
            sb.AppendLine("failed=" + m_failed);
            sb.AppendLine("skipped=" + m_skipped);
            sb.AppendLine("duration=" + durationSeconds.ToString("0.00", CultureInfo.InvariantCulture));
            for (int i = 0; i < m_failures.Count; i++)
            {
                sb.AppendLine("failure=" + m_failures[i]);
            }

            string directory = Path.GetDirectoryName(ResultPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(ResultPath, sb.ToString(), new UTF8Encoding(false));
        }
    }

    // 主要功能：给 AI 一个稳定入口 —— 跑一次 EditMode 全量测试并把结果写进 Temp。
    // 主要数据成员：无。
    // 主要外部方法：RunEditMode。
    // 谁在用它：AI 通过 MCP 的 unity_execute_code 调 `NBC.EditorTools.NbcTestRunner.RunEditMode()`。
    public static class NbcTestRunner
    {
        // 功能：启动一次 EditMode 全量测试。
        // 入参：无。
        // 返回：启动结果字符串（供 MCP 回显）。
        // 步骤：创建 TestRunnerApi 实例 → 注册回调 → 用 EditMode 过滤器执行。
        // 上游/下游：MCP 调用；下游 TestRunnerApi.Execute。
        // 注意：跑之前先删掉上一次的结果文件，否则可能读到旧结果；一次只 Execute 一次。
        public static string RunEditMode()
        {
            if (File.Exists(NbcTestRunnerCallbacks.ResultPath))
            {
                File.Delete(NbcTestRunnerCallbacks.ResultPath);
            }

            TestRunnerApi api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(new NbcTestRunnerCallbacks());
            api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode }));
            return "started";
        }
    }
}
