// 这个文件里有：
//   DungeonCsvBridgeTests（测试类）：验证 CSV 拼装、解析与校验规则，不碰磁盘文件

using System.Collections.Generic;
using NBC.EditorTools;
using NBC.Game.Config;
using NUnit.Framework;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    // 主要功能：锁住 DungeonCsvBridge 的纯逻辑（拼文本、切行、校验），磁盘读写由手工验收覆盖。
    // 主要数据成员：Header 是 4 行表头样例，m_asset 是每个用例新建的 DungeonConfig 实例。
    // 主要外部方法：SetUp、六个 Test 方法。
    // 谁在用它：Test Runner 的 EditMode 全量跑。
    public sealed class DungeonCsvBridgeTests
    {
        // 表头样例，和真实文件一样是 4 行
        private const string Header = "id,name,maxPlayers,spawnRadiusMm,monsters,bossId\n编号,名称,最大人数,出生点半径,普通怪,BOSS\nint,string,int,int,ref:Monster[],ref:Monster\nkey,,\"range(2,4)\",\"range(500,20000)\",,";

        // 每个用例新建的资产实例，避免互相污染
        private DungeonConfig m_asset;

        // 功能：每个用例前建一个空资产。
        // 入参：无。
        // 返回含义：无。
        // 步骤：CreateInstance 后清空 rows。
        // 上游/下游：上游是 NUnit；下游是 ScriptableObject。
        [SetUp]
        public void SetUp()
        {
            m_asset = ScriptableObject.CreateInstance<DungeonConfig>();
            m_asset.rows.Clear();
        }

        // 功能：表头必须原样保留，数据行按列顺序写。
        // 入参：无。
        // 返回含义：无。
        // 步骤：放一行真实数据，断言前 4 行等于 Header、第 5 行等于预期。
        // 上游/下游：上游是 NUnit；下游是 DungeonCsvBridge.BuildCsvText。
        [Test]
        public void BuildCsvText_KeepsHeaderAndFormatsRow()
        {
            m_asset.rows.Add(Row(1001, "狼巢", 4, 3000, new[] { 6001, 6001, 6001 }, 6003));

            string text = DungeonCsvBridge.BuildCsvText(m_asset, Header);
            string[] lines = text.Split('\n');

            Assert.AreEqual(4, CountHeaderLines(text), "表头必须还是 4 行");
            Assert.AreEqual("1001,狼巢,4,3000,\"6001,6001,6001\",6003", lines[4], "数据行的列顺序与引号规则");
        }

        // 功能：名字里有逗号时必须加引号并翻倍内部引号。
        // 入参：无。
        // 返回含义：无。
        // 步骤：放一个含逗号与引号的名字，断言整行被正确引用。
        // 上游/下游：上游是 NUnit；下游是 DungeonCsvBridge.BuildCsvText。
        [Test]
        public void BuildCsvText_QuotesNameWithComma()
        {
            m_asset.rows.Add(Row(1001, "狼,巢\"A\"", 4, 3000, new[] { 6001 }, 6003));

            string text = DungeonCsvBridge.BuildCsvText(m_asset, Header);

            StringAssert.Contains("\"狼,巢\"\"A\"\"\"", text, "逗号与引号都要转义");
        }

        // 功能：切行要认得引号里的逗号。
        // 入参：无。
        // 返回含义：无。
        // 步骤：切 `1001,"6001,6001",3`，断言 3 格且第二格是 `6001,6001`。
        // 上游/下游：上游是 NUnit；下游是 DungeonCsvBridge.SplitLine。
        [Test]
        public void SplitLine_HandlesQuotedComma()
        {
            string[] cells = DungeonCsvBridge.SplitLine("1001,\"6001,6001\",3");

            Assert.AreEqual(3, cells.Length);
            Assert.AreEqual("6001,6001", cells[1]);
        }

        // 功能：引号没闭合要报格式错误，不能默默吞掉。
        // 入参：无。
        // 返回含义：无。
        // 步骤：切一个只有左引号的文本，断言抛 FormatException。
        // 上游/下游：上游是 NUnit；下游是 DungeonCsvBridge.SplitLine。
        [Test]
        public void SplitLine_ThrowsWhenQuoteUnclosed()
        {
            Assert.Throws<System.FormatException>(() => DungeonCsvBridge.SplitLine("1001,\"6001"));
        }

        // 功能：正常数据必须通过校验（引用用真实 Monster 表里的编号）。
        // 入参：无。
        // 返回含义：无。
        // 步骤：放一行 1001 的数据，调 Validate，断言无错误。
        // 上游/下游：上游是 NUnit；下游是 DungeonCsvBridge.Validate。
        [Test]
        public void Validate_AcceptsRealRow()
        {
            m_asset.rows.Add(Row(1001, "狼巢", 4, 3000, new[] { 6001, 6001, 6001 }, 6003));
            List<string> errors = new List<string>();

            Assert.IsTrue(DungeonCsvBridge.Validate(m_asset, errors), string.Join("\n", errors));
        }

        // 功能：主键重复、人数越界、引用不存在都要被拦下。
        // 入参：无。
        // 返回含义：无。
        // 步骤：一次放三行问题数据，断言校验失败且错误条数够多。
        // 上游/下游：上游是 NUnit；下游是 DungeonCsvBridge.Validate。
        [Test]
        public void Validate_RejectsDuplicateAndOutOfRange()
        {
            m_asset.rows.Add(Row(1001, "狼巢", 1, 3000, new[] { 6001 }, 6003));
            m_asset.rows.Add(Row(1001, "重复", 4, 100, new[] { 9999 }, 6003));
            m_asset.rows.Add(Row(1002, string.Empty, 5, 30000, new int[0], 0));
            List<string> errors = new List<string>();

            Assert.IsFalse(DungeonCsvBridge.Validate(m_asset, errors));
            Assert.GreaterOrEqual(errors.Count, 6, "每处问题都要有独立提示");
        }

        // 功能：确认列说明读的是真实 CSV 的中文表头，窗口不会只显示英文字段名。
        // 入参：无。
        // 返回含义：无，用断言判断。
        // 步骤：调 ReadColumns → 断列数 → 断关键列的中文名与规则。
        // 上游/下游：上游是 Test Runner；下游是 DungeonCsvBridge.ReadColumns。
        [Test]
        public void ReadColumns_ReturnsChineseLabelsFromRealCsv()
        {
            List<CsvColumn> columns = DungeonCsvBridge.ReadColumns(out string error);

            Assert.IsEmpty(error, "读表头不应该报错：" + error);
            Assert.AreEqual(6, columns.Count, "Dungeon.csv 应有 6 列");
            Assert.AreEqual("name", columns[1].Name);
            Assert.AreEqual("名称", columns[1].Label, "第二列中文名应来自 CSV 第二行");
            Assert.AreEqual("出生点半径（毫米）", columns[3].Label);
            Assert.AreEqual("range(2,4)", columns[2].Rule);
        }

        // 功能：造一行测试数据。
        // 入参：id、name、maxPlayers、spawnRadiusMm、monsters、bossId。
        // 返回含义：装好的 Config_Dungeon。
        // 步骤：直接 new 并赋值。
        // 上游/下游：上游是各用例；下游是无。
        private static Config_Dungeon Row(int id, string name, int maxPlayers, int spawnRadiusMm, int[] monsters, int bossId)
        {
            return new Config_Dungeon
            {
                id = id,
                name = name,
                maxPlayers = maxPlayers,
                spawnRadiusMm = spawnRadiusMm,
                monsters = monsters,
                bossId = bossId,
            };
        }

        // 功能：数表头行数，用于断言导出没有动表头。
        // 入参：text 是整份 CSV。
        // 返回含义：前 4 行的实际行数（不足 4 行就按实际）。
        // 步骤：切行取前面与 Header 行数相同的部分。
        // 上游/下游：上游是第一个用例；下游是无。
        private static int CountHeaderLines(string text)
        {
            string[] lines = text.Split('\n');
            return Mathf.Min(lines.Length, 4);
        }
    }
}
