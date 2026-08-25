using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// DEV-ONLY dev-tools tab of the Settings window (see the XAML header): the site sweep, the fixed-corpus
	/// dealias validation, and the Pipeline Console's switch. Bound to a <see cref="SiteSweepViewModel"/> and
	/// a <see cref="RadarValidationViewModel"/>, plus the coordinator <see cref="MapViewModel"/> for the
	/// console toggle. Each tool raises its own report event on run completion / its Report button so the host
	/// can open the matching results dialog.
	/// </summary>
	/// <remarks>
	/// ⚠️ Both engine view models are NULL in a Release build (MainWindow constructs them under
	/// <c>#if DEBUG</c>). SettingsWindow is what keeps this body from ever being constructed there — it omits
	/// the tab from the strip and does not x:Load this control. Don't rely on the null-tolerance of x:Bind
	/// instead; the point is that the dev tools are unreachable, not merely blank.
	/// </remarks>
	public sealed partial class DevSettingsTab : UserControl
	{
		public DevSettingsTab()
		{
			InitializeComponent();
			Loaded += OnLoaded;
		}

		/// <summary>The site-sweep engine; bound from the host.</summary>
		public SiteSweepViewModel? SweepVm
		{
			get => (SiteSweepViewModel?)GetValue(SweepVmProperty);
			set => SetValue(SweepVmProperty, value);
		}

		public static readonly DependencyProperty SweepVmProperty =
			DependencyProperty.Register(nameof(SweepVm), typeof(SiteSweepViewModel), typeof(DevSettingsTab), new PropertyMetadata(null));

		/// <summary>The dealias-validation engine; bound from the host.</summary>
		public RadarValidationViewModel? ValidationVm
		{
			get => (RadarValidationViewModel?)GetValue(ValidationVmProperty);
			set => SetValue(ValidationVmProperty, value);
		}

		public static readonly DependencyProperty ValidationVmProperty =
			DependencyProperty.Register(nameof(ValidationVm), typeof(RadarValidationViewModel), typeof(DevSettingsTab), new PropertyMetadata(null));

		/// <summary>The coordinator view model; bound from the host. Only the console toggle uses it.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(DevSettingsTab), new PropertyMetadata(null));

		/// <summary>Raised when the user asks to see the finished site-sweep report.</summary>
		public event EventHandler<SweepReport>? SweepReportRequested;

		/// <summary>Raised when the user asks to see the finished dealias-validation report.</summary>
		public event EventHandler<RadarValidationReport>? ValidationReportRequested;

		// Seed the sweep NumberBoxes from the VM once it's bound (they're double-valued; the VM props are int),
		// and subscribe both VMs for auto-opening their reports on completion.
		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			if (SweepVm is not null)
			{
				DwellBox.Value = SweepVm.DwellSeconds;
				TimeoutBox.Value = SweepVm.PerSiteTimeoutSeconds;
				FramesBox.Value = SweepVm.FramesPerSite;
				SweepVm.PropertyChanged += OnSweepPropertyChanged;
			}

			if (ValidationVm is not null)
			{
				ValidationVm.PropertyChanged += OnValidationPropertyChanged;
			}
		}

		// Auto-open the sweep report the moment a run produces one.
		private void OnSweepPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(SiteSweepViewModel.HasReport) && SweepVm?.LastReport is { } report)
			{
				SweepReportRequested?.Invoke(this, report);
			}
		}

		// Auto-open the validation report the moment a run produces one.
		private void OnValidationPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(RadarValidationViewModel.HasReport) && ValidationVm?.LastReport is { } report)
			{
				ValidationReportRequested?.Invoke(this, report);
			}
		}

		private void OnDwellChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
		{
			if (SweepVm is not null && !double.IsNaN(args.NewValue)) SweepVm.DwellSeconds = (int)args.NewValue;
		}

		private void OnTimeoutChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
		{
			if (SweepVm is not null && !double.IsNaN(args.NewValue)) SweepVm.PerSiteTimeoutSeconds = (int)args.NewValue;
		}

		private void OnFramesChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
		{
			if (SweepVm is not null && !double.IsNaN(args.NewValue)) SweepVm.FramesPerSite = (int)args.NewValue;
		}

		private async void OnStartClick(object sender, RoutedEventArgs e)
		{
			if (SweepVm is not null) await SweepVm.StartAsync();
		}

		private void OnStopClick(object sender, RoutedEventArgs e) => SweepVm?.Stop();

		private void OnSweepReportClick(object sender, RoutedEventArgs e)
		{
			if (SweepVm?.LastReport is { } report) SweepReportRequested?.Invoke(this, report);
		}

		private async void OnRunClick(object sender, RoutedEventArgs e)
		{
			if (ValidationVm is not null) await ValidationVm.StartAsync();
		}

		private void OnValidationStopClick(object sender, RoutedEventArgs e) => ValidationVm?.Stop();

		private void OnValidationReportClick(object sender, RoutedEventArgs e)
		{
			if (ValidationVm?.LastReport is { } report) ValidationReportRequested?.Invoke(this, report);
		}
	}
}
