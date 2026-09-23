// ============================================================================
//  NetErrors —— 服务端拒绝请求时的**错误码**（双端共享）
//  项目：3D联网战斗Demo   对应：M3-S4、`Protocol\nbc_m3.proto` 的 `ErrorResponse`
//
//  ---------------------------------------------------------------------------
//  为什么错误码要放共享层
//  ---------------------------------------------------------------------------
//  和 `NetContract` 同一条判据：**两端必须对同一个数字有同一个理解**。
//  服务端写 `1001`，客户端把它当成"房间满"去显示/分支 —— 两边一旦对不上，
//  表现是"提示语张冠李戴"，而且**不会报错**。
//
//  ⚠️ 只放**真的在用**的码。M3 到 S4 为止只会有这三种拒绝：
//      进房时房满了 / 已经在一个房里了 / 指定的房间不存在
//     （S5 之后"非法目标"之类再加，别提前占坑：用不到的码 = 走不到的分支）
// ============================================================================

// 与 Shared\Condition\、Shared\Net\ 其余文件同一处理由（服务端开了可空、Unity 没开）。
#nullable disable

namespace NBC.Shared.Net
{
    /// <summary>服务端拒绝请求时的错误码（`ErrorResponse.code` 用它们）。</summary>
    public static class NetErrors
    {
        /// <summary>房间满了。</summary>
        public const int RoomFull = 1001;

        /// <summary>这个会话已经在一个房间里了。</summary>
        public const int AlreadyInRoom = 1002;

        /// <summary>指定的房间不存在。</summary>
        public const int RoomNotFound = 1003;

        /// <summary>把错误码说成人话（服务端兜底用；正常情况下服务端会给出更具体的一句）。</summary>
        /// <param name="code">错误码。</param>
        /// <returns>人话。</returns>
        public static string Describe(int code)
        {
            switch (code)
            {
                case RoomFull:
                    return "房间已满";
                case AlreadyInRoom:
                    return "你已经在一个房间里了";
                case RoomNotFound:
                    return "房间不存在";
                default:
                    return "未知错误（" + code + "）";
            }
        }
    }
}
