using OpenBlade.Capture;

// No-argument/default operation cannot construct any native provider.
if (args.Length == 0 || args.SequenceEqual(new[] { "--validate-only" }))
{
    Console.WriteLine("PmSupervisor=ValidateOnly; NativeExecution=NotStarted"); return 0;
}
if (args.Length != 3 || args[0] is not ("--approve-attended-pm-cycle" or "--approve-attended-range-cycle") || args[1] != "--run-id"
    || !Guid.TryParseExact(args[2], "N", out var run) || run == Guid.Empty || run.ToString("N") != args[2])
{
    Console.Error.WriteLine("Fixed reviewed outer-launcher scope required."); return 2;
}
return args[0] == "--approve-attended-range-cycle"
    ? CpuPmNativeSupervisor.RunRange(run, Console.Out) : CpuPmNativeSupervisor.Run(run, Console.Out);
