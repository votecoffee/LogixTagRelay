# Validation and remaining acceptance

Validation date: November 2, 2026. Version 0.4 preview; offline and loopback emulator evidence.

## Local evidence

- C# solution builds in Release with warnings treated as errors.
- Native bridge builds with MSVC x64 against OpENer commit `0e1ab7ee5c272950c0dc8daf7793008d7a518912`.
- All 135 checks pass on .NET 10.0.12. The offline check harness covers nested UDT/array selection, ownership/access rejection,
  JSON duplicate-key/hash checks, XML entity rejection, CPS destination lengths,
  exact scalar codecs (including LINT above 2^53), write races, failed writes,
  triggered acknowledgements, implicit sequence wrap, bad schema, stale/duplicate frames,
  invalid startup frames, matching baseline requirements and the real DLL ABI through P/Invoke.
- Strict limited ST and ladder interpreters execute the emitted merge AOI and wrapper
  together offline. Cases cover variable array lengths, size mismatch, PLC-first byte
  conflicts, exact baseline capture, frozen snapshots, duplicate/wrong-baseline rejection,
  PLC readback adoption and invalid merged STRING LEN rejection before any destination copy.
  These interpreters do not establish native import, task concurrency or timer behavior.
- DINT trigger checks cover zero/held/change/rollover events, optional exact mirrors,
  positive ACK/negative NACK, partial failures, notification failure and cancellation.
  Generation validates control addresses, DINT metadata and reply write access.
- Separate generated-model console applications compile and run. The extended consumer
  uses default/custom STRING, 2D/3D primitive arrays, multidimensional UDT arrays and
  legal one-dimensional UDT member arrays in a 480-byte symmetric image.
- All five local NuGet packages are produced. A fresh consumer with a separate package
  cache restores the package dependencies, consumes the generated model and JSON, builds
  a complete output image and loads native ABI 1. Generated types compile into a separate
  contract class library referenced by the consuming app; all five packages restore,
  including the Design package's embedded ST resource and Emulation's Jint dependency.
  The 0.4 consumer runs scripted motion through explicit and implicit memory transports. This test
  starts no EtherNet/IP listeners.
- Additional checks cover counted STRING data (null and 8-bit bytes), capacities through
  65535, native tail preservation, invalid LEN and late-image rejection, compiler-generated
  rectangular-array models, full-image echo, immutable pending commands and duplicate acknowledgements.
- Synthetic module-template tests verify coupled size/data changes and preservation of
  opaque mode/configuration fields. These fixtures are explicitly not native acceptance evidence.
- Browser workflow exercised with the sanitized Demo fixture: load, select two UDTs,
  assign directions, configure DINT trigger/response and download the C#/JSON/routine/
  AOI/settings ZIP. Download contents and trigger settings were inspected. No controller
  address was contacted.
- The PLC memory emulator has 23 additional offline checks (included in 135 above)
  for typed shared memory, DINT control tags, PLC-first image merge, duplicate/baseline
  rejection, invalid merged STRING LEN, generated wrapper/ST model agreement, JavaScript
  motion, BigInt LINT precision, persistent state, bounded runaway scripts, atomic tick
  failures, stale script transactions and concurrent unchanged API traffic that must
  not invalidate value snapshots or starve scripts.
- All 14 HTTP checks pass through the actual emulator host/client. They cover explicit,
  implicit and DINT-triggered motion, GUI edits, last-API-value metadata, STRING/LINT/
  multidimensional paths, stale contract rejection, transactional memory restore,
  script errors, write-route headers and static GUI serving. The host uses an owned
  ephemeral loopback port and shuts itself down after the test.
- The published emulator passes the same HTTP checks when invoked by DLL from outside
  its content directory. Static files, examples and dependency license notices are included.
- Browser verification loads a contract and script, changes PLC speed, runs the response
  and displays command/actual/in-position and DINT handled/ACK values. Emulation uses
  HTTP plus the managed contract codec; it is not controller or EtherNet/IP evidence.

## Required native/controller acceptance

1. Provide a current L5X and controller/Studio 5000 revision for the intended test PLC.
   Check types, permissions, aliases, program scope, target tag naming collisions and array bounds.
2. Add a Generic Ethernet Module using the generated settings. Import the routine on
   a managed offline project copy; resolve context deliberately, verify and re-export.
   Compare all application references, staging arrays, lengths, embedded ST AOI, rung text
   and watchdog tags. Verify SIZE reports actual bound InOut array lengths on that controller.
   The routine is not automatically called or scheduled.
3. Test explicit reads before authorized writes. Compare scalar values, UDT leaves,
   array elements, signed values, BOOLs, REAL and full LINT precision against native readback.
4. For explicit triggers, confirm the PLC holds snapshot data until acknowledgement;
   test zero/held/new nonzero requests, positive rollover, optional mirror and signed
   DINT ACK/NACK, a new request during transfer, an error during a partial write,
   an app restart, a PLC restart and request wrap. Confirm the application's replay policy.
5. Run OpENer on a selected PC NIC with a unique serial. Verify Forward Open direction,
   assembly sizes, Run/Idle headers, unicast behavior, RPI/jitter, packet loss, sequence
   freshness, acknowledgements and graceful shutdown. Build/ABI success does not cover these.
6. Keep ApplyCommands false initially; observe status. Then, with explicit bench-write
   authorization, verify a known staging command and the acknowledgement ordering.
   Test app stalls, lost network, idle PLC, PLC/app restarts and the watchdog's hold/reset policy.
   Check conflicting bytes within numeric/STRING values, invalid merged LEN, stale baseline
   rejection and API adoption of PLC-winning values. Regenerate both sides for wire protocol 3.
7. Resolve the upstream WIN32 socket-width/MSVC warnings or establish the appropriate
   native evidence before any production-ready claim. Validate package restore and native
   runtime prerequisites on a clean consumer machine.

No controller/runtime acceptance or Studio 5000 import acceptance is recorded here.
No existing PLC project or supplied reference repository was modified.
