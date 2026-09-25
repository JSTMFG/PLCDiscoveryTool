# Validation record

## Taskbar filter and developer change notifications — 2026-09-23

- Taskbar Settings saves per-PLC exclusions, including currently undiscovered entries. The widget's context menu excludes its current PLC immediately; Settings can include it again. Exclusions affect only the carousel, not scanning or refresh.
- Refresh compares all five developer fields with each PLC's last successful read and groups every changed field across the refresh into one Windows notification with before/after values. First reads, unsuccessful reads, and changes to tag mappings do not produce false alerts.
- Core regression tests cover route-specific exclusions, empty/all-hidden and re-inclusion states, multiple field changes, first reads, failed reads, and recovery after a stale state. WPF smoke verifies context-menu exclusion, settings round trip, and immediate re-inclusion.
- The grouped alert uses a Windows toast with expandable detail so the notification can contain the complete list beyond the basic 255-character balloon limit. The notification smoke run completed the Windows send call with two changed fields. Actual banner visibility still depends on the user's Windows notification preferences.
- Release build: zero warnings/errors. Console suite: 77 checks passed. WPF and notification smoke runs passed from an isolated copy of the single-file executable.
- Updated `dist/JST PLC Finder.exe` and `dist/JST PLC Finder.zip`. Executable: 168,084,992 bytes; SHA-256 `22350275F283EC44BBC86255BA26FC9BD4FD8146D5C83FB50606F6EAA6F312AE`.

## Task View mode persistence — 2026-09-23

- Runtime taskbar HWND loss, ownership changes, and positioning failures now reconnect the widget without restoring the main window or ending taskbar mode. Explicit restore still cancels recovery and stops the mode timers.
- Added WPF regression coverage for native activation/visibility changes, actual widget HWND destruction and recreation with the same live view, and cancellation of pending recovery. Overlay coverage checks activation, temporary native hiding, and minimize/restore with Always on top both enabled and disabled.
- Release build passed with zero warnings/errors; all 75 console checks and the WPF smoke suite passed. These checks exercise the native-window transition paths; the actual Win+Tab keyboard gesture and virtual-desktop switching were not automated.
- The self-contained executable also passed the smoke suite when copied alone to `artifacts/taskview-isolated`. Updated `dist/JST PLC Finder.exe` and its ZIP; the running copy was left running. New executable: 143,731,618 bytes, SHA-256 `D5A31868344ACECAB326BF5B2838AD17E3FAA32FFE1D877DB3F9A9E15D85CAE5`.

Build date: 2026-09-22. Target: Windows x64 / .NET 10.0.12. SDK: 10.0.401. PLC dependency: libplctag.NativeImport 1.0.41, native libplctag 2.6.0.

Delivered PLC Finder executable: `dist/JST PLC Finder.exe`, 143,567,778 bytes (approximately 137 MiB). SHA-256: `15103938C226088DC68A431E524DBD21D815E351EC1132A8FB6DC8A13A51738C`.

Delivered Echo server executable: `dist/Echo Agent/JST Echo Reset Agent.exe`, 74,669,613 bytes (approximately 71 MiB). SHA-256: `621CC078882C514AD8B35BC0626B971F9BB8FE6495CC99E434DFF2D025F56343`.

Logix Echo reset integration, 2026-09-22: the new self-contained server agent dynamically loads the API already installed with FactoryTalk Logix Echo and uses controller inventory/read/update operations. PLC Finder requires a unique live match using serial number and IP; routed targets additionally require the matching Echo chassis and slot. Reset requests address only an Echo controller GUID and never send a reset or write command to an EtherNet/IP endpoint. The server owns the full Off/On transaction, retries On recovery, serializes resets per controller, and continues after a client disconnect. Agent traffic uses HTTPS certificate pinning, HMAC-SHA256 request signing, timestamp checks, nonce replay prevention, and a generated 256-bit pairing key. The pairing key saved by PLC Finder is protected with Windows DPAPI.

The installed Rockwell API version 3.0.1130 on the development machine was inspected to verify `ListControllers`, `ReadController`, `UpdateController`, controller GUID, serial, IP, chassis, slot, and `IsEnabled`. Its local FactoryTalk Logix Echo services are disabled, so no real controller was turned off or on during development. The remote server must pass `--validate-sdk`, followed by a controlled reset test on a disposable Echo controller.

No-program classification, 2026-09-22: an unsuccessful `STATION.NAME` read now marks both the station label and connection status as `No program loaded`, while retaining other readable values and the underlying diagnostic error. The wider status column and warning color keep the new state visible. The isolated single-executable smoke test passes (exit 0).

Scan optimization, 2026-09-22: 32 concurrent discovery workers, four independent tag readers, bounded queue of 128 rows, overlapped UDP/TCP fallback, and throttled progress reporting. The native loopback fixture confirms four initial reads instead of eight and four fresh reads on refresh. New regression checks cover discovery while reads are blocked, reader concurrency, cancellation with a full queue, and delayed UDP success after TCP failure. Release build and isolated single-executable smoke test pass (exit 0). Real-network speed has not been measured.

Final release build completed with no compiler warnings or errors. The isolated single-file smoke test exited with code 0. Default and compact renders were visually inspected; the compact layout keeps Scan visible and provides horizontal scrolling for the results table.

Shutdown fix, 2026-09-21: closing now sets its shutdown guard before awaiting cancellation. Dialogs and unhandled-exception UI are suppressed while that guard is set, preventing WPF from trying to show a dialog against a closing window. Settings-save failure is trace-only during shutdown. Rebuilt with no warnings; the packaged smoke test exits normally through the close handler.

## Automated checks

- 59 checks pass in the console test harness.
- Multiple disjoint ranges, overlap removal, numeric order, invalid input, CIDR normalization, /31 and /32, and large-range counting.
- STRING/numeric decoding, incorrect types, missing values, stale timestamps, partial results, and retry backoff.
- Identity response validation, truncation rejection, sender-context verification, real local UDP exchange, TCP fallback, and cancellation.
- Actual native-library integration through a loopback EtherNet/IP fixture: direct controller route, two backplane routes, all four station tags, simulation bit 2, inversion, numeric dates, missing GEM tag, session reuse, and pending-read cancellation.
- Fixture rejects unexpected tag services. Source inspection found no PLC write/set API calls in application code. Session Forward Open/Close and socket sends are normal read-connection operations.
- WPF smoke mode checks startup, native-library load, embedded logo, IP/station filtering, sample statuses, a 50-row UI load, and Stop/cancellation controls. It renders default and compact layouts for visual inspection.
- Echo checks cover direct inventory matching, routed chassis/slot matching, serial mismatch rejection, ambiguous inventory rejection, signed-request tampering, and the explicit Off/On transaction. WPF smoke mode also verifies the Echo badge and reset-button eligibility state.
- Single-file release copied alone to an isolated folder and smoke-tested there, including loading the native library from the bundle.

## Remaining hardware/environment checks

- Real CompactLogix and ControlLogix devices, including the actual chassis routes and tag data types.
- Confirm that STATION.OO.2 uses the intended letters and simulation polarity.
- Compare the four values with trusted reads from the user's PLC program.
- Routing across the user's three PLC networks, firewall behavior, connection limits, and acceptable polling load on real hardware.
- Launch on a separate clean Windows x64 computer with no installed runtime or Rockwell software.
- Install the agent on the actual `10.10.10.200` server, run its read-only SDK validation, and test one controlled restart. Confirm the Logix Echo version and FactoryTalk security policy permit the Windows service account to call the local Service API.

The local fixture validates protocol and application behavior, but does not certify hardware interoperability. No production network scan was run while building this program.

## Embedded taskbar mode

- Core suite: numeric PLC ordering, wraparound, empty/single lists, bridge exclusion, routed controllers, additions/removals, and replacement row instances.
- WPF smoke: real popup HWND owned by Explorer's taskbar; main/overlay exclusivity; filter independence; live Developer/SIM bindings; real dispatcher cycling and changed interval; native double-click message restores main and disposes widget; empty state and new discoveries; saved settings round trip.
- Registered-message smoke: hide and restore through the same native message handler used by peer processes; recover when the taskbar owner process disappears. Smoke messages target only the test window, never broadcast to the user's running copies.
- Rendered widget: `artifacts/taskbar-widget.png`.
- Scope: horizontal taskbar on the local Windows 11 build. The taskbar is selected by the main window monitor. Crowded taskbars require choosing unused space; alternate shells and full multi-monitor/DPI/auto-hide combinations have not been exercised.

- Visibility regression: capture actual composed taskbar screen pixels, assert widget bounds stay within the bar and its dark-blue surface is visible. The former child-HWND implementation failed this check despite passing offscreen rendering tests. Screenshot: artifacts/taskbar-screen.closeup.png.


### Taskbar display selection

Settings picker, settings serialization, Automatic default, and disconnected-display preference retention/fallback are checked by the WPF smoke run. The widget was attached and its actual screen pixels verified on all three connected taskbars: Display 5 (3440x1440, 100%), Display 6 (3840x2160, 150%), and Display 7 (2560x1440, 100%). An embedded PerMonitorV2 manifest fixes coordinate virtualization across mixed-DPI displays. Results and captures are in artifacts/taskbar-displays.txt, artifacts/taskbar-display-visibility.txt, and artifacts/taskbar-display-*.png. Pixel checks explicitly report a skip when fullscreen or auto-hide conceals the widget.

### Taskbar auto-refresh

WPF smoke verifies the taskbar refresh switch defaults off, is saved independently of the main monitor, accepts 2–3600 seconds, starts only in taskbar mode, skips reads when no PLCs are detected, and stops on restoration. The main monitor tick is suppressed while taskbar mode is active so the two intervals do not overlap. The existing protocol suite covers the PLC refresh path.

### Mode color palettes

WPF smoke verifies both mode pickers, independent saved preferences, Navy defaults, Emerald resource updates, and Match Windows resolution. The overlay is rendered as artifacts/overlay-emerald.png. The Emerald taskbar widget is checked against actual composed screen pixels and captured in artifacts/taskbar-emerald.closeup.png when visible. Match Windows now reveals the real taskbar surface in taskbar mode; the composed screen capture is artifacts/taskbar-match-windows.closeup.png. The check also confirms that the widget remains clickable. The overlay uses a neutral light/dark surface, shown in artifacts/overlay-match-windows.png. Windows personalization events reapply colors to active modes.

