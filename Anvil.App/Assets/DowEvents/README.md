# DOW event frames — this folder is RETIRED

**Nothing reads this folder any more, and nothing here ships with the app.** Frames are no longer
bundled; the DOW Event Viewer keeps its own library that you import into at runtime.

## Where the library lives now

```
%LocalAppData%\Anvil\DowEvents
```

`DowEventProvider.EventsDirectory`, mapped to the `dowevents` WebView host. Import frames through
**PastCast → DOW event → Import…**, which copies the chosen `.dow.json` in.

## Why it moved (2026-09-04)

This folder is inside the installed MSIX, which is **read-only** — so a frame could only ever arrive by
being committed and packaged, and no in-app picker was possible at all. It also meant the `*.dow.json`
glob in `Anvil.App.csproj` built whatever ~20 MB sample happened to be sitting here into **every Debug
and Release package**. Both problems have the same fix: a writable library outside the package.

The frame still has to be reachable by the WebView, which *fetches* it — that is why Import copies into
the library rather than pointing at the file where it lies. An arbitrary `C:\…` path is not same-origin.

## If you have a `.dow.json` sitting in this folder

It is not loaded from here. Import it once (the copy is yours to keep or delete afterwards), then this
folder can go.

Frames are produced by [`tools/dow_import.py`](../../../tools/dow_import.py); one file is one
mobile-radar sweep, rendered through the normal radar pipeline and centred on the truck's position. The
file name becomes the list label, so name them clearly. Use **openly-licensed** data only and carry the
source's required citation/acknowledgment (CSWR / FARM) — see `tools/README.md`.
