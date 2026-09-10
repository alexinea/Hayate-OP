// See https://aka.ms/new-console-template for more information

using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;

Console.WriteLine("Hello, World!");

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("=== Hayate Object Pool test environment check ===");
Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
Console.WriteLine($"Architecture: {RuntimeInformation.OSArchitecture}");
Console.WriteLine($".NET version: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"CPU core count: {Environment.ProcessorCount}");
Console.WriteLine($"Is 64-bit OS: {Environment.Is64BitOperatingSystem}");
Console.WriteLine($"Is 64-bit process: {Environment.Is64BitProcess}");

// Verify Release mode
#if DEBUG
Console.ForegroundColor = ConsoleColor.Red;
Console.WriteLine("[FATAL] Currently in Debug mode! Performance tests must run in Release mode!");
Console.ResetColor();
#else
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("[OK] Currently in Release mode, meets requirements");
Console.ResetColor();
#endif

// Verify whether a debugger is attached
if (Debugger.IsAttached)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("[FATAL] A debugger is attached! Performance tests must run without a debugger!");
    Console.ResetColor();
}
else
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("[OK] No debugger attached, meets requirements");
    Console.ResetColor();
}

// Verify CPU load
var cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
cpuCounter.NextValue();
System.Threading.Thread.Sleep(1000);
var cpuUsage = cpuCounter.NextValue();
Console.WriteLine($"Current total CPU usage: {cpuUsage:F2}%");
if (cpuUsage > 20)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("[WARN] CPU usage is too high and will affect test accuracy; please close background programs");
    Console.ResetColor();
}
else
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("[OK] CPU load is normal, meets requirements");
    Console.ResetColor();
}

Console.WriteLine("=======================================");