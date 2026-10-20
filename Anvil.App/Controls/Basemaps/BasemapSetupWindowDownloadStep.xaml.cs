using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Basemaps
{
	/// <summary>One download step's row (see the XAML header): its visual state follows <see cref="BasemapDownloadStep.State"/>.</summary>
	public sealed partial class BasemapSetupWindowDownloadStep : UserControl
	{
		public BasemapSetupWindowDownloadStep()
		{
			InitializeComponent();
			Loaded += (_, _) => ApplyState(); // GoToState before the first layout can be a no-op — re-apply once loaded
		}

		public BasemapDownloadStep Step
		{
			get => (BasemapDownloadStep)GetValue(StepProperty);
			set => SetValue(StepProperty, value);
		}

		public static readonly DependencyProperty StepProperty =
			DependencyProperty.Register(nameof(Step), typeof(BasemapDownloadStep), typeof(BasemapSetupWindowDownloadStep),
				new PropertyMetadata(null, OnStepChanged));

		private static void OnStepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var row = (BasemapSetupWindowDownloadStep)d;
			if (e.OldValue is BasemapDownloadStep old) old.PropertyChanged -= row.OnStepPropertyChanged;
			if (e.NewValue is BasemapDownloadStep now) now.PropertyChanged += row.OnStepPropertyChanged;
			row.ApplyState();
		}

		private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(BasemapDownloadStep.State)) ApplyState();
		}

		private void ApplyState() =>
			VisualStateManager.GoToState(this, (Step?.State ?? BasemapDownloadStepState.Waiting).ToString(), false);
	}
}
