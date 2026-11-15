using System.Globalization;
using BenchmarkDotNet.Running;
using Traced.Benchmarks;

// dotnet run -c Release --project benchmarks/Traced.Benchmarks -- <mode>
//   memory [traces]                 heap / working set probe (default 300000)
//   http <baseUrl> [traces] [workers]  timed loop against a running server
//   anything else                   BenchmarkDotNet switcher, e.g. --filter '*QueryBenchmarks*'
switch (args.FirstOrDefault())
{
    case "memory":
        MemoryProbe.Run(args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 300_000);
        break;
    case "http":
        await HttpLoad.Run(
            new Uri(args[1]),
            args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 300_000,
            args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 20);
        break;
    default:
        BenchmarkSwitcher.FromAssembly(typeof(Workload).Assembly).Run(args);
        break;
}
