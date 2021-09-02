using Benchmark;
using BenchmarkDotNet.Running;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace UnitTest
{
	[TestClass]
	public class BenchmarkTest
	{
		[TestMethod]
		public void PipelinesBenchmark()
		{
			BenchmarkRunner.Run<PipelinesBenchmark>();
		}
	}
}
