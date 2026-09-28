using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// The HISTORICAL SPC outlook overlay for the PastCast window — an independent overlay (like the live
	/// outlook in NowCast) keyed to the replay date/time held by <see cref="RadarViewModel"/>. Selecting a
	/// product fetches that day's archived issuance (via <see cref="ISpcOutlookService.EnsurePastOutlookAsync"/>,
	/// IEM archive back to ~2002) and shows it through the SAME map layer the live outlook uses. The map has
	/// one outlook layer, so <see cref="MapViewModel"/> hands ownership to this VM in past mode and back to
	/// <see cref="OutlookViewModel"/> otherwise.
	///
	/// Days 1-3 (product list cascades: Day 1-2 = Categorical/Tornado/Wind/Hail/Fire, Day 3 = Categorical/
	/// Fire). Issuance cycle is auto (the one in effect at the replay time for Day 1) with an override
	/// dropdown. Drives the map through <see cref="IMapService"/>.
	/// </summary>
	public sealed class PastOutlookViewModel : ObservableObject
	{
		private readonly IMapService _mapService;
		private readonly ISpcOutlookService _outlookService;
		private readonly RadarViewModel _radar;

		private bool _isMapReady;
		private int _applyToken; // guards a stale async apply when the selection changes mid-fetch

		public PastOutlookViewModel(IMapService mapService, ISpcOutlookService outlookService, RadarViewModel radar)
		{
			_mapService = mapService;
			_outlookService = outlookService;
			_radar = radar;

			Days = new[] { new DayOption(1, "Day 1"), new DayOption(2, "Day 2"), new DayOption(3, "Day 3") };
			_selectedDayOption = Days[0];
			RebuildProductOptions();
			_selectedProductOption = ProductOptions[0]; // None
			RebuildCycleOptions();
			_selectedCycleOption = CycleOptions[0]; // Auto

			// Keep the overlay in sync with the replay date/time controls (same card): if a product is up,
			// re-fetch for the new date. Mode enter/leave is driven by MapViewModel (it also owns the live
			// outlook), so we don't handle IsPastEventMode here.
			_radar.PropertyChanged += OnRadarChanged;
		}

		// ── Day (1-3) ──

		public IReadOnlyList<DayOption> Days { get; }

		private DayOption _selectedDayOption;
		public DayOption SelectedDayOption
		{
			get => _selectedDayOption;
			set
			{
				if (value is null || _selectedDayOption == value) return;
				SetProperty(ref _selectedDayOption, value);

				// Cascade the product + cycle lists to the new day, keeping the product type if still valid.
				var keepType = _selectedProductOption?.Type;
				RebuildProductOptions();
				_selectedProductOption = ProductOptions.FirstOrDefault(o => o.Type == keepType) ?? ProductOptions[0];
				OnPropertyChanged(nameof(SelectedProductOption));
				OnPropertyChanged(nameof(HasOutlook)); // the cascade can drop a product this day lacks
				OnPropertyChanged(nameof(ShownOnMap));
				RebuildCycleOptions();
				_selectedCycleOption = CycleOptions[0];
				OnPropertyChanged(nameof(SelectedCycleOption));
				OnPropertyChanged(nameof(SelectedDayIndex));

				_ = ApplyAsync();
			}
		}

		private int SelectedDay => _selectedDayOption.Day;

		/// <summary>The selected day as an INDEX (the persistence layer stores it by position).
		/// Routes through <see cref="SelectedDayOption"/> so the product/cycle cascade still runs.</summary>
		public int SelectedDayIndex
		{
			// Days are 1-3 in order, so the number IS the position — and IReadOnlyList has no IndexOf.
			get => _selectedDayOption.Day - 1;
			set
			{
				if (value >= 0 && value < Days.Count)
				{
					SelectedDayOption = Days[value];
				}
			}
		}

		// ── Product (None + the day's products) ──

		private IReadOnlyList<PastOutlookOption> _productOptions = Array.Empty<PastOutlookOption>();
		public IReadOnlyList<PastOutlookOption> ProductOptions => _productOptions;

		private PastOutlookOption _selectedProductOption;
		public PastOutlookOption SelectedProductOption
		{
			get => _selectedProductOption;
			set
			{
				if (value is null || _selectedProductOption == value) return;
				SetProperty(ref _selectedProductOption, value);
				OnPropertyChanged(nameof(HasOutlook)); // "None" is the section's off switch
				OnPropertyChanged(nameof(ShownOnMap));
				_ = ApplyAsync();
			}
		}

		private void RebuildProductOptions()
		{
			var opts = new List<PastOutlookOption> { new("None", null) };
			opts.Add(new("Categorical", SpcOutlookType.Categorical));
			if (SelectedDay == 1)
			{
				// Only Day 1 breaks out the individual hazards; Days 2-3 carry a single combined
				// "ANY SEVERE" probabilistic instead (SPC's product structure).
				opts.Add(new("Tornado", SpcOutlookType.Tornado));
				opts.Add(new("Wind", SpcOutlookType.Wind));
				opts.Add(new("Hail", SpcOutlookType.Hail));
			}
			else
			{
				opts.Add(new("Probabilistic", SpcOutlookType.ProbabilisticCombined));
			}
			opts.Add(new("Fire Weather", SpcOutlookType.FireWeather));
			_productOptions = opts;
			OnPropertyChanged(nameof(ProductOptions));
		}

		// ── Issuance cycle (Auto + the day's issuances) ──

		private IReadOnlyList<PastCycleOption> _cycleOptions = Array.Empty<PastCycleOption>();
		public IReadOnlyList<PastCycleOption> CycleOptions => _cycleOptions;

		private PastCycleOption _selectedCycleOption;
		public PastCycleOption SelectedCycleOption
		{
			get => _selectedCycleOption;
			set
			{
				if (value is null || _selectedCycleOption == value) return;
				SetProperty(ref _selectedCycleOption, value);
				_ = ApplyAsync();
			}
		}

		private void RebuildCycleOptions()
		{
			var opts = new List<PastCycleOption> { new("Auto", null) };
			opts.AddRange(CandidateCycles(SelectedDay).Select(c => new PastCycleOption($"{c:D2}Z", c)));
			_cycleOptions = opts;
			OnPropertyChanged(nameof(CycleOptions));
		}

		// ── Show / hide (the section header's box) ──
		// ⚠️ SEPARATE FROM THE PRODUCT PICK on purpose: hiding keeps Day / Product / Cycle as they are, so
		// ticking the box back brings the SAME outlook back (the fetch is cached — no re-download). "None"
		// is still "no product"; this is "a product, not drawn right now".

		private bool _isShown = true;
		/// <summary>Whether the picked outlook is drawn. Default true.</summary>
		public bool IsShown
		{
			get => _isShown;
			set
			{
				if (SetProperty(ref _isShown, value))
				{
					OnPropertyChanged(nameof(ShownOnMap));
					_ = ApplyAsync();
				}
			}
		}

		/// <summary>Whether an outlook can actually be on the map: a product is picked AND shown. Gates the
		/// opacity slider; the header BOX binds <see cref="IsShown"/> alone.</summary>
		public bool ShownOnMap => _isShown && HasOutlook;

		/// <summary>The header box's click. ⚠️ Works whatever the product, None included (the user's call,
		/// 2026-09-27): the box is the section's show/hide, and hiding with None picked means a product
		/// chosen later arrives hidden.</summary>
		public void ToggleShown() => IsShown = !_isShown;

		// ── Opacity ──

		private double _opacity = 0.15;
		public double Opacity
		{
			get => _opacity;
			set
			{
				if (Math.Abs(_opacity - value) < 1e-9) return;
				_opacity = value;
				OnPropertyChanged();
				if (_isMapReady) _ = _mapService.SetOutlookOpacityAsync(value);
			}
		}

		// ── The outlook card ──────────────────────────────────────────────────────────────────────
		// The section shows a CARD above its pickers, mirroring the Timeframe card in the same window: a
		// headline, a line of context, and a footer. Three properties rather than one status string, and
		// they are only ever written together (SetCard) so the card can never be half-updated.
		//
		// ⚠️ THE FOOTER IS THE ERROR CHANNEL AS WELL AS THE TIMES SLOT, and that is what makes a two-tier
		// card survive a failure. This used to be ONE composed StatusText that carried the product line AND
		// every error ("Outlook fetch failed", "No Categorical in the 20Z issuance…"), which a headline /
		// sub-line split cannot represent — there is no sensible HEADLINE for a failure. Keeping the
		// headline as "what you asked for" and letting the footer say "and here is what came back" works for
		// both outcomes. Don't move an error into the headline.
		//
		// ⚠️ THE CONTEXT LINE GAINS THE CYCLE ONLY ONCE IT IS KNOWN. With Auto the cycle is resolved by
		// falling back through the day's issuances, so while loading there is no cycle to name yet.

		private string _cardHeadline = NoOutlookHeadline;
		private string _cardContext = string.Empty;
		private string _cardFooter = string.Empty;

		/// <summary>The card's headline: the product you asked for, or that nothing is drawn.</summary>
		public string CardHeadline
		{
			get => _cardHeadline;
			private set => SetProperty(ref _cardHeadline, value);
		}

		/// <summary>The card's middle line: which day, issuance and date the headline refers to.</summary>
		public string CardContext
		{
			get => _cardContext;
			private set => SetProperty(ref _cardContext, value);
		}

		/// <summary>The card's footer: the issued/valid times when the outlook was found, and the reason
		/// when it was not.</summary>
		public string CardFooter
		{
			get => _cardFooter;
			private set => SetProperty(ref _cardFooter, value);
		}

		/// <summary>Whether a product is selected at all. False = "None", which is this section's off
		/// switch — Cycle and Opacity have nothing to act on and are disabled.</summary>
		public bool HasOutlook => _selectedProductOption.Type is not null;

		private const string NoOutlookHeadline = "No outlook drawn";

		// ── The legend (mirrors OutlookViewModel's; the section hosts the shared Primitives/OutlookLegend) ──
		// ⚠️ Set ONLY from a successful show and cleared on every path that clears the layer, so the key can
		// never describe an outlook that is not on the map (None, hidden, failed fetch, leaving PastCast).

		private IReadOnlyList<SpcRiskLevel> _legendEntries = Array.Empty<SpcRiskLevel>();
		private IReadOnlyList<OutlookHatchLegendRow> _hatchLegendRows = Array.Empty<OutlookHatchLegendRow>();
		private bool _showHatching = true;

		/// <summary>The solid half of the drawn product's scale (NOAA colours + names). Empty when nothing is drawn.</summary>
		public IReadOnlyList<SpcRiskLevel> LegendEntries => _legendEntries;

		/// <summary>The Conditional Intensity Groups in the scale, each marked with whether the archived
		/// outlook contains it. Empty for products with none.</summary>
		public IReadOnlyList<OutlookHatchLegendRow> HatchLegendRows => _hatchLegendRows;

		/// <summary>Whether there is anything to key — gates the legend's visibility.</summary>
		public bool HasLegend => _legendEntries.Count > 0 || _hatchLegendRows.Count > 0;

		/// <summary>Draws or hides the CIG hatching on the map (fills and outlines stay). Session-only, like
		/// the live outlook's.</summary>
		public bool ShowHatching
		{
			get => _showHatching;
			set
			{
				if (SetProperty(ref _showHatching, value) && _isMapReady && ShownOnMap)
				{
					_ = _mapService.SetOutlookHatchingVisibleAsync(value);
				}
			}
		}

		// null = nothing drawn.
		private void SetLegend(SpcOutlookProduct? product)
		{
			var scale = product is null ? Array.Empty<SpcRiskLevel>() : _outlookService.GetLegendForProduct(product);
			var present = product is null ? new HashSet<string>() : _outlookService.GetHatchGroupsInOutlook(product);
			_legendEntries = scale.Where(l => !l.IsConditionalIntensity).ToList();
			_hatchLegendRows = scale.Where(l => l.IsConditionalIntensity)
				.Select(l => new OutlookHatchLegendRow(l, present.Contains(l.Code)))
				.ToList();
			OnPropertyChanged(nameof(LegendEntries));
			OnPropertyChanged(nameof(HatchLegendRows));
			OnPropertyChanged(nameof(HasLegend));
		}

		// Every clear of the shared layer goes through here so the legend leaves with the areas.
		private async Task ClearLayerAsync()
		{
			await _mapService.ClearOutlookAsync();
			SetLegend(null);
		}

		// Every card update goes through here, so no path can leave one of the three lines describing a
		// previous selection.
		private void SetCard(string headline, string context, string footer)
		{
			CardHeadline = headline;
			CardContext = context;
			CardFooter = footer;
		}

		// "Day 1 · May 24, 2011", plus the issuance once it is known.
		private static string ContextFor(int day, DateOnly date, int? cycle) =>
			cycle is { } c
				? $"Day {day} · {c:D2}Z issuance · {date:MMM d, yyyy}"
				: $"Day {day} · {date:MMM d, yyyy}";

		// ── Lifecycle / coordination (called by MapViewModel) ──

		/// <summary>Called once the map page is ready.</summary>
		public Task OnMapsReadyAsync()
		{
			_isMapReady = true;
			return _radar.IsPastEventMode ? ApplyAsync() : Task.CompletedTask;
		}

		/// <summary>MapViewModel hands the shared outlook layer to/from this VM as PastCast toggles.
		/// Entering past mode shows the selected historical outlook; leaving clears the layer.</summary>
		public void OnPastModeChanged(bool on)
		{
			if (on) { _ = ApplyAsync(); }
			else { _ = ClearAsync(); }
		}

		private void OnRadarChanged(object? sender, PropertyChangedEventArgs e)
		{
			// A replay date/time change should move the outlook with it, but only when we own the layer
			// (past mode) and a product is actually shown.
			// ⚠️ A LOAD, not a picker move — HasLoadedReplayWindow is re-raised by every successful load. The
			// date/time properties are deliberately not watched: they describe the window Load would set up
			// next, so reacting to them re-fetched for a day the user had not committed to.
			if (e.PropertyName is nameof(RadarViewModel.HasLoadedReplayWindow))
			{
				if (_radar.IsPastEventMode && _selectedProductOption.Type is not null)
				{
					_ = ApplyAsync();
				}
			}
		}

		// The core: fetch (with cycle fallback) + show the selected historical product, or clear for None.
		private async Task ApplyAsync()
		{
			if (!_isMapReady || !_radar.IsPastEventMode) return;

			var token = ++_applyToken;
			var type = _selectedProductOption.Type;
			if (type is null)
			{
				await ClearLayerAsync();
				SetCard(NoOutlookHeadline, string.Empty, string.Empty);
				return;
			}

			var day = SelectedDay;
			if (!_isShown)
			{
				await ClearLayerAsync();
				SetCard(_selectedProductOption.Label, $"Day {day}", "Hidden — tick the box to draw it");
				return;
			}
			// ⚠️ THE LOADED WINDOW, NOT THE PICKERS. Between editing a date and pressing Load the two describe
			// different days, and following the pickers fetched an outlook for a day that was not on the map.
			// Same rule the storm reports follow; both overlays key to what is actually loaded.
			if (_radar.LoadedReplayStartUtc is not { } replayStart)
			{
				await ClearLayerAsync();
				SetCard(NoOutlookHeadline, string.Empty, "Load a timeframe to see its outlook");
				return;
			}

			var date = ConvectiveDay(replayStart);
			SetCard(_selectedProductOption.Label, ContextFor(day, date, null), "Loading…");

			var (result, cycleUsed) = await EnsureWithFallbackAsync(date, day);
			if (token != _applyToken) return; // a newer selection won

			if (result is null || result.Error is not null)
			{
				await ClearLayerAsync();
				SetCard(_selectedProductOption.Label, ContextFor(day, date, null),
					result?.Error is { } err ? $"Outlook fetch failed: {err}" : "Outlook fetch failed.");
				return;
			}
			if (!result.Found || !result.AvailableTypes.Contains(type.Value))
			{
				await ClearLayerAsync();
				SetCard(_selectedProductOption.Label,
					ContextFor(day, date, result.Found ? cycleUsed : null),
					result.Found
						? $"Not in the {cycleUsed:D2}Z issuance for this date."
						: "No archived outlook for this date.");
				return;
			}

			var url = $"https://{SpcOutlookService.CacheHostName}/{SpcOutlookService.PastCacheName(date, day, cycleUsed, type.Value)}";
			var product = new SpcOutlookProduct(
				Id: $"past-{date:yyyyMMdd}-d{day}-c{cycleUsed:D2}-{type}",
				Day: day, Type: type.Value, DisplayName: _selectedProductOption.Label,
				CacheFileName: SpcOutlookService.PastCacheName(date, day, cycleUsed, type.Value),
				LocalUrl: url);

			await _mapService.ShowOutlookAsync(product);
			await _mapService.SetOutlookOpacityAsync(_opacity);
			// ⚠️ ORDERED after the show: the page resets hatching to shown on every show (the layer is shared
			// with the live outlook), so the push must land after it. Same rule as OutlookViewModel.
			await _mapService.SetOutlookHatchingVisibleAsync(_showHatching);
			if (token != _applyToken) return; // a newer selection won while the show was in flight
			SetLegend(product);

			SetCard(_selectedProductOption.Label, ContextFor(day, date, cycleUsed), FormatTimes(result.Times));
		}

		private async Task ClearAsync()
		{
			if (!_isMapReady) return;
			await ClearLayerAsync();
			SetCard(NoOutlookHeadline, string.Empty, string.Empty);
		}

		// Honors an explicit cycle override; otherwise tries the auto cycle first, then the day's remaining
		// issuances, using the first that actually has data (historical days vary in which cycles exist).
		private async Task<(PastOutlookResult? Result, int CycleUsed)> EnsureWithFallbackAsync(DateOnly date, int day)
		{
			if (_selectedCycleOption.Cycle is { } forced)
			{
				return (await _outlookService.EnsurePastOutlookAsync(date, day, forced), forced);
			}

			PastOutlookResult? last = null;
			var order = OrderedAutoCycles(day, _radar.LoadedReplayStartUtc ?? _radar.ReplayStartUtc());
			foreach (var c in order)
			{
				var r = await _outlookService.EnsurePastOutlookAsync(date, day, c);
				last = r;
				if (r.Found || r.Error is not null) return (r, c);
			}
			return (last, order.Length > 0 ? order[0] : 0);
		}

		// ── Replay-date + issuance-cycle resolution ──

		// The SPC "convective day" (12Z→12Z) containing the replay start = the IEM `valid` date.
		private static DateOnly ConvectiveDay(DateTimeOffset startUtc)
		{
			var d = startUtc.UtcDateTime;
			return DateOnly.FromDateTime(d.Hour >= 12 ? d : d.AddDays(-1));
		}

		// Issuance cycles that exist for a day (UTC hour; 16 = the 1630Z update). Day 1 has the full set;
		// forecast days have fewer. Used for the override dropdown and the auto fallback order.
		private static int[] CandidateCycles(int day) => day switch
		{
			1 => new[] { 13, 16, 20, 1, 6 },
			2 => new[] { 17, 6 }, // Day 2 primary issuance is 1730Z (cycle 17); 06Z as fallback
			_ => new[] { 8 },     // Day 3 issued ~0730Z (cycle 8)
		};

		// Auto order: for Day 1, the issuance in effect at the replay time first, then the rest as fallback;
		// for forecast days, the standard order.
		private static int[] OrderedAutoCycles(int day, DateTimeOffset startUtc)
		{
			var all = CandidateCycles(day);
			if (day != 1) return all;

			var convDay = ConvectiveDay(startUtc).ToDateTime(TimeOnly.MinValue);
			// Absolute issuance instants across the 12Z→12Z window.
			var instants = new (int Cycle, DateTime AtUtc)[]
			{
				(13, convDay.AddHours(13)),
				(16, convDay.AddHours(16.5)), // 1630Z
				(20, convDay.AddHours(20)),
				(1,  convDay.AddDays(1).AddHours(1)),
				(6,  convDay.AddDays(1).AddHours(6)),
			};
			var inEffect = instants.Where(i => i.AtUtc <= startUtc.UtcDateTime)
				.OrderByDescending(i => i.AtUtc).Select(i => i.Cycle).FirstOrDefault(13);
			return new[] { inEffect }.Concat(all.Where(c => c != inEffect)).ToArray();
		}

		private static string FormatTimes(SpcOutlookTimes? times)
		{
			if (times is null) return string.Empty;
			var parts = new List<string>(2);
			if (times.Issued is { } iss) parts.Add($"Issued {iss.ToLocalTime():ddd MMM d h:mm tt}");
			if (times.Valid is { } v && times.Expire is { } e)
				parts.Add($"Valid {v.ToLocalTime():ddd h:mm tt} → {e.ToLocalTime():ddd h:mm tt}");
			return string.Join("  ·  ", parts);
		}
	}

	/// <summary>One entry in the PastCast outlook product picker (None carries a null type).</summary>
	public sealed record PastOutlookOption(string Label, SpcOutlookType? Type);

	/// <summary>One entry in the issuance-cycle picker (Auto carries a null cycle).</summary>
	public sealed record PastCycleOption(string Label, int? Cycle);
}
