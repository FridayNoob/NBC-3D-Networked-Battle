// 这个文件里有：
//   MonsterSlotTag（组件）：挂在怪物槽位上，记下它对应哪个实体编号，供「剑碰到谁」反查。

using UnityEngine;

namespace NBC.Game.View
{
    // 主要功能：把「场景里的碰撞体」和「服务端的实体编号」对上号。
    // 主要数据成员：EntityId（实体编号，正整数）。
    // 主要外部方法：无（只读字段）。
    // 谁在用它：SnapshotSceneBinder 建槽位时挂上并赋值；RuntimeNetDriver 用剑的碰撞盒 Overlap 后反查。
    // 注意：不用 GameObject 名字解析 —— 名字是给人看的，随时会改；编号必须是数据。
    public sealed class MonsterSlotTag : MonoBehaviour
    {
        // 这个槽位对应的服务端实体编号；0 = 还没赋值（正常不会出现）
        public int EntityId;
    }
}
