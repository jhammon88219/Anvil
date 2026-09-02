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
	/// <para>⚠️ THERE IS NO MASTER SHOW/HIDE. The section used to carry one checkbox reading
	/// "Tornado / Severe Thunderstorm"; it is now one checkbox per type, so the layer is simply on when
	/// either is on. <see cref="MapOverlayViewModel.IsVisible"/> is still the base's visibility gate, but
	/// it is DERIVED here — the view never binds it. Same model as the storm-report dots, whose three
	/// type toggles have no master either.</para>
	/// </summary>
	public abstract class PhenomOverlayViewModel : MapOverlayViewModel
	{
		// ── What the map draws ──

		private bool _showTornado;
		private bool _showSevere;

		/// <summary>Draw tornado (TO) features. Off by default, like every overlay in this window.</summary>
		public bool ShowTornado
		{
			get => _showTornado;
			set { if (SetProperty(ref _showTornado, value)) { OnKindsChanged(); } }
		}

		/// <summary>Draw severe-thunderstorm (SV) features. Off by default.</summary>
		public bool ShowSevere
		{
			get => _showSevere;
			set { if (SetProperty(ref _showSevere, value)) { OnKindsChanged(); } }
		}

		/// <summary>Whether anything is drawn at all — the derived visibility, and the gate on the opacity
		/// slider (nothing on the map, nothing for it to fade).</summary>
		public bool AnyShown => _showTornado || _showSevere;

		/// <summary>
		/// Clears both type toggles, taking the overlay off the map. Used when entering replay: these are
		/// CURRENT-conditions layers, so they must not hang over historical radar.
		/// </summary>
		/// <remarks>
		/// ⚠️ Not <c>IsVisible = false</c>. Visibility is derived from the toggles now, so writing it
		/// directly would hide the layer while leaving both boxes ticked — the window would then report a
		/// state the map does not show.
		/// </remarks>
		public void HideAll()
		{
			ShowTornado = false;
			ShowSevere = false;
		}

		// ⚠️ Raised from ONE place because both setters land here and AnyShown (and the card's footer,
		// which reads it) depend on both — the same reason StormReportsViewModel funnels its three.
		// ⚠️ KINDS BEFORE VISIBILITY: turning the first type on makes the overlay visible, which is what
		// triggers the page's lazy fetch and layer add — and that add reads the current filter. Push the
		// filter second and the layers appear for one frame carrying the PREVIOUS selection.
		private void OnKindsChanged()
		{
			OnPropertyChanged(nameof(AnyShown));
			RaiseCard();

			if (!IsMapReady) { return; }
			_ = SetKindsAsync(_showTornado, _showSevere);
			IsVisible = AnyShown;
		}

		/// <summary>Push the shown phenomena to the page (the feature-specific IMapService call).</summary>
		protected abstract Task SetKindsAsync(bool tornado, bool severe);

		public override async Task OnMapsReadyAsync()
		{
			await base.OnMapsReadyAsync();
			await SetKindsAsync(_showTornado, _showSevere);
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
