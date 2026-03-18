using Benchmark;
using BenchmarkDotNet.Running;

namespace UnitTest;

public class BenchmarkTest
{
	[Explicit]
	[Test]
	public async Task PipelinesBenchmark(CancellationToken cancellationToken)
	{
		await Task.CompletedTask;
		BenchmarkRunner.Run<PipelinesBenchmark>();
	}
}
