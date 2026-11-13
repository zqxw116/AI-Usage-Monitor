using UsageMonitor;

PetChecks.Run(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "AIUsageMonitor-pets"), (condition, name) =>
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    Console.WriteLine("PASS " + name);
});
