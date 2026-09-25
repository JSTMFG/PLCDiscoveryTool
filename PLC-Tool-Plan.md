# JST PLC Finder — implementation plan

## Objective

Build a simple, read-only Windows desktop application that discovers Allen-Bradley PLCs and prominently displays each controller's IP address and station name, followed by software date, GEM date, and simulation status. Deliver one portable executable with the logo, runtime, and communication dependencies bundled inside it.

Implementation is now in `src/Jst.PlcFinder`, with the distributable executable under `dist`. See `README.md` for operation and `VALIDATION.md` for completed checks and remaining hardware verification. No production network scan was performed during development.

## Proposed scope and assumptions

- Windows x64 desktop application; no installer, separate runtime installation, Studio 5000, or RSLinx dependency.
- Target CompactLogix and ControlLogix first because the supplied addresses use Logix symbolic tags. Allen-Bradley branding alone does not guarantee support for these addresses; show other discovered AB devices with an explanatory unsupported/needs-configuration status.
- Assume controller-scoped `STATION` tags initially. Allow an optional program scope and controller route in Advanced settings.
- Assume simulation bit 1 means simulation enabled and 0 means disabled, subject to verification against a real PLC.
- Assume dates may be strings or other PLC data types. Inspect sample values and types before choosing decoding; do not invent a date encoding.
- Treat the GEM address as `STATION.EIBHOST[2]` (closing the missing parenthesis in the request). Index 2 is used exactly as supplied.
- Interpret `OO` in `STATION.OO.2` as two letter O characters, subject to checking the live tag definition.

## Main screen

Use the provided `JST.BMP`, embedded as an application resource. Preserve its aspect ratio and original artwork. Its sampled blue is `#0C2D83`.

- Full-width blue header with the logo on a white inset at the left and white title “PLC Finder.”
- Compact toolbar: network adapter selector, **Scan**, **Stop**, **Refresh**, and an Advanced button.
- Show the selected adapter's IP/subnet. Put the **Search ranges** editor, manual IPs, controller slot, and route options in Advanced; the selected ranges remain visible beside the Scan button so the scan scope is always clear.
- Let the user add, edit, enable/disable, reorder, and remove multiple search ranges. Support CIDR (for example `10.10.10.0/24`), an `X` shorthand (for example `10.10.10.X`), a start/end range (for example `10.10.9.1-10.10.9.254`), and individual IP addresses. The initial saved examples should include `10.10.10.0/24`, `10.10.9.0/24`, and `192.168.9.0/24`, but do not scan them until the user enables them.
- Show an estimated host count for the enabled ranges and a confirmation summary before a large scan. Normalize, sort, and de-duplicate overlapping ranges so an address is probed once. Validate IPv4 syntax, reject broadcast/network addresses when they cannot be probed, and warn before scanning unusually large ranges.
- Provide **Scan selected ranges**, **Use local adapter subnet**, and **Clear ranges** actions. Saving named presets such as “Line 1,” “Test,” or “All PLC networks” is optional, but the first release should remember the last range list locally and allow users to restore defaults.
- Central sortable table with IP Address and Station Name as the first and most prominent columns. Give Station Name the most width.
- Additional columns: Connection, Software Date, GEM Date, Simulation, Last Updated.
- Search box filters by IP address or station name. Numeric IP sorting, copyable text, keyboard navigation, readable row height, and resizable window.
- Use text plus color: green Online, amber Partial/Stale, gray Not responding, and a prominent amber SIMULATION badge. Unknown simulation status must read Unknown rather than Off.
- Selecting a row opens a small details area with controller model, serial number when available, slot/route, last successful read, and per-field errors.
- Footer shows scan progress and counts, for example “8 controllers found · 6 fully readable · 2 need attention.”

Illustrative table only; these are not actual discovered devices:

| IP Address | Station Name | Connection | Software Date | GEM Date | Simulation |
|---|---|---|---|---|---|
| 192.168.1.20 | LOAD_STATION | Online | 2026-09-01 | 2026-08-28 | Off |
| 192.168.1.21 | TEST_STATION | Online | 2026-09-10 | 2026-09-08 | ON |
| 192.168.1.22 | Unavailable | Tags unavailable | Unavailable | Unavailable | Unknown |

Dates above illustrate layout only. Actual PLC strings should remain unchanged unless their format is explicitly understood.

## Required tag mapping

| Display field | PLC address | Reading/formatting rule |
|---|---|---|
| Station Name | `STATION.NAME` | Decode the actual Logix string/type; do not substitute the device product name when missing. |
| Software Date | `STATION.SOFTWARE_DATE` | Preserve string content; support another encoding only after verifying its definition. |
| GEM Date | `STATION.EIBHOST[2]` | Read this exact array element and decode its actual type. |
| Simulation | `STATION.OO.2` | Read bit 2 directly if supported, or read the parent integer and evaluate `(value & 4) != 0`. Verify the parent type first. |

Each field has its own value, error state, and successful-read timestamp. One failed read must not discard the other successful fields. The simulation flag is separate from the controller's Run/Program operating mode.

## Discovery and communication

1. Enumerate active IPv4 adapters and show a sensible physical-network default; let the user select the PLC-facing interface.
2. On Scan, send EtherNet/IP ListIdentity discovery on the selected interface. Identify Allen-Bradley responses and distinguish controller endpoints from Ethernet bridges and other devices.
3. Let the user choose one or more explicit IPv4 search ranges before discovery. Support CIDR, start/end, and single-IP entries, including noncontiguous networks such as `10.10.10.0/24`, `10.10.9.0/24`, and `192.168.9.0/24` in the same scan. Use bounded unicast identity requests for these ranges because broadcasts do not cross routed subnets; never scan every attached network automatically.
4. Calculate the normalized target set before scanning, remove duplicates, show the total target count and estimated duration, and allow cancellation. Apply a configurable safety limit (for example 4,096 addresses per scan) with an explicit override in Advanced; require confirmation for ranges larger than the limit.
5. Use EtherNet/IP responses and successful controller communication as connectivity evidence. Ping is optional diagnostic information and must not gate discovery or tag reads.
6. For CompactLogix, resolve the supported controller connection route. For ControlLogix behind an Ethernet module, discover processor slots where supported or request a slot/route through Advanced. Never silently assume slot 0.
7. Represent controllers by IP plus route/slot, allowing multiple processors behind the same bridge IP. Clearly label the displayed address as the connection endpoint in row details.
8. Read only the four required fields, reusing sessions and batching compatible reads when supported.
9. Display results incrementally. Proposed starting limits: four simultaneous connection attempts, one active read batch per controller, two-second operation timeout, and one delayed retry. Tune against real hardware.
10. Refresh known controllers every five seconds while monitoring is enabled; do not rerun broad discovery every refresh. Prevent overlapping refreshes and back off on repeated failures.
11. Make Stop responsive and dispose connections on stop/exit. Handle unplugged cables, adapter changes, and cancellation without freezing the UI.

No tag writes, controller mode changes, downloads, or simulation toggles are part of this application.

## Honest status reporting

- **Online:** controller communication and all requested values succeeded.
- **Partial:** controller is reachable but one or more values cannot be read; show a reason beside the affected field.
- **Device found / route needed:** an AB Ethernet endpoint answered, but a controller route has not been resolved.
- **Not responding:** the endpoint did not respond within the current retry policy. This is not proof that it is physically disconnected.
- **Stale:** previously read values are retained with their timestamps and visibly marked stale. A stale Off simulation value must not appear as a current confirmed Off.
- Report missing tag, denied access when identifiable, unsupported type, route failure, and timeout distinctly. Do not guess access-denied when the device reports only a generic error.

External Access must permit reading the relevant tags. A reachable PLC with inaccessible tags remains visible in the table.

## Recommended implementation

Use C# with WPF on a supported .NET LTS release, with libplctag via its .NET integration for Logix tag access. Validate the exact library/runtime combination in a short hardware proof of concept before building the full UI. Use a small dedicated discovery component for EtherNet/IP identity requests.

Keep separate components for the desktop view, discovery, controller routing/tag reads, typed value decoding, and monitoring state. Give the communication boundary a fake implementation so the UI and error handling can be tested without a PLC.

This approach provides a native Windows table-based UI and existing PLC protocol support. libplctag supports AB communication and bit access; controller routes and custom strings still require correct configuration. Pin validated dependencies and embed applicable license notices in an About/Licenses view, with any additional license obligations reviewed before distribution.

## Single-executable packaging

- Publish a self-contained `win-x64` release named `JST PLC Finder.exe` with single-file bundling.
- Embed the logo, icons, default settings, runtime, and native communication libraries. Exclude debug symbols and loose content from the release package.
- Use `PublishSingleFile=true`, `SelfContained=true`, and `IncludeNativeLibrariesForSelfExtract=true`; confirm the PLC library's native loading works from the published bundle. Avoid WPF trimming unless explicitly verified.
- No required configuration, image, DLL, or other companion files beside the executable. Save range and connection preferences in the per-user registry (`HKCU\Software\JST\PlcFinder`) so they persist without a companion settings file.
- Packaging distinction: this produces one file to distribute, but .NET/native components may extract into a per-user runtime cache during execution. If “no other files” also prohibits temporary/runtime extraction, resolve that requirement before implementation and evaluate a native statically linked build instead.
- Test on a clean Windows machine with no .NET runtime or Rockwell software installed, under a standard user account, and with no internet connection. Check local firewall behavior without disabling the firewall or opening blanket rules.

## Implementation stages and completion criteria

1. **Communication and packaging proof:** connect to a known PLC IP, resolve its route, read all four fields, verify actual data types and simulation polarity, and run the packaged executable on a clean machine. This resolves the highest-risk assumptions first.
2. **Discovery:** add adapter selection, multi-range editing, CIDR/start-end/single-IP parsing, target normalization, safety limits, cancellation, local discovery, explicit range/manual IP support, and route-aware results. Verify that a scan can cover `10.10.10.x`, `10.10.9.x`, and `192.168.9.x` together, that overlapping ranges do not duplicate probes, a controller with ICMP blocked remains discoverable, and a bridge is not mislabeled as a PLC.
3. **Branded UI:** implement the blue JST header, embedded logo, prominent IP/name columns, filter/sort, progress, and row details using fake devices first and then real discovery.
4. **Monitoring and failures:** add refresh, cancellation, field-level errors, stale timestamps, bounded parallelism, reconnect/backoff, and session cleanup.
5. **Release validation:** verify single-file distribution, embedded notices, clean-machine startup, offline operation, responsiveness, and correct readings on representative CompactLogix and ControlLogix hardware.

Acceptance checks:

- A known reachable controller appears with its correct endpoint IP, route where needed, and exact `STATION.NAME` value.
- Software/GEM values match Studio 5000 or another trusted read of the same tags.
- Simulation displays On and Off correctly for controlled test values; failed reads display Unknown or explicitly stale status.
- Missing tags, read restrictions, nonstandard string layouts, and disconnected devices do not crash or stall the application.
- Multiple processors behind a shared bridge remain separate rows; repeated discovery does not create duplicate rows for the same endpoint/route.
- Network unplug/reconnect, an empty scan, mixed Ethernet devices, blocked ICMP, and subnets requiring explicit range entry are handled clearly.
- The user can enable the three example /24 ranges in one scan, see the normalized target count before starting, cancel a large scan, and resume with the saved range list later.
- A 50-controller simulated workload stays responsive and respects concurrency limits; real-hardware polling is checked for acceptable controller/network load.
- A packet capture of representative scanning/monitoring confirms that the application issues no tag-write or controller-mode-change operations.
- Only the executable needs to be copied to a supported clean PC; the original BMP is not required at runtime.

## Technical references

Implementation refinements: discovery uses up to 16 bounded probes, while tag reading remains limited to four controllers at once. UDP discovery has a TCP fallback. Large scans have an adjustable confirmation threshold and a hard cap of 65,536 addresses. Controller routing supports direct connected messaging and explicit numeric port/slot routes (segments 0–15); extended routing and custom STRING layouts remain outside this release. Preferences persist in the registry; named presets remain optional future work.

- [Microsoft: single-file deployment and native extraction](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
- [Rockwell: EtherNet/IP discovery port behavior](https://www.rockwellautomation.com/en-in/docs/factorytalk-edge-gateway/distributed-1-00/ft-edge-gateway-help-ditamap/data-sources/data-source-configuration/add-auto-discovery-and-model/ftlinx-and-ft-logix-echo-binding.html)
- [Rockwell: tag External Access](https://www.rockwellautomation.com/en-us/docs/studio-5000-logix-designer/38-02/contents-ditamap/about_external_access.html)
- [libplctag project and licensing](https://github.com/libplctag/libplctag)
- [libplctag tag attributes, strings, and routes](https://github.com/libplctag/libplctag/wiki/Tag-String-Attributes)
- [libplctag API, including bit suffix access](https://github.com/libplctag/libplctag/wiki/API)
