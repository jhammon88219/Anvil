using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>Where one download step stands: not reached, running now, or finished.</summary>
	public enum BasemapDownloadStepState { Waiting, Running, Done }

	/// <summary>
	/// One row of the setup window's download readout (<c>BasemapSetupWindowDownloadStep</c>): a name, its own bar and a
	/// detail line. <see cref="BasemapSetupViewModel"/> owns the five and moves them; a finished row keeps its full bar,
	/// dimmed, so the run reads as filling top to bottom (the user's call, 2026-10-20).
	/// </summary>
	public sealed partial class BasemapDownloadStep : ObservableObject
	{
		public BasemapDownloadStep(string name, bool counts)
		{
			Name = name;
			Counts = counts;
		}

		public string Name { get; }

		/// <summary>False = nothing to count (finding the build, finishing): the bar runs as a moving strip while it works.</summary>
		public bool Counts { get; }

		private BasemapDownloadStepState _state;
		public BasemapDownloadStepState State
		{
			get => _state;
			set
			{
				if (SetProperty(ref _state, value))
				{
					OnPropertyChanged(nameof(IsIndeterminate));
					if (value == BasemapDownloadStepState.Done) Percent = 100;
					if (value == BasemapDownloadStepState.Waiting) { Percent = 0; Detail = ""; }
				}
			}
		}

		private double _percent;
		/// <summary>0-100 of this step alone.</summary>
		public double Percent
		{
			get => _percent;
			set => SetProperty(ref _percent, value);
		}

		private string _detail = "";
		/// <summary>The right-hand words: "41 of 83 sections", "3.1 GB of 7.96 GB · 94 MB/s · about 1 minute left".</summary>
		public string Detail
		{
			get => _detail;
			set => SetProperty(ref _detail, value);
		}

		public bool IsIndeterminate => !Counts && _state == BasemapDownloadStepState.Running;
	}
}
