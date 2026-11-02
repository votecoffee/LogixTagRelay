# Third party notices

OpENer is pinned as the `native/OpENer` submodule at commit `0e1ab7ee5c272950c0dc8daf7793008d7a518912`.
The native bridge links that source into a DLL. Its original license is retained in
`native/OpENer/license.txt` and must accompany native binaries. The application callbacks and
host use OpENer's published integration API; see its WIN32 sample application and main program.
OpENer includes copyright notices from Rockwell Automation and other contributors.

The explicit package references `libplctag` 1.5.2 under MPL-2.0, with its transitive native
packages and their notices. They are separate NuGet dependencies, not copied into the MIT core.
Review the resolved dependency licenses before redistribution. The original user repositories
were read as references; none of their AdvancedHMI, L5Sharp, Scriban, or application code was copied.

The development adapter uses Vendor ID 0, an unassigned identity, and no conformance claim.
Shipping a product requires the appropriate identity and a review of ODVA distribution and
conformance requirements. The generic module is configured without electronic keying during development.
