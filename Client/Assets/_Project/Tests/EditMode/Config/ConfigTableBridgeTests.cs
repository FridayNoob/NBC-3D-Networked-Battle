// 这个文件里有：ConfigTableBridgeTests（通用配置表桥的单元测试）。
using System.Collections.Generic;
using NBC.EditorTools;
using NBC.Game.Config;
using NUnit.Framework;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    // 功能：验证通用桥的注册表、列解析、格式化、解析与校验。
    // 主要数据成员：无。
    // 主要外部方法：每个 [Test] 一个断言组。
    // 谁在用它：Test Runner（AI 代跑，AGENTS.md 4.3）。
    public sealed class ConfigTableBridgeTests
    {
        // 功能：注册表应登记 10 张表且路径按约定拼。
        // 入参：无。
        // 返回含义：无。
        // 步骤：数数量 → 抽查 Monster 的两个路径。
        // 上游/下游：ConfigTableRegistry。
        [Test]
        public void Registry_HasTenTablesWithConventionalPaths()
        {
            Assert.AreEqual(10, ConfigTableRegistry.All.Count);

            ConfigTableEntry monster = ConfigTableRegistry.Find("Monster");

            Assert.IsNotNull(monster);
            Assert.AreEqual("Configs/Design/Monster.csv", monster.CsvPath);
            Assert.AreEqual("Assets/_Project/Configs/MonsterConfig.asset", monster.AssetPath);
            Assert.AreEqual(typeof(Config_Monster), monster.RowType);
        }

        // 功能：按名字查找要大小写不敏感，找不到返回 null。
        // 入参：无。
        // 返回含义：无。
        // 步骤：小写查 → 乱写查。
        // 上游/下游：ConfigTableRegistry.Find。
        [Test]
        public void Registry_FindIsCaseInsensitiveAndNullWhenMissing()
        {
            Assert.IsNotNull(ConfigTableRegistry.Find("monster"));
            Assert.IsNull(ConfigTableRegistry.Find("NoSuchTable"));
            Assert.IsNull(ConfigTableRegistry.Find(null));
        }

        // 功能：Monster 表应读出 10 列，中文名与类型来自 CSV 表头。
        // 入参：无。
        // 返回含义：无。
        // 步骤：ReadColumns → 校验列数与关键列。
        // 上游/下游：ConfigTableBridge.ReadColumns。
        [Test]
        public void ReadColumns_Monster_HasTenColumnsWithLabels()
        {
            ConfigTableEntry entry = ConfigTableRegistry.Find("Monster");
            List<ConfigColumn> columns = ConfigTableBridge.ReadColumns(entry, out string error);

            Assert.IsEmpty(error);
            Assert.AreEqual(10, columns.Count);
            Assert.AreEqual("id", columns[0].Name);
            Assert.AreEqual("编号", columns[0].Label);
            Assert.AreEqual("ref:Skill[]", columns[4].Type);
            Assert.AreEqual("groundLineMm", columns[9].Name);
        }

        // 功能：int[] 多元素要加引号且逗号连接，单元素不加引号，空数组给空串。
        // 入参：无。
        // 返回含义：无。
        // 步骤：格式化三种数组 → 断言。
        // 上游/下游：ConfigTableBridge.FormatCell。
        [Test]
        public void FormatCell_IntArray_QuotesOnlyWhenMoreThanOne()
        {
            Assert.AreEqual("\"2001,2002\"", ConfigTableBridge.FormatCell(new[] { 2001, 2002 }));
            Assert.AreEqual("2001", ConfigTableBridge.FormatCell(new[] { 2001 }));
            Assert.AreEqual(string.Empty, ConfigTableBridge.FormatCell(new int[0]));
        }

        // 功能：float 用不变文化输出，且整数也要带一位小数（CSV 里是 9.0）。
        // 入参：无。
        // 返回含义：无。
        // 步骤：格式化 8.5f 与 9f → 断言字符串。
        // 上游/下游：ConfigTableBridge.FormatCell。
        [Test]
        public void FormatCell_Float_UsesInvariantCultureAndKeepsOneDecimal()
        {
            Assert.AreEqual("8.5", ConfigTableBridge.FormatCell(8.5f));
            Assert.AreEqual("9.0", ConfigTableBridge.FormatCell(9f));
        }

        // 功能：字符串含逗号或引号要加引号并转义。
        // 入参：无。
        // 返回含义：无。
        // 步骤：两个样本 → 断言。
        // 上游/下游：ConfigTableBridge.FormatCell。
        [Test]
        public void FormatCell_String_QuotesWhenNeeded()
        {
            Assert.AreEqual("\"狼巢,东区\"", ConfigTableBridge.FormatCell("狼巢,东区"));
            Assert.AreEqual("\"说\"\"你好\"\"\"", ConfigTableBridge.FormatCell("说\"你好\""));
            Assert.AreEqual("狼巢", ConfigTableBridge.FormatCell("狼巢"));
        }

        // 功能：枚举按名字解析，也接受数字。
        // 入参：无。
        // 返回含义：无。
        // 步骤：TryParseCell 两个样本 → 断言枚举值。
        // 上游/下游：ConfigTableBridge.TryParseCell。
        [Test]
        public void TryParseCell_Enum_AcceptsNameAndNumber()
        {
            Assert.IsTrue(ConfigTableBridge.TryParseCell("Fire", typeof(EDamageType), out object byName, out _));
            Assert.IsTrue(ConfigTableBridge.TryParseCell("1", typeof(EDamageType), out object byNumber, out _));
            Assert.AreEqual(byName, byNumber);
            Assert.AreEqual("Fire", byName.ToString());
        }

        // 功能：非法 int 要报清楚原因而不是抛异常。
        // 入参：无。
        // 返回含义：无。
        // 步骤：TryParseCell("abc", int) → 断言 false 与提示。
        // 上游/下游：ConfigTableBridge.TryParseCell。
        [Test]
        public void TryParseCell_BadInt_ReturnsProblemText()
        {
            Assert.IsFalse(ConfigTableBridge.TryParseCell("abc", typeof(int), out _, out string problem));
            StringAssert.Contains("不是 int", problem);
        }

        // 功能：切分要支持引号内的逗号。
        // 入参：无。
        // 返回含义：无。
        // 步骤：SplitLine 一个样本 → 断言三段。
        // 上游/下游：ConfigTableBridge.SplitLine。
        [Test]
        public void SplitLine_HandlesQuotedComma()
        {
            string[] cells = ConfigTableBridge.SplitLine("1001,\"6001,6001\",6003");

            Assert.AreEqual(3, cells.Length);
            Assert.AreEqual("6001,6001", cells[1]);
        }

        // 功能：导出的文本要保留磁盘表头并按行写数据。
        // 入参：无。
        // 返回含义：无。
        // 步骤：造两行 Skill → BuildCsvText → 断言表头行数与数据行内容。
        // 上游/下游：ConfigTableBridge.BuildCsvText。
        [Test]
        public void BuildCsvText_KeepsHeaderAndWritesRows()
        {
            ConfigTableEntry entry = ConfigTableRegistry.Find("Skill");
            List<Config_Skill> rows = new List<Config_Skill>
            {
                new Config_Skill { id = 9001, name = "测试火", damage = 10, damageType = EDamageType.Fire, hitFrames = new[] { 3, 5 } },
                new Config_Skill { id = 9002, name = "测试冰", damage = 20, damageType = EDamageType.Ice, hitFrames = new[] { 6 } },
            };

            string header = "id,name,damage,damageType,hitFrames\n编号,名称,伤害（点）,伤害类型,命中帧\nint,string,int,enum:EDamageType,int[]\nkey,\"len(1,16)\",\"range(1,99999)\",,\n";
            string text = ConfigTableBridge.BuildCsvText(entry, rows, header);

            Assert.AreEqual("9001,测试火,10,Fire,\"3,5\"", text.Split('\n')[4]);
            Assert.AreEqual("9002,测试冰,20,Ice,6", text.Split('\n')[5]);
        }

        // 功能：主键重复与非法范围要被校验拦下。
        // 入参：无。
        // 返回含义：无。
        // 步骤：造重复 id 与超范围 skill → Validate → 断言错误条数。
        // 上游/下游：ConfigTableBridge.Validate。
        [Test]
        public void Validate_RejectsDuplicateKeyAndOutOfRange()
        {
            ConfigTableEntry entry = ConfigTableRegistry.Find("Skill");
            List<Config_Skill> rows = new List<Config_Skill>
            {
                new Config_Skill { id = 0, name = "空主键", damage = 10, damageType = EDamageType.Fire, hitFrames = new int[0] },
                new Config_Skill { id = 9001, name = "", damage = 0, damageType = EDamageType.Fire, hitFrames = new int[0] },
                new Config_Skill { id = 9001, name = "重复", damage = 10, damageType = EDamageType.Fire, hitFrames = new int[0] },
            };

            List<string> errors = new List<string>();
            Assert.IsFalse(ConfigTableBridge.Validate(entry, rows, errors));
            Assert.GreaterOrEqual(errors.Count, 3, string.Join(" | ", errors));
        }

        // 功能：真实 CSV 的引用列要能查出来（Dungeon 的 monsters 全在 Monster 表里）。
        // 入参：无。
        // 返回含义：无。
        // 步骤：加载 Dungeon SO → Validate → 断言通过。
        // 上游/下游：ConfigTableBridge.Validate + AssetDatabase。
        [Test]
        public void Validate_RealDungeonAsset_Passes()
        {
            ConfigTableEntry entry = ConfigTableRegistry.Find("Dungeon");
            DungeonConfig asset = UnityEditor.AssetDatabase.LoadAssetAtPath<DungeonConfig>(entry.AssetPath);

            Assert.IsNotNull(asset, "找不到 " + entry.AssetPath);

            List<string> errors = new List<string>();
            Assert.IsTrue(ConfigTableBridge.Validate(entry, asset.rows, errors), string.Join(" | ", errors));
        }

        // 功能：Excel 的千位分隔符（20,022,003）要能还原成两个编号。
        // 入参：无。
        // 返回含义：无。
        // 步骤：TryParseCell("20,022,003", int[]) → 断言得到 2002/2003。
        // 上游/下游：ConfigTableBridge.TryParseCell。
        [Test]
        public void TryParseCell_RepairsThousandSeparatedIds()
        {
            bool ok = ConfigTableBridge.TryParseCell("20,022,003", typeof(int[]), out object value, out string problem);

            Assert.IsTrue(ok, problem);
            CollectionAssert.AreEqual(new[] { 2002, 2003 }, (int[])value);
        }

        // 功能：正常编号串不能被「修复」逻辑改坏。
        // 入参：无。
        // 返回含义：无。
        // 步骤：TryParseCell("2002,2003") 与 "2001" → 断言原样。
        // 上游/下游：ConfigTableBridge.TryParseCell。
        [Test]
        public void TryParseCell_KeepsNormalIds()
        {
            Assert.IsTrue(ConfigTableBridge.TryParseCell("2002,2003", typeof(int[]), out object pair, out _));
            CollectionAssert.AreEqual(new[] { 2002, 2003 }, (int[])pair);

            Assert.IsTrue(ConfigTableBridge.TryParseCell("2001", typeof(int[]), out object single, out _));
            CollectionAssert.AreEqual(new[] { 2001 }, (int[])single);

            Assert.IsTrue(ConfigTableBridge.TryParseCell("3,5", typeof(int[]), out object small, out _));
            CollectionAssert.AreEqual(new[] { 3, 5 }, (int[])small);
        }

        // 功能：窗口显示数组时不能带 CSV 引号。
        // 入参：无。
        // 返回含义：无。
        // 步骤：取真实 Monster 第 1 行 → GetCellText(skillIds) → 断言不含引号。
        // 上游/下游：ConfigTableBridge.GetCellText。
        [Test]
        public void GetCellText_DoesNotQuoteArrays()
        {
            ConfigTableEntry entry = ConfigTableRegistry.Find("Monster");
            MonsterConfig asset = UnityEditor.AssetDatabase.LoadAssetAtPath<MonsterConfig>(entry.AssetPath);

            Assert.IsNotNull(asset, "找不到 " + entry.AssetPath);

            List<ConfigColumn> columns = ConfigTableBridge.ReadColumns(entry, out string error);
            Assert.IsEmpty(error);

            ConfigColumn skillColumn = columns.Find(c => c.Name == "skillIds");
            string text = ConfigTableBridge.GetCellText(entry, asset.rows[0], skillColumn);

            Assert.IsFalse(text.Contains("\""), "显示文本不该带引号：" + text);
            Assert.IsTrue(text.Length > 0);
        }
    }
}
