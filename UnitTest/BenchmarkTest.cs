using Benchmark;
using BenchmarkDotNet.Running;

namespace UnitTest;

[TestClass]
public class BenchmarkTest
{
	[TestMethod]
	public void PipelinesBenchmark()
	{
		BenchmarkRunner.Run<PipelinesBenchmark>();
	}
}
