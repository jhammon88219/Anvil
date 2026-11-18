using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.AboutSettingsConsoles
{
	/// <summary>One project on Settings → About → Built with (see the XAML header): its name as a link, what it does at the right.</summary>
	public sealed partial class AboutSettingsConsoleLinkRow : UserControl
	{
		public AboutSettingsConsoleLinkRow()
		{
			InitializeComponent();
		}

		/// <summary>The project's name — the link's text and the leave-Anvil dialog's name for the page.</summary>
		public string Title
		{
			get => (string)GetValue(TitleProperty);
			set => SetValue(TitleProperty, value);
		}

		public static readonly DependencyProperty TitleProperty =
			DependencyProperty.Register(nameof(Title), typeof(string), typeof(AboutSettingsConsoleLinkRow), new PropertyMetadata(""));

		/// <summary>The project's page (where its license lives).</summary>
		public string Url
		{
			get => (string)GetValue(UrlProperty);
			set => SetValue(UrlProperty, value);
		}

		public static readonly DependencyProperty UrlProperty =
			DependencyProperty.Register(nameof(Url), typeof(string), typeof(AboutSettingsConsoleLinkRow), new PropertyMetadata(""));

		/// <summary>What Anvil uses it for, shown at the far right.</summary>
		public string What
		{
			get => (string)GetValue(WhatProperty);
			set => SetValue(WhatProperty, value);
		}

		public static readonly DependencyProperty WhatProperty =
			DependencyProperty.Register(nameof(What), typeof(string), typeof(AboutSettingsConsoleLinkRow), new PropertyMetadata(""));

		private void OnLinkClick(object sender, RoutedEventArgs e) => AboutAnvil.OpenLink(this, Title, Url);
	}
}
