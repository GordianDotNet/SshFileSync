SshFileSync
============

SshFileSync synchronizes all local files of a Windows directory to a remote Linux host via SSH and SFTP or SCP (if SFTP is not supported).

To upload only changed files, an additional cache file '.uploadCache.cache' is stored in the destination folder.
Don't delete this cache file!

Usage
---

### Single transfer mode via command line

> SshFileSync Host Port Username Password LocalDirectory RemoteDirectory

Example: To sync all files from "C:\Temp" to "/home/root/SyncedTemp" on a remote machine with ip 192.168.100.24:

> SshFileSync 192.168.100.24 22 root Password "C:\Temp" "/home/root/SyncedTemp"

### Batch transfer mode via command line

> SshFileSync BatchFilename.csv

Example: To sync all directories from a csv file "MySSHFileTransfers.csv"

> SshFileSync "MySSHFileTransfers.csv"

CSV-Fileformat:
> Host;Port;Username;Password;LocalDirectory;RemoteDirectory

Usage as .Net Library
---

You can use the C# class SshDeltaCopy in your own .Net program:

```
var options = new SshDeltaCopy.Options() {
	Host = "192.168.100.24",
	Port = 22,
	Username = "root",
	Password = "Password",
	RemoveOldFiles = true,
	PrintTimings = true,
	RemoveTempDeleteListFile = true,
};
using (var sshDeltaCopy = new SshDeltaCopy(options))
{
	sshDeltaCopy.DeployDirectory(@"C:\App\MyAppBin", @"/usr/bin/MyApp");
	sshDeltaCopy.DeployDirectory(@"C:\App\PublicContent", @"/home/shared/public");
	sshDeltaCopy.DeployDirectory(@"C:\App\PrivateContent", @"/home/root/private");
	sshDeltaCopy.RunSSHCommand("ls -al");
}
```

# Known Issue
- [ ] Does not delete or create empty directories
- [ ] Supports only Windows as local machine and Linux/Unix as remote machine

# Version History

## 1.3.0
**2017-10-21**
- [x] Bugfix: "set -e" added at the beginning of the upload script. (Exit immediately if a command exits with a non-zero status.) Errors, such as 'tar: write error: No space left on device', during the upload process were not discovered.
- [x] Bugfix: Upgraded to Renci.SshNet 2016.1.0.0 to avoid crashes with compressed content > 100 MB.
- [x] Bugfix: Batch transfer mode has deployed only to the first path. Added "change working directory" for every batch task.

## 1.2.0
**2017-07-16**
- [x] Feature: CreateSSHCommand method implemented

## 1.2.0
**2017-07-15**
- [x] Feature: RunSSHCommand method implemented

## 1.1.0
**2017-07-15**

- [x] Bug: Crash if the file '.uploadCache.cache' was missing while downloading via SFTP
- [x] Bug: Space in remote destination directory (linux) was not supported
- [x] Feature: Batch support via csv file added (multiple different upload directory with one single connection)

## 1.0.0
**2017-07-12**

- [x] Initial Release
