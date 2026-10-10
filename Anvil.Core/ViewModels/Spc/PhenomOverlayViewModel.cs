using System;
using System.Threading.Tasks;

namespace Anvil.ViewModels
{
	/// <summary>The flood row of BOTH watch overlays (NowCast + PastCast). ⚠️ The colour is mirrored in
	/// watches.js STYLE (FA/FL/FF) — NWS's own Flood Watch SeaGreen; change both.</summary>
	public static class WatchFlood
	{
		public const string Label = "Flood";
		public const string Color = "#2E8B57";
	}

	/// <summary>
	/// Base for a CONVECTIVE ALERT overlay whose features carry a `phenom` of TO (tornado) or SV (severe
	/// thunderstorm) — the SPC watch boxes and the storm-based warning polygons, the latter plus an
	/// optional third FF (flash flood) type (<see cref="SupportsFlashFlood"/>). Sits between
	/// <see cref="MapOverlayViewModel"/> (source / visibility / opacity / map-ready latch) and the two
	/// concrete VMs, and adds everything the NowCast window's two alert sections need: one toggle per
	/// phenomenon, that phenomenon's live count, and the section's summary card.
	///
	/// <para>⚠️ IT EXISTS FOR THE SAME REASON <c>geojson-overlay.js</c> DOES. Watches and warnings render
	/// through one shared JS factory because their map layers are the same shape; their view models are
	/// the same shape too, and this is where that lives. A third TO/SV overlay should derive from this,
	/// not copy it.</para>
	///
	/// <para>⚠️ THERE IS NO MASTER SHOW/HIDE ROW. The section used to carry one checkbox reading
	/// "Tornado / Severe Thunderstorm"; it is now one checkbox per type, plus a select-all in the section
	/// HEADER (<see cref="AllShown"/> / <see cref="ToggleAll"/>) that only writes the type ticks.
	/// <see cref="MapOverlayViewModel.IsVisible"/> is still the base's visibility gate, but it is DERIVED
	/// here (ticks AND <see cref="IsModeActive"/>) — the view never binds it.</para>
	/// </summary>
	public abstract class PhenomOverlayViewModel : MapOverlayViewModel
	{
		// ── What the map draws ──

		// ⚠️ BOTH TICKED BY DEFAULT, and that does NOT mean drawn at launch: a tick means "show this while
		// NowCast is on" (see IsModeActive). The map still starts clean; arming NowCast brings them up.
		private bool _showTornado = true;
		private bool _showSevere = true;
		private bool _isModeActive;

		/// <summary>Draw tornado (TO) features while the mode is on. Ticked by default.</summary>
		public bool ShowTornado
		{
			get => _showTornado;
			set { if (SetProperty(ref _showTornado, value)) { OnKindsChanged(); } }
		}

		/// <summary>Draw severe-thunderstorm (SV) features while the mode is on. Ticked by default.</summary>
		public bool ShowSevere
		{
			get => _showSevere;
			set { if (SetProperty(ref _showSevere, value)) { OnKindsChanged(); } }
		}

		private bool _showFlashFlood = true;

		/// <summary>
		/// Whether this overlay HAS a third, flood type. Warnings (Flash Flood Warning, FF) and watches (Flood
		/// Watch — FA zones, FL river points, and pre-2023 Flash Flood Watch FF) both do now. ⚠️ When false
		/// the tick is ignored by every aggregate below, so an overlay behaves exactly as it did with two types.
		/// </summary>
		public virtual bool SupportsFlashFlood => false;

		/// <summary>The flood row's name — "Flash flood" on warnings, "Flood" on watches (the NWS product
		/// names: a Flood Watch replaced the Flash Flood Watch in 2023).</summary>
		public virtual string FloodLabel => "Flash flood";

		/// <summary>The flood row's swatch — the literal the map draws (warnings.js FF / watches.js FA). DATA,
		/// never themed.</summary>
		public virtual string FloodColor => "#2EE05A";

		/// <summary>Whether a feature's phenom code is this overlay's flood type (FF, FA or FL). Warnings only
		/// ever carry FF, so one test serves both.</summary>
		public static bool IsFloodPhenom(string? phenom) => phenom is "FF" or "FA" or "FL";

		/// <summary>Draw flash-flood (FF) features while the mode is on. Ticked by default; meaningful
		/// only when <see cref="SupportsFlashFlood"/>.</summary>
		public bool ShowFlashFlood
		{
			get => _showFlashFlood;
			set { if (SetProperty(ref _showFlashFlood, value)) { OnKindsChanged(); } }
		}

		// The FF tick as it counts toward the aggregates — always false on an overlay without an FF type.
		private bool FlashFloodOn => SupportsFlashFlood && _showFlashFlood;

		/// <summary>Whether any type is ticked — the gate on the opacity slider and the card's footer.
		/// NOT the same as "on the map": that also needs <see cref="IsModeActive"/>.</summary>
		public bool AnyShown => _showTornado || _showSevere || FlashFloodOn;

		/// <summary>The section header's tri-state: true = every type ticked, false = none, null = some.</summary>
		public bool? AllShown =>
			_showTornado && _showSevere && (!SupportsFlashFlood || _showFlashFlood) ? true : AnyShown ? null : false;

		/// <summary>
		/// The section header checkbox: ticks every type, unless every type is already ticked, in which case
		/// it clears them all. A partial section therefore goes to ALL on, the usual select-all convention.
		/// </summary>
		public void ToggleAll()
		{
			var on = AllShown != true;
			// Fields, then ONE OnKindsChanged — going through the setters would push a half-applied filter.
			var ffSame = !SupportsFlashFlood || _showFlashFlood == on;
			if (_showTornado == on && _showSevere == on && ffSame) { return; }
			_showTornado = on;
			_showSevere = on;
			if (SupportsFlashFlood) { _showFlashFlood = on; }
			OnPropertyChanged(nameof(ShowTornado));
			OnPropertyChanged(nameof(ShowSevere));
			OnPropertyChanged(nameof(ShowFlashFlood));
			OnKindsChanged();
		}

		/// <summary>
		/// Whether the mode that owns this overlay is running (NowCast) — set by <c>MapViewModel</c>. The
		/// layer is drawn only while this is on AND a type is ticked.
		/// </summary>
		/// <remarks>
		/// ⚠️ This REPLACED a HideAll() that cleared both ticks on entering replay and never restored them.
		/// Gating on the mode hides current-conditions alerts over historical radar just the same, and the
		/// ticks survive the round trip.
		/// </remarks>
		public bool IsModeActive
		{
			get => _isModeActive;
			set { if (SetProperty(ref _isModeActive, value)) { ApplyVisibility(); } }
		}

		private bool ShouldDraw => AnyShown && _isModeActive;

		// ⚠️ Raised from ONE place because both setters land here and AnyShown (and the card's footer,
		// which reads it) depend on both — the same reason StormReportsViewModel funnels its three.
		// ⚠️ KINDS BEFORE VISIBILITY: turning the first type on makes the overlay visible, which is what
		// triggers the page's lazy fetch and layer add — and that add reads the current filter. Push the
		// filter second and the layers appear for one frame carrying the PREVIOUS selection.
		private void OnKindsChanged()
		{
			OnPropertyChanged(nameof(AnyShown));
			OnPropertyChanged(nameof(AllShown));
			RaiseCard();

			if (!IsMapReady) { return; }
			_ = SetKindsAsync(_showTornado, _showSevere, FlashFloodOn);
			ApplyVisibility();
		}

		private void ApplyVisibility() => IsVisible = ShouldDraw;

		/// <summary>Push the shown phenomena to the page (the feature-specific IMapService call).
		/// <paramref name="flashFlood"/> is always false on an overlay without an FF type.</summary>
		protected abstract Task SetKindsAsync(bool tornado, bool severe, bool flashFlood);

		public override async Task OnMapsReadyAsync()
		{
			// Kinds FIRST (the page keeps the filter and applies it at layer-add), then the base pushes
			// visibility — same ordering rule as OnKindsChanged. IsVisible is written before the base sets
			// IsMapReady, so this assignment pushes nothing on its own.
			await SetKindsAsync(_showTornado, _showSevere, FlashFloodOn);
			IsVisible = ShouldDraw;
			await base.OnMapsReadyAsync();
		}

		// ── Live counts ──

		private int _activeCount;
		private int _tornadoCount;
		private int _severeCount;

		/// <summary>Total active features in the latest fetch — the card's headline number.</summary>
		public int ActiveCount
		{
			get => _activeCount;
			private set { if (SetProperty(ref _activeCount, value)) { RaiseCard(); } }
		}

		/// <summary>Active tornado (TO) features — the count on the tornado row.</summary>
		public int TornadoCount
		{
			get => _tornadoCount;
			private set => SetProperty(ref _tornadoCount, value);
		}

		/// <summary>Active severe-thunderstorm (SV) features — the count on the severe row.</summary>
		public int SevereCount
		{
			get => _severeCount;
			private set => SetProperty(ref _severeCount, value);
		}

		private int _flashFloodCount;

		/// <summary>Active flash-flood (FF) features — the count on the flash-flood row.</summary>
		public int FlashFloodCount
		{
			get => _flashFloodCount;
			private set => SetProperty(ref _flashFloodCount, value);
		}

		// ── The card ──

		private DateTimeOffset? _lastUpdated;
		private string _errorMessage = string.Empty;

		/// <summary>When the counts were last confirmed by a fetch that pulled data; null before the first.
		/// A failed cycle leaves it alone, so its age is how stale the numbers are.</summary>
		public DateTimeOffset? LastUpdated => _lastUpdated;

		/// <summary>
		/// A cycle that actually pulled data: the counts, the "updated" stamp, and a cleared error.
		/// ⚠️ Call on the UI thread — the refresh loops run on background timers.
		/// </summary>
		protected void ApplyRefreshed(int activeCount, int tornadoCount, int severeCount, int flashFloodCount = 0)
		{
			TornadoCount = tornadoCount;
			SevereCount = severeCount;
			FlashFloodCount = flashFloodCount;
			_lastUpdated = DateTimeOffset.Now;
			OnPropertyChanged(nameof(LastUpdated));
			_errorMessage = string.Empty;
			ActiveCount = activeCount;
			RaiseCard();
		}

		/// <summary>
		/// A cycle that failed. The counts and the stamp are LEFT ALONE — the service keeps its
		/// last-known-good file on disk and the map keeps drawing it, so blanking the numbers would
		/// describe a state neither the cache nor the map is in. Only the footer changes.
		/// </summary>
		protected void ApplyRefreshFailed(string? message)
		{
			_errorMessage = string.IsNullOrWhiteSpace(message) ? string.Empty : message!;
			RaiseCard();
		}

		/// <summary>The card's headline: how many of this alert are in effect right now.</summary>
		public string CardHeadline => ActiveCount switch
		{
			0 => $"No active {ItemNounPlural}",
			1 => $"1 active {ItemNounSingular}",
			_ => $"{ActiveCount} active {ItemNounPlural}",
		};

		/// <summary>The card's middle line: when the numbers above were last confirmed. A replay overlay says
		/// which moment they describe instead.</summary>
		public virtual string CardContext =>
			_lastUpdated is { } when
				? $"Updated {when.LocalDateTime:h:mm tt}{CadenceSuffix}"
				: "Waiting for the first update…";

		/// <summary>The card's footer: a failure, else nothing.</summary>
		/// <remarks>
		/// ⚠️ NO LINE ABOUT THE TICKED ROWS ("None shown — pick a type below", "Tornado only"), by the user's call
		/// (2026-10-10): it came and went as boxes were ticked, so the whole window jumped. A section's height must
		/// not change with what you tick — don't re-add a tick-driven card line.
		/// </remarks>
		public string CardFooter => _errorMessage;

		/// <summary>
		/// A card line naming the ELEVATED alerts in effect (a tornado emergency, a PDS…) — the overlay that
		/// can tell overrides it. Empty = the line collapses. Raised with the other card lines.
		/// </summary>
		public virtual string CardThreats => string.Empty;

		/// <summary>Singular noun for the headline ("watch" / "warning").</summary>
		protected abstract string ItemNounSingular { get; }

		/// <summary>Plural noun for the headline ("watches" / "warnings").</summary>
		protected abstract string ItemNounPlural { get; }

		/// <summary>
		/// Appended to the "Updated …" line — how often this overlay re-checks. Empty by default; a feed
		/// whose cadence is worth stating (warnings poll adaptively) overrides it.
		/// </summary>
		protected virtual string CadenceSuffix => string.Empty;

		/// <summary>Re-raises all three card lines. They are computed, and every input to them —
		/// counts, the stamp, an error, the toggles — changes at a different moment.</summary>
		protected void RaiseCard()
		{
			OnPropertyChanged(nameof(CardHeadline));
			OnPropertyChanged(nameof(CardContext));
			OnPropertyChanged(nameof(CardFooter));
			OnPropertyChanged(nameof(CardThreats));
		}
	}
}
