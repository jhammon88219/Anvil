namespace Anvil.Services
{
	/// <summary>
	/// Keeps a secret (the NOAA Weather Wire password) in the OS credential store — Windows Credential Manager via the
	/// App's <c>CredentialStore</c> — never in <c>AppSettings.json</c>. The third platform seam (the user's OK,
	/// 2026-10-19).
	/// </summary>
	/// <remarks>Calls are quick and synchronous; none throws — a store that can't be read reads as "nothing saved".</remarks>
	public interface ICredentialStore
	{
		/// <summary>The password saved for <paramref name="user"/> under <paramref name="resource"/>, or null.</summary>
		string? Read(string resource, string user);

		/// <summary>Saves (or replaces) the password; returns false if the store refused it.</summary>
		bool Write(string resource, string user, string password);

		/// <summary>Forgets every password saved under <paramref name="resource"/>.</summary>
		void Delete(string resource);
	}
}
