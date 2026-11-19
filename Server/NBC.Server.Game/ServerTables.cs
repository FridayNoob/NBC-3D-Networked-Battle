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
using NBC.Shared.Net;

namespace NBC.Server.Game;

/// <summary>`BattleObstacle` 表里服务端要用的那几列：一个障碍的形状与位置。</summary>
public readonly struct ObstacleRow
{
    /// <summary>编号（主键，写回时按顺序给）。</summary>
    public readonly int Id;

    /// <summary>所属副本编号（与 `Dungeon` 表的主键对应）。</summary>
    public readonly int DungeonId;

    /// <summary>形状：0 = 圆，1 = 轴对齐矩形。</summary>
    public readonly int Kind;

    /// <summary>中心 X（毫米）。</summary>
    public readonly int Xmm;

    /// <summary>中心 Z（毫米）。</summary>
    public readonly int Zmm;

    /// <summary>圆的半径（毫米），矩形行不用。</summary>
    public readonly int RadiusMm;

    /// <summary>矩形的半宽（毫米），圆行不用。</summary>
    public readonly int HalfXMm;

    /// <summary>矩形的半高（毫米），圆行不用。</summary>
    public readonly int HalfZMm;

    /// <summary>备注名（写回时取场景里障碍物体的名字，日志用）。</summary>
    public readonly string Name;

    /// <summary>记一行。</summary>
    /// <param name="id">编号。</param>
    /// <param name="dungeonId">所属副本编号。</param>
    /// <param name="kind">形状：0 圆 / 1 矩形。</param>
    /// <param name="xMm">中心 X（毫米）。</param>
    /// <param name="zMm">中心 Z（毫米）。</param>
    /// <param name="radiusMm">圆的半径（毫米）。</param>
    /// <param name="halfXMm">矩形的半宽（毫米）。</param>
    /// <param name="halfZMm">矩形的半高（毫米）。</param>
    /// <param name="name">备注名。</param>
    public ObstacleRow(int id, int dungeonId, int kind, int xMm, int zMm,
        int radiusMm, int halfXMm, int halfZMm, string name)
    {
        Id = id;
        DungeonId = dungeonId;
        Kind = kind;
        Xmm = xMm;
        Zmm = zMm;
        RadiusMm = radiusMm;
        HalfXMm = halfXMm;
        HalfZMm = halfZMm;
        Name = name;
    }
}

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

    /// <summary>仇恨范围（毫米）。</summary>
    public readonly int AggroRangeMm;

    /// <summary>普攻射程（毫米）。</summary>
    public readonly int AttackRangeMm;

    /// <summary>追击移速（毫米/逻辑帧）。</summary>
    public readonly int MoveSpeedMmPerTick;

    /// <summary>回位半径（毫米）。</summary>
    public readonly int LeashRadiusMm;

    /// <summary>会放的技能编号（空数组表示只会普攻；顺序就是轮转顺序）。</summary>
    public readonly int[] SkillIds;

    /// <summary>记一行。</summary>
    /// <param name="id">编号。</param>
    /// <param name="name">名称。</param>
    /// <param name="hp">生命值。</param>
    /// <param name="attack">攻击力。</param>
    /// <param name="aggroRangeMm">仇恨范围（毫米）。</param>
    /// <param name="attackRangeMm">普攻射程（毫米）。</param>
    /// <param name="moveSpeedMmPerTick">追击移速（毫米/逻辑帧）。</param>
    /// <param name="leashRadiusMm">回位半径（毫米）。</param>
    /// <param name="skillIds">会放的技能编号。</param>
    public MonsterRow(int id, string name, int hp, int attack,
        int aggroRangeMm, int attackRangeMm, int moveSpeedMmPerTick, int leashRadiusMm,
        int[] skillIds)
    {
        Id = id;
        Name = name;
        Hp = hp;
        Attack = attack;
        AggroRangeMm = aggroRangeMm;
        AttackRangeMm = attackRangeMm;
        MoveSpeedMmPerTick = moveSpeedMmPerTick;
        LeashRadiusMm = leashRadiusMm;
        SkillIds = skillIds;
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

/// <summary>`Skill` 表里服务端要用的那几列（伤害、冷却、射程、命中帧）。</summary>
public readonly struct SkillRow
{
    /// <summary>编号（主键）。</summary>
    public readonly int Id;

    /// <summary>名称（日志用）。</summary>
    public readonly string Name;

    /// <summary>伤害（点）。</summary>
    public readonly int Damage;

    /// <summary>冷却（逻辑帧，30Hz；0 表示无冷却）。</summary>
    public readonly int CooldownTicks;

    /// <summary>射程（毫米，圆形半径）。</summary>
    public readonly int RangeMm;

    /// <summary>命中帧（逻辑帧，取自表里 hitFrames 的第一个值；至少 1）。</summary>
    public readonly int HitFrame;

    /// <summary>记一行。</summary>
    /// <param name="id">编号。</param>
    /// <param name="name">名称。</param>
    /// <param name="damage">伤害。</param>
    /// <param name="cooldownTicks">冷却（逻辑帧）。</param>
    /// <param name="rangeMm">射程（毫米）。</param>
    /// <param name="hitFrame">命中帧（逻辑帧）。</param>
    public SkillRow(int id, string name, int damage, int cooldownTicks, int rangeMm, int hitFrame)
    {
        Id = id;
        Name = name;
        Damage = damage;
        CooldownTicks = cooldownTicks;
        RangeMm = rangeMm;
        HitFrame = hitFrame;
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

    /// <summary>战场障碍行（按编号排序，判定顺序固定 ⇒ 结果确定）。</summary>
    private readonly List<ObstacleRow> _obstacles = new();

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

    /// <summary>战场障碍表行数（按副本过滤前的总行数）。</summary>
    public int ObstacleCount => _obstacles.Count;

    /// <summary>战场障碍的全部行（顺序与表里的编号顺序一致，`BattleField` 按副本过滤）。</summary>
    public IReadOnlyList<ObstacleRow> Obstacles => _obstacles;

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
            foreach (CsvSheet sheet in CsvSheet.LoadMany(directory, "Dungeon", "Monster", "Hero", "Skill", "DropTable", "BattleObstacle"))
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
                                sheet.Int(r, "attack"),
                                sheet.IntOr(r, "aggroRangeMm", 4000),
                                sheet.IntOr(r, "attackRangeMm", 2000),
                                sheet.IntOr(r, "moveSpeedMmPerTick", 40),
                                sheet.IntOr(r, "leashRadiusMm", 100000),
                                sheet.IntList(r, "skillIds"));
                            result._monsters[row.Id] = row;
                        }

                        break;

                    case "BattleObstacle":
                        for (int r = 0; r < sheet.RowCount; r++)
                        {
                            var row = new ObstacleRow(
                                sheet.Int(r, "id"),
                                sheet.IntOr(r, "dungeonId", 0),
                                sheet.IntOr(r, "kind", 0),
                                sheet.IntOr(r, "xMm", 0),
                                sheet.IntOr(r, "zMm", 0),
                                sheet.IntOr(r, "radiusMm", 0),
                                sheet.IntOr(r, "halfXMm", 0),
                                sheet.IntOr(r, "halfZMm", 0),
                                sheet.StrOr(r, "name", string.Empty));
                            result._obstacles.Add(row);
                        }

                        // 按编号排序：判定顺序固定，回放与探针才可复现
                        result._obstacles.Sort((left, right) => left.Id.CompareTo(right.Id));
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
                            // 命中帧取表里第一个值；表里没写就按 1 帧兜底（否则技能进队列后永不出队）
                            int[] hitFrames = sheet.IntList(r, "hitFrames");
                            int hitFrame = hitFrames.Length > 0 && hitFrames[0] > 0 ? hitFrames[0] : 1;

                            // 毫秒换算成逻辑帧并向上取整（30Hz 一帧 33ms）：5000ms ⇒ 152 帧
                            int cooldownMs = sheet.IntOr(r, "cooldownMs", 0);
                            int tickMs = 1000 / NetContract.TickRate;
                            int cooldownTicks = cooldownMs <= 0 ? 0 : ((cooldownMs + tickMs - 1) / tickMs);

                            var row = new SkillRow(
                                sheet.Int(r, "id"),
                                sheet.Str(r, "name"),
                                sheet.Int(r, "damage"),
                                cooldownTicks,
                                sheet.IntOr(r, "rangeMm", 2000),
                                hitFrame);
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
        string[] lines = ReadAllLinesAuto(path, tableName);

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

    /// <summary>按 BOM 与内容自动判断编码后读整张表。</summary>
    /// <param name="path">文件路径。</param>
    /// <param name="tableName">表名（只用于报错）。</param>
    /// <returns>按行切好的文本。</returns>
    /// <remarks>
    /// 为什么不能直接 File.ReadAllLines(path, Encoding.UTF8)：Excel 另存会把 csv 写成 GBK，
    /// 按 UTF-8 硬读会得到一堆「?」，然后 attack 之类的数字列解析失败、AI 数值全变 0。
    /// 顺序：BOM 优先 → 严格 UTF-8 → 退回 GBK(936)；都不成立就报错，别静默。
    /// </remarks>
    private static string[] ReadAllLinesAuto(string path, string tableName)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string text = DecodeAuto(bytes, tableName);

        if (text.IndexOf('\uFFFD') >= 0)
        {
            throw new InvalidDataException(
                tableName + ".csv 里出现了无法解码的字符（U+FFFD）：请用「UTF-8 带 BOM」或 GBK 保存后再启动服务端。");
        }

        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    /// <summary>按 BOM 与内容自动判断编码。</summary>
    /// <param name="bytes">文件字节。</param>
    /// <param name="tableName">表名（只用于报错）。</param>
    /// <returns>解码后的文本。</returns>
    private static string DecodeAuto(byte[] bytes, string tableName)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936).GetString(bytes);
        }
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

    /// <summary>取一个整数字段**或兜底值**（列缺失 / 空 / 非整数都回落，不抛异常）。</summary>
    /// <param name="row">行下标（0 起）。</param>
    /// <param name="column">列名。</param>
    /// <param name="fallback">兜底值。</param>
    /// <returns>值（读不到就是兜底值）。</returns>
    public int IntOr(int row, string column, int fallback)
    {
        if (!_columns.TryGetValue(column, out int index))
        {
            WarnMissingColumn(column);
            return fallback;
        }

        string[] fields = _rows[row];

        if (index >= fields.Length)
        {
            return fallback;
        }

        return int.TryParse(fields[index], out int value) ? value : fallback;
    }

    /// <summary>缺列时只提示一次（同表同列），避免每行刷屏。</summary>
    /// <param name="column">列名。</param>
    private void WarnMissingColumn(string column)
    {
        if (WarnedColumns.Add(column))
        {
            Console.WriteLine($"[配置表] {TableName}.csv 缺少列 `{column}`，改用兜底值。");
        }
    }

    /// <summary>已经提示过缺列的集合（同表实例内有效）。</summary>
    private readonly HashSet<string> WarnedColumns = new HashSet<string>();

    /// <summary>取一个字符串字段。</summary>
    /// <param name="row">行下标。</param>
    /// <param name="column">列名。</param>
    /// <returns>值。</returns>
    public string Str(int row, string column) => Raw(row, column);

    /// <summary>取一个字符串字段**或兜底值**（列缺失 / 空单元格都回落，不抛异常）。</summary>
    /// <param name="row">行下标。</param>
    /// <param name="column">列名。</param>
    /// <param name="fallback">兜底值（列缺失或单元格为空时用它）。</param>
    /// <returns>值。</returns>
    public string StrOr(int row, string column, string fallback)
    {
        if (!_columns.TryGetValue(column, out int index))
        {
            WarnMissingColumn(column);
            return fallback;
        }

        string[] fields = _rows[row];

        if (index >= fields.Length || string.IsNullOrEmpty(fields[index]))
        {
            return fallback;
        }

        return fields[index];
    }

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
