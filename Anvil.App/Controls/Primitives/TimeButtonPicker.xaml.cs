using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// A time of day behind one button (see the XAML header). Host contract: <see cref="Time"/> two-way. The
	/// flyout edits a DRAFT; only Set writes <see cref="Time"/>.
	/// </summary>
	public sealed partial class TimeButtonPicker : UserControl
	{
		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		// Index 0 is 12 — the clock face's order, and how a 12-hour list reads.
		private static readonly int[] Hours = { 12, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

		private readonly List<int> _minutes = new();

		public TimeButtonPicker()
		{
			InitializeComponent();
			foreach (var h in Hours) { HourBox.Items.Add(h.ToString(Inv)); }
			AmPm.ItemsSource = new[] { "AM", "PM" };
			UpdateFace();
		}

		/// <summary>The committed time of day. Written ONLY by Set; a write from the host refreshes the face.</summary>
		public TimeSpan Time
		{
			get => (TimeSpan)GetValue(TimeProperty);
			set => SetValue(TimeProperty, value);
		}

		public static readonly DependencyProperty TimeProperty =
			DependencyProperty.Register(nameof(Time), typeof(TimeSpan), typeof(TimeButtonPicker),
				new PropertyMetadata(TimeSpan.Zero, (d, _) => ((TimeButtonPicker)d).UpdateFace()));

		private void UpdateFace() =>
			FaceText.Text = DateTime.Today.Add(Time).ToString("h:mm tt", Inv);

		// Every open starts from the COMMITTED value: a dismissed draft is gone.
		private void OnFlyoutOpening(object? sender, object e)
		{
			var hour = Time.Hours;
			var minute = Time.Minutes;

			// 00…55 by 5, plus the committed minute when it is off the 5s, so opening never moves it.
			_minutes.Clear();
			for (var m = 0; m < 60; m += 5) { _minutes.Add(m); }
			if (minute % 5 != 0) { _minutes.Add(minute); _minutes.Sort(); }
			MinuteBox.Items.Clear();
			foreach (var m in _minutes) { MinuteBox.Items.Add(m.ToString("00", Inv)); }

			HourBox.SelectedIndex = hour % 12;          // 0 and 12 → index 0 ("12")
			MinuteBox.SelectedIndex = _minutes.IndexOf(minute);
			AmPm.SelectedIndex = hour >= 12 ? 1 : 0;
		}

		private void OnSetClick(object sender, RoutedEventArgs e)
		{
			var h12 = Hours[Math.Max(HourBox.SelectedIndex, 0)];
			var minute = _minutes[Math.Max(MinuteBox.SelectedIndex, 0)];
			var h24 = h12 % 12 + (AmPm.SelectedIndex == 1 ? 12 : 0);
			Time = new TimeSpan(h24, minute, 0);
			PickerFlyout.Hide();
		}
	}
}
