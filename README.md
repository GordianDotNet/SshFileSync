SshFileSync
============

SshFileSync synchronizes all local files of a directory to a remote Linux host via SSH and SFTP or SCP if SFTP is not supported.

To upload only changed files, an additional cache file '.uploadCache.cache' is stored in the destination folder.
Don't delete this file or all files are uploaded every time.

Usage
---

> SshFileSync Host Port Username Password LocalDirectory RemoteDirectory

Example: To sync all files from "C:\Temp" to "/home/root/SyncedTemp" on a remote machine with ip 192.168.100.24:

> SshFileSync 192.168.100.24 22 root Password "C:\Temp" "/home/root/SyncedTemp"

Usage as .Net Library
---

You can use the C# class SshDeltaCopy in your own .Net program:

```
using (var updater = new SshDeltaCopy("Host", 22, "root", "Password", removeOldFiles: true, printTimings: true, removeTempDeleteListFile: true))
{
    updater.UpdateDirectory(@"C:\Temp", @"/home/root/SyncedTemp");
}
```

# Known Issue
- [ ] Does not delete or create empty directories
- [ ] Supports only Windows as local machine and Linux/Unix as remote machine
- [ ] Crashs if compressed content > 100 MB (Bug in Renci.SshNet 2016.0.0.0 ?)

# Version History

## 1.0.0
**2017-07-12**

- [x] Initial Release
