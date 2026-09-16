using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Models;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The "?" hint button (see the XAML header): one <see cref="RadarGlossaryCard"/> rendered as a rich
	/// tooltip. No click behaviour — hovering or focusing the button IS the interaction.
	/// </summary>
	public sealed partial class GlossaryHint : UserControl
	{
		public GlossaryHint()
		{
			InitializeComponent();
		}

		/// <summary>The explanation to show. Built in Core by <c>RadarGlossary</c>.</summary>
		public RadarGlossaryCard Card
		{
			get => (RadarGlossaryCard)GetValue(CardProperty);
			set => SetValue(CardProperty, value);
		}

		public static readonly DependencyProperty CardProperty =
			DependencyProperty.Register(nameof(Card), typeof(RadarGlossaryCard), typeof(GlossaryHint),
				new PropertyMetadata(null));

		// Screen readers get the term, not a bare "?" — the button has no label of its own.
		public string HintName(RadarGlossaryCard? card) =>
			card is null ? "What is this?" : $"What is this? {card.Term}";

		public Visibility ShowIfText(string? value) =>
			string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
	}
}
