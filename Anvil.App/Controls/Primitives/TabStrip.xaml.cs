using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The left nav rail that selects one page of its host (see TabStrip.xaml for the drawn shape and the rules).
	/// Give it <see cref="ItemsSource"/> of <see cref="TabEntry"/> and bind <see cref="SelectedIndex"/> two-way;
	/// the host swaps its own body on that index.
	/// </summary>
	public sealed partial class TabStrip : UserControl
	{
		public TabStrip()
		{
			InitializeComponent();
		}

		/// <summary>The rows, in rail order. Items must be <see cref="TabEntry"/>.</summary>
		public IEnumerable? ItemsSource
		{
			get => (IEnumerable?)GetValue(ItemsSourceProperty);
			set => SetValue(ItemsSourceProperty, value);
		}

		public static readonly DependencyProperty ItemsSourceProperty =
			DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(TabStrip),
				new PropertyMetadata(null, OnItemsSourceChanged));

		private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var strip = (TabStrip)d;
			strip.Host.ItemsSource = e.NewValue;
			strip.ApplySelection();
		}

		/// <summary>The selected row's index. Two-way bindable; an out-of-range value selects nothing rather
		/// than throwing (the host is responsible for clamping — see MapViewModel.SettingsTabIndex).</summary>
		public int SelectedIndex
		{
			get => (int)GetValue(SelectedIndexProperty);
			set => SetValue(SelectedIndexProperty, value);
		}

		public static readonly DependencyProperty SelectedIndexProperty =
			DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(TabStrip),
				new PropertyMetadata(0, (d, _) => ((TabStrip)d).ApplySelection()));

		/// <summary>
		/// The selected row draws NO face and NO accent bar of its own: the host's body reaches out around it
		/// (PlateOutline's JOIN, the Settings window — the user's pick 2026-11-17), so the page's ground IS its face. The
		/// host measures the row with <see cref="SelectedRowBounds"/>.
		/// </summary>
		public bool JoinsBody
		{
			get => (bool)GetValue(JoinsBodyProperty);
			set => SetValue(JoinsBodyProperty, value);
		}

		public static readonly DependencyProperty JoinsBodyProperty =
			DependencyProperty.Register(nameof(JoinsBody), typeof(bool), typeof(TabStrip),
				new PropertyMetadata(false, (d, _) => ((TabStrip)d).ApplySelection()));

		/// <summary>
		/// Show NO row as selected while true (the Settings window's search results own the moment) — the host's
		/// <see cref="SelectedIndex"/> is kept, so turning it off brings the same row back.
		/// </summary>
		public bool SuppressSelection
		{
			get => (bool)GetValue(SuppressSelectionProperty);
			set => SetValue(SuppressSelectionProperty, value);
		}

		public static readonly DependencyProperty SuppressSelectionProperty =
			DependencyProperty.Register(nameof(SuppressSelection), typeof(bool), typeof(TabStrip),
				new PropertyMetadata(false, (d, _) => ((TabStrip)d).ApplySelection()));

		/// <summary>Raised on EVERY row click, the already-selected row included (SelectedIndex only changes on a new one).</summary>
		public event RoutedEventHandler? RowClicked;

		// ── THE WIDEST ROW (its mark + name + margins) — what the Settings search box is sized from ──
		private readonly Dictionary<FrameworkElement, double> _rowWidths = new();

		/// <summary>The widest row's content width (mark + gap + name + its 12 px margins); 0 before the rows lay out.</summary>
		public double WidestRowWidth { get; private set; }

		/// <summary>Raised when <see cref="WidestRowWidth"/> changes (a longer name, or Windows' Text size).</summary>
		public event EventHandler? WidestRowChanged;

		private void OnRowContentSizeChanged(object sender, SizeChangedEventArgs e)
		{
			if (sender is not FrameworkElement content) { return; }
			_rowWidths[content] = e.NewSize.Width + content.Margin.Left + content.Margin.Right;
			var widest = _rowWidths.Values.Max();
			if (widest != WidestRowWidth)
			{
				WidestRowWidth = widest;
				WidestRowChanged?.Invoke(this, EventArgs.Empty);
			}
		}

		/// <summary>The rows' name size: Body 14 (the Atlas); Settings sets Body large (its BodyLargeFontSize, 2026-11-17).
		/// The marks stay 16 (icons aren't on the type scale).</summary>
		public double LabelFontSize
		{
			get => (double)GetValue(LabelFontSizeProperty);
			set => SetValue(LabelFontSizeProperty, value);
		}

		public static readonly DependencyProperty LabelFontSizeProperty =
			DependencyProperty.Register(nameof(LabelFontSize), typeof(double), typeof(TabStrip),
				new PropertyMetadata(14d, (d, _) => ((TabStrip)d).ApplySelection()));

		/// <summary>
		/// The selected row's button box in <paramref name="relativeTo"/>'s coordinates, or null while nothing is selected or
		/// its row isn't built yet.
		/// </summary>
		public Rect? SelectedRowBounds(UIElement relativeTo)
		{
			int position = Entries.ToList().FindIndex(e => e.IsSelected);
			if (position < 0 || Host.ContainerFromIndex(position) is not DependencyObject container) { return null; }
			var button = FindButton(container);
			if (button is null || button.ActualHeight <= 0) { return null; }
			return button.TransformToVisual(relativeTo).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
		}

		private static ToggleButton? FindButton(DependencyObject root)
		{
			for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
			{
				var child = VisualTreeHelper.GetChild(root, i);
				if (child is ToggleButton button) { return button; }
				if (FindButton(child) is ToggleButton found) { return found; }
			}
			return null;
		}

		/// <summary>Bool → Visibility for the item template. A static so a DataTemplate can call it (x:Bind
		/// inside a template resolves against the ENTRY, so an instance method here is out of reach).</summary>
		public static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>The accent bar: the selected row only, and never while it joins the body.</summary>
		public static Visibility ShowBar(bool selected, bool joinsBody) => selected && !joinsBody ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>The row's look (a Style is safe to read in C#; a theme BRUSH would not be).</summary>
		public static Style RowStyle(bool joinsBody) =>
			(Style)Application.Current.Resources[joinsBody ? "NavRailJoinedTabStyle" : "NavRailTabStyle"];

		private IEnumerable<TabEntry> Entries =>
			ItemsSource?.OfType<TabEntry>() ?? Enumerable.Empty<TabEntry>();

		// The page a row selects: its own PageIndex, else its position (TabEntry.PageIndex).
		private static int PageOf(TabEntry entry, int position) => entry.PageIndex >= 0 ? entry.PageIndex : position;

		// Push the current index onto the entries; the item template lights itself from IsSelected.
		private void ApplySelection()
		{
			int i = 0;
			foreach (var entry in Entries)
			{
				entry.JoinsBody = JoinsBody;
				entry.LabelFontSize = LabelFontSize;
				entry.IsSelected = !SuppressSelection && PageOf(entry, i) == SelectedIndex;
				i++;
			}
		}

		private void OnTabClick(object sender, RoutedEventArgs e)
		{
			if (sender is not ToggleButton button || button.DataContext is not TabEntry entry) return;

			int position = Entries.ToList().IndexOf(entry);
			if (position >= 0) SelectedIndex = PageOf(entry, position);
			RowClicked?.Invoke(this, e);

			// ⚠️ RE-ASSERT, do not delete. The click already flipped IsChecked locally, and a OneWay binding
			// only pushes when the SOURCE changes — so clicking the row you are already on would leave it
			// dark with nothing to turn it back on.
			button.IsChecked = entry.IsSelected;
		}
	}
}
