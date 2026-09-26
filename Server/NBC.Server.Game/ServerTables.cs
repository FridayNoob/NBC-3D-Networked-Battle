// ============================================================================
//  NBC.Server.Game —— 服务端读配置表（M3-S6）
//  项目：3D联网战斗Demo
//  对应：`Docs\17-配置表规范.md`（表格式的**契约**）、Docs\25 §四 S6
//
//  ---------------------------------------------------------------------------
//  一、为什么服务端要**自己读**，而不是复用 Unity 侧那份
//  ---------------------------------------------------------------------------
//  实测（2026-09-23）：
//    · `Config_<表>.cs`（生成物）**只有数据字段**（`public int hp;` …），是纯 C#
//    · **解析逻辑在被生成到 Unity 侧**的 `<表>Config.cs` 里（它 `using UnityEngine`，是 SO 容器 + `LoadFromTsv`）
//  于是服务端要用表数据，只有三条路：
//    ① 让 ConfigKit 生成"纯 C# 的解析器" → **要改工具**（S6 的约定是"加表不改工具代码"）
//    ② 读生成的 JSON → 得手写 JSON 解析（比读 CSV 更容易写错）
//    ③ **读源 CSV**（`Configs\Design\*.csv`）← 选它
//
//  ⚠️ 选 ③ 的真正理由：**源表才是唯一真源**，生成物只是它的投影。
//     服务端读源表 = 少依赖一个中间产物；而且格式由 `Docs\17` §三/§四**冻结**（五行表头 + 类型清单），
//     不是"我随手定的"。
//
//  ⚠️ 如实记代价：这是**第二份读表实现**（Unity 侧的 SO 导入器是第一份）。
//     缓解办法不是"祈祷两份一致"，而是：**探针读的是真表**（`NetProbe` 会断言
//     "副本 1001 里就是 2 只狼 + 1 只狼王，血量来自表"）——
//     格式一旦漂移，探针会当场红，而不是等到某天"怪的血量莫名其妙不对"。
//
//  ---------------------------------------------------------------------------
//  二、只取服务端要的那几列（不做通用表框架）
//  ---------------------------------------------------------------------------
//  这里**不是**一个通用配置系统，只是"服务端跑副本需要的四张表"：
//      `Dungeon`（这一局刷什么）、`Monster`（怪的血与攻击）、`Hero`（英雄的血与移速）、`Skill`（普攻伤害）
//  没用的列**不解析**（少写代码、少一处可能出错的地方）；将来服务端真要用别的列，再加——**别提前占坑**。
//
//  ---------------------------------------------------------------------------
//  三、表在哪儿（开发期与部署期的差别，如实写清）
//  ---------------------------------------------------------------------------
//  开发期：服务端 exe 在 `Server\...\bin\Debug\net8.0\` 下面跑，而源表在**仓库根**的 `Configs\Design\`。
//  所以 `TryResolveConfigDir` 会**从程序集所在目录逐级往上找**，找到 `Configs\Design` 就用它。
//  部署期（M6）：应当把表**随进程发出去**（复制到输出目录），而不是依赖仓库结构 ——
//  这件事写在这里，免得以后有人以为"往上找目录"是最终方案。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NBC.Server.Game;

/// <summary>`Monster` 表里服务端要用的那几列。</summary>
public readonly struct MonsterRow
{
    /// <summary>编号（主键）。</summary>
    public readonly int Id;

    /// <summary>名称（日志用）。</summary>
    public readonly string Name;

    /// <summary>生命值（点）。</summary>
    public readonly int Hp;

    /// <summary>攻击力（点）。</summary>
    public readonly int Attack;

    /// <summary>记一行。</summary>
    /// <param name="id">编号。</param>
    /// <param name="name">名称。</param>
    /// <param name="hp">生命值。</param>
    /// <param name="attack">攻击力。</param>
    public MonsterRow(int id, string name, int hp, int attack)
    {
        Id = id;
        Name = name;
        Hp = hp;
        Attack = attack;
    }
}

/// <summary>`Hero` 表里服务端要用的那几列。</summary>
public readonly struct HeroRow
{
    /// <summary>编号（主键）。</summary>
    public readonly int Id;

    /// <summary>名称（日志用）。</summary>
    public readonly string Name;

    /// <summary>生命值（点）。</summary>
    public readonly int Hp;

    /// <summary>移动速度（毫米/秒）。</summary>
    public readonly int MoveSpeedMmPerSec;

    /// <summary>技能列表（第 0 个当 M3 的"普攻"）。</summary>
    public readonly int[] SkillIds;

    /// <summary>记一行。</summary>
    /// <param name="id">编号。</param>
    /// <param name="name">名称。</param>
    /// <param name="hp">生命值。</param>
    /// <param name="moveSpeedMmPerSec">移动速度（毫米/秒）。</param>
    /// <param name="skillIds">技能列表。</param>
    public HeroRow(int id, string name, int hp, int moveSpeedMmPerSec, int[] skillIds)
    {
        Id = id;
        Name = name;
        Hp = hp;
        MoveSpeedMmPerSec = moveSpeedMmPerSec;
        SkillIds = skillIds;
    }
}

/// <summary>`Dungeon` 表里服务端要用的那几列（S6 新加的表）。</summary>
public readonly struct DungeonRow
{
    /// <summary>编号（主键）。</summary>
    public readonly int Id;

    /// <summary>名称（日志用）。</summary>
    public readonly string Name;

    /// <summary>最大人数。</summary>
    public readonly int MaxPlayers;

    /// <summary>出生点半径（毫米）：怪摆在这个半径附近。</summary>
    public readonly int SpawnRadiusMm;

    /// <summary>普通怪（**写重复就是刷多只**，见 `Docs\17` §五"数组允许重复"）。</summary>
    public readonly int[] Monsters;

    /// <summary>BOSS 的怪编号。</summary>
    public readonly int BossId;

    /// <summary>记一行。</summary>
    /// <param name="id">编号。</param>
    /// <param name="name">名称。</param>
    /// <param name="maxPlayers">最大人数。</param>
    /// <param name="spawnRadiusMm">出生点半径。</param>
    /// <param name="monsters">普通怪。</param>
    /// <param name="bossId">BOSS。</param>
    public DungeonRow(int id, string name, int maxPlayers, int spawnRadiusMm, int[] monsters, int bossId)
    {
        Id = id;
        Name = name;
        MaxPlayers = maxPlayers;
        SpawnRadiusMm = spawnRadiusMm;
        Monsters = monsters;
        BossId = bossId;
    }
}

/// <summary>`DropTable` 表里的一行（**怪物死亡时掷哪几个物品**，S7 新加的表）。</summary>
public readonly struct DropRow
{
    /// <summary>编号（主键）。</summary>
    public readonly int Id;

    /// <summary>哪个怪掉的（`Monster` 表主键）。</summary>
    public readonly int MonsterId;

    /// <summary>物品编号。⚠️ M3 **还没有 `Item` 表**（背包/物品是 M5+），所以这里先用裸编号。</summary>
    public readonly int ItemId;

    /// <summary>最少几个。</summary>
    public readonly int CountMin;

    /// <summary>最多几个。</summary>
    public readonly int CountMax;

    /// <summary>掉落概率（**万分比**：`5000` = 50%，见 `Docs\17` §4.1）。</summary>
    public readonly int ChancePerTenThousand;

    /// <summary>记一行。</summary>
    /// <param name="id">编号。</param>
    /// <param name="monsterId">怪编号。</param>
    /// <param name="itemId">物品编号。</param>
    /// <param name="countMin">最少几个。</param>
    /// <param name="countMax">最多几个。</param>
    /// <param name="chancePerTenThousand">概率（万分比）。</param>
    public DropRow(int id, int monsterId, int itemId, int countMin, int countMax, int chancePerTenThousand)
    {
        Id = id;
        MonsterId = monsterId;
        ItemId = itemId;
        CountMin = countMin;
        CountMax = countMax;
        ChancePerTenThousand = chancePerTenThousand;
    }
}

/// <summary>`Skill` 表里服务端要用的那一列（M3 只取伤害）。</summary>
public readonly struct SkillRow
{
    /// <summary>编号（主键）。</summary>
    public readonly int Id;

    /// <summary>名称（日志用）。</summary>
    public readonly string Name;

    /// <summary>伤害（点）。</summary>
    public readonly int Damage;

    /// <summary>记一行。</summary>
    /// <param name="id">编号。</param>
    /// <param name="name">名称。</param>
    /// <param name="damage">伤害。</param>
    public SkillRow(int id, string name, int damage)
    {
        Id = id;
        Name = name;
        Damage = damage;
    }
}

/// <summary>服务端的配置表（四张：Dungeon / Monster / Hero / Skill）。</summary>
public sealed class ServerTables
{
    private readonly Dictionary<int, DungeonRow> _dungeons = new();
    private readonly Dictionary<int, MonsterRow> _monsters = new();
    private readonly Dictionary<int, HeroRow> _heroes = new();
    private readonly Dictionary<int, SkillRow> _skills = new();

    /// <summary>按怪分组的掉落行（**按主键排序**，保证掷骰顺序确定）。</summary>
    private readonly Dictionary<int, List<DropRow>> _drops = new();

    /// <summary>表是从哪个目录读的（排查用）。</summary>
    public string SourceDirectory { get; }

    /// <summary>四张表各有多少行。</summary>
    public int DungeonCount => _dungeons.Count;

    /// <summary>怪表行数。</summary>
    public int MonsterCount => _monsters.Count;

    /// <summary>英雄表行数。</summary>
    public int HeroCount => _heroes.Count;

    /// <summary>技能表行数。</summary>
    public int SkillCount => _skills.Count;

    /// <summary>掉落表行数。</summary>
    public int DropCount
    {
        get
        {
            int count = 0;

            foreach (KeyValuePair<int, List<DropRow>> pair in _drops)
            {
                count += pair.Value.Count;
            }

            return count;
        }
    }

    /// <summary>取某个怪的掉落行（**按主键升序**；没有就返回空表）。</summary>
    /// <param name="monsterId">怪编号。</param>
    /// <returns>掉落行。</returns>
    public IReadOnlyList<DropRow> FindDrops(int monsterId)
    {
        List<DropRow>? rows;
        return _drops.TryGetValue(monsterId, out rows) ? rows : (IReadOnlyList<DropRow>)System.Array.Empty<DropRow>();
    }

    private ServerTables(string sourceDirectory)
    {
        SourceDirectory = sourceDirectory;
    }

    /// <summary>取副本。</summary>
    /// <param name="id">编号。</param>
    /// <returns>行；没有返回 null。</returns>
    public DungeonRow? FindDungeon(int id)
        => _dungeons.TryGetValue(id, out DungeonRow row) ? row : (DungeonRow?)null;

    /// <summary>取怪。</summary>
    /// <param name="id">编号。</param>
    /// <returns>行；没有返回 null。</returns>
    public MonsterRow? FindMonster(int id)
        => _monsters.TryGetValue(id, out MonsterRow row) ? row : (MonsterRow?)null;

    /// <summary>取英雄。</summary>
    /// <param name="id">编号。</param>
    /// <returns>行；没有返回 null。</returns>
    public HeroRow? FindHero(int id)
        => _heroes.TryGetValue(id, out HeroRow row) ? row : (HeroRow?)null;

    /// <summary>取技能。</summary>
    /// <param name="id">编号。</param>
    /// <returns>行；没有返回 null。</returns>
    public SkillRow? FindSkill(int id)
        => _skills.TryGetValue(id, out SkillRow row) ? row : (SkillRow?)null;

    /// <summary>有掉落配置的怪（按编号升序）—— 启动日志逐条打印用。</summary>
    public IReadOnlyList<int> MonsterIdsWithDrops
    {
        get
        {
            var ids = new List<int>(_drops.Keys);
            ids.Sort();
            return ids;
        }
    }

    /// <summary>所有怪（按编号升序）—— 启动日志逐条自述数值用。</summary>
    public IReadOnlyList<int> MonsterIds
    {
        get
        {
            var ids = new List<int>(_monsters.Keys);
            ids.Sort();
            return ids;
        }
    }

    /// <summary>
    /// 把一个怪的**数值**串成人话（例：`怪 6003 狼王 hp 2000 / 攻 60 / 技能 2001,2002`）。
    /// <para>⚠️ 为什么要自述数值（2026-09-26 负责人第二次踩同一个坑）：他在 Unity 的
    /// `MonsterConfig.asset`（**生成物**）里把狼王血量改成 1000，测试时狼王还是 2000 ——
    /// 因为**服务端读的是 `Configs\Design\Monster.csv`（真源），它根本看不到 SO**。
    /// 上次是掉落表、这次是怪血量，**同一个根因**：改的那份 ≠ 跑的那份，而且**不报错**。
    /// 把服务端真正读到的血量/攻击印在启动横幅里，这类问题从"猜"变成"看一眼"。</para>
    /// </summary>
    /// <param name="monsterId">怪编号。</param>
    /// <returns>人话。</returns>
    public string DescribeMonster(int monsterId)
    {
        MonsterRow? row = FindMonster(monsterId);

        if (row == null)
        {
            return "怪 " + monsterId + " → （Monster 表里没有这一行）";
        }

        return "怪 " + row.Value.Id + " " + row.Value.Name
             + " hp " + row.Value.Hp
             + " / 攻 " + row.Value.Attack;
    }

    /// <summary>把一个英雄的数值串成人话（启动日志自述用；与 <see cref="DescribeMonster"/> 同一个理由）。</summary>
    /// <param name="heroId">英雄编号。</param>
    /// <returns>人话。</returns>
    public string DescribeHero(int heroId)
    {
        HeroRow? row = FindHero(heroId);

        if (row == null)
        {
            return "英雄 " + heroId + " → （Hero 表里没有这一行）";
        }

        return "英雄 " + row.Value.Id + " " + row.Value.Name
             + " hp " + row.Value.Hp
             + " / 移速 " + row.Value.MoveSpeedMmPerSec + "mm/s"
             + " / 技能 " + (row.Value.SkillIds.Length == 0
                 ? "（无）"
                 : string.Join(",", row.Value.SkillIds));
    }

    /// <summary>
    /// 把一个怪的掉落串成人话（例：`怪 6001 → 物品 9001×1~2@10000、9002×1~1@10000`）。
    /// <para>⚠️ 为什么值得在启动时打出来：服务端读的是**源 CSV**、而 Unity 侧读的是**SO（生成物）** ——
    /// 两处来源一旦不一致，"改了表却没生效"就会表现为"打死了什么都不掉"，而且**没有任何报错**。
    /// 把服务端真正读到的值印在启动日志里，这个问题从"猜"变成"看一眼"。</para>
    /// </summary>
    /// <param name="monsterId">怪编号。</param>
    /// <returns>人话。</returns>
    public string DescribeDropsOf(int monsterId)
    {
        IReadOnlyList<DropRow> rows = FindDrops(monsterId);

        if (rows.Count == 0)
        {
            return "怪 " + monsterId + " → （无掉落配置）";
        }

        var text = new System.Text.StringBuilder();
        text.Append("怪 ").Append(monsterId).Append(" → ");

        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
            {
                text.Append('、');
            }

            text.Append("物品 ").Append(rows[i].ItemId)
                .Append('×').Append(rows[i].CountMin).Append('~').Append(rows[i].CountMax)
                .Append('@').Append(rows[i].ChancePerTenThousand);
        }

        return text.ToString();
    }

    /// <summary>一句人话（启动日志用）。</summary>
    /// <returns>描述。</returns>
    public string Describe()
        => $"配置表：副本 {DungeonCount}、怪 {MonsterCount}、英雄 {HeroCount}、技能 {SkillCount}、掉落 {DropCount}" +
           $"（来自 {SourceDirectory}）";

    /// <summary>
    /// 从目录读四张表。**读不到就返回 false + 人话原因**（调用方应当直接拒绝启动 —— 见文件头）。
    /// </summary>
    /// <param name="directory">表目录（`Configs\Design`）。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>成功返回 true。</returns>
    public static bool TryLoad(string directory, out ServerTables tables, out string error)
    {
        // 失败时这里就是 null —— **调用方必须先看返回值**（签名保留非空是为了让调用点干净）
        tables = null!;
        error = string.Empty;

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            error = "配置表目录不存在：" + (directory ?? "(空)");
            return false;
        }

        var result = new ServerTables(directory);

        try
        {
            foreach (CsvSheet sheet in CsvSheet.LoadMany(directory, "Dungeon", "Monster", "Hero", "Skill", "DropTable"))
            {
                switch (sheet.TableName)
                {
                    case "Dungeon":
                        for (int r = 0; r < sheet.RowCount; r++)
                        {
                            var row = new DungeonRow(
                                sheet.Int(r, "id"),
                                sheet.Str(r, "name"),
                                sheet.Int(r, "maxPlayers"),
                                sheet.Int(r, "spawnRadiusMm"),
                                sheet.IntList(r, "monsters"),
                                sheet.Int(r, "bossId"));
                            result._dungeons[row.Id] = row;
                        }

                        break;

                    case "Monster":
                        for (int r = 0; r < sheet.RowCount; r++)
                        {
                            var row = new MonsterRow(
                                sheet.Int(r, "id"),
                                sheet.Str(r, "name"),
                                sheet.Int(r, "hp"),
                                sheet.Int(r, "attack"));
                            result._monsters[row.Id] = row;
                        }

                        break;

                    case "Hero":
                        for (int r = 0; r < sheet.RowCount; r++)
                        {
                            var row = new HeroRow(
                                sheet.Int(r, "id"),
                                sheet.Str(r, "name"),
                                sheet.Int(r, "hp"),
                                sheet.Int(r, "moveSpeed"),
                                sheet.IntList(r, "skillIds"));
                            result._heroes[row.Id] = row;
                        }

                        break;

                    case "Skill":
                        for (int r = 0; r < sheet.RowCount; r++)
                        {
                            var row = new SkillRow(
                                sheet.Int(r, "id"),
                                sheet.Str(r, "name"),
                                sheet.Int(r, "damage"));
                            result._skills[row.Id] = row;
                        }

                        break;

                    case "DropTable":
                        for (int r = 0; r < sheet.RowCount; r++)
                        {
                            var row = new DropRow(
                                sheet.Int(r, "id"),
                                sheet.Int(r, "monsterId"),
                                sheet.Int(r, "itemId"),
                                sheet.Int(r, "countMin"),
                                sheet.Int(r, "countMax"),
                                sheet.Int(r, "chancePerTenThousand"));

                            List<DropRow>? list;
                            if (!result._drops.TryGetValue(row.MonsterId, out list))
                            {
                                list = new List<DropRow>();
                                result._drops[row.MonsterId] = list;
                            }

                            list.Add(row);
                        }

                        break;
                }
            }
        }
        catch (Exception ex)
        {
            error = "读配置表出错：" + ex.Message;
            return false;
        }

        if (result._dungeons.Count == 0 || result._monsters.Count == 0 || result._heroes.Count == 0)
        {
            error = "配置表读出来是空的（副本/怪/英雄至少各要有一行）：" + result.Describe();
            return false;
        }

        tables = result;
        return true;
    }

    /// <summary>
    /// 开发期用：从程序集所在目录**逐级往上**找 `Configs\Design`。
    /// <para>⚠️ 部署期应当把表随进程发出去（见文件头第三节），不要依赖仓库结构。</para>
    /// </summary>
    /// <param name="directory">找到的目录。</param>
    /// <returns>找到返回 true。</returns>
    public static bool TryResolveConfigDir(out string directory)
    {
        directory = string.Empty;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        for (int i = 0; i < 8 && dir != null; i++)
        {
            string candidate = Path.Combine(dir.FullName, "Configs", "Design");

            if (Directory.Exists(candidate))
            {
                directory = candidate;
                return true;
            }

            dir = dir.Parent;
        }

        return false;
    }
}

/// <summary>
/// 一张 CSV 表（**只实现 `Docs\17` 冻结的那点格式**：五行表头 + 逗号分隔 + 双引号包裹）。
/// <para>⚠️ 它不是通用 CSV 解析器：不支持换行字段、注释、多行表头 —— 用不到就不写。</para>
/// </summary>
internal sealed class CsvSheet
{
    /// <summary>表名（文件名去扩展名）。</summary>
    public string TableName { get; }

    /// <summary>列名（第 1 行）→ 下标。</summary>
    private readonly Dictionary<string, int> _columns = new(StringComparer.Ordinal);

    /// <summary>数据行（每行是切好的字段）。</summary>
    private readonly List<string[]> _rows = new();

    /// <summary>数据行数。</summary>
    public int RowCount => _rows.Count;

    private CsvSheet(string tableName)
    {
        TableName = tableName;
    }

    /// <summary>按文件名顺序读若干张表（缺哪张就明确报错，不静默跳过）。</summary>
    /// <param name="directory">目录。</param>
    /// <param name="tableNames">表名（不带扩展名）。</param>
    /// <returns>表。</returns>
    public static List<CsvSheet> LoadMany(string directory, params string[] tableNames)
    {
        var sheets = new List<CsvSheet>();

        for (int i = 0; i < tableNames.Length; i++)
        {
            string path = Path.Combine(directory, tableNames[i] + ".csv");

            if (!File.Exists(path))
            {
                throw new FileNotFoundException("找不到配置表：" + path);
            }

            sheets.Add(Load(path, tableNames[i]));
        }

        return sheets;
    }

    /// <summary>读一张表。</summary>
    /// <param name="path">文件路径。</param>
    /// <param name="tableName">表名。</param>
    /// <returns>表。</returns>
    private static CsvSheet Load(string path, string tableName)
    {
        var sheet = new CsvSheet(tableName);
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);

        // `Docs\17` §三：五行表头（字段名 / 中文说明 / 类型 / 校验 / 数据…）
        if (lines.Length < 5)
        {
            throw new InvalidDataException($"{tableName}.csv 少于 5 行（规范要求：表头 4 行 + 至少 1 行数据）");
        }

        string[] header = Split(lines[0]);

        for (int c = 0; c < header.Length; c++)
        {
            sheet._columns[header[c].Trim()] = c;
        }

        for (int r = 4; r < lines.Length; r++)
        {
            if (string.IsNullOrWhiteSpace(lines[r]))
            {
                continue;       // 末尾空行
            }

            sheet._rows.Add(Split(lines[r]));
        }

        return sheet;
    }

    /// <summary>取一个整数字段。</summary>
    /// <param name="row">行下标（0 起）。</param>
    /// <param name="column">列名。</param>
    /// <returns>值。</returns>
    public int Int(int row, string column)
    {
        string text = Raw(row, column);

        if (!int.TryParse(text, out int value))
        {
            throw new InvalidDataException($"{TableName} 第 {row + 5} 行 `{column}` 不是整数：\"{text}\"");
        }

        return value;
    }

    /// <summary>取一个字符串字段。</summary>
    /// <param name="row">行下标。</param>
    /// <param name="column">列名。</param>
    /// <returns>值。</returns>
    public string Str(int row, string column) => Raw(row, column);

    /// <summary>取一个逗号分隔的整数数组（空 = 空数组）。</summary>
    /// <param name="row">行下标。</param>
    /// <param name="column">列名。</param>
    /// <returns>值。</returns>
    public int[] IntList(int row, string column)
    {
        string text = Raw(row, column);

        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<int>();
        }

        string[] parts = text.Split(',');
        var values = new int[parts.Length];

        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i].Trim(), out values[i]))
            {
                throw new InvalidDataException(
                    $"{TableName} 第 {row + 5} 行 `{column}` 的第 {i + 1} 项不是整数：\"{parts[i]}\"");
            }
        }

        return values;
    }

    /// <summary>取原始文本（列名不存在就明确报错 —— 表头改了要当场知道）。</summary>
    /// <param name="row">行下标。</param>
    /// <param name="column">列名。</param>
    /// <returns>值。</returns>
    private string Raw(int row, string column)
    {
        int index;

        if (!_columns.TryGetValue(column, out index))
        {
            throw new InvalidDataException($"{TableName}.csv 里没有列 `{column}`（表头：{string.Join(",", _columns.Keys)}）");
        }

        string[] fields = _rows[row];
        return index < fields.Length ? fields[index] : string.Empty;
    }

    /// <summary>切一行：逗号分隔，**双引号里的逗号不算分隔符**（数组字段就是这么写的）。</summary>
    /// <param name="line">一行文本。</param>
    /// <returns>字段。</returns>
    private static string[] Split(string line)
    {
        var fields = new List<string>();
        var buffer = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];

            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                fields.Add(buffer.ToString());
                buffer.Clear();
                continue;
            }

            buffer.Append(ch);
        }

        fields.Add(buffer.ToString());
        return fields.ToArray();
    }
}
