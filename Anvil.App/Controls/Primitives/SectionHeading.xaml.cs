using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The heading that opens a section inside a panel — a title followed by a hairline to the panel's edge
	/// (see the XAML header). It replaced <c>SettingsSectionLabelStyle</c>, which could only make the words
	/// louder; the rule is a sibling element, and a TextBlock style cannot add one.
	/// </summary>
	public sealed partial class SectionHeading : UserControl
	{
		public SectionHeading()
		{
			InitializeComponent();
		}

		/// <summary>The section's title, in sentence case, as it should appear.</summary>
		public string Text
		{
			get => (string)GetValue(TextProperty);
			set => SetValue(TextProperty, value);
		}

		public static readonly DependencyProperty TextProperty =
			DependencyProperty.Register(nameof(Text), typeof(string), typeof(SectionHeading),
				new PropertyMetadata(string.Empty, (d, e) => ((SectionHeading)d).Title.Text = (string)e.NewValue));
	}
}
