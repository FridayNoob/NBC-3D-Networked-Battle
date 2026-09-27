// ============================================================================
//  PasswordDigest —— 密码摘要**配方**（唯一实现，双端共享）
//  项目：3D联网战斗Demo   对应：M4-S3 / SRV-06「真正的登录」，见 `Docs\27` §十九
//
//  ---------------------------------------------------------------------------
//  为什么配方要住在**共享层**（而不是各写一份）
//  ---------------------------------------------------------------------------
//  登录能成功的前提是**两端算出同一个字符串**：
//      客户端：digest = SHA256(明文密码)                    ← 发给服务端
//      服务端：stored = SHA256(salt + digest)               ← 与库里的 password_hash 比
//
//  "同一个字符串"这种事**最怕两份实现** —— 只要有一边多写一个 UTF-8 BOM、
//  大小写不同、或者拼接顺序颠倒，就会得到"密码明明是对的，就是登不上"，
//  而且**两端都不会报错**（服务端只会说"密码不对"）。
//
//  所以这里遵守本项目的老规矩：**能共享的逻辑只留一份源码**
//  （`NBC.Shared` 这份文件同时被 Unity 的 `NBC.Shared.asmdef` 和
//   服务端的 `Server\NBC.Shared\NBC.Shared.csproj` 编译，见那个 csproj 的文件头）。
//
//  ⚠️ 库里的 `account.password_hash` 存的是 **SHA256(salt + 客户端摘要)**，
//     不是 `SHA256(salt + 明文)`。这条配方写在 `Docs\08`（种子数据）里 ——
//     **改这里的任何一步，都必须同步重算种子数据**，否则 4 个测试账号全部登不上。
//     `Server\_db-probe` 里有一条**阳性对照**专门盯这件事（配方对不上它必须红）。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 这不是"安全"，只是"比明文强"（如实记录，别自我感觉良好）
//  ---------------------------------------------------------------------------
//  做到了：① 线上**不出现明文密码**（日志/抓包都拿不到用户真正在别处复用的那个密码）
//          ② 库里**不存**能直接登录的凭据本身（还叠了每账号独立的 salt）
//          ③ 比较用**恒定时间**，避免用"比较耗时"去逐字节猜摘要
//  没做到：① 本项目**没有 TLS** ⇒ 抓到这个摘要就能**重放**（等于临时密码）
//          ② 客户端摘要**没有加盐** ⇒ 弱密码仍可能被彩虹表反查（因为它就是 SHA256(password)）
//          ③ 没有失败次数限制 / 没有验证码
//  真正的做法：TLS + 服务端慢哈希（PBKDF2/Argon2/bcrypt）+ 挑战应答（nonce）。
//  **本切片刻意不做** —— 它的目标是"player_id 属于账号，而不是属于第几个连接"，
//  不是"做一套能上线的账号系统"。理由与取舍见 `Docs\27` §19.2。
//
//  ---------------------------------------------------------------------------
//  为什么自己写十六进制转换（不用 `Convert.ToHexString`）
//  ---------------------------------------------------------------------------
//  `Convert.ToHexString` 是 **.NET 5+** 才有的 API，而本程序集是
//  **netstandard2.1**（Unity 2022.3 能引用的上限）。
//  在这里用它 = 服务端编得过、Unity 编不过 —— 正是 `NBC.Shared.csproj`
//  把 `LangVersion` 钉成 9.0 要防的那一类事故（同族，见那个 csproj 的文件头）。
// ============================================================================

using System;
using System.Security.Cryptography;
using System.Text;

namespace NBC.Shared.Auth
{
    /// <summary>
    /// 密码摘要配方（**双端唯一实现**）。
    ///
    /// <para>
    /// 两步：客户端 <see cref="FromPassword"/> 算出线上摘要，
    /// 服务端再用 <see cref="StoredHash"/> 叠上 salt 得到入库值。
    /// </para>
    /// </summary>
    public static class PasswordDigest
    {
        /// <summary>十六进制摘要的字符数（SHA256 = 32 字节 = 64 个十六进制字符）。</summary>
        public const int HexLength = 64;

        /// <summary>配方的人话描述（写日志用，**不要**把摘要本身写进日志）。</summary>
        public const string Recipe = "SHA256(salt + SHA256(password))";

        /// <summary>
        /// <b>客户端侧</b>：明文密码 → 线上摘要（小写十六进制）。
        ///
        /// <para>
        /// ⚠️ 这一步**不加盐**（客户端也不知道 salt）：它只保证"明文不上线"，
        /// 不等于"抓不到就能防重放"。见本文件头部的取舍。
        /// </para>
        /// </summary>
        /// <param name="password">明文密码（不能是 null；允许空串，但空串摘要一样是"合法输入"，
        /// 空密码该由**调用方**拒绝，而不是让这里静默变成别的意思）。</param>
        /// <returns>64 个小写十六进制字符。</returns>
        /// <exception cref="ArgumentNullException">密码是 null。</exception>
        public static string FromPassword(string password)
        {
            if (password == null)
            {
                throw new ArgumentNullException(nameof(password), "[PasswordDigest] 密码是 null。");
            }

            return Hex(Sha256(Encoding.UTF8.GetBytes(password)));
        }

        /// <summary>
        /// <b>服务端侧</b>：把客户端摘要叠上 salt，得到与 `account.password_hash` 比较的值。
        ///
        /// <para>
        /// ⚠️ 拼接顺序是 `salt + digest`，**与 `Docs\08` 的种子数据一致**。
        /// 改顺序不会报错，只会让所有账号登不上 —— 所以 `_db-probe` 有阳性对照盯着。
        /// </para>
        /// </summary>
        /// <param name="salt">该账号独立的盐值（不能为空）。</param>
        /// <param name="clientDigest">客户端发来的摘要。</param>
        /// <returns>64 个小写十六进制字符。</returns>
        /// <exception cref="ArgumentNullException">参数是 null。</exception>
        /// <exception cref="ArgumentException">salt 是空串。</exception>
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

        /// <summary>
        /// 校验一个客户端摘要对不对（**恒定时间比较**）。
        ///
        /// <para>
        /// ⚠️ 为什么不用 `string.Equals` / `==`：普通字符串比较**发现不同就立刻返回**，
        /// 于是"比较花了多久"会泄漏"前几个字符猜对了" —— 攻击者可以逐字节试出来。
        /// 这里从头到尾走满、把差异 XOR 累加，最后只看结果是不是 0。
        /// （代价是每次比较都走完 64 个字符，可以忽略。）
        /// </para>
        /// </summary>
        /// <param name="salt">库里的盐值。</param>
        /// <param name="storedHash">库里的 `password_hash`。</param>
        /// <param name="clientDigest">客户端发来的摘要。</param>
        /// <returns>对得上为 true。</returns>
        public static bool Verify(string salt, string storedHash, string clientDigest)
        {
            if (string.IsNullOrEmpty(salt) || string.IsNullOrEmpty(storedHash) || clientDigest == null)
            {
                return false;
            }

            string expected = StoredHash(salt, clientDigest);

            return FixedTimeEquals(expected, storedHash);
        }

        /// <summary>是不是"看起来像"一个摘要（长度 64、全是十六进制字符）。</summary>
        /// <param name="text">待检查的文本。</param>
        /// <returns>像则为 true。</returns>
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

        /// <summary>恒定时间比较两个等长（或不等长）字符串。</summary>
        /// <param name="a">第一个。</param>
        /// <param name="b">第二个。</param>
        /// <returns>完全相同为 true。</returns>
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

        /// <summary>算 SHA256。</summary>
        /// <param name="data">字节。</param>
        /// <returns>32 字节。</returns>
        private static byte[] Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return sha.ComputeHash(data);
            }
        }

        /// <summary>字节 → 小写十六进制字符串（自己写，理由见文件头）。</summary>
        /// <param name="bytes">字节。</param>
        /// <returns>2 倍长度的十六进制文本。</returns>
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
