using System;
using System.Collections.Generic;
using System.Linq;

namespace Anvil.Services
{
	/// <summary>
	/// One Settings row's words: its <see cref="Title"/> (the About caption — a checkbox row has no label, so the title is the
	/// box's own text), its short <see cref="Hint"/>, the hint while the row is greyed (<see cref="HintOff"/>), a hint per choice
	/// for rows whose hint follows the pick (<see cref="Choices"/>), and the long <see cref="About"/> shown in the page header's
	/// About box when the row is selected — or, for a row whose About follows the pick, one per choice (<see cref="ChoiceAbouts"/>).
	/// </summary>
	public sealed record SettingsTextEntry(
		string Key,
		string Title,
		string About,
		string? Hint = null,
		string? HintOff = null,
		IReadOnlyList<string>? Choices = null,
		IReadOnlyList<string>? ChoiceAbouts = null);

	/// <summary>
	/// EVERY word a Settings row shows beyond its label and control — the hints and the About texts — in ONE place, so the
	/// wording can be read, edited and checked together (the rules: docs/ui-patterns.md "Hints"; checked by SettingsTextTests).
	/// A row names its entry by <c>AboutKey</c> (Primitives/SettingsConsoleRow); a hint that follows state is picked in the
	/// console's code-behind or its view model, but its WORDS still come from here (<see cref="Choices"/>, <see cref="HintOff"/>).
	/// Hints computed from live values (Units samples, sizes, free space, frame counts) are data, not wording, and stay computed.
	/// </summary>
	public static class SettingsText
	{
		/// <summary>The About box holds 3 lines; written to fit, a text stays under this (a longer one trims with "…").</summary>
		public const int AboutMaxLength = 280;

		/// <summary>A hint is one short line.</summary>
		public const int HintMaxLength = 72;

		/// <summary>What the About box says while no row on the page is selected.</summary>
		public const string NothingSelected =
			"Nothing selected yet. A setting that can be selected lights up when you hover over it; click it to read more about it here.";

		private static readonly SettingsTextEntry[] Entries =
		{
			// ── General → Startup ──
			new("startup.resume", "Pick up where I left off",
				"Anvil reopens with the modes and panes you had when you closed it. PastCast comes back ready to load, not loaded. On the very first run nothing is on.",
				Hint: "Opens Anvil in the state you closed it"),
			new("startup.mode", "Start in",
				"Anvil opens in the same mode every time, whatever was open when you closed it. This start is never saved over your last session, so switching back to Pick up where I left off still works.",
				Hint: "Select which Cast mode(s) Anvil starts in"),
			new("startup.homeSite", "Home site",
				"Your home radar. It sits at the top of the site picker and can load by itself when Anvil opens. Set it in the Radar Atlas or from a site's map key. Restore defaults clears it.",
				Hint: "Set it in the Atlas or from a site's map key"),
			new("startup.loadHome", "Load home site on launch",
				"When Anvil opens, it starts the live radar loop for your home site, so you're looking at radar straight away. It only happens when Anvil starts in NowCast; a start in Map only, ForeCast or PastCast leaves the radar off.",
				Hint: "Only when Anvil starts in NowCast"),
			new("startup.fullScreen", "Start in full screen",
				"Anvil opens full screen, with the top bar hidden until you move to the top edge. F11, View → Full screen or Esc switch it back for this session; this setting only decides how Anvil starts.",
				Hint: "F11 or Esc leaves full screen"),
			new("startup.mapSetup", "Show map setup at launch",
				"Opens the map setup window before Anvil while it has no map to draw, so you can download one or add a map key. The setup window's own \"Stop showing this at launch\" is this same setting.",
				Hint: "While Anvil has no map set"),

			// ── General → Units ──
			new("units.set", "Unit set",
				"US, Metric and Forecaster each fix every unit below at once and lock the pickers. Custom unlocks them and remembers your own picks, so you can switch to a preset and back without losing them.",
				Hint: "Use Anvil's presets or choose Custom"),
			new("units.clock", "Clock",
				"How local times read across Anvil: 12-hour or 24-hour. Times in UTC always read 24-hour, like 17:04Z, whichever you pick.",
				Hint: null),
			new("units.zone", "Time zone",
				"Whether times across Anvil read in your computer's local time or in UTC, the time forecasters and the NWS products use.",
				Hint: null),
			new("units.date", "Date",
				"How dates read everywhere in Anvil: the event list, PastCast, the Atlas and every readout that shows a day.",
				Hint: null),
			new("units.distance", "Distance",
				"The unit for ground distances: the range ruler, the distance rings and their labels, and radar ranges. Spacing choices on the Rings page are in this unit too.",
				Hint: null),
			new("units.speed", "Speed",
				"The unit for speeds: storm motion, cell movement and wind values in readouts.",
				Hint: null),
			new("units.height", "Height",
				"The unit for heights above the ground, such as echo tops and storm cell heights.",
				Hint: null),
			new("units.hail", "Hail size",
				"How hail sizes read in storm reports and storm cells.",
				Hint: null),
			new("units.coords", "Coordinates",
				"How latitude and longitude read in the cursor readout and anywhere else Anvil shows a place as numbers.",
				Hint: null),

			// ── General → Window ──
			new("window.monitors", "Monitors",
				"Anvil is built for one monitor today: every panel window opens and follows the main window on the same screen. A multi-monitor mode is planned once it can be built and tested on a second screen.",
				Hint: "Not implemented yet"),

			// ── Appearance → Theme ──
			new("theme.theme", "Theme",
				"A theme is the app's colors and its matching basemap, chosen together so they read as one look. Picking a theme also ends any Basemap override below.",
				Hint: "Sets the app theme and map colors together"),
			new("theme.basemap", "Basemap",
				"Draws a different basemap under the weather while keeping the app's theme. It stays until you pick another theme, which brings back that theme's own basemap.",
				Hint: "Overrides the theme-aware map colors"),

			// ── Appearance → Edges ──
			new("edges.pattern", "Pattern",
				"Hatch draws crossing lines where the chrome meets the map; Dots draws a dithered screen of round dots instead. Every other Edges setting applies to both.",
				Hint: "Crossing lines or a dot screen"),
			new("edges.style", "Style",
				"How the band fades into the map: lines that thin, lines that fade, or both. Black shadow turns the pattern off and brings back the plain dark shade the edges used before.",
				Hint: "Black shadow = the old plain shade"),
			new("edges.color", "Line color",
				"The pattern's color. A follows the bar surface, so it changes with the theme; the swatches and + pick a fixed color instead.",
				Hint: "A = the bar surface"),
			new("edges.width", "Band width",
				"How far the band reaches from the chrome into the map, in pixels.",
				HintOff: "Off with Black shadow"),
			new("edges.spacing", "Line spacing",
				"The gap between the pattern's lines, or between dots with the Dots pattern. Wider spacing reads lighter.",
				Hint: null),
			new("edges.weight", "Line weight",
				"How thick each line is, or how big each dot is with the Dots pattern.",
				Hint: null),
			new("edges.opacity", "Line opacity",
				"How strongly the pattern shows over the map. Lower lets more of the map through.",
				Hint: null),
			new("edges.panes", "Hatch the panes' map-side edges",
				"Also draws the band along the map side of each open temporal pane, not just beside the map tools rail.",
				Hint: "Where an open pane meets the map"),

			// ── Radar → Live polling ──
			new("poll.check", "Check",
				"How NowCast checks for a new radar scan. Regime-aware times each check from the radar's own scan plan, so a new scan is caught soon after it's due. Fixed time checks on a steady timer instead.",
				Choices: new[] { "On the radar's own scan plan", "On a steady timer" }),
			new("poll.every", "Every",
				"How long Fixed time waits between checks. Shorter catches a new scan sooner but asks the server more often. Regime-aware doesn't use it.",
				Hint: "Seconds between checks for a new scan", HintOff: "Only with Fixed time"),

			// ── Radar → Memory ──
			new("memory.ram", "This PC",
				"The memory installed in this computer. The slider below is a share of it.",
				Hint: null),
			new("memory.budget", "Radar may use",
				"How much memory a PastCast loop may hold. It sets the longest loop at each Load; the counts are worst case, with every pane a different product. NowCast loops always fit.",
				Hint: null),

			// ── Radar → Site keys ──
			new("sites.hideOthers", "Hide the other sites while one is loaded",
				"While a radar is loaded, the other site keys on the map hide so they don't cover the radar. Click the loaded site's key again to unload it and bring them back.",
				Hint: "Click the loaded site again to unload it"),
			new("sites.favorites", "Show only my home and favorite sites",
				"The map shows keys only for your home site and the sites you've starred. It's the same switch as the one at the foot of the site picker.",
				Hint: "Star sites in the Atlas to add them"),
			new("sites.reveal", "Show only the sites near the cursor",
				"Experimental. A 230 km ring follows the cursor and only the site keys inside it show, plus the loaded site, so a crowded map stays clear until you point at it.",
				Hint: "Experimental · a 230 km ring follows the cursor"),

			// ── Radar → Scrubber ──
			new("scrubber.style", "Style",
				"How the radar console draws the loop's frames: Classic cells, a Dot matrix or a Hatch. Each fills in as frames arrive; Hatch has its own look settings below.",
				Hint: "How the console draws the loop's frames"),
			new("scrubber.preview", "Preview",
				"The console's own scrubber in the style you picked. It shows the loop on the map, or a sample loop until one loads.",
				Choices: new[] { "The loop on the map", "A sample loop until one loads" }),

			// ── Radar → Hatch scrubber ──
			new("hatch.color", "Line color",
				"The hatch scrubber's line color. A follows your Windows accent color; the swatches and + pick a fixed color.",
				Hint: "A = your Windows accent color", HintOff: "Only with the Hatch style"),
			new("hatch.spacing", "Line spacing",
				"The gap between the hatch scrubber's lines. Sizes are set as on the 35 px edge band and scaled to the scrubber's 24 px strip.",
				Hint: null),
			new("hatch.weight", "Line weight",
				"How thick the hatch scrubber's lines are, scaled to its 24 px strip.",
				Hint: null),
			new("hatch.opacity", "Line opacity",
				"How strongly the hatch scrubber's lines show. The current frame is always drawn bolder.",
				Hint: null),

			// ── Rings & Ruler → Rings ──
			new("rings.site", "Site ring",
				"A plain ring at the edge of the radar's data, around every loaded site. It shows even with the map tools Rings key off, and gives way to the styled Reflectivity reach ring while that one is drawn.",
				Hint: "Shown even with the Rings key off"),
			new("rings.refl", "Reflectivity reach",
				"A ring where the reflectivity data ends for the frame on screen. Its look is the Reflectivity reach card below; the range ruler shares its color.",
				Hint: "Where reflectivity data ends"),
			new("rings.vel", "Velocity reach",
				"A ring where the velocity data ends for the frame on screen, usually well inside the reflectivity ring.",
				Hint: "Where velocity data ends"),
			new("rings.distance", "Distance rings",
				"Evenly spaced rings out from the site, in your distance unit, with labels. Their look and labels are set in the cards below.",
				Hint: "Evenly spaced, in your distance unit"),
			new("rings.spacing", "Spacing",
				"How far apart the distance rings are, in your distance unit. Auto picks a step that suits the radar's reach.",
				Hint: "Auto suits the radar's reach", HintOff: "Turn on Distance rings first"),

			// ── Rings & Ruler → each ring's look card ──
			new("ring.color", "Color",
				"This ring's color. A is the default for this ring; the swatches and + pick a fixed color.",
				Hint: null),
			new("ring.color.refl", "Color",
				"The reflectivity ring's color. The range ruler wears the same color, so changing one changes both.",
				Hint: "The range ruler wears it too"),
			new("ring.color.distance", "Color",
				"The distance rings' color. The distance labels can follow it (their A).",
				HintOff: "Turn on Distance rings first"),
			new("ring.opacity", "Opacity",
				"How strongly this ring shows over the radar. Lower lets more of the radar through.",
				Hint: null),
			new("ring.thickness", "Thickness",
				"How thick this ring's line is, in pixels.",
				Hint: null),
			new("ring.line", "Line",
				"The ring's line pattern: solid or one of the dash patterns.",
				Hint: null),

			// ── Rings & Ruler → Distance labels ──
			new("labels.color", "Color",
				"The distance labels' text color. A follows the distance rings' color.",
				Hint: "A = the distance rings' color", HintOff: "Turn on Distance rings first"),
			new("labels.opacity", "Opacity",
				"How strongly the labels show over the radar.",
				Hint: null),
			new("labels.font", "Font",
				"The labels' typeface weight. Only the fonts the map can draw are offered.",
				Hint: null),
			new("labels.size", "Size",
				"The labels' text size, in pixels.",
				Hint: null),
			new("labels.spacing", "Spacing",
				"Extra space between the letters of each label, in ems.",
				Hint: "Space between the letters"),
			new("labels.halo", "Halo",
				"A soft outline around each letter so labels stay readable over bright radar returns. 0 turns it off.",
				Hint: "Keeps it readable over returns"),
			new("labels.haloColor", "Halo color",
				"The outline's color. A is the theme's dark casing.",
				Hint: "A = the theme's dark casing"),
			new("labels.placement", "Placement",
				"Whether each label sits just above its ring, on it, or just below it.",
				Hint: "Where each label sits on its ring"),
			new("labels.lines", "Lines",
				"How many lines of labels run out from the site. More lines put a distance on every side of the radar.",
				Hint: "How many lines of labels run out from the site"),
			new("labels.position", "Position",
				"The bearing the labels sit on, clockwise from north. You can also drag the knob on the outer ring.",
				Hint: "Or drag the knob on the ring"),
			new("labels.showUnit", "Show the unit",
				"Adds the unit to every label, so it reads 100 mi rather than 100.",
				Hint: "100 mi, not 100"),
			new("labels.knob", "Show the drag knob on the map",
				"The knob on the outer ring that moves every label when you drag it. Hide it once the labels are where you want them.",
				Hint: "Hide it once the labels are placed"),

			// ── Rings & Ruler → Ruler ──
			new("ruler.from", "Ruler from",
				"Where the range ruler measures from: the radar site, or your location as a ray out to the ring. Until you place a location, it measures from the site.",
				Hint: "The site until a location is placed"),
			new("ruler.knob", "Knob size",
				"The size of the ruler's knob and the distance labels' knob, which are drawn alike.",
				Hint: "Sizes the ruler and label knobs"),

			// ── Live Data → Source ──
			new("live.source", "Updates from",
				"Where live updates come from. A new product only wakes its layer, which then fetches its own data. NOAA Weather Wire replaces the IEM feed; Polling only checks each layer on its timer.",
				// ⚠️ TIMINGS: IEM's 15–20 s is MEASURED (docs/live-data.md); Weather Wire's is not yet — say a number only once
				// the lag check has run. (The old text had them backwards: IEM is downstream of the NWS's own wire.)
				Choices: new[]
				{
					"About 15–20 s after issuance · no account",
					"Fastest, straight from the NWS · needs an NWS account",
					"Up to each layer's polling interval · no live service",
				},
				// What each source IS, what you GET, what you GIVE UP (canvas artboard 29, 2026-11-18) — the About box follows the pick.
				ChoiceAbouts: new[]
				{
					"Iowa State University's Environmental Mesonet re-broadcasts every NWS product as it's issued, and Anvil listens. You get no account and nothing to set up. You give up a few seconds against the NWS's own wire, and it relies on a free, best-effort university service.",
					"The National Weather Service's own product stream, straight from the source. You get the fastest path, and Anvil notices a missed product and catches up. You give up a free NWS account (asked for by email, can take days) that signs in on one computer at a time.",
					"No live stream: each layer checks for new data on its own timer (Polling, below). You get nothing extra running, and it works on strict networks. You give up speed: a new warning can show up as late as that layer's polling interval.",
				}),

			// ── Live Data → Weather Wire account ──
			new("wire.request", "Get an account",
				"NOAA Weather Wire needs an account from the NWS. The request page (weather.gov/nwws/nwws_oi_request) asks for your name, contact details and one or more accounts; the NWS says processing can take 10 days or more.",
				Hint: "Request by email to the NWS · can take 10 days or more"),
			new("wire.user", "User name",
				"Your NOAA Weather Wire account name, from the NWS. An account can be signed in on one computer at a time. Once saved it locks; Edit unlocks it, Cancel puts the saved name back.",
				Hint: "From the NWS · one computer at a time"),
			new("wire.password", "Password",
				"Kept only in Windows Credential Manager, never in Anvil's settings file, and never shown in Anvil. Once saved it locks; Edit lets you type a new one, Cancel keeps the saved one.",
				Hint: "Kept in Windows Credential Manager"),
			new("wire.account", "Account",
				"Sign in saves the account and signs in; it lights only when something changed. Test connection signs in to NOAA Weather Wire and straight back out with what's typed (or the saved password). It changes nothing.",
				HintOff: "Only with NOAA Weather Wire"),
			new("wire.details", "Account details",
				"What's saved and where: the user name, the Credential Manager entry, the server and how the connection stands. The password is never shown; Credential Manager can show it after your Windows sign-in. Forget account removes it.",
				Hint: "Facts only · never the password"),

			// ── Live Data → Polling ──
			new("polling.feed", "Polling",
				"Seconds between checks for this layer when no live source carries it. While the live feed is connected, a Live layer waits for it instead, with a safety check every 10 minutes.",
				Choices: new[] { "Waits for the live feed while connected", "No live feed carries it" }),
			new("polling.radar", "Radar",
				"The radar keeps its own schedule, set on the Radar page under Live polling.",
				Hint: "Set in Radar → Live polling"),

			// ── Live Data → Connection ──
			new("conn.status", "Status",
				"Whether the live source is connected, and since when.",
				Hint: null),
			new("conn.last", "Last product",
				"The newest product the live source delivered, and when.",
				Hint: null),
			new("conn.missed", "Missed",
				"Products the live source dropped this session. Each missed one wakes its layer's own check, so nothing stays missing.",
				Hint: null),

			// ── Storage → On disk (the table's rows; no hint — each row shows its own short description) ──
			new("storage.maps", "Maps",
				"Every map file Anvil knows about, wherever it lives. Maps have no Clear and no limits: remove one file at a time in the Map section below, so a map is never deleted by accident.",
				Hint: null),
			new("storage.radar", "Radar volumes",
				"The radar data downloaded for the loops you've loaded. Keeping it makes reloading a loop fast. When it passes Max size or Keep for, the oldest go first; the newest half hour is never removed while you watch it.",
				Hint: null),
			new("storage.diagnostics", "Radar diagnostics",
				"Records Anvil writes about each radar session, used to track down radar problems. Nothing reads them while you use the app. Past Max size or Keep for, the oldest are removed first.",
				Hint: null),
			new("storage.weather", "Weather data",
				"Saved copies of outlooks, warnings, watches, storm reports, storm cells and the rest, so a layer draws at once and survives a dropped connection. Clearing is safe: each layer downloads again on its next check.",
				Hint: null),
			new("storage.logs", "Logs",
				"Anvil's own record of what it did and any errors it hit, one file per day. Handy when reporting a problem. Past Max size or Keep for, the oldest days are removed first.",
				Hint: null),
			new("storage.usage", "Your usage history",
				"Which radar sites you load and how long your loops take to load. It feeds the Atlas's \"Your use\" and is always kept. Clear asks first and can't be undone.",
				Hint: null),

			// ── Storage → Map ──
			new("map.inUse", "In use",
				"The map file Anvil draws now.",
				Hint: null),
			new("map.files", "Files",
				"Every map file Anvil knows about. Pick one to use it; Anvil switches maps at once.",
				Hint: null),
			new("map.add", "Add a map file",
				"Adds a map file you already have, without downloading a new one.",
				Hint: null),

			// ── Storage → Offline map ──
			new("offline.area", "Area",
				"The areas your offline map covers. A low-detail map of the whole world always comes too, so zoomed out it draws like the online map.",
				Hint: "A low-detail map of the whole world always comes too"),
			new("offline.detail", "Detail",
				"How far you can zoom in offline. More detail makes a bigger file; the size estimate follows your pick.",
				Hint: null),
			new("offline.saveTo", "Save to",
				"The folder the download goes to.",
				Hint: null),
			new("offline.file", "File",
				"The name the download gets. It spells out the options you picked; a second map with the same options gets -2, -3 and so on.",
				Hint: null),
			new("offline.download", "Download",
				"Downloads the map in the background. Anvil keeps drawing your current map until you switch to the new one, and a stopped download can resume.",
				Hint: "Runs in the background · your current map stays until you switch"),

			// ── Storage → Online map ──
			new("online.key", "Map key",
				"A free Protomaps key draws the map from their servers instead of a file. Test checks the key; Use online map switches to it at once. Free for personal use.",
				Hint: null),
		};

		private static readonly Dictionary<string, SettingsTextEntry> ByKey = Entries.ToDictionary(e => e.Key, StringComparer.Ordinal);

		/// <summary>Every entry, in page order.</summary>
		public static IReadOnlyList<SettingsTextEntry> All => Entries;

		/// <summary>The entry for <paramref name="key"/>, or null.</summary>
		public static SettingsTextEntry? Get(string? key) =>
			key is not null && ByKey.TryGetValue(key, out var entry) ? entry : null;

		/// <summary>The row's static hint ("" when it has none).</summary>
		public static string HintOf(string key) => Get(key)?.Hint ?? string.Empty;

		/// <summary>The hint while the row is greyed ("" when it has none).</summary>
		public static string HintOffOf(string key) => Get(key)?.HintOff ?? string.Empty;

		/// <summary>The hint for choice <paramref name="index"/> of a row whose hint follows the pick ("" when out of range).</summary>
		public static string ChoiceOf(string key, int index) =>
			Get(key)?.Choices is { } choices && index >= 0 && index < choices.Count ? choices[index] : string.Empty;

		/// <summary>The About text for choice <paramref name="index"/> of a row whose About follows the pick; its plain About
		/// when it has none for that choice.</summary>
		public static string ChoiceAboutOf(string key, int index) =>
			Get(key) is { } e
				? (e.ChoiceAbouts is { } abouts && index >= 0 && index < abouts.Count ? abouts[index] : e.About)
				: string.Empty;

		/// <summary>The hint for a row that is on or greyed.</summary>
		public static string HintFor(string key, bool on) => on ? HintOf(key) : HintOffOf(key);
	}
}
