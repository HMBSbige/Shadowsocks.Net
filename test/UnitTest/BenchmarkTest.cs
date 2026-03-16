using Benchmark;
using BenchmarkDotNet.Running;

namespace UnitTest;

public class BenchmarkTest
{
	[Explicit]
	[Test]
	public void PipelinesBenchmark()
	{
		BenchmarkRunner.Run<PipelinesBenchmark>();
	}
}
