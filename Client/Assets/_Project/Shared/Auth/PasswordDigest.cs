// 这个文件里有：
//   PasswordDigest（静态类）：密码摘要配方的双端唯一实现 —— 客户端把明文算成线上摘要，服务端叠 salt 得到入库值，并提供恒定时间比较。
//
// 注意：配方固定为 SHA256(salt + 客户端摘要)，salt 在前；库里的 password_hash 是按这条配方预先算好的，改任何一步都要同步重算 Docs\08 的种子数据，否则 4 个测试账号全部登不上。
// 注意：摘要形如 64 个小写十六进制字符，这个形状是协议字段与服务端判据的一部分，改动等于改协议，必须双端同版本一起升。
// 注意：这只是「比明文强」，不是安全 —— 项目没有 TLS，抓到摘要就能重放；客户端摘要不加盐，弱密码仍可被彩虹表反查。取舍见 Docs\27。
// 注意：本程序集是 netstandard2.1（Unity 2022.3 的上限），不能用 Convert.ToHexString 这类 .NET 5+ API —— 否则服务端编得过、Unity 编不过，所以十六进制转换自己写。

using System;
using System.Security.Cryptography;
using System.Text;

namespace NBC.Shared.Auth
{
    /// <summary>密码摘要配方：双端共享的唯一实现，客户端出摘要、服务端叠 salt 入库，并做恒定时间校验。</summary>
    // 主要功能：给出登录用的摘要配方 —— 客户端把明文算成线上摘要，服务端把摘要叠上 salt 得到入库值，再以恒定时间比较两份摘要。
    // 主要数据成员：HexLength（摘要字符数，固定 64）、Recipe（配方的文字描述，仅日志用）。
    // 主要外部方法：FromPassword / StoredHash / Verify / LooksLikeDigest / FixedTimeEquals（全是静态方法）。
    // 谁在用它：客户端 Game 层 NetSession 登录时算摘要；服务端 ServerMessageRouter 校验摘要形状，AccountDirectory 与 DevAccountStore 校验摘要本身；服务端探针 _db-probe / _net-probe 拿它核对种子数据。
    public static class PasswordDigest
    {
        /// <summary>十六进制摘要的字符数，固定 64（SHA256 的 32 字节 × 每字节 2 个字符）；服务端按它校验形状，属于协议口径。</summary>
        public const int HexLength = 64;

        /// <summary>配方的文字描述，取值固定为这一条字符串；只给日志与诊断看，不要把摘要本身写进日志。</summary>
        public const string Recipe = "SHA256(salt + SHA256(password))";

        /// <summary>客户端侧：把明文密码（不能是 null，空串照算）变成线上摘要，发给服务端登录。</summary>
        /// <returns>64 个小写十六进制字符；同一密码在客户端与服务端必定算出同一串。</returns>
        // 功能：把明文按 UTF-8 编成字节算 SHA256，再转成 64 个小写十六进制字符。
        // 入参：password 明文密码；null 抛 ArgumentNullException，空串照算（空密码是否允许由调用方决定）
        // 返回：64 个小写十六进制字符，长度恒为 HexLength
        // 步骤：
        //   1) 先判 null 并抛异常 —— 不把 null 静默当空串，免得「没传密码」被算成一个合法摘要
        //   2) 显式按 UTF-8 编码再哈希 —— 编码写死，双端才不会因为默认编码不同算出两串
        //   3) 用本类的 Hex 转小写十六进制（不用 Convert.ToHexString，原因见文件头）
        // 上游：客户端 Game 层 NetSession 登录时调用；服务端 DevAccountStore 与服务端探针对照用
        // 下游：Sha256、Hex
        // 注意：这一步不加盐（客户端也拿不到 salt），只保证明文不上线，防不了摘要重放
        public static string FromPassword(string password)
        {
            if (password == null)
            {
                throw new ArgumentNullException(nameof(password), "[PasswordDigest] 密码是 null。");
            }

            return Hex(Sha256(Encoding.UTF8.GetBytes(password)));
        }

        /// <summary>服务端侧：把客户端摘要叠上该账号的 salt（非空、非 null）再哈希，得到与库里 password_hash 比较的值。</summary>
        /// <returns>64 个小写十六进制字符，与 Docs\08 种子数据里的 password_hash 同口径。</returns>
        // 功能：按「salt 在前、客户端摘要在后」拼接后做 SHA256，整体再转小写十六进制。
        // 入参：salt 账号盐值（null 抛 ArgumentNullException、空串抛 ArgumentException）；clientDigest 客户端摘要（null 抛 ArgumentNullException）
        // 返回：64 个小写十六进制字符
        // 步骤：
        //   1) 三个判空放在最前，报错信息点明坏的是哪一端
        //   2) 按 salt + clientDigest 的顺序拼接 —— 顺序写死在这里，与种子数据一致
        //   3) 按 UTF-8 编码后 SHA256，最后转小写十六进制
        // 上游：本类 Verify；服务端探针 _db-probe 与 _net-probe 核对种子数据
        // 下游：Sha256、Hex
        // 注意：拼接顺序颠倒不会报错，只会让所有账号登不上 —— 别凭直觉改成 digest + salt
        public static string StoredHash(string salt, string clientDigest)
        {
            if (salt == null)
            {
                throw new ArgumentNullException(nameof(salt), "[PasswordDigest] salt 是 null。");
            }

            if (salt.Length == 0)
            {
                throw new ArgumentException("[PasswordDigest] salt 是空串（库里不该出现这种数据）。", nameof(salt));
            }

            if (clientDigest == null)
            {
                throw new ArgumentNullException(nameof(clientDigest), "[PasswordDigest] 客户端摘要是 null。");
            }

            return Hex(Sha256(Encoding.UTF8.GetBytes(salt + clientDigest)));
        }

        /// <summary>校验客户端摘要对不对：用同一配方重算期望值，再与库里的 password_hash 做恒定时间比较。</summary>
        /// <returns>true 表示与库里记录一致、可以放行；false 涵盖密码错、账号数据缺失与摘要形状不对，不区分原因。</returns>
        // 功能：先用 StoredHash 重算期望值，再用 FixedTimeEquals 与库里的 hash 恒时比对。
        // 入参：salt 与 storedHash 为空串、clientDigest 为 null 都直接判否，不抛异常
        // 返回：对得上为 true；false 不告诉调用方具体错在哪（避免把账号数据状态泄漏给登录接口）
        // 步骤：
        //   1) 先挡空参数 —— 登录失败要给玩家一句人话，不该因为库里少个 salt 就抛异常
        //   2) 用同一配方重算 expected，保证校验与入库永远走同一条路径
        //   3) 交给 FixedTimeEquals 比较，不用 ==（理由见该方法的注意）
        // 上游：服务端 NBC.Server.Data 的 AccountDirectory 登录校验
        // 下游：StoredHash、FixedTimeEquals
        public static bool Verify(string salt, string storedHash, string clientDigest)
        {
            if (string.IsNullOrEmpty(salt) || string.IsNullOrEmpty(storedHash) || clientDigest == null)
            {
                return false;
            }

            string expected = StoredHash(salt, clientDigest);

            return FixedTimeEquals(expected, storedHash);
        }

        /// <summary>判断一段文本是不是「看起来像」摘要：长度 64 且全是十六进制字符（大小写都认），null 返回 false。</summary>
        /// <returns>true 只表示形状合法，不保证内容正确，也不保证是小写。</returns>
        // 功能：只查形状 —— 长度等于 HexLength，且每个字符都在 0-9 / a-f / A-F 里。
        // 入参：text 待检查文本（null 直接判否）
        // 返回：形状对为 true；大小写都算合法
        // 步骤：
        //   1) 先比长度，长度不对就不必逐字符走
        //   2) 再逐字符查字符集，遇到第一个非法字符立刻判否
        // 上游：服务端 ServerMessageRouter 握手时的形状校验；AccountDirectory 与 DevAccountStore 登录前过滤
        // 下游：无
        // 注意：这里放行大写，但 StoredHash 与 FixedTimeEquals 都区分大小写 —— 大写摘要能过形状检查，却一定校验不过
        public static bool LooksLikeDigest(string text)
        {
            if (text == null || text.Length != HexLength)
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

                if (!isHex)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>恒定时间比较两个字符串：差异用 XOR 累加，比较耗时与「前几个字符对上了」无关。</summary>
        /// <returns>长度相同且逐字符相同为 true；任一侧为 null 或长度不同为 false。</returns>
        // 功能：把两个字符串逐字符 XOR 累加到一个整数里，最后只看结果是不是 0。
        // 入参：a / b 待比较文本，任一为 null 直接判否
        // 返回：完全相同为 true；长度不同、内容不同、任一为 null 都为 false
        // 步骤：
        //   1) 先挡 null
        //   2) 长度不同立刻返回 false —— 长度不是秘密（摘要恒为 64），不必为此走满全串
        //   3) 走满整个字符串累加差异，中途绝不提前 return
        // 上游：本类 Verify；服务端 DevAccountStore；服务端探针 _db-probe
        // 下游：无
        // 注意：不要换成 string.Equals 或 ==：普通比较发现不同就提前返回，比较耗时会泄漏「前几位猜对了」，攻击者可逐字节试出摘要
        public static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null)
            {
                return false;
            }

            // 长度不同直接判否：长度本身不是秘密（摘要永远是 64 个字符）
            if (a.Length != b.Length)
            {
                return false;
            }

            int diff = 0;

            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }

            return diff == 0;
        }

        // 功能：新建一个 SHA256 实例，算出入参字节的 32 字节摘要。
        // 入参：data 待哈希的字节，UTF-8 编码由调用方负责
        // 返回：32 字节定长摘要，转文本交给 Hex
        // 步骤：
        //   1) 每次调用新建实例，用完由 using 释放 —— 不共享实例，避开线程安全问题
        // 上游：FromPassword、StoredHash
        // 下游：无
        private static byte[] Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return sha.ComputeHash(data);
            }
        }

        // 功能：把字节数组转成小写十六进制字符串，每个字节固定两位。
        // 入参：bytes 待转换字节，长度不定，非 null 由调用方保证
        // 返回：长度是入参字节数的 2 倍，字符全在 0-9 / a-f 里
        // 步骤：
        //   1) 先开 2 倍长度的字符数组，定长写满，不边拼边扩容
        //   2) 每字节拆高 4 位与低 4 位，各自查 digits 表 —— 顺序固定为高位在前
        // 上游：FromPassword、StoredHash
        // 下游：无
        // 注意：不用 Convert.ToHexString —— 那是 .NET 5+ API，本程序集是 netstandard2.1，服务端编得过而 Unity 编不过
        private static string Hex(byte[] bytes)
        {
            const string digits = "0123456789abcdef";

            var chars = new char[bytes.Length * 2];

            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = digits[bytes[i] >> 4];
                chars[(i * 2) + 1] = digits[bytes[i] & 0x0F];
            }

            return new string(chars);
        }
    }
}
