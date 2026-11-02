# Types and change events

## Type mapping

| PLC type | Generated C# value | Wire bytes | Representation |
| --- | --- | --- | --- |
| USINT | `byte` | 1 | Unsigned 8-bit |
| UINT | `ushort` | 2 | Unsigned 16-bit |
| UDINT | `uint` | 4 | Unsigned 32-bit |
| ULINT | `ulong` | 8 | Unsigned 64-bit |
| DT | `PlcDateTime` | 8 | Signed microseconds since 1970-01-01 UTC |
| TIME | `PlcTime` | 8 | Signed duration in microseconds |

LREAL uses `double`; TIME32 uses `PlcTime32` (32-bit microseconds); LTIME and LDT use
`PlcLongTime`/`PlcLongDateTime` (64-bit nanoseconds). Nanosecond-to-.NET helpers reject
sub-tick precision loss. Classic TIMER/COUNTER PRE/ACC and instruction flags expand
as typed members; flags remain PLC-owned and these members use individual explicit reads.

These work as scalar tags, UDT members and array elements in explicit contracts,
implicit images, the browser, emulator memory, HTTP transport and JavaScript scripts.
Existing BOOL, signed integer, REAL and counted STRING mappings remain available.
New enum values are appended to preserve existing numeric enum identities.

Time wrappers preserve every raw 64-bit value. They avoid narrowing the PLC domain to
.NET ticks or silently rounding through floating point. Conversion helpers are checked:

```csharp
tags.Tag_Telemetry.Tag_StartedAt.Value = PlcDateTime.FromDateTimeOffset(
    new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
tags.Tag_Telemetry.Tag_Elapsed.Value = PlcTime.FromTimeSpan(TimeSpan.FromSeconds(1.5));
DateTimeOffset utc = tags.Tag_Telemetry.Tag_StartedAt.Value.ToDateTimeOffset();
TimeSpan duration = tags.Tag_Telemetry.Tag_Elapsed.Value.ToTimeSpan();
long exactMicroseconds = tags.Tag_Telemetry.Tag_Elapsed.Value.Microseconds;
```

Inputs with sub-microsecond .NET ticks are rejected. Conversions outside the .NET
date/time range throw; the raw value remains representable. Raw constructors preserve
the bits, and do not certify that a controller accepts a date or duration. Controller
family, firmware and native time limits still apply. The wire representation remains independent of CLR ticks.

The emulator uses invariant integer text at its GUI/HTTP boundary, including DT/TIME.
JavaScript `plc.get` returns BigInt for LINT/ULINT/DT/TIME/LTIME/LDT. `plc.set` accepts BigInt,
integer text, or safe integer numbers for these types; unsigned overflow/negative
values fail the entire script tick. USINT/UINT/UDINT use Number without precision loss.

The native UDT decoder uses CIP USINT/UINT/UDINT/ULINT codes C6/C7/C8/C9, DT's UTIME
code C0, and TIME's LTIME code D7. Logix TIME must not be confused with CIP TIME DB.
Generated PLC routines MOVE DT/TIME through a LINT scratch value, then copy its bytes
with CPS. The merge AOI still examines only SINT bytes and prefers PLC changes.

The mappings follow Rockwell's [elementary type reference](https://www.rockwellautomation.com/en-id/docs/studio-5000-logix-designer/38-01/contents-ditamap/instruction-set/elementary-data-types.html),
[time/LINT memory-copy guidance](https://www.rockwellautomation.com/en-se/docs/studio-5000-logix-designer/37-00/contents-ditamap/time-and-date-data-types.html),
and [EDS controller type mapping](https://literature.rockwellautomation.com/idc/groups/literature/documents/pm/1756-pm002_-en-d.pdf).
CIP numeric identities also appear in [GoLogix's type definitions](https://github.com/danomagnum/gologix/blob/master/types.go).
Native Studio 5000 verification and controller interoperability remain pending.

## Notifications

Every generated `GeneratedTags` now derives from `PlcModel`, which implements
`INotifyPropertyChanged`. Each leaf `PlcVariable<T>` implements `IObservablePlcVariable`
and `INotifyPropertyChanged`. Existing hand-written `IPlcModel` implementations continue
to work; derive from `PlcModel` and call `InitializeVariables` to aggregate their events.
Regenerate older generated source to obtain the contract-level events.

```csharp
tags.TagValueChanged += (_, change) =>
{
    Console.WriteLine($"{change.Path}: {change.OldValue} -> {change.NewValue} ({change.Origin})");
};
tags.Tag_Telemetry.Tag_Code.PropertyChanged += (_, change) =>
{
    if (change.PropertyName == nameof(PlcVariable<byte>.Value)) RefreshDisplay();
};
tags.Tag_Grid[1, 1].ValueChanged += (_, change) => HandleArrayElement(change);
tags.NotificationFailed += (_, failure) => LogException(failure.Exception);
```

- `ValueChanged` and contract `TagValueChanged` fire only when `Value` changes according
  to `EqualityComparer<T>.Default`. Repeated identical reads, assignments, duplicate
  implicit images and write acknowledgements produce no value event. The first read
  of a CLR default also produces no value event; `HasObservation` becomes true.
- Local edits have `Application` origin. PLC observations and PLC-winning implicit
  replacements have `Plc` origin. Event arguments retain the old/new values even if
  another update occurs before a consumer processes the event.
- `PropertyChanged("Value")` and `PropertyChanged("BoxedValue")` describe value changes.
  Contract property notifications identify the generated top-level property containing
  that leaf. Nested UDT wrappers and array shapes stay fixed; their leaves notify.
- `ObservedValue`/`PlcValue` notify when readback changes, including the first available
  observation. AppToPlc desired `Value` stays separate from readback. Dirty bidirectional
  values retain pending local edits while observed-value notifications continue.
- `Dirty`, `Quality`, `Version` and `HasObservation` notify only when they change.
  Timestamps remain queryable and advance on valid reads; timestamp refreshes do not
  emit property notifications. Use `TagValueChanged` or filter for `Value` for value work.
- `Variables` is one stable `ReadOnlyObservableCollection<IPlcVariable>`, implementing
  `INotifyCollectionChanged` for structural changes. A fixed generated contract has no
  membership edits, so changing an existing element's value emits property events and
  never a collection Add/Remove/Replace/Reset. CLR rectangular arrays retain their normal
  shape; subscribe to their leaf variables. Assign `.Value`; do not replace array entries.

Callbacks run synchronously on the thread applying the update, after the variable's
state lock is released. No UI SynchronizationContext is captured. Dispatch UI work to
the UI thread, queue lengthy processing, and avoid re-entering session synchronization
from callbacks. Events are per variable, not an atomic all-fields snapshot. Concurrent
writers can interleave callbacks; use event arguments for the individual transition.
Subscriber exceptions are isolated per handler, traced and surfaced through
`NotificationFailed`; they do not turn completed PLC work into a communication failure.

`samples/TypesAndEventsDemo` compiles the generated types and executes these semantics
with both explicit and implicit in-memory transports.
