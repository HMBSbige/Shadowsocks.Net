using Benchmark;
using BenchmarkDotNet.Running;

namespace UnitTest;

public class BenchmarkTest
{
	[Test]
	public void PipelinesBenchmark()
	{
		BenchmarkRunner.Run<PipelinesBenchmark>();
	}
}
