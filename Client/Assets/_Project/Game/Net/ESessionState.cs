// 这个文件里有：
//   ESessionState（枚举）：一条会话的五个状态，从未连接到失败。

namespace NBC.Game.Net
{
    // 主要功能：一条会话的五个状态，从未连接到失败。
    // 主要数据成员：无 —— 枚举值本身就是状态，从 0 起连续编号。
    // 主要外部方法：无，只被 NetSession.State 与编辑器窗口读取。
    // 谁在用它：NetSession 内部切状态；Editor 的 NetDebugWindow 按它显示并禁用按钮；NetReconnector 按它决定要不要重连。
    // 注意：单独一个文件是为了让「重连策略」能在不带整个网络栈的工程里被独立验算（见 Server\_api-probe\r007）。
    public enum ESessionState
    {
        // 没连
        Disconnected = 0,

        // 正在连，传输层的非阻塞连接阶段
        Connecting = 1,

        // 连上了，正在等握手结果
        Handshaking = 2,

        // 握手完成，可以收发
        Online = 3,

        // 失败；原因在 FailureReason 里，此时连接已经被关掉
        Failed = 4,
    }
}