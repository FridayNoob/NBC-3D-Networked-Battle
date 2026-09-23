// ============================================================================
//  BattleTestTables —— 战斗测试共用的三张配置表（EditMode 夹具）
//  项目：3D联网战斗Demo   对应：M2-B1 / M2-B2
//
//  ---------------------------------------------------------------------------
//  为什么抽出来（而不是每个测试文件各造一遍）
//  ---------------------------------------------------------------------------
//  `BattleWorldTests` 与 `SkillCasterTests` 需要**同一套数值**才能互相对答案：
//  "穿心箭 120 伤害"、"野狼 300 血" 这些数字如果两边不一致，
//  测试会各自"绿着"，而合起来的行为没人验过。
//
//  📌 这里只集中**造数据**，不集中断言：
//     断言里的数字**照旧写死字面量**（`Assert.AreEqual(300, wolf.MaxHp)`）——
//     断言引用常量的话，"常量被改错"就会让测试跟着一起错，等于没测。
//  （这条是 M1 里"阈值必须算出来"那条教训的同族：**期望值不能来自被测方**。）
// ============================================================================

using System.Collections.Generic;
using NBC.Game.Config;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    /// <summary>战斗测试共用的配置表工厂。</summary>
    internal static class BattleTestTables
    {
        /// <summary>技能：火球（100 伤害）。</summary>
        public const int Fireball = 2001;

        /// <summary>技能：冰箭（80 伤害）。</summary>
        public const int IceArrow = 2002;

        /// <summary>技能：穿心箭（120 伤害）。</summary>
        public const int PiercingArrow = 2003;

        /// <summary>英雄：剑士（1200 血，带火球 + 冰箭）。</summary>
        public const int Knight = 1001;

        /// <summary>英雄：法师（800 血，带穿心箭）。</summary>
        public const int Mage = 1002;

        /// <summary>怪物：野狼（300 血 / 20 攻）。</summary>
        public const int Wolf = 6001;

        /// <summary>怪物：森林蜘蛛（200 血 / 15 攻）。</summary>
        public const int Spider = 6002;

        /// <summary>怪物：狼王（2000 血 / 60 攻）。</summary>
        public const int WolfKing = 6003;

        /// <summary>造技能表。</summary>
        /// <returns>资产（用完要 `Object.DestroyImmediate`）。</returns>
        public static SkillConfig CreateSkills()
        {
            SkillConfig table = ScriptableObject.CreateInstance<SkillConfig>();
            table.rows = new List<Config_Skill>
            {
                Skill(PiercingArrow, "穿心箭", 120),
                Skill(Fireball, "火球", 100),
                Skill(IceArrow, "冰箭", 80)
            };
            table.RebuildIndex();
            return table;
        }

        /// <summary>造英雄表。</summary>
        /// <returns>资产（用完要销毁）。</returns>
        public static HeroConfig CreateHeroes()
        {
            HeroConfig table = ScriptableObject.CreateInstance<HeroConfig>();
            table.rows = new List<Config_Hero>
            {
                Hero(Knight, "剑士", 1200, new[] { Fireball, IceArrow }),
                Hero(Mage, "法师", 800, new[] { PiercingArrow })
            };
            table.RebuildIndex();
            return table;
        }

        /// <summary>造怪物表。</summary>
        /// <returns>资产（用完要销毁）。</returns>
        public static MonsterConfig CreateMonsters()
        {
            MonsterConfig table = ScriptableObject.CreateInstance<MonsterConfig>();
            table.rows = new List<Config_Monster>
            {
                Monster(Wolf, "野狼", 300, 20, new[] { PiercingArrow }),
                Monster(Spider, "森林蜘蛛", 200, 15, new[] { IceArrow, PiercingArrow }),
                Monster(WolfKing, "狼王", 2000, 60, new[] { Fireball, IceArrow })
            };
            table.RebuildIndex();
            return table;
        }

        /// <summary>销毁一张表（传 null 安全）。</summary>
        /// <param name="asset">资产。</param>
        public static void Destroy(Object asset)
        {
            if (asset != null)
            {
                Object.DestroyImmediate(asset);
            }
        }

        /// <summary>造一行技能。</summary>
        private static Config_Skill Skill(int id, string name, int damage)
        {
            Config_Skill row = new Config_Skill();
            row.id = id;
            row.name = name;
            row.damage = damage;
            row.damageType = EDamageType.Physical;
            row.hitFrames = new[] { 3, 5 };
            return row;
        }

        /// <summary>造一行英雄。</summary>
        private static Config_Hero Hero(int id, string name, int hp, int[] skillIds)
        {
            Config_Hero row = new Config_Hero();
            row.id = id;
            row.name = name;
            row.hp = hp;
            row.moveSpeed = 5000;
            row.critRatePerTenThousand = 0;
            row.camDist = 8.5f;
            row.skillIds = skillIds;
            return row;
        }

        /// <summary>造一行怪物。</summary>
        private static Config_Monster Monster(int id, string name, int hp, int attack, int[] skillIds)
        {
            Config_Monster row = new Config_Monster();
            row.id = id;
            row.name = name;
            row.hp = hp;
            row.attack = attack;
            row.skillIds = skillIds;
            return row;
        }
    }
}
