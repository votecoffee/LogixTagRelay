# Contract projects

Save/open `.ltrx.json` projects in the browser to retain catalog, selections, generated
C# identity, update settings and assembly instances. Each download also contains the
project. Browser projects embed their catalog; CLI projects may instead reference a
relative `sourceL5x` with an optional `sourceSha256` pin. Refresh the export explicitly
when controller definitions change. Native module templates are selected separately.

Use a dedicated contract class library with these package references (local preview
feed until publication):

```xml
<ItemGroup>
  <PackageReference Include="LogixTagRelay" Version="0.8.0-preview.1" />
  <PackageReference Include="LogixTagRelay.Build" Version="0.8.0-preview.1" PrivateAssets="all" />
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
