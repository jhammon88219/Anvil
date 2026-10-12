using System;
using System.IO;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="FileVersionMemo"/> parses a file once per VERSION (perf audit issue 5): the outlook legend re-reads the
	/// same cached product on every refresh. It must never serve an answer for a file that has since changed.
	/// </summary>
	public sealed class FileVersionMemoTests : IDisposable
	{
		private readonly string _path = Path.Combine(Path.GetTempPath(), "anvil-memo-" + Guid.NewGuid().ToString("N") + ".txt");

		public void Dispose()
		{
			try { File.Delete(_path); } catch { /* best effort */ }
		}

		[Fact]
		public void SameFile_IsReadOnce()
		{
			File.WriteAllText(_path, "one");
			var memo = new FileVersionMemo();
			var reads = 0;

			var a = memo.Get("k", _path, () => { reads++; return File.ReadAllText(_path); });
			var b = memo.Get("k", _path, () => { reads++; return File.ReadAllText(_path); });

			Assert.Equal("one", a);
			Assert.Equal("one", b);
			Assert.Equal(1, reads);
		}

		[Fact]
		public void ReplacedFile_IsReadAgain()
		{
			File.WriteAllText(_path, "one");
			var memo = new FileVersionMemo();
			memo.Get("k", _path, () => File.ReadAllText(_path));

			// A new version, the way the service writes one: a temp file moved over it, with a later stamp.
			var temp = _path + ".tmp";
			File.WriteAllText(temp, "two!");
			File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(_path).AddSeconds(5));
			File.Move(temp, _path, overwrite: true);

			Assert.Equal("two!", memo.Get("k", _path, () => File.ReadAllText(_path)));
		}

		[Fact]
		public void Kinds_AreRememberedSeparately()
		{
			File.WriteAllText(_path, "one");
			var memo = new FileVersionMemo();
			memo.Get("times", _path, () => "times-answer");

			Assert.Equal("peak-answer", memo.Get("peak", _path, () => "peak-answer"));
		}

		[Fact]
		public void MissingFile_IsNeverRemembered()
		{
			var memo = new FileVersionMemo();
			var reads = 0;

			memo.Get("k", _path, () => { reads++; return "none"; });
			memo.Get("k", _path, () => { reads++; return "none"; });

			Assert.Equal(2, reads);
		}
	}
}
