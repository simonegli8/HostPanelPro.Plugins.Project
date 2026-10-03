using LibArchive.Net;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HostPanelPro.Plugins;

public class Zip
{
	public static CancellationToken Cancel => PluginManager.Cancel.Token;
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

			using (var reader = stream != null
				? new LibArchiveReader(stream, password: null, blockSize: 10240)
				: new LibArchiveReader(zipFile, blockSize: 10240, password: null))
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
