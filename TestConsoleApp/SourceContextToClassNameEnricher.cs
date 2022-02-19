using Serilog.Core;
using Serilog.Events;

namespace TestConsoleApp;

internal class SourceContextToClassNameEnricher : ILogEventEnricher
{
	public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
	{
		const string sourceContext = @"SourceContext";
		if (!logEvent.Properties.TryGetValue(sourceContext, out LogEventPropertyValue? value))
		{
			return;
		}

		string typeName = value.ToString();
		int pos = typeName.LastIndexOf('.');
		if (pos > 0)
		{
			logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(sourceContext, typeName[(pos + 1)..^1]));
		}
	}
}
