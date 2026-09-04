// See https://aka.ms/new-console-template for more information

using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;

Console.WriteLine("Hello, World!");

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("=== Hayate Object Pool 测试环境校验 ===");
Console.WriteLine($"操作系统: {RuntimeInformation.OSDescription}");
Console.WriteLine($"架构: {RuntimeInformation.OSArchitecture}");
Console.WriteLine($".NET版本: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"CPU核心数: {Environment.ProcessorCount}");
Console.WriteLine($"是否64位系统: {Environment.Is64BitOperatingSystem}");
Console.WriteLine($"是否64位进程: {Environment.Is64BitProcess}");

// 校验Release模式
#if DEBUG
Console.ForegroundColor = ConsoleColor.Red;
Console.WriteLine("[严重错误] 当前为Debug模式！性能测试必须用Release模式！");
Console.ResetColor();
#else
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("[√] 当前为Release模式，符合要求");
Console.ResetColor();
#endif

// 校验调试器是否附加
if (Debugger.IsAttached)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("[严重错误] 调试器已附加！性能测试必须禁用调试器！");
    Console.ResetColor();
}
else
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("[√] 无调试器附加，符合要求");
    Console.ResetColor();
}

// 校验CPU负载
var cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
cpuCounter.NextValue();
System.Threading.Thread.Sleep(1000);
var cpuUsage = cpuCounter.NextValue();
Console.WriteLine($"当前CPU总使用率: {cpuUsage:F2}%");
if (cpuUsage > 20)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("[警告] CPU使用率过高，会影响测试结果准确性，请关闭后台程序");
    Console.ResetColor();
}
else
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("[√] CPU负载正常，符合要求");
    Console.ResetColor();
}

Console.WriteLine("=======================================");