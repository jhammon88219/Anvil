using System.Globalization;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// Settings → Radar → "Radar memory": the RAM a PastCast loop may hold, as a slider over this PC's detected RAM, with
	/// what it buys per pane layout and one warning line. Sub-VM of <see cref="RadarViewModel"/> (<c>Radar.Memory</c>);
	/// the math is <see cref="RadarMemoryBudget"/>, the persisted value <see cref="AppSettings.RadarMemoryGb"/>.
	/// </summary>
	/// <remarks>
	/// The budget sizes the PastCast frame cap at each Load (RadarLoopEngine) from the panes' products AT LOAD. A pane or
	/// layout change afterwards is only LOGGED when it puts the loop over budget (the user's call, 2026-10-08) — the next
	/// Load plans for it. NowCast loops (≤ 30 frames) are untouched.
	/// </remarks>
	public sealed class RadarMemoryViewModel : ObservableObject
	{
		private readonly ISettingsService _settings;

		public RadarMemoryViewModel(ISettingsService settings, double? machineRamGb = null)
		{
			_settings = settings;
			RamGb = machineRamGb ?? RadarMemoryBudget.MachineRamGb();
		}

		/// <summary>This PC's RAM in GB.</summary>
		public double RamGb { get; }

		public double MinGb => RadarMemoryBudget.MinGb;
		public double MaxGb => RadarMemoryBudget.MaxGb(RamGb);
		public double StepGb => RadarMemoryBudget.StepGb;
		public double RecommendedGb => RadarMemoryBudget.RecommendedGb(RamGb);

		/// <summary>"16 GB of RAM".</summary>
		public string RamText => $"{Gb(RamGb)} of RAM";

		/// <summary>The effective budget (GB). Two-way with the slider; persisted on every change.</summary>
		public double BudgetGb
		{
			get => RadarMemoryBudget.Effective(_settings.Settings.RadarMemoryGb, RamGb);
			set
			{
				var gb = RadarMemoryBudget.Effective(value, RamGb);
				if (_settings.Settings.RadarMemoryGb == gb) { return; }
				_settings.Settings.RadarMemoryGb = gb; // persists (auto-save)
				RaiseAll();
			}
		}

		/// <summary>Back to the recommended default (null = it follows this PC's RAM).</summary>
		public void UseRecommended()
		{
			_settings.Settings.RadarMemoryGb = null;
			RaiseAll();
		}

		public bool IsRecommended => BudgetGb == RecommendedGb;

		/// <summary>"4 GB" — the slider's value readout.</summary>
		public string BudgetText => Gb(BudgetGb);

		// The readout: frames per layout at its WORST cost (every pane a different dual-pol product) — "at least".
		public string OnePaneText => LayoutText(1, "1 pane");
		public string TwoPaneText => LayoutText(2, "2 panes");
		public string FourPaneText => LayoutText(4, "4 panes");

		public RadarMemoryLevel Level => RadarMemoryBudget.LevelOf(BudgetGb, RamGb);
		public bool IsWarning => Level != RadarMemoryLevel.Fine;

		/// <summary>The one line under the readout.</summary>
		public string NoteText => Level switch
		{
			RadarMemoryLevel.High => "High: over half this PC's memory. With other apps open, your PC may slow down or the map may go black.",
			RadarMemoryLevel.Low => "Low: 4-pane loops will be short.",
			_ => $"Recommended is a quarter of your RAM ({Gb(RecommendedGb)} here).",
		};

		/// <summary>The PastCast frame cap for a loop whose frames cost <paramref name="products"/> products.</summary>
		public int FrameCap(int products) => RadarMemoryBudget.FrameCap(BudgetGb, products);

		private string LayoutText(int panes, string label) =>
			$"{label}: {RadarMemoryBudget.FrameCap(BudgetGb, RadarMemoryBudget.WorstProductsFor(panes))}+ frames";

		private static string Gb(double gb) => gb.ToString("0.#", CultureInfo.InvariantCulture) + " GB";

		private void RaiseAll()
		{
			OnPropertyChanged(nameof(BudgetGb));
			OnPropertyChanged(nameof(BudgetText));
			OnPropertyChanged(nameof(IsRecommended));
			OnPropertyChanged(nameof(OnePaneText));
			OnPropertyChanged(nameof(TwoPaneText));
			OnPropertyChanged(nameof(FourPaneText));
			OnPropertyChanged(nameof(Level));
			OnPropertyChanged(nameof(IsWarning));
			OnPropertyChanged(nameof(NoteText));
		}
	}
}
