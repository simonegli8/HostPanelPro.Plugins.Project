using SharpCompress;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Common.Options;
using SharpCompress.Factories;
using SharpCompress.Readers;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
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
	public static async Task UnzipFile(string zipFile, string destFolder, Func<string, bool> filter = null, Stream stream = null,
		Func<long, long, Task> progress = null)
	{
		if (zipFile.EndsWith(".7z")) await Unzip7zFile(zipFile, destFolder, filter, stream, progress);
		else await UnzipZipFile(zipFile, destFolder, filter, stream, progress);
	}
	public static async Task Unzip7zFile(string zipFile, string destFolder, Func<string, bool> filter = null, Stream stream = null,
		Func<long, long, Task> progress = null)
	{
		try
        {
			var cancel = Cancel;
            if (!Directory.Exists(destFolder)) Directory.CreateDirectory(destFolder);

            if (filter == null) filter = name => true;

			//Log.WriteStart("Unzipping file");
			//Log.WriteInfo(string.Format("Unzipping file \"{0}\" to the folder \"{1}\"", zipFile, destFolder));

			using (var file = stream ?? new FileStream(zipFile, System.IO.FileMode.Open, FileAccess.Read))
			using (var zip = SevenZipArchive.OpenArchive(file))
			{
				long zipSize = zip.TotalUncompressedSize;
				long unzipped = 0;

				int files = 0;

				var reader = zip.ExtractAllEntries();
				while (reader.MoveToNextEntry())
				{
					cancel.ThrowIfCancellationRequested();

					if (filter(reader.Entry.Key))
					{
						try
						{
							if (reader.Entry.IsDirectory) Directory.CreateDirectory(Path.Combine(destFolder, reader.Entry.Key.Replace('/', Path.DirectorySeparatorChar)));
							else reader.WriteEntryToDirectory(destFolder, new ExtractionOptions(true, true));
                        }
                        catch
						{
                            if (cancel.IsCancellationRequested) break;
                            
							throw;
						}

						files++;
						unzipped += reader.Entry.Size;

						if (zipSize != 0 && reader.Entry.Size > 0)
						{
							await (progress?.Invoke(unzipped, zipSize) ?? Task.CompletedTask);
						}

                        await Task.Yield();
                    }
                }

				//Installer.Current.Files = files;

				await (progress?.Invoke(zipSize, zipSize) ?? Task.CompletedTask);
				//Log.WriteEnd("Unzipped file");
			}
		}
		catch (Exception ex)
		{
			if (ex is ThreadAbortException)
				return;

			throw;
		}
	}
	public static async Task UnzipZipFile(string zipFile, string destFolder, Func<string, bool> filter = null, Stream stream = null,
		Func<long, long, Task> progress = null)
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

					if (zipSize != 0) await (progress?.Invoke(unzipped, zipSize) ?? Task.CompletedTask);
				}

				//Installer.Current.Files = files;

				await (progress?.Invoke(zipSize, zipSize) ?? Task.CompletedTask);

				//Log.WriteEnd("Unzipped file");
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

	public static async Task Zip7zFiles(string zip, string root, params IEnumerable<string> files)
	{
        var cancel = Cancel;

		Directory.CreateDirectory(Path.GetDirectoryName(zip));

		using (var stream = new FileStream(zip, FileMode.Create, FileAccess.Write))
		using (var writer = WriterFactory.OpenWriter(stream, ArchiveType.SevenZip,
			new SevenZipWriterOptions(CompressionType.LZMA2) { CompressionLevel = 9, LeaveStreamOpen = true }))
		{

			var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (root.Length > 0 && root[root.Length - 1] != Path.DirectorySeparatorChar) root = root + Path.DirectorySeparatorChar;

			if (!files.Any()) files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories);

			var noslash = Path.DirectorySeparatorChar != '/';

			foreach (var file in files)
			{
				cancel.ThrowIfCancellationRequested();

				var entry = file;
				if (entry.StartsWith(root)) entry = entry.Substring(root.Length);
				/*var path = Path.GetDirectoryName(entry);
				if (noslash) path = path.Replace(Path.DirectorySeparatorChar, '/');
				if (path != "" && !dirs.Contains(path))
				{
					dirs.Add(path);
					await writer.WriteDirectoryAsync(path);
				}*/
				if (noslash) entry = entry.Replace(Path.DirectorySeparatorChar, '/');
				writer.Write(entry, file);
			}
			await stream.FlushAsync();
		}
    }
}
