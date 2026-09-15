using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The map's site-check toast (see the XAML header): progress while an ANNOUNCED availability pass cascades,
	/// then a summary for a few seconds. Driven entirely by <see cref="RadarViewModel"/>'s pass state.
	/// </summary>
	public sealed partial class SiteCheckToast : UserControl
	{
		private const int SummaryMilliseconds = 4000;

		private bool _checking;
		private int _version; // bumped per show, so an old pass's fade can't hide a newer toast

		public SiteCheckToast()
		{
			InitializeComponent();
			Unloaded += (_, _) => Detach(ViewModel);
		}

		public RadarViewModel ViewModel
		{
			get => (RadarViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(RadarViewModel), typeof(SiteCheckToast),
				new PropertyMetadata(null, (d, e) =>
				{
					var toast = (SiteCheckToast)d;
					toast.Detach(e.OldValue as RadarViewModel);
					toast.Attach(e.NewValue as RadarViewModel);
				}));

		private void Attach(RadarViewModel? vm)
		{
			if (vm is null) return;
			vm.PropertyChanged += OnViewModelPropertyChanged;
			vm.SiteCheckFinished += OnSiteCheckFinished;
			// The launch pass can already be running when the window builds this control.
			if (vm.IsSiteCheckRunning && vm.IsSiteCheckAnnounced)
			{
				ShowChecking(vm);
			}
		}

		private void Detach(RadarViewModel? vm)
		{
			if (vm is null) return;
			vm.PropertyChanged -= OnViewModelPropertyChanged;
			vm.SiteCheckFinished -= OnSiteCheckFinished;
		}

		private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (ViewModel is not { } vm) return;

			if (e.PropertyName == nameof(RadarViewModel.IsSiteCheckRunning) && vm.IsSiteCheckRunning && vm.IsSiteCheckAnnounced)
			{
				ShowChecking(vm);
			}
			else if (e.PropertyName == nameof(RadarViewModel.SitesChecked) && _checking)
			{
				UpdateChecking(vm);
			}
		}

		private void OnSiteCheckFinished(object? sender, bool completed)
		{
			if (!_checking || ViewModel is not { } vm) return;
			_checking = false;

			if (completed)
			{
				OnlineText.Text = $"{vm.SitesOnline} online";
				OfflineText.Text = $"{vm.SitesOffline} offline";
				Show(DonePanel);
			}
			else if (vm.IsPastEventMode)
			{
				// Entering PastCast cut the pass short on purpose — nothing failed, so say nothing.
				Pill.Opacity = 0;
				return;
			}
			else
			{
				Show(FailedText);
			}
			_ = FadeAfterAsync(_version, SummaryMilliseconds);
		}

		private void ShowChecking(RadarViewModel vm)
		{
			_checking = true;
			_version++;
			Show(CheckingPanel);
			UpdateChecking(vm);
			Pill.Opacity = 1;
		}

		private void UpdateChecking(RadarViewModel vm)
		{
			var total = vm.SiteCheckTotal;
			CheckingText.Text = $"Checking radar sites · {vm.SitesChecked} / {total}";
			CheckProgress.Value = total == 0 ? 0 : (double)vm.SitesChecked / total;
		}

		private void Show(FrameworkElement panel)
		{
			CheckingPanel.Visibility = panel == CheckingPanel ? Visibility.Visible : Visibility.Collapsed;
			DonePanel.Visibility = panel == DonePanel ? Visibility.Visible : Visibility.Collapsed;
			FailedText.Visibility = panel == FailedText ? Visibility.Visible : Visibility.Collapsed;
		}

		private async Task FadeAfterAsync(int version, int milliseconds)
		{
			await Task.Delay(milliseconds);
			if (version == _version && !_checking)
			{
				Pill.Opacity = 0; // OpacityTransition eases it out
			}
		}
	}
}
