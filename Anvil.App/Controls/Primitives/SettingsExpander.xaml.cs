using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Windows.Foundation;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// One section of a Settings tab — a fixed-height header over a collapsible <see cref="Body"/> (see
	/// SettingsExpander.xaml). Never re-orders; the temporal windows' map layers are
	/// <see cref="DragMoveLayerExpander"/>.
	/// </summary>
	[ContentProperty(Name = nameof(Body))]
	public sealed partial class SettingsExpander : UserControl
	{
		public SettingsExpander()
		{
			InitializeComponent();
			Loaded += (_, _) => VisualStateManager.GoToState(this, "Normal", false);
		}

		// ── Properties ───────────────────────────────────────────────────────────────────────────────

		/// <summary>The section title.</summary>
		public string Header
		{
			get => (string)GetValue(HeaderProperty);
			set => SetValue(HeaderProperty, value);
		}
		public static readonly DependencyProperty HeaderProperty =
			DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingsExpander), new PropertyMetadata(""));

		/// <summary>The section's content. Null = a header-only section (no chevron, no toggling).</summary>
		public object? Body
		{
			get => GetValue(BodyProperty);
			set => SetValue(BodyProperty, value);
		}
		public static readonly DependencyProperty BodyProperty =
			DependencyProperty.Register(nameof(Body), typeof(object), typeof(SettingsExpander), new PropertyMetadata(null));

		/// <summary>Whether the body is showing. Persisted only through <see cref="PersistExpansion"/>.</summary>
		public bool IsExpanded
		{
			get => (bool)GetValue(IsExpandedProperty);
			set => SetValue(IsExpandedProperty, value);
		}
		public static readonly DependencyProperty IsExpandedProperty =
			DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(SettingsExpander), new PropertyMetadata(false));

		// ── x:Bind functions (layout) ────────────────────────────────────────────────────────────────

		// Round only the header's top corners while the body shows beneath it.
		public CornerRadius HeaderCorners(bool expanded, object? body) =>
			expanded && body is not null ? new CornerRadius(3, 3, 0, 0) : new CornerRadius(3);

		public Visibility ChevronVisibility(object? body) => BoolVisibility(body is not null);
		public Visibility BodyVisibility(bool expanded, object? body) => BoolVisibility(expanded && body is not null);
		public Visibility BoolVisibility(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
		public double ChevronAngle(bool expanded) => expanded ? 90 : 0;
		// A TextBlock has no disabled state; the title dims through Opacity (no theme brush in code).
		public double DimUnless(bool enabled) => enabled ? 1.0 : 0.4;
		public string ChevronName(string header, bool expanded) => (expanded ? "Collapse " : "Expand ") + header;

		// LAZY BODY: handed to the presenter only once the section has been EXPANDED, then kept (collapsing just
		// hides it) — see DragMoveLayerExpander for the launch-freeze story behind it.
		private bool _bodyAttached;

		public object? PresentedBody(bool expanded, object? body)
		{
			if (expanded) _bodyAttached = true;
			return _bodyAttached ? body : null;
		}

		// FIRST LAYOUT per section, logged (radar diag "section.measure", ≥ 5 ms) — what each costs when its window opens.
		private bool _measured;

		protected override Size MeasureOverride(Size availableSize)
		{
			if (_measured) return base.MeasureOverride(availableSize);
			_measured = true;
			var clock = System.Diagnostics.Stopwatch.StartNew();
			var size = base.MeasureOverride(availableSize);
			if (clock.ElapsedMilliseconds >= 5)
			{
				Anvil.Services.RadarDiagnostics.Log("ui", "section.measure", ("section", Header),
					("ms", clock.ElapsedMilliseconds), ("expanded", IsExpanded), ("bodyAttached", _bodyAttached));
			}
			return size;
		}

		// ── Header: expand / collapse, hover ─────────────────────────────────────────────────────────

		private void OnChevronClick(object sender, RoutedEventArgs e) => IsExpanded = !IsExpanded;

		private void OnHeaderTapped(object sender, TappedRoutedEventArgs e)
		{
			if (Body is null) { return; }
			// The chevron's own Click already toggled.
			if (e.OriginalSource is DependencyObject src && IsWithin(src, ChevronButton)) { return; }
			IsExpanded = !IsExpanded;
		}

		private static bool IsWithin(DependencyObject src, DependencyObject container)
		{
			for (var d = src; d is not null; d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d))
			{
				if (ReferenceEquals(d, container)) { return true; }
			}
			return false;
		}

		private void OnHeaderPointerEntered(object sender, PointerRoutedEventArgs e) =>
			VisualStateManager.GoToState(this, "PointerOver", false);

		private void OnHeaderPointerExited(object sender, PointerRoutedEventArgs e) =>
			VisualStateManager.GoToState(this, "Normal", false);

		// ── Host helpers ─────────────────────────────────────────────────────────────────────────────

		/// <summary>
		/// Restores each section's saved open/closed state, then saves every later toggle. Call ONCE per host.
		/// The key is "window/" + the lower-cased Header. ⚠️ Renaming a Header forgets its state.
		/// </summary>
		public static void PersistExpansion(IEnumerable<SettingsExpander> sections, string window,
			Func<string, bool?> get, Action<string, bool> set)
		{
			foreach (var section in sections)
			{
				var key = window + "/" + section.Header.ToLowerInvariant();
				if (get(key) is bool open) { section.IsExpanded = open; }
				section.RegisterPropertyChangedCallback(IsExpandedProperty,
					(d, _) => set(key, ((SettingsExpander)d).IsExpanded));
			}
		}
	}
}
