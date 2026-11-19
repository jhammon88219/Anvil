using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// XAML resource TYPES the compiler never checks (docs/bugs.md S36): a <c>{StaticResource}</c> is resolved only when the page
	/// LOADS, so a resource of the wrong type builds clean and crashes at runtime with a XamlParseException.
	/// </summary>
	public class XamlResourceTypeTests
	{
		[Fact]
		public void Grid_tracks_never_take_a_double_resource()
		{
			// ColumnDefinition.Width / RowDefinition.Height are GridLength — an x:Double key there throws on load.
			// (MinWidth / MaxWidth / MinHeight / MaxHeight ARE doubles, so they're not checked.)
			var files = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "Anvil.App"), "*.xaml", SearchOption.AllDirectories)
				.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
					&& !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
				.ToList();
			var texts = files.ToDictionary(f => f, f => Regex.Replace(File.ReadAllText(f), "<!--.*?-->", "", RegexOptions.Singleline));

			var doubleKeys = new HashSet<string>(StringComparer.Ordinal);
			foreach (var text in texts.Values)
			{
				foreach (Match m in Regex.Matches(text, @"<x:Double\s+x:Key=""([^""]+)""")) { doubleKeys.Add(m.Groups[1].Value); }
			}

			var track = new Regex(@"<(?:ColumnDefinition[^>]*\bWidth|RowDefinition[^>]*\bHeight)=""\{StaticResource\s+([^}\s]+)\s*\}""");
			var wrong = new List<string>();
			foreach (var (file, text) in texts)
			{
				foreach (Match m in track.Matches(text))
				{
					if (doubleKeys.Contains(m.Groups[1].Value)) { wrong.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value} is an x:Double"); }
				}
			}
			Assert.Empty(wrong);
		}

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
