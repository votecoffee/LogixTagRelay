using LogixTagRelay;
using LogixTagRelay.Generated;

var config = SyncConfiguration.FromJson(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "logixrelay.json")));
var tags = new GeneratedTags();
tags.Tag_Request.Tag_Recipe.Value = new string('R', 128);
tags.Tag_Request.Tag_Execute.Value = true;
var codec = new ImplicitImage(config, tags);
var outgoing = codec.BuildAppToPlc();
var incoming = outgoing.ToArray();
ImplicitImage.WriteInt(incoming, 12, 1);
ImplicitImage.WriteInt(incoming, 16, ImplicitImage.ReadInt(outgoing, 12));
foreach (var field in config.Fields)
{
    if (field.Path == "Metrics.Title") PlcTypes.Encode(field, "PLC label", incoming.AsSpan(field.Offset));
    if (field.Path == "Values[1,2]") PlcTypes.Encode(field, 12.5f, incoming.AsSpan(field.Offset));
    if (field.Path == "Cubes[1,1,1]") PlcTypes.Encode(field, 9007199254740993L, incoming.AsSpan(field.Offset));
    if (field.Path == "Stations[1,1].Name") PlcTypes.Encode(field, "Station11", incoming.AsSpan(field.Offset));
}
codec.ReceivePlcToApp(incoming, DateTimeOffset.UtcNow);
if (tags.Tag_Metrics.Tag_Title.Value != "PLC label" || tags.Tag_Values[1,2].Value != 12.5f ||
    tags.Tag_Cubes[1,1,1].Value != 9007199254740993L || tags.Tag_Stations[1,1].Tag_Name.Value != "Station11")
    throw new Exception("Typed string/rectangular-array consumer failed.");
Console.WriteLine($"Typed STRING[82], custom STRING[12/128], 2D/3D and UDT arrays consumed. Complete image: {outgoing.Length} bytes each way.");
