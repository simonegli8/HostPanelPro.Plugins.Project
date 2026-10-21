using LibArchive.Net;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HostPanelPro.Plugins;

public class Zip
{
	public static CancellationToken Cancel => PluginManager.Cancel.Token;

	static Zip()
	{
		PreloadNativeLibrary();
	}

	[System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
	static extern IntPtr LoadLibrary(string fileName);
    private static bool IsCore => !(IsNetFX || IsNetNative);
    [SupportedOSPlatformGuard("windows")]
    private static bool IsNetFX => RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.OrdinalIgnoreCase);
    [SupportedOSPlatformGuard("windows")]
    private static bool IsNetNative => RuntimeInformation.FrameworkDescription.StartsWith(".NET Native", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// .NET Framework does not probe runtimes/{rid}/native, so load archive.dll by full path;
    /// a module loaded this way satisfies the later DllImport("archive") of LibArchive.Net.
    /// </summary>
    static void PreloadNativeLibrary()
	{
		try
		{
			if (IsCore || Path.DirectorySeparatorChar != '\\') return;

			var rid = "win-" + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
			var dirs = new List<string>();
			try { dirs.Add(Path.GetDirectoryName(new Uri(typeof(Zip).Assembly.CodeBase).LocalPath)); } catch { }
			dirs.Add(AppDomain.CurrentDomain.BaseDirectory);
			dirs.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin"));

			foreach (var dir in dirs.Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase))
			{
				var path = Path.Combine(dir, "runtimes", rid, "native", "archive.dll");
				if (File.Exists(path) && LoadLibrary(path) != IntPtr.Zero) return;
			}
		}
		catch { }
	}
	public static void UnzipFile(string zipFile, string destFolder, Func<string, bool> filter = null, Stream stream = null,
		Action<long, long> progress = null)
	{
		if (zipFile.EndsWith(".7z")) Unzip7zFile(zipFile, destFolder, filter, stream, progress);
		else UnzipZipFile(zipFile, destFolder, filter, stream, progress);
	}
	public static void Unzip7zFile(string zipFile, string destFolder, Func<string, bool> filter = null, Stream stream = null,
		Action<long, long> progress = null)
	{
		try
        {
			var cancel = Cancel;
            if (!Directory.Exists(destFolder)) Directory.CreateDirectory(destFolder);

            if (filter == null) filter = name => true;

			//Log.WriteStart("Unzipping file");
			//Log.WriteInfo(string.Format("Unzipping file \"{0}\" to the folder \"{1}\"", zipFile, destFolder));

			// LibArchive.Net's stream reader has no seek callback, but 7z must seek (header at the end),
			// so streams go through SeekableArchiveReader, which unpacks while the download is running.
			if (stream != null)
			{
				Unzip7zStream(stream, destFolder, filter, progress, cancel);
				return;
			}

			using (var reader = new LibArchiveReader(zipFile, blockSize: 10240, password: null))
			{
				// 7z stores all entry sizes in one header block, so a metadata-only pass (never touching
				// Entry.Stream) is cheap; Reset() then rewinds for the real extraction pass below.
				long zipSize = 0;
				foreach (var entry in reader.Entries())
				{
					if (!entry.IsDirectory && filter(entry.Name)) zipSize += entry.LengthBytes ?? 0;
				}
				reader.Reset();

				long unzipped = 0;

				foreach (var entry in reader.Entries())
				{
					cancel.ThrowIfCancellationRequested();

					if (filter(entry.Name))
					{
						try
						{
							var destPath = Path.Combine(destFolder, entry.Name.Replace('/', Path.DirectorySeparatorChar));
							if (entry.IsDirectory) Directory.CreateDirectory(destPath);
							else
							{
								Directory.CreateDirectory(Path.GetDirectoryName(destPath));
								using var dest = new FileStream(destPath, System.IO.FileMode.Create, FileAccess.Write);
								entry.Stream.CopyTo(dest);
							}
                        }
                        catch
						{
                            if (cancel.IsCancellationRequested) break;

							throw;
						}

						unzipped += entry.LengthBytes ?? 0;

						if (zipSize != 0 && entry.LengthBytes > 0)
						{
							progress?.Invoke(unzipped, zipSize);
						}
                    }
                }

				progress?.Invoke(zipSize, zipSize);
			}
		}
		catch (Exception ex)
		{
			if (ex is ThreadAbortException)
				return;

			throw;
		}
	}

	static void Unzip7zStream(Stream stream, string destFolder, Func<string, bool> filter, Action<long, long> progress,
		CancellationToken cancel)
	{
		using var reader = new SeekableArchiveReader(stream);
		var total = stream.Length;
		var buffer = new byte[81920];
		while (reader.NextEntry(out var name, out var isDirectory))
		{
			cancel.ThrowIfCancellationRequested();
			if (!filter(name)) continue;

			var destPath = Path.Combine(destFolder, name.Replace('/', Path.DirectorySeparatorChar));
			if (isDirectory) Directory.CreateDirectory(destPath);
			else
			{
				Directory.CreateDirectory(Path.GetDirectoryName(destPath));
				using var dest = new FileStream(destPath, System.IO.FileMode.Create, FileAccess.Write);
				int read;
				while ((read = reader.ReadData(buffer)) > 0)
				{
					cancel.ThrowIfCancellationRequested();
					dest.Write(buffer, 0, read);
				}
			}

			// no cheap pre-pass here (it would decode a solid archive twice), so progress is the position in the archive
			progress?.Invoke(Math.Min(stream.Position, total), total);
		}
		progress?.Invoke(total, total);
	}

	/// <summary>
	/// Minimal libarchive reader over a seekable Stream. LibArchive.Net (0.3.1) only registers a read
	/// callback for streams, which makes 7z fail with "does not support seeking a device".
	/// </summary>
	sealed class SeekableArchiveReader : IDisposable
	{
		const string Lib = "archive";
		const int Eof = 1, Failed = -25;
		const int FileTypeMask = 0xF000, FileTypeDir = 0x4000;
		const int BlockSize = 1 << 20;

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		delegate IntPtr ReadCallback(IntPtr archive, IntPtr clientData, ref IntPtr buffer);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		delegate long SeekCallback(IntPtr archive, IntPtr clientData, long offset, int whence);

		[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr archive_read_new();
		[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int archive_read_support_filter_all(IntPtr a);
		[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int archive_read_support_format_all(IntPtr a);
		[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int archive_read_set_read_callback(IntPtr a, ReadCallback cb);
		[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int archive_read_set_seek_callback(IntPtr a, SeekCallback cb);
		[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int archive_read_set_callback_data(IntPtr a, IntPtr data);
		[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int archive_read_open1(IntPtr a);
		[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int archive_read_next_header(IntPtr a, out IntPtr entry);
		[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr archive_read_data(IntPtr a, byte[] buffer, IntPtr size);
		[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int archive_read_free(IntPtr a);
		[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr archive_error_string(IntPtr a);
		[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr archive_entry_pathname(IntPtr entry);
		[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int archive_entry_filetype(IntPtr entry);

		readonly Stream stream;
		readonly byte[] readBuffer = new byte[BlockSize];
		readonly IntPtr nativeBuffer = Marshal.AllocHGlobal(BlockSize);
		// keep the delegates rooted for as long as libarchive may call them
		readonly ReadCallback readCallback;
		readonly SeekCallback seekCallback;
		IntPtr archive;
		Exception callbackError;
		bool freed;

		public SeekableArchiveReader(Stream stream)
		{
			this.stream = stream;
			readCallback = OnRead;
			seekCallback = OnSeek;
			archive = archive_read_new();
			if (archive == IntPtr.Zero) throw new InvalidOperationException("Could not create libarchive reader");
			try
			{
				archive_read_support_filter_all(archive);
				archive_read_support_format_all(archive);
				Check(archive_read_set_read_callback(archive, readCallback));
				Check(archive_read_set_seek_callback(archive, seekCallback));
				Check(archive_read_set_callback_data(archive, IntPtr.Zero)); // allocates the client dataset open1 relies on
				Check(archive_read_open1(archive));
			}
			catch
			{
				Dispose();
				throw;
			}
		}

		IntPtr OnRead(IntPtr a, IntPtr clientData, ref IntPtr buffer)
		{
			try
			{
				var read = stream.Read(readBuffer, 0, readBuffer.Length);
				if (read > 0) Marshal.Copy(readBuffer, 0, nativeBuffer, read);
				buffer = nativeBuffer;
				return (IntPtr)read;
			}
			catch (Exception ex)
			{
				callbackError = ex;
				return (IntPtr)(-1);
			}
		}

		long OnSeek(IntPtr a, IntPtr clientData, long offset, int whence)
		{
			try
			{
				// whence 0/1/2 = SEEK_SET/SEEK_CUR/SEEK_END, same values as SeekOrigin
				return stream.Seek(offset, (SeekOrigin)whence);
			}
			catch (Exception ex)
			{
				callbackError = ex;
				return -1;
			}
		}

		void Check(int result)
		{
			if (result > Failed) return; // OK, EOF and warnings
			if (callbackError != null) throw new IOException("Error reading archive stream", callbackError);
			throw new InvalidDataException(ErrorString());
		}

		string ErrorString()
		{
			var ptr = archive_error_string(archive);
			if (ptr == IntPtr.Zero) return "libarchive error";
			return Utf8(ptr);
		}

		static string Utf8(IntPtr ptr)
		{
			int len = 0;
			while (Marshal.ReadByte(ptr, len) != 0) len++;
			var bytes = new byte[len];
			Marshal.Copy(ptr, bytes, 0, len);
			return Encoding.UTF8.GetString(bytes);
		}

		public bool NextEntry(out string name, out bool isDirectory)
		{
			var result = archive_read_next_header(archive, out var entry);
			if (result == Eof)
			{
				name = null;
				isDirectory = false;
				return false;
			}
			Check(result);
			name = Utf8(archive_entry_pathname(entry));
			isDirectory = (archive_entry_filetype(entry) & FileTypeMask) == FileTypeDir;
			return true;
		}

		public int ReadData(byte[] buffer)
		{
			var read = (long)archive_read_data(archive, buffer, (IntPtr)buffer.Length);
			if (read < 0) Check(Failed);
			return (int)read;
		}

		public void Dispose()
		{
			if (archive != IntPtr.Zero)
			{
				archive_read_free(archive);
				archive = IntPtr.Zero;
			}
			if (!freed)
			{
				freed = true;
				Marshal.FreeHGlobal(nativeBuffer);
			}
		}
	}
	public static void UnzipZipFile(string zipFile, string destFolder, Func<string, bool> filter = null, Stream stream = null,
		Action<long, long> progress = null)
	{
        var cancel = Cancel;
        
		try
        {
			if (!Directory.Exists(destFolder)) Directory.CreateDirectory(destFolder);

			if (filter == null) filter = name => true;

			//Log.WriteStart("Unzipping file");
			//Log.WriteInfo(string.Format("Unzipping file \"{0}\" to the folder \"{1}\"", zipFile, destFolder));

			using (var file = stream ?? new FileStream(zipFile, System.IO.FileMode.Open, FileAccess.Read))
			using (var zip = new ZipArchive(file))
			{
				long zipSize = file.Length;
				long unzipped = 0;

				int files = 0;

                foreach (var entry in zip.Entries)
                {
                    cancel.ThrowIfCancellationRequested();

                    if (filter(entry.FullName))
                    {
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(Path.Combine(destFolder, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                        }
                        else
                        {
                            var filename = Path.Combine(destFolder, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                            var dir = Path.GetDirectoryName(filename);
                            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                            entry.ExtractToFile(filename, true);
                            files++;
                        }
                    }
                    else if (!string.IsNullOrEmpty(entry.Name)) files++;

                    unzipped += entry.CompressedLength;

                    if (zipSize != 0) progress?.Invoke(unzipped, zipSize);
                }

				progress?.Invoke(zipSize, zipSize);
			}
		}
		catch (Exception ex)
		{
			if (ex is ThreadAbortException)
				return;
            
			if (cancel.IsCancellationRequested) return;
            
			throw;
		}
	}

    public static void Zip7zFiles(string zip, string root, IEnumerable<string> files  = null, Action<int, int> progress = null)
    {
        var cancel = Cancel;

        Directory.CreateDirectory(Path.GetDirectoryName(zip));

        if (root.Length > 0 && root[root.Length - 1] != Path.DirectorySeparatorChar) root = root + Path.DirectorySeparatorChar;

        int count = 0;
        if (files == null) files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories);
        else if (progress != null)
        {
            var list = files.ToList();
            count = list.Count;
            files = list;
        }
        // CompressionType.None here means "don't add an outer filter" -- libarchive's own 7z writer
        // still compresses internally (and solidly, across all entries); adding e.g. CompressionType.Lzma
        // on top wraps the whole 7z container in a second, non-standard raw LZMA stream instead.
        using (var writer = new LibArchiveWriter(zip, ArchiveFormat.SevenZip, CompressionType.None,
            compressionLevel: 9, blockSize: 10240, password: null, encryption: EncryptionType.None))
        {
            int n = 1;
            foreach (var file in files)
            {
                cancel.ThrowIfCancellationRequested();

                var entry = file;
                if (entry.StartsWith(root)) entry = entry.Substring(root.Length);
                entry = entry.Replace(Path.DirectorySeparatorChar, '/');
                writer.AddFile(file, entry);
                if (progress != null) progress(n++, count);
            }
        }
    }
}
