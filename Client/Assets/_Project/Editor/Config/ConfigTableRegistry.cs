// 这个文件里有：ConfigTableEntry（一张配置表的登记信息）、ConfigTableRegistry（10 张表的清单与查找）。
using System;
using System.Collections.Generic;
using NBC.Game.Config;

namespace NBC.EditorTools
{
    // 功能：登记一张配置表的名字、CSV 路径、SO 路径与行类型。
    // 主要数据成员：Name 表名、CsvPath 仓库相对路径、AssetPath Unity 路径、RowType 行类型。
    // 主要外部方法：无，纯数据。
    // 谁在用它：ConfigTableRegistry 建清单；ConfigTableBridge 按它读写。
    public sealed class ConfigTableEntry
    {
        // 表名，与 CSV 文件名一致，如 Monster
        public readonly string Name;

        // CSV 仓库相对路径，如 Configs/Design/Monster.csv
        public readonly string CsvPath;

        // SO 的 Unity 路径，如 Assets/_Project/Configs/MonsterConfig.asset
        public readonly string AssetPath;

        // 行类型，如 typeof(Config_Monster)
        public readonly Type RowType;

        // 功能：构造一条登记。
        // 入参：name 表名；csvPath 仓库相对路径；assetPath Unity 路径；rowType 行类型。
        // 返回含义：无。
        // 步骤：四个字段直接赋值。
        // 上游/下游：ConfigTableRegistry 调用。
        public ConfigTableEntry(string name, string csvPath, string assetPath, Type rowType)
        {
            Name = name;
            CsvPath = csvPath;
            AssetPath = assetPath;
            RowType = rowType;
        }
    }

    // 功能：集中登记所有配置表，并提供按名字查找。
    // 主要数据成员：s_all 只读清单。
    // 主要外部方法：All 取清单、Find 按名查。
    // 谁在用它：ConfigTableBridge、R052 的窗口、测试。
    public static class ConfigTableRegistry
    {
        // 表名到登记的清单，顺序与 Docs/新项目文档/需求索引 的配置表一节一致
        private static readonly List<ConfigTableEntry> s_all = new List<ConfigTableEntry>
        {
            Entry("Achievement", typeof(Config_Achievement)),
            Entry("DropTable", typeof(Config_DropTable)),
            Entry("Dungeon", typeof(Config_Dungeon)),
            Entry("Hero", typeof(Config_Hero)),
            Entry("Level", typeof(Config_Level)),
            Entry("Monster", typeof(Config_Monster)),
            Entry("Quest", typeof(Config_Quest)),
            Entry("QuestCondition", typeof(Config_QuestCondition)),
            Entry("Reward", typeof(Config_Reward)),
            Entry("Skill", typeof(Config_Skill)),
        };

        // 功能：按表名拼出 CSV 与 SO 路径。
        // 入参：name 表名；rowType 行类型。
        // 返回含义：登记对象。
        // 步骤：CSV 固定 Configs/Design/<表名>.csv；SO 固定 Assets/_Project/Configs/<表名>Config.asset。
        // 上游/下游：静态清单初始化调用。
        private static ConfigTableEntry Entry(string name, Type rowType)
        {
            return new ConfigTableEntry(
                name,
                "Configs/Design/" + name + ".csv",
                "Assets/_Project/Configs/" + name + "Config.asset",
                rowType);
        }

        // 功能：拿到全部登记。
        // 入参：无。
        // 返回含义：只读清单（顺序固定）。
        // 步骤：直接返回内部清单。
        // 上游/下游：窗口画下拉、测试遍历。
        public static IReadOnlyList<ConfigTableEntry> All { get { return s_all; } }

        // 功能：按表名查登记。
        // 入参：name 表名，大小写不敏感。
        // 返回含义：找到返回登记，找不到返回 null。
        // 步骤：线性查找，10 张表不值得建字典。
        // 上游/下游：ConfigTableBridge 与窗口。
        public static ConfigTableEntry Find(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            foreach (ConfigTableEntry entry in s_all)
            {
                if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }

            return null;
        }
    }
}
