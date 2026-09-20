using System.Diagnostics;
using NBC.Server.Core;

const int TickRate = 30;              // 逻辑帧率
const int MaxTicksPerUpdate = 5;      // 单轮最多推进几帧
const int RunSeconds = 8;             // 跑多久

var scheduler = new TickScheduler(TickRate);
var totalWatch = Stopwatch.StartNew();

// ---- 统计用的变量（你自己想需要哪些） ----
// 提示：至少需要
//   · 总推进帧数
//   · 丢弃次数
//   · 单轮最大追帧数
//   · 上一次打印统计的帧号（为了每 30 帧打一次）
int totalTicks = 0;
int ticksCnt = 0;

int secondDropBase = 0;
int droppedTicks = 0;

int maxTicksInOneLoop = 0;

int lastPrintSecond = 0;
int secondTickBase = 0;

bool hasLagged = false;


//测试

int loopCount = 0;        // 主循环跑了几轮
double elapsedSum = 0;    // ConsumePendingTicks 内部看到的 elapsed 总和
bool firstTickSeen = false;
long watchAtFirstTick = 0;

// ---- Ctrl+C 优雅退出 ----
// 提示：Console.CancelKeyPress 是一个事件
//       在里面设置一个标志位 _running = false，并把 e.Cancel 设为 true
//       （设为 true 表示"我自己处理，别让系统直接杀进程"）

// ① 定义标志位（放在 while 循环之前）
bool running = true;

// ② 注册事件处理（也在 while 之前，只注册一次）
Console.CancelKeyPress += (sender, e) =>
{
    running = false;      // 让循环条件变假
    e.Cancel = true;      // 关键：告诉运行时"我自己处理，别直接杀进程"
};

Console.WriteLine("=== NBC 主循环实验开始 ===");
scheduler.Start();

// ❓ 这里需要一个 while 循环。循环条件是什么？
while (running)
{
    // ① 取当前真实时间，判断"第 3 秒"是否到了
    //    到了且还没模拟过卡顿 → Thread.Sleep(200)，并把"已卡顿"标志置真
    //    （这是唯一允许用 Sleep 的地方，因为它模拟的是"进程被卡住"）
    if(hasLagged == false && totalWatch.ElapsedMilliseconds >= 3000)
    {
        Console.WriteLine("=== 模拟卡顿 400 ms ===");
        Thread.Sleep(400);
        hasLagged = true;
    }
    // ② 推进逻辑帧
    int ticks = scheduler.ConsumePendingTicks(MaxTicksPerUpdate);

    //测试
    loopCount++;
    // 记录"第一次推进逻辑帧"时的真实时间（用于判断启动延迟）
    if (!firstTickSeen && ticks > 0)
    {
        firstTickSeen = true;
        watchAtFirstTick = totalWatch.ElapsedMilliseconds;
    }

    if (ticks > 1)
    {
        ticksCnt++;
    }
    // ③ 对每一个推进的帧，打印一行
    //    格式：[tick 123] 累计真实时间 4123 ms
    //    提示：scheduler.CurrentTick 是当前帧号；累计时间用 totalWatch.ElapsedMilliseconds
    //    注意：如果 ticks 是 0，就不打印（这一轮没推进逻辑）
    for (int i = 0; i < ticks; i++)
    {
        //Console.WriteLine($"[tick {scheduler.CurrentTick - ticks + i + 1}] 累计真实时间 {totalWatch.ElapsedMilliseconds} ms");
    }
    // ④ 累计统计：总帧数 += ticks；追帧数要记录"这一轮推进了几帧"
    //    ❓ 单轮最大追帧数怎么更新？（提示：和当前 ticks 比较）
    totalTicks += ticks;
    maxTicksInOneLoop = Math.Max(maxTicksInOneLoop, ticks);
    droppedTicks = scheduler.DroppedTicks;
    

    // ⑤ 判断是否该打印"每秒统计"
    //    提示：用 scheduler.CurrentTick / TickRate 是否比上次多 1
    //    打印：本秒推进 X 帧（期望 30），丢弃 Y 次，追帧 Z 次
    if (scheduler.CurrentTick / TickRate > lastPrintSecond)
    {
        long currentSecond = scheduler.CurrentTick / TickRate;
        Console.WriteLine($"[第 {currentSecond} 秒] 本秒推进 {totalTicks - secondTickBase} 帧（期望 {TickRate}），丢弃 {droppedTicks - secondDropBase} 帧，追帧 {ticksCnt} 次");
        secondDropBase = droppedTicks; // 记录本秒的丢弃基数
        lastPrintSecond = (int)currentSecond;
        secondTickBase = totalTicks; // 记录本秒的总帧数基数
        ticksCnt = 0; // 重置追帧次数
    }
    // ⑥ 判断是否跑够时间 → 退出循环
    if (totalWatch.ElapsedMilliseconds > RunSeconds * 1000)
        break;
}

// ---- 退出后打印总计 ----
// 总帧数 N（期望 240），丢弃次数 D，最大单轮追帧数 M
//Console.WriteLine($"总帧数 {totalTicks}（期望 240），丢弃次数 {droppedTicks}，最大单轮追帧数 {maxTicksInOneLoop}");
Console.WriteLine($"总帧数 {totalTicks}（期望 240）");
Console.WriteLine($"丢弃 {droppedTicks} 帧，最大单轮追帧 {maxTicksInOneLoop} 帧");
Console.WriteLine($"主循环轮数 {loopCount}，首次推进帧时已过 {watchAtFirstTick} ms，总时长 {totalWatch.ElapsedMilliseconds} ms");