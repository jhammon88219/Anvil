using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="TemporalWindowPersistence"/>: every temporal-window choice RESTORES from settings into the
	/// real view models, a user change is SAVED back, restoring writes nothing, and a never-set (null)
	/// setting leaves the view model's own default alone.
	/// </summary>
	/// <remarks>
	/// The view models are the real ones; their services are <see cref="Null{T}"/> stand-ins that do
	/// nothing, so no map, network or disk is touched. Two outlook calls are overridden so the live outlook
	/// has days and products to pick between.
	/// </remarks>
	public class TemporalWindowPersistenceTests
	{
		// ── A do-nothing implementation of any interface ───────────────────────────────────────────────
		public class Null<T> : DispatchProxy where T : class
		{
			private Dictionary<string, Func<object?[]?, object?>> _overrides = new();

			public static T Create(Dictionary<string, Func<object?[]?, object?>>? overrides = null)
			{
				var proxy = Create<T, Null<T>>();
				((Null<T>)(object)proxy)._overrides = overrides ?? new();
				return proxy;
			}

			protected override object? Invoke(MethodInfo? method, object?[]? args)
			{
				if (method is null) { return null; }
				if (_overrides.TryGetValue(method.Name, out var fn)) { return fn(args); }
				return DefaultFor(method.ReturnType);
			}

			private static object? DefaultFor(Type t)
			{
				if (t == typeof(void)) { return null; }
				if (t == typeof(Task)) { return Task.CompletedTask; }
				if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>))
				{
					var inner = DefaultFor(t.GetGenericArguments()[0]);
					return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(t.GetGenericArguments()[0])
						.Invoke(null, new[] { inner });
				}
				if (t == typeof(string)) { return ""; }
				if (t.IsArray) { return Array.CreateInstance(t.GetElementType()!, 0); }
				if (t.IsGenericType && typeof(IEnumerable).IsAssignableFrom(t))
				{
					var list = typeof(List<>).MakeGenericType(t.GetGenericArguments()[0]);
					if (t.IsAssignableFrom(list)) { return Activator.CreateInstance(list); }
				}
				return t.IsValueType ? Activator.CreateInstance(t) : null;
			}
		}

		private sealed class Rig
		{
			public readonly AppSettings Settings;
			public readonly RadarViewModel Radar;
			public readonly WarningsViewModel Warnings;
			public readonly WatchesViewModel Watches;
			public readonly StormReportsViewModel Reports;
			public readonly DamageSurveysViewModel Damage;
			public readonly PastOutlookViewModel PastOutlook;
			public readonly OutlookViewModel Outlook;
			public readonly StormCellsViewModel Cells;

			public Rig(AppSettings settings)
			{
				Settings = settings;
				var map = Null<IMapService>.Create();
				var dispatcher = Null<IDispatcher>.Create();
				var settingsService = Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
				var spc = Null<ISpcOutlookService>.Create(new()
				{
					["get_AvailableDays"] = _ => new[] { 1, 2 },
					["GetProductsForDay"] = a => new[]
					{
						Product((int)a![0]!, SpcOutlookType.Categorical),
						Product((int)a![0]!, SpcOutlookType.Tornado),
					},
				});

				Radar = new RadarViewModel(map, Null<IRadarSiteProvider>.Create(), Null<ILevel2RadarService>.Create(),
					Null<IDowEventProvider>.Create(), settingsService, null);
				Warnings = new WarningsViewModel(map, Null<IWarningService>.Create(), dispatcher, NullLogger<WarningsViewModel>.Instance);
				Watches = new WatchesViewModel(map, Null<ISpcWatchService>.Create(), dispatcher, NullLogger<WatchesViewModel>.Instance);
				Reports = new StormReportsViewModel(map, Null<IStormReportService>.Create(), Radar, dispatcher, NullLogger<StormReportsViewModel>.Instance);
				Damage = new DamageSurveysViewModel(map, Null<IDamageSurveyService>.Create(), Radar, NullLogger<DamageSurveysViewModel>.Instance);
				PastOutlook = new PastOutlookViewModel(map, spc, Radar);
				Outlook = new OutlookViewModel(map, spc, dispatcher, NullLogger<OutlookViewModel>.Instance);
				Cells = new StormCellsViewModel(map, Null<IStormCellService>.Create(), Radar, dispatcher, NullLogger<StormCellsViewModel>.Instance);

				TemporalWindowPersistence.Attach(settings, Radar, Warnings, Watches, Reports, Damage, PastOutlook, Outlook, Cells);
			}

			private static SpcOutlookProduct Product(int day, SpcOutlookType type) =>
				new($"d{day}{type}", day, type, type.ToString(), $"d{day}{type}.geojson", $"https://x/d{day}{type}.geojson");
		}

		[Fact]
		public void NeverSet_KeepsViewModelDefaults_AndWritesNothing()
		{
			var s = new AppSettings();
			var rig = new Rig(s);

			Assert.True(rig.Radar.ShowRadarLayer);
			Assert.True(rig.Warnings.ShowTornado);
			Assert.False(rig.Damage.ShowPoints);
			Assert.Equal(SpcOutlookType.Categorical, rig.Outlook.SelectedOption?.Product?.Type);

			// Restoring nothing must not have written defaults into the file.
			Assert.Null(s.RadarOpacity);
			Assert.Null(s.ShowRadarLayer);
			Assert.Null(s.WarningsShowTornado);
			Assert.Null(s.StormCellsShowTracks);
			Assert.Null(s.PastOutlookProduct);
			Assert.Null(s.ForeCastOutlookProduct);
		}

		[Fact]
		public void Restores_EveryChoice()
		{
			var s = new AppSettings
			{
				RadarOpacity = 0.35, ShowRadarLayer = false,
				WarningsShowTornado = false, WarningsShowSevere = true, WarningsShowFlashFlood = false, WarningsOpacity = 0.4,
				WatchesShowTornado = true, WatchesShowSevere = false, WatchesOpacity = 0.6,
				StormReportsShowTornado = false, StormReportsShowWind = true, StormReportsShowHail = false, StormReportsOpacity = 0.5,
				DamageSurveysShowAreas = false, DamageSurveysShowTracks = true, DamageSurveysShowPoints = true, DamageSurveysOpacity = 0.25,
				StormCellsShowTracks = true, StormCellsShowTvs = false, StormCellsShowMeso = true, StormCellsShowHail = false, StormCellsOpacity = 0.65,
				PastOutlookDay = 2, PastOutlookProduct = "ProbabilisticCombined", PastOutlookOpacity = 0.3,
				ForeCastOutlookDay = 2, ForeCastOutlookProduct = "Tornado", ForeCastOutlookOpacity = 0.45, ForeCastShowHatching = false,
			};
			var rig = new Rig(s);

			Assert.Equal(0.35, rig.Radar.RadarOpacity);
			Assert.False(rig.Radar.ShowRadarLayer);
			Assert.False(rig.Warnings.ShowTornado);
			Assert.False(rig.Warnings.ShowFlashFlood);
			Assert.Equal(0.4, rig.Warnings.Opacity);
			Assert.False(rig.Watches.ShowSevere);
			Assert.Equal(0.6, rig.Watches.Opacity);
			Assert.False(rig.Reports.ShowTornado);
			Assert.False(rig.Reports.ShowHail);
			Assert.Equal(0.5, rig.Reports.Opacity);
			Assert.False(rig.Damage.ShowAreas);
			Assert.True(rig.Damage.ShowPoints);
			Assert.Equal(0.25, rig.Damage.Opacity);
			Assert.False(rig.Cells.ShowTvs);
			Assert.False(rig.Cells.ShowHail);
			Assert.True(rig.Cells.ShowMeso);
			Assert.Equal(0.65, rig.Cells.Opacity);
			Assert.Equal(2, rig.PastOutlook.SelectedDayOption.Day);
			Assert.Equal(SpcOutlookType.ProbabilisticCombined, rig.PastOutlook.SelectedProductOption.Type);
			Assert.Equal(0.3, rig.PastOutlook.Opacity);
			Assert.Equal(2, rig.Outlook.SelectedDayOption?.Day);
			Assert.Equal(SpcOutlookType.Tornado, rig.Outlook.SelectedOption?.Product?.Type);
			Assert.Equal(0.45, rig.Outlook.OutlookOpacity);
			Assert.False(rig.Outlook.ShowHatching);
		}

		[Fact]
		public void Saves_UserChanges()
		{
			var s = new AppSettings();
			var rig = new Rig(s);

			rig.Radar.RadarOpacity = 0.55;
			rig.Warnings.ToggleAll();                       // all on → all off
			rig.Reports.ShowWind = false;
			rig.Damage.Opacity = 0.9;
			rig.Cells.ToggleAll();                          // all on → all off
			rig.PastOutlook.SelectedDayIndex = 1;           // day 2 cascades product + cycle
			rig.PastOutlook.SelectedProductOption = rig.PastOutlook.ProductOptions.First(o => o.Type == SpcOutlookType.Categorical);
			rig.Outlook.SelectedOption = rig.Outlook.ProductOptions[0]; // None
			rig.Outlook.ShowHatching = false;

			Assert.Equal(0.55, s.RadarOpacity);
			Assert.False(s.WarningsShowTornado);
			Assert.False(s.WarningsShowSevere);
			Assert.False(s.WarningsShowFlashFlood);
			Assert.False(s.StormReportsShowWind);
			Assert.Equal(0.9, s.DamageSurveysOpacity);
			Assert.False(s.StormCellsShowTracks);
			Assert.False(s.StormCellsShowHail);
			Assert.Equal(2, s.PastOutlookDay);
			Assert.Equal("Categorical", s.PastOutlookProduct);
			Assert.Null(s.PastOutlookCycle);                // Auto
			Assert.Equal("None", s.ForeCastOutlookProduct);
			Assert.False(s.ForeCastShowHatching);
		}

		[Fact]
		public void SavedValues_SurviveANewSession()
		{
			var s = new AppSettings();
			var first = new Rig(s);
			first.Watches.Opacity = 0.2;
			first.Outlook.SelectedDayOption = first.Outlook.Days.First(d => d.Day == 2);
			first.Outlook.SelectedOption = first.Outlook.ProductOptions.First(o => o.Product?.Type == SpcOutlookType.Tornado);

			var second = new Rig(s);                        // same settings object = the reloaded file
			Assert.Equal(0.2, second.Watches.Opacity);
			Assert.Equal(2, second.Outlook.SelectedDayOption?.Day);
			Assert.Equal(SpcOutlookType.Tornado, second.Outlook.SelectedOption?.Product?.Type);
		}

		[Fact]
		public void OutOfRangeOpacity_IsClamped()
		{
			var rig = new Rig(new AppSettings { RadarOpacity = 7, StormReportsOpacity = -1 });
			Assert.Equal(1, rig.Radar.RadarOpacity);
			Assert.Equal(0, rig.Reports.Opacity);
		}
	}
}
