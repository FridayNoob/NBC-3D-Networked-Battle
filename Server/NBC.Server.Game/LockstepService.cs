// ============================================================================
//  LockstepService —— 锁步的**服务端一侧**：开局、收输入、到点广播（M4-S4 S4-c 第二半）
//  项目：3D联网战斗Demo
//
//  ---------------------------------------------------------------------------
//  一、为什么单独一个类（而不是写在 `Program.cs` 里）
//  ---------------------------------------------------------------------------
//  与 `ProgressBroadcaster` / `QuestStateBroadcaster` / `ServerMessagePump` 同一条理由：
//  **Host 与探针共用同一份**。抄一份进探针，验的就是**抄本**，而真正跑在生产里的那份没人验。
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 谁在 tick、怎么防并发 tick
//  ---------------------------------------------------------------------------
//  ① **挂进现有主循环，不另起计时器**：`RoomBattleService.Tick()` 已经是"30Hz、单线程、
//     遍历 `_registry.Rooms`"那一条路。锁步的推进就在**同一次遍历**里对每个房间各调一次。
//     ⚠️ **不引入独立计时**：两个计时源会在同一房间上交错调用 `AdvanceTick`，
//        而 `StepsExecuted` 与"广播帧数"立刻分叉 —— 那正是 §三十二 变异 M 验过的
//        "帧数悄悄错开"，只不过来源从"重复输入"变成"重复 tick"。
//
//  ② **再加一道防御性单飞闸**（`Interlocked.CompareExchange`）：因为"只从主循环调"
//     是**约定**、不是结构。约定会被忘，闸不会被忘。
//     ⚠️ **闸要看得见**：被挡下来的次数记在 `GuardBlocked` 里并对外暴露 ——
//        否则这道防御永远是"看不见的"（挡没挡过、挡过几次，都没人知道）。
//
//  ---------------------------------------------------------------------------
//  三、⚠️ `Missing` 与 `initial_hash`（两条最容易将来没人知道的语义）
//  ---------------------------------------------------------------------------
//  · 第 N 帧在 `LockstepScheduler` 到点广播；**届时没交输入的玩家**由调度器填
//    **默认输入（不动不打）** 并置 `Missing = true` ⇒ **缺人必须看得见**。
//  · 开局发的 `LockstepStart.initial_hash` 是**服务端按同一份实体建出来的世界**的哈希
//    （用 `WorldStateHash` 算，**不是另算一份**）。客户端拿它当"**能不能开始**"的判据。
//
//  ---------------------------------------------------------------------------
//  四、字段类型的一处约定（协议里也写了）
//  ---------------------------------------------------------------------------
//  `LockstepStartEntity.entity_id` 在 wire 上是 **`int64`**，而内存里 `SimEntity.Id` 是 **`int`**。
//  ⇒ **转换必须显式**（下面 `ToWire` / `FromWire` 都写了 `(int)` / `(long)` 并在注释里说明），
//    不许让它"看起来能自动转" —— 那正是将来 Id 超范围时**静默窄化**的来源。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using Google.Protobuf;          // `ToByteArray()`
using NBC.Protocol;
using NBC.Server.Core;
using NBC.Shared;               // `Fix64` / `FixVector3`（定点与向量）
using NBC.Shared.Net;
using NBC.Shared.Sim;

namespace NBC.Server.Game
{
    /// <summary>锁步服务端：开局、收输入、到点广播（Host 与探针共用同一份）。</summary>
    public sealed class LockstepService
    {
        /// <summary>传输（发包用）。</summary>
        private readonly INetTransport m_transport;

        /// <summary>房间表（找会话用）。</summary>
        private readonly RoomRegistry m_registry;

        /// <summary>每个房间的锁步状态（房号 → 状态）。⚠️ **只按键查找，从不遍历**。</summary>
        private readonly List<RoomLockstep> m_rooms = new List<RoomLockstep>();

        /// <summary>累计广播出去的帧数。</summary>
        private long m_broadcastFrames;

        /// <summary>累计被单飞闸挡下来的 tick 次数（⚠️ 见文件头 ②：闸要看得见）。</summary>
        private long m_guardBlocked;

        /// <summary>累计拒绝的输入（玩家编号非法 / 帧号非法 / 重复 / 过期）。</summary>
        private long m_rejectedInputs;

        /// <summary>累计开局次数。</summary>
        private long m_started;

        /// <summary>造一个锁步服务。</summary>
        /// <param name="transport">传输（不能为 null）。</param>
        /// <param name="registry">房间表（不能为 null）。</param>
        public LockstepService(INetTransport transport, RoomRegistry registry)
        {
            if (transport == null) { throw new ArgumentNullException(nameof(transport), "[LockstepService] 传输是 null。"); }
            if (registry == null) { throw new ArgumentNullException(nameof(registry), "[LockstepService] 房间表是 null。"); }

            m_transport = transport;
            m_registry = registry;
        }

        /// <summary>值得记一句的事情。</summary>
        public event Action<string>? Note;

        /// <summary>累计广播了几帧。</summary>
        public long BroadcastFrames
        {
            get { return m_broadcastFrames; }
        }

        /// <summary>累计被单飞闸挡下来的 tick 次数（正常应当恒为 0；非 0 说明有并发路径）。</summary>
        public long GuardBlocked
        {
            get { return m_guardBlocked; }
        }

        /// <summary>累计拒绝的输入条数。</summary>
        public long RejectedInputs
        {
            get { return m_rejectedInputs; }
        }

        /// <summary>一句人话（统计行用）。</summary>
        /// <returns>例：`锁步：开局 1 个房、广播 27 帧、挡 0 次、拒 2 条`。</returns>
        public string Describe()
        {
            return "锁步：开局 " + m_started + " 个房、广播 " + m_broadcastFrames +
                   " 帧、挡 " + m_guardBlocked + " 次、拒 " + m_rejectedInputs + " 条";
        }

        /// <summary>把处理器注册进路由（**显式注册**）。</summary>
        /// <param name="router">路由（不能为 null）。</param>
        public void RegisterHandlers(ServerMessageRouter router)
        {
            if (router == null)
            {
                throw new ArgumentNullException(nameof(router), "[LockstepService] 路由是 null。");
            }

            router.Register(ClientMessage.PayloadOneofCase.LockstepInput, HandleLockstepInput);
        }

        /// <summary>
        /// **开局**：为某个房间建世界与调度器，并把 `LockstepStart` 发给房里所有会话。
        /// <para>⚠️ 实体表会被**按 `entity_id` 排序**后发送（判据②在协议层的形态）。</para>
        /// </summary>
        /// <param name="roomId">房号。</param>
        /// <param name="entities">初始实体（顺序无所谓）。</param>
        /// <param name="delayTicks">固定输入延迟（&lt; 0 钳到 0）。</param>
        /// <returns>开局成功返回 true（房号不存在 ⇒ false）。</returns>
        public bool StartRoom(string roomId, IReadOnlyList<SimEntity> entities, int delayTicks)
        {
            Room? room = FindRoom(roomId);

            if (room == null)
            {
                Note?.Invoke("锁步开局失败：房号 " + roomId + " 不在房间表里");
                return false;
            }

            var world = new WorldState();
            var sorted = new List<SimEntity>();

            if (entities != null)
            {
                for (int i = 0; i < entities.Count; i++)
                {
                    sorted.Add(entities[i]);
                }
            }

            // ⚠️ **按 entity_id 排序**（不许依赖调用方给的顺序）—— 判据②在协议层的形态
            sorted.Sort(CompareEntityById);

            for (int i = 0; i < sorted.Count; i++)
            {
                // ⚠️ 这里**用 `Add` 的默认**（MaxHp = hp、AttackReadyTick = 0）建世界，
                //    然后把**同一份值**作为开局数据发出去 —— 两端因此建出**逐位相同**的世界。
                //    ⚠️ 将来若要让实体"带伤出生"，必须**同时**改这里与 `LockstepStart` 的发送
                //    （否则第 0 帧哈希就不同 ⇒ 满屏假分歧）。
                world.Add(sorted[i].Id, sorted[i].Position, sorted[i].Hp);
            }

            world.SetTick(0);

            var roster = new List<long>(sorted.Count);

            for (int i = 0; i < sorted.Count; i++)
            {
                roster.Add(sorted[i].Id);
            }

            var state = new RoomLockstep();
            state.RoomId = roomId;
            state.World = world;
            state.Scheduler = new LockstepScheduler(world, roster, delayTicks);

            m_rooms.Add(state);
            m_started++;

            // 开局消息（含"能不能开始"的判据：initial_hash）
            var start = new LockstepStart();
            start.Frame = 0;
            start.InitialHash = WorldStateHash.Compute(world);     // ⚠️ 与运行期**同一份**算法

            for (int i = 0; i < sorted.Count; i++)
            {
                var entry = new LockstepStartEntity();
                entry.EntityId = sorted[i].Id;                      // int → int64：**显式**且安全（见文件头四）
                entry.PosXRaw = sorted[i].Position.X.RawValue;
                entry.PosYRaw = sorted[i].Position.Y.RawValue;
                entry.PosZRaw = sorted[i].Position.Z.RawValue;
                entry.Hp = sorted[i].Hp;
                entry.MaxHp = sorted[i].MaxHp;                      // ⚠️ 哈希读它
                entry.AttackReadyTick = sorted[i].AttackReadyTick;  // ⚠️ 哈希读它
                start.Entities.Add(entry);
            }

            int sent = SendToRoom(room, new ServerMessage { LockstepStart = start });

            Note?.Invoke("锁步开局（" + roomId + "）：" + sorted.Count + " 个实体、" +
                         "initial_hash=" + start.InitialHash + "、发给 " + sent + " 个会话");

            return true;
        }

        /// <summary>
        /// 推进一帧：**对每个房间各调一次** `AdvanceTick`，到点就广播。
        /// <para>⚠️ 由主循环调用（见文件头 ②）；带**每房间单飞闸**。</para>
        /// </summary>
        /// <returns>这一遍一共广播了几帧。</returns>
        public int Tick()
        {
            int broadcast = 0;

            for (int i = 0; i < m_rooms.Count; i++)
            {
                RoomLockstep state = m_rooms[i];

                // ⚠️ 单飞闸：同一房间**同时只允许一个推进者**。
                //    实测里它应当**恒为 0 次**（主循环是单线程）；非 0 就是一个真信号。
                if (Interlocked.CompareExchange(ref state.Advancing, 1, 0) != 0)
                {
                    m_guardBlocked++;
                    Note?.Invoke("⚠️ 锁步：房 " + state.RoomId + " 的推进被**单飞闸**挡下 —— 有并发 tick 路径！");
                    continue;
                }

                try
                {
                    LockstepFramePlan plan;

                    while (state.Scheduler.AdvanceTick(out plan))
                    {
                        Broadcast(plan, state);
                        broadcast++;
                        m_broadcastFrames++;
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref state.Advancing, 0);
                }
            }

            return broadcast;
        }

        /// <summary>处理一条锁步输入（**失败要回 `ErrorResponse` 带原因**，别静默）。</summary>
        /// <param name="session">来源会话。</param>
        /// <param name="message">消息。</param>
        /// <returns>分发结果。</returns>
        private DispatchResult HandleLockstepInput(ClientSession session, ClientMessage message)
        {
            LockstepInput input = message.LockstepInput;

            if (input == null)
            {
                return DispatchResult.KickOut("LockstepInput 消息里没有 LockstepInput 字段（协议违规）");
            }

            // ① 身份：游客没有实体（与 §二十六 任务那条同一个判据）
            if (session.PlayerId <= 0)
            {
                m_rejectedInputs++;

                return DispatchResult.ReplyWith(Error(NetErrors.PlayerMismatch,
                    "你是游客（没有玩家档案），不能参与锁步对局。"));
            }

            // ② 帧号不能为负
            if (input.Frame < 0)
            {
                m_rejectedInputs++;

                return DispatchResult.ReplyWith(Error(NetErrors.QuestRejected,
                    "锁步输入的帧号不能为负（收到 " + input.Frame + "）"));
            }

            RoomLockstep? state = FindStateOfSession(session);

            if (state == null)
            {
                m_rejectedInputs++;

                return DispatchResult.ReplyWith(Error(NetErrors.QuestUnavailable,
                    "你不在任何进行中的锁步房间里（房主还没开局，或你已退房）。"));
            }

            // ③ 交给调度器（它负责去重/乱序/过期 —— 见 §三十二 的三条语义）
            string reason;
            bool accepted = state.Scheduler.Submit(
                session.PlayerId, input.Frame,
                // wire 上是两个 int64 raw ⇒ 这里**显式**装回定点（没有"看起来能自动转"的路径）
                new FixVector3(Fix64.FromRaw(input.MoveXRaw), Fix64.FromRaw(input.MoveYRaw), Fix64.Zero),
                input.Buttons, input.TargetEntityId, out reason);

            if (!accepted)
            {
                m_rejectedInputs++;
                return DispatchResult.ReplyWith(Error(NetErrors.QuestRejected,
                    "锁步输入被拒绝：" + reason));
            }

            return DispatchResult.Handled;
        }

        /// <summary>把一帧广播给房里所有会话。</summary>
        /// <param name="plan">帧内容。</param>
        /// <param name="state">房间锁步状态。</param>
        private void Broadcast(LockstepFramePlan plan, RoomLockstep state)
        {
            Room? room = FindRoom(state.RoomId);

            if (room == null)
            {
                return;
            }

            var frame = new LockstepFrame();
            frame.Frame = plan.Frame;
            frame.ServerHash = plan.ServerHash;

            for (int i = 0; i < plan.Inputs.Count; i++)
            {
                FrameInput fi = plan.Inputs[i];
                var entry = new LockstepFrameInput();

                entry.PlayerId = fi.PlayerId;
                entry.MoveXRaw = fi.MoveDirection.X.RawValue;
                entry.MoveYRaw = fi.MoveDirection.Y.RawValue;
                entry.Buttons = fi.Buttons;
                entry.TargetEntityId = fi.TargetEntityId;
                entry.Missing = fi.Missing;     // ⚠️ 缺人必须看得见
                frame.Inputs.Add(entry);
            }

            SendToRoom(room, new ServerMessage { LockstepFrame = frame });
        }

        /// <summary>把一条消息发给房里所有会话（**一个席位一个会话** ⇒ 遍历席位）。</summary>
        /// <param name="room">房间。</param>
        /// <param name="message">消息。</param>
        /// <returns>发出去了几条。</returns>
        private int SendToRoom(Room room, ServerMessage message)
        {
            byte[] payload = message.ToByteArray();
            int sent = 0;

            // ⚠️ 顺序不影响哈希（哈希按 Id 排序喂），所以这里按席位遍历是安全的
            foreach (RoomSeat seat in room.Seats)
            {
                if (seat.Session != null && m_transport.Send(seat.Session.SessionId, payload))
                {
                    sent++;
                }
            }

            return sent;
        }

        /// <summary>按房号找房间。</summary>
        /// <param name="roomId">房号。</param>
        /// <returns>房间；没有则 null。</returns>
        private Room? FindRoom(string roomId)
        {
            foreach (Room room in m_registry.Rooms)
            {
                if (room.RoomId == roomId)
                {
                    return room;
                }
            }

            return null;
        }

        /// <summary>找会话所在房间的锁步状态。</summary>
        /// <param name="session">会话。</param>
        /// <returns>状态；没有则 null。</returns>
        private RoomLockstep? FindStateOfSession(ClientSession session)
        {
            Room? room = m_registry.FindBySession(session);

            if (room == null)
            {
                return null;
            }

            for (int i = 0; i < m_rooms.Count; i++)
            {
                if (m_rooms[i].RoomId == room.RoomId)
                {
                    return m_rooms[i];
                }
            }

            return null;
        }

        /// <summary>按实体编号比较（静态 = 不捕获、不分配）。</summary>
        /// <param name="a">左。</param>
        /// <param name="b">右。</param>
        /// <returns>比较结果。</returns>
        private static int CompareEntityById(SimEntity a, SimEntity b)
        {
            return a.Id.CompareTo(b.Id);
        }

        /// <summary>造一条错误消息。</summary>
        /// <param name="code">错误码。</param>
        /// <param name="message">给玩家看的原因。</param>
        /// <returns>消息。</returns>
        private static ServerMessage Error(int code, string message)
        {
            return new ServerMessage { Error = new ErrorResponse { Code = code, Message = message } };
        }

        /// <summary>一个房间的锁步状态。</summary>
        private sealed class RoomLockstep
        {
            /// <summary>房号。</summary>
            public string RoomId = string.Empty;

            /// <summary>世界。</summary>
            public WorldState? World;

            /// <summary>调度器。</summary>
            public LockstepScheduler? Scheduler;

            /// <summary>单飞闸（0 = 空闲，1 = 正在推进）。</summary>
            public int Advancing;
        }
    }
}
