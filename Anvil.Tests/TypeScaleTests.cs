using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The TYPE SCALE (Anvil.App/Controls/Styles.xaml, docs/ui-patterns.md "Type scale"): text never types a bare FontSize —
	/// it names one of the eight roles (a *TextStyle, or {StaticResource *FontSize}). Icons are not text and keep their own
	/// sizes: a FontIcon / SymbolIcon / *IconSource, anything with a Glyph, an icon FontFamily, or a glyph code as its Text.
	/// </summary>
	public class TypeScaleTests
	{
		private static readonly string[] Roles = { "Display", "PageTitle", "Title", "Subtitle", "BodyLarge", "Body", "Caption", "Overline" };

		private static readonly Regex Comment = new(@"<!--.*?-->", RegexOptions.Singleline);
		private static readonly Regex Tag = new(@"<(?![/!?])([\w:.]+)((?:[^>""']|""[^""]*""|'[^']*')*)>", RegexOptions.Singleline);
		private static readonly Regex BareFontSize = new(@"\bFontSize=""\d");
		private static readonly Regex BareSetter = new(@"<Setter Property=""FontSize"" Value=""\d");
		private static readonly Regex StyleHead = new(@"<Style\b[^>]*>");
		private static readonly Regex FontSizeDouble = new(@"<x:Double x:Key=""(\w*FontSize)"">");
		private static readonly Regex IconFamily = new(@"FontFamily=""[^""]*(Icons|MDL2|Symbol|Fluent|Emoji)", RegexOptions.IgnoreCase);
		private static readonly Regex GlyphText = new(@"Text=""[^""]*(&#x[EF][0-9A-Fa-f]{3};|&#x1F)");

		[Fact]
		public void No_text_in_xaml_types_a_bare_font_size()
		{
			var offenders = new List<string>();
			foreach (var file in AppXaml())
			{
				var code = Comment.Replace(File.ReadAllText(file), "");
				var name = Path.GetFileName(file);

				foreach (Match tag in Tag.Matches(code))
				{
					string element = tag.Groups[1].Value, attrs = tag.Groups[2].Value;
					if (BareFontSize.IsMatch(attrs) && !IsIcon(element, attrs))
					{
						offenders.Add($"{name}: <{element} {attrs.Trim()[..Math.Min(80, attrs.Trim().Length)]}");
					}
				}

				foreach (Match setter in BareSetter.Matches(code))
				{
					var head = StyleHead.Matches(code[..setter.Index]).LastOrDefault()?.Value ?? "";
					if (!head.Contains("Icon"))
					{
						offenders.Add($"{name}: {setter.Value}… in {head}");
					}
				}

				foreach (Match d in FontSizeDouble.Matches(code))
				{
					if (!(name == "Styles.xaml" && Roles.Any(r => d.Groups[1].Value == r + "FontSize")))
					{
						offenders.Add($"{name}: <x:Double {d.Groups[1].Value}> — alias a role instead (<StaticResource ResourceKey=\"…FontSize\"/>)");
					}
				}
			}
			Assert.True(offenders.Count == 0,
				"Text must use a type-scale role (Styles.xaml TYPE SCALE), not a bare FontSize:\n" + string.Join("\n", offenders));
		}

		[Fact]
		public void Every_role_is_defined_with_a_size_and_line_height()
		{
			var styles = File.ReadAllText(Path.Combine(RepoRoot(), "Anvil.App", "Controls", "Styles.xaml"));
			foreach (var role in Roles)
			{
				Assert.Matches($@"<x:Double x:Key=""{role}FontSize"">\d+</x:Double>", styles);
				Assert.Matches($@"<x:Double x:Key=""{role}LineHeight"">\d+</x:Double>", styles);
				Assert.Contains($@"x:Key=""{role}TextStyle""", styles);
			}
		}

		private static bool IsIcon(string element, string attrs) =>
			element.EndsWith("Icon", StringComparison.Ordinal) || element.EndsWith("IconSource", StringComparison.Ordinal)
			|| attrs.Contains("Glyph=", StringComparison.Ordinal) || IconFamily.IsMatch(attrs) || GlyphText.IsMatch(attrs);

		private static IEnumerable<string> AppXaml() =>
			Directory.EnumerateFiles(Path.Combine(RepoRoot(), "Anvil.App"), "*.xaml", SearchOption.AllDirectories)
				.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
					&& !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

		private static string RepoRoot()
		{
			for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
			{
				if (File.Exists(Path.Combine(dir.FullName, "Anvil.slnx"))) { return dir.FullName; }
			}
			throw new InvalidOperationException("Anvil.slnx not found above the test output folder");
		}
	}
}
