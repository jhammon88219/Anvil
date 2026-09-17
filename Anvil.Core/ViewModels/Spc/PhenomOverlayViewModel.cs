using System;
using System.Threading.Tasks;

namespace Anvil.ViewModels
{
	/// <summary>
	/// Base for a CONVECTIVE ALERT overlay whose features carry a `phenom` of TO (tornado) or SV (severe
	/// thunderstorm) — the SPC watch boxes and the storm-based warning polygons. Sits between
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

		/// <summary>Whether any type is ticked — the gate on the opacity slider and the card's footer.
		/// NOT the same as "on the map": that also needs <see cref="IsModeActive"/>.</summary>
		public bool AnyShown => _showTornado || _showSevere;

		/// <summary>The section header's tri-state: true = every type ticked, false = none, null = some.</summary>
		public bool? AllShown => _showTornado && _showSevere ? true : AnyShown ? null : false;

		/// <summary>
		/// The section header checkbox: ticks every type, unless every type is already ticked, in which case
		/// it clears them all. A partial section therefore goes to ALL on, the usual select-all convention.
		/// </summary>
		public void ToggleAll()
		{
			var on = AllShown != true;
			// Fields, then ONE OnKindsChanged — going through both setters would push a half-applied filter.
			if (_showTornado == on && _showSevere == on) { return; }
			_showTornado = on;
			_showSevere = on;
			OnPropertyChanged(nameof(ShowTornado));
			OnPropertyChanged(nameof(ShowSevere));
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
			_ = SetKindsAsync(_showTornado, _showSevere);
			ApplyVisibility();
		}

		private void ApplyVisibility() => IsVisible = ShouldDraw;

		/// <summary>Push the shown phenomena to the page (the feature-specific IMapService call).</summary>
		protected abstract Task SetKindsAsync(bool tornado, bool severe);

		public override async Task OnMapsReadyAsync()
		{
			// Kinds FIRST (the page keeps the filter and applies it at layer-add), then the base pushes
			// visibility — same ordering rule as OnKindsChanged. IsVisible is written before the base sets
			// IsMapReady, so this assignment pushes nothing on its own.
			await SetKindsAsync(_showTornado, _showSevere);
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

		// ── The card ──

		private DateTimeOffset? _lastUpdated;
		private string _errorMessage = string.Empty;

		/// <summary>
		/// A cycle that actually pulled data: the counts, the "updated" stamp, and a cleared error.
		/// ⚠️ Call on the UI thread — the refresh loops run on background timers.
		/// </summary>
		protected void ApplyRefreshed(int activeCount, int tornadoCount, int severeCount)
		{
			TornadoCount = tornadoCount;
			SevereCount = severeCount;
			_lastUpdated = DateTimeOffset.Now;
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

		/// <summary>The card's middle line: when the numbers above were last confirmed.</summary>
		public string CardContext =>
			_lastUpdated is { } when
				? $"Updated {when.LocalDateTime:h:mm tt}{CadenceSuffix}"
				: "Waiting for the first update…";

		/// <summary>
		/// The card's footer: failures first, then — when the rows filter what the counts describe — what
		/// is actually on the map.
		/// </summary>
		/// <remarks>
		/// ⚠️ THE PARTIAL LINE IS NOT DECORATION. The headline counts every active alert while the rows
		/// decide which are drawn, so during an outbreak "12 active warnings" can sit over a map showing
		/// three. Naming the filter is what keeps the headline honest; "None shown" is the same problem at
		/// its limit, and the same fix the storm-report card uses.
		/// </remarks>
		public string CardFooter =>
			_errorMessage.Length > 0 ? _errorMessage :
			!AnyShown ? "None shown — pick a type below" :
			!_showSevere ? "Tornado only" :
			!_showTornado ? "Severe thunderstorm only" :
			string.Empty;

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
		}
	}
}
