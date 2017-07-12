using System;

namespace SshFileSync
{
    public class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length < 6)
                {
                    Console.WriteLine("Error: Missing parameters!");
                    Console.WriteLine("usage: SshFileSync Host#0 Port#1 SshUserName#2 SshPassword#3 LocalSourceDirectory#4 RemoteDestinationDirectory#4");
                    return -1;
                }
                
                if (int.TryParse(args[1], out int port))
                {
                    port = 22;
                }

                using (var updater = new SshDeltaCopy(args[0], port, args[2], args[3]))
                {
                    updater.UpdateDirectory(args[4], args[5]);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception: {ex.Message}\n{ex.StackTrace}");
                return -2;
            }

            return 0;
        }
    }
}
