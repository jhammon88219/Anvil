using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;
// ⚠️ Anvil.Controls.Windows SHADOWS WinRT's Windows.* root, so a leading "Windows." inline binds
// to this namespace and fails to resolve. A using directive sits OUTSIDE the namespace, where
// Windows still means the global one — so import these and use the types BARE. Documented trap;
// it bit RadarControls.AgeBrush the moment the folder was created.
using Windows.UI;
using Windows.System;

namespace Anvil.Controls.Windows
{
	/// <summary>
	/// DEV TOOL. The style editor's content (see the XAML header for the shape and the rules): one row per
	/// colour SLOT in the loaded basemap, edited live.
	/// </summary>
	/// <remarks>
	/// ⚠️ Debug-only in practice — nothing opens it in Release, because its switch lives on the Dev tab,
	/// which is neither listed nor constructed there. The window registration itself is not
	/// <c>#if DEBUG</c>'d, exactly like the Pipeline Console's.
	/// ⚠️ It edits through <see cref="MapStyleTuningViewModel"/>, which owns the override table; this class
	/// holds only the FILTERED view of the rows, which is view state.
	/// </remarks>
	public sealed partial class StyleEditorWindow : UserControl
	{
		public StyleEditorWindow()
		{
			InitializeComponent();
			Loaded += OnLoaded;
		}

		/// <summary>The coordinator view model; bound from the host (supplies the pin flag).</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(StyleEditorWindow), new PropertyMetadata(null));

		/// <summary>The style editor engine. Null in Release, where this window is never opened.</summary>
		public MapStyleTuningViewModel? TuneVm
		{
			get => (MapStyleTuningViewModel?)GetValue(TuneVmProperty);
			set => SetValue(TuneVmProperty, value);
		}

		public static readonly DependencyProperty TuneVmProperty =
			DependencyProperty.Register(nameof(TuneVm), typeof(MapStyleTuningViewModel), typeof(StyleEditorWindow), new PropertyMetadata(null));

		/// <summary>
		/// The rows actually shown — the engine's slots, narrowed by the search box. ⚠️ A SEPARATE
		/// collection, not a filter on the engine's: the filter is view state and the engine's list is the
		/// document-ordered truth the export depends on.
		/// </summary>
		public ObservableCollection<StyleSlotRow> Rows { get; } = new();

		/// <summary>"86 slots · 3 overridden", or the loading state before the page has answered.</summary>
		public string CountText => TuneVm is null
			? ""
			: TuneVm.Slots.Count == 0
				? "Reading the basemap's colours…"
				: $"{TuneVm.Slots.Count} slots · {TuneVm.OverrideCount} overridden";

		private async void OnLoaded(object sender, RoutedEventArgs e)
		{
			if (TuneVm is null) return;

			// ⚠️ Populating is START-THEN-POLL inside the engine (the page has to fetch the style file), so
			// this await can take a moment on first open. The count readout says so meanwhile.
			await TuneVm.LoadSlotsAsync();
			Rebuild();
		}

		private void Rebuild()
		{
			Rows.Clear();
			if (TuneVm is null) return;

			var query = SearchBox.Text?.Trim();
			var matches = string.IsNullOrEmpty(query)
				? TuneVm.Slots
				: TuneVm.Slots.Where(r =>
					r.LayerId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
					r.Property.Contains(query, StringComparison.OrdinalIgnoreCase));

			foreach (var row in matches) Rows.Add(row);
			Bindings.Update();
		}

		private void OnSearchChanged(object sender, TextChangedEventArgs e) => Rebuild();

		private void OnClearOverridesClick(object sender, RoutedEventArgs e)
		{
			TuneVm?.ClearOverrides();
			Bindings.Update();
		}

		// Commit on Enter as well as on focus loss: typing a hex and tabbing away is the common path, but
		// pressing Enter and watching the map is the one you actually want while iterating.
		private void OnSlotColorKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
		{
			if (e.Key == VirtualKey.Enter) Commit(sender);
		}

		private void OnSlotColorCommitted(object sender, RoutedEventArgs e) => Commit(sender);

		private void Commit(object sender)
		{
			if (TuneVm is null || sender is not TextBox box || box.Tag is not string key) return;

			var row = TuneVm.Slots.FirstOrDefault(r => r.Key == key);
			if (row is null) return;

			var text = box.Text?.Trim();

			// Empty CLEARS the override rather than setting black — the box is the slot's colour, and an
			// emptied box means "no opinion, follow the transform".
			if (string.IsNullOrEmpty(text))
			{
				TuneVm.SetOverride(row, null);
				box.Text = row.EffectiveColor;
				Bindings.Update();
				return;
			}

			if (!text.StartsWith('#')) text = "#" + text;

			// ⚠️ Reject rather than guess. A half-typed hex pushed to the page would repaint the map with a
			// colour nobody asked for, and the box would then disagree with the map about what it says.
			if (!TryParse(text, out _))
			{
				box.Text = row.EffectiveColor;
				return;
			}

			TuneVm.SetOverride(row, text.ToLowerInvariant());
			Bindings.Update();
		}

		private static bool TryParse(string? hex, out Color color)
		{
			color = Colors.Transparent;
			if (string.IsNullOrWhiteSpace(hex)) return false;

			var body = hex.TrimStart('#');
			if (body.Length != 6 ||
				!int.TryParse(body, System.Globalization.NumberStyles.HexNumber,
					System.Globalization.CultureInfo.InvariantCulture, out var rgb))
			{
				return false;
			}

			color = ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
			return true;
		}

		// ── The shared colour picker ─────────────────────────────────────────────────────────────────
		// ⚠️ ONE ColorPicker for every row (see the XAML). These two fields are what make that safe: the ROW
		// it is currently editing, and a guard for the fact that setting Picker.Color programmatically
		// raises ColorChanged just as a user drag does.

		private StyleSlotRow? _pickerRow;
		private Flyout? _pickerFlyout;
		private ColorPicker? _picker;

		// ⚠️ BUILT IN CODE, NOT DECLARED IN UserControl.Resources. A named element inside a ResourceDictionary
		// is only instantiated when something REQUESTS that resource — and nothing does here, because the
		// flyout is shown from code rather than referenced by {StaticResource}. Its generated field would
		// then still be null on the first click. Creating it explicitly, once, removes the question.
		private Flyout EnsurePicker()
		{
			if (_pickerFlyout is not null) return _pickerFlyout;

			_picker = new ColorPicker
			{
				// The bundled styles are #rrggbb — there is no alpha channel to edit, and offering one
				// would let you pick a colour the export cannot represent.
				IsAlphaEnabled = false,
				IsAlphaSliderVisible = false,
				IsAlphaTextInputVisible = false,
				IsHexInputVisible = true,
				IsColorChannelTextInputVisible = true,
				IsMoreButtonVisible = false,
			};
			_picker.ColorChanged += OnPickerColorChanged;

			_pickerFlyout = new Flyout
			{
				Content = _picker,
				Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Right,
			};
			return _pickerFlyout;
		}

		// ⚠️ WITHOUT THIS, OPENING THE PICKER WOULD CREATE AN OVERRIDE. Seeding Picker.Color to the row's
		// current colour fires ColorChanged, which would write that colour back as an explicit override —
		// so merely LOOKING at a slot would mark it edited, and Clear overrides would have work to undo that
		// the user never asked for.
		private bool _seedingPicker;

		private void OnSwatchClick(object sender, RoutedEventArgs e)
		{
			if (TuneVm is null || sender is not Button button || button.Tag is not string key) return;

			var row = TuneVm.Slots.FirstOrDefault(r => r.Key == key);
			if (row is null) return;

			_pickerRow = row;

			var flyout = EnsurePicker();

			_seedingPicker = true;
			try
			{
				if (_picker is not null && TryParse(row.EffectiveColor, out var color)) _picker.Color = color;
			}
			finally
			{
				_seedingPicker = false;
			}

			// Shown AT the button rather than attached to it, because the flyout is shared: one instance
			// moved to whichever swatch was clicked.
			flyout.ShowAt(button);
		}

		// Live: every drag on the spectrum pushes straight through, so the MAP is the preview. The engine
		// debounces the actual paint, so this can fire as often as it likes.
		private void OnPickerColorChanged(ColorPicker sender, ColorChangedEventArgs args)
		{
			if (_seedingPicker || TuneVm is null || _pickerRow is null) return;

			var c = args.NewColor;
			TuneVm.SetOverride(_pickerRow, $"#{c.R:x2}{c.G:x2}{c.B:x2}");
			Bindings.Update();
		}

		/// <summary>Raised by the Export button; MainWindow owns the save picker (a UserControl has no HWND).</summary>
		public event EventHandler? ExportRequested;

		private void OnExportClick(object sender, RoutedEventArgs e) => ExportRequested?.Invoke(this, EventArgs.Empty);
	}
}
