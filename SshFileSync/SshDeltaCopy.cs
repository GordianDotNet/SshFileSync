using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Renci.SshNet;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace SshFileSync
{
    /// <summary>
    /// TODO remove/create empty folders
    /// </summary>
    public class SshDeltaCopy : IDisposable
    {
        private struct UpdateCacheEntry
        {
            public string FilePath;
            public long LastWriteTimeUtcTicks;
            public long FileSize;

            public static UpdateCacheEntry ReadFromStream(BinaryReader reader)
            {
                var fileEntry = new UpdateCacheEntry
                {
                    FilePath = reader.ReadString(),
                    LastWriteTimeUtcTicks = reader.ReadInt64(),
                    FileSize = reader.ReadInt64(),
                };

                return fileEntry;
            }

            public static void WriteToStream(BinaryWriter writer, string filePath, FileInfo fileInfo)
            {
                writer.Write(filePath);
                writer.Write(fileInfo.LastWriteTimeUtc.Ticks);
                writer.Write(fileInfo.Length);
            }
        }

        [Flags]
        private enum UpdateFlages : uint
        {
            NONE = 0,
            DELETE_FILES = 1,
            UPADTE_FILES = 2,
            DELETE_AND_UPDATE_FILES = DELETE_FILES | UPADTE_FILES,
        }

        private readonly Renci.SshNet.SftpClient _sftpClient;
        private readonly Renci.SshNet.ScpClient _scpClient;
        private readonly Renci.SshNet.SshClient _sshClient;
        private readonly ConnectionInfo _connectionInfo;

        private string _scpDestinationDirectory;
        private readonly string _deleteListFileName;
        private readonly string _uploadCacheFileName;
        private readonly string _uploadCacheTempFileName;
        private readonly string _compressedUploadDiffContentFilename;

        private readonly Stopwatch _stopWatch = new Stopwatch();
        private readonly bool _printTimings;
        private long _lastElapsedMilliseconds;

        private bool _removeTempDeleteListFile;
        private bool _removeOldFiles;

        public SshDeltaCopy(string host, int port, string username, string password, bool removeOldFiles = true, bool printTimings = true, bool removeTempDeleteListFile = true)
        {
            _stopWatch.Restart();
            _lastElapsedMilliseconds = 0;

            _printTimings = printTimings;
            _removeTempDeleteListFile = removeTempDeleteListFile;
            _removeOldFiles = removeOldFiles;

            PrintTime($"Connecting to {username}@{host}:{port} ...");

            _sshClient = new SshClient(host, port, username, password);
            _sshClient.Connect();

            try
            {
                var sftpClient = new SftpClient(host, port, username, password);
                sftpClient.Connect();
                _sftpClient = sftpClient;
            }
            catch (Exception ex)
            {
                PrintTime($"Error: {ex.Message} Is SFTP supported for {username}@{host}:{port}? We are using SCP instead!");
                _scpClient = new ScpClient(host, port, username, password);
                _scpClient.Connect();
            }

            _connectionInfo = _sshClient.ConnectionInfo;

            _uploadCacheFileName = $".uploadCache.cache";
            _uploadCacheTempFileName = $"{_uploadCacheFileName}.tmp";
            _deleteListFileName = $".deletedFilesList.cache";
            _compressedUploadDiffContentFilename = $"compressedUploadDiffContent.tar.gz";

            PrintTime($"Connected to {_connectionInfo.Username}@{_connectionInfo.Host}:{_connectionInfo.Port} via SSH and {(_sftpClient == null ? "SFTP" : "SCP")}");
        }

        public void Dispose()
        {
            _sshClient?.Dispose();
            _sftpClient?.Dispose();
            _scpClient?.Dispose();
        }

        public void UpdateDirectory(string sourceDirectory, string destinationDirectory)
        {
            _stopWatch.Restart();
            _lastElapsedMilliseconds = 0;

            PrintTime($"Copy{(_removeOldFiles ? " and remove" : string.Empty)} all changed files from '{sourceDirectory}' to '{destinationDirectory}'");

            var sourceDirInfo = new DirectoryInfo(sourceDirectory);
            if (!sourceDirInfo.Exists)
            {
                throw new DirectoryNotFoundException(sourceDirectory);
            }

            ChangeWorkingDirectory(destinationDirectory);

            var localFileCache = CreateLocalFileCache(sourceDirInfo);

            var fileListToDelete = new StringBuilder();
            var filesNoUpdateNeeded = new ConcurrentDictionary<string, bool>();

            DownloadAndCalculateChangedFiles(localFileCache, fileListToDelete, filesNoUpdateNeeded);

            var updateFlags = CreateAndUploadFileDiff(localFileCache, filesNoUpdateNeeded, fileListToDelete.ToString());

            if (updateFlags != UpdateFlages.NONE)
            {
                UnzipCompressedFileDiffAndRemoveOldFiles(destinationDirectory, updateFlags);
            }

            PrintTime($"Finished!");
        }

        private void ChangeWorkingDirectory(string destinationDirectory)
        {
            var cmd = _sshClient.RunCommand($"mkdir -p {destinationDirectory}");
            if (cmd.ExitStatus != 0)
            {
                throw new Exception(cmd.Error);
            }

            if (_sftpClient != null)
            {
                _sftpClient.ChangeDirectory(destinationDirectory);
                _scpDestinationDirectory = "";
            }
            else
            {
                _scpDestinationDirectory = destinationDirectory;
            }

            PrintTime($"Working directory changed to '{destinationDirectory}'");
        }

        private void DownloadFile(string path, Stream output)
        {
            if (_sftpClient != null)
            {
                _sftpClient.DownloadFile(path, output);
            }
            else
            {
                _scpClient.Download(_scpDestinationDirectory + "/" + path, output);
            }
        }

        private void UploadFile(Stream input, string path)
        {
            if (_sftpClient != null)
            {
                _sftpClient.UploadFile(input, path);
            }
            else
            {
                _scpClient.Upload(input, _scpDestinationDirectory + "/" + path);
            }
        }

        private ConcurrentDictionary<string, FileInfo> CreateLocalFileCache(DirectoryInfo sourceDirInfo)
        {
            var startIndex = sourceDirInfo.FullName.Length;
            var localFileCache = new ConcurrentDictionary<string, FileInfo>();
            Parallel.ForEach(GetFiles(sourceDirInfo.FullName), file =>
            {
                var cleanedRelativeFilePath = file.Substring(startIndex);
                cleanedRelativeFilePath = cleanedRelativeFilePath.Replace("\\", "/").TrimStart('/');
                localFileCache[cleanedRelativeFilePath] = new FileInfo(file);
            });

            PrintTime($"Local file cache created");
            return localFileCache;
        }

        private void DownloadAndCalculateChangedFiles(ConcurrentDictionary<string, FileInfo> localFileCache, StringBuilder fileListToDelete, ConcurrentDictionary<string, bool> filesNoUpdateNeeded)
        {
            try
            {
                using (MemoryStream currentRemoteCacheFile = new MemoryStream())
                {
                    DownloadFile(_uploadCacheFileName, currentRemoteCacheFile);

                    PrintTime($"Remote file cache '{_uploadCacheFileName}' downloaded");

                    currentRemoteCacheFile.Seek(0, SeekOrigin.Begin);
                    using (BinaryReader reader = new BinaryReader(currentRemoteCacheFile))
                    {
                        int entryCount = reader.ReadInt32();
                        int deleteFileCount = 0;
                        long deleteFileSize = 0;
                        int upToDateFileCount = 0;
                        long upToDateFileSize = 0;
                        long remoteFilesSize = 0;
                        for (int i = 0; i < entryCount; i++)
                        {
                            var remotefileEntry = UpdateCacheEntry.ReadFromStream(reader);
                            remoteFilesSize += remotefileEntry.FileSize;

                            if (!localFileCache.TryGetValue(remotefileEntry.FilePath, out FileInfo localFileInfo))
                            {
                                deleteFileCount++;
                                deleteFileSize += remotefileEntry.FileSize;
                                fileListToDelete.Append(remotefileEntry.FilePath).Append("\n");
                            }
                            else if (localFileInfo.LastWriteTimeUtc.Ticks == remotefileEntry.LastWriteTimeUtcTicks && localFileInfo.Length == remotefileEntry.FileSize)
                            {
                                upToDateFileCount++;
                                upToDateFileSize += remotefileEntry.FileSize;
                                filesNoUpdateNeeded[remotefileEntry.FilePath] = true;
                            }
                        }
                        PrintTime($"{deleteFileCount,7:n0} [{deleteFileSize,13:n0} bytes] of {entryCount,7:n0} [{remoteFilesSize,13:n0} bytes] files need to be deleted");
                        PrintTime($"{upToDateFileCount,7:n0} [{upToDateFileSize,13:n0} bytes] of {entryCount,7:n0} [{remoteFilesSize,13:n0} bytes] files don't need to be updated");
                    }
                }
            }
            catch (Renci.SshNet.Common.ScpException)
            {
                PrintTime($"Remote file cache '{_uploadCacheFileName}' not found! We are uploading all files!");
            }

            PrintTime($"Diff between local and remote file cache calculated");
        }

        private UpdateFlages CreateAndUploadFileDiff(ConcurrentDictionary<string, FileInfo> localFileCache, ConcurrentDictionary<string, bool> filesNoUpdateNeeded, string fileListToDelete)
        {
            var updateFlags = UpdateFlages.NONE;

            using (Stream tarGzStream = new MemoryStream())
            {
                using (var tarGzWriter = WriterFactory.Open(tarGzStream, ArchiveType.Tar, CompressionType.GZip))
                {
                    using (MemoryStream newCacheFileStream = new MemoryStream())
                    {
                        using (BinaryWriter newCacheFileWriter = new BinaryWriter(newCacheFileStream))
                        {
                            newCacheFileWriter.Write(localFileCache.Count);

                            var updateNeeded = false;
                            var updateFileCount = 0;
                            long updateFileSize = 0;
                            var allFileCount = 0;
                            long allFileSize = 0;

                            foreach (var file in localFileCache)
                            {
                                allFileCount++;
                                allFileSize += file.Value.Length;

                                // add new cache file entry
                                UpdateCacheEntry.WriteToStream(newCacheFileWriter, file.Key, file.Value);

                                // add new file entry
                                if (filesNoUpdateNeeded.ContainsKey(file.Key))
                                {
                                    continue;
                                }

                                updateNeeded = true;
                                updateFileCount++;
                                updateFileSize += file.Value.Length;

                                try
                                {
                                    tarGzWriter.Write(file.Key, file.Value);
                                }
                                catch (Exception ex)
                                {
                                    PrintError(ex);
                                }
                            }

                            PrintTime($"{updateFileCount,7:n0} [{updateFileSize,13:n0} bytes] of {allFileCount,7:n0} [{allFileSize,13:n0} bytes] files need to be updated");

                            if (!string.IsNullOrEmpty(fileListToDelete))
                            {
                                updateFlags |= UpdateFlages.DELETE_FILES;

                                using (var deleteListStream = new MemoryStream(Encoding.UTF8.GetBytes(fileListToDelete.ToString())))
                                {
                                    UploadFile(deleteListStream, $"{_deleteListFileName}");
                                }

                                PrintTime($"Deleted file list '{_deleteListFileName}' uploaded");

                                if (!updateNeeded)
                                {
                                    PrintTime($"Only delete old files");
                                    return updateFlags;
                                }
                            }
                            else if (!updateNeeded)
                            {
                                PrintTime($"No update needed");
                                return updateFlags;
                            }

                            updateFlags |= UpdateFlages.UPADTE_FILES;

                            newCacheFileStream.Seek(0, SeekOrigin.Begin);
                            UploadFile(newCacheFileStream, $"{_uploadCacheTempFileName}");

                            PrintTime($"New remote file cache '{_uploadCacheTempFileName}' uploaded");
                        }
                    }
                }

                var tarGzStreamSize = tarGzStream.Length;
                tarGzStream.Seek(0, SeekOrigin.Begin);
                UploadFile(tarGzStream, _compressedUploadDiffContentFilename);

                PrintTime($"Compressed file diff '{_compressedUploadDiffContentFilename}' [{tarGzStreamSize,13:n0} bytes] uploaded");
            }

            return updateFlags;
        }

        private SshCommand UnzipCompressedFileDiffAndRemoveOldFiles(string destinationDirectory, UpdateFlages updateFlags)
        {
            var commandText = $"cd {destinationDirectory}";
            if (updateFlags.HasFlag(UpdateFlages.UPADTE_FILES))
            {
                commandText += $";tar -zxf {_compressedUploadDiffContentFilename}";
                if (_removeTempDeleteListFile)
                {
                    commandText += $";rm {_compressedUploadDiffContentFilename}";
                }
                commandText += $";mv {_uploadCacheTempFileName} {_uploadCacheFileName}";
            }
            if (updateFlags.HasFlag(UpdateFlages.DELETE_FILES))
            {
                if (_removeOldFiles)
                {
                    commandText += $";while read file ; do rm \"$file\" ; done < {_deleteListFileName}";
                }

                if (_removeTempDeleteListFile)
                {
                    commandText += $";rm {_deleteListFileName}";
                }
            }

            SshCommand cmd = _sshClient.RunCommand(commandText);
            if (cmd.ExitStatus != 0)
            {
                throw new Exception(cmd.Error);
            }

            PrintTime($"Compressed file diff '{_compressedUploadDiffContentFilename}' unzipped {(_removeTempDeleteListFile ? "and temp files cleaned up" : "and temp files not cleaned up")}");

            return cmd;
        }

        private void PrintTime(string text)
        {
            if (_printTimings)
            {
                var currentElapsedMilliseconds = _stopWatch.ElapsedMilliseconds;
                Console.WriteLine($"[{(currentElapsedMilliseconds - _lastElapsedMilliseconds),7} ms][{currentElapsedMilliseconds,7} ms] {text}");
                _lastElapsedMilliseconds = currentElapsedMilliseconds;
            }
        }

        private void PrintError(Exception ex)
        {
            Console.WriteLine($"Exception: {ex.Message}\n{ex.StackTrace}");
        }

        public static IEnumerable<string> GetFiles(string path)
        {
            Queue<string> queue = new Queue<string>();
            queue.Enqueue(path);
            while (queue.Count > 0)
            {
                path = queue.Dequeue();
                try
                {
                    foreach (string subDir in Directory.GetDirectories(path))
                    {
                        queue.Enqueue(subDir);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                }
                string[] files = null;
                try
                {
                    files = Directory.GetFiles(path);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                }
                if (files != null)
                {
                    for (int i = 0; i < files.Length; i++)
                    {
                        yield return files[i];
                    }
                }
            }
        }
    }
}
