using Benchmark;
using BenchmarkDotNet.Running;

namespace UnitTest;

public class BenchmarkTest
{
	[Explicit]
	[Test]
	public void PipelinesBenchmark(CancellationToken cancellationToken)
	{
		BenchmarkRunner.Run<PipelinesBenchmark>();
	}
}
