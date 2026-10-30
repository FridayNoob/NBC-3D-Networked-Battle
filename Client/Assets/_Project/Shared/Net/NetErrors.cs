// 这个文件里有：
//   NetErrors（静态类）：服务端拒绝请求时回给客户端的错误码，两端对同一个数字有同一个理解

// 服务端开了可空、Unity 没开；显式关掉，让两端看到同一套规则。
#nullable disable

namespace NBC.Shared.Net
{
    // 主要功能：给「服务端拒绝请求」定一组错误码，客户端按数字分支显示，服务端按数字回。
    // 主要数据成员：RoomFull / AlreadyInRoom / RoomNotFound / PlayerMismatch /
    //       QuestUnavailable / QuestRejected / DungeonNotFound，7 个 int 常量，取值 1001..1007。
    // 主要外部方法：Describe（把码翻成一句人话）。
    // 谁在用它：服务端的房间与任务流程组错误响应（RoomRegistry、LockstepService、QuestService 等），
    //       客户端 NetDebugWindow 按码分支显示。
    /// <summary>服务端拒绝请求时填进 ErrorResponse.code 的错误码常量。</summary>
    // 注意：两端必须对同一个数字有同一个理解 —— 服务端写 1001、客户端当成房间满；一旦对不上，
    //       表现是提示语张冠李戴，而且不会报错。
    // 注意：只放真的在用的码；用不到的码就是走不到的分支，等真有那种拒绝再加。
    public static class NetErrors
    {
        /// <summary>房间满了，进房被拒；取值 1001。</summary>
        public const int RoomFull = 1001;

        /// <summary>这个会话已经在一个房间里了；取值 1002。</summary>
        public const int AlreadyInRoom = 1002;

        /// <summary>指定的房间不存在；取值 1003。</summary>
        public const int RoomNotFound = 1003;

        /// <summary>输入里的 player_id 不是这个会话的玩家；取值 1004。</summary>
        // 注意：这是权威被违反（客户端 bug 或作弊），必须响亮地回一条错误；而「射程不够 / 冷却中」
        //       这类由世界状态决定的拒绝只记日志、不回错误，否则会变成每帧刷屏。
        public const int PlayerMismatch = 1004;

        /// <summary>服务端没接任务权威（没接数据库 / 读不到四张表），现在不能接取或交付；取值 1005。</summary>
        // 注意：必须与「游客被拒」分开 —— 前者是服务端配置问题（该修服务端），后者是身份问题
        //       （该去登录）；混成一个码，排查时会走错方向。
        public const int QuestUnavailable = 1005;

        /// <summary>任务状态机拒绝了这个动作（已经接过 / 条件没满 / 不是已完成等）；取值 1006。</summary>
        // 注意：原因原文在 ErrorResponse.Message 里，客户端要把它显示出来 —— 只显示一个编号，
        //       玩家看不懂，也排查不动。
        public const int QuestRejected = 1006;

        /// <summary>进房请求里的副本编号不在 Dungeon 配置表里；取值 1007。</summary>
        // 注意：必须与 1003「房间不存在」分开 —— 1003 是这个房号没有，1007 是这个副本不存在；
        //       混成一个码，客户端会提示「房间不存在」，而真正的问题是客户端发了一个坏副本号，
        //       排查方向会被直接带偏。
        // 注意：这条校验属于服务端防护 —— 不校验就得等到建战斗世界时才发现，而那时异常已经抛在
        //       主循环里，一个坏副本号能把整个 Host 打挂。
        public const int DungeonNotFound = 1007;

        /// <summary>把错误码翻成一句给玩家看的中文；未登记的码带上编号返回。</summary>
        /// <param name="code">要翻的错误码。</param>
        /// <returns>一句中文描述，不会是 null 或空串。</returns>
        public static string Describe(int code)
        {
            // 功能：把错误码翻成一句给玩家看的中文。
            // 入参：code 错误码，任何 int 都接受（未登记的走默认分支，不报错）
            // 返回：固定一句中文；未登记的码返回「未知错误（编号）」，保留排查线索。
            // 步骤：
            //   1) 逐码命中就返回对应的一句
            //   2) 都没命中落到默认分支，把原编号拼进「未知错误」里 —— 少了编号，排查时连是哪个码都不知道
            // 上游：服务端拒绝请求时兜底填 ErrorResponse.Message
            // 下游：无
            // 注意：正常情况下服务端会给出更具体的一句，这里只是兜底。
            switch (code)
            {
                case RoomFull:
                    return "房间已满";
                case AlreadyInRoom:
                    return "你已经在一个房间里了";
                case RoomNotFound:
                    return "房间不存在";
                case PlayerMismatch:
                    return "输入里的玩家 id 不是你的";
                case QuestUnavailable:
                    return "服务端现在不能处理任务动作";
                case QuestRejected:
                    return "任务动作被拒绝";
                case DungeonNotFound:
                    return "副本不存在";
                default:
                    return "未知错误（" + code + "）";
            }
        }
    }
}
