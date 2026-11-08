// Program.cs —— 演示入口与分发器
//
// 用法：
//   dotnet run                 跑全部演示（串跑 + 汇总）
//   dotnet run -- all          同上
//   dotnet run -- 02           只跑演示 02
//   dotnet run -- 04 07        只跑指定编号（串跑 + 汇总）

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SuperAgentDemo;

public static class Program
{
    // [编号, 对应讲义标题, 演示主体]
    private static readonly (string Num, string Desc, Func<Task> Body)[] Registry =
    {
        ("02", "双层架构：Router 协调 + Worker 执行", Demo02.RunAsync),
        ("03", "事件驱动与两级事件过滤", Demo03.RunAsync),
        ("04", "事件路由与订阅直通", Demo04.RunAsync),
        ("05", "上下文组织与链式召回", Demo05.RunAsync),
        ("06", "提前行动与推测执行", Demo06.RunAsync),
        ("07", "面向人的交互：异步全双工 + 多信道表达", Demo07.RunAsync),
        ("08", "计算、状态与执行环境分离", Demo08.RunAsync),
        ("09", "Watchdog 与异常恢复", Demo09.RunAsync),
        ("10", "初步实验与证据解读：批处理压测 + 消融对比", Demo10.RunAsync),
    };

    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 重定向时可能不支持，忽略 */ }

        var only = args.Where(a => Regex.IsMatch(a, @"^\d{2}$")).ToList();
        var wantAll = args.Length == 0 || args.Any(a => a is "all" or "run-all" or "-a" or "--all");

        // 单个编号：直接跑那一个演示（等价 node demos/02.js）
        if (only.Count == 1 && !wantAll)
        {
            var d = Registry.FirstOrDefault(x => x.Num == only[0]);
            if (d.Body == null) { Console.Error.WriteLine($"未知演示编号：{only[0]}（可用：{string.Join(" ", Registry.Select(x => x.Num))}）"); return 2; }
            return Demo.Run(d.Body);
        }

        var selected = only.Count > 0 ? Registry.Where(x => only.Contains(x.Num)).ToArray() : Registry;
        if (selected.Length == 0)
        {
            Console.Error.WriteLine($"没有匹配的演示编号：{string.Join(", ", only)}（可用：{string.Join(" ", Registry.Select(x => x.Num))}）");
            return 2;
        }
        return RunAll(selected);
    }

    private static int RunAll((string Num, string Desc, Func<Task> Body)[] selected)
    {
        var bar = new string('=', 76);
        var results = new List<(string Num, string Desc, bool Ok, double Ms)>();

        Console.WriteLine(bar);
        Console.WriteLine("Super-agent 教学演示 · 串跑（C# / .NET 9）");
        Console.WriteLine($"计划运行 {selected.Length} 个演示：{string.Join(" ", selected.Select(d => d.Num))}");
        Console.WriteLine(bar);

        foreach (var (num, desc, body) in selected)
        {
            Console.WriteLine($"\n{bar}\n>> 演示 {num} · {desc}\n{bar}");
            var sw = Stopwatch.StartNew();
            var ok = Demo.Run(body) == 0;
            sw.Stop();
            if (!ok) Console.Error.WriteLine($"\n[失败] 演示 {num}");
            results.Add((num, desc, ok, sw.Elapsed.TotalMilliseconds));
        }

        Console.WriteLine($"\n{bar}\n运行汇总\n{bar}");
        foreach (var r in results)
        {
            var flag = r.Ok ? "[通过]" : "[失败]";
            Console.WriteLine($"{flag} 演示 {r.Num}  {(r.Ms / 1000).ToString("0.0").PadLeft(5)}s  {r.Desc}");
        }
        var failed = results.Count(r => !r.Ok);
        Console.WriteLine($"\n共 {results.Count} 个演示：通过 {results.Count - failed}，失败 {failed}。");
        Console.WriteLine("提示：单独重跑某个演示可用  dotnet run -- 09");
        return failed > 0 ? 1 : 0;
    }
}
