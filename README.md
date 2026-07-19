# Anvil

**Severe-weather workstation for Windows** .Decodes NEXRAD Level II from raw base data, live or
replayed from the archive back to 2008, and GPU-renders it over local, fully style-controlled vector
basemaps, with SPC outlooks, watches, and DOW mobile-radar frames.

Anvil reads raw WSR-88D Level II volumes, decodes the Message 31 base data itself, and renders every
gate on the GPU. No server-side rendering, no image tiles. The basemap is a local PMTiles archive
instead of a tile service, so you control the styling and panning costs nothing.

## What it does

### Radar

- **Real Level II decode.** Reads the raw `.V06` volume directly: Message 31 radial headers,
  per-moment data blocks, VCP and elevation tables.
- **Six products.** Reflectivity, Velocity, Correlation Coefficient, Differential Reflectivity,
  Specific Differential Phase, and Spectrum Width. Reflectivity uses the standard NWS discrete dBZ
  band scale. The rest use ramps built for this app (`docs/radar-products-history.md`).
- **Velocity dealiasing.** A port of the Py-ART region-based algorithm, validated against Py-ART
  itself (`docs/velocity-dealias.md`).
- **Tilt selection.** Elevation cuts resolve from the volume's own elevation table, handling SAILS
  re-scans and split-cut Doppler companions (`docs/radar-tilts.md`).
- **Live loop.** Recent volumes from the AWS archive, plus a near-real-time frame assembled from the
  chunks bucket that cuts the usual 10 minute archive latency
  (`docs/radar-live-frame-internals.md`).
- **Past Event Viewer.** Pick a site, a date back to 2008, a start time, and a window from 30 minutes
  to 12 hours. Scrub or play it through the same pipeline as live (`docs/past-event-viewer.md`).
- **Inspector.** Reads the decoded value under the cursor and marks it on the color scale.
- **Site coverage.** The full WSR-88D network, with TDWRs and research radars as optional layers.
- **DOW frames.** Curated Doppler On Wheels mobile-radar frames render through the same path
  (`docs/dow-event-viewer.md`).

### Severe weather overlays

- **SPC outlooks.** Convective and fire weather, Days 1 through 8, with probability fills and per-CIG
  significant-area hatching. Nested groups are clipped so lower hatch does not show through the
  higher group's gaps.
- **Watches.** Tornado and Severe Thunderstorm watch areas, county-aggregated from the NWS
  watch/warning/advisory service.

### Basemap

Five styles ship with the app: Regular, Dark, Data Viz Light, Data Viz Black, and Data Viz Grayscale.
All read from one local PMTiles archive. Because the tiles and the style JSON are both local, a
cartography change is a file edit rather than a service request.

## Requirements

| | |
|---|---|
| OS | Windows 10 version 1809 (build 17763) or later. Windows 11 recommended. |
| SDK | .NET 8 and Windows App SDK 2.1.3 |
| IDE | Visual Studio 2022 with the Windows App SDK workload |
| Runtime | WebView2. Preinstalled on Windows 11; on Windows 10 install the Evergreen runtime. |
| Basemap | A PMTiles archive. See [Basemap data](#basemap-data) below. |

Visual Studio is effectively required to run Anvil, not just to build it. The app is packaged as MSIX
and depends on package identity for its local caches, so it has to be deployed rather than launched
from a loose executable. Building from the command line works. Running that way does not.

## Basemap data

Anvil ships without a basemap. The archive runs to tens of gigabytes, so it is not in the repo. Set
this up before the first run.

Without it the app still launches and every weather overlay still draws, but the map underneath is
black. That looks like a broken build rather than missing data.

Anvil reads a single [PMTiles](https://protomaps.com/docs/pmtiles) archive built from the
[Protomaps basemaps](https://github.com/protomaps/basemaps), which use OpenStreetMap data. All five
bundled styles point at the same file:

    pmtiles://https://mapdata/usa_full.pmtiles

### 1. Build an archive

Protomaps publishes daily planet builds. Extract only the region you need with the
[`pmtiles` CLI](https://github.com/protomaps/go-pmtiles):

```sh
pmtiles extract https://build.protomaps.com/<YYYYMMDD>.pmtiles usa_full.pmtiles \
  --bbox=-125.0,24.0,-66.5,49.5 --maxzoom=12
```

Max zoom drives file size far more than the bounding box does. A CONUS extract is a few gigabytes at
zoom 12 and tens of gigabytes at full detail. Check the Protomaps documentation for the current build
URL, which has changed over time.

### 2. Name it `usa_full.pmtiles` and put it on your Desktop

Both parts matter right now.

The filename is hard-coded in all five bundled styles and in `SettingsService.MapDataFileName`.

The location matters because the in-app folder picker is one of the controls not yet wired back into
the current UI. Until it returns, Anvil resolves the folder itself, checking in order:

1. Your Desktop
2. `%USERPROFILE%\OneDrive\Desktop`
3. `%USERPROFILE%\Desktop`

Put the archive in any of those and Anvil finds it with no configuration.

To keep it somewhere else, edit `ResolveDefaultFolder` in `Anvil.App/Services/SettingsService.cs`. To
use a different filename, change `MapDataFileName` in the same file and the `url` field in each
`style*.json` under `Anvil.App/Assets/Map/`.








## About this project

Anvil is self-taught work. I have no formal background in meteorology, radar engineering, or
software development, so expect some non-standard choices. Corrections are welcome, particularly on
the meteorology and the Level II decoding.

## Status

Active development, and likely to stay that way for a year or more. There are no tagged releases and
no stability guarantees. The UI is mid-rebuild: several capabilities exist in the view models ahead of
the controls that expose them, so what the app can do is currently ahead of what you can reach on
screen.

**Not for operational use.** This is a personal project for exploring radar data. Use official NWS
products and your local warnings for any safety-of-life decision.
