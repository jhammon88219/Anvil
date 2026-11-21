using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.TemporalPanes
{
	/// <summary>
	/// A minimized temporal pane's whole face (see the XAML header): the expand chevron + one icon key per layer. Placed
	/// and filled by its <see cref="TemporalPane"/>.
	/// </summary>
	public sealed partial class TemporalPaneKeyColumn : UserControl
	{
		public TemporalPaneKeyColumn()
		{
			InitializeComponent();
		}

		/// <summary>The keys, top first (the pane's z-order).</summary>
		public IReadOnlyList<TemporalPaneKeyColumnItem>? Items
		{
			get => (IReadOnlyList<TemporalPaneKeyColumnItem>?)GetValue(ItemsProperty);
			set => SetValue(ItemsProperty, value);
		}
		public static readonly DependencyProperty ItemsProperty =
			DependencyProperty.Register(nameof(Items), typeof(IReadOnlyList<TemporalPaneKeyColumnItem>), typeof(TemporalPaneKeyColumn),
				new PropertyMetadata(null));

		/// <summary>The first key's top, px from the pane's top (the full pane's first layer row).</summary>
		public double KeysTop
		{
			get => (double)GetValue(KeysTopProperty);
			set => SetValue(KeysTopProperty, value);
		}
		public static readonly DependencyProperty KeysTopProperty =
			DependencyProperty.Register(nameof(KeysTop), typeof(double), typeof(TemporalPaneKeyColumn), new PropertyMetadata(0.0));

		/// <summary>The expand chevron's top, px from the pane's top (level with the full pane's ‹).</summary>
		public double ExpandTop
		{
			get => (double)GetValue(ExpandTopProperty);
			set => SetValue(ExpandTopProperty, value);
		}
		public static readonly DependencyProperty ExpandTopProperty =
			DependencyProperty.Register(nameof(ExpandTop), typeof(double), typeof(TemporalPaneKeyColumn), new PropertyMetadata(0.0));

		/// <summary>The pane sits on the window's RIGHT (ForeCast): its chevron points left.</summary>
		public bool IsRightPane
		{
			get => (bool)GetValue(IsRightPaneProperty);
			set => SetValue(IsRightPaneProperty, value);
		}
		public static readonly DependencyProperty IsRightPaneProperty =
			DependencyProperty.Register(nameof(IsRightPane), typeof(bool), typeof(TemporalPaneKeyColumn), new PropertyMetadata(false));

		/// <summary>The expand chevron was clicked.</summary>
		public event EventHandler? ExpandRequested;

		private void OnExpandClick(object sender, RoutedEventArgs e) => ExpandRequested?.Invoke(this, EventArgs.Empty);

		public Thickness TopMargin(double top) => new(0, Math.Max(0, top), 0, 0);

		// The chevron points the way the pane will MOVE (the bar tabs' rule): a left pane grows rightward (›), ForeCast leftward (‹).
		public string ExpandGlyph(bool rightPane) => rightPane ? "" : "";
	}
}
