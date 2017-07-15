using System;
using System.IO;

namespace SshFileSync
{
    public class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length > SshDeltaCopy.Options.DESTINATION_DIRECTORY_INDEX)
                {
                    var options = SshDeltaCopy.Options.CreateFromArgs(args);
                    using (var sshDeltaCopy = new SshDeltaCopy(options))
                    {
                        //sshDeltaCopy.RunSSHCommand("ls -al");
                        sshDeltaCopy.DeployDirectory(options.SourceDirectory, options.DestinationDirectory);
                        //sshDeltaCopy.RunSSHCommand("ls -al");
                        //sshDeltaCopy.RunSSHCommand("df -h");
                    }
                }
                else if (args.Length > 0 && File.Exists(args[0]))
                {
                    SshDeltaCopy.RunBatchfile(args[0]);
                }
                else if (File.Exists(SshDeltaCopy.DefaultBatchFilename))
                {
                    SshDeltaCopy.RunBatchfile(SshDeltaCopy.DefaultBatchFilename);
                }                
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception: {ex.Message}\n{ex.StackTrace}");
                return -2;
            }

#if DEBUG
            System.Threading.Thread.Sleep(5000);
#endif

            return 0;
        }
    }
}
