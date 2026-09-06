using System.Collections.Generic;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Supplies the visual identities the app can wear, and resolves a persisted id back to one.
	/// </summary>
	public interface IThemeProvider
	{
		/// <summary>Every theme this build ships, in the order a picker should offer them.</summary>
		IReadOnlyList<AppTheme> GetThemes();

		/// <summary>The theme used when nothing has been chosen, or when a stored choice can't be honored.</summary>
		AppTheme Default { get; }

		/// <summary>
		/// The theme with this id, or <see cref="Default"/> when <paramref name="id"/> is empty or names a
		/// theme this build doesn't have.
		/// </summary>
		/// <remarks>
		/// ⚠️ The fallback lives HERE so no caller reinvents it. A settings file written by a build that had
		/// more themes (or differently-named ones) must land on a real theme rather than leaving the app
		/// with none — the same reason <c>SettingsTabPlacement</c> persists as a string.
		/// </remarks>
		AppTheme Resolve(string? id);
	}
}
