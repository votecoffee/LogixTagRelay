using Example.Contracts.Telemetry;

var tags = new TelemetryTags();
tags.Tag_Telemetry.Tag_Code.Value = 42;
if (tags.Variables.Count != 10) throw new Exception("Contract generation failed.");
Console.WriteLine($"Build-generated {nameof(TelemetryTags)}: {tags.Variables.Count} variables; IntelliSense resolves Code={tags.Tag_Telemetry.Tag_Code.Value}.");
