# Release review

## Assessment

The project is suitable for a controlled internal pilot after the changes below. This review does not establish readiness for unrestricted deployment. In particular, device status remains partly inferred from application-specific tags, and the portable Echo agent deliberately trusts the reachable network.

## Fixed in this review

- Reserved queued developer saves so scanning, refresh, or reset cannot silently take their operation slot and cause a false “Saved” message.
- Captured original cell values at edit start and deferred source updates so comparison does not depend on WPF focus-event ordering.
- Paused automatic refresh during cell editing, pending writes, and modal dialogs. Stale/unread tracking values cannot be edited.
- Validated all changed string lengths before writing the first field. Rejected duplicate tag mappings that could overwrite Developer with the automatic date.
- After a write failure, reread actual PLC values rather than displaying a fictitious rollback. Cancellation requires a fresh tracking read.
- Avoided destroying active native read handles during window shutdown.
- Serialized Echo inventory connection and reset preflight with other operations.
- Required a routed Echo controller's own serial number and slot to match inventory; used the routed identity after reset.
- Attempted Echo re-enable recovery even when the SDK throws after the disable request may already have changed the controller.
- Updated Help for inline editing, configurable refresh, automatic backplane discovery, and portable agent operation.

## Verification completed

- 73 automated checks passed. These include the native PLC library against a local EtherNet/IP fixture, discovery/cancellation, backplane decoding, tracking writes, preflight validation, Echo matching, and reset recovery.
- WPF smoke check passed: actual grid edit/cancel, pending-save exclusion, interval/start/stop controls, filtering, compact layout, 50 rows, and cancellation.
- PLC Finder published successfully as a self-contained executable.
- Tests used local fixtures; this review issued no live PLC writes or live Echo resets.

## Remaining release concerns

| Priority | Finding | Recommended next step |
| --- | --- | --- |

| High, deployment-dependent | Portable Echo mode has no caller authentication and the client accepts an unpinned certificate. Any reachable client can request an inventory-authorized Echo reset. This reflects the requested no-key operation. | Keep the initial release on the controlled internal network; use network access restrictions or a future automatic authenticated enrollment flow for broader deployment. |
| High, if installing as a Windows service | The installed agent requires pairing, while the current UI always creates a client without credentials. | Distribute the portable package for this release; align service authentication before claiming installed-service support. |
| Medium | Tracking read timeouts are still presented as “Developer tracking not installed.” | Distinguish missing tags from unavailable reads, and show the underlying read error in a tooltip. |
| Medium | Agent shutdown waits 30 seconds, while reset recovery can run longer. Closing the agent during recovery may interrupt re-enabling. | Extend/drain graceful shutdown and expose outstanding reset jobs before releasing unattended service operation. |
| Medium | Agent request bodies are checked after copying to memory; chunked requests can exceed the intended limit before rejection. Completed reset jobs also have no retention limit. | Enforce limits while reading and prune completed job records for long-running deployments. |
| Medium | Autosizing uses WPF realized cells; long values in offscreen rows may not be included until scrolled into view. | Measure visible-column text across all rows, including headers, while retaining row virtualization. |
| Usability/performance | Refresh skips ticks while busy. Nine sequential reads per controller can make a refresh take longer than the chosen interval, especially with timeouts. | Display cycle duration/next refresh and consider grouping compatible tag reads after benchmarking. Avoid increasing network concurrency without measurements. |
| Usability | Column order and manually adjusted widths are not persisted; only visibility and mappings are. | Persist layout preferences if users arrange their own layouts. |

## Pilot acceptance checks

Use representative direct PLCs, the multi-controller chassis, and both V34/V36 Echo controllers. Verify an authorized tracking edit and claimed date, a failed/partial write, an Echo reset, and a longer monitoring session. Repeat with unreachable devices and while editing. Confirm the server is running the updated portable agent package before evaluating recovery behavior.

## Station-tag classification correction

Read-only diagnostics against 10.10.10.39 identified a 5069-L380ERM/A running firmware 34.11. All four configured station tags returned PLCTAG_ERR_NOT_FOUND both directly and through route 1,0. Replaced the unsupported no-program inference with Station tags not found; station values remain unavailable or explicitly stale. No live writes or resets were performed.
