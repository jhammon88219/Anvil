using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.TemporalPanes
{
	/// <summary>One layer section's rail button (see the XAML header): its body toggles the layer.</summary>
	public sealed partial class TemporalPaneRailButton : UserControl
	{
		public TemporalPaneRailButton()
		{
			InitializeComponent();
		}

		/// <summary>The section this button stands for; bound from the rail's item template.</summary>
		public TemporalPaneSectionViewModel Section
		{
			get => (TemporalPaneSectionViewModel)GetValue(SectionProperty);
			set => SetValue(SectionProperty, value);
		}

		public static readonly DependencyProperty SectionProperty =
			DependencyProperty.Register(nameof(Section), typeof(TemporalPaneSectionViewModel), typeof(TemporalPaneRailButton), new PropertyMetadata(null));

		private void OnBodyClick(object sender, RoutedEventArgs e)
		{
			if (Section is null) { return; }
			Section.Toggle();
			TemporalPaneRailButtonBody.IsChecked = Section.IsOn;
		}
	}
}
