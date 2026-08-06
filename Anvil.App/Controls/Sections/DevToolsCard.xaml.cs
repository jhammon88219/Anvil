using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Sections
{
	/// <summary>
	/// DEV-ONLY dev-tools card (see the XAML header). Combines the former DevSweepCard + DevValidationCard
	/// into ONE card with two labeled sections, bound to a <see cref="SiteSweepViewModel"/> (site sweep) and
	/// a <see cref="RadarValidationViewModel"/> (dealias validation). Its open/close is a plain view-state
	/// bool (<see cref="IsCardOpen"/>) toggled by the bar's dev "Dev" button — a dev tool has no app-wide VM
	/// flag. Each tool raises its own report event on run completion / its Report button so the host can open
	/// the matching results dialog.
	/// </summary>
	public sealed partial class DevToolsCard : UserControl
	{
		public DevToolsCard()
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
			DependencyProperty.Register(nameof(SweepVm), typeof(SiteSweepViewModel), typeof(DevToolsCard), new PropertyMetadata(null));

		/// <summary>The dealias-validation engine; bound from the host.</summary>
		public RadarValidationViewModel? ValidationVm
		{
			get => (RadarValidationViewModel?)GetValue(ValidationVmProperty);
			set => SetValue(ValidationVmProperty, value);
		}

		public static readonly DependencyProperty ValidationVmProperty =
			DependencyProperty.Register(nameof(ValidationVm), typeof(RadarValidationViewModel), typeof(DevToolsCard), new PropertyMetadata(null));

		/// <summary>Whether the card is open (bar button ↔ this). View state on the control, like the
		/// OverlayBar's own pull-tab visibility — a dev tool has no app-wide VM flag.</summary>
		public bool IsCardOpen
		{
			get => (bool)GetValue(IsCardOpenProperty);
			set => SetValue(IsCardOpenProperty, value);
		}

		public static readonly DependencyProperty IsCardOpenProperty =
			DependencyProperty.Register(nameof(IsCardOpen), typeof(bool), typeof(DevToolsCard), new PropertyMetadata(false));

		/// <summary>Raised when the user asks to see the finished site-sweep report.</summary>
		public event EventHandler<SweepReport>? SweepReportRequested;

		/// <summary>Raised when the user asks to see the finished dealias-validation report.</summary>
		public event EventHandler<RadarValidationReport>? ValidationReportRequested;

		public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

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

		private void OnCardCloseRequested(object sender, RoutedEventArgs e) => IsCardOpen = false;
	}
}
