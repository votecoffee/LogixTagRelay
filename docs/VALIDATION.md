# Validation and remaining acceptance

Validation date: November 2, 2026. Offline-only 0.2 preview.

## Local evidence

- C# solution builds in Release with warnings treated as errors.
- Native bridge builds with MSVC x64 against OpENer commit `0e1ab7ee5c272950c0dc8daf7793008d7a518912`.
- All 77 checks pass on .NET 10.0.12. The offline check harness covers nested UDT/array selection, ownership/access rejection,
  JSON duplicate-key/hash checks, XML entity rejection, CPS destination lengths,
  exact scalar codecs (including LINT above 2^53), write races, failed writes,
  triggered acknowledgements, implicit sequence wrap, bad schema, stale/duplicate frames,
  initialized command requirements and the real DLL ABI through P/Invoke.
- Separate generated-model console applications compile and run. The extended consumer
  uses default/custom STRING, 2D/3D primitive arrays, multidimensional UDT arrays and
  legal one-dimensional UDT member arrays in a 480-byte symmetric image.
- All four local NuGet packages are produced. A fresh consumer with a separate package
  cache restores the package dependencies, consumes the generated model and JSON, builds
  a complete output image and loads native ABI 1. This test starts no EtherNet/IP listeners.
- Additional checks cover counted STRING data (null and 8-bit bytes), capacities through
  65535, native tail preservation, invalid LEN and late-image rejection, compiler-generated
  rectangular-array models, full-image echo, immutable pending commands and duplicate acknowledgements.
- Synthetic module-template tests verify coupled size/data changes and preservation of
  opaque mode/configuration fields. These fixtures are explicitly not native acceptance evidence.
- Browser workflow exercised with a synthetic export: load, select two UDTs, assign
  directions and download C#/JSON/L5X/settings ZIP. No controller address was contacted.

## Required native/controller acceptance

1. Provide a current L5X and controller/Studio 5000 revision for the intended test PLC.
   Check types, permissions, aliases, program scope, target tag naming collisions and array bounds.
2. Add a Generic Ethernet Module using the generated settings. Import the routine on
   a managed offline project copy; resolve context deliberately, verify and re-export.
   Compare all application references, staging arrays, lengths, rung text and watchdog tags.
   The routine is not automatically called or scheduled.
3. Test explicit reads before authorized writes. Compare scalar values, UDT leaves,
   array elements, signed values, BOOLs, REAL and full LINT precision against native readback.
4. For explicit triggers, confirm the PLC holds snapshot data until acknowledgement;
   test a held request, a new request during transfer, an error during a partial write,
   an app restart, a PLC restart and request wrap. Confirm the application's replay policy.
5. Run OpENer on a selected PC NIC with a unique serial. Verify Forward Open direction,
   assembly sizes, Run/Idle headers, unicast behavior, RPI/jitter, packet loss, sequence
   freshness, acknowledgements and graceful shutdown. Build/ABI success does not cover these.
6. Keep ApplyCommands false initially; observe status. Then, with explicit bench-write
   authorization, verify a known staging command and the acknowledgement ordering.
   Test app stalls, lost network, idle PLC, PLC/app restarts and the watchdog's hold/reset policy.
7. Resolve the upstream WIN32 socket-width/MSVC warnings or establish the appropriate
   native evidence before any production-ready claim. Validate package restore and native
   runtime prerequisites on a clean consumer machine.

No controller/runtime acceptance or Studio 5000 import acceptance is recorded here.
No existing PLC project or supplied reference repository was modified.
