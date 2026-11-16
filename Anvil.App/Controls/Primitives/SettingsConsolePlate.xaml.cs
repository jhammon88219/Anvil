using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The casing of one Settings section (see the XAML header): a titled card that never collapses. The section's
	/// rows are its <see cref="Body"/> (the content property); <see cref="HeaderContent"/> (a "Restore defaults") starts on the
	/// rows' control column; <see cref="ShowDefaultHeader"/> puts the DEFAULT column title on the same row, far right.
	/// </summary>
	[ContentProperty(Name = nameof(Body))]
	public sealed partial class SettingsConsolePlate : UserControl
	{
		public SettingsConsolePlate()
		{
			InitializeComponent();
		}

		/// <summary>The section's name, drawn large.</summary>
		public string Title
		{
			get => (string)GetValue(TitleProperty);
			set => SetValue(TitleProperty, value);
		}

		public static readonly DependencyProperty TitleProperty =
			DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingsConsolePlate), new PropertyMetadata(""));

		/// <summary>On the title row at the rows' control column, e.g. the section's Restore defaults. Null = nothing.</summary>
		public object? HeaderContent
		{
			get => GetValue(HeaderContentProperty);
			set => SetValue(HeaderContentProperty, value);
		}

		public static readonly DependencyProperty HeaderContentProperty =
			DependencyProperty.Register(nameof(HeaderContent), typeof(object), typeof(SettingsConsolePlate), new PropertyMetadata(null));

		/// <summary>Whether the rows carry a right-hand DEFAULT column, titled once here.</summary>
		public bool ShowDefaultHeader
		{
			get => (bool)GetValue(ShowDefaultHeaderProperty);
			set => SetValue(ShowDefaultHeaderProperty, value);
		}

		public static readonly DependencyProperty ShowDefaultHeaderProperty =
			DependencyProperty.Register(nameof(ShowDefaultHeader), typeof(bool), typeof(SettingsConsolePlate), new PropertyMetadata(false));

		/// <summary>The section's rows.</summary>
		public object? Body
		{
			get => GetValue(BodyProperty);
			set => SetValue(BodyProperty, value);
		}

		public static readonly DependencyProperty BodyProperty =
			DependencyProperty.Register(nameof(Body), typeof(object), typeof(SettingsConsolePlate), new PropertyMetadata(null));

		public Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
	}
}
