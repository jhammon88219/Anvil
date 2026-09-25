using System;
using System.Collections.Generic;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// The PastCast saved-event list: the curated BUILT-IN events bundled into Anvil.Core, merged with the
	/// user's own, which persist to a JSON file under <c>%LocalAppData%\Anvil</c>.
	/// </summary>
	/// <remarks>
	/// ⚠️ Built-in events are READ-ONLY here, not merely in the UI: <see cref="Remove"/> refuses one and
	/// returns false. The list a user can delete from is only ever their own.
	/// </remarks>
	public interface ISavedEventLibrary
	{
		/// <summary>Every event, grouped by kind (Other last) — built-ins and the user's own together — newest
		/// first within each.</summary>
		IReadOnlyList<SavedEvent> GetEvents();

		/// <summary>
		/// Saves a new user event and returns it.
		/// </summary>
		/// <exception cref="ArgumentException">The name is blank, or a leg fails validation (see
		/// <see cref="SavedEventLibrary.Validate"/>) — the message is written for the user.</exception>
		SavedEvent Add(string name, IReadOnlyList<SavedEventLeg> legs, string notes, SavedEventKind kind = SavedEventKind.Other);

		/// <summary>
		/// Sets (or, with null, clears) one leg's key time on a USER event and returns the updated event.
		/// Null when there is no such user event or leg (built-ins are refused, like <see cref="Remove"/>).
		/// </summary>
		/// <exception cref="ArgumentException">The key fails validation — the message is written for the user.</exception>
		SavedEvent? SetLegKey(string id, int legIndex, SavedEventKey? key);

		/// <summary>Re-types a USER event and returns it; null when there is no such user event.</summary>
		SavedEvent? SetKind(string id, SavedEventKind kind);

		/// <summary>Deletes a USER event. False when there is no such event or it is built in.</summary>
		bool Remove(string id);
	}
}
