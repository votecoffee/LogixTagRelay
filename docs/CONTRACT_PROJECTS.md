# Contract projects

For a complete walkthrough from the ZIP to a working .NET app, see the
[consumer manual](CONSUMER_MANUAL.md).

Use the browser's contract list to add, edit, and delete explicit contracts and
one implicit contract. **Save contract** validates the contract and stores it in the
current browser. **Export project** downloads one `.workspace.json` containing every
saved contract and its module attachment, if present. **Download interface** creates
one ZIP with that workspace file and a folder for each contract. Each folder includes
its `.ltrx.json`, generated C# and configuration. The implicit folder also includes
the PLC artifacts.

**Open or import** opens a `.workspace.json` as the current project, with confirmation
before replacing existing entries. It can also import a single `.ltrx.json` into the
current project. For a contract that references `sourceL5x`, choose the companion
L5X export when prompted. The browser verifies an optional `sourceSha256` pin and
stores an embedded catalog. Refresh the catalog when controller definitions change.

In the implicit editor, the optional Generic Ethernet Module export supplies the
native module definition for the generated module import. The selected catalog
tags and UDT members determine the minimum SINT image size in each direction.
Leave the override blank to use that size automatically, or enter a larger aligned
size up to 480 bytes. An attached module template is resized to the generated image.
Explicit contracts address tags by name. Each contract may have a separate optional
BOOL heartbeat that uses explicit messaging and defaults to five seconds.

Use a dedicated contract class library with these package references (local preview
feed until publication):

```xml
<ItemGroup>
  <PackageReference Include="LogixTagRelay" Version="0.8.1-preview.1" />
  <PackageReference Include="LogixTagRelay.Build" Version="0.8.1-preview.1" PrivateAssets="all" />
  <LogixContract Include="telemetry.ltrx.json" />
</ItemGroup>
```

Normal `dotnet build` generates source under `obj/.../LogixTagRelay`, compiles it into
the class library, and copies JSON to `contracts/<project-file-stem>/logixrelay.json`
under its output directory and propagates it to referencing applications. The generated
class also exposes `CreateConfiguration()` with its embedded matching configuration.
Consumers reference that library for ordinary compiler
checking and IntelliSense. Do not also compile a downloaded manual GeneratedTags.cs.
Use distinct file stems and namespaces for multiple contract projects in one library.

Project validation errors appear as `LTR001` MSBuild diagnostics. Type checking of the
generated API uses the ordinary C# compiler. Generation does not browse or write a PLC.
Outputs are only rewritten when their text changes; referenced L5X files are read on
every generation, so changed sources are not hidden by stale incremental timestamps.

The standalone generator also supports:

```powershell
dotnet run --project src/LogixTagRelay.Build -- generate path/to/contract.ltrx.json path/to/output
dotnet run --project src/LogixTagRelay.Build -- compare old/logixrelay.json new/logixrelay.json
```

Comparison reports member removals, additions, changed types/access/layout, image
lengths and assembly settings. Additions still change the wire schema: regenerate both
PLC and API peers together. Runtime model/config matching remains mandatory.

`samples/ContractProjectDemo` demonstrates source-bound build generation from this repo.

## IntelliSense descriptions

Generated XML summaries use exported tag descriptions and operand comments, then
UDT member defaults or the UDT type description where applicable. Multiline text
is retained as XML documentation paragraphs and escaped safely. Program-scoped,
nested and indexed operand comments are preserved in saved catalogs. Exports with
localized documentation use CurrentLanguage, or en-US when unspecified.

If several instances share one generated UDT type but have different member
comments, IntelliSense remarks list the descriptions with their PLC paths. Array
element overrides are listed similarly. This preserves the generated CLR type
identity and rectangular array element type. Missing comments retain path/type
fallback summaries. Description edits do not change the wire schema.

The current live CIP browser does not retrieve documentation; load an L5X export
containing comments to include them. See [release verification](RELEASING.md) for
compiled XML documentation and package-consumer checks.
