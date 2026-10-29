using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.TemporalPanes
{
	/// <summary>One layer section's rail button (see the XAML header): the body toggles the layer, the car docks it.</summary>
	public sealed partial class TemporalPaneRailButton : UserControl
	{
		// Segoe Fluent ChevronLeft / ChevronRight.
		private const string ChevronLeft = "";
		private const string ChevronRight = "";

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

		// The car is on the MAP side: right of the body on a left-edge pane, left of it on ForeCast's right edge.
		public int BodyColumn(bool rightEdge) => rightEdge ? 1 : 0;
		public int CarColumn(bool rightEdge) => rightEdge ? 0 : 1;

		// Toward the map while undocked, back toward the edge while docked — mirrored on the right edge.
		public string CarGlyph(bool docked, bool rightEdge) => docked ^ rightEdge ? ChevronLeft : ChevronRight;

		public string CarToolTip(bool docked) => docked ? "Undock from beside the rail" : "Dock beside the rail";

		private void OnBodyClick(object sender, RoutedEventArgs e)
		{
			if (Section is null) { return; }
			Section.Toggle();
			TemporalPaneRailButtonBody.IsChecked = Section.IsOn;
		}

		private void OnCarClick(object sender, RoutedEventArgs e)
		{
			if (Section is null) { return; }
			Section.ToggleDocked();
			TemporalPaneRailButtonCar.IsChecked = Section.IsDocked;
		}
	}
}
