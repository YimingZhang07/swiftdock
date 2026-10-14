# Task 9 — website icons

Implemented on top of the earlier tasks staged in the index. Task 9 changes are unstaged; new files are untracked. No packages or standalone probe programs were added, and no staging commands were run.

## Behavior and decisions

- URL tiles and editor previews display the frozen globe immediately, then replace it asynchronously. Custom icons, including missing custom files that fall back to the globe, never trigger favicon discovery. Import and launch code need no favicon special cases. Window show and prewarm do not await website requests.
- Website-directed HTTP/HTTPS redirects and HTML-referenced icons may cross hosts and ports, including apex → www and CDN hosts. No third-party favicon lookup service is constructed or queried. HTTPS → HTTP redirects are allowed: this unauthenticated decorative fetch sends no cookies or authorization, rejects URL userinfo, and applies the same host guard on every hop. Such HTTP content has the usual lack of transport integrity.
- Public items cannot reach `localhost` (including subdomains/trailing dots), loopback, unspecified, RFC 1918 private, IPv6 unique-local, or IPv4/IPv6 link-local IP literals. IPv4-mapped IPv6 is checked as IPv4. Items whose original host is itself localhost or one of these local literals may also access local destinations, preserving intranet support. Classification inspects host text only, with no DNS lookups, so it does not protect against a public-looking hostname resolving to a private address. The original item's host remains the basis for the guard throughout redirects.
- The tolerant tokenizer handles attribute order, quoting, case, rel tokens, multiple sizes, `any`, HTML entities, relative URLs, protocol-relative URLs and the first base href. Comments and raw script/style/text content are skipped. Known raster candidates are sorted before unknown types, then by the smallest declared adequate size or largest smaller size. Fallbacks are `/favicon.ico` on the final page's origin, then the item's original origin, deduplicated and always last. Even a non-HTML/error response preserves the final page origin for fallback. WIC decides actual format support, including WebP; undecodable candidates fall through. Frame selection is shared with file icons.
- Production uses one shared `HttpClient` with a cookie-free, decompressing `SocketsHttpHandler`. Redirects are followed manually so every hop is checked; at most five redirects per request. The four-second request token covers headers and body. Active discovery has a ten-second budget; a shared semaphore allows four active sites, and each origin shares pending discovery. Refresh cancels obsolete work and serializes its replacement. HTML reads stop at 512 KiB; images over 1 MiB are rejected, including streams without Content-Length. Images must have an image content type; HTML must be `text/html`.
- A source at least 75% of the requested maximum dimension fills the box using high-quality scaling and preserved aspect ratio. Smaller sources use the largest integer multiple fitting within 62% of the box, on a white rounded plate with 22% corner radius and a one-pixel black border at alpha 0x14. Sources between 62% and 75% are reduced slightly using high-quality scaling. Nearest-neighbor is used only for exact integer scales.
- Cache identity is SHA-256 of origin, physical pixel size and `favicon-v1`. Frozen memory images and atomic `.favicon` records live in the existing icon-cache directory. A record contains expiry ticks and the rendered PNG, or only expiry for failure. Success lasts 30 days; failure lasts one day. Expired tile entries are revisited lazily on window show. Refresh bypasses both caches for the origin and updates matching tiles. Cancelled work from disable/refresh is not negatively cached.
- Settings → 外观 → “自动获取网站图标” defaults on. Optional `autoFetchWebsiteIcons: false` stores opt-out; the default is omitted. Read-only profiles disable the checkbox. Turning off cancels requests and restores globes without using favicon caches; turning on loads lazily. Each actual discovery writes one `favicon.result` line containing only origin, outcome and elapsed milliseconds, never the page path or query.

## Files changed

| Area | Files |
| --- | --- |
| Discovery, cache and rendering | New `QuickLaunch/Services/FaviconParser.cs`, new `QuickLaunch/Services/FaviconService.cs`, `QuickLaunch/Services/IconService.cs` |
| Configuration and presentation state | `QuickLaunch/Models/AppConfig.cs`, `QuickLaunch/Services/ConfigValidation.cs`, `QuickLaunch/ViewModels/MainViewModel.cs`, `QuickLaunch/ViewModels/IconVisual.cs` |
| Settings, tiles, refresh and preview | `QuickLaunch/Views/SettingsWindow.xaml`, `QuickLaunch/Views/SettingsWindow.xaml.cs`, `QuickLaunch/Views/LauncherWindow.xaml.cs`, `QuickLaunch/Views/LauncherWindow.Management.cs`, `QuickLaunch/Views/EditItemDialog.xaml.cs` |
| Tests and fake HTTP | New `QuickLaunch.Tests/FaviconTests.cs`, new `QuickLaunch/Diagnostics/FaviconFixtureHandler.cs`, `QuickLaunch.Tests/LauncherInputTests.cs` |
| Desktop smoke integration | New `QuickLaunch/Diagnostics/FaviconSmoke.cs`, `QuickLaunch/Diagnostics/PrototypeSmoke.cs`, `QuickLaunch/Diagnostics/DialogAppearanceSmoke.cs`, `QuickLaunch/App.xaml.cs` |
| Documentation | `README.md`, `docs/QUICKSTART.zh-CN.md`, this file |

## Reviewer follow-up — redirects and CDN icons

The reviewer reported **315/315 unit tests** and **403/403 desktop smoke checks** passing outside the sandbox for the initial implementation. The follow-up changes only `FaviconParser.cs`, `FaviconService.cs`, `FaviconTests.cs`, `README.md`, `docs/QUICKSTART.zh-CN.md` and this report.

- Replaced exact-origin restrictions with the host guard described above, for page redirects, candidates and icon redirects. HTTPS → HTTP is deliberately allowed under that same guard. Cache/deduplication keys and result logging still use the item's original origin.
- Added final-origin then original-origin fallback, retaining the final URL even for non-HTML or unsuccessful page responses. Existing same-origin fallback deduplication remains covered.
- Fake-handler tests cover apex → www, absolute/protocol-relative/base-relative CDN references, public-to-local candidates and redirects at later hops, localhost/private IPv4/IPv6 intranet items, userinfo rejection, local address range boundaries and IPv4-mapped IPv6, downgrade behavior, fallback order and original-origin cache/log identity. No real network requests or probe programs were used.
- `.\scripts\Validate.ps1`: **exit 1**. Locked restore succeeds; Release `-warnaserror` build has **0 warnings / 0 errors**. **364 passed / 6 failed / 0 skipped / 370 total**; all **83 favicon tests pass**. The six failures are the same four `File.Replace`, one named-pipe and one registry access-denied failures listed below. No tests were skipped or weakened. Desktop smoke was not rerun for this fetch-policy-only revision.
- `git diff --check`: **pass**. The git index SHA-256 remains `E3C55E28E10A33AF65F3F8CD3324996002FEA89483AAE6A53D30C1B05A20C9EF`, matching the start of this follow-up. No packages or index changes.

## Initial validation — 2026-10-13

- Final `.\scripts\Validate.ps1`: **exit 1**. Locked restore succeeds. Release `-warnaserror` build succeeds with **0 warnings / 0 errors**. Tests: **309 passed / 6 failed / 0 skipped / 315 total**. All **28 favicon tests pass**, covering parser/order, HTTP redirects/content/byte limits, request and overall timeout caching, success/failure expiry across restarts, deduplication, concurrency, cancellation and refresh, custom/disabled zero-request behavior, rendering and query-free logging.
- `.\scripts\Validate.ps1 -DesktopSmoke` was also attempted: **exit 1 before the smoke phase**, with the same six access-denied failures. That earlier run preceded three additional favicon tests: **306 passed / 6 failed / 312 total**, with a clean build.
- The six failures match the previous validation reports: `ConfigTests.SlowWriteAndQueuedRevisionsKeepNewestSnapshot`, `LegacyAutoHideSettingIsRetiredWithoutLosingItems`, `SnapshotRoundtripCreatesBackupAndOmitsVolatileFields`, and `FailureKeepsOldFileAndAllowsSameRevisionRetry` fail at `File.Replace`; `HotkeyAndInstanceTests.SecondaryForwardsToPrimaryWithoutOpeningAnotherInstance` fails at the named pipe; `InteractionTests.StartupRegistryRoundtripTouchesOnlyTestKey` fails at the test registry key. Existing checks were not skipped or weakened.
- Final separate `.\scripts\Run.ps1 -Smoke`: **exit 1**, **376 passed / 3 failed / 379 recorded checks**, with `completed: false` after a later existing configuration save receives access denied. The three recorded failures are `tray-created`, `root-capture-acquired`, and `capture-survives-hidden-source-group`. All **nine new favicon smoke checks pass**, including globe-before-arrival, eventual replacement, failure fallback, custom priority, settings toggles, opt-out persistence, default omission and refresh. The updated read-only settings check also passes.
- All favicon requests in tests and smoke use the injected `FaviconFixtureHandler`; it has no network fallback. No live website tests were performed.
- `git diff --check`: **pass**. Index SHA-256 checked during final review remained `E3C55E28E10A33AF65F3F8CD3324996002FEA89483AAE6A53D30C1B05A20C9EF`.

Raw output remains in `artifacts/tests/launcher.trx`, `artifacts/validation/smoke-result.json` and `artifacts/validation/quicklaunch-*.jsonl`.

## Snapshot

`artifacts/validation/launcher-favicons.png` was generated and visually inspected. The dark launcher contains a large 128-pixel PNG, a tiny 16-pixel ICO with a dark mark on transparency, a failed site retaining the globe, and a fourth fake site with a custom star that suppresses fetching. The plate keeps the small dark mark crisp and visible.

This is a WPF render snapshot, not a DWM/physical-screen capture. Actual browser drag/drop, real-site compatibility and monitor DPI transitions still need the reviewer's desktop check. The image and raw validation files remain under the existing ignored `artifacts/` directory.
