using System;
using System.ComponentModel;
using System.Linq;
using Anvil.Services;

namespace Anvil.ViewModels
{
	/// <summary>
	/// Makes every choice in the three temporal windows survive a restart: layer ticks, opacities and the
	/// two outlooks' pickers. <see cref="Attach"/> runs once from <see cref="MapViewModel"/>'s constructor —
	/// it RESTORES the saved values into the subsystem view models, then TRACKS their changes back into
	/// <see cref="AppSettings"/> (whose auto-save writes the file).
	/// </summary>
	/// <remarks>
	/// ⚠️ RESTORE GOES THROUGH THE PUBLIC SETTERS, before the map is ready. That is safe because every one
	/// of them pushes to the map only once its VM's map-ready flag is up, and each VM's OnMapsReadyAsync
	/// replays its current state — the same path a user change made before map-ready takes.
	/// ⚠️ TRACKING STARTS AFTER RESTORE, so restoring writes nothing back.
	/// ⚠️ A setting that is null was never changed: the VM keeps its own default (see the note on these
	/// properties in AppSettings). Nothing here states a default.
	/// ⚠️ Section open/closed state is NOT here — it is view state, kept by the windows through
	/// <see cref="MapViewModel.IsSectionExpanded"/> / <see cref="MapViewModel.SetSectionExpanded"/>.
	/// ⚠️ ADD A CHOICE = an AppSettings property + one line in Restore + one case in Track.
	/// </remarks>
	internal static class TemporalWindowPersistence
	{
		private const string NoneProduct = "None";

		public static void Attach(AppSettings s, RadarViewModel radar, WarningsViewModel warnings, WatchesViewModel watches,
			StormReportsViewModel reports, DamageSurveysViewModel damage, PastOutlookViewModel pastOutlook, OutlookViewModel outlook,
			StormCellsViewModel cells)
		{
			Restore(s, radar, warnings, watches, reports, damage, pastOutlook, outlook, cells);
			Track(s, radar, warnings, watches, reports, damage, pastOutlook, outlook, cells);
		}

		private static void Restore(AppSettings s, RadarViewModel radar, WarningsViewModel warnings, WatchesViewModel watches,
			StormReportsViewModel reports, DamageSurveysViewModel damage, PastOutlookViewModel pastOutlook, OutlookViewModel outlook,
			StormCellsViewModel cells)
		{
			if (s.RadarOpacity is double ro) { radar.RadarOpacity = Unit(ro); }
			if (s.ShowRadarLayer is bool rl) { radar.ShowRadarLayer = rl; }

			if (s.WarningsShowTornado is bool wt) { warnings.ShowTornado = wt; }
			if (s.WarningsShowSevere is bool ws) { warnings.ShowSevere = ws; }
			if (s.WarningsShowFlashFlood is bool wf) { warnings.ShowFlashFlood = wf; }
			if (s.WarningsOpacity is double wo) { warnings.Opacity = Unit(wo); }

			if (s.WatchesShowTornado is bool at) { watches.ShowTornado = at; }
			if (s.WatchesShowSevere is bool aS) { watches.ShowSevere = aS; }
			if (s.WatchesOpacity is double ao) { watches.Opacity = Unit(ao); }

			if (s.StormReportsShowTornado is bool rt) { reports.ShowTornado = rt; }
			if (s.StormReportsShowWind is bool rw) { reports.ShowWind = rw; }
			if (s.StormReportsShowHail is bool rh) { reports.ShowHail = rh; }
			if (s.StormReportsOpacity is double rop) { reports.Opacity = Unit(rop); }

			if (s.DamageSurveysShowAreas is bool da) { damage.ShowAreas = da; }
			if (s.DamageSurveysShowTracks is bool dt) { damage.ShowTracks = dt; }
			if (s.DamageSurveysShowPoints is bool dp) { damage.ShowPoints = dp; }
			if (s.DamageSurveysOpacity is double dop) { damage.Opacity = Unit(dop); }

			if (s.StormCellsShowTracks is bool ct) { cells.ShowTracks = ct; }
			if (s.StormCellsShowTvs is bool cv) { cells.ShowTvs = cv; }
			if (s.StormCellsShowMeso is bool cm) { cells.ShowMeso = cm; }
			if (s.StormCellsShowHail is bool ch) { cells.ShowHail = ch; }
			if (s.StormCellsOpacity is double cop) { cells.Opacity = Unit(cop); }

			// Past outlook: day FIRST — it cascades (rebuilds) the product and cycle lists the other two pick from.
			if (s.PastOutlookDay is int pd && pd >= 1 && pd <= pastOutlook.Days.Count) { pastOutlook.SelectedDayIndex = pd - 1; }
			if (s.PastOutlookProduct is string pp &&
				pastOutlook.ProductOptions.FirstOrDefault(o => ProductKey(o.Type) == pp) is { } pastOption)
			{
				pastOutlook.SelectedProductOption = pastOption;
			}
			if (s.PastOutlookCycle is int pc &&
				pastOutlook.CycleOptions.FirstOrDefault(o => o.Cycle == pc) is { } cycleOption)
			{
				pastOutlook.SelectedCycleOption = cycleOption;
			}
			if (s.PastOutlookOpacity is double po) { pastOutlook.Opacity = Unit(po); }

			// Live outlook: day FIRST, for the same reason (picking a day also picks that day's default product).
			if (s.ForeCastOutlookDay is int fd && outlook.Days.FirstOrDefault(d => d.Day == fd) is { } dayOption)
			{
				outlook.SelectedDayOption = dayOption;
			}
			if (s.ForeCastOutlookProduct is string fp &&
				outlook.ProductOptions.FirstOrDefault(o => ProductKey(o.Product?.Type) == fp) is { } liveOption)
			{
				outlook.SelectedOption = liveOption;
			}
			if (s.ForeCastOutlookOpacity is double fo) { outlook.OutlookOpacity = Unit(fo); }
			if (s.ForeCastShowHatching is bool fh) { outlook.ShowHatching = fh; }
		}

		private static void Track(AppSettings s, RadarViewModel radar, WarningsViewModel warnings, WatchesViewModel watches,
			StormReportsViewModel reports, DamageSurveysViewModel damage, PastOutlookViewModel pastOutlook, OutlookViewModel outlook,
			StormCellsViewModel cells)
		{
			radar.PropertyChanged += (_, e) =>
			{
				switch (e.PropertyName)
				{
					case nameof(RadarViewModel.RadarOpacity): s.RadarOpacity = radar.RadarOpacity; break;
					case nameof(RadarViewModel.ShowRadarLayer): s.ShowRadarLayer = radar.ShowRadarLayer; break;
				}
			};

			warnings.PropertyChanged += (_, e) =>
			{
				switch (e.PropertyName)
				{
					case nameof(PhenomOverlayViewModel.ShowTornado): s.WarningsShowTornado = warnings.ShowTornado; break;
					case nameof(PhenomOverlayViewModel.ShowSevere): s.WarningsShowSevere = warnings.ShowSevere; break;
					case nameof(PhenomOverlayViewModel.ShowFlashFlood): s.WarningsShowFlashFlood = warnings.ShowFlashFlood; break;
					case nameof(PhenomOverlayViewModel.Opacity): s.WarningsOpacity = warnings.Opacity; break;
				}
			};

			watches.PropertyChanged += (_, e) =>
			{
				switch (e.PropertyName)
				{
					case nameof(PhenomOverlayViewModel.ShowTornado): s.WatchesShowTornado = watches.ShowTornado; break;
					case nameof(PhenomOverlayViewModel.ShowSevere): s.WatchesShowSevere = watches.ShowSevere; break;
					case nameof(PhenomOverlayViewModel.Opacity): s.WatchesOpacity = watches.Opacity; break;
				}
			};

			reports.PropertyChanged += (_, e) =>
			{
				switch (e.PropertyName)
				{
					case nameof(StormReportsViewModel.ShowTornado): s.StormReportsShowTornado = reports.ShowTornado; break;
					case nameof(StormReportsViewModel.ShowWind): s.StormReportsShowWind = reports.ShowWind; break;
					case nameof(StormReportsViewModel.ShowHail): s.StormReportsShowHail = reports.ShowHail; break;
					case nameof(StormReportsViewModel.Opacity): s.StormReportsOpacity = reports.Opacity; break;
				}
			};

			damage.PropertyChanged += (_, e) =>
			{
				switch (e.PropertyName)
				{
					case nameof(DamageSurveysViewModel.ShowAreas): s.DamageSurveysShowAreas = damage.ShowAreas; break;
					case nameof(DamageSurveysViewModel.ShowTracks): s.DamageSurveysShowTracks = damage.ShowTracks; break;
					case nameof(DamageSurveysViewModel.ShowPoints): s.DamageSurveysShowPoints = damage.ShowPoints; break;
					case nameof(DamageSurveysViewModel.Opacity): s.DamageSurveysOpacity = damage.Opacity; break;
				}
			};

			cells.PropertyChanged += (_, e) =>
			{
				switch (e.PropertyName)
				{
					case nameof(StormCellsViewModel.ShowTracks): s.StormCellsShowTracks = cells.ShowTracks; break;
					case nameof(StormCellsViewModel.ShowTvs): s.StormCellsShowTvs = cells.ShowTvs; break;
					case nameof(StormCellsViewModel.ShowMeso): s.StormCellsShowMeso = cells.ShowMeso; break;
					case nameof(StormCellsViewModel.ShowHail): s.StormCellsShowHail = cells.ShowHail; break;
					case nameof(StormCellsViewModel.Opacity): s.StormCellsOpacity = cells.Opacity; break;
				}
			};

			// ⚠️ A day change CASCADES and raises product + cycle too, so all three land together.
			pastOutlook.PropertyChanged += (_, e) =>
			{
				switch (e.PropertyName)
				{
					case nameof(PastOutlookViewModel.SelectedDayOption):
					case nameof(PastOutlookViewModel.SelectedDayIndex):
						s.PastOutlookDay = pastOutlook.SelectedDayOption.Day; break;
					case nameof(PastOutlookViewModel.SelectedProductOption):
						s.PastOutlookProduct = ProductKey(pastOutlook.SelectedProductOption.Type); break;
					case nameof(PastOutlookViewModel.SelectedCycleOption):
						s.PastOutlookCycle = pastOutlook.SelectedCycleOption.Cycle; break;
					case nameof(PastOutlookViewModel.Opacity):
						s.PastOutlookOpacity = pastOutlook.Opacity; break;
				}
			};

			outlook.PropertyChanged += (_, e) =>
			{
				switch (e.PropertyName)
				{
					case nameof(OutlookViewModel.SelectedDayOption):
						if (outlook.SelectedDayOption is { } day) { s.ForeCastOutlookDay = day.Day; }
						break;
					// ⚠️ The product combo transiently NULLS its selection while the list swaps under a day
					// change — never save that; the real pick follows a moment later.
					case nameof(OutlookViewModel.SelectedOption):
						if (outlook.SelectedOption is { } option) { s.ForeCastOutlookProduct = ProductKey(option.Product?.Type); }
						break;
					case nameof(OutlookViewModel.OutlookOpacity): s.ForeCastOutlookOpacity = outlook.OutlookOpacity; break;
					case nameof(OutlookViewModel.ShowHatching): s.ForeCastShowHatching = outlook.ShowHatching; break;
				}
			};
		}

		// The persisted name of a product: the enum NAME, "None" for the off option.
		private static string ProductKey(Models.SpcOutlookType? type) => type?.ToString() ?? NoneProduct;

		// A hand-edited file can hold anything; an opacity outside 0-1 would push a nonsense value to the page.
		private static double Unit(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 1;
	}
}
