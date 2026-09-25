# PLC Discovery Tool (JST PLC Finder)

JST PLC Finder is a Windows utility for controls, maintenance, and commissioning teams that need to identify Allen-Bradley Logix controllers on networks reachable from a Windows PC. It helps answer practical questions such as **which CompactLogix or ControlLogix stations are online, what station is at an address, and what configured status or date tags report?**

The scanner discovers controllers and reads configured tags so a technician can build a live view of reachable equipment, refresh known stations, and spot missing or stale data. It does not write tags or change a physical controller's mode. It does not assign addresses, create routes, or replace Studio 5000 or RSLinx.

## What it does

- Discovers CompactLogix and ControlLogix controllers by local-network broadcast or selected IP ranges.
- Displays controller addresses, station names, connection status, configured date fields, and simulation state as results arrive.
- Supports one device, address ranges, CIDR notation, and whole `/24` subnets; overlaps are combined before scanning.
- Refreshes known stations on demand or on a timer, with stale values and read errors identified in the interface.
- Supports direct controller connections and numeric Ethernet bridge routes for multiple processors.
- Provides compact overlay and Windows taskbar views for keeping station information visible while using other applications.
- Optionally works with a separate Echo agent to restart one uniquely identified **emulated Logix Echo** controller. This optional action is not available for physical, unmatched, or ambiguous devices; see [Echo agent setup](ECHO-AGENT-SETUP.md).

## Download

Download the latest ready-to-run Windows x64 build from [GitHub Releases](https://github.com/JSTMFG/PLCDiscoveryTool/releases/latest). The release ZIP contains the self-contained PLC Finder executable; extract it and run `JST PLC Finder.exe`.

## Run the application

After building or obtaining a release, launch `dist/JST PLC Finder.exe` on a Windows x64 computer. The published executable is self-contained and includes the .NET runtime and native PLC communication library. Normal use does not require an installer, internet access, Studio 5000, or RSLinx. Third-party license information is available in **Help / About**.

1. Enable or edit the search ranges in the left pane. Example ranges are provided but start disabled.
2. Choose **Scan selected ranges** or **Discover local network**.
3. Review results as they arrive. Use **Refresh** for current readings or enable **Auto-refresh** to monitor known stations.
4. Use **Stop** to cancel an active scan or monitoring operation.

Only scan networks you are authorized to inspect. The target networks must already be reachable through Windows network configuration. The application does not change PC routes or PLC configuration.

## Search ranges and network behavior

| Input format | Example |
| --- | --- |
| Whole `/24` subnet | `10.10.10.X` or `10.10.10.*` |
| CIDR | `10.10.9.0/24` |
| Inclusive start and end | `192.168.9.10-192.168.9.80` |
| Single device | `10.10.10.20` |

`/24` and `X` ranges scan `.1` through `.254`; `/31` and `/32` keep all host addresses. Explicit start/end ranges are used literally because the app cannot infer their subnet mask. The target count is shown before scanning. Scans are capped at 65,536 unique addresses, and a confirmation is requested above the configurable large-scan threshold (4,096 by default).

Local discovery uses broadcast on one selected adapter. Range discovery uses Windows routing and checks addresses concurrently, with UDP discovery followed by TCP fallback. If discovery and the operating-system route select different local interfaces, the UI reports **Route mismatch** rather than reading tags through an unexpected local address. Resolve the PC route or use Automatic adapter selection when appropriate.

## Controller data and routes

By default, the app reads these Logix tags:

| Display field | Tag | Default interpretation |
| --- | --- | --- |
| Station name | `STATION.NAME` | Logix STRING |
| Software date | `STATION.SOFTWARE_DATE` | Logix STRING |
| GEM date | `STATION.EIBHOST[2]` | Logix STRING |
| Simulation | `STATION.OO.2` | Bit 2 set means ON |

`OO` is two letter O characters. Advanced settings support program scope, inverted simulation polarity, and numeric date-tag types (`DINT`, `LINT`, `INT`, `REAL`). Numeric tag values are shown raw; no date encoding is assumed. STRING decoding expects a 32-bit length followed by character bytes.

The default connection addresses the controller's Message Router directly and assumes no backplane slot. For an Ethernet bridge, configure its processor route in Advanced settings; for example, `1,0` means backplane port 1, slot 0. Multiple routes such as `1,0;1,2` create separate rows. Numeric route segments 0–15 are supported; extended IP/DH+ routing is not.

Other Allen-Bradley devices may be discovered, but this release does not implement PLC-5, SLC, MicroLogix, or Micro800 tag maps. Tags must permit external reads. An unavailable station-name tag does not hide a device; unreadable fields are marked accordingly, and last successful readings are marked **stale** after a failed refresh. **Not responding** means there was no timely EtherNet/IP response; it is not proof that a device is physically disconnected. Simulation state is separate from controller Run/Program mode.

## Optional Logix Echo reset agent

The main scanner sends discovery, connection-management, and tag-read requests; it has no tag-write or physical controller-mode-change feature. If configured, the separate Echo agent can use Rockwell's local Service API to turn one positively identified emulated Echo controller off and back on. The app requires a live, unique inventory match using controller GUID, serial number, IP, chassis, and route/slot before enabling that action. Agent requests use HTTPS certificate pinning, a pairing key, request signing, timestamp checks, and replay protection. Setup and operating requirements are in [ECHO-AGENT-SETUP.md](ECHO-AGENT-SETUP.md).

## Build and validate from source

Building requires Windows and the .NET 10 SDK. From the repository root, run:

```powershell
./build.ps1
```

The script runs the tests and publishes self-contained Windows x64 executables into `dist`: PLC Finder and the optional server agent under `dist/Echo Agent`. It also refreshes both ZIP packages, including `dist/JST Echo Reset Agent - Portable.zip` with the agent launch scripts. Source projects are under `src`; tests are under `tests/Jst.PlcFinder.Tests`. NuGet dependencies are pinned with lock files. The script uses `.tools/dotnet` when that local SDK is present, otherwise it uses `dotnet` from `PATH`.

The integration tests use a loopback-only fake EtherNet/IP PLC on TCP port 44818; they do not contact production PLCs. They cover direct and routed connections, tag reads, bit access, missing tags, session reuse, and cancellation. The fake endpoint rejects unexpected tag services.

To run the app's explicit visual smoke test after building:

```powershell
& './dist/JST PLC Finder.exe' --smoke-test
```

This mode loads private sample rows, checks several window layouts and controls, writes results to `artifacts`, and exits. Normal app use does not create those test artifacts. Generated build output, local SDK files, and artifacts are excluded from Git.

## Display modes and saved settings

The standard window includes a filterable station list. **Overlay mode** provides a compact resizable list with configurable fields, opacity, and color. **Taskbar mode** displays one selected controller beside the Windows clock/taskbar area and can rotate through included stations. Its display, scroll interval, refresh interval, color, and PLC inclusion list are configurable. The overlay and taskbar modes keep separate visual settings; main-window auto-refresh is independent of taskbar auto-refresh.

Under **Settings > Notifications**, each Windows alert can be switched on or off separately: a PLC stops responding, a PLC responds again, an automatic scan finds a new device, or Developer tracking fields change. Connectivity alerts work in Main, Overlay, and Taskbar modes after a completed scan or refresh. Windows notification settings may also silence the app.

Preferences are stored per Windows user under `HKEY_CURRENT_USER\Software\JST\PlcFinder`; no settings file needs to be distributed. As with other self-contained .NET applications, runtime components may be extracted to the user's .NET cache when the program starts.

## Project notes

- [Validation notes](VALIDATION.md) describe development-PC checks and remaining validation limits. Physical PLC reads, plant routing, firewall policies, actual tag types/polarity, and a separate clean Windows machine still need field verification.
- [Release review](RELEASE-REVIEW.md) contains the delivered-build review.
- The current Windows executables are unsigned.
