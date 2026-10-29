using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.TemporalPanes
{
	/// <summary>A temporal pane collapsed to its rail (see the XAML header): expand + one button per layer section.</summary>
	public sealed partial class TemporalPaneRail : UserControl
	{
		public TemporalPaneRail()
		{
			InitializeComponent();
			Loaded += (_, _) => ApplyEdge();
		}

		/// <summary>The pane this rail belongs to; bound from TemporalPane.</summary>
		public TemporalPaneViewModel Pane
		{
			get => (TemporalPaneViewModel)GetValue(PaneProperty);
			set => SetValue(PaneProperty, value);
		}

		public static readonly DependencyProperty PaneProperty =
			DependencyProperty.Register(nameof(Pane), typeof(TemporalPaneViewModel), typeof(TemporalPaneRail),
				new PropertyMetadata(null, (d, _) => ((TemporalPaneRail)d).ApplyEdge()));

		// ForeCast's rail is mirrored (the chevron points the other way). Not templated, so GoToState works before Loaded too.
		private void ApplyEdge() =>
			VisualStateManager.GoToState(this, Pane?.IsRightEdge == true ? "RightEdge" : "LeftEdge", false);

		private void OnExpandClick(object sender, RoutedEventArgs e) => Pane?.ToggleState();
	}
}
